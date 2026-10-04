using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Il "post-it": finestra senza bordi ancorata sopra la taskbar, in basso a destra.</summary>
public sealed class FlyoutWindow : Window
{
    private readonly TrayApp _app;
    private TimeStore Store => _app.Store;
    private ActivityTracker Tracker => _app.Tracker;

    private readonly TextBlock _title = Ui.Text("Nessuna attività", 15, true);
    private readonly TextBlock _elapsed = Ui.Text("00:00:00", 26, true);
    private readonly TextBlock _since = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly TextBlock _attach = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly TextBlock _today = Ui.Text("Oggi: 0h 00m", 12, true);
    private readonly Button _pause;
    private readonly Button _stop;
    private readonly ComboBox _client = new() { IsEditable = true, FontSize = 12 };
    private readonly ComboBox _peoplePick = new() { FontSize = 12, ToolTip = "Aggiungi un collega dall'elenco" };
    private readonly TextBox _people = Ui.Input();
    private readonly TextBox _notes = Ui.Input();
    private readonly UniformGrid _cats = new() { Columns = 2 };
    private readonly ToggleButton _pin;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private long? _shownActivityId;
    private long? _pausedCategoryId;
    private TimeSpan _doneToday;

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
        Width = 340;
        ShowActivated = true;
        Title = "TimeHero";

        _pause = Ui.Btn("⏸ Pausa", (_, _) => OnPauseResume());
        _stop = Ui.Btn("⏹ Fine", (_, _) => OnStop(), Ui.Danger);
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

        // attività corrente
        var current = new Border
        {
            Background = Ui.Panel,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 4, 0, 6),
        };
        var cp = new StackPanel();
        cp.Children.Add(_title);
        cp.Children.Add(_elapsed);
        cp.Children.Add(_since);
        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_pause);
        buttons.Children.Add(_stop);
        cp.Children.Add(buttons);
        var shots = new UniformGrid { Columns = 2 };
        shots.Children.Add(Ui.Btn("📎 Screenshot", async (_, _) => await _app.CaptureScreenshotAsync(),
            tooltip: "Ritaglia una parte dello schermo e allegala all'attività in corso"));
        shots.Children.Add(Ui.Btn("📋 Incolla", (_, _) => _app.PasteScreenshot(),
            tooltip: "Allega l'immagine negli appunti (anche Ctrl+V)"));
        cp.Children.Add(shots);
        cp.Children.Add(_attach);
        current.Child = cp;
        root.Children.Add(current);

        // dettagli
        root.Children.Add(Ui.Field("Cliente", _client));
        var peopleRow = new DockPanel();
        _peoplePick.Width = 90;
        DockPanel.SetDock(_peoplePick, Dock.Right);
        peopleRow.Children.Add(_peoplePick);
        peopleRow.Children.Add(_people);
        root.Children.Add(Ui.Field("Colleghi (separati da virgola)", peopleRow));
        root.Children.Add(Ui.Field("Note", _notes));

        // categorie
        var catsLabel = Ui.Text("Avvia attività", 10.5, fg: Ui.Subtle);
        catsLabel.Margin = new Thickness(0, 10, 0, 2);
        root.Children.Add(catsLabel);
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
    /// <summary>Riallinea l'interfaccia ai dati (chiamato a ogni cambio di attività e all'apertura).</summary>
    public void Refresh()
    {
        var cur = Tracker.Current;
        var cats = Store.GetCategories();
        RebuildCategories(cats, cur?.CategoryId);

        var clientText = _client.Text;
        _client.ItemsSource = Store.GetClients().Select(c => c.Name).ToList();
        _client.Text = clientText;
        _peoplePick.ItemsSource = Store.GetPeople().Select(p => p.Name).ToList();

        if (cur is not null && _shownActivityId != cur.Id) FillDetails(cur);
        _shownActivityId = cur?.Id;

        var today = DateTime.Today;
        var from = DateTime.SpecifyKind(today, DateTimeKind.Local).ToUniversalTime();
        var to = DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Local).ToUniversalTime();
        _doneToday = TimeSpan.FromTicks(Store.GetActivities(from, to)
            .Where(a => !a.IsRunning).Sum(a => a.Duration().Ticks));

        if (cur is null)
        {
            _title.Text = _pausedCategoryId is { } pid
                ? $"In pausa: {cats.FirstOrDefault(c => c.Id == pid)?.Name}"
                : "Nessuna attività";
            _since.Text = "";
            _attach.Text = "";
        }
        else
        {
            var cat = cats.FirstOrDefault(c => c.Id == cur.CategoryId)?.Name ?? "?";
            _title.Text = cat;
            _since.Text = $"dalle {cur.StartUtc.ToLocalTime():HH:mm}";
            var n = Store.GetAttachments(cur.Id).Count;
            _attach.Text = n == 0 ? "" : n == 1 ? "📎 1 allegato" : $"📎 {n} allegati";
        }

        _pause.Content = cur is null ? "▶ Riprendi" : "⏸ Pausa";
        _pause.IsEnabled = cur is not null || _pausedCategoryId is not null;
        _stop.IsEnabled = cur is not null || _pausedCategoryId is not null;
        UpdateClock();
    }

    private void UpdateClock()
    {
        var cur = Tracker.Current;
        var running = cur?.Duration() ?? TimeSpan.Zero;
        _elapsed.Text = Ui.Hms(running);
        _today.Text = $"Oggi: {Reporting.FormatHm(_doneToday + running)}";
    }

    private void RebuildCategories(IReadOnlyList<Category> cats, long? activeId)
    {
        _cats.Children.Clear();
        foreach (var cat in cats)
        {
            var c = cat;
            var isActive = c.Id == activeId;
            var btn = Ui.Btn(isActive ? "● " + c.Name : c.Name, (_, _) => StartCategory(c.Id),
                Ui.Hex(isActive ? c.Color : Blend(c.Color)));
            btn.FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal;
            btn.Margin = new Thickness(2);
            _cats.Children.Add(btn);
        }
    }

    /// <summary>Versione più tenue del colore della categoria (per i pulsanti non attivi).</summary>
    private static string Blend(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        byte Mix(byte v) => (byte)(v + (255 - v) * 0.55);
        return $"#{Mix(c.R):X2}{Mix(c.G):X2}{Mix(c.B):X2}";
    }

    // ---------- azioni ----------
    private void StartCategory(long categoryId)
    {
        var (clientId, personIds) = ReadDetails();
        var notes = Tracker.Current is null ? NullIfEmpty(_notes.Text) : null;
        _pausedCategoryId = null;
        _shownActivityId = null; // forza il riallineamento dei campi
        Tracker.Start(categoryId, clientId, personIds, notes);
        // il cliente e i colleghi restano nei campi (stesso cliente, altro tipo di lavoro); le note no
        if (notes is null) _notes.Text = "";
    }

    private void OnPauseResume()
    {
        if (Tracker.Current is { } cur)
        {
            ApplyDetailsToCurrent();
            _pausedCategoryId = cur.CategoryId;
            Tracker.Stop();
        }
        else if (_pausedCategoryId is { } id)
        {
            StartCategory(id);
        }
    }

    private void OnStop()
    {
        ApplyDetailsToCurrent();
        _pausedCategoryId = null;
        Tracker.Stop();
        ClearDetails();
        Refresh();
    }

    private void ApplyDetailsToCurrent()
    {
        if (Tracker.Current is null) return;
        var (clientId, personIds) = ReadDetails();
        Tracker.UpdateCurrent(clientId, personIds, NullIfEmpty(_notes.Text));
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
