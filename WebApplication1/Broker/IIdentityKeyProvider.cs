using Microsoft.AspNetCore.Http;

namespace WebApplication1.Broker;

public interface IIdentityKeyProvider
{
    string GetKey(HttpRequest request);
}


