namespace TimeHero.Core;

/// <summary>Un fatto della vita di un'attività: avvio, ripresa, nota, pausa, chiusura.</summary>
public record TimelineEvent(DateTime WhenUtc, TimelineKind Kind, string Text, long? NoteId = null, long? TranscriptId = null);

public enum TimelineKind { Start, Resume, Note, Transcript, Pause, Close }

public static class Timeline
{
    /// <summary>
    /// Ricostruisce in ordine cronologico la storia di un'attività: quando è partita, ogni nota scritta,
    /// ogni pausa (con la durata della sessione), ogni ripresa e l'eventuale chiusura.
    /// </summary>
    public static List<TimelineEvent> Build(Activity a, IEnumerable<ActivityNote> notes,
        IEnumerable<Transcript>? transcripts = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var events = new List<TimelineEvent>();
        var segments = a.Segments.OrderBy(s => s.StartUtc).ToList();

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            events.Add(new TimelineEvent(s.StartUtc, i == 0 ? TimelineKind.Start : TimelineKind.Resume,
                i == 0 ? "Avviata" : "Ripresa"));

            if (s.EndUtc is not { } end) continue;
            var lastAndClosed = a.IsClosed && i == segments.Count - 1;
            var minutes = (int)Math.Round((end - s.StartUtc).TotalMinutes);
            events.Add(new TimelineEvent(end, lastAndClosed ? TimelineKind.Close : TimelineKind.Pause,
                $"{(lastAndClosed ? "Chiusa" : "In pausa")} (sessione di {minutes} min)"));
        }

        foreach (var n in notes) events.Add(new TimelineEvent(n.CreatedUtc, TimelineKind.Note, n.Text, n.Id));

        foreach (var t in transcripts ?? Enumerable.Empty<Transcript>())
        {
            var words = string.IsNullOrWhiteSpace(t.Text) ? 0 : t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            events.Add(new TimelineEvent(t.CreatedUtc, TimelineKind.Transcript,
                t.Status == "done" ? $"Trascrizione pronta ({words} parole) — apri per leggerla" : $"Trascrizione non riuscita: {t.Error}",
                null, t.Id));
        }

        // a parità di orario: prima l'avvio, poi le note, poi la pausa/chiusura
        static int Order(TimelineKind k) => k switch
        {
            TimelineKind.Start or TimelineKind.Resume => 0,
            TimelineKind.Note or TimelineKind.Transcript => 1,
            _ => 2,
        };
        return events.OrderBy(e => e.WhenUtc).ThenBy(e => Order(e.Kind)).ToList();
    }

    public static string KindIcon(TimelineKind k) => k switch
    {
        TimelineKind.Start => "▶",
        TimelineKind.Resume => "▶",
        TimelineKind.Note => "📝",
        TimelineKind.Transcript => "📄",
        TimelineKind.Pause => "⏸",
        _ => "✔",
    };
}
