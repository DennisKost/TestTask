using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WebApplication1.Broker;

namespace WebApplication1.Endpoints;

public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapBrokerProxy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/{**path}", new[]{"GET","POST","PUT","DELETE","PATCH","HEAD","OPTIONS"}, async context =>
        {
            var broker = context.RequestServices.GetRequiredService<IBrokerClient>();
            var result = await broker.SendAsync(context.Request, context.RequestAborted);
            context.Response.StatusCode = result.StatusCode;
            if(result.Body.Length > 0)
            {
                await context.Response.Body.WriteAsync(result.Body, 0, result.Body.Length, context.RequestAborted);
            }
        });

        return endpoints;
    }
}


