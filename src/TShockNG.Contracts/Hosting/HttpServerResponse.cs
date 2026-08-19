using System.Collections.Generic;

namespace TShockNG.Contracts.Hosting;

public sealed record HttpServerResponse(
    int StatusCode,
    string ContentType,
    string Body,
    IReadOnlyDictionary<string, string>? Headers = null);
