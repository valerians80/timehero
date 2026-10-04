using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Il "post-it": finestra senza bordi ancorata sopra la taskbar, in basso a destra.</summary>
public sealed class FlyoutWindow : Window
{
    private static readonly Brush PausedTile = Ui.Hex("#F4978E");

    private readonly TrayApp _app;
    private TimeStore Store => _app.Store;
    private ActivityTracker Tracker => _app.Tracker;

    // attività in corso
    private readonly TextBox _curTitle = new()
    {
        FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Ink,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(0, 0, 0, 2), ToolTip = "Titolo dell'attività (modificabile)",
    };
    private readonly TextBlock _curMeta = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly TextBlock _elapsed = Ui.Text("00:00:00", 26, true);
    private readonly TextBlock _session = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly TextBlock _attach = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly Button _pause;
    private readonly Button _finish;
    private readonly StackPanel _details = new();
    private readonly ComboBox _client = new() { IsEditable = true, FontSize = 12 };
    private readonly ComboBox _peoplePick = new() { FontSize = 12, ToolTip = "Aggiungi un collega dall'elenco" };
    private readonly TextBox _people = Ui.Input();
    private readonly TextBox _notes = Ui.Input();

    // attività in pausa (riquadri rossi)
    private readonly StackPanel _pausedSection = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _pausedLabel = Ui.Text("", 10.5, fg: Ui.Subtle);
    private readonly WrapPanel _pausedTiles = new();

    // nuova attività
    private readonly TextBox _newTitle = Ui.Input();
    private readonly UniformGrid _cats = new() { Columns = 2 };

    private readonly TextBlock _today = Ui.Text("Oggi: 0h 00m", 12, true);
    private readonly ToggleButton _pin;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private long? _shownActivityId;
    private TimeSpan _curClosed;   // tempo già accumulato dall'attività in corso nelle sessioni concluse
    private TimeSpan _doneToday;   // tempo di oggi nelle sessioni concluse

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;
    public bool Pinned => _pin.IsChecked == true;

    public FlyoutWindow(TrayApp app)
    {
        _app = app;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        ShowActivated = true;
        Title = "TimeHero";

        _pause = Ui.Btn("⏸ Pausa", (_, _) => OnPause(), tooltip: "Metti in pausa: resta aperta e la ritrovi nei riquadri rossi");
        _finish = Ui.Btn("✔ Fine", (_, _) => OnFinish(), Ui.Go, "Chiudi definitivamente l'attività");
        _pin = new ToggleButton
        {
            Content = "📌",
            ToolTip = "Tieni aperto",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            Cursor = Cursors.Hand,
        };

        Content = BuildUi();

        _peoplePick.SelectionChanged += (_, _) =>
        {
            if (_peoplePick.SelectedItem is not string name) return;
            var names = SplitNames(_people.Text);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            _people.Text = string.Join(", ", names);
            _peoplePick.SelectedIndex = -1;
            ApplyDetailsToCurrent();
        };
        _curTitle.LostFocus += (_, _) => ApplyDetailsToCurrent();
        _client.LostFocus += (_, _) => ApplyDetailsToCurrent();
        _client.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyDetailsToCurrent);
        _people.LostFocus += (_, _) => ApplyDetailsToCurrent();
        _notes.LostFocus += (_, _) => ApplyDetailsToCurrent();

        Deactivated += (_, _) =>
        {
            if (!Pinned && IsVisible) Hide();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) LastHiddenUtc = DateTime.UtcNow;
        };
        SizeChanged += (_, _) => { if (IsVisible) Reposition(); };
        PreviewKeyDown += OnPreviewKeyDown;
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
    }

    // ---------- costruzione UI ----------
    private UIElement BuildUi()
    {
        var root = new StackPanel { Margin = new Thickness(12) };

        // intestazione
        var header = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        tools.Children.Add(_pin);
        tools.Children.Add(Ui.Btn("📊", (_, _) => _app.ShowHistory(), Brushes.Transparent, "Storico"));
        tools.Children.Add(Ui.Btn("⚙", (_, _) => _app.ShowSettings(), Brushes.Transparent, "Impostazioni"));
        header.Children.Add(tools);
        header.Children.Add(Ui.Text("TimeHero", 12, true, Ui.Subtle));
        root.Children.Add(header);

        // attività in corso
        var current = new Border
        {
            Background = Ui.Panel,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 4, 0, 6),
        };
        var cp = new StackPanel();
        cp.Children.Add(_curTitle);
        cp.Children.Add(_curMeta);
        cp.Children.Add(_elapsed);
        cp.Children.Add(_session);
        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_pause);
        buttons.Children.Add(_finish);
        cp.Children.Add(buttons);
        var shots = new UniformGrid { Columns = 2 };
        shots.Children.Add(Ui.Btn("📎 Screenshot", async (_, _) => await _app.CaptureScreenshotAsync(),
            tooltip: "Ritaglia una parte dello schermo e allegala all'attività in corso"));
        shots.Children.Add(Ui.Btn("📋 Incolla", (_, _) => _app.PasteScreenshot(),
            tooltip: "Allega l'immagine negli appunti (anche Ctrl+V)"));
        cp.Children.Add(shots);
        cp.Children.Add(_attach);

        _details.Children.Add(Ui.Field("Cliente", _client));
        var peopleRow = new DockPanel();
        _peoplePick.Width = 90;
        DockPanel.SetDock(_peoplePick, Dock.Right);
        peopleRow.Children.Add(_peoplePick);
        peopleRow.Children.Add(_people);
        _details.Children.Add(Ui.Field("Colleghi (separati da virgola)", peopleRow));
        _details.Children.Add(Ui.Field("Note", _notes));
        cp.Children.Add(_details);
        current.Child = cp;
        root.Children.Add(current);

        // attività in pausa
        _pausedSection.Children.Add(_pausedLabel);
        _pausedSection.Children.Add(_pausedTiles);
        root.Children.Add(_pausedSection);

        // nuova attività
        var newLabel = Ui.Text("Nuova attività", 10.5, fg: Ui.Subtle);
        newLabel.Margin = new Thickness(0, 8, 0, 2);
        root.Children.Add(newLabel);
        _newTitle.ToolTip = "Titolo, es. \"Deploy VM per Contoso\". Se lo lasci vuoto si usa il nome della categoria.";
        root.Children.Add(_newTitle);
        var catsHint = Ui.Text("poi scegli il tipo per avviarla ↓", 10, fg: Ui.Subtle);
        catsHint.Margin = new Thickness(0, 2, 0, 2);
        root.Children.Add(catsHint);
        root.Children.Add(_cats);

        // piè di pagina
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var hist = Ui.Btn("Storico →", (_, _) => _app.ShowHistory());
        DockPanel.SetDock(hist, Dock.Right);
        footer.Children.Add(hist);
        footer.Children.Add(_today);
        root.Children.Add(footer);

        return new Border
        {
            Margin = new Thickness(10),
            Background = Ui.NoteBg,
            BorderBrush = Ui.NoteEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
            Child = root,
        };
    }

    // ---------- visualizzazione ----------
    public void ShowFlyout()
    {
        Refresh();
        if (!IsVisible) Show();
        Reposition();
        Activate();
    }

    public void ToggleFlyout()
    {
        if (IsVisible) { Hide(); return; }
        // il clic sull'icona nella tray ha appena fatto perdere il focus (e quindi chiuso) la finestra
        if ((DateTime.UtcNow - LastHiddenUtc).TotalMilliseconds < 300) return;
        ShowFlyout();
    }

    private void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 4;
        Top = wa.Bottom - ActualHeight - 4;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
        {
            _app.PasteScreenshot();
            e.Handled = true;
        }
    }

    // ---------- stato ----------
    /// <summary>Riallinea l'interfaccia ai dati (a ogni cambio di attività e all'apertura).</summary>
    public void Refresh()
    {
        var cur = Tracker.Current;
        var allCats = Store.GetCategories(true);
        RebuildCategories(allCats.Where(c => c.Active).ToList());

        var clientText = _client.Text;
        _client.ItemsSource = Store.GetClients().Select(c => c.Name).ToList();
        _client.Text = clientText;
        _peoplePick.ItemsSource = Store.GetPeople().Select(p => p.Name).ToList();

        if (_shownActivityId != cur?.Id)
        {
            if (cur is not null) FillDetails(cur);
            else ClearDetails();
        }
        _shownActivityId = cur?.Id;

        _details.IsEnabled = cur is not null;
        _curTitle.IsEnabled = cur is not null;
        _pause.IsEnabled = cur is not null;
        _finish.IsEnabled = cur is not null;

        if (cur is null)
        {
            _curTitle.Text = "Nessuna attività in corso";
            _curMeta.Text = Tracker.Paused.Count > 0 ? "Riprendi un riquadro rosso o avviane una nuova" : "";
            _attach.Text = "";
            _curClosed = TimeSpan.Zero;
        }
        else
        {
            var cat = allCats.FirstOrDefault(c => c.Id == cur.CategoryId)?.Name ?? "?";
            _curMeta.Text = $"{cat} · dalle {cur.StartUtc.ToLocalTime():HH:mm}";
            var n = Store.GetAttachments(cur.Id).Count;
            _attach.Text = n == 0 ? "" : n == 1 ? "📎 1 allegato" : $"📎 {n} allegati";
            _curClosed = TimeSpan.FromTicks(cur.Segments.Where(s => s.EndUtc is not null)
                .Sum(s => s.Duration(DateTime.UtcNow).Ticks));
        }

        RebuildPausedTiles(allCats);

        var today = DateTime.Today;
        var from = DateTime.SpecifyKind(today, DateTimeKind.Local).ToUniversalTime();
        var to = DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Local).ToUniversalTime();
        _doneToday = TimeSpan.FromTicks(Store.GetSegments(from, to)
            .Where(s => s.EndUtc is not null).Sum(s => s.Duration(DateTime.UtcNow).Ticks));

        UpdateClock();
    }

    private void UpdateClock()
    {
        var cur = Tracker.Current;
        var open = cur?.Segments.FirstOrDefault(s => s.EndUtc is null);
        var session = open is null ? TimeSpan.Zero : DateTime.UtcNow - open.StartUtc;
        _elapsed.Text = Ui.Hms(cur is null ? TimeSpan.Zero : _curClosed + session);
        _session.Text = cur is null ? "" : $"totale attività · sessione corrente {Ui.Hms(session)}";
        _today.Text = $"Oggi: {Reporting.FormatHm(_doneToday + session)}";
    }

    private void RebuildPausedTiles(IReadOnlyList<Category> cats)
    {
        _pausedTiles.Children.Clear();
        var paused = Tracker.Paused;
        _pausedSection.Visibility = paused.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (paused.Count == 0) return;

        _pausedLabel.Text = $"In pausa ({paused.Count}) — clic per riprendere";
        foreach (var a in paused) _pausedTiles.Children.Add(BuildTile(a, cats));
    }

    /// <summary>Riquadro rosso di un'attività in pausa: titolo, tempo totale, riprendi / chiudi.</summary>
    private UIElement BuildTile(Activity a, IReadOnlyList<Category> cats)
    {
        var cat = cats.FirstOrDefault(c => c.Id == a.CategoryId)?.Name ?? "?";
        var total = a.Duration(DateTime.UtcNow);
        var since = a.StartUtc.ToLocalTime().Date < DateTime.Today ? $" · dal {a.StartUtc.ToLocalTime():dd/MM}" : "";

        var title = Ui.Text(a.Title, 12.5, true);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        var meta = Ui.Text($"{cat} · {Reporting.FormatHm(total)}{since}", 10.5, fg: Ui.Ink);
        meta.TextTrimming = TextTrimming.CharacterEllipsis;
        meta.TextWrapping = TextWrapping.NoWrap;

        var info = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        info.Children.Add(title);
        info.Children.Add(meta);
        info.MouseLeftButtonUp += (_, _) => Resume(a);

        var actions = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(Ui.Btn("▶", (_, _) => Resume(a), Brushes.White, "Riprendi"));
        actions.Children.Add(Ui.Btn("✔", (_, _) => Tracker.Finish(a.Id), Brushes.White, "Chiudi definitivamente"));

        var body = new StackPanel();
        body.Children.Add(info);
        body.Children.Add(actions);

        return new Border
        {
            Width = 157,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(8),
            Background = PausedTile,
            CornerRadius = new CornerRadius(8),
            ToolTip = a.Title,
            Child = body,
        };
    }

    private void RebuildCategories(IReadOnlyList<Category> cats)
    {
        _cats.Children.Clear();
        foreach (var cat in cats)
        {
            var c = cat;
            var btn = Ui.Btn(c.Name, (_, _) => StartNew(c), Ui.Hex(Blend(c.Color)));
            btn.Margin = new Thickness(2);
            _cats.Children.Add(btn);
        }
    }

    /// <summary>Versione più tenue del colore della categoria.</summary>
    private static string Blend(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        byte Mix(byte v) => (byte)(v + (255 - v) * 0.55);
        return $"#{Mix(c.R):X2}{Mix(c.G):X2}{Mix(c.B):X2}";
    }

    // ---------- azioni ----------
    /// <summary>Crea e avvia una nuova attività; quella in corso va in pausa (riquadro rosso).</summary>
    private void StartNew(Category cat)
    {
        ApplyDetailsToCurrent();
        var title = _newTitle.Text.Trim();
        if (title.Length == 0) title = cat.Name;
        _newTitle.Text = "";
        Tracker.Start(title, cat.Id);
        Dispatcher.BeginInvoke(() => _client.Focus());
    }

    private void Resume(Activity a)
    {
        ApplyDetailsToCurrent();
        Tracker.Resume(a.Id);
    }

    private void OnPause()
    {
        ApplyDetailsToCurrent();
        Tracker.Pause();
    }

    private void OnFinish()
    {
        ApplyDetailsToCurrent();
        Tracker.Finish();
    }

    private void ApplyDetailsToCurrent()
    {
        if (Tracker.Current is not { } cur) return;
        var (clientId, personIds) = ReadDetails();
        var title = _curTitle.Text.Trim();
        if (title.Length > 0) cur.Title = title;
        cur.ClientId = clientId;
        cur.PersonIds = personIds;
        cur.Notes = NullIfEmpty(_notes.Text);
        Tracker.UpdateDetails(cur);
    }

    /// <summary>Legge i campi; crea al volo clienti/colleghi nuovi nel database.</summary>
    private (long? clientId, List<long> personIds) ReadDetails()
    {
        var clientName = _client.Text?.Trim();
        long? clientId = string.IsNullOrEmpty(clientName) ? null : Store.GetOrAddClient(clientName);
        var ids = SplitNames(_people.Text).Select(Store.GetOrAddPerson).ToList();
        return (clientId, ids);
    }

    private void FillDetails(Activity a)
    {
        var clients = Store.GetClients(true);
        var people = Store.GetPeople();
        _curTitle.Text = a.Title;
        _client.Text = clients.FirstOrDefault(c => c.Id == a.ClientId)?.Name ?? "";
        _people.Text = string.Join(", ", a.PersonIds.Select(id => people.FirstOrDefault(p => p.Id == id)?.Name)
            .Where(n => n is not null));
        _notes.Text = a.Notes ?? "";
    }

    private void ClearDetails()
    {
        _client.Text = "";
        _people.Text = "";
        _notes.Text = "";
    }

    private static List<string> SplitNames(string? text) =>
        (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
