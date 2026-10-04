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
    private const string ActivityColumns = "Id, CategoryId, ClientId, StartUtc, EndUtc, Notes, Billable";

    private Activity ReadActivity(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        CategoryId = r.GetInt64(1),
        ClientId = r.IsDBNull(2) ? null : r.GetInt64(2),
        StartUtc = ParseUtc(r.GetString(3)),
        EndUtc = r.IsDBNull(4) ? null : ParseUtc(r.GetString(4)),
        Notes = r.IsDBNull(5) ? null : r.GetString(5),
        Billable = r.GetInt64(6) == 1,
    };

    private void LoadPeople(List<Activity> acts)
    {
        foreach (var a in acts)
        {
            using var c = Cmd("SELECT PersonId FROM ActivityPeople WHERE ActivityId = $a", ("$a", a.Id));
            using var r = c.ExecuteReader();
            while (r.Read()) a.PersonIds.Add(r.GetInt64(0));
        }
    }

    private List<Activity> QueryActivities(string where, params (string, object?)[] args)
    {
        using var c = Cmd($"SELECT {ActivityColumns} FROM Activities {where}", args);
        var list = new List<Activity>();
        using (var r = c.ExecuteReader())
            while (r.Read()) list.Add(ReadActivity(r));
        LoadPeople(list);
        return list;
    }

    public long InsertActivity(Activity a)
    {
        using var tx = _conn.BeginTransaction();
        using var c = Cmd("INSERT INTO Activities (CategoryId, ClientId, StartUtc, EndUtc, Notes, Billable) " +
                          "VALUES ($cat, $cli, $s, $e, $n, $b)",
            ("$cat", a.CategoryId), ("$cli", a.ClientId), ("$s", Iso(a.StartUtc)),
            ("$e", a.EndUtc is null ? null : Iso(a.EndUtc.Value)), ("$n", a.Notes), ("$b", a.Billable ? 1 : 0));
        c.Transaction = tx;
        a.Id = InsertId(c);
        SavePeople(a, tx);
        tx.Commit();
        return a.Id;
    }

    public void UpdateActivity(Activity a)
    {
        using var tx = _conn.BeginTransaction();
        using var c = Cmd("UPDATE Activities SET CategoryId=$cat, ClientId=$cli, StartUtc=$s, EndUtc=$e, " +
                          "Notes=$n, Billable=$b WHERE Id=$i",
            ("$cat", a.CategoryId), ("$cli", a.ClientId), ("$s", Iso(a.StartUtc)),
            ("$e", a.EndUtc is null ? null : Iso(a.EndUtc.Value)), ("$n", a.Notes),
            ("$b", a.Billable ? 1 : 0), ("$i", a.Id));
        c.Transaction = tx;
        c.ExecuteNonQuery();
        SavePeople(a, tx);
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

    public Activity? GetRunningActivity() =>
        QueryActivities("WHERE EndUtc IS NULL ORDER BY StartUtc DESC LIMIT 1").FirstOrDefault();

    public Activity? GetActivity(long id) => QueryActivities("WHERE Id = $i", ("$i", id)).FirstOrDefault();

    /// <summary>Attività che iniziano nell'intervallo [fromUtc, toUtc).</summary>
    public List<Activity> GetActivities(DateTime fromUtc, DateTime toUtc) =>
        QueryActivities("WHERE StartUtc >= $f AND StartUtc < $t ORDER BY StartUtc",
            ("$f", Iso(fromUtc)), ("$t", Iso(toUtc)));

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
