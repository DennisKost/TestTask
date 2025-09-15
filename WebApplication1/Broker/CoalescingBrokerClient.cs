using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WebApplication1.Options;

namespace WebApplication1.Broker;

public class CoalescingBrokerClient(IBrokerClient inner, IIdentityKeyProvider keyProvider, BrokerOptions options, ILogger<CoalescingBrokerClient> logger) : IBrokerClient
{
    private readonly IBrokerClient _inner = inner;
    private readonly IIdentityKeyProvider _keyProvider = keyProvider;
    private readonly BrokerOptions _options = options;
    private readonly ILogger<CoalescingBrokerClient> _logger = logger;
    private readonly ConcurrentDictionary<string, Task<BrokerResponse>> _inflight = new();

    public Task<BrokerResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var key = _keyProvider.GetKey(request);
        var task = _inflight.GetOrAdd(key, x => ExecuteAsync(request, key, cancellationToken));
        return task.ContinueWith(t =>
        {
            _inflight.TryRemove(key, out _);
            return t.Result;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<BrokerResponse> ExecuteAsync(HttpRequest request, string key, CancellationToken ct)
    {
        try
        {
            return await _inner.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Coalesced request failed for key {Key}", key);
            return new BrokerResponse(StatusCodes.Status502BadGateway, Array.Empty<byte>());
        }
    }
}


