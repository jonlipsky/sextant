using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

public sealed class DatabaseProvider : IDisposable
{
    private readonly string _dbPath;
    private IndexDatabase? _db;

    public DatabaseProvider(string? dbPath = null)
    {
        _dbPath = dbPath ?? SextantConfiguration.FromEnvironment().DbPath;
    }

    public bool DatabaseExists => File.Exists(_dbPath);

    public IndexDatabase? GetDatabase()
    {
        if (!DatabaseExists)
            return null;

        _db ??= new IndexDatabase(_dbPath);
        return _db;
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}
