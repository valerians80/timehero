using Microsoft.Data.Sqlite;

namespace TimeHero.Core;

/// <summary>Apre il file SQLite e crea lo schema al primo avvio.</summary>
public static class Database
{
    public static string DefaultPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeHero");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "timehero.sqlite");
        }
    }

    public static string DefaultAttachmentsDir
    {
        get
        {
            var dir = Path.Combine(Path.GetDirectoryName(DefaultPath)!, "Attachments");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <param name="path">Percorso del file, oppure ":memory:" per i test.</param>
    public static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Foreign Keys=True");
        conn.Open();
        Migrate(conn);
        return conn;
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Clients (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE,
            Code TEXT,
            Active INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS People (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE,
            Email TEXT,
            Role TEXT);
        CREATE TABLE IF NOT EXISTS Categories (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE,
            Color TEXT NOT NULL DEFAULT '#F2C94C',
            SortOrder INTEGER NOT NULL DEFAULT 0,
            Active INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS Activities (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CategoryId INTEGER NOT NULL REFERENCES Categories(Id),
            ClientId INTEGER REFERENCES Clients(Id),
            StartUtc TEXT NOT NULL,
            EndUtc TEXT,
            Notes TEXT,
            Billable INTEGER NOT NULL DEFAULT 1);
        CREATE INDEX IF NOT EXISTS IX_Activities_Start ON Activities(StartUtc);
        CREATE TABLE IF NOT EXISTS ActivityPeople (
            ActivityId INTEGER NOT NULL REFERENCES Activities(Id) ON DELETE CASCADE,
            PersonId INTEGER NOT NULL REFERENCES People(Id),
            PRIMARY KEY (ActivityId, PersonId));
        CREATE TABLE IF NOT EXISTS Attachments (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ActivityId INTEGER NOT NULL REFERENCES Activities(Id) ON DELETE CASCADE,
            FilePath TEXT NOT NULL,
            CreatedUtc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Settings (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL);
        """;

    private static readonly (string Name, string Color)[] DefaultCategories =
    {
        ("Attività programmate", "#F2C94C"),
        ("Chiamate", "#6FCF97"),
        ("Gestione mail", "#56CCF2"),
        ("Riunioni", "#BB86FC"),
        ("Analisi / Studio", "#F2994A"),
        ("Altro", "#BDBDBD"),
    };

    private static void Migrate(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }

        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Categories";
        if ((long)count.ExecuteScalar()! > 0) return;

        var order = 0;
        foreach (var (name, color) in DefaultCategories)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO Categories (Name, Color, SortOrder) VALUES ($n, $c, $o)";
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$c", color);
            ins.Parameters.AddWithValue("$o", order++);
            ins.ExecuteNonQuery();
        }
    }
}
