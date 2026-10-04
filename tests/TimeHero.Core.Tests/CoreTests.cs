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
