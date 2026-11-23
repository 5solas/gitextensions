using System.Collections.Concurrent;
using System.Diagnostics;

namespace GitCommands;

/// <summary>
///  Redirects the start of a git launcher (<c>cmd\git.exe</c>, <c>bin\git.exe</c>, scoop shim) to the real
///  git binary of the same Git for Windows installation, which saves one or two process creations per git call.
///  Anything not recognized as a known installation layout is left untouched.
/// </summary>
public static class GitBinaryResolver
{
    private static readonly string[] RealBinaryFolders = ["ucrt64", "clangarm64", "mingw64", "mingw32"];
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///  Hook for the process start: replaces <see cref="ProcessStartInfo.FileName"/> if a real binary is found.
    /// </summary>
    public static void TryRedirect(ProcessStartInfo startInfo)
        => TryRedirect(startInfo, AppSettings.GitCommand, AppSettings.UseDirectGitBinary.Value);

    internal static void TryRedirect(ProcessStartInfo startInfo, string configuredGitCommand, bool enabled)
    {
        if (!enabled
            || !OperatingSystem.IsWindows()
            || startInfo.UseShellExecute
            || string.IsNullOrEmpty(startInfo.FileName)
            || !string.Equals(startInfo.FileName, configuredGitCommand, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? real = Resolve(configuredGitCommand);
        if (real is not null)
        {
            startInfo.FileName = real;
        }
    }

    /// <summary>
    ///  Resolves the real git binary for a configured git command, or <see langword="null"/> if not applicable.
    /// </summary>
    internal static string? Resolve(string configuredGitCommand)
        => Cache.GetOrAdd(configuredGitCommand, ResolveUncached);

    private static string? ResolveUncached(string configuredGitCommand)
    {
        try
        {
            string name = Path.GetFileName(configuredGitCommand);
            if (!name.Equals("git", StringComparison.OrdinalIgnoreCase) && !name.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string fileName = Path.HasExtension(configuredGitCommand) ? configuredGitCommand : configuredGitCommand + ".exe";
            if (!PathUtil.TryFindFullPath(fileName, out string? launcher))
            {
                return null;
            }

            string? shimTarget = ReadScoopShimTarget(launcher);
            if (shimTarget is not null)
            {
                launcher = shimTarget;
            }

            if (!Path.GetFileName(launcher).Equals("git.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(launcher))
            {
                return null;
            }

            DirectoryInfo? launcherDir = new FileInfo(launcher).Directory;
            if (launcherDir is null
                || !(launcherDir.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase) || launcherDir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
                || launcherDir.Parent is not { } root
                || !File.Exists(Path.Combine(root.FullName, "usr", "bin", "sh.exe")))
            {
                return null;
            }

            foreach (string folder in RealBinaryFolders)
            {
                string candidate = Path.Combine(root.FullName, folder, "bin", "git.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // fall back to the configured command
        }

        return null;
    }

    private static string? ReadScoopShimTarget(string launcher)
    {
        string shim = Path.ChangeExtension(launcher, ".shim");
        if (!File.Exists(shim))
        {
            return null;
        }

        foreach (string line in File.ReadLines(shim))
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                string value = line[(eq + 1)..].Trim().Trim('"');
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    internal static void ClearCache() => Cache.Clear();
}
