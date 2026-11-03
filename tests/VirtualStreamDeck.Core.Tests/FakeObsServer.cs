using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VirtualStreamDeck.Core.Obs;

/// <summary>
/// Tiny obs-websocket v5 server for tests: does the HTTP upgrade by hand, then speaks the
/// Hello / Identify / Request protocol. Behaves like OBS for auth failures (close code 4009).
/// </summary>
sealed class FakeObsServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();

    public FakeObsServer(string password)
    {
        Password = password;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public string Password { get; }
    public int Connections;
    public ConcurrentQueue<(string Type, JsonObject? Data)> Requests { get; } = new();

    /// <summary>Returns (responseData, null) on success or (null, (code, comment)) on failure.</summary>
    public Func<string, JsonObject?, (JsonObject? Data, (int Code, string Comment)? Error)> Handler { get; set; } =
        (_, _) => (null, null);

    /// <summary>Simulates OBS crashing: drops every open connection.</summary>
    public void DropAll()
    {
        foreach (var c in _clients) c.Close();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _clients.Add(client);
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var key = await ReadUpgradeRequest(stream);
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: obswebsocket.json\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));

            using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, SubProtocol = "obswebsocket.json" });
            Interlocked.Increment(ref Connections);

            string salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await Send(ws, new JsonObject
            {
                ["op"] = 0,
                ["d"] = new JsonObject
                {
                    ["obsWebSocketVersion"] = "5.5.2",
                    ["rpcVersion"] = 1,
                    ["authentication"] = new JsonObject { ["challenge"] = challenge, ["salt"] = salt },
                },
            });

            var identify = await Receive(ws);
            var auth = identify?["d"]?["authentication"]?.GetValue<string>();
            if (auth != ObsClient.ComputeAuthentication(Password, salt, challenge))
            {
                await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", CancellationToken.None);
                return;
            }
            await Send(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });

            while (ws.State == WebSocketState.Open)
            {
                var msg = await Receive(ws);
                if (msg == null) return;
                var d = msg["d"]!.AsObject();
                var type = d["requestType"]!.GetValue<string>();
                var data = d["requestData"]?.AsObject();
                Requests.Enqueue((type, data == null ? null : JsonNode.Parse(data.ToJsonString())!.AsObject()));

                var (result, error) = Handler(type, data);
                var status = new JsonObject { ["result"] = error == null, ["code"] = error?.Code ?? 100 };
                if (error != null) status["comment"] = error.Value.Comment;
                var reply = new JsonObject
                {
                    ["requestType"] = type,
                    ["requestId"] = d["requestId"]!.GetValue<string>(),
                    ["requestStatus"] = status,
                };
                if (result != null) reply["responseData"] = result;
                await Send(ws, new JsonObject { ["op"] = 7, ["d"] = reply });
            }
        }
        catch
        {
            // connection dropped
        }
    }

    private static async Task<string> ReadUpgradeRequest(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("closed");
            sb.Append((char)one[0]);
        }
        foreach (var line in sb.ToString().Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                return line["Sec-WebSocket-Key:".Length..].Trim();
        }
        throw new IOException("no key");
    }

    private static Task Send(WebSocket ws, JsonObject message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonNode?> Receive(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        DropAll();
    }
}
