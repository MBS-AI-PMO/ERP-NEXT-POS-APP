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

    /// <summary>Deletes the files. Antivirus or the search indexer can briefly hold a fresh file on Windows, so a
    /// locked file is retried and then left behind in %TEMP% rather than failing a test that already passed.</summary>
    public void Dispose()
    {
        // Clear only this file's pool: ClearAllPools would also close idle connections of tests running in parallel.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = true }.ToString()))
            SqliteConnection.ClearPool(connection);
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            for (var attempt = 1; File.Exists(f); attempt++)
            {
                try
                {
                    File.Delete(f);
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 5)
                {
                    Thread.Sleep(50);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
    }
}
