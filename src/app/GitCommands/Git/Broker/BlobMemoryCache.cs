using System.Diagnostics.CodeAnalysis;
using GitExtensions.Extensibility.Git;

namespace GitCommands.Git.Broker;

/// <summary>
///  Keeps the contents of the most recently used blobs in memory. The content of an id never changes.
/// </summary>
internal sealed class BlobMemoryCache(Func<long> getMaxBytes, int maxBytesPerBlob = BlobMemoryCache.MaxBytesPerBlob)
{
    internal const int MaxBytesPerBlob = 16 * 1024 * 1024;

    /// <summary>
    ///  The estimated memory consumption of an entry in addition to its content.
    /// </summary>
    internal const int OverheadBytes = 200;

    private readonly Lock _lock = new();

    // The first entry is the most recently used one
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<(string Repository, ObjectId ObjectId), LinkedListNode<Entry>> _entryByKey = [];
    private long _totalBytes;

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

    public bool TryGet(string repository, ObjectId objectId, [NotNullWhen(true)] out byte[]? content)
    {
        long maxBytes = getMaxBytes();
        lock (_lock)
        {
            // The limit may have been lowered
            Trim(maxBytes);
            if (!_entryByKey.TryGetValue((repository, objectId), out LinkedListNode<Entry>? node))
            {
                content = null;
                return false;
            }

            _entries.Remove(node);
            _entries.AddFirst(node);
            content = node.Value.Content;
            return true;
        }
    }

    /// <summary>
    ///  Stores the content, which must not be changed. The least recently used contents are removed if the limit is exceeded.
    /// </summary>
    public void Add(string repository, ObjectId objectId, byte[] content)
    {
        long maxBytes = getMaxBytes();
        if (content.Length > maxBytesPerBlob || content.Length + OverheadBytes > maxBytes)
        {
            return;
        }

        lock (_lock)
        {
            if (!_entryByKey.ContainsKey((repository, objectId)))
            {
                _entryByKey[(repository, objectId)] = _entries.AddFirst(new Entry(repository, objectId, content));
                _totalBytes += content.Length + OverheadBytes;
            }

            Trim(maxBytes);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _entryByKey.Clear();
            _totalBytes = 0;
        }
    }

    private void Trim(long maxBytes)
    {
        while (_totalBytes > maxBytes && _entries.Last is { } last)
        {
            _entries.Remove(last);
            _entryByKey.Remove((last.Value.Repository, last.Value.ObjectId));
            _totalBytes -= last.Value.Content.Length + OverheadBytes;
        }
    }

    private sealed record Entry(string Repository, ObjectId ObjectId, byte[] Content);
}
