using System.Net;
using System.Text;

namespace TimeHero.Core.Tests;

/// <summary>Un finto endpoint Azure OpenAI per l'API di trascrizione.</summary>
public sealed class FakeAzureServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _lock = new();
    private int _requests;

    public int Port { get; }
    public string Endpoint => $"http://localhost:{Port}";

    public string? RequiredApiKey { get; set; }
    public string? RequiredBearer { get; set; }
    public string ResponseText { get; set; } = "Buongiorno, iniziamo la riunione.";

    /// <summary>Le prime N richieste ricevono questo stato (es. 429), poi 200.</summary>
    public int FailFirst { get; set; }
    public int FailStatus { get; set; } = 429;
    public string? FailBody { get; set; }

    /// <summary>Se valorizzato, risponde sempre con questo stato.</summary>
    public int? AlwaysStatus { get; set; }
    public string? AlwaysBody { get; set; }

    public int Requests { get { lock (_lock) return _requests; } }
    public string? LastPath { get; private set; }
    public string? LastQuery { get; private set; }
    public string? LastApiKeyHeader { get; private set; }
    public string? LastSubscriptionKey { get; private set; }

    /// <summary>Risposta della trascrizione veloce di Azure AI Speech (due interlocutori).</summary>
    public string SpeechJson { get; set; } = """
        {"durationMilliseconds":9000,
         "combinedPhrases":[{"channel":0,"text":"Buongiorno iniziamo. Ciao ho preparato il piano. Perfetto."}],
         "phrases":[
           {"channel":0,"speaker":1,"offsetMilliseconds":0,"durationMilliseconds":2000,"text":"Buongiorno, iniziamo.","locale":"it-IT"},
           {"channel":0,"speaker":2,"offsetMilliseconds":4000,"durationMilliseconds":3000,"text":"Ciao, ho preparato il piano.","locale":"it-IT"},
           {"channel":0,"speaker":2,"offsetMilliseconds":7500,"durationMilliseconds":1000,"text":"Lo condivido.","locale":"it-IT"},
           {"channel":0,"speaker":1,"offsetMilliseconds":8800,"durationMilliseconds":1000,"text":"Perfetto.","locale":"it-IT"}]}
        """;
    public string? LastAuthorization { get; private set; }
    public string? LastBodyText { get; private set; }   // corpo multipart letto come testo (per cercare campi e nome file)
    public int LastBodyLength { get; private set; }

    public FakeAzureServer()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        int n;
        lock (_lock) n = ++_requests;
        using var ms = new MemoryStream();
        ctx.Request.InputStream.CopyTo(ms);
        LastBodyLength = (int)ms.Length;
        LastBodyText = Encoding.Latin1.GetString(ms.ToArray());
        LastPath = ctx.Request.Url!.AbsolutePath;
        LastQuery = ctx.Request.Url.Query;
        LastApiKeyHeader = ctx.Request.Headers["api-key"];
        LastSubscriptionKey = ctx.Request.Headers["Ocp-Apim-Subscription-Key"];
        var isSpeech = LastPath.Contains("speechtotext", StringComparison.OrdinalIgnoreCase);
        var givenKey = isSpeech ? LastSubscriptionKey : LastApiKeyHeader;
        LastAuthorization = ctx.Request.Headers["Authorization"];

        int status = 200;
        string body = isSpeech
            ? SpeechJson
            : $"{{\"text\":{System.Text.Json.JsonSerializer.Serialize(ResponseText)}}}";

        if (AlwaysStatus is { } s) { status = s; body = AlwaysBody ?? "{}"; }
        else if (n <= FailFirst) { status = FailStatus; body = FailBody ?? "{}"; ctx.Response.AddHeader("Retry-After", "0"); }
        else if (RequiredApiKey is not null && givenKey != RequiredApiKey) { status = 401; body = "{\"error\":{\"message\":\"Access denied due to invalid subscription key.\"}}"; }
        else if (RequiredBearer is not null && LastAuthorization != "Bearer " + RequiredBearer) { status = 401; body = "{}"; }

        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* già chiuso */ }
    }
}
