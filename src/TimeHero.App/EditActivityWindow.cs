using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Modifica (o inserimento manuale) di un'attività e delle sue sessioni di lavoro.</summary>
public sealed class EditActivityWindow : Window
{
    private static readonly Regex SessionLine = new(
        @"^\s*(\d{1,2}/\d{1,2}/\d{4})\s+(\d{1,2}:\d{2})\s*-\s*(\d{1,2}:\d{2})\s*$", RegexOptions.Compiled);

    private readonly TimeStore _store;
    private readonly Activity _activity;
    private readonly bool _isNew;

    private readonly TextBox _title = Ui.Input();
    private readonly ComboBox _category = new();
    private readonly ComboBox _client = new() { IsEditable = true };
    private readonly TextBox _people = Ui.Input();
    private readonly TextBox _notes = Ui.Input();
    private readonly TextBox _sessions = Ui.Input();
    private readonly CheckBox _billable = new() { Content = "Fatturabile", Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _closed = new() { Content = "Attività chiusa", Margin = new Thickness(0, 4, 0, 0) };
    private readonly ListBox _attachments = new() { MaxHeight = 90, Background = Ui.Surface };
    private readonly TextBlock _error = Ui.Text("", 11, fg: System.Windows.Media.Brushes.Firebrick);

    public bool Saved { get; private set; }

    public EditActivityWindow(TimeStore store, long? activityId, DateTime? defaultDate = null)
    {
        _store = store;
        _isNew = activityId is null;
        _activity = activityId is { } id
            ? store.GetActivity(id)!
            : new Activity { StartUtc = DateTime.UtcNow };

        Title = _isNew ? "Nuova attività" : "Modifica attività";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg;
        Foreground = Ui.Fg;
        FontFamily = Ui.Font;
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);

        var cats = store.GetCategories(true);
        _category.ItemsSource = cats;
        _category.DisplayMemberPath = nameof(Category.Name);
        var clients = store.GetClients(true);
        _client.ItemsSource = clients.Select(c => c.Name).ToList();
        var people = store.GetPeople();

        _title.Text = _activity.Title;
        _category.SelectedItem = cats.FirstOrDefault(c => c.Id == _activity.CategoryId) ?? cats.FirstOrDefault();
        _client.Text = clients.FirstOrDefault(c => c.Id == _activity.ClientId)?.Name ?? "";
        _people.Text = string.Join(", ", _activity.PersonIds.Select(pid => people.FirstOrDefault(p => p.Id == pid)?.Name)
            .Where(n => n is not null));
        _notes.Text = _activity.Notes ?? "";
        _notes.AcceptsReturn = true;
        _notes.Height = 50;
        _notes.TextWrapping = TextWrapping.Wrap;
        _billable.IsChecked = _isNew || _activity.Billable;
        _closed.IsChecked = _isNew || _activity.IsClosed;

        var running = !_isNew && _activity.IsRunning;
        if (running)
        {
            _closed.IsEnabled = false;
            _closed.ToolTip = "L'attività è in corso: mettila in pausa o chiudila dal post-it.";
        }

        // una sessione per riga: "gg/mm/aaaa HH:mm-HH:mm"
        var day = defaultDate ?? DateTime.Today;
        _sessions.Text = _isNew
            ? $"{day:dd/MM/yyyy} {DateTime.Now.AddHours(-1):HH:mm}-{DateTime.Now:HH:mm}"
            : string.Join(Environment.NewLine, _activity.Segments.Where(s => s.EndUtc is not null)
                .Select(s => $"{s.StartUtc.ToLocalTime():dd/MM/yyyy HH:mm}-{s.EndUtc!.Value.ToLocalTime():HH:mm}"));
        _sessions.AcceptsReturn = true;
        _sessions.Height = 78;
        _sessions.TextWrapping = TextWrapping.NoWrap;
        _sessions.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _sessions.FontFamily = new System.Windows.Media.FontFamily("Consolas");

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Ui.Field("Titolo attività", _title));
        root.Children.Add(Ui.Field("Categoria", _category));
        root.Children.Add(Ui.Field("Cliente", _client));
        root.Children.Add(Ui.Field("Colleghi (separati da virgola)", _people));
        root.Children.Add(Ui.Field("Note", _notes));
        root.Children.Add(Ui.Field(
            running ? "Sessioni concluse (una per riga) — la sessione in corso non si modifica qui"
                    : "Sessioni di lavoro (una per riga: gg/mm/aaaa HH:mm-HH:mm)", _sessions));
        root.Children.Add(_billable);
        root.Children.Add(_closed);

        if (!_isNew)
        {
            var atts = store.GetAttachments(_activity.Id);
            if (atts.Count > 0)
            {
                _attachments.ItemsSource = atts.Select(a => a.FilePath).ToList();
                _attachments.MouseDoubleClick += (_, _) =>
                {
                    if (_attachments.SelectedItem is string p && File.Exists(p))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
                };
                root.Children.Add(Ui.Field($"Allegati ({atts.Count}) — doppio clic per aprire", _attachments));
            }
        }

        root.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (!_isNew) buttons.Children.Add(Ui.Btn("Elimina", (_, _) => Delete(), Ui.Danger));
        buttons.Children.Add(Ui.Btn("Annulla", (_, _) => Close()));
        buttons.Children.Add(Ui.Btn("Salva", (_, _) => Save(), Ui.Go));
        root.Children.Add(buttons);
        Content = root;
    }

    private bool TryParseSessions(out List<(DateTime StartUtc, DateTime EndUtc)> result)
    {
        result = new();
        var lines = (_sessions.Text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var m = SessionLine.Match(lines[i]);
            if (!m.Success ||
                !DateTime.TryParseExact(m.Groups[1].Value, "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                !TimeSpan.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var s) ||
                !TimeSpan.TryParse(m.Groups[3].Value, CultureInfo.InvariantCulture, out var e))
            {
                _error.Text = $"Riga {i + 1} non valida: usa gg/mm/aaaa HH:mm-HH:mm";
                return false;
            }
            if (e <= s)
            {
                _error.Text = $"Riga {i + 1}: la fine deve essere dopo l'inizio.";
                return false;
            }
            result.Add((
                DateTime.SpecifyKind(day.Date + s, DateTimeKind.Local).ToUniversalTime(),
                DateTime.SpecifyKind(day.Date + e, DateTimeKind.Local).ToUniversalTime()));
        }
        return true;
    }

    private void Save()
    {
        _error.Text = "";
        if (string.IsNullOrWhiteSpace(_title.Text)) { _error.Text = "Scrivi un titolo."; return; }
        if (_category.SelectedItem is not Category cat) { _error.Text = "Scegli una categoria."; return; }
        if (!TryParseSessions(out var sessions)) return;
        var closed = _closed.IsChecked == true && !_activity.IsRunning;
        if (_isNew && sessions.Count == 0) { _error.Text = "Inserisci almeno una sessione."; return; }

        _activity.Title = _title.Text.Trim();
        _activity.CategoryId = cat.Id;
        var clientName = _client.Text?.Trim();
        _activity.ClientId = string.IsNullOrEmpty(clientName) ? null : _store.GetOrAddClient(clientName);
        _activity.PersonIds = (_people.Text ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(_store.GetOrAddPerson).ToList();
        _activity.Notes = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim();
        _activity.Billable = _billable.IsChecked == true;

        if (_isNew)
        {
            _activity.Segments = sessions.Select(x => new Segment(0, 0, x.StartUtc, x.EndUtc)).ToList();
            _activity.ClosedUtc = closed ? sessions.Max(x => x.EndUtc) : null;
            _store.InsertActivity(_activity);
        }
        else
        {
            _store.SaveActivity(_activity, sessions, closed);
        }
        Saved = true;
        Close();
    }

    private void Delete()
    {
        if (MessageBox.Show(this, "Eliminare definitivamente questa attività con tutte le sue sessioni?", "Conferma",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _store.DeleteActivity(_activity.Id);
        Saved = true;
        Close();
    }
}
