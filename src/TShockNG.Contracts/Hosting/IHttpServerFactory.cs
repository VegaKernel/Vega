using System.Net;

namespace TShockNG.Contracts.Hosting;

public interface IHttpServerFactory
{
    IHttpServer Create(IPAddress address, int port);
}
