// Nbs.PullRequest.Operator, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// Nbs.PullRequest.Operator.PullRequestOperator
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nbs.PullRequest.Operator;

public class PullRequestOperator : BackgroundService
{
	[CompilerGenerated]
	[DebuggerBrowsable(DebuggerBrowsableState.Never)]
	private IKubernetes _003Cclient_003EP;

	[CompilerGenerated]
	[DebuggerBrowsable(DebuggerBrowsableState.Never)]
	private ILogger<PullRequestOperator> _003Clogger_003EP;

	private static readonly Channel<ServiceEntry> ProcessorChannel = Channel.CreateUnbounded<ServiceEntry>(new UnboundedChannelOptions
	{
		SingleReader = true
	});

	private static readonly ConcurrentDictionary<ServiceEntry, V1Service> ServiceCache = new ConcurrentDictionary<ServiceEntry, V1Service>();

	private readonly EnvoyOptions _envoyOptions;

	public PullRequestOperator(IKubernetes client, ILogger<PullRequestOperator> logger, IOptions<EnvoyOptions> envoyOptions)
	{
		_003Cclient_003EP = client;
		_003Clogger_003EP = logger;
		_envoyOptions = envoyOptions.Value;
		base._002Ector();
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await RefreshServiceCache(stoppingToken);
		ICoreV1Operations coreV = _003Cclient_003EP.CoreV1;
		bool? watch = true;
		using ((await coreV.ListServiceForAllNamespacesWithHttpMessagesAsync(null, null, null, "nbs.pullrequest=true", null, null, null, null, null, null, watch, null, stoppingToken)).Watch<V1Service, V1ServiceList>(HandleEvent, delegate(Exception e)
		{
			_003Clogger_003EP.LogError(e, "An unexpected exception occured during service watching.");
		}, delegate
		{
			_003Clogger_003EP.LogWarning("Service watcher closed");
		}))
		{
			Console.WriteLine("Operator Started");
			await StartConsumer(stoppingToken);
		}
	}

	private async Task RefreshServiceCache(CancellationToken cancellationToken)
	{
		_003Clogger_003EP.LogInformation("Initializing service cache");
		foreach (V1Service service in await _003Cclient_003EP.CoreV1.ListServiceForAllNamespacesAsync(null, null, null, "nbs.pullrequest=true", null, null, null, null, null, null, null, cancellationToken))
		{
			ServiceCache[GetServiceKey(service)] = service;
			_003Clogger_003EP.LogInformation("Service {ServiceName} in namespace {Namespace} added to cache", service.Name(), service.Namespace());
		}
		_003Clogger_003EP.LogInformation("Loaded {Count} items into the service cache", ServiceCache.Count);
	}

	private ServiceEntry GetServiceKey(V1Service service)
	{
		return new ServiceEntry(service.Namespace(), service.Name());
	}

	private ServiceEntry GetReconcileEntry(V1Service service)
	{
		if (service.Labels().ContainsKey("nbs.pullrequest/original"))
		{
			return new ServiceEntry(service.Namespace(), service.Labels()["nbs.pullrequest/original-name"]);
		}
		if (service.Labels().ContainsKey("nbs.pullrequest/enabled"))
		{
			return new ServiceEntry(service.Namespace(), service.Labels()["nbs.pullrequest/route-from"]);
		}
		return new ServiceEntry(service.Namespace(), service.Name());
	}

	private string GetOriginalName(string serviceName)
	{
		return ("original-" + serviceName).Truncate(63);
	}

	private string GetEnvoyName(string serviceName)
	{
		return ("envoy-" + serviceName).Truncate(63);
	}

	public void HandleEvent(WatchEventType type, V1Service service)
	{
		switch (type)
		{
		case WatchEventType.Added:
		case WatchEventType.Modified:
			ServiceCache[GetServiceKey(service)] = service;
			break;
		case WatchEventType.Deleted:
		{
			ServiceCache.TryRemove(GetServiceKey(service), out V1Service _);
			break;
		}
		}
		ProcessorChannel.Writer.TryWrite(GetReconcileEntry(service));
	}

	public async Task Reconcile(ServiceEntry entry)
	{
		ServiceEntry entry2 = entry;
		using (LoggerExtensions.BeginScope(_003Clogger_003EP, "Reconcile - "))
		{
			V1Service service = await TryGetService(entry2);
			V1Service envoyService = ((service != null && service.Labels().ContainsKey("nbs.pullrequest/envoy")) ? service : null);
			_003Clogger_003EP.LogInformation("{Status} - Envoy Service {ServiceName} in namespace {Namespace}", (envoyService == null) ? HttpStatusCode.NotFound : HttpStatusCode.OK, entry2.ServiceName, entry2.Namespace);
			V1Service originalService = await TryGetService(new ServiceEntry(entry2.Namespace, GetOriginalName(entry2.ServiceName)));
			_003Clogger_003EP.LogInformation("{Status} - Original Service {ServiceName} in namespace {Namespace}", (originalService == null) ? HttpStatusCode.NotFound : HttpStatusCode.OK, entry2.ServiceName, entry2.Namespace);
			string value;
			List<V1Service> pullRequestServices = (from s in ServiceCache.Values
				where s.Labels().ContainsKey("nbs.pullrequest/enabled")
				where s.Labels().TryGetValue("nbs.pullrequest/route-from", out value) && value == entry2.ServiceName
				select s).ToList();
			_003Clogger_003EP.LogInformation("Found {PullRequestCount} pull requests for {ServiceName} in namespace {Namespace}", pullRequestServices.Count, entry2.ServiceName, entry2.Namespace);
			if (pullRequestServices.Count == 0)
			{
				if (envoyService != null)
				{
					await RemoveEnvoy(entry2);
				}
				if (originalService != null)
				{
					await RevertOriginalService(entry2);
				}
			}
			else
			{
				await CreateOriginalService(entry2);
				await RegisterPullRequests(entry2, pullRequestServices);
				await CreateEnvoyDeployment(entry2);
			}
		}
	}

	public async Task<V1Service?> TryGetService(ServiceEntry entry)
	{
		if (ServiceCache.TryGetValue(entry, out V1Service service))
		{
			return service;
		}
		_003Clogger_003EP.LogWarning("Service {ServiceName} in namespace {Namespace} not found in cache, attempting to retrieve", entry.ServiceName, entry.Namespace);
		try
		{
			service = await _003Cclient_003EP.CoreV1.ReadNamespacedServiceAsync(entry.ServiceName, entry.Namespace);
		}
		catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}
		if (service != null)
		{
			ServiceCache[GetServiceKey(service)] = service;
		}
		return service;
	}

	public async Task CreateOriginalService(ServiceEntry entry)
	{
		V1Service service = await TryGetService(entry);
		if (await TryGetService(new ServiceEntry(entry.Namespace, GetOriginalName(entry.ServiceName))) == null && service != null && !service.Labels().ContainsKey("nbs.pullrequest/envoy"))
		{
			ServiceCache.TryRemove(GetServiceKey(service), out V1Service _);
			V1Service originalService = new V1Service
			{
				Metadata = new V1ObjectMeta
				{
					Name = GetOriginalName(service.Name()),
					NamespaceProperty = service.Namespace(),
					Labels = service.Metadata.Labels,
					Annotations = service.Metadata.Annotations
				},
				Spec = service.Spec
			};
			originalService.Metadata.Labels.Add("nbs.pullrequest", "true");
			originalService.Metadata.Labels.Add("nbs.pullrequest/original", "true");
			originalService.Metadata.Labels.Add("nbs.pullrequest/original-name", service.Name());
			originalService.Spec.ClusterIP = null;
			originalService.Spec.ClusterIPs = null;
			_003Clogger_003EP.LogInformation("Creating Original Service {ServiceName} in namespace {Namespace}", originalService.Name(), originalService.Namespace());
			await _003Cclient_003EP.CoreV1.CreateNamespacedServiceAsync(originalService, originalService.Namespace());
		}
	}

	public async Task RegisterPullRequests(ServiceEntry entry, IList<V1Service> pullRequests)
	{
		V1Service originalService = await TryGetService(new ServiceEntry(entry.Namespace, GetOriginalName(entry.ServiceName)));
		if (originalService == null)
		{
			_003Clogger_003EP.LogWarning("Original Service {ServiceName} in namespace {Namespace} not found", GetOriginalName(entry.ServiceName), entry.Namespace);
			return;
		}
		V1Deployment originalDeployment = await _003Cclient_003EP.AppsV1.ReadNamespacedDeploymentAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
		if (originalDeployment == null)
		{
			_003Clogger_003EP.LogWarning("Original Deployment {DeploymentName} in namespace {Namespace} not found", entry.ServiceName, entry.Namespace);
			return;
		}
		_003Clogger_003EP.LogInformation("Original Deployment {DeploymentName} in namespace {Namespace} found", entry.ServiceName, entry.Namespace);
		IDictionary<string, string> labels = originalDeployment.Labels();
		labels["app.kubernetes.io/name"] = GetEnvoyName(entry.ServiceName);
		V1ConfigMap envoyConfigMap2 = await _003Cclient_003EP.CoreV1.ReadNamespacedConfigMapAsync(GetEnvoyName(entry.ServiceName), entry.Namespace).Handle404AsNull();
		if (envoyConfigMap2 == null)
		{
			envoyConfigMap2 = new V1ConfigMap
			{
				Metadata = new V1ObjectMeta
				{
					Name = GetEnvoyName(entry.ServiceName),
					NamespaceProperty = entry.Namespace,
					Labels = labels,
					Annotations = originalDeployment.Annotations()
				},
				Data = new Dictionary<string, string> { ["envoy.yaml"] = EnvoyYaml.GetYaml(originalService, pullRequests) }
			};
			_003Clogger_003EP.LogInformation("Creating Envoy ConfigMap {ConfigMapName} in namespace {Namespace}", envoyConfigMap2.Name(), envoyConfigMap2.Namespace());
			await _003Cclient_003EP.CoreV1.CreateNamespacedConfigMapAsync(envoyConfigMap2, entry.Namespace);
		}
		else
		{
			V1Patch patch = new V1Patch(new
			{
				Data = new Dictionary<string, string> { ["envoy.yaml"] = EnvoyYaml.GetYaml(originalService, pullRequests) }
			}, V1Patch.PatchType.MergePatch);
			_003Clogger_003EP.LogInformation("Patching Envoy ConfigMap {ConfigMapName} in namespace {Namespace}", envoyConfigMap2.Name(), envoyConfigMap2.Namespace());
			await _003Cclient_003EP.CoreV1.PatchNamespacedConfigMapAsync(patch, envoyConfigMap2.Name(), envoyConfigMap2.Namespace());
		}
	}

	public async Task CreateEnvoyDeployment(ServiceEntry entry)
	{
		using (_003Clogger_003EP.BeginScope("Envoy Deployment - "))
		{
			V1Service originalService = await TryGetService(new ServiceEntry(entry.Namespace, GetOriginalName(entry.ServiceName)));
			if (originalService == null)
			{
				_003Clogger_003EP.LogWarning("Original Service {ServiceName} in namespace {Namespace} not found", GetOriginalName(entry.ServiceName), entry.Namespace);
				return;
			}
			_003Clogger_003EP.LogInformation("Original Service {ServiceName} in namespace {Namespace} found", GetOriginalName(entry.ServiceName), entry.Namespace);
			V1Service envoyService = await TryGetService(entry);
			if (envoyService != null && !envoyService.Labels().ContainsKey("nbs.pullrequest/envoy"))
			{
				_003Clogger_003EP.LogInformation("Deleting Service {ServiceName} in namespace {Namespace} before creating envoy", envoyService.Name(), envoyService.Namespace());
				ServiceCache.TryRemove(GetServiceKey(envoyService), out V1Service _);
				await _003Cclient_003EP.CoreV1.DeleteNamespacedServiceAsync(envoyService.Name(), envoyService.Namespace());
			}
			envoyService = await TryGetService(entry);
			if (envoyService == null)
			{
				IDictionary<string, string> envoyLabels = originalService.Labels();
				envoyLabels.Remove("nbs.pullrequest/original");
				envoyLabels.Remove("nbs.pullrequest/original-name");
				envoyLabels.TryAdd("nbs.pullrequest", "true");
				envoyLabels.TryAdd("nbs.pullrequest/generated", "true");
				envoyLabels.TryAdd("nbs.pullrequest/envoy", "true");
				envoyService = new V1Service
				{
					Metadata = new V1ObjectMeta
					{
						Name = entry.ServiceName,
						NamespaceProperty = entry.Namespace,
						Labels = envoyLabels,
						Annotations = originalService.Annotations()
					},
					Spec = new V1ServiceSpec
					{
						Selector = new Dictionary<string, string> { ["app.kubernetes.io/name"] = GetEnvoyName(entry.ServiceName) },
						Ports = new List<V1ServicePort>
						{
							new V1ServicePort
							{
								Port = 80,
								TargetPort = _envoyOptions.ContainerPort
							},
							new V1ServicePort
							{
								Port = _envoyOptions.AdminContainerPort,
								TargetPort = _envoyOptions.AdminContainerPort
							}
						}
					}
				};
				_003Clogger_003EP.LogInformation("Creating Envoy Service {ServiceName} in namespace {Namespace} before creating envoy", envoyService.Name(), envoyService.Namespace());
				envoyService = await _003Cclient_003EP.CoreV1.CreateNamespacedServiceAsync(envoyService, envoyService.Namespace());
				ServiceCache[GetServiceKey(envoyService)] = envoyService;
			}
			else
			{
				_003Clogger_003EP.LogInformation("Envoy Service {ServiceName} in namespace {Namespace} already exists", envoyService.Name(), envoyService.Namespace());
			}
			V1Deployment originalDeployment = await _003Cclient_003EP.AppsV1.ReadNamespacedDeploymentAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
			if (originalDeployment == null)
			{
				_003Clogger_003EP.LogWarning("Original Deployment {DeploymentName} in namespace {Namespace} not found", entry.ServiceName, entry.Namespace);
				return;
			}
			_003Clogger_003EP.LogInformation("Original Deployment {DeploymentName} in namespace {Namespace} found", entry.ServiceName, entry.Namespace);
			IDictionary<string, string> labels = originalDeployment.Labels();
			labels["app.kubernetes.io/name"] = GetEnvoyName(entry.ServiceName);
			V1Deployment envoyDeployment2 = await _003Cclient_003EP.AppsV1.ReadNamespacedDeploymentAsync(GetEnvoyName(entry.ServiceName), entry.Namespace).Handle404AsNull();
			if (envoyDeployment2 == null)
			{
				envoyDeployment2 = new V1Deployment
				{
					Metadata = new V1ObjectMeta
					{
						Name = GetEnvoyName(entry.ServiceName),
						NamespaceProperty = entry.Namespace,
						Labels = labels,
						Annotations = originalDeployment.Annotations()
					},
					Spec = new V1DeploymentSpec
					{
						Selector = new V1LabelSelector
						{
							MatchLabels = new Dictionary<string, string> { ["app.kubernetes.io/name"] = GetEnvoyName(entry.ServiceName) }
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
										Image = "envoyproxy/envoy:v1.26.0",
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
										Args = new List<string> { "--config-path", "/etc/envoy/envoy.yaml" },
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
											Name = GetEnvoyName(entry.ServiceName)
										}
									}
								}
							}
						}
					}
				};
				_003Clogger_003EP.LogInformation("Creating Envoy Deployment {DeploymentName} in namespace {Namespace}", envoyDeployment2.Name(), envoyDeployment2.Namespace());
				await _003Cclient_003EP.AppsV1.CreateNamespacedDeploymentAsync(envoyDeployment2, entry.Namespace);
			}
			else
			{
				_003Clogger_003EP.LogInformation("Envoy Deployment {DeploymentName} in namespace {Namespace} already exists", envoyDeployment2.Name(), envoyDeployment2.Namespace());
			}
		}
	}

	public async Task RemoveEnvoy(ServiceEntry entry)
	{
		V1Deployment deployment = await _003Cclient_003EP.AppsV1.ReadNamespacedDeploymentAsync(GetEnvoyName(entry.ServiceName), entry.Namespace).Handle404AsNull();
		if (deployment != null)
		{
			_003Clogger_003EP.LogInformation("Deleting Envoy Deployment {DeploymentName} in namespace {Namespace}", deployment.Name(), deployment.Namespace());
			await _003Cclient_003EP.AppsV1.DeleteNamespacedDeploymentAsync(GetEnvoyName(entry.ServiceName), entry.Namespace, new V1DeleteOptions
			{
				Preconditions = new V1Preconditions
				{
					ResourceVersion = deployment.ResourceVersion(),
					Uid = deployment.Uid()
				}
			});
		}
		V1Service service = await _003Cclient_003EP.CoreV1.ReadNamespacedServiceAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
		if (service?.Labels().ContainsKey("nbs.pullrequest/envoy") ?? false)
		{
			ServiceCache.TryRemove(GetServiceKey(service), out V1Service _);
			_003Clogger_003EP.LogInformation("Deleting Envoy Service {ServiceName} in namespace {Namespace}", service.Name(), service.Namespace());
			await _003Cclient_003EP.CoreV1.DeleteNamespacedServiceAsync(service.Name(), service.Namespace(), new V1DeleteOptions
			{
				Preconditions = new V1Preconditions
				{
					ResourceVersion = service.ResourceVersion(),
					Uid = service.Uid()
				}
			});
		}
	}

	public async Task RevertOriginalService(ServiceEntry entry)
	{
		V1Service service = await _003Cclient_003EP.CoreV1.ReadNamespacedServiceAsync(entry.ServiceName, entry.Namespace).Handle404AsNull();
		if (!service.Labels().ContainsKey("nbs.pullrequest/original"))
		{
			_003Clogger_003EP.LogWarning("Service {ServiceName} in namespace {Namespace} is not marked with the original labels", service.Name(), service.Namespace());
			return;
		}
		string originalName = service.Labels()["nbs.pullrequest/original-name"];
		IDictionary<string, string> labels = service.Labels();
		labels.Remove("nbs.pullrequest");
		labels.Remove("nbs.pullrequest/original");
		labels.Remove("nbs.pullrequest/original-name");
		V1Patch servicePatch = new V1Patch(new
		{
			Metadata = new
			{
				name = originalName,
				labels = labels
			}
		}, V1Patch.PatchType.JsonPatch);
		ServiceCache.TryRemove(GetServiceKey(service), out V1Service _);
		_003Clogger_003EP.LogInformation("Reverting Original Service {ServiceName} in namespace {Namespace}", service.Name(), service.Namespace());
		await _003Cclient_003EP.CoreV1.PatchNamespacedServiceAsync(servicePatch, service.Name(), service.Namespace());
	}

	private async Task StartConsumer(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (ServiceEntry entry in ProcessorChannel.Reader.ReadAllAsync(cancellationToken))
			{
				await Reconcile(entry);
			}
		}
		catch (OperationCanceledException)
		{
			_003Clogger_003EP.LogWarning("Cancellation Requested");
		}
		catch (Exception ex3)
		{
			Exception ex = ex3;
			_003Clogger_003EP.LogCritical(ex, "An unexpected exception occured during cluster reconciliation");
		}
		finally
		{
			_003Clogger_003EP.LogInformation("Consumer complete");
		}
	}
}
