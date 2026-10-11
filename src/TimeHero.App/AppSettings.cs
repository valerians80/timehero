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

    /// <summary>Avvia OBS insieme a TimeHero, così è già pronto quando inizia la chiamata.</summary>
    public bool ObsKeepReady { get => GetBool("obsKeepReady", false); set => SetBool("obsKeepReady", value); }

    /// <summary>Password del WebSocket, salvata cifrata con DPAPI (leggibile solo dal tuo utente su questo PC).</summary>
    public string ObsPassword
    {
        get
        {
            var raw = _store.GetSetting("obsPassword");
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
        set => _store.SetSetting("obsPassword", string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));
    }

    private int Get(string key, int def) => int.TryParse(_store.GetSetting(key), out var v) ? v : def;
    private void Set(string key, int value) => _store.SetSetting(key, value.ToString());
    private bool GetBool(string key, bool def) => _store.GetSetting(key) is { } s ? s == "1" : def;
    private void SetBool(string key, bool value) => _store.SetSetting(key, value ? "1" : "0");
}
