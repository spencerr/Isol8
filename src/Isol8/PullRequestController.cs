using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

namespace Isol8;

public class PullRequestController(IKubernetes client, ILogger<PullRequestController> logger, IOptions<EnvoyOptions> envoyOptions, EnvoyYaml envoyYaml, ServiceCache serviceCache) : BackgroundService
{
    public readonly Channel<ServiceEntry> ProcessorChannel = Channel.CreateUnbounded<ServiceEntry>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly EnvoyOptions _envoyOptions = envoyOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var cancellationToken = cts.Token;

        var healthCheckTask = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Touch("/tmp/healthcheck");

                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }, cancellationToken);

        logger.LogInformation("[Startup] Populating Service Cache");
        var serviceEntries = await RefreshCache(cancellationToken);

        logger.LogInformation("[Startup] Pushing {Count} Services for Reconciling during initialization", serviceEntries.Count);
        foreach (var entry in serviceEntries)
        {
            ProcessorChannel.Writer.TryWrite(entry);
        }

        var consumerTask = StartConsumer(cancellationToken);
        while (!cancellationToken.IsCancellationRequested && !consumerTask.IsCompleted)
        {
            using var watcher = client.CoreV1.ListServiceForAllNamespacesWithHttpMessagesAsync(labelSelector: Constants.LabelSelector, watch: true, cancellationToken: cancellationToken)
                .Watch<V1Service, V1ServiceList>(HandleEvent,
                    async e =>
                    {
                        logger.LogError(e, "An unexpected exception occured during service watching.");
                        await cts.CancelAsync();
                    },
                    () => logger.LogWarning("Service watcher closed...")
                );

            var watcherTask = Task.Run(async () =>
            {
                var start = DateTime.UtcNow;
                while (!watcher.Watching && DateTime.UtcNow.Subtract(start) < TimeSpan.FromSeconds(30))
                {
                    logger.LogInformation("Waiting for watcher to start...");
                    await Task.Delay(1000, cancellationToken);
                }

                while (!cancellationToken.IsCancellationRequested && watcher.Watching)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }, cancellationToken);

            await Task.WhenAny(healthCheckTask, watcherTask, consumerTask);
        }
    }

    private void Touch(string path)
    {
#if !DEBUG
		using var fileStream = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
#endif
    }

    /// <summary>
    /// Initializes the service cache by retrieving all services with the <see cref="Constants.WatchLabel"/>.
    /// Finds all Services,ConfigMaps, and Deployments with the <see cref="Constants.WatchLabel"/> and pushes them to the processor channel.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<HashSet<ServiceEntry>> RefreshCache(CancellationToken cancellationToken)
    {
        serviceCache.Clear();

        var serviceEntries = new HashSet<ServiceEntry>();

        var services = await client.CoreV1.ListServiceForAllNamespacesAsync(labelSelector: Constants.LabelSelector, cancellationToken: cancellationToken);
        logger.LogInformation("[Refresh Cache] Caching {Count} Services", services.Items.Count);
        foreach (var service in services)
        {
            serviceEntries.Add(GetReconcileEntry(service));
            serviceCache[GetServiceKey(service)] = service;
            logger.LogInformation("Adding Service {ServiceName} in Namespace {Namespace}", service.Name(), service.Namespace());
        }

        var configMaps = await client.CoreV1.ListConfigMapForAllNamespacesAsync(labelSelector: Constants.LabelSelector, cancellationToken: cancellationToken);
        logger.LogInformation("[Refresh Cache] Found {Count} ConfigMaps", configMaps.Items.Count);
        foreach (var configMap in configMaps)
        {
            var entry = new ServiceEntry(configMap.Namespace(), configMap.Annotations()[Constants.WatchNameAnnotation]);
            serviceEntries.Add(entry);
        }

        var deployments = await client.AppsV1.ListDeploymentForAllNamespacesAsync(labelSelector: Constants.LabelSelector, cancellationToken: cancellationToken);
        logger.LogInformation("[Refresh Cache] Found {Count} Deployments", deployments.Items.Count);
        foreach (var deployment in deployments)
        {
            var entry = new ServiceEntry(deployment.Namespace(), deployment.Annotations()[Constants.WatchNameAnnotation]);
            serviceEntries.Add(entry);
        }

        return serviceEntries;
    }

    public void HandleEvent(WatchEventType type, V1Service service)
    {
        switch (type)
        {
            case WatchEventType.Added:
            case WatchEventType.Modified:
                serviceCache[GetServiceKey(service)] = service;
                break;
            case WatchEventType.Deleted:
                serviceCache.TryRemove(GetServiceKey(service), out var _);
                break;
        }

        ProcessorChannel.Writer.TryWrite(GetReconcileEntry(service));
    }

    public ServiceEntry GetServiceKey(V1Service service)
    {
        return new ServiceEntry(service.Namespace(), service.Name());
    }

    public ServiceEntry GetReconcileEntry(V1Service service)
    {
        if (service.Annotations()?.ContainsKey(Constants.OriginalNameAnnotation) == true)
        {
            return new ServiceEntry(service.Namespace(), service.Annotations()[Constants.OriginalNameAnnotation]);
        }
        if (service.Annotations()?.ContainsKey(Constants.RouteFromAnnotation) == true)
        {
            return new ServiceEntry(service.Namespace(), service.Annotations()[Constants.RouteFromAnnotation]);
        }
        return new ServiceEntry(service.Namespace(), service.Name());
    }

    public async Task Reconcile(ServiceEntry entry)
    {
        var requestId = Guid.NewGuid();
        logger.LogInformation("[Reconcile {Id}] Attempting to reconcile Service {ServiceName} in Namespace {Namespace}", requestId, entry.ServiceName, entry.Namespace);

        var service = await TryGetService(entry);
        var envoyService = service != null && service.Annotations()?.ContainsKey(Constants.EnvoyAnnotation) == true ? service : null;
        logger.LogInformation("[Reconcile {Id}] Envoy Service {ServiceName} in namespace {Namespace} - {Status}", requestId, entry.ServiceName, entry.Namespace, envoyService == null ? HttpStatusCode.NotFound : HttpStatusCode.OK);

        var originalService = await TryGetService(new ServiceEntry(entry.Namespace, entry.GetOriginalName()));
        logger.LogInformation("[Reconcile {Id}] Original Service {ServiceName} in namespace {Namespace} - {Status}", requestId, entry.ServiceName, entry.Namespace, originalService == null ? HttpStatusCode.NotFound : HttpStatusCode.OK);

        var pullRequestServices = serviceCache.Values
            .Where(s => s.Annotations()?.TryGetValue(Constants.RouteFromAnnotation, out var value) == true && value == entry.ServiceName)
            .ToList();

        foreach (var pullRequest in pullRequestServices)
        {
            logger.LogInformation("[Reconcile {Id}] Found Pull Request Service {ServiceName} in namespace {Namespace}", requestId, pullRequest.Name(), pullRequest.Namespace());
        }

        logger.LogInformation("[Reconcile {Id}] Found {Count} pull requests for {ServiceName} in namespace {Namespace}", requestId, pullRequestServices.Count, entry.ServiceName, entry.Namespace);
        if (pullRequestServices.Count == 0)
        {
            if (envoyService != null)
            {
                await RemoveEnvoy(requestId, entry);
            }

            if (originalService != null)
            {
                await RevertOriginalService(requestId, entry);
            }
        }
        else
        {
            await CreateOriginalService(requestId, entry);
            await RegisterPullRequests(requestId, entry, pullRequestServices);
            await CreateEnvoyDeployment(requestId, entry);
        }
    }

    public async Task<V1Service?> TryGetService(ServiceEntry entry)
    {
        if (serviceCache.TryGetValue(entry, out var service))
        {
            return service;
        }

        logger.LogWarning("Service {ServiceName} in namespace {Namespace} not found in cache, attempting to retrieve", entry.ServiceName, entry.Namespace);

        service = await client.CoreV1.ReadNamespacedServiceAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
        if (service != null)
        {
            serviceCache[GetServiceKey(service)] = service;
        }

        return service;
    }

    public async Task RegisterPullRequests(Guid requestId, ServiceEntry entry, IList<V1Service> pullRequests)
    {
        var originalService = await TryGetService(new ServiceEntry(entry.Namespace, entry.GetOriginalName()));
        if (originalService == null)
        {
            logger.LogWarning("[Register {Id}] Original Service {ServiceName} in namespace {Namespace} not found", requestId, entry.GetOriginalName(), entry.Namespace);
            return;
        }
        logger.LogInformation("[Register {Id}] Original Service {ServiceName} in namespace {Namespace} found", requestId, entry.ServiceName, entry.Namespace);

        var originalDeployment = await client.AppsV1.ReadNamespacedDeploymentAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
        if (originalDeployment == null)
        {
            logger.LogWarning("[Register {Id}] Original Deployment {DeploymentName} in namespace {Namespace} not found", requestId, entry.ServiceName, entry.Namespace);
            return;
        }
        logger.LogInformation("[Register {Id}] Original Deployment {DeploymentName} in namespace {Namespace} found", requestId, entry.ServiceName, entry.Namespace);

        var envoyConfigMap = await client.CoreV1.ReadNamespacedConfigMapAsync(entry.GetEnvoyName(), entry.Namespace).Handle404AsNull();
        if (envoyConfigMap == null)
        {
            envoyConfigMap = new V1ConfigMap
            {
                Metadata = new V1ObjectMeta
                {
                    Name = entry.GetEnvoyName(),
                    NamespaceProperty = entry.Namespace,
                    Labels = originalDeployment.Labels() ?? new Dictionary<string, string>(),
                    Annotations = originalDeployment.Annotations() ?? new Dictionary<string, string>()
                },
                Data = new Dictionary<string, string>
                {
                    ["envoy.yaml"] = envoyYaml.GetYaml(originalService, pullRequests)
                }
            };

            envoyConfigMap.Metadata.Annotations[Constants.WatchNameAnnotation] = entry.ServiceName;
            envoyConfigMap.Metadata.Annotations[Constants.GeneratedAnnotation] = "true";
            envoyConfigMap.Metadata.Labels["app.kubernetes.io/name"] = entry.GetEnvoyName();
            envoyConfigMap.Metadata.Labels[Constants.WatchLabel] = "true";

            logger.LogInformation("[Register {Id}] Creating Envoy ConfigMap {ConfigMapName} in namespace {Namespace}", requestId, envoyConfigMap.Name(), envoyConfigMap.Namespace());
            await client.CoreV1.CreateNamespacedConfigMapAsync(envoyConfigMap, entry.Namespace);
        }
        else
        {
            var patch = new V1Patch(new
            {
                Data = new Dictionary<string, string>
                {
                    ["envoy.yaml"] = envoyYaml.GetYaml(originalService, pullRequests)
                }
            }, V1Patch.PatchType.MergePatch);

            logger.LogInformation("[Register {Id}] Patching Envoy ConfigMap {ConfigMapName} in namespace {Namespace}", requestId, envoyConfigMap.Name(), envoyConfigMap.Namespace());
            await client.CoreV1.PatchNamespacedConfigMapAsync(patch, envoyConfigMap.Name(), envoyConfigMap.Namespace());
        }
    }

    public async Task CreateEnvoyDeployment(Guid requestId, ServiceEntry entry)
    {
        var originalService = await TryGetService(new ServiceEntry(entry.Namespace, entry.GetOriginalName()));
        if (originalService == null)
        {
            logger.LogWarning("[Create Envoy {Id}] Original Service {ServiceName} in namespace {Namespace} not found", requestId, entry.GetOriginalName(), entry.Namespace);
            return;
        }

        logger.LogInformation("[Create Envoy {Id}] Original Service {ServiceName} in namespace {Namespace} found", requestId, entry.GetOriginalName(), entry.Namespace);

        var envoyService = await TryGetService(entry);
        if (envoyService != null && envoyService.Annotations()?.ContainsKey(Constants.EnvoyAnnotation) != true)
        {
            serviceCache.TryRemove(entry, out var _);

            logger.LogInformation("[Create Envoy {Id}] Deleting Service {ServiceName} in namespace {Namespace} before creating envoy", requestId, envoyService.Name(), envoyService.Namespace());
            await client.CoreV1.DeleteNamespacedServiceAsync(envoyService.Name(), envoyService.Namespace());
        }

        envoyService = await TryGetService(entry);
        if (envoyService == null)
        {
            envoyService = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = entry.ServiceName,
                    NamespaceProperty = entry.Namespace,
                    Labels = originalService.Labels() ?? new Dictionary<string, string>(),
                    Annotations = originalService.Annotations() ?? new Dictionary<string, string>()
                },
                Spec = new V1ServiceSpec
                {
                    Selector = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = entry.GetEnvoyName()
                    },
                    Ports = new List<V1ServicePort>
                    {
                        new V1ServicePort
                        {
                            Name = "http",
                            Port = _envoyOptions.ServicePort,
                            TargetPort = _envoyOptions.ContainerPort
                        },
                        new V1ServicePort
                        {
                            Name = "admin",
                            Port = _envoyOptions.AdminContainerPort,
                            TargetPort = _envoyOptions.AdminContainerPort
                        }
                    }
                }
            };

            envoyService.Metadata.Annotations[Constants.GeneratedAnnotation] = "true";
            envoyService.Metadata.Annotations[Constants.EnvoyAnnotation] = "true";
            envoyService.Metadata.Annotations.Remove(Constants.OriginalNameAnnotation);
            envoyService.Metadata.Labels[Constants.WatchLabel] = "true";

            logger.LogInformation("[Create Envoy {Id}] Creating Envoy Service {ServiceName} in namespace {Namespace} before creating envoy", requestId, envoyService.Name(), envoyService.Namespace());
            envoyService = await client.CoreV1.CreateNamespacedServiceAsync(envoyService, envoyService.Namespace());
            serviceCache[GetServiceKey(envoyService)] = envoyService;
        }
        else
        {
            logger.LogInformation("[Create Envoy {Id}] Envoy Service {ServiceName} in namespace {Namespace} already exists", requestId, envoyService.Name(), envoyService.Namespace());
        }

        var originalDeployment = await client.AppsV1.ReadNamespacedDeploymentAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
        if (originalDeployment == null)
        {
            logger.LogWarning("[Create Envoy {Id}] Original Deployment {DeploymentName} in namespace {Namespace} not found", requestId, entry.ServiceName, entry.Namespace);
            return;
        }

        logger.LogInformation("[Create Envoy {Id}] Original Deployment {DeploymentName} in namespace {Namespace} found", requestId, entry.ServiceName, entry.Namespace);
        var labels = originalDeployment.Labels() ?? new Dictionary<string, string>();
        labels["app.kubernetes.io/name"] = entry.GetEnvoyName();

        var envoyDeployment = await client.AppsV1.ReadNamespacedDeploymentAsync(entry.GetEnvoyName(), entry.Namespace).Handle404AsNull();
        if (envoyDeployment == null)
        {
            envoyDeployment = new V1Deployment
            {
                Metadata = new V1ObjectMeta
                {
                    Name = entry.GetEnvoyName(),
                    NamespaceProperty = entry.Namespace,
                    Labels = labels,
                    Annotations = originalDeployment.Annotations() ?? new Dictionary<string, string>()
                },
                Spec = new V1DeploymentSpec
                {
                    Selector = new V1LabelSelector
                    {
                        MatchLabels = new Dictionary<string, string>
                        {
                            ["app.kubernetes.io/name"] = entry.GetEnvoyName()
                        }
                    },
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Labels = labels
                        },
                        Spec = new V1PodSpec
                        {
                            Containers = new List<V1Container>
                            {
                                new V1Container
                                {
                                    Name = "envoy",
                                    Image = _envoyOptions.Image,
                                    Ports = new List<V1ContainerPort>
                                    {
                                        new V1ContainerPort
                                        {
                                            ContainerPort = 8080
                                        },
                                        new V1ContainerPort
                                        {
                                            ContainerPort = 8081
                                        }
                                    },
                                    VolumeMounts = new List<V1VolumeMount>
                                    {
                                        new V1VolumeMount
                                        {
                                            Name = "envoy-config",
                                            MountPath = "/etc/envoy",
                                            ReadOnlyProperty = true
                                        }
                                    },
                                    Args = new List<string>
                                    {
                                        "--config-path",
                                        "/etc/envoy/envoy.yaml"
                                    },
                                    Resources = new V1ResourceRequirements
                                    {
                                        Limits = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["memory"] = new ResourceQuantity("256Mi"),
                                            ["cpu"] = new ResourceQuantity("500m")
                                        },
                                        Requests = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["memory"] = new ResourceQuantity("128Mi"),
                                            ["cpu"] = new ResourceQuantity("250m")
                                        }
                                    }
                                }
                            },
                            Tolerations = originalDeployment.Spec.Template.Spec.Tolerations,
                            NodeSelector = originalDeployment.Spec.Template.Spec.NodeSelector,
                            Volumes = new List<V1Volume>
                            {
                                new V1Volume
                                {
                                    Name = "envoy-config",
                                    ConfigMap = new V1ConfigMapVolumeSource
                                    {
                                        Name = entry.GetEnvoyName()
                                    }
                                }
                            }
                        }
                    }
                }
            };

            envoyDeployment.Metadata.Labels[Constants.WatchLabel] = "true";
            envoyDeployment.Metadata.Annotations[Constants.GeneratedAnnotation] = "true";
            envoyDeployment.Metadata.Annotations[Constants.WatchNameAnnotation] = entry.ServiceName;
            envoyDeployment.Metadata.Labels = envoyDeployment.Metadata.Labels.Where(l => !l.Key.StartsWith("argocd")).ToDictionary();

            logger.LogInformation("[Create Envoy {Id}] Creating Envoy Deployment {DeploymentName} in namespace {Namespace}", requestId, envoyDeployment.Name(), envoyDeployment.Namespace());
            await client.AppsV1.CreateNamespacedDeploymentAsync(envoyDeployment, entry.Namespace);
        }
        else
        {
            logger.LogInformation("[Create Envoy {Id}] Envoy Deployment {DeploymentName} in namespace {Namespace} already exists", requestId, envoyDeployment.Name(), envoyDeployment.Namespace());
        }

    }

    public async Task RemoveEnvoy(Guid requestId, ServiceEntry entry)
    {
        var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(entry.GetEnvoyName(), entry.Namespace).Handle404AsNull();
        if (deployment != null)
        {
            logger.LogInformation("[Remove Envoy {Id}] Deleting Envoy Deployment {DeploymentName} in namespace {Namespace}", requestId, deployment.Name(), deployment.Namespace());
            await client.AppsV1.DeleteNamespacedDeploymentAsync(entry.GetEnvoyName(), entry.Namespace, new V1DeleteOptions
            {
                Preconditions = new V1Preconditions
                {
                    ResourceVersion = deployment.ResourceVersion(),
                    Uid = deployment.Uid()
                }
            });
        }

        var configMap = await client.CoreV1.ReadNamespacedConfigMapAsync(entry.GetEnvoyName(), entry.Namespace).Handle404AsNull();
        if (deployment != null)
        {
            logger.LogInformation("[Remove Envoy {Id}] Deleting Envoy ConfigMap {ConfigMapName} in namespace {Namespace}", requestId, configMap.Name(), configMap.Namespace());
            await client.CoreV1.DeleteNamespacedConfigMapAsync(configMap.Name(), configMap.Namespace(), new V1DeleteOptions
            {
                Preconditions = new V1Preconditions
                {
                    ResourceVersion = configMap.ResourceVersion(),
                    Uid = configMap.Uid()
                }
            });
        }

        var service = await client.CoreV1.ReadNamespacedServiceAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
        if (service != null && service.Annotations()?.ContainsKey(Constants.EnvoyAnnotation) == true)
        {
            serviceCache.TryRemove(GetServiceKey(service), out var _);
            logger.LogInformation("[Remove Envoy {Id}] Deleting Envoy Service {ServiceName} in namespace {Namespace}", requestId, service.Name(), service.Namespace());
            await client.CoreV1.DeleteNamespacedServiceAsync(service.Name(), service.Namespace(), new V1DeleteOptions
            {
                Preconditions = new V1Preconditions
                {
                    ResourceVersion = service.ResourceVersion(),
                    Uid = service.Uid()
                }
            });
        }
    }

    public async Task CreateOriginalService(Guid requestId, ServiceEntry entry)
    {
        var originalService = await TryGetService(new ServiceEntry(entry.Namespace, entry.GetOriginalName()));
        if (originalService is not null)
        {
            logger.LogInformation("[Create Service {Id}] Original Service {ServiceName} in namespace {Namespace} already exists", requestId, originalService.Name(), originalService.Namespace());
            return;
        }

        var service = await TryGetService(entry);
        if (service is null)
        {
            logger.LogWarning("[Create Service {Id}] Service {ServiceName} in namespace {Namespace} not found", requestId, entry.ServiceName, entry.Namespace);
            return;
        }

        if (service.Annotations()?.ContainsKey(Constants.EnvoyAnnotation) != true)
        {
            serviceCache.TryRemove(GetServiceKey(service), out var _);

            originalService = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = entry.GetOriginalName(),
                    NamespaceProperty = service.Namespace(),
                    Labels = service.Metadata.Labels ?? new Dictionary<string, string>(),
                    Annotations = service.Metadata.Annotations ?? new Dictionary<string, string>()
                },
                Spec = service.Spec
            };

            originalService.Metadata.Annotations[Constants.GeneratedAnnotation] = "true";
            originalService.Metadata.Annotations[Constants.OriginalNameAnnotation] = service.Name();
            originalService.Metadata.Labels[Constants.WatchLabel] = "true";

            originalService.Spec.ClusterIP = null;
            originalService.Spec.ClusterIPs = null;

            logger.LogInformation("[Create Service {Id}] Creating Original Service {ServiceName} in namespace {Namespace}", requestId, originalService.Name(), originalService.Namespace());
            await client.CoreV1.CreateNamespacedServiceAsync(originalService, originalService.Namespace());
        }
    }

    public async Task RevertOriginalService(Guid requestId, ServiceEntry entry)
    {
        var originalService = await client.CoreV1.ReadNamespacedServiceAsync(entry.GetOriginalName(), entry.Namespace).Handle404AsNull();
        if (originalService == null)
        {
            logger.LogWarning("[Revert Service {Id}] Service {ServiceName} in namespace {Namespace} not found", requestId, entry.ServiceName, entry.Namespace);
            return;
        }

        if (originalService.Annotations()?.TryGetValue(Constants.OriginalNameAnnotation, out var originalName) != true)
        {
            logger.LogWarning("[Revert Service {Id}] Service {ServiceName} in namespace {Namespace} is not marked with the original labels", requestId, originalService.Name(), originalService.Namespace());
            return;
        }

        var service = await client.CoreV1.ReadNamespacedServiceAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
        if (service == null)
        {
            var revertedService = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = originalName,
                    NamespaceProperty = originalService.Namespace(),
                    Labels = originalService.Metadata.Labels ?? new Dictionary<string, string>(),
                    Annotations = originalService.Metadata.Annotations ?? new Dictionary<string, string>()
                },
                Spec = originalService.Spec
            };

            revertedService.Spec.ClusterIP = null;
            revertedService.Spec.ClusterIPs = null;

            revertedService.Metadata.Annotations.Remove(Constants.GeneratedAnnotation);
            revertedService.Metadata.Annotations.Remove(Constants.OriginalNameAnnotation);
            revertedService.Metadata.Labels.Remove(Constants.WatchLabel);

            logger.LogInformation("[Revert Service {Id}] Reverting Service {ServiceName} in namespace {Namespace}", requestId, revertedService.Name(), revertedService.Namespace());
            await client.CoreV1.CreateNamespacedServiceAsync(revertedService, revertedService.Namespace());
        }
        
        logger.LogInformation("[Revert Service {Id}] Deleting Original Service {ServiceName} in namespace {Namespace}", requestId, originalService.Name(), originalService.Namespace());
        await client.CoreV1.DeleteNamespacedServiceAsync(originalService.Name(), originalService.Namespace(), new V1DeleteOptions
        {
            Preconditions = new V1Preconditions
            {
                ResourceVersion = originalService.ResourceVersion(),
                Uid = originalService.Uid()
            }
        });
    }

    private async Task StartConsumer(CancellationToken cancellationToken)
    {
        logger.LogInformation("Consumer started");
        try
        {
            await foreach (ServiceEntry entry in ProcessorChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await Reconcile(entry);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Cancellation Requested");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An unexpected exception occured during cluster reconciliation");
        }
        finally
        {
            logger.LogInformation("Consumer complete");
        }
    }
}