using System.Diagnostics;
using System.Text;

namespace TimeHero.Core;

/// <summary>Un file audio pronto da inviare; se è temporaneo viene cancellato quando lo si rilascia.</summary>
public sealed class PreparedAudio : IDisposable
{
    public string Path { get; }
    private readonly bool _temporary;

    public PreparedAudio(string path, bool temporary)
    {
        Path = path;
        _temporary = temporary;
    }

    public void Dispose()
    {
        if (!_temporary) return;
        try { File.Delete(Path); } catch { /* il file temporaneo verrà ripulito dal sistema */ }
    }
}

public interface IAudioPreparer
{
    Task<PreparedAudio> PrepareAsync(string inputPath, CancellationToken ct = default);
}

/// <summary>
/// Estrae dalla registrazione il solo audio (mono, AAC 48 kbps) in un m4a con l'indice all'inizio ("faststart").
/// Azure AI Speech rifiuta con "InvalidAudioFormat" i file lunghi/grandi con l'indice in fondo, e il video della
/// registrazione di OBS è inutile per la trascrizione: così il file diventa anche molto più piccolo.
/// </summary>
public sealed class FfmpegAudioPreparer : IAudioPreparer
{
    private readonly string _ffmpeg;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);

    public FfmpegAudioPreparer(string ffmpegPath) => _ffmpeg = ffmpegPath;

    /// <summary>
    /// Cerca ffmpeg: percorso indicato dall'utente, poi PATH, poi le posizioni dove lo mettono winget e le
    /// installazioni più comuni (un programma avviato all'accesso potrebbe non avere il PATH aggiornato).
    /// </summary>
    public static string? Find(string? configured = null)
    {
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe" } : new[] { "ffmpeg" };

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = configured.Trim().Trim('"');
            if (File.Exists(c)) return c;
            if (Directory.Exists(c))
                foreach (var n in names)
                    if (File.Exists(Path.Combine(c, n))) return Path.Combine(c, n);
        }

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
                foreach (var pkg in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*"))
                    foreach (var bin in SafeEnumerate(pkg, "bin"))
                        dirs.Add(bin);
        }
        dirs.Add(@"C:\ffmpeg\bin");
        dirs.Add(@"C:\Program Files\ffmpeg\bin");

        foreach (var d in dirs)
            foreach (var n in names)
            {
                var p = Path.Combine(d.Trim().Trim('"'), n);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string leaf)
    {
        try { return Directory.EnumerateDirectories(root, leaf, SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public async Task<PreparedAudio> PrepareAsync(string inputPath, CancellationToken ct = default)
    {
        var output = Path.Combine(Path.GetTempPath(), $"timehero_{Guid.NewGuid():N}.m4a");
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath,
                     "-vn", "-ac", "1", "-c:a", "aac", "-b:a", "48k", "-movflags", "+faststart", output,
                 })
            psi.ArgumentList.Add(a);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new TranscriptionException($"Non riesco ad avviare ffmpeg ({_ffmpeg}): {ex.Message}", null, ex);
        }
        if (proc is null) throw new TranscriptionException("Non riesco ad avviare ffmpeg.");

        using (proc)
        {
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.BeginErrorReadLine();
            _ = proc.StandardOutput.ReadToEndAsync(ct); // svuota l'output per non bloccare il processo

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* già finito */ }
                SafeDelete(output);
                if (ct.IsCancellationRequested) throw;
                throw new TranscriptionException("ffmpeg ci ha messo troppo a preparare l'audio.");
            }

            if (proc.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                SafeDelete(output);
                var tail = stderr.ToString().Trim();
                if (tail.Length > 300) tail = tail[^300..];
                throw new TranscriptionException(
                    $"ffmpeg non è riuscito a estrarre l'audio (codice {proc.ExitCode}){(tail.Length > 0 ? ": " + tail : "")}");
            }
        }
        return new PreparedAudio(output, temporary: true);
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { /* ok */ }
    }
}
