using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TShockNG.Contracts.Hosting;

namespace TShockNG.Core.Hosting;

internal sealed class KestrelHttpServer : IHttpServer
{
    private readonly IPAddress address;
    private readonly int port;
    private WebApplication? application;

    public KestrelHttpServer(IPAddress address, int port)
    {
        this.address = address;
        this.port = port;
    }

    public async ValueTask StartAsync(
        Func<HttpServerRequest, CancellationToken, ValueTask<HttpServerResponse>> handler,
        CancellationToken cancellationToken = default)
    {
        if (application is not null)
            throw new InvalidOperationException("HTTP server is already running.");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(address, port);
        });

        var app = builder.Build();
        app.Run(async context =>
        {
            var query = context.Request.Query.ToDictionary(
                item => item.Key,
                item => item.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

            var request = new HttpServerRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                query,
                context.Connection.RemoteIpAddress?.ToString() ?? string.Empty);

            var response = await handler(request, context.RequestAborted);
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;

            if (response.Headers is not null)
            {
                foreach (var header in response.Headers)
                    context.Response.Headers[header.Key] = header.Value;
            }

            await context.Response.WriteAsync(response.Body, context.RequestAborted);
        });

        try
        {
            await app.StartAsync(cancellationToken);
            application = app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (application is null)
            return;

        var app = application;
        application = null;
        await app.StopAsync(cancellationToken);
        await app.DisposeAsync();
    }

    public void Dispose()
    {
        StopAsync().AsTask().GetAwaiter().GetResult();
    }
}
