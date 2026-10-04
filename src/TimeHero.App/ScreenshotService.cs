using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TimeHero.App;

public static class ScreenshotService
{
    /// <summary>
    /// Apre il ritaglio di Windows (Snipping Tool) e attende che l'immagine arrivi negli appunti.
    /// Va chiamato dal thread UI. Restituisce il percorso del PNG salvato, o null se scaduto/annullato.
    /// </summary>
    public static async Task<string?> CaptureAsync(string targetPath, TimeSpan timeout)
    {
        var seq = NativeMethods.GetClipboardSequenceNumber();
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch
        {
            return null;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            if (NativeMethods.GetClipboardSequenceNumber() == seq) continue;
            var saved = TrySaveClipboardImage(targetPath);
            if (saved) return targetPath;
            seq = NativeMethods.GetClipboardSequenceNumber(); // cambiato ma non è un'immagine: continua
        }
        return null;
    }

    /// <summary>Salva come PNG l'immagine presente negli appunti (Ctrl+V / Win+Shift+S).</summary>
    public static bool TrySaveClipboardImage(string targetPath)
    {
        try
        {
            if (!Clipboard.ContainsImage()) return false;
            var img = Clipboard.GetImage();
            if (img is null) return false;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var fs = File.Create(targetPath);
            encoder.Save(fs);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false; // appunti momentaneamente bloccati da un altro processo
        }
    }
}
