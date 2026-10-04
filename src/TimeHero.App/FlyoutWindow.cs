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
    private readonly TrayApp _app;
    private TimeStore Store => _app.Store;
    private ActivityTracker Tracker => _app.Tracker;

    // attività in corso
    private readonly TextBox _curTitle = new()
    {
        FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Fg,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(0, 0, 0, 2), ToolTip = "Titolo dell'attività (modificabile)",
    };
    private readonly TextBlock _curMeta = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly TextBlock _elapsed = Ui.Text("00:00:00", 26, true);
    private readonly TextBlock _session = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly TextBlock _attach = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly TextBox _logBox = Ui.Input();
    private readonly TextBlock _logHint = new()
    {
        Text = "Aggiungi una nota con orario…", FontSize = 12, Foreground = Ui.Subtle,
        IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
    };
    private readonly TextBlock _lastNote = Ui.Text("", 10.5, fg: Ui.Subtle);
    private readonly Button _pause;
    private readonly Button _finish;
    private readonly Button _editBtn;
    private readonly Button _timelineBtn;
    private readonly StackPanel _runningBlock = new();
    private readonly ComboBox _client = new() { IsEditable = true, FontSize = 12 };
    private readonly ComboBox _peoplePick = new() { FontSize = 12, ToolTip = "Aggiungi un collega dall'elenco" };
    private readonly TextBox _people = Ui.Input();
    private readonly TextBox _notes = Ui.Input();

    // attività in pausa (riquadri rossi)
    private readonly StackPanel _pausedSection = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _pausedLabel = Ui.Text("", 10.5, fg: Ui.Subtle);
    private readonly WrapPanel _pausedTiles = new();

    // pannello "nuova attività" / "dettagli" (nascosto finché non serve)
    private enum PanelMode { None, New, Edit }
    private PanelMode _mode = PanelMode.None;
    private Button _newBtn = null!;
    private readonly Border _form = new();
    private readonly TextBlock _formTitle = Ui.Text("", 12, true);
    private readonly TextBox _newTitle = Ui.Input();
    private StackPanel _titleField = null!;
    private readonly StackPanel _catsBlock = new();
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
        FontFamily = Ui.Font;
        Foreground = Ui.Fg;

        _pause = Ui.Btn("⏸ Pausa", (_, _) => OnPause(), tooltip: "Metti in pausa: resta aperta e la ritrovi nei riquadri rossi");
        _finish = Ui.Btn("✔ Fine", (_, _) => OnFinish(), Ui.Go, "Chiudi definitivamente l'attività");
        _editBtn = Ui.Btn("✎", (_, _) => ToggleEdit(), Brushes.Transparent, "Cliente, colleghi e note dell'attività in corso");
        _timelineBtn = Ui.Btn("🕘", (_, _) =>
        {
            if (Tracker.Current is { } cur) _app.ShowTimeline(cur.Id);
        }, Brushes.Transparent, "Timeline dell'attività: avvio, note, pause e riprese");
        _pin = new ToggleButton
        {
            Content = "📌",
            ToolTip = "Tieni aperto",
            Foreground = Ui.Fg,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            Cursor = Cursors.Hand,
        };

        Content = BuildUi();
        SetMode(PanelMode.None);

        _peoplePick.SelectionChanged += (_, _) =>
        {
            if (_peoplePick.SelectedItem is not string name) return;
            var names = SplitNames(_people.Text);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            _people.Text = string.Join(", ", names);
            _peoplePick.SelectedIndex = -1;
            ApplyCurrent();
        };
        _logBox.TextChanged += (_, _) =>
            _logHint.Visibility = _logBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _logBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddLog(); e.Handled = true; } };
        _curTitle.LostFocus += (_, _) => ApplyCurrent();
        _client.LostFocus += (_, _) => ApplyCurrent();
        _client.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyCurrent);
        _people.LostFocus += (_, _) => ApplyCurrent();
        _notes.LostFocus += (_, _) => ApplyCurrent();

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
            Background = Ui.Surface,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 6, 0, 8),
        };
        var cp = new StackPanel();
        var titleRow = new DockPanel();
        var titleTools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(titleTools, Dock.Right);
        titleTools.Children.Add(_timelineBtn);
        titleTools.Children.Add(_editBtn);
        titleRow.Children.Add(titleTools);
        titleRow.Children.Add(_curTitle);
        cp.Children.Add(titleRow);
        cp.Children.Add(_curMeta);

        // visibile solo con un'attività in corso: tempo, pausa/fine, screenshot
        _runningBlock.Children.Add(_elapsed);
        _runningBlock.Children.Add(_session);

        // diario: una nota con orario (Invio), subito sotto il cronometro
        var logRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var logAdd = Ui.Btn("＋", (_, _) => AddLog(), Ui.AccentDim, "Aggiungi la nota alla timeline dell'attività", Ui.Accent);
        DockPanel.SetDock(logAdd, Dock.Right);
        logRow.Children.Add(logAdd);
        var logField = new Grid();
        logField.Children.Add(_logBox);
        logField.Children.Add(_logHint);
        logRow.Children.Add(logField);
        _runningBlock.Children.Add(logRow);
        _lastNote.TextTrimming = TextTrimming.CharacterEllipsis;
        _lastNote.TextWrapping = TextWrapping.NoWrap;
        _lastNote.Margin = new Thickness(2, 3, 0, 0);
        _runningBlock.Children.Add(_lastNote);
        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_pause);
        buttons.Children.Add(_finish);
        _runningBlock.Children.Add(buttons);
        var shots = new UniformGrid { Columns = 2 };
        shots.Children.Add(Ui.Btn("📎 Screenshot", async (_, _) => await _app.CaptureScreenshotAsync(),
            tooltip: "Ritaglia una parte dello schermo e allegala all'attività in corso"));
        shots.Children.Add(Ui.Btn("📋 Incolla", (_, _) => _app.PasteScreenshot(),
            tooltip: "Allega l'immagine negli appunti (anche Ctrl+V)"));
        _runningBlock.Children.Add(shots);
        _runningBlock.Children.Add(_attach);
        cp.Children.Add(_runningBlock);
        current.Child = cp;
        root.Children.Add(current);

        // attività in pausa
        _pausedSection.Children.Add(_pausedLabel);
        _pausedSection.Children.Add(_pausedTiles);
        root.Children.Add(_pausedSection);

        // pulsante "nuova attività"
        _newBtn = Ui.Btn("＋ Nuova attività", (_, _) => ToggleNew(), Ui.AccentDim, null, Ui.Accent);
        _newBtn.Margin = new Thickness(0, 2, 0, 0);
        _newBtn.Padding = new Thickness(10, 9, 10, 9);
        _newBtn.FontSize = 13;
        root.Children.Add(_newBtn);

        // pannello nascosto: titolo, cliente, colleghi, note e tipo (si apre con "nuova attività" o ✎)
        var form = new StackPanel();
        form.Children.Add(_formTitle);
        _newTitle.ToolTip = "Es. \"Deploy VM per Contoso\". Se lo lasci vuoto si usa il nome del tipo.";
        _titleField = Ui.Field("Titolo", _newTitle);
        form.Children.Add(_titleField);
        form.Children.Add(Ui.Field("Cliente", _client));
        var peopleRow = new DockPanel();
        _peoplePick.Width = 90;
        DockPanel.SetDock(_peoplePick, Dock.Right);
        peopleRow.Children.Add(_peoplePick);
        peopleRow.Children.Add(_people);
        form.Children.Add(Ui.Field("Colleghi (separati da virgola)", peopleRow));
        form.Children.Add(Ui.Field("Note", _notes));
        var catsLabel = Ui.Text("Scegli il tipo per avviarla", 10.5, fg: Ui.Subtle);
        catsLabel.Margin = new Thickness(2, 10, 0, 3);
        _catsBlock.Children.Add(catsLabel);
        _catsBlock.Children.Add(_cats);
        form.Children.Add(_catsBlock);

        _form.Child = form;
        _form.Background = Ui.Surface;
        _form.BorderBrush = Ui.Border;
        _form.BorderThickness = new Thickness(1);
        _form.CornerRadius = new CornerRadius(12);
        _form.Padding = new Thickness(12);
        _form.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_form);

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
            Background = Ui.Bg,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.55 },
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
            SetMode(PanelMode.None); // cambiata l'attività: il pannello si richiude
            _logBox.Text = "";
        }
        _shownActivityId = cur?.Id;

        _curTitle.IsEnabled = cur is not null;
        _runningBlock.Visibility = cur is null ? Visibility.Collapsed : Visibility.Visible;
        _editBtn.Visibility = cur is null ? Visibility.Collapsed : Visibility.Visible;
        _timelineBtn.Visibility = _editBtn.Visibility;

        if (cur is null)
        {
            _curTitle.Text = "Nessuna attività in corso";
            _curMeta.Text = Tracker.Paused.Count > 0
                ? "Riprendi un riquadro rosso o avviane una nuova"
                : "Avvia la prima attività con il pulsante qui sotto";
            _attach.Text = "";
            _lastNote.Visibility = Visibility.Collapsed;
            _curClosed = TimeSpan.Zero;
        }
        else
        {
            var cat = allCats.FirstOrDefault(c => c.Id == cur.CategoryId)?.Name ?? "?";
            _curMeta.Text = $"{cat} · dalle {cur.StartUtc.ToLocalTime():HH:mm}";
            var n = Store.GetAttachments(cur.Id).Count;
            _attach.Text = n == 0 ? "" : n == 1 ? "📎 1 allegato" : $"📎 {n} allegati";
            ShowLastNote(Store.GetNotes(cur.Id));
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

        var title = Ui.Text(a.Title, 12.5, true, Brushes.White);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        var meta = Ui.Text($"{cat} · {Reporting.FormatHm(total)}{since}", 10.5, fg: Ui.Hex("#FFE4E1"));
        meta.TextTrimming = TextTrimming.CharacterEllipsis;
        meta.TextWrapping = TextWrapping.NoWrap;

        var info = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        info.Children.Add(title);
        info.Children.Add(meta);
        info.MouseLeftButtonUp += (_, _) => Resume(a);

        var actions = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(Ui.Btn("▶", (_, _) => Resume(a), Ui.Hex("#33FFFFFF"), "Riprendi", Brushes.Transparent));
        actions.Children.Add(Ui.Btn("✔", (_, _) => Tracker.Finish(a.Id), Ui.Hex("#33FFFFFF"), "Chiudi definitivamente", Brushes.Transparent));

        var body = new StackPanel();
        body.Children.Add(info);
        body.Children.Add(actions);

        return new Border
        {
            Width = 160,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(8),
            Background = Ui.Danger,
            CornerRadius = new CornerRadius(10),
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
            _cats.Children.Add(Ui.CategoryBtn(c.Name, c.Color, (_, _) => StartNew(c)));
        }
    }

    // ---------- azioni ----------
    private void SetMode(PanelMode mode)
    {
        _mode = mode;
        _form.Visibility = mode == PanelMode.None ? Visibility.Collapsed : Visibility.Visible;
        _titleField.Visibility = mode == PanelMode.New ? Visibility.Visible : Visibility.Collapsed;
        _catsBlock.Visibility = mode == PanelMode.New ? Visibility.Visible : Visibility.Collapsed;
        _formTitle.Text = mode == PanelMode.New ? "Nuova attività" : "Dettagli dell'attività in corso";
        _newBtn.Content = mode == PanelMode.New ? "✕ Annulla" : "＋ Nuova attività";
        _editBtn.Content = mode == PanelMode.Edit ? "✕" : "✎";
    }

    private void ToggleNew()
    {
        if (_mode == PanelMode.New) { SetMode(PanelMode.None); return; }
        ApplyCurrent();
        ClearDetails();
        _newTitle.Text = "";
        SetMode(PanelMode.New);
        Dispatcher.BeginInvoke(() => _newTitle.Focus());
    }

    private void ToggleEdit()
    {
        if (_mode == PanelMode.Edit)
        {
            ApplyCurrent();
            SetMode(PanelMode.None);
            return;
        }
        if (Tracker.Current is not { } cur) return;
        FillDetails(cur);
        SetMode(PanelMode.Edit);
    }

    /// <summary>Avvia una nuova attività con i dati del pannello; quella in corso va in pausa (riquadro rosso).</summary>
    private void StartNew(Category cat)
    {
        ApplyCurrent();
        var title = _newTitle.Text.Trim();
        if (title.Length == 0) title = cat.Name;
        var (clientId, personIds) = ReadDetails();
        var notes = NullIfEmpty(_notes.Text);
        _newTitle.Text = "";
        Tracker.Start(title, cat.Id, clientId, personIds, notes); // Refresh richiude il pannello
    }

    private void Resume(Activity a)
    {
        ApplyCurrent();
        Tracker.Resume(a.Id);
    }

    private void OnPause()
    {
        ApplyCurrent();
        Tracker.Pause();
    }

    private void OnFinish()
    {
        ApplyCurrent();
        Tracker.Finish();
    }

    private void AddLog()
    {
        var text = _logBox.Text.Trim();
        if (text.Length == 0) return;
        if (Tracker.AddNote(text) is null) return;
        _logBox.Text = "";
        if (Tracker.Current is { } cur) ShowLastNote(Store.GetNotes(cur.Id));
        _logBox.Focus();
    }

    private void ShowLastNote(IReadOnlyList<ActivityNote> notes)
    {
        if (notes.Count == 0) { _lastNote.Visibility = Visibility.Collapsed; return; }
        var last = notes[^1];
        _lastNote.Text = $"📝 {notes.Count}  ·  ultima {last.CreatedUtc.ToLocalTime():HH:mm} — {last.Text}";
        _lastNote.ToolTip = last.Text;
        _lastNote.Visibility = Visibility.Visible;
    }

    /// <summary>Salva il titolo (sempre) e, se il pannello dettagli è aperto, cliente/colleghi/note dell'attività in corso.</summary>
    private void ApplyCurrent()
    {
        if (Tracker.Current is not { } cur) return;
        var title = _curTitle.Text.Trim();
        if (title.Length > 0) cur.Title = title;
        if (_mode == PanelMode.Edit)
        {
            var (clientId, personIds) = ReadDetails();
            cur.ClientId = clientId;
            cur.PersonIds = personIds;
            cur.Notes = NullIfEmpty(_notes.Text);
        }
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
