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
}
