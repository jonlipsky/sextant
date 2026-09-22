using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Helper for binding parameter values in a way that works both for a freshly created command
/// (parameter added on first use) and for a reused, prepared command (parameter value updated in
/// place). Reusing a single command across many rows lets SQLite keep one compiled statement
/// instead of re-preparing per row, which is a core part of the batched write path.
/// </summary>
internal static class SqlParam
{
    public static void Set(SqliteCommand cmd, string name, object? value)
    {
        var boxed = value ?? DBNull.Value;
        var index = cmd.Parameters.IndexOf(name);
        if (index >= 0)
            cmd.Parameters[index].Value = boxed;
        else
            cmd.Parameters.AddWithValue(name, boxed);
    }
}
