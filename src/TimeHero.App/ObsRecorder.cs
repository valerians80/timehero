using System.Diagnostics;
using System.IO;
using TimeHero.Core;

namespace TimeHero.App;

/// <summary>
/// Registra l'audio con OBS mentre è in corso un'attività di un tipo "da registrare" (chiamate, riunioni).
/// Avvia OBS se serve e avvia la registrazione via WebSocket. Quando l'attività va in pausa la registrazione va
/// in pausa con lei (stesso file, nessuna perdita di tempo alla ripresa); si chiude e si allega a "Fine", quando
/// parte un'altra attività da registrare, dopo un tempo massimo di pausa o alla chiusura di TimeHero.
/// Tutte le operazioni sono serializzate: i cambi di stato rapidi non si accavallano.
/// </summary>
public sealed class ObsRecorder
{
    private readonly TimeStore _store;
    private readonly ActivityTracker _tracker;
    private readonly AppSettings _settings;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _dirty;
    private bool _quiet;

    private readonly HashSet<long> _forced = new();      // attività da registrare su richiesta dell'utente
    private readonly HashSet<long> _suppressed = new();  // attività dove l'utente ha fermato la registrazione (o è fallita)

    private readonly SynchronizationContext? _ctx = SynchronizationContext.Current;
    private bool _forceStop;
    private int _expiryToken;
    private DateTime? _pausedUtc;
    private TimeSpan _recordedBefore;
    private DateTime _segmentStartUtc;

    private long? _activityId;
    private long? _lastSeenId;
    private bool _owns;                    // la registrazione l'ha avviata TimeHero (non l'utente da OBS)
    private string? _previousProfile;
    private string? _previousCollection;

    public ObsRecorder(TimeStore store, ActivityTracker tracker, AppSettings settings)
    {
        _store = store;
        _tracker = tracker;
        _settings = settings;
    }

    public enum RecState { Idle, Starting, Recording, Paused, Stopping }

    public RecState State { get; private set; } = RecState.Idle;

    /// <summary>Per i test: durata massima della pausa. Se null si usa l'impostazione (minuti, 0 = mai).</summary>
    public TimeSpan? PauseTimeoutOverride { get; set; }

    /// <summary>Tempo effettivamente registrato (le pause non contano), come lo conta OBS.</summary>
    public TimeSpan Elapsed => _recordedBefore + (State == RecState.Recording ? DateTime.UtcNow - _segmentStartUtc : TimeSpan.Zero);

    private TimeSpan PauseTimeout => PauseTimeoutOverride ?? TimeSpan.FromMinutes(Math.Max(0, _settings.ObsPauseMinutes));

    /// <summary>Ultimo messaggio (errore o avviso) da mostrare all'utente; null se tutto ok.</summary>
    public string? Message { get; private set; }

    public bool Enabled => _settings.ObsEnabled;
    public bool IsRecording => State == RecState.Recording;

    /// <summary>C'è una registrazione aperta (anche in pausa) o in avvio.</summary>
    public bool HasRecording => State is RecState.Recording or RecState.Paused or RecState.Starting;
    public long? RecordingActivityId => _activityId;

    public event Action? Changed;

    /// <summary>Una registrazione è stata chiusa e allegata all'attività (id dell'allegato).</summary>
    public event Action<long>? AudioSaved;

    // ---------- richieste dall'esterno ----------
    /// <summary>Da chiamare a ogni cambio di attività: porta lo stato della registrazione in linea con quello voluto.</summary>
    public void Reconcile()
    {
        _dirty = true;
        _ = RunAsync();
    }

    /// <summary>Pulsante manuale: ferma (o avvia) la registrazione dell'attività in corso, a prescindere dal tipo.</summary>
    public void ToggleManual()
    {
        if (_tracker.Current is not { } cur) return;
        if (_activityId == cur.Id || State == RecState.Starting)
        {
            _forced.Remove(cur.Id);
            _suppressed.Add(cur.Id);
            _forceStop = true; // l'utente vuole chiuderla davvero, non metterla in pausa
        }
        else
        {
            _suppressed.Remove(cur.Id);
            _forced.Add(cur.Id);
            Message = null;
        }
        Reconcile();
    }

    /// <summary>All'avvio di TimeHero: apre OBS (senza registrare) così è pronto.</summary>
    public async Task WarmUpAsync()
    {
        if (!Enabled || !_settings.ObsKeepReady) return;
        try
        {
            await using var s = await ConnectAsync(launch: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TimeHero: OBS non pronto: {ex.Message}");
        }
    }

    /// <summary>Chiusura di TimeHero: ferma e salva la registrazione in corso. Va eseguito fuori dal thread UI.</summary>
    public async Task StopForExitAsync()
    {
        _quiet = true;
        await _gate.WaitAsync(TimeSpan.FromSeconds(8));
        try
        {
            if (_activityId is not null) await StopCoreAsync();
        }
        catch
        {
            // in uscita non c'è niente da mostrare
        }
    }

    // ---------- logica ----------
    private bool ShouldRecord(TimeHero.Core.Activity a)
    {
        if (!Enabled) return false;
        if (_forced.Contains(a.Id)) return true;
        if (_suppressed.Contains(a.Id)) return false;
        return _store.GetCategories(true).FirstOrDefault(c => c.Id == a.CategoryId)?.Record == true;
    }

    private async Task RunAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // un altro ciclo è già in corso: vedrà _dirty
        try
        {
            while (_dirty)
            {
                _dirty = false;
                await StepAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
        if (_dirty) _ = RunAsync();
    }

    private async Task StepAsync()
    {
        var cur = _tracker.Current;
        if (cur?.Id != _lastSeenId)
        {
            _lastSeenId = cur?.Id;
            if (State == RecState.Idle) Message = null; // un errore riguarda l'attività di prima
        }

        if (_forceStop)
        {
            _forceStop = false;
            if (_activityId is not null) await StopCoreAsync();
        }

        long? want = cur is not null && ShouldRecord(cur) ? cur.Id : null;

        if (_activityId is { } owner)
        {
            if (want == owner)
            {
                // la stessa attività è tornata in corso: la registrazione in pausa riprende (stesso file)
                if (State == RecState.Paused) await ResumeCoreAsync();
                if (_activityId == owner) return;
            }
            else if (want is null && Enabled && _tracker.Paused.Any(a => a.Id == owner))
            {
                // l'attività è in pausa (o ne è in corso una che non si registra): in pausa anche la registrazione
                if (State == RecState.Recording) await PauseCoreAsync();
                if (_dirty) return; // nel frattempo è cambiato qualcosa (es. ripresa): si rivaluta da capo
                if (_activityId == owner)
                {
                    if (PauseExpired()) await StopCoreAsync();
                    else { ScheduleExpiry(); return; }
                }
            }
            else
            {
                // attività finita, disattivata, oppure un'altra attività da registrare: il file si chiude
                await StopCoreAsync();
            }
        }

        if (want is { } id && _activityId is null) await StartCoreAsync(id);
    }

    private bool PauseExpired() =>
        _pausedUtc is { } t && PauseTimeout > TimeSpan.Zero && DateTime.UtcNow - t >= PauseTimeout;

    /// <summary>Se nessuno la riprende, la registrazione in pausa si chiude da sola (così il file viene salvato).</summary>
    private void ScheduleExpiry()
    {
        var timeout = PauseTimeout;
        if (timeout <= TimeSpan.Zero || _pausedUtc is not { } since) return;
        var token = ++_expiryToken;
        var remaining = since + timeout - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        _ = Task.Delay(remaining + TimeSpan.FromMilliseconds(200)).ContinueWith(_ =>
            Post(() => { if (token == _expiryToken) Reconcile(); }));
    }

    private void Post(Action action)
    {
        if (_ctx is not null) _ctx.Post(_ => action(), null);
        else action();
    }

    private async Task StartCoreAsync(long activityId)
    {
        SetState(RecState.Starting, null);
        try
        {
            await using var s = await ConnectAsync(launch: true);

            var status = await s.RequestAsync("GetRecordStatus");
            if (status?.GetProperty("outputActive").GetBoolean() == true)
            {
                // sta già registrando (avviata a mano da OBS): non la tocchiamo, né la fermiamo dopo
                _owns = false;
                _activityId = activityId;
                _recordedBefore = TimeSpan.Zero;
                _segmentStartUtc = DateTime.UtcNow;
                SetState(RecState.Recording, "OBS stava già registrando: la lascio com'è.");
                return;
            }

            await SwitchProfileAsync(s);
            await s.RequestAsync("StartRecord");
            _owns = true;
            _activityId = activityId;
            _recordedBefore = TimeSpan.Zero;
            _segmentStartUtc = DateTime.UtcNow;
            _pausedUtc = null;
            SetState(RecState.Recording, null);
        }
        catch (Exception ex)
        {
            _suppressed.Add(activityId); // niente tentativi a raffica; il pulsante REC permette di riprovare
            _activityId = null;
            SetState(RecState.Idle, $"Registrazione non avviata: {ex.Message}");
            Notify("Registrazione non avviata", ex.Message);
        }
    }

    /// <summary>Mette in pausa la registrazione di OBS; se OBS non lo permette la chiude (come prima).</summary>
    private async Task PauseCoreAsync()
    {
        if (!_owns)
        {
            // registrazione avviata dall'utente in OBS: non è nostra, la lasciamo stare
            _activityId = null;
            SetState(RecState.Idle, null);
            return;
        }
        try
        {
            await using var s = await ConnectAsync(launch: false);
            await s.RequestAsync("PauseRecord");
            _recordedBefore += DateTime.UtcNow - _segmentStartUtc;
            _pausedUtc = DateTime.UtcNow;
            SetState(RecState.Paused, null);
        }
        catch
        {
            // pausa non supportata (es. "stesso encoder dello streaming") o OBS non raggiungibile: si chiude il file
            await StopCoreAsync();
        }
    }

    /// <summary>Riprende la registrazione in pausa. Se non è più possibile, l'attività ne avvierà una nuova.</summary>
    private async Task ResumeCoreAsync()
    {
        try
        {
            await using var s = await ConnectAsync(launch: false);
            await s.RequestAsync("ResumeRecord");
            _segmentStartUtc = DateTime.UtcNow;
            _pausedUtc = null;
            _expiryToken++;
            SetState(RecState.Recording, null);
        }
        catch (Exception ex)
        {
            // la registrazione in pausa non c'è più (chiusa da OBS o da un riavvio): ne parte una nuova
            _activityId = null;
            _owns = false;
            _pausedUtc = null;
            _recordedBefore = TimeSpan.Zero;
            SetState(RecState.Idle, $"La registrazione in pausa non era più disponibile ({ex.Message}): ne avvio una nuova.");
        }
    }

    private async Task StopCoreAsync()
    {
        _expiryToken++; // annulla l'eventuale chiusura programmata
        var activityId = _activityId!.Value;
        var owned = _owns;
        SetState(RecState.Stopping, null);
        string? path = null;
        string? error = null;
        try
        {
            if (owned)
            {
                await using var s = await ConnectAsync(launch: false);
                var data = await s.RequestAsync("StopRecord");
                path = data?.TryGetProperty("outputPath", out var p) == true ? p.GetString() : null;
                await RestoreProfileAsync(s);
            }
        }
        catch (Exception ex)
        {
            error = $"Registrazione non chiusa correttamente: {ex.Message}";
            Notify("Registrazione non chiusa correttamente", ex.Message);
        }

        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var attachmentId = _store.AddAttachment(activityId, path, "audio");
                if (!_quiet) AudioSaved?.Invoke(attachmentId);
            }
            catch (Exception ex) { error ??= $"File registrato ma non allegato: {ex.Message}"; }
        }

        _activityId = null;
        _owns = false;
        _pausedUtc = null;
        _recordedBefore = TimeSpan.Zero;
        SetState(RecState.Idle, error);
    }

    // ---------- profilo / raccolta di scene ----------
    private async Task SwitchProfileAsync(ObsSession s)
    {
        _previousProfile = _previousCollection = null;
        var profile = _settings.ObsProfile.Trim();
        var collection = _settings.ObsSceneCollection.Trim();

        if (profile.Length > 0)
        {
            var list = await s.RequestAsync("GetProfileList");
            var current = list?.GetProperty("currentProfileName").GetString();
            if (!string.Equals(current, profile, StringComparison.Ordinal))
            {
                await s.RequestAsync("SetCurrentProfile", new { profileName = profile });
                _previousProfile = current;
            }
        }
        if (collection.Length > 0)
        {
            var list = await s.RequestAsync("GetSceneCollectionList");
            var current = list?.GetProperty("currentSceneCollectionName").GetString();
            if (!string.Equals(current, collection, StringComparison.Ordinal))
            {
                await s.RequestAsync("SetCurrentSceneCollection", new { sceneCollectionName = collection });
                _previousCollection = current;
            }
        }
    }

    /// <summary>Rimette il profilo/raccolta che l'utente aveva prima, così OBS torna come lo aveva lasciato.</summary>
    private async Task RestoreProfileAsync(ObsSession s)
    {
        try
        {
            if (_previousProfile is not null) await s.RequestAsync("SetCurrentProfile", new { profileName = _previousProfile });
            if (_previousCollection is not null) await s.RequestAsync("SetCurrentSceneCollection", new { sceneCollectionName = _previousCollection });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TimeHero: profilo OBS non ripristinato: {ex.Message}");
        }
        _previousProfile = _previousCollection = null;
    }

    // ---------- connessione e avvio di OBS ----------
    /// <summary>Si collega a OBS; se non è aperto (e <paramref name="launch"/>) lo avvia e attende il WebSocket.</summary>
    public async Task<ObsSession> ConnectAsync(bool launch)
    {
        var host = _settings.ObsHost;
        var port = _settings.ObsPort;
        var pw = _settings.ObsPassword;

        try
        {
            return await ReadyAsync(await ObsSession.ConnectAsync(host, port, pw, TimeSpan.FromSeconds(3)));
        }
        catch (ObsAuthException) { throw; }
        catch (ObsException ex) when (launch && ex.Code != ObsException.NotReady)
        {
            // OBS non risponde: probabilmente è chiuso
        }

        var running = Process.GetProcessesByName("obs64").Length > 0;
        if (!running)
        {
            var exe = FindObsExe() ?? throw new ObsException(
                "OBS non è installato nel percorso standard: indica il percorso di obs64.exe nelle impostazioni.");
            Process.Start(new ProcessStartInfo(exe, "--minimize-to-tray --disable-shutdown-check")
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!, // OBS vuole partire dalla sua cartella bin\64bit
                UseShellExecute = false,
            });
        }

        ObsException? last = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(1000);
            try
            {
                return await ReadyAsync(await ObsSession.ConnectAsync(host, port, pw, TimeSpan.FromSeconds(3)));
            }
            catch (ObsAuthException) { throw; }
            catch (ObsException ex) { last = ex; }
        }
        throw new ObsException(running
            ? "OBS è aperto ma il WebSocket non risponde: attivalo in OBS → Strumenti → Impostazioni WebSocket server."
            : $"OBS è stato avviato ma non risponde sul WebSocket ({last?.Message}).");
    }

    /// <summary>Aspetta che OBS abbia finito di caricarsi (profilo, scene) prima di usare la connessione.</summary>
    private static async Task<ObsSession> ReadyAsync(ObsSession session)
    {
        try
        {
            await session.WaitReadyAsync(TimeSpan.FromSeconds(40));
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public string? FindObsExe()
    {
        var configured = _settings.ObsExePath.Trim();
        if (configured.Length > 0 && File.Exists(configured)) return configured;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        return roots.Select(r => Path.Combine(r, "obs-studio", "bin", "64bit", "obs64.exe")).FirstOrDefault(File.Exists);
    }

    private void SetState(RecState state, string? message)
    {
        State = state;
        Message = message;
        if (!_quiet) Changed?.Invoke();
    }

    private void Notify(string title, string body)
    {
        if (_quiet) return;
        try { Notifier.Info(title, body); } catch { /* notifica non essenziale */ }
    }
}
