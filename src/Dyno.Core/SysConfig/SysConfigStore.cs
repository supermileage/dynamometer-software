using Dyno.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Dyno.Core.SysConfig;

/// <summary>
/// Persists the user's runtime sysconfig values in a SQLite database on this computer. The board
/// has no settings storage, so this file is the durable copy: the app re-pushes every saved value
/// to the device after each handshake, and a parameter with no row here simply runs the firmware's
/// config.h default.
/// </summary>
public sealed class SysConfigStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Where the database lives unless a caller chooses otherwise:
    /// <c>&lt;ApplicationData&gt;/Dyno/sysconfig.db</c> (e.g. <c>~/.config/Dyno</c> on Linux).</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create
            ),
            "Dyno",
            "sysconfig.db"
        );

    public string DatabasePath { get; }

    public SysConfigStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? DefaultDatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connection = new SqliteConnection($"Data Source={DatabasePath}");
        _connection.Open();

        using var create = _connection.CreateCommand();
        // name is stored alongside the id so the file remains self-describing (inspectable with
        // any sqlite tool) even without the app's catalog at hand.
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS sysconfig (
                param_id   INTEGER PRIMARY KEY,
                name       TEXT NOT NULL,
                value      REAL NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>All saved values, keyed by wire id. Parameters absent here have never been
    /// changed by the user (or were reset) and run the firmware default.</summary>
    public IReadOnlyDictionary<sysconfig_param_t, double> LoadAll()
    {
        var values = new Dictionary<sysconfig_param_t, double>();
        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT param_id, value FROM sysconfig";
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            values[(sysconfig_param_t)reader.GetInt32(0)] = reader.GetDouble(1);
        }
        return values;
    }

    /// <summary>Inserts or updates one parameter's saved value.</summary>
    public void Save(sysconfig_param_t id, string name, double value)
    {
        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO sysconfig (param_id, name, value, updated_at)
            VALUES ($id, $name, $value, $now)
            ON CONFLICT (param_id) DO UPDATE SET
                name = excluded.name,
                value = excluded.value,
                updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$id", (int)id);
        upsert.Parameters.AddWithValue("$name", name);
        upsert.Parameters.AddWithValue("$value", value);
        upsert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        upsert.ExecuteNonQuery();
    }

    /// <summary>Forgets a parameter's saved value, returning it to the firmware default (which
    /// takes effect on the device's next reboot — the store never pushes, only remembers).</summary>
    public void Remove(sysconfig_param_t id)
    {
        using var delete = _connection.CreateCommand();
        delete.CommandText = "DELETE FROM sysconfig WHERE param_id = $id";
        delete.Parameters.AddWithValue("$id", (int)id);
        delete.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
