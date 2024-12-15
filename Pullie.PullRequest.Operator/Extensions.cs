using System.Net;
using k8s.Autorest;

namespace Pullie.PullRequest.Operator;

public static class Extensions
{
    public static string Truncate(this string str, int maxLength)
    {
        if (string.IsNullOrEmpty(str))
        {
            return str;
        }

        return str.Length <= maxLength ? str : str[..maxLength];
    }

    public static async Task<TType?> Handle404AsNull<TType>(this Task<TType> task)
        where TType : class
    {
        try
        {
            return await task;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

public static class Constants
{
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