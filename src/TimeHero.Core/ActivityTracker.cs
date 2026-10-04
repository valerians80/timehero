namespace TimeHero.Core;

/// <summary>
/// Logica delle attività: una sola attività "in corso" (che accumula tempo) e un numero qualsiasi
/// di attività in pausa. Pausa/ripresa aggiungono sessioni alla STESSA attività; solo <see cref="Finish"/> la chiude.
/// </summary>
public sealed class ActivityTracker
{
    private readonly TimeStore _store;
    private readonly Func<DateTime> _clock;

    public ActivityTracker(TimeStore store, Func<DateTime>? utcClock = null)
    {
        _store = store;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        LoadState();
    }

    /// <summary>L'attività che sta accumulando tempo ora.</summary>
    public Activity? Current { get; private set; }

    /// <summary>Attività aperte ma in pausa (i "riquadri rossi").</summary>
    public IReadOnlyList<Activity> Paused { get; private set; } = Array.Empty<Activity>();

    /// <summary>Cambia l'attività in corso o l'elenco di quelle in pausa.</summary>
    public event Action? Changed;

    /// <summary>Rilegge lo stato dal database (dopo modifiche fatte altrove, es. nello storico).</summary>
    public void Reload()
    {
        LoadState();
        Changed?.Invoke();
    }

    private void LoadState()
    {
        var running = _store.GetRunningActivities();
        // difesa da dati incoerenti: se per errore ce n'è più d'una, ne resta in corso solo l'ultima
        foreach (var extra in running.Take(Math.Max(0, running.Count - 1)))
            _store.EndOpenSegment(extra.Id, _clock());
        if (running.Count > 1) running = _store.GetRunningActivities();

        Current = running.LastOrDefault();
        Paused = _store.GetOpenActivities().Where(a => a.Id != Current?.Id).ToList();
    }

    /// <summary>Crea una nuova attività e la avvia; quella in corso (se c'è) va in pausa.</summary>
    public Activity Start(string? title, long categoryId, long? clientId = null,
        IEnumerable<long>? personIds = null, string? notes = null)
    {
        var now = _clock();
        PauseCurrent(now);

        var a = new Activity
        {
            Title = string.IsNullOrWhiteSpace(title) ? "(senza titolo)" : title.Trim(),
            CategoryId = categoryId,
            ClientId = clientId,
            StartUtc = now,
            Notes = notes,
            PersonIds = personIds?.ToList() ?? new(),
            Segments = { new Segment(0, 0, now, null) },
        };
        _store.InsertActivity(a);
        LoadState();
        Changed?.Invoke();
        return a;
    }

    /// <summary>Mette in pausa l'attività in corso (resta aperta). L'orario può essere retrodatato.</summary>
    public void Pause(DateTime? atUtc = null)
    {
        if (Current is null) return;
        PauseCurrent(atUtc ?? _clock());
        LoadState();
        Changed?.Invoke();
    }

    /// <summary>Riprende un'attività in pausa aggiungendole una nuova sessione; quella in corso va in pausa.</summary>
    public void Resume(long activityId)
    {
        if (Current?.Id == activityId) return;
        var target = Paused.FirstOrDefault(a => a.Id == activityId);
        if (target is null) return;

        var now = _clock();
        PauseCurrent(now);
        _store.StartSegment(activityId, now);
        LoadState();
        Changed?.Invoke();
    }

    /// <summary>
    /// Chiude definitivamente un'attività (quella in corso se non si specifica un id).
    /// Se era in pausa la chiusura coincide con la fine dell'ultima sessione.
    /// </summary>
    public void Finish(long? activityId = null)
    {
        var target = activityId is null || activityId == Current?.Id
            ? Current
            : Paused.FirstOrDefault(a => a.Id == activityId);
        if (target is null) return;

        var now = _clock();
        DateTime closedAt;
        if (target.IsRunning)
        {
            _store.EndOpenSegment(target.Id, now);
            closedAt = now;
        }
        else
        {
            closedAt = target.Segments.Count > 0
                ? target.Segments.Max(s => s.EndUtc ?? s.StartUtc)
                : now;
        }
        _store.CloseActivity(target.Id, closedAt);
        LoadState();
        Changed?.Invoke();
    }

    /// <summary>Aggiunge una nota con l'orario attuale all'attività in corso (nessun evento di cambio stato).</summary>
    public ActivityNote? AddNote(string text)
    {
        if (Current is null || string.IsNullOrWhiteSpace(text)) return null;
        var now = _clock();
        var id = _store.AddNote(Current.Id, text, now);
        return new ActivityNote(id, Current.Id, now, text.Trim());
    }

    /// <summary>Salva titolo/cliente/colleghi/note modificati (non cambia lo stato, nessun evento).</summary>
    public void UpdateDetails(Activity a) => _store.UpdateActivity(a);

    private void PauseCurrent(DateTime atUtc)
    {
        if (Current is not null) _store.EndOpenSegment(Current.Id, atUtc);
    }
}
