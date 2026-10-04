using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Modifica (o inserimento manuale) di un'attività.</summary>
public sealed class EditActivityWindow : Window
{
    private readonly TimeStore _store;
    private readonly Activity _activity;
    private readonly bool _isNew;

    private readonly DatePicker _date = new();
    private readonly TextBox _start = Ui.Input();
    private readonly TextBox _end = Ui.Input();
    private readonly ComboBox _category = new();
    private readonly ComboBox _client = new() { IsEditable = true };
    private readonly TextBox _people = Ui.Input();
    private readonly TextBox _notes = Ui.Input();
    private readonly CheckBox _billable = new() { Content = "Fatturabile", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _attachments = new() { MaxHeight = 90, Background = Ui.Panel };
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
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.NoteBg;

        var cats = store.GetCategories(true);
        _category.ItemsSource = cats;
        _category.DisplayMemberPath = nameof(Category.Name);
        var clients = store.GetClients(true);
        _client.ItemsSource = clients.Select(c => c.Name).ToList();
        var people = store.GetPeople();

        var local = _activity.StartUtc.ToLocalTime();
        _date.SelectedDate = _isNew ? (defaultDate ?? DateTime.Today) : local.Date;
        _start.Text = _isNew ? DateTime.Now.AddHours(-1).ToString("HH:mm") : local.ToString("HH:mm");
        _end.Text = _isNew ? DateTime.Now.ToString("HH:mm") : _activity.EndUtc?.ToLocalTime().ToString("HH:mm") ?? "";
        _category.SelectedItem = cats.FirstOrDefault(c => c.Id == _activity.CategoryId) ?? cats.FirstOrDefault();
        _client.Text = clients.FirstOrDefault(c => c.Id == _activity.ClientId)?.Name ?? "";
        _people.Text = string.Join(", ", _activity.PersonIds.Select(pid => people.FirstOrDefault(p => p.Id == pid)?.Name)
            .Where(n => n is not null));
        _notes.Text = _activity.Notes ?? "";
        _notes.AcceptsReturn = true;
        _notes.Height = 56;
        _notes.TextWrapping = TextWrapping.Wrap;
        _billable.IsChecked = _activity.Billable;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Ui.Field("Giorno", _date));
        var times = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        times.Children.Add(Ui.Field("Inizio (HH:mm)", _start));
        times.Children.Add(Ui.Field("Fine (vuoto = in corso)", _end));
        root.Children.Add(times);
        root.Children.Add(Ui.Field("Categoria", _category));
        root.Children.Add(Ui.Field("Cliente", _client));
        root.Children.Add(Ui.Field("Colleghi (separati da virgola)", _people));
        root.Children.Add(Ui.Field("Note", _notes));
        root.Children.Add(_billable);

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

    private void Save()
    {
        if (_date.SelectedDate is not { } day) { _error.Text = "Scegli un giorno."; return; }
        if (!TimeSpan.TryParseExact(_start.Text.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var s))
        { _error.Text = "Ora di inizio non valida (HH:mm)."; return; }

        TimeSpan? e = null;
        if (!string.IsNullOrWhiteSpace(_end.Text))
        {
            if (!TimeSpan.TryParseExact(_end.Text.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var ee))
            { _error.Text = "Ora di fine non valida (HH:mm)."; return; }
            if (ee <= s) { _error.Text = "La fine deve essere dopo l'inizio."; return; }
            e = ee;
        }
        if (_category.SelectedItem is not Category cat) { _error.Text = "Scegli una categoria."; return; }

        _activity.StartUtc = DateTime.SpecifyKind(day.Date + s, DateTimeKind.Local).ToUniversalTime();
        _activity.EndUtc = e is { } end ? DateTime.SpecifyKind(day.Date + end, DateTimeKind.Local).ToUniversalTime() : null;
        _activity.CategoryId = cat.Id;
        var clientName = _client.Text?.Trim();
        _activity.ClientId = string.IsNullOrEmpty(clientName) ? null : _store.GetOrAddClient(clientName);
        _activity.PersonIds = (_people.Text ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(_store.GetOrAddPerson).ToList();
        _activity.Notes = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim();
        _activity.Billable = _billable.IsChecked == true;

        if (_isNew) _store.InsertActivity(_activity);
        else _store.UpdateActivity(_activity);
        Saved = true;
        Close();
    }

    private void Delete()
    {
        if (MessageBox.Show(this, "Eliminare definitivamente questa attività?", "Conferma",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _store.DeleteActivity(_activity.Id);
        Saved = true;
        Close();
    }
}
