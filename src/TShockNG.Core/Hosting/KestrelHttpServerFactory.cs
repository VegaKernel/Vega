using System.Net;
using TShockNG.Contracts.Hosting;

namespace TShockNG.Core.Hosting;

public sealed class KestrelHttpServerFactory : IHttpServerFactory
{
    public IHttpServer Create(IPAddress address, int port) => new KestrelHttpServer(address, port);
}
