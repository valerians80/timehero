using System.Windows;
using System.Windows.Controls;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Anagrafiche (clienti, colleghi, categorie) e preferenze.</summary>
public sealed class SettingsWindow : Window
{
    private readonly TrayApp _app;
    private TimeStore Store => _app.Store;

    private readonly TextBox _reminder = Ui.Input();
    private readonly TextBox _idle = Ui.Input();
    private readonly CheckBox _autostart = new() { Content = "Avvia TimeHero con Windows", Margin = new Thickness(0, 12, 0, 0) };

    public SettingsWindow(TrayApp app)
    {
        _app = app;
        Title = "TimeHero — impostazioni";
        Width = 440;
        Height = 480;
        Background = Ui.Bg;
        Foreground = Ui.Fg;
        FontFamily = Ui.Font;
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tabs = new TabControl { Margin = new Thickness(10) };
        tabs.Items.Add(new TabItem { Header = "Generali", Content = BuildGeneral() });
        tabs.Items.Add(new TabItem
        {
            Header = "Clienti",
            Content = ListTab(
                () => Store.GetClients().Select(c => c.Name).ToList(),
                name => Store.GetOrAddClient(name),
                name => { var c = Store.GetClients().FirstOrDefault(x => x.Name == name); if (c != null) Store.SetClientActive(c.Id, false); },
                "Disattiva", "Il cliente sparisce dall'elenco ma resta nello storico.")
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Colleghi",
            Content = ListTab(
                () => Store.GetPeople().Select(p => p.Name).ToList(),
                name => Store.GetOrAddPerson(name),
                name =>
                {
                    var p = Store.GetPeople().FirstOrDefault(x => x.Name == name);
                    if (p != null && MessageBox.Show(this, $"Eliminare {p.Name}? Verrà tolto anche dalle attività passate.",
                            "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        Store.DeletePerson(p.Id);
                },
                "Elimina", null)
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Categorie",
            Content = ListTab(
                () => Store.GetCategories().Select(c => c.Name).ToList(),
                name => { if (!Store.GetCategories(true).Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) Store.AddCategory(name); },
                name => { var c = Store.GetCategories().FirstOrDefault(x => x.Name == name); if (c != null) Store.SetCategoryActive(c.Id, false); },
                "Disattiva", "Le categorie sono i pulsanti di avvio rapido nel post-it.")
        });
        Content = tabs;
    }

    private UIElement BuildGeneral()
    {
        _reminder.Text = _app.Settings.ReminderMinutes.ToString();
        _idle.Text = _app.Settings.IdleMinutes.ToString();
        _autostart.IsChecked = AutoStart.IsEnabled;

        var p = new StackPanel { Margin = new Thickness(12) };
        p.Children.Add(Ui.Field("Promemoria \"attività in corso\" ogni N minuti (0 = mai)", _reminder));
        p.Children.Add(Ui.Field("Chiedi conferma dopo N minuti di inattività (0 = mai)", _idle));
        p.Children.Add(_autostart);
        p.Children.Add(Ui.Text("Scorciatoia per aprire il post-it: Ctrl+Alt+T", 11, fg: Ui.Subtle));
        ((TextBlock)p.Children[^1]).Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(Ui.Text($"Dati: {Database.DefaultPath}", 10.5, fg: Ui.Subtle));
        ((TextBlock)p.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(Ui.Btn("Salva", (_, _) => SaveGeneral(), Ui.Go));
        ((Button)p.Children[^1]).HorizontalAlignment = HorizontalAlignment.Left;
        ((Button)p.Children[^1]).Margin = new Thickness(0, 16, 0, 0);
        return p;
    }

    private void SaveGeneral()
    {
        if (int.TryParse(_reminder.Text, out var r) && r >= 0) _app.Settings.ReminderMinutes = r;
        if (int.TryParse(_idle.Text, out var i) && i >= 0) _app.Settings.IdleMinutes = i;
        AutoStart.Set(_autostart.IsChecked == true);
        MessageBox.Show(this, "Impostazioni salvate.", "TimeHero", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private UIElement ListTab(Func<List<string>> load, Action<string> add, Action<string> remove,
        string removeLabel, string? hint)
    {
        var list = new ListBox { Background = Ui.Surface, BorderBrush = Ui.Border };
        var input = Ui.Input();
        void Reload() => list.ItemsSource = load();
        void Add()
        {
            var name = input.Text.Trim();
            if (name.Length == 0) return;
            add(name);
            input.Text = "";
            Reload();
        }
        input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Add(); };
        Reload();

        var root = new DockPanel { Margin = new Thickness(10) };
        var addRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var addBtn = Ui.Btn("Aggiungi", (_, _) => Add(), Ui.Go);
        DockPanel.SetDock(addBtn, Dock.Right);
        addRow.Children.Add(addBtn);
        addRow.Children.Add(input);
        DockPanel.SetDock(addRow, Dock.Top);
        root.Children.Add(addRow);

        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(Ui.Btn(removeLabel + " selezionato", (_, _) =>
        {
            if (list.SelectedItem is string s) { remove(s); Reload(); }
        }, Ui.Danger));
        if (hint is not null) bottom.Children.Add(Ui.Text(hint, 10.5, fg: Ui.Subtle));
        root.Children.Add(bottom);
        root.Children.Add(list);
        return root;
    }
}
