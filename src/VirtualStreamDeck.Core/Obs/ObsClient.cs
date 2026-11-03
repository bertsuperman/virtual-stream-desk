using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirtualStreamDeck.Core.Obs;

/// <summary>Error with a user-facing (Slovak) message.</summary>
public sealed class ObsException : Exception
{
    public ObsException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Minimal client for the obs-websocket v5 protocol that ships with OBS 28+.
/// Connects lazily on the first request, keeps the connection open and reconnects when it drops.
/// Thread-safe: requests may be sent from several threads at once.
/// Protocol: https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md
/// </summary>
public sealed class ObsClient : IDisposable
{
    private const int OpHello = 0, OpIdentify = 1, OpIdentified = 2, OpRequest = 6, OpRequestResponse = 7;
    private const int CloseAuthenticationFailed = 4009, CloseUnsupportedRpcVersion = 4010;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private ObsSettings _settings = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource<JsonElement>? _hello;
    private TaskCompletionSource<bool>? _identified;
    private volatile bool _ready;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsConnected => _ready && _ws?.State == WebSocketState.Open;

    /// <summary>Applies new connection settings. Drops the current connection only if they changed.</summary>
    public void Configure(ObsSettings settings)
    {
        lock (_stateLock)
        {
            if (settings.Host == _settings.Host && settings.Port == _settings.Port && settings.Password == _settings.Password)
                return;
            _settings = new ObsSettings { Host = settings.Host, Port = settings.Port, Password = settings.Password };
        }
        Disconnect();
    }

    // ───────────────────────── high level ─────────────────────────

    public async Task ExecuteAsync(ObsAction action, CancellationToken ct = default)
    {
        switch (action.Command)
        {
            case ObsCommand.SwitchScene:
                await RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = Require(action.Scene, L.T("scénu", "scene")) }, ct);
                break;
            case ObsCommand.ToggleRecord:
                await RequestAsync("ToggleRecord", null, ct);
                break;
            case ObsCommand.ToggleStream:
                await RequestAsync("ToggleStream", null, ct);
                break;
            case ObsCommand.SaveReplay:
                await RequestAsync("SaveReplayBuffer", null, ct);
                break;
            case ObsCommand.ToggleMute:
                await RequestAsync("ToggleInputMute", new JsonObject { ["inputName"] = Require(action.Source, L.T("zdroj zvuku", "audio source")) }, ct);
                break;
            case ObsCommand.ToggleSource:
                await ToggleSourceAsync(action.Scene, Require(action.Source, L.T("zdroj", "source")), ct);
                break;
            default:
                throw new ObsException(L.T("Neznámy OBS príkaz.", "Unknown OBS command."));
        }
    }

    private async Task ToggleSourceAsync(string? scene, string source, CancellationToken ct)
    {
        scene = string.IsNullOrWhiteSpace(scene) ? await GetCurrentSceneAsync(ct) : scene;
        var id = await RequestAsync("GetSceneItemId", new JsonObject { ["sceneName"] = scene, ["sourceName"] = source }, ct);
        int itemId = id.GetProperty("sceneItemId").GetInt32();
        var state = await RequestAsync("GetSceneItemEnabled", new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = itemId }, ct);
        bool enabled = state.GetProperty("sceneItemEnabled").GetBoolean();
        await RequestAsync("SetSceneItemEnabled", new JsonObject
        {
            ["sceneName"] = scene,
            ["sceneItemId"] = itemId,
            ["sceneItemEnabled"] = !enabled,
        }, ct);
    }

    /// <summary>Scenes in the same order as the OBS scene list (top first).</summary>
    public async Task<IReadOnlyList<string>> GetScenesAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetSceneList", null, ct);
        return data.GetProperty("scenes").EnumerateArray()
            .OrderByDescending(s => s.TryGetProperty("sceneIndex", out var i) ? i.GetInt32() : 0)
            .Select(s => s.GetProperty("sceneName").GetString() ?? "")
            .Where(n => n.Length > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetInputsAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetInputList", null, ct);
        return data.GetProperty("inputs").EnumerateArray()
            .Select(s => s.GetProperty("inputName").GetString() ?? "")
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Sources placed in a scene (the current program scene if <paramref name="scene"/> is empty).</summary>
    public async Task<IReadOnlyList<string>> GetSceneSourcesAsync(string? scene, CancellationToken ct = default)
    {
        scene = string.IsNullOrWhiteSpace(scene) ? await GetCurrentSceneAsync(ct) : scene;
        var data = await RequestAsync("GetSceneItemList", new JsonObject { ["sceneName"] = scene }, ct);
        return data.GetProperty("sceneItems").EnumerateArray()
            .Select(s => s.GetProperty("sourceName").GetString() ?? "")
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
    }

    public async Task<string> GetCurrentSceneAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetCurrentProgramScene", null, ct);
        // "sceneName" since obs-websocket 5.3, "currentProgramSceneName" before (still sent, deprecated).
        if (data.TryGetProperty("sceneName", out var n) && n.GetString() is { Length: > 0 } name) return name;
        return data.GetProperty("currentProgramSceneName").GetString() ?? "";
    }

    /// <summary>Connects (if needed) and returns the OBS version, e.g. for a "Test connection" button.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetVersion", null, ct);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("obsVersion", out var v)
            ? v.GetString() ?? "?"
            : "?";
    }

    private static string ConnectionLost => L.T("Spojenie s OBS sa prerušilo.", "The connection to OBS was lost.");

    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new ObsException(L.T($"Tlačidlo nemá nastavenú {what}.", $"The button has no {what} set.")) : value;

    // ───────────────────────── requests ─────────────────────────

    /// <summary>Sends a request and returns its responseData (default if the response has none).</summary>
    public async Task<JsonElement> RequestAsync(string requestType, JsonObject? requestData = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var d = new JsonObject { ["requestType"] = requestType, ["requestId"] = id };
            if (requestData != null) d["requestData"] = requestData;
            await SendAsync(OpRequest, d, ct);

            JsonElement response;
            try
            {
                response = await tcs.Task.WaitAsync(RequestTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new ObsException(L.T("OBS neodpovedá.", "OBS is not responding."));
            }

            var status = response.GetProperty("requestStatus");
            if (!status.GetProperty("result").GetBoolean())
            {
                int code = status.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                string? comment = status.TryGetProperty("comment", out var m) ? m.GetString() : null;
                throw new ObsException(DescribeFailure(requestType, code, comment));
            }

            return response.TryGetProperty("responseData", out var data) ? data.Clone() : default;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    internal static string DescribeFailure(string requestType, int code, string? comment)
    {
        string detail = comment ?? L.T($"kód {code}", $"code {code}");
        return code switch
        {
            600 => L.T($"OBS: nenašlo sa to, čo má tlačidlo ovládať ({detail}).", $"OBS: what the button controls was not found ({detail})."),
            500 or 501 => $"OBS: {detail}",
            604 => L.T($"OBS: replay buffer nie je zapnutý ({detail}).", $"OBS: the replay buffer is not running ({detail})."),
            _ => L.T($"OBS odmietlo príkaz {requestType}: {detail}", $"OBS rejected {requestType}: {detail}"),
        };
    }

    private async Task SendAsync(int op, JsonObject d, CancellationToken ct)
    {
        var ws = _ws ?? throw new ObsException(ConnectionLost);
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["op"] = op, ["d"] = d }.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            Disconnect();
            throw new ObsException(ConnectionLost, ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ───────────────────────── connection ─────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return;
            Disconnect();

            ObsSettings settings;
            lock (_stateLock) settings = _settings;

            var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("obswebsocket.json");
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            var hello = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var identified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);
                await ws.ConnectAsync(new Uri($"ws://{settings.Host}:{settings.Port}"), timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or UriFormatException or HttpRequestException)
            {
                ws.Dispose();
                ct.ThrowIfCancellationRequested();
                throw new ObsException(
                    L.T($"Nedá sa pripojiť k OBS ({settings.Host}:{settings.Port}). Beží OBS a je zapnutý WebSocket server? " +
                        "V OBS: Nástroje → Nastavenia WebSocket servera.",
                        $"Cannot connect to OBS ({settings.Host}:{settings.Port}). Is OBS running with the WebSocket server enabled? " +
                        "In OBS: Tools → WebSocket Server Settings."), ex);
            }

            var loopCts = new CancellationTokenSource();
            lock (_stateLock)
            {
                _ws = ws;
                _hello = hello;
                _identified = identified;
                _loopCts = loopCts;
            }
            _ = Task.Run(() => ReceiveLoopAsync(ws, loopCts.Token));

            try
            {
                var helloData = await hello.Task.WaitAsync(ConnectTimeout, ct);

                var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
                if (helloData.TryGetProperty("authentication", out var auth))
                {
                    if (string.IsNullOrEmpty(settings.Password))
                        throw new ObsException(L.T("OBS vyžaduje heslo. Zadaj ho v nastaveniach Stream Decku (sekcia OBS).",
                            "OBS requires a password. Enter it in the Stream Deck settings (OBS section)."));
                    identify["authentication"] = ComputeAuthentication(
                        settings.Password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
                }
                await SendAsync(OpIdentify, identify, ct);
                await identified.Task.WaitAsync(ConnectTimeout, ct);
                _ready = true;
            }
            catch (TimeoutException ex)
            {
                Disconnect();
                throw new ObsException(L.T("OBS neodpovedá pri pripájaní.", "OBS did not respond while connecting."), ex);
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>base64(sha256(base64(sha256(password + salt)) + challenge))</summary>
    internal static string ComputeAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        Exception failure = new ObsException(ConnectionLost);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        failure = DescribeClose(ws.CloseStatus, ws.CloseStatusDescription);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                HandleMessage(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // connection lost or closed by Disconnect()
        }
        finally
        {
            if (ReferenceEquals(_ws, ws)) _ready = false;
            FailWaiters(ws, failure);
        }
    }

    private static ObsException DescribeClose(WebSocketCloseStatus? status, string? description) => (int?)status switch
    {
        CloseAuthenticationFailed => new ObsException(L.T("Nesprávne heslo k OBS. Skontroluj ho v nastaveniach (sekcia OBS).", "Wrong OBS password. Check it in the settings (OBS section).")),
        CloseUnsupportedRpcVersion => new ObsException(L.T("Táto verzia OBS nie je podporovaná. Treba OBS 28 alebo novšie.", "This OBS version is not supported. OBS 28 or newer is required.")),
        _ => new ObsException(L.T("OBS ukončilo spojenie", "OBS closed the connection") + (string.IsNullOrEmpty(description) ? "." : $": {description}")),
    };

    private void HandleMessage(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (!root.TryGetProperty("op", out var opEl) || !root.TryGetProperty("d", out var d)) return;

        switch (opEl.GetInt32())
        {
            case OpHello:
                _hello?.TrySetResult(d.Clone());
                break;
            case OpIdentified:
                _identified?.TrySetResult(true);
                break;
            case OpRequestResponse:
                if (d.TryGetProperty("requestId", out var id) && id.GetString() is { } key &&
                    _pending.TryGetValue(key, out var tcs))
                {
                    tcs.TrySetResult(d.Clone());
                }
                break;
            // Events (op 5) are not subscribed to yet.
        }
    }

    private void FailWaiters(ClientWebSocket ws, Exception error)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_ws, ws)) return; // a newer connection owns the waiters
            _hello?.TrySetException(error);
            _identified?.TrySetException(error);
        }
        foreach (var tcs in _pending.Values) tcs.TrySetException(error);
    }

    public void Disconnect()
    {
        ClientWebSocket? ws;
        CancellationTokenSource? loopCts;
        lock (_stateLock)
        {
            _ready = false;
            ws = _ws;
            loopCts = _loopCts;
            _ws = null;
            _loopCts = null;
            _hello?.TrySetException(new ObsException(ConnectionLost));
            _identified?.TrySetException(new ObsException(ConnectionLost));
        }
        try { loopCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { ws?.Abort(); ws?.Dispose(); } catch { /* ignore */ }
        foreach (var tcs in _pending.Values) tcs.TrySetException(new ObsException(ConnectionLost));
    }

    public void Dispose() => Disconnect();
}
