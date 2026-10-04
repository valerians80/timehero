using TimeHero.Core;
using Xunit;

namespace TimeHero.Core.Tests;

public class CoreTests
{
    private static TimeStore NewStore() => new(Database.Open(":memory:"));

    [Fact]
    public void Seeds_default_categories()
    {
        using var s = NewStore();
        var names = s.GetCategories().Select(c => c.Name).ToList();
        Assert.Contains("Chiamate", names);
        Assert.Contains("Gestione mail", names);
        Assert.Contains("Attività programmate", names);
    }

    [Fact]
    public void Starting_a_new_activity_closes_the_previous_one()
    {
        using var s = NewStore();
        var t = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
        var tracker = new ActivityTracker(s, () => t);
        var cat = s.GetCategories();

        var first = tracker.Start(cat[0].Id);
        t = t.AddMinutes(30);
        var second = tracker.Start(cat[1].Id);

        Assert.Equal(t, s.GetActivity(first.Id)!.EndUtc);
        Assert.Equal(second.Id, s.GetRunningActivity()!.Id);
        Assert.Single(s.GetActivities(t.AddDays(-1), t.AddDays(1)).Where(a => a.IsRunning));
    }

    [Fact]
    public void Running_activity_is_recovered_after_restart()
    {
        using var s = NewStore();
        var tracker = new ActivityTracker(s);
        tracker.Start(s.GetCategories()[0].Id);
        var again = new ActivityTracker(s);
        Assert.NotNull(again.Current);
    }

    [Fact]
    public void Clients_people_and_attachments_roundtrip()
    {
        using var s = NewStore();
        var cli = s.GetOrAddClient("Contoso");
        Assert.Equal(cli, s.GetOrAddClient("contoso"));
        var p = s.GetOrAddPerson("Mario Rossi");

        var tracker = new ActivityTracker(s);
        var a = tracker.Start(s.GetCategories()[1].Id, cli, new[] { p }, "call kickoff");
        s.AddAttachment(a.Id, @"C:\x\shot.png");
        tracker.Stop();

        var loaded = s.GetActivity(a.Id)!;
        Assert.Equal(cli, loaded.ClientId);
        Assert.Equal(new[] { p }, loaded.PersonIds);
        Assert.Single(s.GetAttachments(a.Id));
        Assert.False(loaded.IsRunning);
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
    public void Rows_summary_and_csv()
    {
        using var s = NewStore();
        var cli = s.GetOrAddClient("Fabrikam; \"SpA\"");
        var cats = s.GetCategories();
        var day = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Local);
        s.InsertActivity(new Activity { CategoryId = cats[0].Id, ClientId = cli,
            StartUtc = day.ToUniversalTime(), EndUtc = day.AddMinutes(40).ToUniversalTime() });
        s.InsertActivity(new Activity { CategoryId = cats[1].Id,
            StartUtc = day.AddHours(1).ToUniversalTime(), EndUtc = day.AddHours(1).AddMinutes(20).ToUniversalTime(), Notes = "a\nb" });

        var rows = Reporting.BuildRows(s, day.Date, day.Date.AddDays(1));
        Assert.Equal(2, rows.Count);
        Assert.Equal(TimeSpan.FromMinutes(60), TimeSpan.FromTicks(rows.Sum(r => r.Duration.Ticks)));

        var byCat = Reporting.SummarizeBy(rows, r => r.Category);
        Assert.Equal(cats[0].Name, byCat[0].Key);

        var csv = Reporting.ToCsv(rows, 15).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, csv.Length);
        Assert.Contains("\"Fabrikam; \"\"SpA\"\"\"", csv[1]);
        Assert.Contains(";45;0,75;", csv[1]); // 40 min -> 45 arrotondato
    }
}

public class SummaryTextTests
{
    [Fact]
    public void Summary_groups_by_day_client_category_and_rounds()
    {
        using var s = new TimeStore(Database.Open(":memory:"));
        var cli = s.GetOrAddClient("Contoso");
        var cat = s.GetCategories()[1];
        var day = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Local);
        foreach (var (off, len, note) in new[] { (0, 10, "kickoff"), (60, 10, "follow-up"), (120, 10, "kickoff") })
            s.InsertActivity(new Activity { CategoryId = cat.Id, ClientId = cli, Notes = note,
                StartUtc = day.AddMinutes(off).ToUniversalTime(), EndUtc = day.AddMinutes(off + len).ToUniversalTime() });

        var text = Reporting.ToSummaryText(Reporting.BuildRows(s, day.Date, day.Date.AddDays(1)), 15);
        Assert.Contains("totale 0h 30m", text);              // 30 min -> già multiplo di 15
        Assert.Contains("Contoso | Chiamate | 0,50 h | kickoff; follow-up", text);
    }
}
