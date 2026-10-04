using System.Globalization;
using System.Text;

namespace TimeHero.Core;

/// <summary>Riga pronta per lo storico / il timesheet (orari in locale).</summary>
public record TimesheetRow(
    long ActivityId, DateOnly Date, DateTime Start, DateTime End, TimeSpan Duration,
    string Category, string? Client, string People, string? Notes, bool Billable, int Attachments,
    bool Running);

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

    /// <summary>Costruisce le righe del giorno/intervallo [fromLocal, toLocal) con i nomi risolti.</summary>
    public static List<TimesheetRow> BuildRows(TimeStore store, DateTime fromLocalDate, DateTime toLocalDate,
        DateTime? nowUtc = null)
    {
        var from = DateTime.SpecifyKind(fromLocalDate.Date, DateTimeKind.Local).ToUniversalTime();
        var to = DateTime.SpecifyKind(toLocalDate.Date, DateTimeKind.Local).ToUniversalTime();
        var now = nowUtc ?? DateTime.UtcNow;

        var cats = store.GetCategories(true).ToDictionary(c => c.Id, c => c.Name);
        var clients = store.GetClients(true).ToDictionary(c => c.Id, c => c.Name);
        var people = store.GetPeople().ToDictionary(p => p.Id, p => p.Name);

        return store.GetActivities(from, to).Select(a =>
        {
            var start = a.StartUtc.ToLocalTime();
            var end = (a.EndUtc ?? now).ToLocalTime();
            return new TimesheetRow(
                a.Id, DateOnly.FromDateTime(start), start, end, end - start,
                cats.GetValueOrDefault(a.CategoryId, "?"),
                a.ClientId is { } cid ? clients.GetValueOrDefault(cid) : null,
                string.Join(", ", a.PersonIds.Select(id => people.GetValueOrDefault(id, "?"))),
                a.Notes, a.Billable, store.GetAttachments(a.Id).Count, a.IsRunning);
        }).ToList();
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
        sb.AppendLine("Data;Inizio;Fine;Durata (min);Durata (ore);Categoria;Cliente;Colleghi;Note;Fatturabile;Allegati");
        foreach (var r in rows)
        {
            var d = Round(r.Duration, roundMinutes);
            sb.AppendLine(string.Join(';',
                r.Date.ToString("dd/MM/yyyy", it), r.Start.ToString("HH:mm"), r.End.ToString("HH:mm"),
                ((int)Math.Round(d.TotalMinutes)).ToString(it), d.TotalHours.ToString("0.00", it),
                Esc(r.Category), Esc(r.Client), Esc(r.People), Esc(r.Notes),
                r.Billable ? "sì" : "no", r.Attachments.ToString(it)));
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
                .GroupBy(r => new { Client = r.Client ?? "(nessun cliente)", r.Category })
                .Select(g => new
                {
                    g.Key.Client,
                    g.Key.Category,
                    Total = Round(TimeSpan.FromTicks(g.Sum(r => r.Duration.Ticks)), roundMinutes),
                    Notes = g.Select(r => r.Notes).Where(n => !string.IsNullOrWhiteSpace(n))
                             .Select(n => n!.Trim()).Distinct().ToList(),
                })
                .OrderBy(l => l.Client).ThenBy(l => l.Category)
                .ToList();

            var total = TimeSpan.FromTicks(lines.Sum(l => l.Total.Ticks));
            sb.AppendLine($"{day.Key.ToString("ddd dd/MM/yyyy", it)} — totale {FormatHm(total)}");
            foreach (var l in lines)
            {
                sb.Append($"  {l.Client} | {l.Category} | {l.Total.TotalHours.ToString("0.00", it)} h");
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
