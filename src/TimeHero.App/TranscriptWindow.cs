using System.Windows;
using System.Windows.Controls;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Il testo di una trascrizione: lettura, copia, salvataggio.</summary>
public sealed class TranscriptWindow : Window
{
    private readonly Transcript _transcript;
    private readonly string _title;

    public TranscriptWindow(Transcript transcript, string activityTitle)
    {
        _transcript = transcript;
        _title = activityTitle;
        Title = "TimeHero — trascrizione";
        Width = 720;
        Height = 620;
        Background = Ui.Bg;
        Foreground = Ui.Fg;
        FontFamily = Ui.Font;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);

        var root = new DockPanel { Margin = new Thickness(14) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(Ui.Text(activityTitle, 15, true));
        header.Children.Add(Ui.Text(
            $"{transcript.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · modello {transcript.Model}", 11, fg: Ui.Subtle));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Ui.Btn("📋 Copia", (_, _) => Clipboard.SetText(transcript.Text ?? "")));
        buttons.Children.Add(Ui.Btn("💾 Salva .txt", (_, _) => Save()));
        buttons.Children.Add(Ui.Btn("Chiudi", (_, _) => Close()));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new TextBox
        {
            Text = transcript.Status == "done" ? transcript.Text ?? "" : $"Trascrizione non riuscita:\n{transcript.Error}",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            Padding = new Thickness(10),
        });
        Content = root;
    }

    private void Save()
    {
        var safe = string.Concat(_title.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Testo (*.txt)|*.txt",
            FileName = $"{safe} - {_transcript.CreatedUtc.ToLocalTime():yyyyMMdd_HHmm}.txt",
        };
        if (dlg.ShowDialog(this) == true) System.IO.File.WriteAllText(dlg.FileName, _transcript.Text ?? "");
    }
}
