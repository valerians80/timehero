using System.Security.Cryptography;
using System.Text;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Impostazioni salvate nella tabella Settings del database.</summary>
public sealed class AppSettings
{
    private readonly TimeStore _store;
    public AppSettings(TimeStore store) => _store = store;

    /// <summary>Ogni quanti minuti ricordare l'attività in corso (0 = mai).</summary>
    public int ReminderMinutes { get => Get("reminderMinutes", 5); set => Set("reminderMinutes", value); }

    /// <summary>Dopo quanti minuti di inattività chiedere se l'attività è ancora valida (0 = mai).</summary>
    public int IdleMinutes { get => Get("idleMinutes", 10); set => Set("idleMinutes", value); }

    /// <summary>Arrotondamento predefinito dello storico, in minuti (0 = nessuno).</summary>
    public int RoundMinutes { get => Get("roundMinutes", 15); set => Set("roundMinutes", value); }

    // ---------- registrazione audio con OBS ----------
    public bool ObsEnabled { get => GetBool("obsEnabled", false); set => SetBool("obsEnabled", value); }
    public string ObsHost { get => _store.GetSetting("obsHost") ?? "localhost"; set => _store.SetSetting("obsHost", value); }
    public int ObsPort { get => Get("obsPort", 4455); set => Set("obsPort", value); }

    /// <summary>Percorso di obs64.exe; vuoto = cerca nelle cartelle di installazione standard.</summary>
    public string ObsExePath { get => _store.GetSetting("obsExe") ?? ""; set => _store.SetSetting("obsExe", value); }

    /// <summary>Profilo e raccolta di scene da usare per registrare (vuoto = quelli attuali di OBS).</summary>
    public string ObsProfile { get => _store.GetSetting("obsProfile") ?? ""; set => _store.SetSetting("obsProfile", value); }
    public string ObsSceneCollection { get => _store.GetSetting("obsCollection") ?? ""; set => _store.SetSetting("obsCollection", value); }

    /// <summary>
    /// Una registrazione in pausa (attività messa in pausa e non ripresa) si chiude da sola dopo N minuti,
    /// così il file viene salvato e trascritto. 0 = mai.
    /// </summary>
    public int ObsPauseMinutes { get => Get("obsPauseMinutes", 30); set => Set("obsPauseMinutes", value); }

    /// <summary>Avvia OBS insieme a TimeHero, così è già pronto quando inizia la chiamata.</summary>
    public bool ObsKeepReady { get => GetBool("obsKeepReady", false); set => SetBool("obsKeepReady", value); }

    /// <summary>Password del WebSocket, salvata cifrata con DPAPI (leggibile solo dal tuo utente su questo PC).</summary>
    public string ObsPassword { get => Secret("obsPassword"); set => SetSecret("obsPassword", value); }

    // ---------- trascrizione con Azure OpenAI ----------
    /// <summary>"openai" (Whisper / GPT) oppure "speech" (Azure AI Speech, riconosce gli interlocutori).</summary>
    public string AzureProvider { get => _store.GetSetting("azProvider") ?? "openai"; set => _store.SetSetting("azProvider", value); }
    public string SpeechApiVersion { get => _store.GetSetting("speechApiVersion") ?? "2024-11-15"; set => _store.SetSetting("speechApiVersion", value); }
    public int SpeechMaxSpeakers { get => Get("speechMaxSpeakers", 4); set => Set("speechMaxSpeakers", value); }

    public string AzureEndpoint { get => _store.GetSetting("azEndpoint") ?? ""; set => _store.SetSetting("azEndpoint", value); }
    public string AzureDeployment { get => _store.GetSetting("azDeployment") ?? ""; set => _store.SetSetting("azDeployment", value); }
    public string AzureApiVersion { get => _store.GetSetting("azApiVersion") ?? "2025-03-01-preview"; set => _store.SetSetting("azApiVersion", value); }
    public string AzureLanguage { get => _store.GetSetting("azLanguage") ?? "it"; set => _store.SetSetting("azLanguage", value); }

    /// <summary>"key" (chiave API) oppure "entra" (accesso con l'account Microsoft).</summary>
    public string AzureAuth { get => _store.GetSetting("azAuth") ?? "key"; set => _store.SetSetting("azAuth", value); }
    public string AzureTenant { get => _store.GetSetting("azTenant") ?? ""; set => _store.SetSetting("azTenant", value); }
    public string AzureKey { get => Secret("azKey"); set => SetSecret("azKey", value); }

    /// <summary>Trascrivi da sola ogni registrazione appena finita.</summary>
    public bool AutoTranscribe { get => GetBool("autoTranscribe", true); set => SetBool("autoTranscribe", value); }

    /// <summary>Record di autenticazione Entra serializzato: evita di rifare l'accesso a ogni avvio.</summary>
    public string AzureAuthRecord { get => _store.GetSetting("azAuthRecord") ?? ""; set => _store.SetSetting("azAuthRecord", value); }

    private string Secret(string key)
    {
        var raw = _store.GetSetting(key);
        if (string.IsNullOrEmpty(raw)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(raw), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    private void SetSecret(string key, string value) => _store.SetSetting(key, string.IsNullOrEmpty(value)
        ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));

    private int Get(string key, int def) => int.TryParse(_store.GetSetting(key), out var v) ? v : def;
    private void Set(string key, int value) => _store.SetSetting(key, value.ToString());
    private bool GetBool(string key, bool def) => _store.GetSetting(key) is { } s ? s == "1" : def;
    private void SetBool(string key, bool value) => _store.SetSetting(key, value ? "1" : "0");
}
