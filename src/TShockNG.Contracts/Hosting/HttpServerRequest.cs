using System.Collections.Generic;

namespace TShockNG.Contracts.Hosting;

public sealed record HttpServerRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    string RemoteAddress);
