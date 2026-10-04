using System.IO;
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
        bottom.Children.Add(Ui.Btn("📋 Copia riepilogo", (_, _) => CopySummary(), tooltip: "Riepilogo per giorno/cliente/categoria da incollare nel timesheet"));
        bottom.Children.Add(Ui.Btn("💾 Esporta CSV", (_, _) => ExportCsv()));
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
        _grid.Background = Ui.Surface;
        _grid.RowBackground = Ui.Surface;
        _grid.AlternatingRowBackground = Ui.RowAlt;
        _grid.Foreground = Ui.Fg;
        _grid.BorderBrush = Ui.Border;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.RowHeight = 28;

        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Surface2));
        header.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Subtle));
        header.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Border));
        header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        _grid.ColumnHeaderStyle = header;

        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        cell.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var cellSel = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        cellSel.Setters.Add(new Setter(Control.BackgroundProperty, Ui.AccentDim));
        cellSel.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Fg));
        cell.Triggers.Add(cellSel);
        _grid.CellStyle = cell;

        var row = new Style(typeof(DataGridRow));
        var rowSel = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowSel.Setters.Add(new Setter(Control.BackgroundProperty, Ui.AccentDim));
        row.Triggers.Add(rowSel);
        _grid.RowStyle = row;
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
        _grid.ItemsSource = _rows.Select(r => new HistRow(r, round)).ToList();

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
    private sealed class HistRow
    {
        public TimesheetRow Src { get; }
        private readonly int _round;
        public HistRow(TimesheetRow src, int round) { Src = src; _round = round; }

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
