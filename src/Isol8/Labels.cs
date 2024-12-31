namespace Isol8.Operator;

public static class Constants
{
    private const string Prefix = "isol8.io";

    public const string WatchLabel = $"{Prefix}/watch";
    public const string LabelSelector = $"{WatchLabel}=true";
    public const string EnabledAttribute = $"{Prefix}/enabled";
    public const string GeneratedAttribute = $"{Prefix}/generated";

    public const string OriginalAttrribute = $"{Prefix}/original";
    public const string OriginalNameAttrribute = $"{Prefix}/original-name";

    public const string EnvoyAttrribute = $"{Prefix}/envoy";
    public const string RouteFromAttrribute = $"{Prefix}/route-from";
    public const string RouteFromNamespaceAttrribute = $"{Prefix}/route-from-namespace";
    public const string RouteOnAttrribute = $"{Prefix}/route-on";

    public const int ServiceNameLength = 63;

    /// <summary>
    /// Returns a prefixed service name for the original service.
    /// </summary>
    /// <param name="entry"></param>
    /// <returns></returns>
    public static string GetOriginalName(this ServiceEntry entry)
        => $"original-{entry.ServiceName}".Truncate(Constants.ServiceNameLength);

    /// <summary>
    /// Returns a prefixed service name for the envoy service.
    /// </summary>
    /// <param name="entry"></param>
    /// <returns></returns>
    public static string GetEnvoyName(this ServiceEntry entry)
        => $"envoy-{entry.ServiceName}".Truncate(Constants.ServiceNameLength);
}