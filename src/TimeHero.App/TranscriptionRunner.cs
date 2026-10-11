using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Azure.Core;
using Azure.Identity;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>
/// Coda di trascrizione in background: una registrazione alla volta, senza bloccare l'interfaccia.
/// Usa una propria connessione al database (SQLite non è fatto per un uso concorrente della stessa connessione).
/// </summary>
public sealed class TranscriptionRunner
{
    private const string Scope = "https://cognitiveservices.azure.com/.default";

    private readonly AppSettings _settings;
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>();
    private readonly object _lock = new();
    private readonly Dictionary<long, long> _pending = new(); // allegato → attività
    private InteractiveBrowserCredential? _credential;

    public TranscriptionRunner(AppSettings settings)
    {
        _settings = settings;
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Cambia lo stato (in coda, finita, fallita). Può arrivare da un thread qualsiasi.</summary>
    public event Action? Changed;

    public bool IsConfigured => BuildOptions().IsConfigured;

    public bool IsBusyFor(long activityId)
    {
        lock (_lock) return _pending.Values.Contains(activityId);
    }

    public bool IsPending(long attachmentId)
    {
        lock (_lock) return _pending.ContainsKey(attachmentId);
    }

    public void Enqueue(long attachmentId, long activityId)
    {
        lock (_lock)
        {
            if (_pending.ContainsKey(attachmentId)) return;
            _pending[attachmentId] = activityId;
        }
        _queue.Writer.TryWrite(attachmentId);
        Changed?.Invoke();
    }

    public TranscriptionOptions BuildOptions()
    {
        var (endpoint, deployment, apiVersion) = AzureEndpoint.Normalize(
            _settings.AzureEndpoint, _settings.AzureDeployment, _settings.AzureApiVersion);
        return new TranscriptionOptions(
            endpoint, deployment, apiVersion,
            _settings.AzureAuth == "key" ? _settings.AzureKey : null,
            _settings.AzureAuth == "entra" ? GetTokenAsync : null,
            string.IsNullOrWhiteSpace(_settings.AzureLanguage) ? null : _settings.AzureLanguage.Trim());
    }

    private async Task LoopAsync()
    {
        await foreach (var attachmentId in _queue.Reader.ReadAllAsync())
        {
            try
            {
                var options = BuildOptions();
                using var store = TimeStore.OpenFile();
                if (!options.IsConfigured)
                {
                    var att = store.GetAttachment(attachmentId);
                    if (att is not null)
                        store.SaveTranscript(att.Id, att.ActivityId, options.Deployment, null, "error",
                            "Trascrizione non configurata: apri ⚙ → Trascrizione.");
                    continue;
                }

                var t = await new TranscriptionService(store).TranscribeAsync(attachmentId, options);
                var title = store.GetActivity(t.ActivityId)?.Title ?? "attività";
                try
                {
                    if (t.Status == "done") Notifier.Info("Trascrizione pronta", title);
                    else Notifier.Info("Trascrizione non riuscita", $"{title}: {t.Error}");
                }
                catch
                {
                    // la notifica non è essenziale
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"TimeHero: trascrizione fallita: {ex}");
                try { Notifier.Info("Trascrizione non riuscita", ex.Message); } catch { /* ok */ }
            }
            finally
            {
                lock (_lock) _pending.Remove(attachmentId);
                Changed?.Invoke();
            }
        }
    }

    // ---------- accesso con Microsoft Entra ID ----------
    /// <summary>
    /// Token per Azure OpenAI con l'account Microsoft dell'utente. Il primo accesso apre il browser;
    /// poi il record di autenticazione salvato evita di chiedere di nuovo le credenziali.
    /// </summary>
    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_credential is null)
        {
            var opts = new InteractiveBrowserCredentialOptions
            {
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "TimeHero" },
            };
            if (!string.IsNullOrWhiteSpace(_settings.AzureTenant)) opts.TenantId = _settings.AzureTenant.Trim();

            var saved = _settings.AzureAuthRecord;
            if (!string.IsNullOrEmpty(saved))
            {
                try
                {
                    using var ms = new MemoryStream(Convert.FromBase64String(saved));
                    opts.AuthenticationRecord = await AuthenticationRecord.DeserializeAsync(ms, ct);
                }
                catch
                {
                    // record non valido: nuovo accesso
                }
            }
            _credential = new InteractiveBrowserCredential(opts);

            if (opts.AuthenticationRecord is null)
            {
                var record = await _credential.AuthenticateAsync(new TokenRequestContext(new[] { Scope }), ct);
                using var ms = new MemoryStream();
                await record.SerializeAsync(ms, ct);
                _settings.AzureAuthRecord = Convert.ToBase64String(ms.ToArray());
            }
        }
        var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);
        return token.Token;
    }

    /// <summary>Dimenticare l'account (dopo un cambio di tenant o di utente).</summary>
    public void ResetSignIn()
    {
        _credential = null;
        _settings.AzureAuthRecord = "";
    }
}
