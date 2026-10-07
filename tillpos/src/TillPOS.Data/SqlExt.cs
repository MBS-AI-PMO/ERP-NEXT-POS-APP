using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TillPOS.Data;

internal static class SqlExt
{
    public static int Exec(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, tx, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, tx, sql, ps);
        return cmd.ExecuteScalar();
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, null, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    public static decimal Dec(SqliteDataReader r, int i) => decimal.Parse(r.GetString(i), NumberStyles.Number, CultureInfo.InvariantCulture);
    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly? Date(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateOnly.ParseExact(r.GetString(i), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static string? Instant(DateTimeOffset? at) => at?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset? Instant(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
