using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TimeHero.Core;

/// <summary>Risposta a una richiesta obs-websocket v5 (op 7).</summary>
public record ObsResponse(string RequestId, bool Ok, int Code, string? Comment, JsonElement? Data);

/// <summary>
/// Pezzi puri del protocollo obs-websocket v5 (OBS 28+): autenticazione e formato dei messaggi.
/// Il trasporto (WebSocket) sta nell'app; qui c'è ciò che si può testare ovunque.
/// </summary>
public static class ObsProtocol
{
    public const int OpHello = 0, OpIdentify = 1, OpIdentified = 2, OpRequest = 6, OpRequestResponse = 7;

    /// <summary>Stringa di autenticazione: base64(sha256(base64(sha256(password+salt)) + challenge)).</summary>
    public static string ComputeAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Messaggio Identify in risposta all'Hello; aggiunge l'autenticazione solo se OBS la richiede.</summary>
    public static string BuildIdentify(JsonElement helloData, string? password)
    {
        var data = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        if (helloData.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            var salt = auth.GetProperty("salt").GetString() ?? "";
            var challenge = auth.GetProperty("challenge").GetString() ?? "";
            data["authentication"] = ComputeAuth(password ?? "", salt, challenge);
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["op"] = OpIdentify, ["d"] = data });
    }

    public static bool RequiresAuth(JsonElement helloData) =>
        helloData.TryGetProperty("authentication", out var a) && a.ValueKind == JsonValueKind.Object;

    public static string BuildRequest(string requestType, string requestId, object? requestData = null)
    {
        var d = new Dictionary<string, object> { ["requestType"] = requestType, ["requestId"] = requestId };
        if (requestData is not null) d["requestData"] = requestData;
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["op"] = OpRequest, ["d"] = d });
    }

    /// <summary>Estrae (op, d) da un messaggio ricevuto.</summary>
    public static (int Op, JsonElement Data) ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Clone: il JsonDocument viene eliminato qui, l'elemento deve sopravvivere
        return (root.GetProperty("op").GetInt32(), root.GetProperty("d").Clone());
    }

    public static ObsResponse ParseResponse(JsonElement d)
    {
        var status = d.GetProperty("requestStatus");
        return new ObsResponse(
            d.GetProperty("requestId").GetString() ?? "",
            status.GetProperty("result").GetBoolean(),
            status.TryGetProperty("code", out var c) ? c.GetInt32() : 0,
            status.TryGetProperty("comment", out var cm) ? cm.GetString() : null,
            d.TryGetProperty("responseData", out var rd) ? rd.Clone() : null);
    }
}
