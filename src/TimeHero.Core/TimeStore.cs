using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TimeHero.Core;

/// <summary>Accesso ai dati (CRUD) su SQLite.</summary>
public sealed class TimeStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public TimeStore(SqliteConnection conn) => _conn = conn;

    public static TimeStore OpenFile(string? path = null) => new(Database.Open(path ?? Database.DefaultPath));

    public void Dispose() => _conn.Dispose();

    // ---------- helper ----------
    private SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private long InsertId(SqliteCommand c)
    {
        c.CommandText += "; SELECT last_insert_rowid();";
        return (long)c.ExecuteScalar()!;
    }

    // ---------- clienti ----------
    public IReadOnlyList<Client> GetClients(bool includeInactive = false)
    {
        using var c = Cmd("SELECT Id, Name, Code, Active FROM Clients " +
                          (includeInactive ? "" : "WHERE Active = 1 ") + "ORDER BY Name COLLATE NOCASE");
        using var r = c.ExecuteReader();
        var list = new List<Client>();
        while (r.Read())
            list.Add(new Client(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) == 1));
        return list;
    }

    public long AddClient(string name, string? code = null)
    {
        using var c = Cmd("INSERT INTO Clients (Name, Code) VALUES ($n, $c)", ("$n", name.Trim()), ("$c", code));
        return InsertId(c);
    }

    public void SetClientActive(long id, bool active)
    {
        using var c = Cmd("UPDATE Clients SET Active = $a WHERE Id = $i", ("$a", active ? 1 : 0), ("$i", id));
        c.ExecuteNonQuery();
    }

    /// <summary>Restituisce l'id del cliente col nome dato, creandolo se non esiste.</summary>
    public long GetOrAddClient(string name)
    {
        using var c = Cmd("SELECT Id FROM Clients WHERE Name = $n COLLATE NOCASE", ("$n", name.Trim()));
        return c.ExecuteScalar() is long id ? id : AddClient(name);
    }

    // ---------- colleghi ----------
    public IReadOnlyList<Person> GetPeople()
    {
        using var c = Cmd("SELECT Id, Name, Email, Role FROM People ORDER BY Name COLLATE NOCASE");
        using var r = c.ExecuteReader();
        var list = new List<Person>();
        while (r.Read())
            list.Add(new Person(r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public long AddPerson(string name, string? email = null, string? role = null)
    {
        using var c = Cmd("INSERT INTO People (Name, Email, Role) VALUES ($n, $e, $r)",
            ("$n", name.Trim()), ("$e", email), ("$r", role));
        return InsertId(c);
    }

    public long GetOrAddPerson(string name)
    {
        using var c = Cmd("SELECT Id FROM People WHERE Name = $n COLLATE NOCASE", ("$n", name.Trim()));
        return c.ExecuteScalar() is long id ? id : AddPerson(name);
    }

    public void DeletePerson(long id)
    {
        using var c = Cmd("DELETE FROM ActivityPeople WHERE PersonId = $i; DELETE FROM People WHERE Id = $i", ("$i", id));
        c.ExecuteNonQuery();
    }

    // ---------- categorie ----------
    public IReadOnlyList<Category> GetCategories(bool includeInactive = false)
    {
        using var c = Cmd("SELECT Id, Name, Color, SortOrder, Active FROM Categories " +
                          (includeInactive ? "" : "WHERE Active = 1 ") + "ORDER BY SortOrder, Name");
        using var r = c.ExecuteReader();
        var list = new List<Category>();
        while (r.Read())
            list.Add(new Category(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4) == 1));
        return list;
    }

    public long AddCategory(string name, string color = "#F2C94C")
    {
        using var c = Cmd("INSERT INTO Categories (Name, Color, SortOrder) " +
                          "VALUES ($n, $c, (SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM Categories))",
            ("$n", name.Trim()), ("$c", color));
        return InsertId(c);
    }

    public void SetCategoryActive(long id, bool active)
    {
        using var c = Cmd("UPDATE Categories SET Active = $a WHERE Id = $i", ("$a", active ? 1 : 0), ("$i", id));
        c.ExecuteNonQuery();
    }

    // ---------- attività ----------
    private const string ActivityColumns = "Id, Title, CategoryId, ClientId, StartUtc, EndUtc, Notes, Billable";

    private static Activity ReadActivity(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Title = r.GetString(1),
        CategoryId = r.GetInt64(2),
        ClientId = r.IsDBNull(3) ? null : r.GetInt64(3),
        StartUtc = ParseUtc(r.GetString(4)),
        ClosedUtc = r.IsDBNull(5) ? null : ParseUtc(r.GetString(5)),
        Notes = r.IsDBNull(6) ? null : r.GetString(6),
        Billable = r.GetInt64(7) == 1,
    };

    private static Segment ReadSegment(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), ParseUtc(r.GetString(2)), r.IsDBNull(3) ? null : ParseUtc(r.GetString(3)));

    private void LoadDetails(List<Activity> acts)
    {
        foreach (var a in acts)
        {
            using (var c = Cmd("SELECT PersonId FROM ActivityPeople WHERE ActivityId = $a", ("$a", a.Id)))
            using (var r = c.ExecuteReader())
                while (r.Read()) a.PersonIds.Add(r.GetInt64(0));

            using (var c = Cmd("SELECT Id, ActivityId, StartUtc, EndUtc FROM ActivitySegments " +
                               "WHERE ActivityId = $a ORDER BY StartUtc", ("$a", a.Id)))
            using (var r = c.ExecuteReader())
                while (r.Read()) a.Segments.Add(ReadSegment(r));
        }
    }

    private List<Activity> QueryActivities(string where, params (string, object?)[] args)
    {
        using var c = Cmd($"SELECT {ActivityColumns} FROM Activities {where}", args);
        var list = new List<Activity>();
        using (var r = c.ExecuteReader())
            while (r.Read()) list.Add(ReadActivity(r));
        LoadDetails(list);
        return list;
    }

    /// <summary>Inserisce l'attività con le sue sessioni (a.Segments) e i colleghi.</summary>
    public long InsertActivity(Activity a)
    {
        using var tx = _conn.BeginTransaction();
        if (a.Segments.Count > 0) a.StartUtc = a.Segments.Min(s => s.StartUtc);
        using var c = Cmd("INSERT INTO Activities (Title, CategoryId, ClientId, StartUtc, EndUtc, Notes, Billable) " +
                          "VALUES ($t, $cat, $cli, $s, $e, $n, $b)",
            ("$t", a.Title), ("$cat", a.CategoryId), ("$cli", a.ClientId), ("$s", Iso(a.StartUtc)),
            ("$e", a.ClosedUtc is null ? null : Iso(a.ClosedUtc.Value)), ("$n", a.Notes), ("$b", a.Billable ? 1 : 0));
        c.Transaction = tx;
        a.Id = InsertId(c);
        SavePeople(a, tx);
        for (var i = 0; i < a.Segments.Count; i++)
        {
            var seg = a.Segments[i];
            using var ins = Cmd("INSERT INTO ActivitySegments (ActivityId, StartUtc, EndUtc) VALUES ($a, $s, $e)",
                ("$a", a.Id), ("$s", Iso(seg.StartUtc)), ("$e", seg.EndUtc is null ? null : Iso(seg.EndUtc.Value)));
            ins.Transaction = tx;
            a.Segments[i] = seg with { Id = InsertId(ins), ActivityId = a.Id };
        }
        tx.Commit();
        return a.Id;
    }

    /// <summary>Aggiorna titolo, categoria, cliente, note, fatturabile e colleghi (non le sessioni).</summary>
    public void UpdateActivity(Activity a)
    {
        using var tx = _conn.BeginTransaction();
        using var c = Cmd("UPDATE Activities SET Title=$t, CategoryId=$cat, ClientId=$cli, Notes=$n, Billable=$b WHERE Id=$i",
            ("$t", a.Title), ("$cat", a.CategoryId), ("$cli", a.ClientId), ("$n", a.Notes),
            ("$b", a.Billable ? 1 : 0), ("$i", a.Id));
        c.Transaction = tx;
        c.ExecuteNonQuery();
        SavePeople(a, tx);
        tx.Commit();
    }

    /// <summary>
    /// Salvataggio dalla finestra di modifica: dettagli + sostituzione delle sessioni già concluse
    /// (la sessione eventualmente in corso non viene toccata) + apertura/chiusura dell'attività.
    /// </summary>
    public void SaveActivity(Activity a, IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> closedSegments, bool closed)
    {
        using var tx = _conn.BeginTransaction();
        using (var c = Cmd("UPDATE Activities SET Title=$t, CategoryId=$cat, ClientId=$cli, Notes=$n, Billable=$b WHERE Id=$i",
                   ("$t", a.Title), ("$cat", a.CategoryId), ("$cli", a.ClientId), ("$n", a.Notes),
                   ("$b", a.Billable ? 1 : 0), ("$i", a.Id)))
        {
            c.Transaction = tx;
            c.ExecuteNonQuery();
        }
        SavePeople(a, tx);

        using (var del = Cmd("DELETE FROM ActivitySegments WHERE ActivityId = $a AND EndUtc IS NOT NULL", ("$a", a.Id)))
        {
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        foreach (var (s, e) in closedSegments)
        {
            using var ins = Cmd("INSERT INTO ActivitySegments (ActivityId, StartUtc, EndUtc) VALUES ($a, $s, $e)",
                ("$a", a.Id), ("$s", Iso(s)), ("$e", Iso(e)));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }

        var all = new List<Segment>();
        using (var sel = Cmd("SELECT Id, ActivityId, StartUtc, EndUtc FROM ActivitySegments WHERE ActivityId = $a", ("$a", a.Id)))
        {
            sel.Transaction = tx;
            using var r = sel.ExecuteReader();
            while (r.Read()) all.Add(ReadSegment(r));
        }
        var start = all.Count > 0 ? all.Min(x => x.StartUtc) : a.StartUtc;
        DateTime? end = !closed ? null
            : all.Count > 0 ? all.Max(x => x.EndUtc ?? x.StartUtc)
            : a.ClosedUtc ?? DateTime.UtcNow;
        using (var upd = Cmd("UPDATE Activities SET StartUtc=$s, EndUtc=$e WHERE Id=$i",
                   ("$s", Iso(start)), ("$e", end is null ? null : Iso(end.Value)), ("$i", a.Id)))
        {
            upd.Transaction = tx;
            upd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void SavePeople(Activity a, SqliteTransaction tx)
    {
        using (var del = Cmd("DELETE FROM ActivityPeople WHERE ActivityId = $a", ("$a", a.Id)))
        {
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        foreach (var pid in a.PersonIds.Distinct())
        {
            using var ins = Cmd("INSERT INTO ActivityPeople (ActivityId, PersonId) VALUES ($a, $p)",
                ("$a", a.Id), ("$p", pid));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
    }

    public void DeleteActivity(long id)
    {
        using var c = Cmd("DELETE FROM Activities WHERE Id = $i", ("$i", id));
        c.ExecuteNonQuery();
    }

    public Activity? GetActivity(long id) => QueryActivities("WHERE Id = $i", ("$i", id)).FirstOrDefault();

    /// <summary>Attività con una sessione in corso (normalmente al massimo una).</summary>
    public List<Activity> GetRunningActivities() =>
        QueryActivities("WHERE EndUtc IS NULL AND Id IN (SELECT ActivityId FROM ActivitySegments WHERE EndUtc IS NULL) " +
                        "ORDER BY StartUtc");

    /// <summary>Attività non ancora chiuse con "Fine" (in corso o in pausa).</summary>
    public List<Activity> GetOpenActivities() => QueryActivities("WHERE EndUtc IS NULL ORDER BY StartUtc");

    // ---------- sessioni ----------
    public long StartSegment(long activityId, DateTime startUtc)
    {
        using var c = Cmd("INSERT INTO ActivitySegments (ActivityId, StartUtc) VALUES ($a, $s)",
            ("$a", activityId), ("$s", Iso(startUtc)));
        return InsertId(c);
    }

    /// <summary>Chiude la sessione aperta dell'attività (mai prima del suo inizio).</summary>
    public void EndOpenSegment(long activityId, DateTime endUtc)
    {
        using var sel = Cmd("SELECT Id, StartUtc FROM ActivitySegments WHERE ActivityId = $a AND EndUtc IS NULL", ("$a", activityId));
        var open = new List<(long Id, DateTime Start)>();
        using (var r = sel.ExecuteReader())
            while (r.Read()) open.Add((r.GetInt64(0), ParseUtc(r.GetString(1))));
        foreach (var (id, start) in open)
        {
            using var c = Cmd("UPDATE ActivitySegments SET EndUtc = $e WHERE Id = $i",
                ("$e", Iso(endUtc < start ? start : endUtc)), ("$i", id));
            c.ExecuteNonQuery();
        }
    }

    public void CloseActivity(long activityId, DateTime closedUtc)
    {
        using var c = Cmd("UPDATE Activities SET EndUtc = $e WHERE Id = $i", ("$e", Iso(closedUtc)), ("$i", activityId));
        c.ExecuteNonQuery();
    }

    /// <summary>Sessioni che iniziano nell'intervallo [fromUtc, toUtc).</summary>
    public List<Segment> GetSegments(DateTime fromUtc, DateTime toUtc)
    {
        using var c = Cmd("SELECT Id, ActivityId, StartUtc, EndUtc FROM ActivitySegments " +
                          "WHERE StartUtc >= $f AND StartUtc < $t ORDER BY StartUtc",
            ("$f", Iso(fromUtc)), ("$t", Iso(toUtc)));
        var list = new List<Segment>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(ReadSegment(r));
        return list;
    }

    // ---------- allegati ----------
    public long AddAttachment(long activityId, string filePath)
    {
        using var c = Cmd("INSERT INTO Attachments (ActivityId, FilePath, CreatedUtc) VALUES ($a, $p, $c)",
            ("$a", activityId), ("$p", filePath), ("$c", Iso(DateTime.UtcNow)));
        return InsertId(c);
    }

    public IReadOnlyList<Attachment> GetAttachments(long activityId)
    {
        using var c = Cmd("SELECT Id, ActivityId, FilePath, CreatedUtc FROM Attachments " +
                          "WHERE ActivityId = $a ORDER BY CreatedUtc", ("$a", activityId));
        using var r = c.ExecuteReader();
        var list = new List<Attachment>();
        while (r.Read())
            list.Add(new Attachment(r.GetInt64(0), r.GetInt64(1), r.GetString(2), ParseUtc(r.GetString(3))));
        return list;
    }

    // ---------- impostazioni ----------
    public string? GetSetting(string key)
    {
        using var c = Cmd("SELECT Value FROM Settings WHERE Key = $k", ("$k", key));
        return c.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Cmd("INSERT INTO Settings (Key, Value) VALUES ($k, $v) " +
                          "ON CONFLICT(Key) DO UPDATE SET Value = $v", ("$k", key), ("$v", value));
        c.ExecuteNonQuery();
    }
}
