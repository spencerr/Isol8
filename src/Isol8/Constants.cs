namespace Isol8;

public static class Constants
{
    private const string Prefix = "isol8.io";

    public const string WatchLabel = $"{Prefix}/watch";
    public const string LabelSelector = $"{WatchLabel}=true";

    public const string GeneratedAnnotation = $"{Prefix}/generated";
    public const string EnvoyAnnotation = $"{Prefix}/envoy";
    public const string WatchNameAnnotation = $"{Prefix}/watch-name";

    public const string OriginalNameAnnotation = $"{Prefix}/original-name";
    public const string RouteFromAnnotation = $"{Prefix}/route-from";
    public const string RouteFromNamespaceAnnotation = $"{Prefix}/route-from-namespace";
    public const string RouteOnAnnotation = $"{Prefix}/route-on";

    public const int ServiceNameLength = 63;
}