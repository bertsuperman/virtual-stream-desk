using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VirtualStreamDeck.Core.Remote;

public sealed class RemoteException : Exception
{
    public RemoteException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Tiny web server for controlling the deck from a phone on the same network.
/// No dependencies: TcpListener + a minimal HTTP/1.1 parser + WebSocket.CreateFromStream.
///
///   GET /                 the web app (public, contains no secrets)
///   GET /app.png, /manifest.webmanifest
///   GET /api/ping?t=…     200 if the token is valid, 401 otherwise
///   GET /icons/x.png?t=…  button icons
///   GET /ws?t=…           WebSocket: server sends {"type":"deck",…} (on connect and on every change);
///                         client sends {"type":"press","id":…,"seq":n} or {"type":"profile","id":…}
///
/// Every request except the static app needs the token from the QR code.
/// </summary>
public sealed partial class RemoteServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly Deck _deck;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly object _lifecycle = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile string _token = "";

    public RemoteServer(Deck deck)
    {
        _deck = deck;
        _deck.Changed += (_, _) => _ = BroadcastDeckAsync();
    }

    public bool IsRunning => _listener != null;

    /// <summary>Why the last <see cref="Start"/> failed (null when running or stopped normally).</summary>
    public string? LastError { get; private set; }
    public int Port { get; private set; }
    public int ClientCount => _clients.Count;

    /// <summary>Raised (on a background thread) when a phone connects or disconnects.</summary>
    public event Action? ClientsChanged;

    /// <summary>Raised (on a background thread) when a phone presses a button, so the desktop can light it up.</summary>
    public event Action<string>? ButtonPressed;

    /// <summary>Starts listening on all interfaces. Port 0 = pick a free one (tests).</summary>
    public void Start(int port, string token)
    {
        lock (_lifecycle)
        {
            Stop();
            _token = token;
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                LastError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? L.T($"Port {port} už používa iná aplikácia. Zvoľ iný.", $"Port {port} is already used by another app. Choose a different one.")
                    : L.T($"Server sa nepodarilo spustiť na porte {port}: {ex.Message}", $"Could not start the server on port {port}: {ex.Message}");
                throw new RemoteException(LastError, ex);
            }
            LastError = null;
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(listener, ct));
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            LastError = null;
            _cts = null;
            DisconnectAll();
        }
    }

    /// <summary>New secret: phones with the old QR code are disconnected and can't reconnect.</summary>
    public void SetToken(string token)
    {
        if (token == _token) return;
        _token = token;
        DisconnectAll();
    }

    public static string BuildUrl(string address, int port, string token) => $"http://{address}:{port}/?t={token}";

    // ───────────────────────── HTTP ─────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch { return; } // stopped
            _ = Task.Run(() => HandleConnectionAsync(tcp, ct));
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers);

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        bool keepOpen = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var request = await ReadRequestAsync(stream, timeout.Token);
            if (request == null) return;

            if (request.Method != "GET")
            {
                await RespondAsync(stream, 405, "text/plain", "Method not allowed"u8.ToArray());
                return;
            }

            switch (request.Path)
            {
                case "/":
                case "/index.html":
                    await RespondAsync(stream, 200, "text/html; charset=utf-8", IndexHtml());
                    break;
                case "/app.png":
                    await RespondAsync(stream, 200, "image/png", Asset("app.png"), cache: true);
                    break;
                case "/manifest.webmanifest":
                    await RespondAsync(stream, 200, "application/manifest+json", Manifest(request));
                    break;
                case "/api/ping":
                    await RespondAsync(stream, Authorized(request) ? 200 : 401, "application/json", "{}"u8.ToArray());
                    break;
                case "/ws":
                    if (!Authorized(request))
                    {
                        await RespondAsync(stream, 401, "text/plain", "Unauthorized"u8.ToArray());
                        return;
                    }
                    keepOpen = true;
                    await RunWebSocketAsync(tcp, stream, request, ct);
                    break;
                default:
                    if (request.Path.StartsWith("/icons/", StringComparison.Ordinal))
                        await ServeIconAsync(stream, request);
                    else
                        await RespondAsync(stream, 404, "text/plain", "Not found"u8.ToArray());
                    break;
            }
        }
        catch
        {
            // client went away, malformed request, timeout…
        }
        finally
        {
            if (!keepOpen) tcp.Dispose();
        }
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct);
            if (n == 0) return null;
            length += n;
            int end = IndexOfHeaderEnd(buffer, length);
            if (end >= 0) return ParseRequest(Encoding.ASCII.GetString(buffer, 0, end));
            if (length == buffer.Length) return null; // headers too large
        }
    }

    private static int IndexOfHeaderEnd(byte[] buf, int length)
    {
        for (int i = 3; i < length; i++)
            if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n') return i - 3;
        return -1;
    }

    private static Request? ParseRequest(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 3) return null;

        var target = parts[1];
        int q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair);
                query[key] = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : "";
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new Request(parts[0], path, query, headers);
    }

    /// <summary>
    /// With a valid token the home-screen icon starts at "/?t=…": an app added to the home screen
    /// (on iPhone it doesn't share storage with Safari) then connects without scanning again.
    /// </summary>
    private byte[] Manifest(Request request)
    {
        var json = Encoding.UTF8.GetString(Asset("manifest.webmanifest"));
        if (Authorized(request))
            json = json.Replace("\"start_url\": \"/\"", $"\"start_url\": \"/?t={Uri.EscapeDataString(request.Query["t"])}\"");
        return Encoding.UTF8.GetBytes(json);
    }

    private bool Authorized(Request request)
    {
        var token = _token;
        if (token.Length == 0 || !request.Query.TryGetValue("t", out var given)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
    }

    [GeneratedRegex(@"^/icons/([A-Za-z0-9_-]{1,64}\.(png|jpg|jpeg|gif|bmp|ico|webp))$")]
    private static partial Regex IconPathRegex();

    private async Task ServeIconAsync(NetworkStream stream, Request request)
    {
        var m = IconPathRegex().Match(request.Path);
        if (!Authorized(request) || !m.Success)
        {
            await RespondAsync(stream, m.Success ? 401 : 404, "text/plain", "No"u8.ToArray());
            return;
        }
        var file = Path.Combine(_deck.Store.IconsDirectory, m.Groups[1].Value);
        if (!File.Exists(file))
        {
            await RespondAsync(stream, 404, "text/plain", "Not found"u8.ToArray());
            return;
        }
        var type = m.Groups[2].Value switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "ico" => "image/x-icon",
            _ => "image/webp",
        };
        await RespondAsync(stream, 200, type, await File.ReadAllBytesAsync(file), cache: true);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string contentType, byte[] body, bool cache = false)
    {
        var reason = status switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   (cache ? "Cache-Control: max-age=86400\r\n" : "Cache-Control: no-store\r\n") +
                   "X-Content-Type-Options: nosniff\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static readonly ConcurrentDictionary<string, byte[]> AssetCache = new();

    /// <summary>The page in the PC's language, so the "connecting…" screen is right before the first message.</summary>
    private static byte[] IndexHtml() => L.Current == AppLanguage.Sk
        ? Asset("index.html")
        : AssetCache.GetOrAdd("index.html#en", _ =>
            System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(Asset("index.html"))
                .Replace("<html lang=\"sk\">", "<html lang=\"en\">")));

    private static byte[] Asset(string name) => AssetCache.GetOrAdd(name, n =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VirtualStreamDeck.Core.Remote.web." + n)
                      ?? throw new FileNotFoundException(n);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    // ───────────────────────── WebSocket ─────────────────────────

    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public required TcpClient Tcp { get; init; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private async Task RunWebSocketAsync(TcpClient tcp, NetworkStream stream, Request request, CancellationToken ct)
    {
        if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key) ||
            !request.Headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(stream, 404, "text/plain", "WebSocket expected"u8.ToArray());
            tcp.Dispose();
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), ct);

        var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(10),
            KeepAliveTimeout = TimeSpan.FromSeconds(25), // drop phones that went to sleep / left the Wi-Fi
        });
        var client = new Client { Socket = socket, Tcp = tcp };
        var id = Guid.NewGuid();
        _clients[id] = client;
        ClientsChanged?.Invoke();

        try
        {
            await SendAsync(client, BuildDeckMessage());
            await ReceiveLoopAsync(client, ct);
        }
        catch
        {
            // disconnected
        }
        finally
        {
            _clients.TryRemove(id, out _);
            socket.Dispose();
            tcp.Dispose();
            ClientsChanged?.Invoke();
        }
    }

    private async Task ReceiveLoopAsync(Client client, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (client.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await client.Socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                message.Write(buffer, 0, r.Count);
                if (message.Length > 64 * 1024) return; // nonsense
            } while (!r.EndOfMessage);

            JsonNode? msg;
            try { msg = JsonNode.Parse(message.ToArray()); }
            catch (JsonException) { continue; }

            var type = msg?["type"]?.GetValue<string>();
            if (type == "profile" && msg!["id"]?.GetValue<string>() is { } profileId)
            {
                _deck.SwitchProfile(profileId); // Changed → everyone gets the new grid
                continue;
            }

            if (type == "press" && msg!["id"]?.GetValue<string>() is { } buttonId)
            {
                var seq = msg["seq"]?.DeepClone();
                ButtonPressed?.Invoke(buttonId);
                _ = Task.Run(async () =>
                {
                    var result = await _deck.PressAsync(buttonId);
                    var reply = new JsonObject
                    {
                        ["type"] = "result",
                        ["id"] = buttonId,
                        ["seq"] = seq,
                        ["ok"] = result.Success,
                        ["error"] = result.Error,
                    };
                    try { await SendAsync(client, reply.ToJsonString()); } catch { /* gone */ }
                });
            }
        }
    }

    private static async Task SendAsync(Client client, string json)
    {
        await client.SendLock.WaitAsync();
        try
        {
            await client.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private async Task BroadcastDeckAsync()
    {
        if (_clients.IsEmpty) return;
        var json = BuildDeckMessage();
        foreach (var c in _clients.Values)
        {
            try { await SendAsync(c, json); } catch { /* the receive loop will clean up */ }
        }
    }

    private void DisconnectAll()
    {
        foreach (var (_, c) in _clients)
        {
            try { c.Tcp.Close(); } catch { /* ignore */ }
        }
    }

    /// <summary>What the phone needs to draw the grid. Hidden buttons are left out.</summary>
    internal string BuildDeckMessage() => _deck.Read(config =>
    {
        var buttons = new JsonArray();
        foreach (var b in config.VisibleButtons)
        {
            string? icon = null;
            if (_deck.Store.ResolveIconPath(b.Icon) is { } full) icon = "/icons/" + Path.GetFileName(full);
            buttons.Add(new JsonObject
            {
                ["id"] = b.Id,
                ["row"] = b.Row,
                ["col"] = b.Col,
                ["label"] = b.Label,
                ["icon"] = icon,
                ["emoji"] = Emoji(b.Action),
            });
        }
        return new JsonObject
        {
            ["type"] = "deck",
            ["rows"] = config.Rows,
            ["columns"] = config.Columns,
            ["showLabels"] = config.Settings.ShowLabels,
            ["lang"] = L.Code(L.Current),
            ["activeProfile"] = config.ActiveProfile.Id,
            ["profiles"] = new JsonArray(config.Profiles
                .Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name })
                .ToArray()),
            ["buttons"] = buttons,
        }.ToJsonString();
    });

    /// <summary>Phones don't have the Segoe icon font, so buttons without an icon get an emoji.</summary>
    internal static string Emoji(DeckAction? action) => action switch
    {
        OpenAction o when o.IsUrl => "🌐",
        OpenAction o when o.Target.TrimEnd().EndsWith('\\') || o.Target.TrimEnd().EndsWith('/') => "📁",
        OpenAction => "🚀",
        KeysAction => "⌨️",
        MediaAction m => m.Command switch
        {
            MediaCommand.PlayPause => "⏯️",
            MediaCommand.Next => "⏭️",
            MediaCommand.Prev => "⏮️",
            MediaCommand.Stop => "⏹️",
            MediaCommand.VolumeUp => "🔊",
            MediaCommand.VolumeDown => "🔉",
            MediaCommand.Mute => "🔇",
            _ => "🎵",
        },
        ProfileAction => "📂",
        ObsAction o => o.Command switch
        {
            ObsCommand.SwitchScene => "🎬",
            ObsCommand.ToggleRecord => "⏺️",
            ObsCommand.ToggleStream => "📡",
            ObsCommand.ToggleMute => "🎙️",
            ObsCommand.ToggleSource => "👁️",
            ObsCommand.SaveReplay => "💾",
            _ => "🎥",
        },
        _ => "▫️",
    };

    public void Dispose() => Stop();
}
