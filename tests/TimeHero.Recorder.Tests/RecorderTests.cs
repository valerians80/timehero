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
            for (var i = 0; i < 120; i++) { if (cond()) return; await Task.Delay(50); }
            Assert.Fail("timeout: " + what);
        }

        public int Count(string request) => Obs.Calls.Count(c => c == request || c.StartsWith(request + ":"));
        public void Dispose() { Obs.Dispose(); }
    }

    [Fact]
    public async Task Pause_pauses_the_recording_resume_continues_it_and_finish_saves_a_single_file()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call con Mario", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "recording");

        r.Tracker.Pause();                                           // pausa dell'attività…
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");
        Assert.True(r.Obs.Paused);                                   // …= pausa in OBS
        Assert.True(r.Obs.Recording);                                // il file è ancora aperto
        Assert.Empty(r.Store.GetAttachments(a.Id));                  // nessun file chiuso, nessun allegato

        r.Tracker.Resume(a.Id);
        await Rig.Until(() => r.Rec.IsRecording, "resumed");
        Assert.False(r.Obs.Paused);

        r.Tracker.Finish();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle && !r.Obs.Recording, "stopped");

        var att = Assert.Single(r.Store.GetAttachments(a.Id));       // un solo file per tutta l'attività
        Assert.Equal("audio", att.Kind);
        Assert.Equal(@"C:\Rec\chiamata.mkv", att.FilePath);
        Assert.Equal(1, r.Count("StartRecord"));
        Assert.Equal(1, r.Count("PauseRecord"));
        Assert.Equal(1, r.Count("ResumeRecord"));
        Assert.Equal(1, r.Count("StopRecord"));
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
    public async Task Starting_a_non_recorded_activity_pauses_the_call_recording_and_resuming_the_call_continues_it()
    {
        using var r = new Rig();
        var call = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");

        r.Tracker.Start("Rispondo a una mail", r.Mail);              // la call va in pausa, l'altra non si registra
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");
        Assert.True(r.Obs.Paused);
        Assert.Equal(call.Id, r.Rec.RecordingActivityId);

        r.Tracker.Resume(call.Id);
        await Rig.Until(() => r.Rec.IsRecording, "resumed");
        Assert.Equal(1, r.Count("StartRecord"));                     // sempre lo stesso file
        Assert.Equal(1, r.Count("ResumeRecord"));
        Assert.Empty(r.Store.GetAttachments(call.Id));
    }

    [Fact]
    public async Task Switching_to_another_recorded_activity_closes_the_first_file_and_starts_a_new_one()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call A", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec A");
        r.Obs.NextOutputPath = @"C:\Rec\A.mkv";
        var b = r.Tracker.Start("Call B", r.Calls);                  // un solo file alla volta: A si chiude
        await Rig.Until(() => r.Rec.IsRecording && r.Rec.RecordingActivityId == b.Id, "rec B");

        Assert.Equal(@"C:\Rec\A.mkv", Assert.Single(r.Store.GetAttachments(a.Id)).FilePath);
        Assert.Empty(r.Store.GetAttachments(b.Id));
        Assert.True(r.Obs.Recording);
        Assert.False(r.Obs.Paused);
    }

    [Fact]
    public async Task A_paused_recording_is_closed_and_saved_after_the_timeout()
    {
        using var r = new Rig();
        r.Rec.PauseTimeoutOverride = TimeSpan.FromMilliseconds(600);
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");

        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");
        Assert.Empty(r.Store.GetAttachments(a.Id));

        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle && !r.Obs.Recording, "closed by timeout");
        Assert.Single(r.Store.GetAttachments(a.Id));
    }

    [Fact]
    public async Task Resuming_in_time_cancels_the_timeout()
    {
        using var r = new Rig();
        r.Rec.PauseTimeoutOverride = TimeSpan.FromMilliseconds(800);
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");
        r.Tracker.Resume(a.Id);
        await Rig.Until(() => r.Rec.IsRecording, "resumed");

        await Task.Delay(1400);                                      // oltre il timeout: non deve chiudersi
        Assert.True(r.Rec.IsRecording);
        Assert.True(r.Obs.Recording);
        Assert.Equal(0, r.Count("StopRecord"));
    }

    [Fact]
    public async Task If_obs_cannot_pause_the_file_is_closed_and_resume_starts_a_new_one()
    {
        using var r = new Rig();
        r.Obs.RejectPause = true;
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");

        r.Obs.NextOutputPath = @"C:\Rec\parte1.mkv";
        r.Tracker.Pause();                                           // come prima: si chiude e si allega
        await Rig.Until(() => r.Store.GetAttachments(a.Id).Count == 1 && !r.Obs.Recording, "fallback stop");
        Assert.Equal(@"C:\Rec\parte1.mkv", r.Store.GetAttachments(a.Id)[0].FilePath);

        r.Tracker.Resume(a.Id);
        await Rig.Until(() => r.Rec.IsRecording, "new recording");
        Assert.Equal(2, r.Count("StartRecord"));
    }

    [Fact]
    public async Task If_the_recording_was_closed_in_obs_while_paused_resuming_starts_a_new_one()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");

        r.Obs.StopFromUi();                                          // l'utente chiude la registrazione dentro OBS
        r.Tracker.Resume(a.Id);
        await Rig.Until(() => r.Rec.IsRecording && r.Obs.Recording, "new recording");
        Assert.Equal(2, r.Count("StartRecord"));
        Assert.Equal(1, r.Count("ResumeRecord"));                    // ha provato a riprendere, poi ne ha avviata una nuova
    }

    [Fact]
    public async Task Finishing_a_paused_activity_closes_and_saves_the_file()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");

        r.Tracker.Finish(a.Id);                                      // chiusa da pausa
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle && !r.Obs.Recording, "stopped");
        Assert.Single(r.Store.GetAttachments(a.Id));
    }

    [Fact]
    public async Task AudioSaved_is_raised_when_the_file_is_closed_not_when_paused()
    {
        using var r = new Rig();
        var saved = new List<long>();
        r.Rec.AudioSaved += id => saved.Add(id);

        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");
        await Task.Delay(300);
        Assert.Empty(saved);

        r.Tracker.Resume(a.Id);
        await Rig.Until(() => r.Rec.IsRecording, "resumed");
        r.Tracker.Finish();
        await Rig.Until(() => saved.Count == 1, "AudioSaved");
        Assert.Equal(Assert.Single(r.Store.GetAttachments(a.Id)).Id, saved[0]);
    }

    [Fact]
    public async Task Profile_stays_switched_while_paused_and_is_restored_when_the_file_closes()
    {
        using var r = new Rig();
        r.Settings.ObsProfile = "TimeHero";
        r.Settings.ObsSceneCollection = "TimeHero";
        r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        Assert.Equal("TimeHero", r.Obs.CurrentProfile);

        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");
        Assert.Equal("TimeHero", r.Obs.CurrentProfile);              // un file aperto non permette di cambiare profilo

        r.Tracker.Resume(r.Tracker.Paused.Single().Id);
        await Rig.Until(() => r.Rec.IsRecording, "resumed");
        r.Tracker.Finish();
        await Rig.Until(() => !r.Obs.Recording && r.Obs.CurrentProfile == "Predefinito", "restored");
        Assert.Equal("Senza titolo", r.Obs.CurrentCollection);
    }

    [Fact]
    public async Task Rejected_start_reports_the_error_and_does_not_retry_until_the_button_is_used()
    {
        using var r = new Rig();
        r.Obs.RejectStart = "No audio device";
        r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle && r.Rec.Message != null, "error");
        Assert.Contains("No audio device", r.Rec.Message);
        Assert.False(r.Obs.Recording);

        var startsBefore = r.Count("StartRecord");
        r.Rec.Reconcile();
        await Task.Delay(300);
        Assert.Equal(startsBefore, r.Count("StartRecord"));          // nessun tentativo a raffica

        r.Obs.RejectStart = null;
        r.Rec.ToggleManual();                                        // l'utente riprova col pulsante
        await Rig.Until(() => r.Rec.IsRecording, "manual retry");
    }

    [Fact]
    public async Task Obs_already_recording_is_left_alone_even_when_the_activity_is_paused()
    {
        using var r = new Rig();
        await using (var s = await ObsSession.ConnectAsync("localhost", r.Obs.Port, null))
            await s.RequestAsync("StartRecord");                     // registrazione avviata dall'utente in OBS

        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "adopted");
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Idle, "released");

        Assert.True(r.Obs.Recording);                                // non l'abbiamo né messa in pausa né fermata
        Assert.False(r.Obs.Paused);
        Assert.Equal(0, r.Count("PauseRecord"));
        Assert.Equal(0, r.Count("StopRecord"));
        Assert.Empty(r.Store.GetAttachments(a.Id));
    }

    [Fact]
    public async Task Manual_button_forces_recording_on_any_category_and_stop_means_close_the_file()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Analisi", r.Mail);
        await Task.Delay(200);
        Assert.False(r.Rec.IsRecording);

        r.Rec.ToggleManual();
        await Rig.Until(() => r.Rec.IsRecording, "forced");

        r.Rec.ToggleManual();                                        // "Ferma registrazione": chiude davvero
        await Rig.Until(() => !r.Obs.Recording && r.Rec.State == ObsRecorder.RecState.Idle, "stopped by user");
        Assert.Single(r.Store.GetAttachments(a.Id));
        Assert.Equal(0, r.Count("PauseRecord"));
        await Task.Delay(300);
        Assert.False(r.Rec.IsRecording);                             // e non riparte da sola
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
    public async Task Exit_saves_the_running_recording()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        await Task.Run(() => r.Rec.StopForExitAsync());
        Assert.False(r.Obs.Recording);
        Assert.Single(r.Store.GetAttachments(a.Id));
    }

    [Fact]
    public async Task Exit_saves_a_paused_recording_too()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");

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

    [Fact]
    public async Task Elapsed_counts_only_the_recorded_time_not_the_pauses()
    {
        using var r = new Rig();
        var a = r.Tracker.Start("Call", r.Calls);
        await Rig.Until(() => r.Rec.IsRecording, "rec");
        await Task.Delay(400);
        r.Tracker.Pause();
        await Rig.Until(() => r.Rec.State == ObsRecorder.RecState.Paused, "paused");

        var atPause = r.Rec.Elapsed;
        Assert.InRange(atPause.TotalMilliseconds, 300, 1500);
        await Task.Delay(700);
        Assert.Equal(atPause, r.Rec.Elapsed);                        // in pausa il tempo non avanza

        r.Tracker.Resume(a.Id);
        await Rig.Until(() => r.Rec.IsRecording, "resumed");
        await Task.Delay(300);
        Assert.True(r.Rec.Elapsed > atPause);
    }
}
