using System;
using System.Threading;
using System.Threading.Tasks;

namespace TShockNG.Contracts.Hosting;

public interface IHttpServer : IDisposable
{
    ValueTask StartAsync(
        Func<HttpServerRequest, CancellationToken, ValueTask<HttpServerResponse>> handler,
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
