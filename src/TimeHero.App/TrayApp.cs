using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>Cuore dell'applicazione: icona nella tray, scorciatoia, promemoria, rilevamento assenze.</summary>
public sealed class TrayApp : IDisposable
{
    public TimeStore Store { get; }
    public ActivityTracker Tracker { get; }
    public AppSettings Settings { get; }

    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly FlyoutWindow _flyout;
    private readonly HotkeyService _hotkey;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(15) };

    private DateTime _lastReminder = DateTime.UtcNow;
    private DateTime? _awaySince;
    private bool _asking;
    private HistoryWindow? _history;
    private SettingsWindow? _settings;

    public TrayApp()
    {
        Store = TimeStore.OpenFile();
        Tracker = new ActivityTracker(Store);
        Settings = new AppSettings(Store);

        Notifier.Init(OnToastAction);
        _flyout = new FlyoutWindow(this);

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Visible = true,
            Text = "TimeHero",
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) _flyout.ToggleFlyout();
        };

        // Ctrl+Alt+T apre/chiude il post-it
        _hotkey = new HotkeyService(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x54);
        _hotkey.Pressed += _flyout.ToggleFlyout;
        if (!_hotkey.Registered)
            Notifier.Info("Scorciatoia non disponibile", "Ctrl+Alt+T è già usata da un'altra app: usa l'icona nella tray.");

        Tracker.Changed += () =>
        {
            _lastReminder = DateTime.UtcNow;
            UpdateTrayText();
            _flyout.Refresh();
        };

        SystemEvents.SessionSwitch += OnSessionSwitch;
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        UpdateTrayText();

        System.Windows.Application.Current.Dispatcher.BeginInvoke(RecoverIfNeeded);
    }

    // ---------- finestre ----------
    public void ToggleFlyout() => _flyout.ToggleFlyout();
    public void ShowFlyout() => _flyout.ShowFlyout();

    /// <summary>Riallinea il post-it dopo modifiche fatte da altre finestre.</summary>
    public void ShowFlyoutRefresh() => Tracker.Reload();

    public void ShowHistory()
    {
        if (_history is null)
        {
            _history = new HistoryWindow(this);
            _history.Closed += (_, _) => _history = null;
        }
        _history.Show();
        _history.Activate();
        _history.Reload();
    }

    public void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(this);
            _settings.Closed += (_, _) => { _settings = null; _flyout.Refresh(); };
        }
        _settings.Show();
        _settings.Activate();
    }

    public void Exit()
    {
        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private System.Windows.Forms.ContextMenuStrip BuildMenu()
    {
        var m = new System.Windows.Forms.ContextMenuStrip();
        m.Items.Add("Apri TimeHero", null, (_, _) => ShowFlyout());
        m.Items.Add("Storico e timesheet", null, (_, _) => ShowHistory());
        m.Items.Add("Impostazioni", null, (_, _) => ShowSettings());
        m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        m.Items.Add("Esci", null, (_, _) => Exit());
        return m;
    }

    private static System.Drawing.Icon MakeIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF2, 0xC9, 0x4C));
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x2B, 0x2A, 0x23), 3);
            g.FillEllipse(fill, 2, 2, 28, 28);
            g.DrawEllipse(pen, 2, 2, 28, 28);
            g.DrawLine(pen, 16, 16, 16, 7);
            g.DrawLine(pen, 16, 16, 23, 19);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void UpdateTrayText()
    {
        string text;
        if (Tracker.Current is { } cur)
        {
            text = $"TimeHero · {cur.Title} · {(int)cur.Duration().TotalMinutes} min";
        }
        else text = Tracker.Paused.Count > 0
            ? $"TimeHero · {Tracker.Paused.Count} in pausa"
            : "TimeHero · nessuna attività";
        _tray.Text = text.Length > 63 ? text[..63] : text; // limite di NotifyIcon
    }

    // ---------- timer: promemoria, battito, assenze ----------
    private void OnTick()
    {
        var now = DateTime.UtcNow;
        Store.SetSetting("heartbeat", now.ToString("o"));

        CheckIdle();

        var every = Settings.ReminderMinutes;
        if (every > 0 && !_asking && Tracker.Current is { } cur &&
            now - _lastReminder >= TimeSpan.FromMinutes(every) - TimeSpan.FromSeconds(1))
        {
            _lastReminder = now;
            SendReminder(cur);
        }
        UpdateTrayText();
    }

    private void SendReminder(Activity cur)
    {
        var cat = Store.GetCategories(true).FirstOrDefault(c => c.Id == cur.CategoryId)?.Name;
        var client = cur.ClientId is { } id ? Store.GetClients(true).FirstOrDefault(c => c.Id == id)?.Name : null;
        var detail = string.Join(" · ", new[] { cat, client }.Where(x => !string.IsNullOrEmpty(x)));
        var paused = Tracker.Paused.Count;
        Notifier.Reminder($"⏱ In corso: {cur.Title}",
            $"{(detail.Length > 0 ? detail + " — " : "")}sessione da {(int)(DateTime.UtcNow - (cur.Segments.LastOrDefault()?.StartUtc ?? cur.StartUtc)).TotalMinutes} min, " +
            $"totale {Reporting.FormatHm(cur.Duration())}" +
            (paused > 0 ? $"\nIn pausa: {paused}" : ""));
    }

    private void OnToastAction(string action)
    {
        if (action == "pause") Tracker.Pause();
        else if (action == "finish") Tracker.Finish();
        else if (action == "open") ShowFlyout();
    }

    // ---------- assenze (inattività / blocco schermo) ----------
    private TimeSpan IdleThreshold => TimeSpan.FromMinutes(Math.Max(1, Settings.IdleMinutes));

    private void CheckIdle()
    {
        if (Settings.IdleMinutes <= 0 || _asking) return;
        var idle = NativeMethods.IdleTime();

        if (Tracker.Current is not null && idle >= IdleThreshold && _awaySince is null)
            _awaySince = DateTime.UtcNow - idle;
        else if (_awaySince is not null && idle < IdleThreshold)
        {
            var since = _awaySince.Value;
            _awaySince = null;
            AskAfterAway(since);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock && Tracker.Current is not null && _awaySince is null)
                _awaySince = DateTime.UtcNow;
            else if (e.Reason == SessionSwitchReason.SessionUnlock && _awaySince is { } since)
            {
                _awaySince = null;
                AskAfterAway(since);
            }
        });
    }

    private void AskAfterAway(DateTime sinceUtc)
    {
        if (Tracker.Current is not { } cur || _asking) return;
        var away = DateTime.UtcNow - sinceUtc;
        if (away < IdleThreshold) return;

        _asking = true;
        try
        {
            var r = System.Windows.MessageBox.Show(
                $"Sei stato lontano dal PC dalle {sinceUtc.ToLocalTime():HH:mm} ({(int)away.TotalMinutes} min).\n\n" +
                "SÌ = conta questo tempo nell'attività in corso\n" +
                $"NO = metti l'attività in pausa alle {sinceUtc.ToLocalTime():HH:mm}",
                "TimeHero — eri ancora al lavoro?",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.No && Tracker.Current?.Id == cur.Id) Tracker.Pause(sinceUtc);
        }
        finally
        {
            _asking = false;
            _lastReminder = DateTime.UtcNow;
        }
    }

    /// <summary>All'avvio: se l'app era stata chiusa con un'attività aperta, chiede cosa farne.</summary>
    private void RecoverIfNeeded()
    {
        if (Tracker.Current is not { } cur) return;
        if (!DateTime.TryParse(Store.GetSetting("heartbeat"), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var hb)) return;
        hb = hb.ToUniversalTime();
        if (hb <= (cur.Segments.LastOrDefault()?.StartUtc ?? cur.StartUtc) || DateTime.UtcNow - hb < TimeSpan.FromMinutes(2)) return; // era attiva fino a poco fa

        var r = System.Windows.MessageBox.Show(
            $"L'attività \"{cur.Title}\" risulta ancora in corso " +
            $"(sessione iniziata alle {cur.Segments.LastOrDefault()?.StartUtc.ToLocalTime():dd/MM HH:mm}).\n" +
            $"TimeHero è stato chiuso alle {hb.ToLocalTime():HH:mm}.\n\n" +
            "SÌ = continua a contare\n" +
            $"NO = mettila in pausa alle {hb.ToLocalTime():HH:mm}",
            "TimeHero — attività rimasta aperta", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.No) Tracker.Pause(hb);
    }

    // ---------- screenshot ----------
    private string NewAttachmentPath(Activity cur)
    {
        var dir = Path.Combine(Database.DefaultAttachmentsDir, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{DateTime.Now:HHmmss}_{cur.Id}.png");
    }

    public async Task CaptureScreenshotAsync()
    {
        if (Tracker.Current is not { } cur)
        {
            Notifier.Info("Nessuna attività in corso", "Avvia un'attività prima di allegare uno screenshot.");
            return;
        }
        _flyout.Hide(); // altrimenti finirebbe nello screenshot
        var path = await ScreenshotService.CaptureAsync(NewAttachmentPath(cur), TimeSpan.FromSeconds(60));
        if (path is null) return;
        Store.AddAttachment(cur.Id, path);
        Notifier.Info("Screenshot allegato", "Aggiunto all'attività in corso.");
        _flyout.Refresh();
    }

    public void PasteScreenshot()
    {
        if (Tracker.Current is not { } cur)
        {
            Notifier.Info("Nessuna attività in corso", "Avvia un'attività prima di allegare uno screenshot.");
            return;
        }
        var path = NewAttachmentPath(cur);
        if (!ScreenshotService.TrySaveClipboardImage(path))
        {
            Notifier.Info("Nessuna immagine negli appunti", "Copia uno screenshot (Win+Shift+S) e riprova.");
            return;
        }
        Store.AddAttachment(cur.Id, path);
        _flyout.Refresh();
    }

    public void Dispose()
    {
        _tick.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hotkey.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
