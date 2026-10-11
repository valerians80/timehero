namespace TimeHero.Core;

public static class RecordingFiles
{
    private static readonly string[] Extensions = { ".mp4", ".mkv", ".m4a", ".mp3", ".mov", ".flv", ".ts" };

    /// <summary>
    /// OBS può rimuxare il file a fine registrazione (es. .mkv → .mp4): se il percorso salvato non esiste
    /// più, cerca lo stesso nome con un'altra estensione.
    /// </summary>
    public static string Resolve(string path)
    {
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return path;
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var ext in Extensions)
        {
            var candidate = Path.Combine(dir, name + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return path;
    }

    /// <summary>
    /// Attende che esista un file in un formato accettato (supportedExtensions) e che abbia smesso di crescere:
    /// OBS può ancora rimuxare .mkv → .mp4 o chiudere il file qualche secondo dopo lo stop.
    /// Restituisce null se entro il timeout non compare nulla di utilizzabile.
    /// </summary>
    public static async Task<string?> WaitUsableAsync(string path, IReadOnlyCollection<string> supportedExtensions,
        TimeSpan timeout, TimeSpan? poll = null, CancellationToken ct = default)
    {
        var interval = poll ?? TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + timeout;
        string? lastPath = null;
        long lastSize = -1;

        while (true)
        {
            var found = FindUsable(path, supportedExtensions);
            if (found is not null)
            {
                var size = new FileInfo(found).Length;
                if (found == lastPath && size == lastSize && size > 0) return found; // stabile
                lastPath = found;
                lastSize = size;
            }
            if (DateTime.UtcNow >= deadline) return found is not null && new FileInfo(found).Length > 0 ? found : null;
            await Task.Delay(interval, ct);
        }
    }

    private static string? FindUsable(string path, IReadOnlyCollection<string> supported)
    {
        bool Ok(string p) => File.Exists(p) && supported.Contains(Path.GetExtension(p).ToLowerInvariant());
        if (Ok(path)) return path;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var ext in Extensions)
        {
            var candidate = Path.Combine(dir, name + ext);
            if (Ok(candidate)) return candidate;
        }
        return null;
    }
}
