using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TimeHero.Core;

/// <summary>Trasforma la risposta della trascrizione veloce di Azure AI Speech in un testo a turni di parola.</summary>
public static class SpeechTranscript
{
    /// <summary>
    /// Raggruppa le frasi consecutive dello stesso interlocutore:
    /// "[00:04] Interlocutore 2: …". Senza informazioni sugli interlocutori restituisce il testo semplice.
    /// </summary>
    public static string Format(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var turns = new List<(int? Speaker, long Offset, StringBuilder Text)>();
        if (root.TryGetProperty("phrases", out var phrases) && phrases.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in phrases.EnumerateArray())
            {
                var text = p.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(text)) continue;
                int? speaker = p.TryGetProperty("speaker", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
                var offset = p.TryGetProperty("offsetMilliseconds", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : 0;

                if (turns.Count > 0 && turns[^1].Speaker == speaker)
                    turns[^1].Text.Append(' ').Append(text);
                else
                    turns.Add((speaker, offset, new StringBuilder(text)));
            }
        }

        if (turns.Count == 0)
        {
            // nessuna frase: usa il testo complessivo, se c'è
            if (root.TryGetProperty("combinedPhrases", out var combined) && combined.ValueKind == JsonValueKind.Array)
                return string.Join(Environment.NewLine,
                    combined.EnumerateArray().Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : null)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
            return "";
        }

        var hasSpeakers = turns.Any(t => t.Speaker is not null);
        var anyHour = turns.Any(t => t.Offset >= 3_600_000);
        var sb = new StringBuilder();
        foreach (var (speaker, offset, text) in turns)
        {
            var ts = TimeSpan.FromMilliseconds(offset);
            var stamp = anyHour ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            sb.Append('[').Append(stamp).Append("] ");
            if (hasSpeakers) sb.Append(speaker is { } n ? $"Interlocutore {n}: " : "Interlocutore ?: ");
            sb.AppendLine(text.ToString());
        }
        return sb.ToString().TrimEnd();
    }
}
