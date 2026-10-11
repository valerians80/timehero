using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TimeHero.Core;

public class ObsException : Exception
{
    public ObsException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Password non accettata da OBS (non ha senso riprovare).</summary>
public sealed class ObsAuthException : ObsException
{
    public ObsAuthException(string? message = null) : base(message ?? "Password del WebSocket di OBS errata.") { }
}

/// <summary>Una connessione obs-websocket v5: si apre, si fanno le richieste, si chiude.</summary>
public sealed class ObsSession : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;

    private ObsSession(ClientWebSocket ws) => _ws = ws;

    public static async Task<ObsSession> ConnectAsync(string host, int port, string? password, TimeSpan? timeout = null)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("obswebsocket.json");
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var stage = "connessione";
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{host}:{port}"), cts.Token);

            stage = "handshake";
            var (op, hello) = ObsProtocol.ParseMessage(await ReceiveAsync(ws, cts.Token));
            if (op != ObsProtocol.OpHello) throw new ObsException("Risposta inattesa da OBS (non è obs-websocket v5?).");
            var needsAuth = ObsProtocol.RequiresAuth(hello);
            if (needsAuth && string.IsNullOrEmpty(password))
                throw new ObsAuthException("OBS richiede una password per il WebSocket: inseriscila nelle impostazioni di TimeHero.");

            await SendAsync(ws, ObsProtocol.BuildIdentify(hello, password), cts.Token);

            stage = "autenticazione";
            string reply;
            try
            {
                reply = await ReceiveAsync(ws, cts.Token);
            }
            catch (Exception) when (needsAuth)
            {
                throw new ObsAuthException(); // OBS chiude la connessione (codice 4009) se la password è errata
            }
            var (op2, _) = ObsProtocol.ParseMessage(reply);
            if (op2 != ObsProtocol.OpIdentified) throw new ObsException("OBS non ha confermato l'identificazione.");
            return new ObsSession(ws);
        }
        catch (ObsException)
        {
            ws.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            ws.Dispose();
            throw new ObsException(
                ex is OperationCanceledException ? $"OBS non risponde ({stage}, timeout)." : $"OBS non raggiungibile ({stage}): {ex.Message}", ex);
        }
    }

    /// <summary>Invia una richiesta e restituisce i dati della risposta (o solleva ObsException se OBS la rifiuta).</summary>
    public async Task<JsonElement?> RequestAsync(string requestType, object? requestData = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var id = Guid.NewGuid().ToString("N");
        await SendAsync(_ws, ObsProtocol.BuildRequest(requestType, id, requestData), cts.Token);
        while (true)
        {
            var (op, d) = ObsProtocol.ParseMessage(await ReceiveAsync(_ws, cts.Token));
            if (op != ObsProtocol.OpRequestResponse) continue;
            var r = ObsProtocol.ParseResponse(d);
            if (r.RequestId != id) continue;
            if (!r.Ok) throw new ObsException($"OBS ha rifiutato {requestType}: {r.Comment ?? "errore"} (codice {r.Code}).");
            return r.Data;
        }
    }

    private static Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new ObsException($"OBS ha chiuso la connessione ({(int?)r.CloseStatus}).");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        }
        catch
        {
            // chiusura best-effort
        }
        _ws.Dispose();
    }
}
