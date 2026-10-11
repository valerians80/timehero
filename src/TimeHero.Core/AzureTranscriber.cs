using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TimeHero.Core;

public enum TranscriptionProvider
{
    /// <summary>Azure OpenAI: Whisper / gpt-4o-transcribe (solo testo, max 25 MB).</summary>
    OpenAi,

    /// <summary>Azure AI Speech, trascrizione veloce: riconosce gli interlocutori, file fino a ~300 MB.</summary>
    Speech,
}

/// <param name="Endpoint">Es. https://miarisorsa.openai.azure.com</param>
/// <param name="Deployment">Nome del deployment (Whisper o gpt-4o-transcribe).</param>
/// <param name="ApiKey">Chiave della risorsa; se vuota si usa <paramref name="TokenProvider"/> (Microsoft Entra ID).</param>
public record TranscriptionOptions(
    string Endpoint,
    string Deployment,
    string ApiVersion = "2025-03-01-preview",
    string? ApiKey = null,
    Func<CancellationToken, Task<string>>? TokenProvider = null,
    string? Language = "it",
    TranscriptionProvider Provider = TranscriptionProvider.OpenAi,
    int MaxSpeakers = 4)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        (Provider == TranscriptionProvider.Speech || !string.IsNullOrWhiteSpace(Deployment)) &&
        (!string.IsNullOrEmpty(ApiKey) || TokenProvider is not null);

    /// <summary>Nome da mostrare/salvare come "modello" della trascrizione.</summary>
    public string ModelLabel => Provider == TranscriptionProvider.Speech ? "azure-ai-speech" : Deployment;

    public long MaxBytes => Provider == TranscriptionProvider.Speech
        ? AzureTranscriber.SpeechMaxBytes
        : AzureTranscriber.MaxBytes;
}

public sealed class TranscriptionException : Exception
{
    public HttpStatusCode? Status { get; }
    public TranscriptionException(string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner) => Status = status;
}

/// <summary>Client dell'API di trascrizione di Azure OpenAI (POST .../audio/transcriptions).</summary>
public sealed class AzureTranscriber
{
    /// <summary>Formati accettati dall'API (mkv no: in OBS scegli mp4).</summary>
    public static readonly string[] SupportedExtensions =
        { ".flac", ".m4a", ".mp3", ".mp4", ".mpeg", ".mpga", ".oga", ".ogg", ".wav", ".webm" };

    public const long MaxBytes = 25L * 1024 * 1024;
    public const long SpeechMaxBytes = 300L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _retryDelayCap;

    public AzureTranscriber(HttpClient? http = null, TimeSpan? retryDelayCap = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _retryDelayCap = retryDelayCap ?? TimeSpan.FromSeconds(60);
    }

    public static string BuildUrl(TranscriptionOptions o) => o.Provider == TranscriptionProvider.Speech
        ? $"{o.Endpoint.Trim().TrimEnd('/')}/speechtotext/transcriptions:transcribe?api-version={Uri.EscapeDataString(o.ApiVersion)}"
        : $"{o.Endpoint.Trim().TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(o.Deployment.Trim())}" +
          $"/audio/transcriptions?api-version={Uri.EscapeDataString(o.ApiVersion)}";

    /// <summary>"it" → "it-IT", "it,en" → ["it-IT","en-US"]; valori già completi (it-IT) restano come sono.</summary>
    public static string[] SpeechLocales(string? language)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["it"] = "it-IT", ["en"] = "en-US", ["fr"] = "fr-FR", ["de"] = "de-DE", ["es"] = "es-ES",
            ["pt"] = "pt-PT", ["nl"] = "nl-NL",
        };
        return (language ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Contains('-') ? l : map.GetValueOrDefault(l, l))
            .ToArray();
    }

    /// <summary>Invia il file e restituisce il testo. Riprova da sola su 429 e errori temporanei del server.</summary>
    public async Task<string> TranscribeAsync(string filePath, TranscriptionOptions o, string? prompt = null,
        CancellationToken ct = default)
    {
        if (!o.IsConfigured) throw new TranscriptionException("Trascrizione non configurata (endpoint, deployment, chiave).");

        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                using var req = await BuildRequestAsync(filePath, o, prompt, ct);
                resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                    return o.Provider == TranscriptionProvider.Speech ? SpeechTranscript.Format(body) : ParseText(body);

                var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                if (retryable && attempt < attempts)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                    await Task.Delay(wait > _retryDelayCap ? _retryDelayCap : wait, ct);
                    continue;
                }
                throw Describe(resp.StatusCode, body);
            }
            catch (HttpRequestException ex) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, _retryDelayCap.TotalSeconds)), ct);
                _ = ex;
            }
            catch (HttpRequestException ex)
            {
                throw new TranscriptionException($"Servizio Azure non raggiungibile: {ex.Message}", null, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TranscriptionException("Il servizio Azure non ha risposto in tempo.", null, ex);
            }
            finally
            {
                resp?.Dispose();
            }
        }
    }

    private static async Task<HttpRequestMessage> BuildRequestAsync(string filePath, TranscriptionOptions o,
        string? prompt, CancellationToken ct)
    {
        var form = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(filePath));
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(filePath));
        var speech = o.Provider == TranscriptionProvider.Speech;

        if (speech)
        {
            form.Add(file, "audio", Path.GetFileName(filePath));
            var def = new Dictionary<string, object>
            {
                ["diarization"] = new Dictionary<string, object> { ["enabled"] = true, ["maxSpeakers"] = Math.Clamp(o.MaxSpeakers, 2, 35) },
            };
            var locales = SpeechLocales(o.Language);
            if (locales.Length > 0) def["locales"] = locales;
            form.Add(new StringContent(JsonSerializer.Serialize(def), System.Text.Encoding.UTF8, "application/json"), "definition");
        }
        else
        {
            form.Add(file, "file", Path.GetFileName(filePath));
            form.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrWhiteSpace(o.Language)) form.Add(new StringContent(o.Language.Trim()), "language");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        }

        var req = new HttpRequestMessage(HttpMethod.Post, BuildUrl(o)) { Content = form };
        if (!string.IsNullOrEmpty(o.ApiKey))
        {
            if (speech) req.Headers.Add("Ocp-Apim-Subscription-Key", o.ApiKey);
            else req.Headers.Add("api-key", o.ApiKey);
        }
        else
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await o.TokenProvider!(ct));
        }
        return req;
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" or ".mpga" or ".mpeg" => "audio/mpeg",
        ".m4a" or ".mp4" => "audio/mp4",
        ".wav" => "audio/wav",
        ".webm" => "audio/webm",
        ".ogg" or ".oga" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => "application/octet-stream",
    };

    private static string ParseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return body.Trim(); // response_format "text"
        }
    }

    private static TranscriptionException Describe(HttpStatusCode status, string body)
    {
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                detail = m.GetString();
        }
        catch (JsonException)
        {
            // corpo non JSON
        }

        var msg = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Accesso negato ad Azure: controlla la chiave oppure, con Entra ID, che il tuo utente abbia il ruolo " +
                "\"Cognitive Services OpenAI User\" (OpenAI) o \"Cognitive Services Speech User\" (Speech) sulla risorsa.",
            HttpStatusCode.NotFound => "Servizio o deployment non trovato: controlla endpoint e nome del deployment.",
            HttpStatusCode.UnsupportedMediaType =>
                "Formato audio non accettato dal servizio: prova mp3, wav o ogg (in OBS: registrazione audio con FFmpeg personalizzato).",
            HttpStatusCode.RequestEntityTooLarge => "File troppo grande per il servizio (massimo 25 MB).",
            HttpStatusCode.TooManyRequests => "Troppe richieste ad Azure (limite di quota): riprova più tardi.",
            _ => $"Azure ha risposto {(int)status}.",
        };
        return new TranscriptionException(detail is null ? msg : $"{msg} Dettaglio: {detail}", status);
    }
}
