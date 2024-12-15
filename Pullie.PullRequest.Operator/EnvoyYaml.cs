using System.Text;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace Pullie.PullRequest.Operator;

public class EnvoyYaml(IOptions<EnvoyOptions> envoyOptions)
{
    private readonly EnvoyOptions _envoyOptions = envoyOptions.Value;

    public string GetYaml(V1Service originalService, IList<V1Service> pullRequests)
    {
        var builder = new StringBuilder();

        builder.Append($@"
static_resources:
  listeners:
  - name: listener_0
    address:
      socket_address: {{ address: 0.0.0.0, port_value: {_envoyOptions.ContainerPort} }}
    filter_chains:
    - filters:
      - name: envoy.filters.network.http_connection_manager
        typed_config:
          ""@type"": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
          stat_prefix: ingress_http
          route_config:
            name: local_route
            virtual_hosts:
            - name: {originalService.Namespace()}
              domains: [""*""]
              routes:
");

        foreach (var pullRequest in pullRequests)
        {
            builder.Append($@"
              - match:
                  headers:
                  - name: kubernetes-route-as
                    exact_match: {pullRequest.Labels()[Labels.RouteOnLabel]}
                  prefix: ""/""
                route:
                  cluster: {pullRequest.Name()}-cluster
");
        }

        builder.Append($@"
              - match: {{ prefix: ""/"" }}
                route:
                  cluster: {originalService.Name()}-cluster
          http_filters:
          - name: envoy.filters.http.router
            typed_config:
              ""@type"": type.googleapis.com/envoy.extensions.filters.http.router.v3.Router
  clusters:
  - name: {originalService.Name()}-cluster
    connect_timeout: 0.25s
    type: STRICT_DNS
    lb_policy: ROUND_ROBIN
    load_assignment:
      cluster_name: {originalService.Name()}-cluster
      endpoints:
      - lb_endpoints:
        - endpoint:
            address:
              socket_address: {{ address: {originalService.Name()}.{originalService.Namespace()}.svc.cluster.local, port_value: {originalService.Spec.Ports[0].Port} }}
");

        foreach (var pullRequest in pullRequests)
        {
            builder.Append($@"
  - name: {pullRequest.Name()}-cluster
    connect_timeout: 0.25s
    type: STRICT_DNS
    lb_policy: ROUND_ROBIN
    load_assignment:
      cluster_name: {pullRequest.Name()}-cluster
      endpoints:
      - lb_endpoints:
        - endpoint:
            address:
              socket_address: {{ address: {pullRequest.Name()}.{pullRequest.Namespace()}.svc.cluster.local, port_value: {pullRequest.Spec.Ports[0].Port} }}
");
        }

        builder.Append($@"
admin:
  access_log_path: /dev/stdout
  address:
    socket_address: {{ address: 0.0.0.0, port_value: {_envoyOptions.AdminContainerPort} }}
");

        return builder.Replace("\r\n", "\n").ToString();
    }
}