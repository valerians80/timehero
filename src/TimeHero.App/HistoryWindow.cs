using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Storico delle attività, totali e strumenti per compilare il timesheet.</summary>
public sealed class HistoryWindow : Window
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private readonly TrayApp _app;
    private TimeStore Store => _app.Store;

    private readonly DatePicker _from = new() { SelectedDate = DateTime.Today, Foreground = Ui.Fg, Margin = new Thickness(0, 2, 0, 0) };
    private readonly DatePicker _to = new() { SelectedDate = DateTime.Today, Foreground = Ui.Fg, Margin = new Thickness(0, 2, 0, 0) };
    private readonly ComboBox _round = new() { Width = 100, Margin = new Thickness(0, 2, 0, 0) };
    private readonly DataGrid _grid = new();
    private Button _deleteBtn = null!;
    private CheckBox _selectAll = null!;
    private readonly ListBox _byClient = new();
    private readonly ListBox _byActivity = new();
    private readonly ListBox _byCategory = new();
    private readonly ListBox _byDay = new();
    private readonly TextBlock _total = Ui.Text("", 14, true);
    private List<TimesheetRow> _rows = new();
    private bool _loading;

    private static readonly int[] RoundOptions = { 0, 5, 10, 15, 30 };

    public HistoryWindow(TrayApp app)
    {
        _app = app;
        Title = "TimeHero — storico e timesheet";
        Width = 1240;
        Height = 720;
        Background = Ui.Bg;
        Foreground = Ui.Fg;
        FontFamily = Ui.Font;
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _round.ItemsSource = RoundOptions.Select(m => m == 0 ? "Nessuno" : $"{m} min").ToList();
        _round.SelectedIndex = Math.Max(0, Array.IndexOf(RoundOptions, app.Settings.RoundMinutes));
        _round.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _app.Settings.RoundMinutes = RoundMinutes;
            Reload();
        };
        _from.SelectedDateChanged += (_, _) => Reload();
        _to.SelectedDateChanged += (_, _) => Reload();

        BuildGrid();

        var root = new DockPanel { Margin = new Thickness(12) };

        // barra superiore
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(Ui.Btn("Oggi", (_, _) => SetRange(DateTime.Today, DateTime.Today)));
        top.Children.Add(Ui.Btn("Ieri", (_, _) => SetRange(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-1))));
        top.Children.Add(Ui.Btn("Questa settimana", (_, _) => { var m = Monday(DateTime.Today); SetRange(m, m.AddDays(6)); }));
        top.Children.Add(Ui.Btn("Settimana scorsa", (_, _) => { var m = Monday(DateTime.Today).AddDays(-7); SetRange(m, m.AddDays(6)); }));
        top.Children.Add(Ui.Btn("Questo mese", (_, _) =>
        {
            var f = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            SetRange(f, f.AddMonths(1).AddDays(-1));
        }));
        top.Children.Add(Pad(Ui.Text("Dal", 12), 12));
        top.Children.Add(_from);
        top.Children.Add(Pad(Ui.Text("al", 12), 6));
        top.Children.Add(_to);
        top.Children.Add(Pad(Ui.Text("Arrotonda a", 12), 12));
        top.Children.Add(_round);
        root.Children.Add(top);

        // barra inferiore
        var bottom = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(Ui.Btn("+ Aggiungi", (_, _) => AddManual(), Ui.Go));
        bottom.Children.Add(Ui.Btn("Modifica", (_, _) => EditSelected()));
        bottom.Children.Add(Ui.Btn("🕘 Timeline", (_, _) =>
        {
            if (_grid.SelectedItem is HistRow row) _app.ShowTimeline(row.Src.ActivityId);
        }, tooltip: "Storia dell'attività selezionata: avvio, note con orario, pause e riprese"));
        bottom.Children.Add(Ui.Btn("📋 Copia riepilogo", (_, _) => CopySummary(), tooltip: "Riepilogo per giorno/cliente/categoria da incollare nel timesheet"));
        bottom.Children.Add(Ui.Btn("💾 Esporta CSV", (_, _) => ExportCsv()));
        _deleteBtn = Ui.Btn("🗑 Elimina selezionate (0)", (_, _) => DeleteChecked(), Ui.Danger,
            "Elimina in una volta le attività spuntate nella prima colonna");
        _deleteBtn.IsEnabled = false;
        bottom.Children.Add(_deleteBtn);
        root.Children.Add(bottom);

        // riepiloghi a destra
        var side = new StackPanel { Width = 270, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(side, Dock.Right);
        side.Children.Add(_total);
        side.Children.Add(Section("Per attività", _byActivity));
        side.Children.Add(Section("Per cliente", _byClient));
        side.Children.Add(Section("Per categoria", _byCategory));
        side.Children.Add(Section("Per giorno", _byDay));
        root.Children.Add(side);

        root.Children.Add(_grid);
        Content = root;
    }

    private int RoundMinutes => RoundOptions[Math.Max(0, _round.SelectedIndex)];

    private static FrameworkElement Pad(FrameworkElement e, double left)
    {
        e.Margin = new Thickness(left, 6, 4, 0);
        return e;
    }

    private static StackPanel Section(string title, ListBox list)
    {
        list.Height = 88;
        list.Background = Ui.Surface;
        list.BorderBrush = Ui.Border;
        var p = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(Ui.Text(title, 11, true, Ui.Subtle));
        p.Children.Add(list);
        return p;
    }

    private static DateTime Monday(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private void SetRange(DateTime from, DateTime to)
    {
        _loading = true;
        _from.SelectedDate = from;
        _to.SelectedDate = to;
        _loading = false;
        Reload();
    }

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        Ui.StyleGrid(_grid);
        _grid.MouseDoubleClick += (_, _) => EditSelected();

        void Col(string header, string path, double width = double.NaN)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = double.IsNaN(width) ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
            });
        }
        // prima colonna: casella di selezione per riga, e una nell'intestazione per selezionarle tutte
        _selectAll = new CheckBox { ToolTip = "Seleziona / deseleziona tutte", HorizontalAlignment = HorizontalAlignment.Center };
        _selectAll.Click += (_, _) =>
        {
            // lo stato si legge una volta sola: mentre scorro le righe la casella dell'intestazione si riallinea da sola
            var on = _selectAll.IsChecked == true;
            foreach (var r in Rows().ToList()) r.Checked = on;
        };
        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(HistRow.Checked))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        box.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        box.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = _selectAll,
            Width = new DataGridLength(34),
            CanUserSort = false,
            CanUserResize = false,
            CellTemplate = new DataTemplate { VisualTree = box },
        });

        Col("Giorno", nameof(HistRow.Day), 90);
        Col("Inizio", nameof(HistRow.Start), 55);
        Col("Fine", nameof(HistRow.End), 65);
        Col("Durata", nameof(HistRow.Duration), 60);
        Col("Arrot.", nameof(HistRow.Rounded), 60);
        Col("Stato", nameof(HistRow.Status), 65);
        Col("Attività", nameof(HistRow.Title), 170);
        Col("Categoria", nameof(HistRow.Category), 120);
        Col("Cliente", nameof(HistRow.Client), 120);
        Col("Colleghi", nameof(HistRow.People), 120);
        Col("Note", nameof(HistRow.Notes));
        Col("📎", nameof(HistRow.Attachments), 35);
    }

    public void Reload()
    {
        var from = _from.SelectedDate ?? DateTime.Today;
        var to = _to.SelectedDate ?? from;
        if (to < from) (from, to) = (to, from);

        _rows = Reporting.BuildRows(Store, from, to.AddDays(1));
        var round = RoundMinutes;
        _grid.ItemsSource = _rows.Select(r => new HistRow(r, round, OnCheckedChanged)).ToList();
        _selectAll.IsChecked = false;
        OnCheckedChanged();

        var total = TimeSpan.FromTicks(_rows.Sum(r => r.Duration.Ticks));
        _total.Text = $"Totale: {Reporting.FormatHm(total)}";

        static List<string> Lines(IEnumerable<SummaryLine> s) =>
            s.Select(l => $"{l.Key} — {Reporting.FormatHm(l.Total)}").ToList();
        _byActivity.ItemsSource = Lines(Reporting.SummarizeBy(_rows, r => r.Title, round));
        _byClient.ItemsSource = Lines(Reporting.SummarizeBy(_rows, r => r.Client ?? "(nessun cliente)", round));
        _byCategory.ItemsSource = Lines(Reporting.SummarizeBy(_rows, r => r.Category, round));
        _byDay.ItemsSource = Reporting.SummarizeBy(_rows, r => r.Date.ToString("yyyy-MM-dd"), round)
            .OrderBy(l => l.Key)
            .Select(l => $"{DateTime.ParseExact(l.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("ddd dd/MM", It)} — {Reporting.FormatHm(l.Total)}")
            .ToList();
    }

    private IEnumerable<HistRow> Rows() => (_grid.ItemsSource as IEnumerable<HistRow>) ?? Enumerable.Empty<HistRow>();

    private void OnCheckedChanged()
    {
        var rows = Rows().ToList();
        var n = rows.Count(r => r.Checked);
        _deleteBtn.Content = $"🗑 Elimina selezionate ({n})";
        _deleteBtn.IsEnabled = n > 0;
        // la casella dell'intestazione segue lo stato delle righe (nessuna / tutte / alcune)
        _selectAll.IsThreeState = false;
        _selectAll.IsChecked = rows.Count > 0 && n == rows.Count;
    }

    /// <summary>Elimina in blocco le attività spuntate (con sessioni, note e trascrizioni). I file su disco restano.</summary>
    private void DeleteChecked()
    {
        var ids = Rows().Where(r => r.Checked).Select(r => r.Src.ActivityId).Distinct().ToList();
        if (ids.Count == 0) return;

        var activities = ids.Select(Store.GetActivity).Where(a => a is not null).Select(a => a!).ToList();
        var msg = new System.Text.StringBuilder();
        msg.AppendLine(ids.Count == 1
            ? "Eliminare l'attività selezionata?"
            : $"Eliminare le {ids.Count} attività selezionate?");
        msg.AppendLine();
        msg.AppendLine("Verranno rimossi anche sessioni, note e trascrizioni. L'operazione non si può annullare.");
        msg.AppendLine("I file audio e gli screenshot sul disco non vengono cancellati.");

        var multiDay = activities.Count(a => a.Segments.Select(s => s.StartUtc.ToLocalTime().Date).Distinct().Count() > 1);
        if (multiDay > 0)
        {
            msg.AppendLine();
            msg.AppendLine(multiDay == 1
                ? "Attenzione: 1 attività ha sessioni anche in altri giorni e verrà eliminata per intero."
                : $"Attenzione: {multiDay} attività hanno sessioni anche in altri giorni e verranno eliminate per intero.");
        }
        if (activities.Any(a => a.IsRunning))
        {
            msg.AppendLine();
            msg.AppendLine("Tra queste c'è l'attività in corso: verrà eliminata e il cronometro si fermerà.");
        }

        if (MessageBox.Show(this, msg.ToString().TrimEnd(), "Elimina attività", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        foreach (var id in ids) Store.DeleteActivity(id);
        _app.ShowFlyoutRefresh(); // rilegge lo stato (attività in corso / in pausa)
        Reload();
    }

    private void AddManual()
    {
        var w = new EditActivityWindow(Store, null, _from.SelectedDate) { Owner = this };
        w.ShowDialog();
        if (w.Saved) { Reload(); _app.ShowFlyoutRefresh(); }
    }

    private void EditSelected()
    {
        if (_grid.SelectedItem is not HistRow row) return;
        var w = new EditActivityWindow(Store, row.Src.ActivityId) { Owner = this };
        w.ShowDialog();
        if (w.Saved) { Reload(); _app.ShowFlyoutRefresh(); }
    }

    private void CopySummary()
    {
        if (_rows.Count == 0) return;
        Clipboard.SetText(Reporting.ToSummaryText(_rows, RoundMinutes));
        MessageBox.Show(this, "Riepilogo copiato negli appunti.", "TimeHero", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportCsv()
    {
        if (_rows.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (Excel)|*.csv",
            FileName = $"timesheet_{(_from.SelectedDate ?? DateTime.Today):yyyyMMdd}_{(_to.SelectedDate ?? DateTime.Today):yyyyMMdd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, Reporting.ToCsv(_rows, RoundMinutes), new UTF8Encoding(true));
    }

    /// <summary>Riga del DataGrid (proprietà già formattate).</summary>
    private sealed class HistRow : INotifyPropertyChanged
    {
        public TimesheetRow Src { get; }
        private readonly int _round;
        private readonly Action _onChecked;
        private bool _checked;

        public HistRow(TimesheetRow src, int round, Action onChecked)
        {
            Src = src;
            _round = round;
            _onChecked = onChecked;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Casella di selezione per l'eliminazione in blocco.</summary>
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                _onChecked();
            }
        }

        public string Day => Src.Start.ToString("ddd dd/MM", It);
        public string Start => Src.Start.ToString("HH:mm");
        public string End => Src.Running ? "in corso" : Src.End.ToString("HH:mm");
        public string Duration => $"{(int)Src.Duration.TotalHours}:{Src.Duration.Minutes:00}";
        public string Rounded
        {
            get
            {
                var r = Reporting.Round(Src.Duration, _round);
                return $"{(int)r.TotalHours}:{r.Minutes:00}";
            }
        }
        public string Status => Src.Status;
        public string Title => Src.Title;
        public string Category => Src.Category;
        public string Client => Src.Client ?? "";
        public string People => Src.People;
        public string Notes => Src.Notes?.ReplaceLineEndings(" ") ?? "";
        public string Attachments => Src.Attachments > 0 ? Src.Attachments.ToString() : "";
    }
}
