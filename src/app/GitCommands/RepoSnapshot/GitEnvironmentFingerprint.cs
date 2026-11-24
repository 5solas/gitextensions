using System.Collections;
using System.Globalization;
using System.Text;

namespace GitCommands.RepoSnapshot;

/// <summary>
///  Describes the git executable and the environment variables which influence which files git reads.
/// </summary>
internal static class GitEnvironmentFingerprint
{
    private static readonly HashSet<string> _variables = new(StringComparer.OrdinalIgnoreCase)
    {
        "HOME", "HOMEDRIVE", "HOMEPATH", "USERPROFILE", "XDG_CONFIG_HOME", "APPDATA", "PROGRAMDATA", "MSYSTEM"
    };

    // With these variables, the files of the repository are not determined by the working directory
    private static readonly string[] _unsupportedVariables =
    [
        "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_CONFIG", "GIT_GRAFT_FILE", "GIT_SHALLOW_FILE"
    ];

    private static volatile FoundExecutable? _foundExecutable;

    /// <summary>
    ///  Reads the current state.
    /// </summary>
    /// <returns><see langword="null"/> if the files read by git cannot be determined in this environment.</returns>
    public static string? TryRead()
    {
        // The variables as set for the next git process, see Executable.Start()
        EnvironmentConfiguration.SetEnvironmentVariables();

        if (_unsupportedVariables.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
        {
            return null;
        }

        StringBuilder result = new();
        if (!TryAppendExecutable(result))
        {
            return null;
        }

        IEnumerable<DictionaryEntry> variables = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Where(entry => entry.Key is string name && (_variables.Contains(name) || name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => (string)entry.Key, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in variables)
        {
            result.Append(variable.Key).Append('=').Append(variable.Value).Append('\n');
        }

        return result.ToString();
    }

    /// <summary>
    ///  Finds the executable in PATH. This is slow compared to everything else, so it is not repeated while PATH is the same.
    /// </summary>
    private static string? FindExecutable(string command)
    {
        string fileName = OperatingSystem.IsWindows() && !Path.HasExtension(command) ? $"{command}.exe" : command;
        if (Path.IsPathFullyQualified(fileName))
        {
            return fileName;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        FoundExecutable? found = _foundExecutable;
        if (found is not null && found.FileName == fileName && found.Path == path && File.Exists(found.FullPath))
        {
            return found.FullPath;
        }

        if (!PathUtil.TryFindFullPath(fileName, out string? fullPath))
        {
            return null;
        }

        _foundExecutable = new FoundExecutable(fileName, path, fullPath);
        return fullPath;
    }

    private static bool TryAppendExecutable(StringBuilder result)
    {
        string command = AppSettings.GitCommand;
        if (FindExecutable(command) is not string fullPath)
        {
            return false;
        }

        AppendFile(fullPath);
        if (AppSettings.UseDirectGitBinary.Value && GitBinaryResolver.Resolve(command) is string realBinary)
        {
            AppendFile(realBinary);
        }

        return true;

        void AppendFile(string path)
        {
            // Does not throw for a missing file
            FileInfo file = new(path);
            result.Append(path).Append('|')
                .Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append((file.Exists ? file.Length : -1).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private sealed record FoundExecutable(string FileName, string Path, string FullPath);
}
