namespace TimeHero.Core;

/// <summary>Logica "avvia / termina": una sola attività in corso alla volta.</summary>
public sealed class ActivityTracker
{
    private readonly TimeStore _store;
    private readonly Func<DateTime> _clock;

    public ActivityTracker(TimeStore store, Func<DateTime>? utcClock = null)
    {
        _store = store;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        Current = store.GetRunningActivity();
    }

    public Activity? Current { get; private set; }

    /// <summary>Rilegge l'attività in corso dal database (dopo modifiche fatte altrove, es. nello storico).</summary>
    public void Reload()
    {
        Current = _store.GetRunningActivity();
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>Avvia una nuova attività; se ce n'è una in corso la chiude nello stesso istante.</summary>
    public Activity Start(long categoryId, long? clientId = null, IEnumerable<long>? personIds = null, string? notes = null)
    {
        var now = _clock();
        if (Current is not null) Stop(now);

        var a = new Activity
        {
            CategoryId = categoryId,
            ClientId = clientId,
            StartUtc = now,
            Notes = notes,
            PersonIds = personIds?.ToList() ?? new(),
        };
        _store.InsertActivity(a);
        Current = a;
        Changed?.Invoke();
        return a;
    }

    public Activity? Stop(DateTime? endUtc = null)
    {
        if (Current is null) return null;
        var a = Current;
        a.EndUtc = endUtc ?? _clock();
        if (a.EndUtc < a.StartUtc) a.EndUtc = a.StartUtc;
        _store.UpdateActivity(a);
        Current = null;
        Changed?.Invoke();
        return a;
    }

    /// <summary>Modifica cliente/colleghi/note dell'attività in corso.</summary>
    public void UpdateCurrent(long? clientId, IEnumerable<long> personIds, string? notes)
    {
        if (Current is null) return;
        Current.ClientId = clientId;
        Current.PersonIds = personIds.ToList();
        Current.Notes = notes;
        _store.UpdateActivity(Current);
        Changed?.Invoke();
    }
}
