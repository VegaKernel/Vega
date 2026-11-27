namespace Vega.Contracts.Operations;

public enum PluginRestMethod { Query, Action }

public enum PluginRestStatus { Success, NoContent, BadRequest, NotFound, Conflict, Unavailable, Failed }

/// <summary>Host-side bounded JSON reply; never carries headers, tokens or arbitrary HTTP objects.</summary>
public sealed record PluginRestResponse(PluginRestStatus Status, byte[]? Json = null);

/// <summary>Called only by the authenticated operator REST listener. Not exposed as an ordinary SDK capability.</summary>
public interface IPluginRestRouter
{
    ValueTask<PluginRestResponse> DispatchAsync(string plugin, string operation, PluginRestMethod method,
        byte[] requestJson, CancellationToken cancellationToken = default);
}
