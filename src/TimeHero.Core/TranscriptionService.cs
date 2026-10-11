namespace TimeHero.Core;

/// <summary>
/// Trascrive una registrazione allegata a un'attività e salva il risultato (o l'errore) nel database.
/// </summary>
public sealed class TranscriptionService
{
    private readonly TimeStore _store;
    private readonly AzureTranscriber _api;

    /// <summary>Quanto aspettare che OBS finisca di scrivere/rimuxare il file prima di rinunciare.</summary>
    public TimeSpan FileWait { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan FilePoll { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Limite di dimensione; se null si usa quello del servizio scelto.</summary>
    public long? MaxBytes { get; set; }

    public TranscriptionService(TimeStore store, AzureTranscriber? api = null)
    {
        _store = store;
        _api = api ?? new AzureTranscriber();
    }

    /// <summary>Trascrive l'allegato audio. Non solleva per errori attesi: li salva come trascrizione "error".</summary>
    public async Task<Transcript> TranscribeAsync(long attachmentId, TranscriptionOptions options, CancellationToken ct = default)
    {
        var att = _store.GetAttachment(attachmentId) ?? throw new InvalidOperationException("Allegato inesistente.");
        string? text = null, error = null;

        try
        {
            var path = await RecordingFiles.WaitUsableAsync(att.FilePath, AzureTranscriber.SupportedExtensions, FileWait, FilePoll, ct);
            if (path is null)
            {
                var ext = Path.GetExtension(RecordingFiles.Resolve(att.FilePath)).ToLowerInvariant();
                error = !File.Exists(RecordingFiles.Resolve(att.FilePath))
                    ? "File della registrazione non trovato (spostato o eliminato?)."
                    : $"Formato {ext} non accettato da Azure: in OBS imposta il formato di registrazione su mp4.";
            }
            else
            {
                var size = new FileInfo(path).Length;
                var limit = MaxBytes ?? options.MaxBytes;
                if (size > limit)
                {
                    error = $"File di {size / 1024.0 / 1024.0:0.#} MB: supera il limite di {limit / 1024 / 1024} MB. " +
                            "Abbassa il bitrate audio in OBS o metti in pausa a metà chiamata per creare due file.";
                }
                else
                {
                    text = await _api.TranscribeAsync(path, options, BuildPrompt(att.ActivityId), ct);
                }
            }
        }
        catch (TranscriptionException ex)
        {
            error = ex.Message;
        }

        var id = _store.SaveTranscript(att.Id, att.ActivityId, options.ModelLabel, text,
            error is null ? "done" : "error", error);
        return _store.GetTranscript(id)!;
    }

    /// <summary>Contesto per il modello: aiuta a scrivere bene i nomi di clienti e persone.</summary>
    private string? BuildPrompt(long activityId)
    {
        var a = _store.GetActivity(activityId);
        if (a is null) return null;
        var parts = new List<string> { "Conversazione di lavoro in italiano." };
        if (a.ClientId is { } cid && _store.GetClients(true).FirstOrDefault(c => c.Id == cid) is { } client)
            parts.Add($"Cliente: {client.Name}.");
        var names = _store.GetPeople().Where(p => a.PersonIds.Contains(p.Id)).Select(p => p.Name).ToList();
        if (names.Count > 0) parts.Add($"Partecipanti: {string.Join(", ", names)}.");
        if (!string.IsNullOrWhiteSpace(a.Title)) parts.Add($"Argomento: {a.Title}.");
        var prompt = string.Join(" ", parts);
        return prompt.Length > 600 ? prompt[..600] : prompt;
    }
}
