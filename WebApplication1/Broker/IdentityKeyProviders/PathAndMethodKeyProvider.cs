using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace WebApplication1.Broker.IdentityKeyProviders;

public class PathAndMethodKeyProvider : IIdentityKeyProvider
{
    public string GetKey(HttpRequest request)
    {
        var path = request.Path.Value ?? "/";
        var method = request.Method ?? "GET";
        var composite = method + " " + path;
        var bytes = Encoding.UTF8.GetBytes(composite);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach(var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}


