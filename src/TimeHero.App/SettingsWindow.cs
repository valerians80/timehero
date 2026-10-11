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
        Width = 480;
        Height = 700;
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
        tabs.Items.Add(new TabItem { Header = "Registrazione", Content = BuildObs() });
        Content = tabs;
    }

    // ---------- registrazione audio con OBS ----------
    private readonly CheckBox _obsEnabled = new() { Content = "Registra l'audio con OBS (chiamate e riunioni)" };
    private readonly TextBox _obsHost = Ui.Input();
    private readonly TextBox _obsPort = Ui.Input();
    private readonly PasswordBox _obsPw = new()
    {
        FontSize = 12, Padding = new Thickness(4, 4, 4, 4), Background = Ui.Surface, Foreground = Ui.Fg,
        BorderBrush = Ui.Border, CaretBrush = Ui.Fg,
    };
    private readonly TextBox _obsExe = Ui.Input();
    private readonly TextBox _obsProfile = Ui.Input();
    private readonly TextBox _obsCollection = Ui.Input();
    private readonly CheckBox _obsKeepReady = new() { Content = "Apri OBS insieme a TimeHero (pronto a registrare)", Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _recordCats = new();
    private readonly TextBlock _obsResult = Ui.Text("", 11, fg: Ui.Subtle);

    private UIElement BuildObs()
    {
        var st = _app.Settings;
        _obsEnabled.IsChecked = st.ObsEnabled;
        _obsHost.Text = st.ObsHost;
        _obsPort.Text = st.ObsPort.ToString();
        _obsPw.Password = st.ObsPassword;
        _obsExe.Text = st.ObsExePath.Length > 0 ? st.ObsExePath : (_app.Recorder.FindObsExe() ?? "");
        _obsProfile.Text = st.ObsProfile;
        _obsCollection.Text = st.ObsSceneCollection;
        _obsKeepReady.IsChecked = st.ObsKeepReady;

        var p = new StackPanel { Margin = new Thickness(14) };
        p.Children.Add(_obsEnabled);

        var conn = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        conn.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        conn.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        var host = Ui.Field("Indirizzo OBS", _obsHost);
        var port = Ui.Field("Porta", _obsPort);
        port.Margin = new Thickness(8, 6, 0, 0);
        Grid.SetColumn(port, 1);
        conn.Children.Add(host);
        conn.Children.Add(port);
        p.Children.Add(conn);
        p.Children.Add(Ui.Field("Password del WebSocket", _obsPw));

        var exeRow = new DockPanel();
        var browse = Ui.Btn("Sfoglia…", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "OBS (obs64.exe)|obs64.exe|Programmi (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) == true) _obsExe.Text = dlg.FileName;
        });
        DockPanel.SetDock(browse, Dock.Right);
        exeRow.Children.Add(browse);
        exeRow.Children.Add(_obsExe);
        p.Children.Add(Ui.Field("Percorso di OBS (obs64.exe)", exeRow));
        p.Children.Add(Ui.Field("Profilo OBS da usare (facoltativo)", _obsProfile));
        p.Children.Add(Ui.Field("Raccolta di scene OBS da usare (facoltativo)", _obsCollection));
        p.Children.Add(_obsKeepReady);

        var catsLabel = Ui.Text("Registra automaticamente le attività di tipo:", 11, fg: Ui.Subtle);
        catsLabel.Margin = new Thickness(2, 14, 0, 4);
        p.Children.Add(catsLabel);
        foreach (var c in Store.GetCategories())
            _recordCats.Children.Add(new CheckBox { Content = c.Name, IsChecked = c.Record, Tag = c.Id, Margin = new Thickness(0, 3, 0, 3) });
        p.Children.Add(_recordCats);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Ui.Btn("Salva", (_, _) => SaveObs(), Ui.Go));
        buttons.Children.Add(Ui.Btn("Prova connessione", async (_, _) => await TestObsAsync(),
            tooltip: "Si collega a OBS (lo apre se è chiuso) e mostra versione, profili e stato"));
        p.Children.Add(buttons);
        _obsResult.Margin = new Thickness(2, 10, 0, 0);
        p.Children.Add(_obsResult);

        var help = Ui.Text(
            "Impostazioni consigliate in OBS per file piccoli:\n" +
            "• Strumenti → Impostazioni WebSocket server: attiva il server, porta 4455, imposta una password.\n" +
            "• Crea un profilo e una raccolta di scene \"TimeHero\" dedicati (e indicali qui sopra).\n" +
            "• Impostazioni → Audio: Canali = Mono; Audio desktop = il dispositivo di uscita; Microfono/Aux = il tuo microfono.\n" +
            "• Impostazioni → Video: risoluzione base e di output 160×90, FPS 1 (valore intero). Lascia la scena senza sorgenti video.\n" +
            "• Impostazioni → Output (modalità Semplice) → Registrazione: formato mp4 o mkv, bitrate audio 64 kbps.\n" +
            "Con queste impostazioni un'ora di registrazione pesa circa 30 MB.", 10.5, fg: Ui.Subtle);
        help.Margin = new Thickness(2, 16, 0, 0);
        p.Children.Add(help);

        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = p };
    }

    private void ApplyObsFields()
    {
        var st = _app.Settings;
        st.ObsEnabled = _obsEnabled.IsChecked == true;
        st.ObsHost = string.IsNullOrWhiteSpace(_obsHost.Text) ? "localhost" : _obsHost.Text.Trim();
        st.ObsPort = int.TryParse(_obsPort.Text, out var port) && port is > 0 and < 65536 ? port : 4455;
        st.ObsPassword = _obsPw.Password;
        st.ObsExePath = _obsExe.Text.Trim();
        st.ObsProfile = _obsProfile.Text.Trim();
        st.ObsSceneCollection = _obsCollection.Text.Trim();
        st.ObsKeepReady = _obsKeepReady.IsChecked == true;
        foreach (CheckBox cb in _recordCats.Children)
            Store.SetCategoryRecord((long)cb.Tag, cb.IsChecked == true);
    }

    private void SaveObs()
    {
        ApplyObsFields();
        _obsResult.Foreground = Ui.Fg;
        _obsResult.Text = "Impostazioni di registrazione salvate.";
        _app.Recorder.Reconcile();
    }

    private async Task TestObsAsync()
    {
        ApplyObsFields();
        _obsResult.Foreground = Ui.Subtle;
        _obsResult.Text = "Connessione a OBS… (se è chiuso lo avvio, può servire qualche secondo)";
        try
        {
            await using var s = await _app.Recorder.ConnectAsync(launch: true);
            var ver = await s.RequestAsync("GetVersion");
            var profiles = await s.RequestAsync("GetProfileList");
            var collections = await s.RequestAsync("GetSceneCollectionList");
            var rec = await s.RequestAsync("GetRecordStatus");

            string Names(System.Text.Json.JsonElement? e, string prop) =>
                e is { } el ? string.Join(", ", el.GetProperty(prop).EnumerateArray().Select(x => x.GetString())) : "?";

            _obsResult.Foreground = Ui.Hex("#7BD88F");
            _obsResult.Text =
                $"✔ Connesso a OBS {ver?.GetProperty("obsVersion").GetString()} " +
                $"(WebSocket {ver?.GetProperty("obsWebSocketVersion").GetString()}).\n" +
                $"Profili: {Names(profiles, "profiles")} — attuale: {profiles?.GetProperty("currentProfileName").GetString()}\n" +
                $"Raccolte di scene: {Names(collections, "sceneCollections")} — attuale: {collections?.GetProperty("currentSceneCollectionName").GetString()}\n" +
                $"Registrazione in corso: {(rec?.GetProperty("outputActive").GetBoolean() == true ? "sì" : "no")}";
        }
        catch (Exception ex)
        {
            _obsResult.Foreground = Ui.Hex("#FF8A80");
            _obsResult.Text = "✖ " + ex.Message;
        }
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
