namespace TimeHero.Core;

public record Client(long Id, string Name, string? Code, bool Active);

public record Person(long Id, string Name, string? Email, string? Role);

public record Category(long Id, string Name, string Color, int SortOrder, bool Active);

public record Attachment(long Id, long ActivityId, string FilePath, DateTime CreatedUtc);

/// <summary>Una attività tracciata. End == null significa "in corso". Gli orari sono UTC.</summary>
public class Activity
{
    public long Id { get; set; }
    public long CategoryId { get; set; }
    public long? ClientId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public string? Notes { get; set; }
    public bool Billable { get; set; } = true;
    public List<long> PersonIds { get; set; } = new();

    public bool IsRunning => EndUtc is null;

    public TimeSpan Duration(DateTime? nowUtc = null) =>
        (EndUtc ?? nowUtc ?? DateTime.UtcNow) - StartUtc;
}
