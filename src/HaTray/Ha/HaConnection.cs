using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaTray.Ha;

internal enum ConnectionStatus
{
    Connecting,
    Connected,
    Disconnected,
    AuthFailed,
}

/// <summary>
/// Home Assistant 的 WebSocket API（/api/websocket）：长期访问令牌认证，命令按 id 配对回包，断线自动重连。
/// 事件和状态回调都在后台线程上触发，界面要自己切回 UI 线程。
/// </summary>
internal sealed class HaConnection : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Uri _wsUri;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _nextId;
    private Task? _loop;

    public HaConnection(Uri baseUri, string token)
    {
        _wsUri = ToWebSocketUri(baseUri);
        _token = token;
    }

    public event Action<ConnectionStatus, string?>? StatusChanged;

    /// <summary>认证通过后触发；处理函数在这里拉注册表、订阅事件。</summary>
    public event Func<HaConnection, Task>? Connected;

    /// <summary>订阅推来的事件：订阅命令的 id 和事件内容。</summary>
    public event Action<int, JsonElement>? EventReceived;

    public static Uri ToWebSocketUri(Uri baseUri)
    {
        var b = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/api/websocket",
        };
        return b.Uri;
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>只连一次、认证一次就断开，给设置窗口的「保存」检查地址和令牌用。返回 null 表示成功，否则是给人看的错误。</summary>
    public static async Task<string?> TestAsync(Uri baseUri, string token)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(ToWebSocketUri(baseUri), cts.Token).ConfigureAwait(false);
            return await AuthenticateAsync(ws, token, cts.Token).ConfigureAwait(false)
                ? null
                : L.T("令牌无效或已被撤销。", "The token is invalid or has been revoked.");
        }
        catch (OperationCanceledException)
        {
            return L.T("连接超时，检查地址和端口。", "Timed out. Check the address and port.");
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        {
            return L.T("连不上：", "Couldn't connect: ") + ex.Message;
        }
    }

    public async Task<JsonElement> SendCommandAsync(JsonObject command, CancellationToken ct = default)
    {
        var ws = _ws ?? throw new InvalidOperationException("Not connected");
        var id = command["id"] is JsonValue given ? given.GetValue<int>() : NextId();
        command["id"] = id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(ws, command.ToJsonString(), ct).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var reg = timeout.Token.Register(() => tcs.TrySetException(new TimeoutException()));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>先领一个命令 id：订阅命令要在发出去之前就知道自己的 id，才不会漏掉紧跟着回来的第一条事件。</summary>
    public int NextId() => Interlocked.Increment(ref _nextId);

    public Task CallServiceAsync(string domain, string service, string entityId, JsonObject? data = null) =>
        SendCommandAsync(new JsonObject
        {
            ["type"] = "call_service",
            ["domain"] = domain,
            ["service"] = service,
            ["target"] = new JsonObject { ["entity_id"] = entityId },
            ["service_data"] = data ?? [],
        });

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _ws?.Abort();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _sendLock.Dispose();
    }

    private static async Task<bool> AuthenticateAsync(ClientWebSocket ws, string token, CancellationToken ct)
    {
        using var hello = await ReceiveAsync(ws, ct).ConfigureAwait(false);
        if (hello?.RootElement.GetProperty("type").GetString() != "auth_required")
        {
            throw new IOException("Unexpected handshake");
        }

        var auth = new JsonObject { ["type"] = "auth", ["access_token"] = token };
        await ws.SendAsync(Encoding.UTF8.GetBytes(auth.ToJsonString()), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        using var reply = await ReceiveAsync(ws, ct).ConfigureAwait(false);
        return reply?.RootElement.GetProperty("type").GetString() == "auth_ok";
    }

    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage)
            {
                break;
            }
        }

        return JsonDocument.Parse(ms.ToArray());
    }

    private async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            StatusChanged?.Invoke(ConnectionStatus.Connecting, null);
            string? error = null;
            using (var ws = new ClientWebSocket())
            {
                try
                {
                    await ws.ConnectAsync(_wsUri, ct).ConfigureAwait(false);
                    if (!await AuthenticateAsync(ws, _token, ct).ConfigureAwait(false))
                    {
                        // 令牌错了重试也没用，停在这里等用户改设置。
                        StatusChanged?.Invoke(ConnectionStatus.AuthFailed, null);
                        return;
                    }

                    _ws = ws;
                    attempt = 0;
                    var receive = ReceiveLoopAsync(ws, ct);
                    if (Connected is { } handler)
                    {
                        await handler(this).ConfigureAwait(false);
                    }

                    StatusChanged?.Invoke(ConnectionStatus.Connected, null);
                    await receive.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or JsonException or TimeoutException or InvalidOperationException)
                {
                    error = ex.Message;
                }
                finally
                {
                    _ws = null;
                    foreach (var p in _pending.Values)
                    {
                        p.TrySetException(new IOException("Disconnected"));
                    }
                }
            }

            StatusChanged?.Invoke(ConnectionStatus.Disconnected, error);
            await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (true)
        {
            using var doc = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (doc is null)
            {
                return;
            }

            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "result" && root.TryGetProperty("id", out var idEl) && _pending.TryGetValue(idEl.GetInt32(), out var tcs))
            {
                if (root.GetProperty("success").GetBoolean())
                {
                    tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
                }
                else
                {
                    var msg = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m) ? m.GetString() : null;
                    tcs.TrySetException(new HaCommandException(msg ?? "Command failed"));
                }
            }
            else if (type == "event" && root.TryGetProperty("event", out var ev) && root.TryGetProperty("id", out var subId))
            {
                EventReceived?.Invoke(subId.GetInt32(), ev.Clone());
            }
        }
    }
}

internal sealed class HaCommandException(string message) : Exception(message);
