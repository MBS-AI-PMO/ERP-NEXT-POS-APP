using TillPOS.Data;

namespace TillPOS.Tests.Data;

public sealed class TillDbTests : IDisposable
{
    private readonly TempDb temp = new();

    public void Dispose() => temp.Dispose();

    private int UserVersion()
    {
        using var c = temp.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void SetUserVersion(int version)
    {
        using var c = temp.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version}";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Migrate_brings_the_schema_to_this_builds_version_and_is_repeatable()
    {
        Assert.Equal(TillDb.SchemaVersion, UserVersion());
        temp.Db.Migrate();
        Assert.Equal(TillDb.SchemaVersion, UserVersion());
    }

    [Fact]
    public void A_database_of_a_newer_tillpos_is_refused()
    {
        SetUserVersion(TillDb.SchemaVersion + 1);

        var ex = Assert.Throws<InvalidOperationException>(() => temp.Db.Migrate());

        Assert.Equal($"This database was last used by a newer TillPOS (schema {TillDb.SchemaVersion + 1}). Install that version.", ex.Message);
        Assert.Equal(TillDb.SchemaVersion + 1, UserVersion());   // left as it was
    }
}
