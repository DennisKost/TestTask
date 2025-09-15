using Microsoft.AspNetCore.Http;

namespace WebApplication1.Broker;

public interface IBrokerClient
{
    Task<BrokerResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken);
}

public readonly record struct BrokerResponse(int StatusCode, byte[] Body);


