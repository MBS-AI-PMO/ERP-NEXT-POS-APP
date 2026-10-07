using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TillPOS.Data;

public sealed class TillDb(string path)
{
    public string Path { get; } = path;

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = true }.ToString());
        c.Open();
        c.Exec(null, "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; PRAGMA synchronous = FULL;");
        return c;
    }

    /// <summary>The schema version this TillPOS builds (PRAGMA user_version after <see cref="Migrate"/>).</summary>
    public static int SchemaVersion => Migrations.All.Length;

    /// <summary>Brings the database up to <see cref="SchemaVersion"/>. A database already at a higher version was last used by a
    /// newer TillPOS: it is refused (never silently used by an older build, which would not know the newer columns).</summary>
    /// <exception cref="InvalidOperationException">The database is newer than this build.</exception>
    public void Migrate()
    {
        using var c = Open();
        var version = Convert.ToInt32(c.Scalar(null, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > Migrations.All.Length)
            throw new InvalidOperationException(
                $"This database was last used by a newer TillPOS (schema {version.ToString(CultureInfo.InvariantCulture)}). Install that version.");
        for (var i = version; i < Migrations.All.Length; i++)
        {
            using var tx = c.BeginTransaction();
            c.Exec(tx, Migrations.All[i]);
            c.Exec(tx, $"PRAGMA user_version = {i + 1}");
            tx.Commit();
        }
    }
}
