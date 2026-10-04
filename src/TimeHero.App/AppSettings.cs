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

    private int Get(string key, int def) => int.TryParse(_store.GetSetting(key), out var v) ? v : def;
    private void Set(string key, int value) => _store.SetSetting(key, value.ToString());
}
