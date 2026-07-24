using Dyno.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Dyno.Core.SysConfig;

/// <summary>
/// Persists the SysConfig page's settings in a SQLite database on this computer, in two tables that
/// differ in what reads them back:
/// <list type="bullet">
/// <item><b><c>sysconfig</c></b> — the runtime parameters. The board has no settings storage, so
/// this table is the durable copy: the app re-pushes every saved value to the device after each
/// handshake, and a parameter with no row here simply runs the firmware's config.h default.</item>
/// <item><b><c>pcconstants</c></b> — values the desktop app uses itself and never sends to the
/// device: the moment of inertia, the force-sensor lever arm and the gear ratio behind the torque,
/// power and geared readouts it derives. Keyed by name, since no wire id describes them.</item>
/// <item><b><c>compiletime</c></b> — the <c>#define</c>s from config.h / debug.h. Nothing consumes
/// these yet: the firmware still builds from the headers, and the app no longer writes them. A row
/// records only that the user wants a value the header doesn't have, and the page shows it back.
/// Values are kept as text because that is what a <c>#define</c> is — <c>ADS1115_RATE_475</c> and
/// <c>16 + 1</c> are as valid as <c>100u</c>.</item>
/// </list>
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
        // Pooling off: the store holds this one connection for its whole life, so a pool adds
        // nothing — and a pooled connection would keep the file handle open past Dispose, which
        // on Windows leaves the .db locked (deleting it then throws; the tests do exactly that).
        _connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
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
            CREATE TABLE IF NOT EXISTS compiletime (
                name       TEXT PRIMARY KEY,
                file       TEXT NOT NULL,
                value      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pcconstants (
                name       TEXT PRIMARY KEY,
                value      REAL NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();

        RekeySysConfigByName();
    }

    /// <summary>
    /// Re-keys saved runtime parameters onto the current catalog's ids, matching on the stored
    /// name, and forgets rows whose name the catalog no longer has.
    /// </summary>
    /// <remarks>
    /// Wire ids are positional, so removing a parameter shifts every id after it. A database
    /// written before such a change holds rows under the old numbering, and reading them back by
    /// id would silently hand each parameter its neighbour's value — a PID gain landing in a
    /// mechanical constant, with nothing to show anything had gone wrong. The name column exists
    /// for exactly this: it makes the file self-describing enough to renumber safely.
    ///
    /// Runs on every open and is a no-op once the ids agree, so it costs one query in the normal
    /// case and needs no schema version to track.
    /// </remarks>
    private void RekeySysConfigByName()
    {
        var byName = SysConfigCatalog.Parameters.ToDictionary(p => p.Name, p => p.Id);

        var rows = new List<(int Id, string Name, double Value, string UpdatedAt)>();
        using (var select = _connection.CreateCommand())
        {
            select.CommandText = "SELECT param_id, name, value, updated_at FROM sysconfig";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(
                    (
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetDouble(2),
                        reader.GetString(3)
                    )
                );
            }
        }

        bool stale = rows.Any(r => !byName.TryGetValue(r.Name, out var id) || (int)id != r.Id);
        if (!stale)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM sysconfig";
            clear.ExecuteNonQuery();
        }
        foreach (var row in rows)
        {
            // A name the catalog has dropped is deliberately not carried over: the parameter no
            // longer exists on the device, so there is nothing for its value to mean.
            if (!byName.TryGetValue(row.Name, out var id))
            {
                continue;
            }
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sysconfig (param_id, name, value, updated_at)
                VALUES ($id, $name, $value, $updated);
                """;
            insert.Parameters.AddWithValue("$id", (int)id);
            insert.Parameters.AddWithValue("$name", row.Name);
            insert.Parameters.AddWithValue("$value", row.Value);
            insert.Parameters.AddWithValue("$updated", row.UpdatedAt);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Every saved PC constant, keyed by name. These never reach the device — they are
    /// the desktop app's own inputs to the torque, power and gearing it derives.</summary>
    public IReadOnlyDictionary<string, double> LoadAllPcConstants()
    {
        var values = new Dictionary<string, double>();
        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT name, value FROM pcconstants";
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetDouble(1);
        }
        return values;
    }

    /// <summary>Inserts or updates one PC constant.</summary>
    public void SavePcConstant(string name, double value)
    {
        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO pcconstants (name, value, updated_at)
            VALUES ($name, $value, $now)
            ON CONFLICT (name) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$name", name);
        upsert.Parameters.AddWithValue("$value", value);
        upsert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        upsert.ExecuteNonQuery();
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

    /// <summary>The compile-time settings the user has changed, keyed by define name. A define
    /// absent here is being left at whatever its header says.</summary>
    public IReadOnlyDictionary<string, string> LoadAllCompileTime()
    {
        var values = new Dictionary<string, string>();
        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT name, value FROM compiletime";
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }
        return values;
    }

    /// <summary>Inserts or updates one compile-time setting. <paramref name="file"/> is the header
    /// it came from (config.h / debug.h), kept so a row says where it belongs.</summary>
    public void SaveCompileTime(string name, string file, string value)
    {
        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO compiletime (name, file, value, updated_at)
            VALUES ($name, $file, $value, $now)
            ON CONFLICT (name) DO UPDATE SET
                file = excluded.file,
                value = excluded.value,
                updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$name", name);
        upsert.Parameters.AddWithValue("$file", file);
        upsert.Parameters.AddWithValue("$value", value);
        upsert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        upsert.ExecuteNonQuery();
    }

    /// <summary>Forgets a compile-time setting, leaving the define at its header value.</summary>
    public void RemoveCompileTime(string name)
    {
        using var delete = _connection.CreateCommand();
        delete.CommandText = "DELETE FROM compiletime WHERE name = $name";
        delete.Parameters.AddWithValue("$name", name);
        delete.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
