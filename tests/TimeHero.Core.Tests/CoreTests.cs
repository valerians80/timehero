using Microsoft.Data.Sqlite;
using TimeHero.Core;
using Xunit;

namespace TimeHero.Core.Tests;

public class CoreTests
{
    private static TimeStore NewStore() => new(Database.Open(":memory:"));

    private static readonly DateTime Day9 = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Seeds_default_categories()
    {
        using var s = NewStore();
        var names = s.GetCategories().Select(c => c.Name).ToList();
        Assert.Contains("Chiamate", names);
        Assert.Contains("Attività programmate", names);
    }

    [Fact]
    public void Pause_and_resume_accumulate_time_on_the_same_activity()
    {
        using var s = NewStore();
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        var cat = s.GetCategories();

        var deploy = tracker.Start("Deploy VM per Contoso", cat[0].Id);   // 09:00
        t = t.AddMinutes(30);
        tracker.Start("Chiamata Mario", cat[1].Id);                       // 09:30 -> deploy in pausa
        Assert.Equal(deploy.Id, Assert.Single(tracker.Paused).Id);
        t = t.AddMinutes(15);
        tracker.Resume(deploy.Id);                                        // 09:45 -> riprende deploy
        t = t.AddMinutes(45);
        tracker.Finish();                                                 // 10:30 -> fine

        var d = s.GetActivity(deploy.Id)!;
        Assert.Equal(2, d.Segments.Count);                                // due sessioni, una sola attività
        Assert.Equal(TimeSpan.FromMinutes(75), d.Duration(t));            // 30 + 45
        Assert.True(d.IsClosed);

        // la chiamata è rimasta in pausa, non chiusa
        var call = Assert.Single(tracker.Paused);
        Assert.Equal("Chiamata Mario", call.Title);
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void Only_one_activity_runs_at_a_time_and_pausing_keeps_it_open()
    {
        using var s = NewStore();
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        var cat = s.GetCategories()[0].Id;

        tracker.Start("A", cat);
        t = t.AddMinutes(10);
        tracker.Start("B", cat);
        t = t.AddMinutes(10);
        tracker.Pause();

        Assert.Null(tracker.Current);
        Assert.Equal(2, tracker.Paused.Count);
        Assert.Empty(s.GetRunningActivities());
        Assert.Equal(2, s.GetOpenActivities().Count);
    }

    [Fact]
    public void Finishing_a_paused_activity_closes_it_at_end_of_last_session()
    {
        using var s = NewStore();
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        var a = tracker.Start("A", s.GetCategories()[0].Id);
        t = t.AddMinutes(20);
        tracker.Pause();
        t = t.AddHours(3);
        tracker.Finish(a.Id);

        var loaded = s.GetActivity(a.Id)!;
        Assert.Equal(Day9.ToUniversalTime().AddMinutes(20), loaded.ClosedUtc);
        Assert.Equal(TimeSpan.FromMinutes(20), loaded.Duration(t));
        Assert.Empty(tracker.Paused);
    }

    [Fact]
    public void Pause_can_be_backdated()
    {
        using var s = NewStore();
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        var a = tracker.Start("A", s.GetCategories()[0].Id);
        t = t.AddHours(1);
        tracker.Pause(Day9.ToUniversalTime().AddMinutes(25));
        Assert.Equal(TimeSpan.FromMinutes(25), s.GetActivity(a.Id)!.Duration(t));
    }

    [Fact]
    public void State_is_recovered_after_restart()
    {
        using var s = NewStore();
        var tracker = new ActivityTracker(s);
        var cat = s.GetCategories()[0].Id;
        tracker.Start("A", cat);
        tracker.Start("B", cat);

        var again = new ActivityTracker(s);
        Assert.Equal("B", again.Current!.Title);
        Assert.Equal("A", Assert.Single(again.Paused).Title);
    }

    [Fact]
    public void Clients_people_and_attachments_roundtrip()
    {
        using var s = NewStore();
        var cli = s.GetOrAddClient("Contoso");
        Assert.Equal(cli, s.GetOrAddClient("contoso"));
        var p = s.GetOrAddPerson("Mario Rossi");

        var tracker = new ActivityTracker(s);
        var a = tracker.Start("Kickoff", s.GetCategories()[1].Id, cli, new[] { p }, "note");
        s.AddAttachment(a.Id, @"C:\x\shot.png");
        tracker.Finish();

        var loaded = s.GetActivity(a.Id)!;
        Assert.Equal(cli, loaded.ClientId);
        Assert.Equal(new[] { p }, loaded.PersonIds);
        Assert.Single(s.GetAttachments(a.Id));
        Assert.True(loaded.IsClosed);
    }

    [Theory]
    [InlineData(7, 15, 0)]
    [InlineData(8, 15, 15)]
    [InlineData(22, 15, 15)]
    [InlineData(23, 15, 30)]
    [InlineData(23, 0, 23)]
    public void Rounding(int minutes, int step, int expected) =>
        Assert.Equal(TimeSpan.FromMinutes(expected), Reporting.Round(TimeSpan.FromMinutes(minutes), step));

    [Fact]
    public void History_has_one_row_per_activity_per_day_even_with_many_pauses()
    {
        using var s = NewStore();
        var t = Day9.AddHours(3).ToUniversalTime(); // 12:00
        var tracker = new ActivityTracker(s, () => t);
        var cat = s.GetCategories();
        var cli = s.GetOrAddClient("Fabrikam; \"SpA\"");

        var deploy = tracker.Start("Deploy VM", cat[0].Id, cli);   // 12:00
        t = t.AddMinutes(20);
        tracker.Start("Call", cat[1].Id);                           // 12:20
        t = t.AddMinutes(10);
        tracker.Resume(deploy.Id);                                  // 12:30
        t = t.AddMinutes(20);
        tracker.Pause();                                            // 12:50 -> deploy: 20 + 20 = 40 min
        t = t.AddMinutes(10);
        tracker.Resume(deploy.Id);                                  // 13:00
        t = t.AddMinutes(5);
        tracker.Finish();                                           // 13:05 -> deploy: 45 min

        var rows = Reporting.BuildRows(s, Day9.Date, Day9.Date.AddDays(1), t);
        Assert.Equal(2, rows.Count);                                // Deploy VM + Call, nient'altro
        var d = rows.Single(r => r.Title == "Deploy VM");
        Assert.Equal(TimeSpan.FromMinutes(45), d.Duration);
        Assert.Equal("chiusa", d.Status);
        Assert.Equal("in pausa", rows.Single(r => r.Title == "Call").Status);

        var csv = Reporting.ToCsv(rows, 15).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, csv.Length);
        Assert.Contains("\"Fabrikam; \"\"SpA\"\"\"", csv.Single(l => l.Contains("Deploy VM")));
        Assert.Contains(";45;0,75;Deploy VM;", csv.Single(l => l.Contains("Deploy VM")));

        var byTitle = Reporting.SummarizeBy(rows, r => r.Title);
        Assert.Equal("Deploy VM", byTitle[0].Key);
    }

    [Fact]
    public void Summary_text_groups_by_day_client_and_title()
    {
        using var s = NewStore();
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        var cli = s.GetOrAddClient("Contoso");
        var cat = s.GetCategories()[0].Id;

        tracker.Start("Deploy VM", cat, cli, notes: "kickoff");
        t = t.AddMinutes(30);
        tracker.Finish();

        var text = Reporting.ToSummaryText(Reporting.BuildRows(s, Day9.Date, Day9.Date.AddDays(1), t), 15);
        Assert.Contains("totale 0h 30m", text);
        Assert.Contains("Contoso | Deploy VM (Attività programmate) | 0,50 h | kickoff", text);
    }

    [Fact]
    public void SaveActivity_replaces_segments_and_can_reopen()
    {
        using var s = NewStore();
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        var a = tracker.Start("A", s.GetCategories()[0].Id);
        t = t.AddMinutes(10);
        tracker.Finish();

        var loaded = s.GetActivity(a.Id)!;
        loaded.Title = "A bis";
        s.SaveActivity(loaded, new[]
        {
            (Day9.ToUniversalTime(), Day9.ToUniversalTime().AddMinutes(30)),
            (Day9.ToUniversalTime().AddHours(2), Day9.ToUniversalTime().AddHours(2).AddMinutes(15)),
        }, closed: false);

        var after = s.GetActivity(a.Id)!;
        Assert.Equal("A bis", after.Title);
        Assert.Equal(2, after.Segments.Count);
        Assert.Equal(TimeSpan.FromMinutes(45), after.Duration(t));
        Assert.True(after.IsPaused);
    }

    [Fact]
    public void Old_database_is_migrated_to_activities_with_segments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timehero_{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var old = new SqliteConnection($"Data Source={path}"))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Categories (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE,
                        Color TEXT NOT NULL DEFAULT '#F2C94C', SortOrder INTEGER NOT NULL DEFAULT 0, Active INTEGER NOT NULL DEFAULT 1);
                    INSERT INTO Categories (Name) VALUES ('Chiamate');
                    CREATE TABLE Clients (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE, Code TEXT, Active INTEGER NOT NULL DEFAULT 1);
                    CREATE TABLE People (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE, Email TEXT, Role TEXT);
                    CREATE TABLE Activities (Id INTEGER PRIMARY KEY AUTOINCREMENT, CategoryId INTEGER NOT NULL, ClientId INTEGER,
                        StartUtc TEXT NOT NULL, EndUtc TEXT, Notes TEXT, Billable INTEGER NOT NULL DEFAULT 1);
                    INSERT INTO Activities (CategoryId, StartUtc, EndUtc) VALUES (1, '2026-10-05T07:00:00.0000000Z', '2026-10-05T07:30:00.0000000Z');
                    INSERT INTO Activities (CategoryId, StartUtc, EndUtc) VALUES (1, '2026-10-05T08:00:00.0000000Z', NULL);
                    """;
                cmd.ExecuteNonQuery();
            }

            using var s = new TimeStore(Database.Open(path));
            var all = s.GetOpenActivities();
            var running = Assert.Single(all);
            Assert.True(running.IsRunning);
            Assert.Equal("Chiamate", running.Title);
            var closed = s.GetActivity(1)!;
            Assert.True(closed.IsClosed);
            Assert.Equal(TimeSpan.FromMinutes(30), closed.Duration(DateTime.UtcNow));

            // riaprire un database già migrato non deve rifare la migrazione
            s.Dispose();
            using var again = new TimeStore(Database.Open(path));
            Assert.Single(again.GetActivity(1)!.Segments);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}

public class NotesAndTimelineTests
{
    private static readonly DateTime Day9 = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Timeline_orders_starts_notes_pauses_and_close()
    {
        using var s = new TimeStore(Database.Open(":memory:"));
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        var cat = s.GetCategories()[0].Id;

        var a = tracker.Start("Deploy VM", cat);                 // 09:00 avviata
        t = t.AddMinutes(10);
        tracker.AddNote("creata la VM, manca la rete");          // 09:10
        t = t.AddMinutes(20);
        tracker.Start("Call", cat);                              // 09:30 deploy in pausa
        t = t.AddMinutes(15);
        tracker.Resume(a.Id);                                    // 09:45 ripresa
        t = t.AddMinutes(5);
        tracker.AddNote("rete sistemata");                       // 09:50
        t = t.AddMinutes(10);
        tracker.Finish();                                        // 10:00 chiusa

        var loaded = s.GetActivity(a.Id)!;
        var events = Timeline.Build(loaded, s.GetNotes(a.Id), nowUtc: t);
        Assert.Equal(
            new[] { TimelineKind.Start, TimelineKind.Note, TimelineKind.Pause, TimelineKind.Resume, TimelineKind.Note, TimelineKind.Close },
            events.Select(e => e.Kind).ToArray());
        Assert.Equal("creata la VM, manca la rete", events[1].Text);
        Assert.Contains("sessione di 30 min", events[2].Text);
        Assert.Contains("Chiusa", events[5].Text);
    }

    [Fact]
    public void Notes_need_a_running_activity_and_non_empty_text()
    {
        using var s = new TimeStore(Database.Open(":memory:"));
        var tracker = new ActivityTracker(s);
        Assert.Null(tracker.AddNote("niente in corso"));
        tracker.Start("A", s.GetCategories()[0].Id);
        Assert.Null(tracker.AddNote("   "));
        Assert.NotNull(tracker.AddNote("ok"));
    }

    [Fact]
    public void Notes_can_be_deleted_and_cascade_with_the_activity()
    {
        using var s = new TimeStore(Database.Open(":memory:"));
        var tracker = new ActivityTracker(s);
        var a = tracker.Start("A", s.GetCategories()[0].Id);
        var n1 = tracker.AddNote("uno")!;
        tracker.AddNote("due");
        s.DeleteNote(n1.Id);
        Assert.Equal("due", Assert.Single(s.GetNotes(a.Id)).Text);
        s.DeleteActivity(a.Id);
        Assert.Empty(s.GetNotes(a.Id));
    }

    [Fact]
    public void Day_log_appears_in_csv_and_summary()
    {
        using var s = new TimeStore(Database.Open(":memory:"));
        var t = Day9.ToUniversalTime();
        var tracker = new ActivityTracker(s, () => t);
        tracker.Start("Deploy VM", s.GetCategories()[0].Id);
        t = t.AddMinutes(20);
        tracker.AddNote("aperto ticket");
        t = t.AddMinutes(10);
        tracker.Finish();

        var rows = Reporting.BuildRows(s, Day9.Date, Day9.Date.AddDays(1), t);
        Assert.Equal("09:20 aperto ticket", rows[0].Log);
        Assert.Contains(";09:20 aperto ticket;", Reporting.ToCsv(rows));
        Assert.Contains("| 09:20 aperto ticket", Reporting.ToSummaryText(rows, 15));
    }
}

public class RecordingTests
{
    [Fact]
    public void Obs_auth_matches_the_documented_example()
    {
        // esempio della documentazione di obs-websocket v5
        var auth = ObsProtocol.ComputeAuth("supersecretpassword",
            "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=",
            "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", auth);
    }

    [Fact]
    public void Identify_includes_auth_only_when_obs_requires_it()
    {
        var (_, withAuth) = ObsProtocol.ParseMessage(
            """{"op":0,"d":{"obsWebSocketVersion":"5.1.0","rpcVersion":1,"authentication":{"challenge":"c","salt":"s"}}}""");
        Assert.True(ObsProtocol.RequiresAuth(withAuth));
        var identify = ObsProtocol.BuildIdentify(withAuth, "pw");
        var (op, d) = ObsProtocol.ParseMessage(identify);
        Assert.Equal(ObsProtocol.OpIdentify, op);
        Assert.Equal(ObsProtocol.ComputeAuth("pw", "s", "c"), d.GetProperty("authentication").GetString());

        var (_, noAuth) = ObsProtocol.ParseMessage("""{"op":0,"d":{"obsWebSocketVersion":"5.1.0","rpcVersion":1}}""");
        Assert.False(ObsProtocol.RequiresAuth(noAuth));
        Assert.DoesNotContain("authentication", ObsProtocol.BuildIdentify(noAuth, "pw"));
    }

    [Fact]
    public void Request_and_response_roundtrip()
    {
        var req = ObsProtocol.BuildRequest("SetCurrentProfile", "id1", new { profileName = "TimeHero" });
        Assert.Contains("\"requestType\":\"SetCurrentProfile\"", req);
        Assert.Contains("\"profileName\":\"TimeHero\"", req);

        var (op, d) = ObsProtocol.ParseMessage(
            """{"op":7,"d":{"requestType":"StopRecord","requestId":"id1","requestStatus":{"result":true,"code":100},"responseData":{"outputPath":"C:\\rec\\a.mkv"}}}""");
        Assert.Equal(ObsProtocol.OpRequestResponse, op);
        var r = ObsProtocol.ParseResponse(d);
        Assert.True(r.Ok);
        Assert.Equal("id1", r.RequestId);
        Assert.Equal(@"C:\rec\a.mkv", r.Data!.Value.GetProperty("outputPath").GetString());

        var (_, bad) = ObsProtocol.ParseMessage(
            """{"op":7,"d":{"requestType":"StartRecord","requestId":"id2","requestStatus":{"result":false,"code":500,"comment":"Already active"}}}""");
        var fail = ObsProtocol.ParseResponse(bad);
        Assert.False(fail.Ok);
        Assert.Equal("Already active", fail.Comment);
    }

    [Fact]
    public void Default_categories_flag_calls_and_meetings_for_recording()
    {
        using var s = new TimeStore(Database.Open(":memory:"));
        var cats = s.GetCategories();
        Assert.True(cats.Single(c => c.Name == "Chiamate").Record);
        Assert.True(cats.Single(c => c.Name == "Riunioni").Record);
        Assert.False(cats.Single(c => c.Name == "Gestione mail").Record);

        var mail = cats.Single(c => c.Name == "Gestione mail");
        s.SetCategoryRecord(mail.Id, true);
        Assert.True(s.GetCategories().Single(c => c.Id == mail.Id).Record);
    }

    [Fact]
    public void Attachments_keep_their_kind()
    {
        using var s = new TimeStore(Database.Open(":memory:"));
        var a = new ActivityTracker(s).Start("Call", s.GetCategories()[1].Id);
        s.AddAttachment(a.Id, @"C:\x\shot.png");
        s.AddAttachment(a.Id, @"C:\rec\call.mkv", "audio");
        var list = s.GetAttachments(a.Id);
        Assert.Equal("image", list[0].Kind);
        Assert.Equal("audio", list[1].Kind);
    }

    [Fact]
    public void Old_database_gets_recording_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timehero_{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var old = new SqliteConnection($"Data Source={path}"))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Categories (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE,
                        Color TEXT NOT NULL DEFAULT '#F2C94C', SortOrder INTEGER NOT NULL DEFAULT 0, Active INTEGER NOT NULL DEFAULT 1);
                    INSERT INTO Categories (Name) VALUES ('Chiamate'), ('Gestione mail');
                    """;
                cmd.ExecuteNonQuery();
            }
            using var s = new TimeStore(Database.Open(path));
            var cats = s.GetCategories();
            Assert.True(cats.Single(c => c.Name == "Chiamate").Record);
            Assert.False(cats.Single(c => c.Name == "Gestione mail").Record);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_finds_the_remuxed_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"th_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var mkv = Path.Combine(dir, "call.mkv");
            var mp4 = Path.Combine(dir, "call.mp4");
            File.WriteAllText(mp4, "x");
            Assert.Equal(mp4, RecordingFiles.Resolve(mkv));   // l'mkv è stato rimuxato in mp4
            File.WriteAllText(mkv, "x");
            Assert.Equal(mkv, RecordingFiles.Resolve(mkv));   // se esiste, resta quello
            Assert.Equal(Path.Combine(dir, "none.mkv"), RecordingFiles.Resolve(Path.Combine(dir, "none.mkv")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ObsSessionTests
{
    [Fact]
    public async Task Connects_without_password_and_runs_a_recording_cycle()
    {
        using var obs = new FakeObsServer();
        await using var s = await ObsSession.ConnectAsync("localhost", obs.Port, null);

        var ver = await s.RequestAsync("GetVersion");
        Assert.Equal("30.2.3", ver!.Value.GetProperty("obsVersion").GetString());

        await s.RequestAsync("StartRecord");
        Assert.True(obs.Recording);
        var stop = await s.RequestAsync("StopRecord");
        Assert.False(obs.Recording);
        Assert.Equal(@"C:\Rec\chiamata.mkv", stop!.Value.GetProperty("outputPath").GetString());
    }

    [Fact]
    public async Task Authenticates_with_the_password()
    {
        using var obs = new FakeObsServer { Password = "supersecretpassword" };
        await using var s = await ObsSession.ConnectAsync("localhost", obs.Port, "supersecretpassword");
        var status = await s.RequestAsync("GetRecordStatus");
        Assert.False(status!.Value.GetProperty("outputActive").GetBoolean());
    }

    [Fact]
    public async Task Wrong_password_raises_auth_exception()
    {
        using var obs = new FakeObsServer { Password = "giusta" };
        await Assert.ThrowsAsync<ObsAuthException>(() => ObsSession.ConnectAsync("localhost", obs.Port, "sbagliata"));
    }

    [Fact]
    public async Task Missing_password_gives_a_clear_message()
    {
        using var obs = new FakeObsServer { Password = "giusta" };
        var ex = await Assert.ThrowsAsync<ObsAuthException>(() => ObsSession.ConnectAsync("localhost", obs.Port, ""));
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_request_raises_with_obs_comment()
    {
        using var obs = new FakeObsServer { RejectStart = "No audio device" };
        await using var s = await ObsSession.ConnectAsync("localhost", obs.Port, null);
        var ex = await Assert.ThrowsAsync<ObsException>(() => s.RequestAsync("StartRecord"));
        Assert.Contains("No audio device", ex.Message);
    }

    [Fact]
    public async Task Nothing_listening_raises_obs_exception()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await Assert.ThrowsAsync<ObsException>(() => ObsSession.ConnectAsync("localhost", port, null, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Request_returns_the_obs_status_code_on_failure()
    {
        using var obs = new FakeObsServer { NotReadyRequests = 1 };
        await using var s = await ObsSession.ConnectAsync("localhost", obs.Port, null);
        var ex = await Assert.ThrowsAsync<ObsException>(() => s.RequestAsync("GetVersion"));
        Assert.Equal(ObsException.NotReady, ex.Code);
    }

    [Fact]
    public async Task WaitReady_retries_while_obs_is_still_loading()
    {
        using var obs = new FakeObsServer { NotReadyRequests = 3 };
        await using var s = await ObsSession.ConnectAsync("localhost", obs.Port, null);
        await s.WaitReadyAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, obs.Calls.Count(c => c == "GetVersion")); // 3 rifiuti + 1 riuscita
        Assert.Equal("30.2.3", (await s.RequestAsync("GetVersion"))!.Value.GetProperty("obsVersion").GetString());
    }

    [Fact]
    public async Task WaitReady_gives_up_after_the_timeout()
    {
        using var obs = new FakeObsServer { NotReadyRequests = 1000 };
        await using var s = await ObsSession.ConnectAsync("localhost", obs.Port, null);
        var ex = await Assert.ThrowsAsync<ObsException>(() => s.WaitReadyAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(ObsException.NotReady, ex.Code);
    }
}

public class TranscriptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"th_tr_{Guid.NewGuid():N}");

    public TranscriptionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* ok */ } }

    private string Audio(string name, int bytes = 2048)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray());
        return p;
    }

    private static TranscriptionOptions Opts(FakeAzureServer az, string? key = "k1", string deployment = "whisper") =>
        new(az.Endpoint, deployment, "2025-03-01-preview", key, null, "it");

    private static AzureTranscriber Api() => new(null, TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task Sends_file_with_key_and_returns_text()
    {
        using var az = new FakeAzureServer { RequiredApiKey = "k1" };
        var file = Audio("call.mp4");
        var text = await Api().TranscribeAsync(file, Opts(az), "Cliente: Contoso.");

        Assert.Equal("Buongiorno, iniziamo la riunione.", text);
        Assert.Equal("/openai/deployments/whisper/audio/transcriptions", az.LastPath);
        Assert.Contains("api-version=2025-03-01-preview", az.LastQuery);
        Assert.Equal("k1", az.LastApiKeyHeader);
        Assert.Contains("filename=call.mp4", az.LastBodyText);
        Assert.Contains("name=language", az.LastBodyText);
        Assert.Contains("Cliente: Contoso.", az.LastBodyText);
        Assert.True(az.LastBodyLength > 2048);   // il file è nel corpo
    }

    [Fact]
    public async Task Uses_bearer_token_when_there_is_no_key()
    {
        using var az = new FakeAzureServer { RequiredBearer = "tok123" };
        var o = new TranscriptionOptions(az.Endpoint, "gpt-4o-transcribe", TokenProvider: _ => Task.FromResult("tok123"));
        var text = await Api().TranscribeAsync(Audio("a.m4a"), o);
        Assert.NotEmpty(text);
        Assert.Equal("Bearer tok123", az.LastAuthorization);
        Assert.Null(az.LastApiKeyHeader);
    }

    [Fact]
    public async Task Retries_after_429_then_succeeds()
    {
        using var az = new FakeAzureServer { FailFirst = 2, FailStatus = 429 };
        var text = await Api().TranscribeAsync(Audio("a.mp4"), Opts(az));
        Assert.NotEmpty(text);
        Assert.Equal(3, az.Requests);
    }

    [Fact]
    public async Task Maps_errors_to_clear_messages()
    {
        using var az = new FakeAzureServer { RequiredApiKey = "giusta" };
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => Api().TranscribeAsync(Audio("a.mp4"), Opts(az, "sbagliata")));
        Assert.Contains("Accesso negato", ex.Message);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, ex.Status);

        using var nf = new FakeAzureServer { AlwaysStatus = 404, AlwaysBody = "{\"error\":{\"message\":\"DeploymentNotFound\"}}" };
        var ex2 = await Assert.ThrowsAsync<TranscriptionException>(() => Api().TranscribeAsync(Audio("b.mp4"), Opts(nf)));
        Assert.Contains("Deployment non trovato", ex2.Message);
        Assert.Contains("DeploymentNotFound", ex2.Message);

        using var bad = new FakeAzureServer { AlwaysStatus = 400, AlwaysBody = "{\"error\":{\"message\":\"Invalid file format.\"}}" };
        var ex3 = await Assert.ThrowsAsync<TranscriptionException>(() => Api().TranscribeAsync(Audio("c.mp4"), Opts(bad)));
        Assert.Contains("Invalid file format.", ex3.Message);
        Assert.Equal(1, bad.Requests); // 400: nessun nuovo tentativo
    }

    [Fact]
    public async Task Gives_up_after_three_server_errors()
    {
        using var az = new FakeAzureServer { AlwaysStatus = 503 };
        await Assert.ThrowsAsync<TranscriptionException>(() => Api().TranscribeAsync(Audio("a.mp4"), Opts(az)));
        Assert.Equal(3, az.Requests);
    }

    [Fact]
    public async Task Unconfigured_options_are_rejected()
    {
        var o = new TranscriptionOptions("", "", ApiKey: null);
        Assert.False(o.IsConfigured);
        await Assert.ThrowsAsync<TranscriptionException>(() => Api().TranscribeAsync(Audio("a.mp4"), o));
    }

    // ---------- servizio: dal file all'attività ----------
    private static (TimeStore Store, long ActivityId, long AttachmentId) Setup(string audioPath)
    {
        var s = new TimeStore(Database.Open(":memory:"));
        var cli = s.GetOrAddClient("Contoso");
        var p = s.GetOrAddPerson("Mario Rossi");
        var a = new ActivityTracker(s).Start("Kickoff migrazione", s.GetCategories().Single(c => c.Name == "Chiamate").Id, cli, new[] { p });
        var att = s.AddAttachment(a.Id, audioPath, "audio");
        return (s, a.Id, att);
    }

    private static TranscriptionService Service(TimeStore s) =>
        new(s, new AzureTranscriber(null, TimeSpan.FromMilliseconds(50)))
        { FileWait = TimeSpan.FromSeconds(3), FilePoll = TimeSpan.FromMilliseconds(40) };

    [Fact]
    public async Task Service_transcribes_and_saves_with_context_prompt()
    {
        using var az = new FakeAzureServer();
        var (s, actId, attId) = Setup(Audio("call.mp4"));
        using var _ = s;

        var t = await Service(s).TranscribeAsync(attId, Opts(az));
        Assert.Equal("done", t.Status);
        Assert.Equal("Buongiorno, iniziamo la riunione.", t.Text);
        Assert.Equal("whisper", t.Model);
        Assert.Equal(attId, t.AttachmentId);
        Assert.Contains("Cliente: Contoso.", az.LastBodyText);
        Assert.Contains("Partecipanti: Mario Rossi.", az.LastBodyText);
        Assert.Contains("Kickoff migrazione", az.LastBodyText);
        Assert.Single(s.GetTranscripts(actId));
    }

    [Fact]
    public async Task Service_replaces_the_previous_transcript_on_retry()
    {
        using var az = new FakeAzureServer { AlwaysStatus = 401, AlwaysBody = "{}" };
        var (s, actId, attId) = Setup(Audio("call.mp4"));
        using var _ = s;
        var svc = Service(s);

        var failed = await svc.TranscribeAsync(attId, Opts(az));
        Assert.Equal("error", failed.Status);
        Assert.Contains("Accesso negato", failed.Error);

        az.AlwaysStatus = null;
        var ok = await svc.TranscribeAsync(attId, Opts(az));
        Assert.Equal("done", ok.Status);
        Assert.Equal(failed.Id, ok.Id);                      // stessa riga, non un duplicato
        Assert.Single(s.GetTranscripts(actId));
    }

    [Fact]
    public async Task Service_reports_unsupported_format_missing_and_oversized_files()
    {
        using var az = new FakeAzureServer();

        var (s1, _, att1) = Setup(Audio("call.mkv"));
        using (s1)
        {
            var t = await Service(s1).TranscribeAsync(att1, Opts(az));
            Assert.Equal("error", t.Status);
            Assert.Contains("mp4", t.Error);
        }

        var (s2, _, att2) = Setup(Path.Combine(_dir, "sparito.mp4"));
        using (s2)
        {
            var t = await Service(s2).TranscribeAsync(att2, Opts(az));
            Assert.Contains("non trovato", t.Error);
        }

        var (s3, _, att3) = Setup(Audio("grande.mp4", 5000));
        using (s3)
        {
            var svc = Service(s3);
            svc.MaxBytes = 4096;
            var t = await svc.TranscribeAsync(att3, Opts(az));
            Assert.Contains("supera il limite", t.Error);
        }
        Assert.Equal(0, az.Requests);   // nessuna chiamata inutile al servizio
    }

    [Fact]
    public async Task Service_waits_for_obs_to_remux_mkv_to_mp4()
    {
        using var az = new FakeAzureServer();
        var mkv = Audio("call.mkv");
        var (s, _, attId) = Setup(mkv);
        using var store = s;

        // dopo un po' OBS finisce il rimux e compare l'mp4
        var remux = Task.Run(async () => { await Task.Delay(300); Audio("call.mp4"); });
        var t = await Service(s).TranscribeAsync(attId, Opts(az));
        Assert.Equal("done", t.Status);
        Assert.Contains("filename=call.mp4", az.LastBodyText);
    }

    [Fact]
    public void Timeline_includes_transcripts()
    {
        var (s, actId, attId) = Setup(Audio("call.mp4"));
        using var _ = s;
        s.SaveTranscript(attId, actId, "whisper", "uno due tre", "done", null);
        var a = s.GetActivity(actId)!;
        var ev = Timeline.Build(a, s.GetNotes(actId), s.GetTranscripts(actId));
        var tr = Assert.Single(ev, e => e.Kind == TimelineKind.Transcript);
        Assert.Contains("3 parole", tr.Text);
        Assert.NotNull(tr.TranscriptId);
    }
}
