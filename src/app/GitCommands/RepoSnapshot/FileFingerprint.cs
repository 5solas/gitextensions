using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace GitCommands.RepoSnapshot;

/// <summary>
///  The contents of some small files (or that they are missing) at a point in time, without starting a process.
/// </summary>
/// <remarks>
///  The contents are compared, not size and time: a change is also found if the file is rewritten with the same size
///  within the resolution of the file times or if the file time is preserved.
/// </remarks>
internal sealed class FileFingerprint
{
    internal const int MaxFileSize = 4 * 1024 * 1024;

    private const string Missing = "missing";

    private readonly SortedDictionary<string, string> _stateByPath;

    private FileFingerprint(SortedDictionary<string, string> stateByPath)
    {
        _stateByPath = stateByPath;
    }

    public IReadOnlyCollection<string> Paths => _stateByPath.Keys;

    public long EstimatedBytes => _stateByPath.Sum(pair => 100L + (2L * (pair.Key.Length + pair.Value.Length)));

    /// <summary>
    ///  Reads the files.
    /// </summary>
    /// <returns><see langword="null"/> if the state of a file cannot be determined.</returns>
    public static FileFingerprint? TryRead(IEnumerable<string> paths)
    {
        SortedDictionary<string, string> stateByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (stateByPath.ContainsKey(path))
            {
                continue;
            }

            if (!TryReadState(path, out string? state))
            {
                return null;
            }

            stateByPath.Add(path, state);
        }

        return new FileFingerprint(stateByPath);
    }

    /// <summary>
    ///  Gets whether all files have the same contents as when this fingerprint was read.
    /// </summary>
    public bool IsCurrent()
        => _stateByPath.All(pair => TryReadState(pair.Key, out string? state) && state == pair.Value);

    /// <summary>
    ///  Gets whether the files which are part of both fingerprints have the same contents.
    /// </summary>
    public bool IsSameForCommonFiles(FileFingerprint other)
        => _stateByPath.All(pair => !other._stateByPath.TryGetValue(pair.Key, out string? otherState) || otherState == pair.Value);

    public bool IsSame(FileFingerprint other)
        => _stateByPath.Count == other._stateByPath.Count
            && _stateByPath.All(pair => other._stateByPath.TryGetValue(pair.Key, out string? otherState) && otherState == pair.Value);

    public bool Contains(string path) => _stateByPath.ContainsKey(path);

    public bool IsExisting(string path) => _stateByPath.TryGetValue(path, out string? state) && state != Missing;

    private static bool TryReadState(string path, [NotNullWhen(true)] out string? state)
    {
        state = null;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileSize)
            {
                return false;
            }

            state = Convert.ToHexString(SHA256.HashData(stream));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            state = Missing;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
