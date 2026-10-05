using Microsoft.Data.Sqlite;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

/// <summary>A migrated SQLite file in %TEMP%, deleted after the test.</summary>
public sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tillpos-test-{Guid.NewGuid():N}.db");
        Db = new TillDb(Path);
        Db.Migrate();
    }

    public string Path { get; }
    public TillDb Db { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
