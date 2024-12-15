namespace Pullie.PullRequest.Operator;

public static class Labels
{
    public const string LabelSelector = "pullie.pullrequest=true";
    public const string Label = "pullie.pullrequest";

    public const string OriginalLabel = "pullie.pullrequest/original";
    public const string OriginalNameLabel = "pullie.pullrequest/original-name";
    public const string EnabledLabel = "pullie.pullrequest/enabled";

    public const string GeneratedLabel = "pullie.pullrequest/generated";
    public const string EnvoyLabel = "pullie.pullrequest/envoy";
    public const string RouteFromLabel = "pullie.pullrequest/route-from";
    public const string RouteOnLabel = "pullie.pullrequest/route-on";
}