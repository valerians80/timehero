using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TimeHero.Core;

namespace TimeHero.Core.Tests;

/// <summary>Un finto OBS (obs-websocket v5) per provare client e registratore senza OBS.</summary>
public sealed class FakeObsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    public int Port { get; }
    public string? Password { get; set; }

    public bool Recording { get; private set; }
    public bool Paused { get; private set; }

    /// <summary>Se true, PauseRecord viene rifiutato (come con "stesso encoder dello streaming").</summary>
    public bool RejectPause { get; set; }

    /// <summary>Simula l'utente che chiude la registrazione dall'interfaccia di OBS.</summary>
    public void StopFromUi() { Recording = false; Paused = false; }
    public string CurrentProfile { get; private set; } = "Predefinito";
    public string CurrentCollection { get; private set; } = "Senza titolo";
    public string[] Profiles { get; set; } = { "Predefinito", "TimeHero" };
    public string[] Collections { get; set; } = { "Senza titolo", "TimeHero" };
    public string NextOutputPath { get; set; } = @"C:\Rec\chiamata.mkv";

    /// <summary>Le prime N richieste ricevono "OBS is not ready" (codice 207), come subito dopo l'avvio di OBS.</summary>
    public int NotReadyRequests { get; set; }

    /// <summary>Se valorizzato, StartRecord viene rifiutato con questo messaggio.</summary>
    public string? RejectStart { get; set; }

    private readonly List<string> _calls = new();
    public List<string> Calls { get { lock (_lock) return _calls.ToList(); } }

    public FakeObsServer()
    {
        // porta libera
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private async Task Serve(HttpListenerContext ctx)
    {
        WebSocket ws;
        try { ws = (await ctx.AcceptWebSocketAsync("obswebsocket.json")).WebSocket; }
        catch { return; }

        try
        {
            // Hello
            var hello = new Dictionary<string, object> { ["obsWebSocketVersion"] = "5.5.0", ["rpcVersion"] = 1 };
            const string salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
            const string challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";
            if (Password is not null) hello["authentication"] = new { challenge, salt };
            await Send(ws, new { op = 0, d = hello });

            // Identify
            var (op, d) = ObsProtocol.ParseMessage(await Receive(ws));
            if (op != 1) return;
            if (Password is not null)
            {
                var given = d.TryGetProperty("authentication", out var a) ? a.GetString() : null;
                if (given != ObsProtocol.ComputeAuth(Password, salt, challenge))
                {
                    await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", CancellationToken.None);
                    return;
                }
            }
            await Send(ws, new { op = 2, d = new { negotiatedRpcVersion = 1 } });

            while (ws.State == WebSocketState.Open)
            {
                var text = await Receive(ws);
                var (rop, rd) = ObsProtocol.ParseMessage(text);
                if (rop != 6) continue;
                var type = rd.GetProperty("requestType").GetString()!;
                var id = rd.GetProperty("requestId").GetString()!;
                var data = rd.TryGetProperty("requestData", out var x) ? x : (JsonElement?)null;
                lock (_lock) _calls.Add(type + (data is { } dd ? ":" + dd.GetRawText() : ""));
                var (ok, code, comment, response) = Handle(type, data);
                await Send(ws, new
                {
                    op = 7,
                    d = new { requestType = type, requestId = id, requestStatus = new { result = ok, code, comment }, responseData = response },
                });
            }
        }
        catch
        {
            // connessione chiusa dal client
        }
        finally
        {
            ws.Dispose();
        }
    }

    private (bool Ok, int Code, string? Comment, object? Data) Handle(string type, JsonElement? data)
    {
        lock (_lock)
        {
            if (NotReadyRequests > 0)
            {
                NotReadyRequests--;
                return (false, 207, "OBS is not ready to perform the request.", null);
            }
        }
        switch (type)
        {
            case "GetVersion": return (true, 100, null, new { obsVersion = "30.2.3", obsWebSocketVersion = "5.5.0" });
            case "GetRecordStatus": return (true, 100, null, new { outputActive = Recording, outputPaused = Paused });
            case "PauseRecord":
                if (!Recording) return (false, 501, "Output not active", null);
                if (RejectPause) return (false, 703, "Pausing is not supported by this encoder", null);
                Paused = true;
                return (true, 100, null, null);
            case "ResumeRecord":
                if (!Paused) return (false, 501, "Output not paused", null);
                Paused = false;
                return (true, 100, null, null);
            case "StartRecord":
                if (RejectStart is not null) return (false, 500, RejectStart, null);
                Recording = true;
                return (true, 100, null, null);
            case "StopRecord":
                if (!Recording) return (false, 501, "Output not active", null);
                Recording = false;
                Paused = false;
                return (true, 100, null, new { outputPath = NextOutputPath });
            case "GetProfileList": return (true, 100, null, new { currentProfileName = CurrentProfile, profiles = Profiles });
            case "SetCurrentProfile":
                CurrentProfile = data!.Value.GetProperty("profileName").GetString()!;
                return (true, 100, null, null);
            case "GetSceneCollectionList":
                return (true, 100, null, new { currentSceneCollectionName = CurrentCollection, sceneCollections = Collections });
            case "SetCurrentSceneCollection":
                CurrentCollection = data!.Value.GetProperty("sceneCollectionName").GetString()!;
                return (true, 100, null, null);
            default: return (false, 204, "Unknown request type", null);
        }
    }

    private static Task Send(WebSocket ws, object message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> Receive(WebSocket ws)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) throw new OperationCanceledException();
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* già chiuso */ }
    }
}
