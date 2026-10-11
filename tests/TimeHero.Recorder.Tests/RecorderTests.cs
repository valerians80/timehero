using TimeHero.App;
using TimeHero.Core;
using TimeHero.Core.Tests;
using Xunit;

public class RecorderTests
{
    private sealed class Rig : IDisposable
    {
        public TimeStore Store = new(Database.Open(":memory:"));
        public FakeObsServer Obs = new();
        public ActivityTracker Tracker;
        public AppSettings Settings;
        public ObsRecorder Rec;
        public long Calls, Mail;

        public Rig()
        {
            Tracker = new ActivityTracker(Store);
            Settings = new AppSettings(Store) { ObsEnabled = true, ObsHost = "localhost", ObsPort = Obs.Port };
            Rec = new ObsRecorder(Store, Tracker, Settings);
            Calls = Store.GetCategories().Single(c => c.Name == "Chiamate").Id;
            Mail = Store.GetCategories().Single(c => c.Name == "Gestione mail").Id;
            Tracker.Changed += () => Rec.Reconcile();   // come in TrayApp
        }

        public static async Task Until(Func<bool> cond, string what)
        {
            for (var i = 0; i < 100; i++) { if (cond()) return; await Task.Delay(50); }
            Assert.Fail("timeout: " + what);
        }

        public void Dispose() { Obs.Dispose(); }
    }

    [Fact]
    public async Task Call_starts_recording_and_pause_stops_it_and_attaches_the_file()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call con Mario", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "recording");
        Assert.True(r.Obs.Recording);

        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle && !r.Obs.Recording, "stopped");

        var att = Assert.Single(r.Store.GetAttachments(a.Id));
        Assert.Equal("audio", att.Kind);
        Assert.Equal(@"C:\Rec\chiamata.mkv", att.FilePath);
    }

    [Fact]
    public async Task Non_recording_category_never_touches_obs()
    {
        using var r = new Rig();
        r.Tracker.Start("Smistamento mail", r.Mail);
        await Task.Delay(400);
        Assert.False(r.Rec.IsRecording);
        Assert.Empty(r.Obs.Calls);
    }

    [Fact]
    public async Task Switching_between_two_calls_closes_the_first_file_and_starts_the_second()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call A", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec A");
        r.Obs.NextOutputPath = @"C:\Rec\A.mkv";
        var b = r.Tracker.Start("Call B", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording && r.Rec.RecordingActivityId == b.Id, "rec B");

        Assert.Equal(@"C:\Rec\A.mkv", Assert.Single(r.Store.GetAttachments(a.Id)).FilePath);
        Assert.Empty(r.Store.GetAttachments(b.Id));
        Assert.True(r.Obs.Recording);
    }

    [Fact]
    public async Task Resume_after_a_pause_records_a_second_file_on_the_same_activity()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call lunga", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec 1");
        r.Obs.NextOutputPath = @"C:\Rec\parte1.mkv";
        r.Tracker.Pause();
        await Rig.Until(() => r.Store.GetAttachments(a.Id).Count == 1 && !r.Obs.Recording, "stop 1");

        r.Tracker.Resume(a.Id);
        await Rig.Until(() => r.Rec.IsRecording, "rec 2");
        r.Obs.NextOutputPath = @"C:\Rec\parte2.mkv";
        r.Tracker.Finish();
        await Rig.Until(() => r.Store.GetAttachments(a.Id).Count == 2, "stop 2");
        Assert.Equal(new[] { @"C:\Rec\parte1.mkv", @"C:\Rec\parte2.mkv" }, r.Store.GetAttachments(a.Id).Select(x => x.FilePath));
    }

    [Fact]
    public async Task Profile_is_switched_for_the_recording_and_restored_afterwards()
    {
        using var r = new Rig();
        r.Settings.ObsProfile = "TimeHero";
        r.Settings.ObsSceneCollection = "TimeHero";
        r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        Assert.Equal("TimeHero", r.Obs.CurrentProfile);
        Assert.Equal("TimeHero", r.Obs.CurrentCollection);

        r.Tracker.Pause();
        await Rig.Until(() => !r.Obs.Recording && r.Obs.CurrentProfile == "Predefinito", "restored");
        Assert.Equal("Senza titolo", r.Obs.CurrentCollection);
    }

    [Fact]
    public async Task Rejected_start_reports_the_error_and_does_not_retry_until_the_button_is_used()
    {
        using var r = new Rig();
        r.Obs.RejectStart = "No audio device";
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle && r.Rec.Message != null, "error");
        Assert.Contains("No audio device", r.Rec.Message);
        Assert.False(r.Obs.Recording);

        var startsBefore = r.Obs.Calls.Count(c => c == "StartRecord");
        r.Rec.Reconcile();
        await Task.Delay(300);
        Assert.Equal(startsBefore, r.Obs.Calls.Count(c => c == "StartRecord")); // nessun tentativo a raffica

        r.Obs.RejectStart = null;
        r.Rec.ToggleManual();                                                  // l'utente riprova col pulsante
        await Rig.Until(() => r.Rec.IsRecording, "manual retry");
    }

    [Fact]
    public async Task Obs_already_recording_is_left_alone()
    {
        using var r = new Rig();
        // l'utente ha già avviato la registrazione da OBS
        await using (var s = await ObsSession.ConnectAsync("localhost", r.Obs.Port, null))
            await s.RequestAsync("StartRecord");

        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "adopted");
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle, "idle");

        Assert.True(r.Obs.Recording);                       // non l'abbiamo fermata
        Assert.Empty(r.Store.GetAttachments(a.Id));         // e non abbiamo allegato nulla
        Assert.Equal(1, r.Obs.Calls.Count(c => c == "StartRecord")); // solo quello dell'utente
    }

    [Fact]
    public async Task Manual_button_forces_recording_on_any_category_and_stops_it()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Analisi", r.Mail);
        await Task.Delay(200);
        Assert.False(r.Rec.IsRecording);

        r.Rec.ToggleManual();
        await Rig.Until(() => r.Rec.IsRecording, "forced");

        r.Rec.ToggleManual();                                // fermata a mano, resta fermata
        await Rig.Until(() => !r.Obs.Recording && r.Rec.State == ObsRecorder.RecState.Idle, "stopped by user");
        Assert.Single(r.Store.GetAttachments(a.Id));
        await Task.Delay(300);
        Assert.False(r.Rec.IsRecording);
    }

    [Fact]
    public async Task Recording_a_call_is_stopped_by_the_button_and_not_restarted_automatically()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "auto");
        r.Rec.ToggleManual();
        await Rig.Until(() => !r.Obs.Recording, "stopped");
        r.Tracker.UpdateDetails(a);
        r.Rec.Reconcile();
        await Task.Delay(300);
        Assert.False(r.Rec.IsRecording);
    }

    [Fact]
    public async Task Disabled_setting_means_no_recording()
    {
        using var r = new Rig();
        r.Settings.ObsEnabled = false;
        r.Tracker.Start("Call", r.Calls);
        await Task.Delay(300);
        Assert.Empty(r.Obs.Calls);
    }

    [Fact]
    public async Task Exit_stops_and_saves_the_running_recording()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        await Task.Run(() => r.Rec.StopForExitAsync());
        Assert.False(r.Obs.Recording);
        Assert.Single(r.Store.GetAttachments(a.Id));
    }

    [Fact]
    public async Task Wrong_password_is_reported()
    {
        using var r = new Rig();
        r.Obs.Password = "giusta";
        r.Settings.ObsPassword = "";   // vuoto: OBS lo richiede
        r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.Message != null, "message");
        Assert.Contains("password", r.Rec.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(r.Rec.IsRecording);
    }

    [Fact]
    public async Task Recording_starts_even_if_obs_is_still_loading_when_the_call_begins()
    {
        using var r = new Rig();
        r.Obs.NotReadyRequests = 4;               // OBS appena aperto: risponde 207 per qualche secondo
        r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "recording after ready");
        Assert.True(r.Obs.Recording);
        Assert.Null(r.Rec.Message);
    }
}
