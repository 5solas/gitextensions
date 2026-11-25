using System.Diagnostics.CodeAnalysis;

namespace GitCommands.RepoSnapshot;

/// <summary>
///  A part of the data of a repository which is kept in the <see cref="RepoSnapshotStore"/>, immutable.
/// </summary>
internal interface IRepoSnapshotPart
{
    /// <summary>
    ///  Gets the estimated memory consumption.
    /// </summary>
    long EstimatedBytes { get; }
}

/// <summary>
///  Keeps data of the most recently used repositories in memory.
/// </summary>
/// <remarks>
///  The store does not know whether the data is still valid: whoever reads a part must validate it.
/// </remarks>
internal sealed class RepoSnapshotStore
{
    internal const int MaxRepositories = 200;

    private readonly Lock _lock = new();
    private readonly Func<long> _getMaxBytes;
    private readonly int _maxRepositories;

    // The first entry is the most recently used one
    private readonly LinkedList<RepoEntry> _entries = new();
    private readonly Dictionary<string, LinkedListNode<RepoEntry>> _entryByKey = new(StringComparer.OrdinalIgnoreCase);
    private long _totalBytes;

    public RepoSnapshotStore(Func<long> getMaxBytes, int maxRepositories = MaxRepositories)
    {
        _getMaxBytes = getMaxBytes;
        _maxRepositories = maxRepositories;
    }

    /// <summary>
    ///  Gets the store of the application.
    /// </summary>
    public static RepoSnapshotStore Default { get; } = new(() => Math.Max(0L, AppSettings.RepoSnapshotMemoryLimit.Value) * 1024 * 1024);

    public bool IsEnabled => _getMaxBytes() > 0;

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    internal long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>
    ///  Gets the key of a repository, the same one for all spellings of its working directory.
    /// </summary>
    public static bool TryGetKey(string? workingDir, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(workingDir))
        {
            return false;
        }

        try
        {
            key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDir));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool TryGet<T>(string key, [NotNullWhen(true)] out T? part)
        where T : class, IRepoSnapshotPart
    {
        part = null;
        long maxBytes = _getMaxBytes();

        lock (_lock)
        {
            // The limit may have been lowered
            Trim(maxBytes);
            if (!_entryByKey.TryGetValue(key, out LinkedListNode<RepoEntry>? node)
                || !node.Value.Parts.TryGetValue(typeof(T), out IRepoSnapshotPart? found))
            {
                return false;
            }

            _entries.Remove(node);
            _entries.AddFirst(node);
            part = (T)found;
            return true;
        }
    }

    /// <summary>
    ///  Stores <paramref name="part"/>, replaces the existing part of the same type.
    ///  The least recently used repositories are removed if a limit is exceeded.
    /// </summary>
    public void Set<T>(string key, T part)
        where T : class, IRepoSnapshotPart
    {
        long maxBytes = _getMaxBytes();

        lock (_lock)
        {
            RemovePart(key, typeof(T));
            if (part.EstimatedBytes > maxBytes || maxBytes <= 0)
            {
                Trim(maxBytes);
                return;
            }

            if (_entryByKey.TryGetValue(key, out LinkedListNode<RepoEntry>? node))
            {
                _entries.Remove(node);
                _entries.AddFirst(node);
            }
            else
            {
                node = _entries.AddFirst(new RepoEntry(key));
                _entryByKey.Add(key, node);
            }

            node.Value.Parts[typeof(T)] = part;
            _totalBytes += part.EstimatedBytes;
            Trim(maxBytes);
        }
    }

    public void Remove<T>(string key)
        where T : class, IRepoSnapshotPart
    {
        lock (_lock)
        {
            RemovePart(key, typeof(T));
        }
    }

    internal void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _entryByKey.Clear();
            _totalBytes = 0;
        }
    }

    private void RemovePart(string key, Type type)
    {
        if (_entryByKey.TryGetValue(key, out LinkedListNode<RepoEntry>? node) && node.Value.Parts.Remove(type, out IRepoSnapshotPart? part))
        {
            _totalBytes -= part.EstimatedBytes;
            if (node.Value.Parts.Count == 0)
            {
                RemoveEntry(node);
            }
        }
    }

    private void RemoveEntry(LinkedListNode<RepoEntry> node)
    {
        _totalBytes -= node.Value.Parts.Values.Sum(part => part.EstimatedBytes);
        _entries.Remove(node);
        _entryByKey.Remove(node.Value.Key);
    }

    private void Trim(long maxBytes)
    {
        while (_entries.Last is { } last && (_totalBytes > maxBytes || maxBytes <= 0 || _entries.Count > _maxRepositories))
        {
            RemoveEntry(last);
        }
    }

    private sealed class RepoEntry(string key)
    {
        public string Key { get; } = key;

        public Dictionary<Type, IRepoSnapshotPart> Parts { get; } = [];
    }
}
