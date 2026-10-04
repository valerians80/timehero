namespace TimeHero.Core;

public record Client(long Id, string Name, string? Code, bool Active);

public record Person(long Id, string Name, string? Email, string? Role);

public record Category(long Id, string Name, string Color, int SortOrder, bool Active);

public record Attachment(long Id, long ActivityId, string FilePath, DateTime CreatedUtc);

/// <summary>Una nota con orario: il "diario" dell'attività, da cui si ricostruisce la cronologia.</summary>
public record ActivityNote(long Id, long ActivityId, DateTime CreatedUtc, string Text);

/// <summary>Una sessione di lavoro su un'attività. EndUtc == null significa "in corso". Orari UTC.</summary>
public record Segment(long Id, long ActivityId, DateTime StartUtc, DateTime? EndUtc)
{
    public TimeSpan Duration(DateTime nowUtc) => (EndUtc ?? nowUtc) - StartUtc;
}

/// <summary>
/// Un'attività con titolo (es. "Deploy VM per Contoso"). Può essere lavorata in più sessioni
/// (<see cref="Segments"/>): in pausa tra una e l'altra, chiusa solo quando ClosedUtc è valorizzato.
/// </summary>
public class Activity
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public long CategoryId { get; set; }
    public long? ClientId { get; set; }

    /// <summary>Inizio della prima sessione.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Quando l'attività è stata chiusa con "Fine"; null = ancora aperta (in corso o in pausa).</summary>
    public DateTime? ClosedUtc { get; set; }

    public string? Notes { get; set; }
    public bool Billable { get; set; } = true;
    public List<long> PersonIds { get; set; } = new();
    public List<Segment> Segments { get; set; } = new();

    public bool IsClosed => ClosedUtc is not null;
    public bool IsRunning => !IsClosed && Segments.Any(s => s.EndUtc is null);
    public bool IsPaused => !IsClosed && !IsRunning;

    /// <summary>Tempo realmente lavorato: somma di tutte le sessioni.</summary>
    public TimeSpan Duration(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return TimeSpan.FromTicks(Segments.Sum(s => s.Duration(now).Ticks));
    }
}
