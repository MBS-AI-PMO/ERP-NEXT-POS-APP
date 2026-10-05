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
        c.Exec(null, "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;");
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        var version = Convert.ToInt32(c.Scalar(null, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        for (var i = version; i < Migrations.All.Length; i++)
        {
            using var tx = c.BeginTransaction();
            c.Exec(tx, Migrations.All[i]);
            c.Exec(tx, $"PRAGMA user_version = {i + 1}");
            tx.Commit();
        }
    }
}
