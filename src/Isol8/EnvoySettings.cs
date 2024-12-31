using k8s.Models;

namespace Isol8;

public class EnvoyOptions
{
    public const string Key = "Envoy";
    public string Image { get; set; } = "envoyproxy/envoy:v1.26.0";
    public short IngressPort { get; set; } = 80;
    public short ServicePort { get; set; } = 80;
    public short ContainerPort { get; set; } = 8080;
    public short AdminContainerPort { get; set; } = 8081;

    public Dictionary<string, ResourceQuantity> Requests { get; set; } = new Dictionary<string, ResourceQuantity>
    {
        ["memory"] = new ResourceQuantity("128Mi"),
        ["cpu"] = new ResourceQuantity("250m")
    };

    public Dictionary<string, ResourceQuantity> Limits { get; set; } = new Dictionary<string, ResourceQuantity>
    {
        ["memory"] = new ResourceQuantity("256Mi"),
        ["cpu"] = new ResourceQuantity("500m")
    };
}

