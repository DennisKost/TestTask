using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WebApplication1.Options;

namespace WebApplication1.Broker;

public class FileBrokerClient(BrokerOptions options, ILogger<FileBrokerClient> logger) : IBrokerClient, IDisposable
{
    private readonly BrokerOptions _options = options;
    private readonly ILogger<FileBrokerClient> _logger = logger;
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<BrokerResponse>>> _waiters = new();
    private FileSystemWatcher? _watcher;
    private volatile bool _disposed;

    public Task<BrokerResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if(_disposed)
        {
            throw new ObjectDisposedException(nameof(FileBrokerClient));
        }

        if(!Directory.Exists(_options.DirectoryPath))
        {
            _logger.LogError("Broker directory '{Directory}' is not available", _options.DirectoryPath);
            return Task.FromResult(new BrokerResponse(StatusCodes.Status503ServiceUnavailable, Array.Empty<byte>()));
        }

        var method = request.Method ?? "GET";
        var path = request.Path.Value ?? "/";
        var key = ComputeFileKey(method, path);
        var reqPath = Path.Combine(_options.DirectoryPath, key + ".req");
        var respPath = Path.Combine(_options.DirectoryPath, key + ".resp");

        var tcs = new TaskCompletionSource<BrokerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = _waiters.GetOrAdd(key, _ => new List<TaskCompletionSource<BrokerResponse>>());
        lock(list)
        {
            list.Add(tcs);
        }

        EnsureRequestFileExists(reqPath, method, path);

        if(File.Exists(respPath))
        {
            var maybe = TryReadAndDeleteResponse(respPath, reqPath);
            if(maybe.HasValue)
            {
                CompleteAllWaiters(key, maybe.Value);
            }
        }

        var opTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.CoalescedTtlSeconds));
        var timeoutTask = Task.Delay(opTimeout, CancellationToken.None);

        return WaitAnyAsync(tcs.Task, timeoutTask, cancellationToken);
    }

    public void Dispose()
    {
        if(_disposed)
        {
            return;
        }
        _disposed = true;
        _watcher?.Dispose();
        foreach(var pair in _waiters)
        {
            lock(pair.Value)
            {
                foreach(var w in pair.Value)
                {
                    TrySetCanceled(w);
                }
                pair.Value.Clear();
            }
        }
        _waiters.Clear();
        GC.SuppressFinalize(this);
    }

    public FileBrokerClient Start()
    {
        _watcher = new FileSystemWatcher
        {
            Path = _options.DirectoryPath,
            Filter = "*.resp",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = Directory.Exists(_options.DirectoryPath)
        };
        _watcher.Created += (_, e) => OnResponseCreatedInternal(e.FullPath);
        _watcher.Changed += (_, e) => OnResponseCreatedInternal(e.FullPath);
        return this;
    }

    private static string ComputeFileKey(string method, string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var composite = method + path;
        var bytes = Encoding.UTF8.GetBytes(composite);
        var hash = md5.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach(var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private void CompleteAllWaiters(string key, BrokerResponse response)
    {
        if(_waiters.TryRemove(key, out var list))
        {
            lock(list)
            {
                foreach(var t in list)
                {
                    t.TrySetResult(response);
                }
                list.Clear();
            }
        }
    }

    private void EnsureRequestFileExists(string reqPath, string method, string path)
    {
        try
        {
            if(!File.Exists(reqPath))
            {
                var content = method + "\n" + path + "\n";
                File.WriteAllText(reqPath, content, Encoding.UTF8);
                _logger.LogInformation("Request enqueued: {File}", reqPath);
            }
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Failed to create request file {File}", reqPath);
        }
    }

    private static void TrySetCanceled(TaskCompletionSource<BrokerResponse> tcs) => tcs.TrySetResult(new BrokerResponse(StatusCodes.Status499ClientClosedRequest, Array.Empty<byte>()));

    private static Task<BrokerResponse> WaitAnyAsync(Task<BrokerResponse> resultTask, Task timeoutTask, CancellationToken ct)
    {
        return CoreAsync(resultTask, timeoutTask, ct);

        static async Task<BrokerResponse> CoreAsync(Task<BrokerResponse> resultTask, Task timeoutTask, CancellationToken ct)
        {
            var cancelTcs = new TaskCompletionSource<BrokerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => cancelTcs.TrySetResult(new BrokerResponse(StatusCodes.Status499ClientClosedRequest, Array.Empty<byte>())));
            var completed = await Task.WhenAny(resultTask, timeoutTask, cancelTcs.Task).ConfigureAwait(false);
            if(ReferenceEquals(completed, timeoutTask))
            {
                return new BrokerResponse(StatusCodes.Status504GatewayTimeout, Array.Empty<byte>());
            }
            if(ReferenceEquals(completed, cancelTcs.Task))
            {
                return await cancelTcs.Task.ConfigureAwait(false);
            }
            return await resultTask.ConfigureAwait(false);
        }
    }

    private void OnResponseCreatedInternal(string respPath)
    {
        var key = Path.GetFileNameWithoutExtension(respPath) ?? "";
        var reqPath = Path.Combine(_options.DirectoryPath, key + ".req");

        var maybe = TryReadAndDeleteResponse(respPath, reqPath);
        if(maybe.HasValue)
        {
            CompleteAllWaiters(key, maybe.Value);
        }
    }

    private BrokerResponse? TryReadAndDeleteResponse(string respPath, string reqPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(5);
        while(sw.Elapsed < deadline)
        {
            try
            {
                using var stream = new FileStream(respPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var firstLine = reader.ReadLine();
                if(!int.TryParse(firstLine, out var status))
                {
                    status = StatusCodes.Status502BadGateway;
                }
                var rest = reader.ReadToEnd();
                var body = Encoding.UTF8.GetBytes(rest ?? "");
                var response = new BrokerResponse(status, body);
                try
                {
                    File.Delete(respPath);
                }
                catch(Exception exDel)
                {
                    _logger.LogError(exDel, "Failed to delete response file {File}", respPath);
                }
                try
                {
                    if(File.Exists(reqPath))
                    {
                        File.Delete(reqPath);
                    }
                }
                catch(Exception exDel2)
                {
                    _logger.LogError(exDel2, "Failed to delete request file {File}", reqPath);
                }
                return response;
            }
            catch(IOException)
            {
                Thread.Sleep(25);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Failed to process response file {File}", respPath);
                return new BrokerResponse(StatusCodes.Status502BadGateway, Array.Empty<byte>());
            }
        }
        return null;
    }

    private static void OnResponseCreated(object sender, FileSystemEventArgs e) {}
}


