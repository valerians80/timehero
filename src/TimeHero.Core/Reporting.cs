using System.Globalization;
using System.Text;

namespace TimeHero.Core;

/// <summary>Riga pronta per lo storico / il timesheet (orari in locale).</summary>
/// <summary>
/// Una riga per attività e per giorno: il tempo è la somma di tutte le sessioni di quel giorno,
/// quindi pause e riprese non moltiplicano le voci.
/// </summary>
public record TimesheetRow(
    long ActivityId, DateOnly Date, DateTime Start, DateTime End, TimeSpan Duration,
    string Title, string Category, string? Client, string People, string? Notes, bool Billable, int Attachments,
    bool Running, string Status, string? Log = null);

public record SummaryLine(string Key, TimeSpan Total);

public static class Reporting
{
    /// <summary>Arrotonda al multiplo più vicino di <paramref name="minutes"/> (0 = nessun arrotondamento).</summary>
    public static TimeSpan Round(TimeSpan d, int minutes)
    {
        if (minutes <= 0) return d;
        var steps = Math.Round(d.TotalMinutes / minutes, MidpointRounding.AwayFromZero);
        return TimeSpan.FromMinutes(steps * minutes);
    }

    /// <summary>
    /// Costruisce le righe del timesheet per l'intervallo [fromLocalDate, toLocalDate) con i nomi risolti.
    /// Una sessione è attribuita al giorno in cui inizia.
    /// </summary>
    public static List<TimesheetRow> BuildRows(TimeStore store, DateTime fromLocalDate, DateTime toLocalDate,
        DateTime? nowUtc = null)
    {
        var from = DateTime.SpecifyKind(fromLocalDate.Date, DateTimeKind.Local).ToUniversalTime();
        var to = DateTime.SpecifyKind(toLocalDate.Date, DateTimeKind.Local).ToUniversalTime();
        var now = nowUtc ?? DateTime.UtcNow;

        var cats = store.GetCategories(true).ToDictionary(c => c.Id, c => c.Name);
        var clients = store.GetClients(true).ToDictionary(c => c.Id, c => c.Name);
        var people = store.GetPeople().ToDictionary(p => p.Id, p => p.Name);

        var segments = store.GetSegments(from, to);
        var activities = segments.Select(s => s.ActivityId).Distinct()
            .Select(store.GetActivity).Where(a => a is not null).ToDictionary(a => a!.Id, a => a!);

        return segments
            .Where(s => activities.ContainsKey(s.ActivityId))
            .GroupBy(s => (s.ActivityId, Day: DateOnly.FromDateTime(s.StartUtc.ToLocalTime())))
            .Select(g =>
            {
                var a = activities[g.Key.ActivityId];
                var start = g.Min(s => s.StartUtc).ToLocalTime();
                var end = g.Max(s => s.EndUtc ?? now).ToLocalTime();
                var running = g.Any(s => s.EndUtc is null);
                return new TimesheetRow(
                    a.Id, g.Key.Day, start, end,
                    TimeSpan.FromTicks(g.Sum(s => s.Duration(now).Ticks)),
                    a.Title,
                    cats.GetValueOrDefault(a.CategoryId, "?"),
                    a.ClientId is { } cid ? clients.GetValueOrDefault(cid) : null,
                    string.Join(", ", a.PersonIds.Select(id => people.GetValueOrDefault(id, "?"))),
                    a.Notes, a.Billable, store.GetAttachments(a.Id).Count,
                    running, a.IsClosed ? "chiusa" : running ? "in corso" : "in pausa",
                    DayLog(store.GetNotes(a.Id), g.Key.Day));
            })
            .OrderBy(r => r.Start)
            .ToList();
    }

    /// <summary>Le note con orario scritte quel giorno, in una riga: "10:42 chiamato il cliente; 11:05 ...".</summary>
    private static string? DayLog(IEnumerable<ActivityNote> notes, DateOnly day)
    {
        var lines = notes
            .Where(n => DateOnly.FromDateTime(n.CreatedUtc.ToLocalTime()) == day)
            .Select(n => $"{n.CreatedUtc.ToLocalTime():HH:mm} {n.Text.ReplaceLineEndings(" ")}")
            .ToList();
        return lines.Count == 0 ? null : string.Join("; ", lines);
    }

    public static List<SummaryLine> SummarizeBy(IEnumerable<TimesheetRow> rows, Func<TimesheetRow, string> key,
        int roundMinutes = 0) =>
        rows.GroupBy(key)
            .Select(g => new SummaryLine(g.Key, Round(TimeSpan.FromTicks(g.Sum(r => r.Duration.Ticks)), roundMinutes)))
            .OrderByDescending(s => s.Total)
            .ToList();

    public static string FormatHm(TimeSpan t) => $"{(int)t.TotalHours}h {t.Minutes:00}m";

    /// <summary>CSV con separatore ';' (apribile direttamente in Excel italiano).</summary>
    public static string ToCsv(IEnumerable<TimesheetRow> rows, int roundMinutes = 0)
    {
        var it = CultureInfo.GetCultureInfo("it-IT");
        var sb = new StringBuilder();
        sb.AppendLine("Data;Inizio;Fine;Durata (min);Durata (ore);Attività;Categoria;Cliente;Colleghi;Note;Cronologia;Fatturabile;Stato;Allegati");
        foreach (var r in rows)
        {
            var d = Round(r.Duration, roundMinutes);
            sb.AppendLine(string.Join(';',
                r.Date.ToString("dd/MM/yyyy", it), r.Start.ToString("HH:mm"), r.End.ToString("HH:mm"),
                ((int)Math.Round(d.TotalMinutes)).ToString(it), d.TotalHours.ToString("0.00", it),
                Esc(r.Title), Esc(r.Category), Esc(r.Client), Esc(r.People), Esc(r.Notes), Esc(r.Log),
                r.Billable ? "sì" : "no", r.Status, r.Attachments.ToString(it)));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Riepilogo testuale da usare come promemoria per compilare il timesheet:
    /// per ogni giorno una riga per (cliente, categoria) con ore arrotondate e note.
    /// </summary>
    public static string ToSummaryText(IEnumerable<TimesheetRow> rows, int roundMinutes = 0)
    {
        var it = CultureInfo.GetCultureInfo("it-IT");
        var sb = new StringBuilder();
        foreach (var day in rows.GroupBy(r => r.Date).OrderBy(g => g.Key))
        {
            var lines = day
                .GroupBy(r => new { Client = r.Client ?? "(nessun cliente)", r.Title, r.Category })
                .Select(g => new
                {
                    g.Key.Client,
                    g.Key.Title,
                    g.Key.Category,
                    Total = Round(TimeSpan.FromTicks(g.Sum(r => r.Duration.Ticks)), roundMinutes),
                    Notes = g.SelectMany(r => new[] { r.Notes, r.Log }).Where(n => !string.IsNullOrWhiteSpace(n))
                             .Select(n => n!.Trim()).Distinct().ToList(),
                })
                .OrderBy(l => l.Client).ThenBy(l => l.Title)
                .ToList();

            var total = TimeSpan.FromTicks(lines.Sum(l => l.Total.Ticks));
            sb.AppendLine($"{day.Key.ToString("ddd dd/MM/yyyy", it)} — totale {FormatHm(total)}");
            foreach (var l in lines)
            {
                sb.Append($"  {l.Client} | {l.Title} ({l.Category}) | {l.Total.TotalHours.ToString("0.00", it)} h");
                if (l.Notes.Count > 0) sb.Append(" | ").Append(string.Join("; ", l.Notes));
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Contains(';') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
