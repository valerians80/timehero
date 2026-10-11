using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>La storia di una singola attività: avvio, note con orario, pause, riprese, chiusura.</summary>
public sealed class TimelineWindow : Window
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private readonly TrayApp _app;
    private TimeStore _store => _app.Store;
    private readonly long _activityId;
    private readonly TextBlock _header = Ui.Text("", 15, true);
    private readonly TextBlock _summary = Ui.Text("", 11, fg: Ui.Subtle);
    private readonly DataGrid _grid = new();
    private readonly TextBox _note = Ui.Input();
    private readonly TextBox _time = Ui.Input();
    private readonly TextBlock _error = Ui.Text("", 11, fg: Ui.Danger);
    private List<TimelineEvent> _events = new();

    public TimelineWindow(TrayApp app, long activityId)
    {
        _app = app;
        _activityId = activityId;
        Title = "TimeHero — timeline attività";
        Width = 620;
        Height = 560;
        Background = Ui.Bg;
        Foreground = Ui.Fg;
        FontFamily = Ui.Font;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);

        // la trascrizione arriva da un altro thread: aggiorna la finestra sul thread UI
        Action onTranscribed = () => Dispatcher.BeginInvoke(new Action(Reload));
        _app.Transcriber.Changed += onTranscribed;
        Closed += (_, _) => _app.Transcriber.Changed -= onTranscribed;

        Ui.StyleGrid(_grid);
        _grid.MouseDoubleClick += (_, _) => OpenSelectedTranscript();
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.RowHeight = double.NaN;
        void Col(string header, string path, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = double.IsNaN(width) ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                ElementStyle = WrapStyle(),
            });
        }
        Col("Quando", nameof(Row.When), 120);
        Col("", nameof(Row.Icon), 32);
        Col("Cosa è successo", nameof(Row.Text), double.NaN);

        _time.Width = 70;
        _time.ToolTip = "Orario della nota (oggi), HH:mm";
        _note.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddNote(); };

        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(_header);
        top.Children.Add(_summary);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var add = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(add, Dock.Bottom);
        var addBtn = Ui.Btn("Aggiungi nota", (_, _) => AddNote(), Ui.AccentDim, null, Ui.Accent);
        DockPanel.SetDock(addBtn, Dock.Right);
        DockPanel.SetDock(_time, Dock.Left);
        _time.Margin = new Thickness(0, 0, 6, 0);
        add.Children.Add(addBtn);
        add.Children.Add(_time);
        add.Children.Add(_note);
        root.Children.Add(add);

        var bottomBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bottomBar.Children.Add(Ui.Btn("📄 Apri trascrizione", (_, _) => OpenSelectedTranscript(), tooltip: "Seleziona la riga della trascrizione (o doppio clic)"));
        bottomBar.Children.Add(Ui.Btn("Trascrivi registrazioni", (_, _) => TranscribeAll(),
            tooltip: "Invia ad Azure le registrazioni di questa attività non ancora trascritte (o fallite)"));
        bottomBar.Children.Add(Ui.Btn("Elimina nota", (_, _) => DeleteSelectedNote(), Ui.Danger));
        bottomBar.Children.Add(_error);
        _error.Margin = new Thickness(10, 8, 0, 0);
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        root.Children.Add(bottomBar);

        root.Children.Add(_grid);
        Content = root;
        Reload();
    }

    private static Style WrapStyle()
    {
        var st = new Style(typeof(TextBlock));
        st.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        st.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 5)));
        return st;
    }

    private void Reload()
    {
        var a = _store.GetActivity(_activityId);
        if (a is null) { Close(); return; }
        var notes = _store.GetNotes(a.Id);
        var transcripts = _store.GetTranscripts(a.Id);
        _events = Timeline.Build(a, notes, transcripts);

        var cat = _store.GetCategories(true).FirstOrDefault(c => c.Id == a.CategoryId)?.Name;
        var client = a.ClientId is { } cid ? _store.GetClients(true).FirstOrDefault(c => c.Id == cid)?.Name : null;
        _header.Text = a.Title;
        _summary.Text = string.Join(" · ", new[]
        {
            cat, client,
            $"tempo totale {Reporting.FormatHm(a.Duration())}",
            $"{a.Segments.Count} {(a.Segments.Count == 1 ? "sessione" : "sessioni")}",
            $"{notes.Count} {(notes.Count == 1 ? "nota" : "note")}",
            _store.GetAttachments(a.Id).Count(x => x.Kind == "audio") is var rec and > 0
                ? $"{rec} {(rec == 1 ? "registrazione" : "registrazioni")}"
                : null,
            a.IsClosed ? "chiusa" : a.IsRunning ? "in corso" : "in pausa",
        }.Where(x => !string.IsNullOrEmpty(x)));

        _grid.ItemsSource = _events.Select(e => new Row(e)).ToList();
        _time.Text = DateTime.Now.ToString("HH:mm");
    }

    private void AddNote()
    {
        _error.Foreground = Ui.Danger;
        _error.Text = "";
        var text = _note.Text.Trim();
        if (text.Length == 0) return;
        if (!TimeSpan.TryParseExact(_time.Text.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var t))
        {
            _error.Text = "Orario non valido (HH:mm).";
            return;
        }
        var when = DateTime.SpecifyKind(DateTime.Today + t, DateTimeKind.Local).ToUniversalTime();
        _store.AddNote(_activityId, text, when);
        _note.Text = "";
        Reload();
    }

    private void OpenSelectedTranscript()
    {
        _error.Foreground = Ui.Danger;
        _error.Text = "";
        if (_grid.SelectedItem is not Row { Src.TranscriptId: { } id }) return;
        var t = _store.GetTranscript(id);
        if (t is null) return;
        new TranscriptWindow(t, _store.GetActivity(_activityId)?.Title ?? "Attività").Show();
    }

    /// <summary>Mette in coda le registrazioni dell'attività che non hanno ancora una trascrizione riuscita.</summary>
    private void TranscribeAll()
    {
        _error.Foreground = Ui.Danger;
        _error.Text = "";
        if (!_app.Transcriber.IsConfigured)
        {
            _error.Text = "Configura prima la trascrizione: ⚙ → Trascrizione.";
            return;
        }
        var done = _store.GetTranscripts(_activityId).Where(t => t.Status == "done").Select(t => t.AttachmentId).ToHashSet();
        var todo = _store.GetAttachments(_activityId).Where(a => a.Kind == "audio" && !done.Contains(a.Id)).ToList();
        if (todo.Count == 0) { _error.Text = "Niente da trascrivere: tutte le registrazioni hanno già una trascrizione."; return; }
        foreach (var a in todo) _app.Transcriber.Enqueue(a.Id, _activityId);
        _error.Foreground = Ui.Fg;
        _error.Text = $"In coda: {todo.Count} registrazione/i. Ti avviso quando è pronta.";
    }

    private void DeleteSelectedNote()
    {
        _error.Foreground = Ui.Danger;
        _error.Text = "";
        if (_grid.SelectedItem is not Row { Src.NoteId: { } id })
        {
            _error.Text = "Seleziona una riga di tipo nota.";
            return;
        }
        _store.DeleteNote(id);
        Reload();
    }

    private sealed class Row
    {
        public TimelineEvent Src { get; }
        public Row(TimelineEvent src) => Src = src;
        public string When => Src.WhenUtc.ToLocalTime().ToString("ddd dd/MM HH:mm", It);
        public string Icon => Timeline.KindIcon(Src.Kind);
        public string Text => Src.Text;
    }
}
