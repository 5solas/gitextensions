using System.Collections.Concurrent;
using System.Diagnostics;

namespace GitCommands;

/// <summary>
///  Redirects the start of a git launcher (<c>cmd\git.exe</c>, <c>bin\git.exe</c>, scoop shim) to the real
///  git binary of the same Git for Windows installation, which saves one or two process creations per git call.
///  Anything not recognized as a known installation layout is left untouched.
/// </summary>
/// <remarks>
///  The real binary sets up the same environment as the launcher (MSYSTEM, PLINK_PROTOCOL, GIT_EXEC_PATH, sh in PATH),
///  verified for the ucrt64 (Git for Windows 2.56.1) and mingw64 (PortableGit 2.47.1) layouts. Only the position of
///  "~/bin" in PATH differs (in front of the git folders instead of behind them), so the launcher is kept if it exists.
/// </remarks>
internal static class GitBinaryResolver
{
    private const long RecheckIntervalMilliseconds = 30_000;

    private static readonly string[] RealBinaryFolders = ["ucrt64", "clangarm64", "mingw64", "mingw32"];
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static Func<long> GetTickCount { get; set; } = () => Environment.TickCount64;

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
    /// <remarks>
    ///  A resolved real binary is checked again at most every 30 seconds (not for every git call). If it no longer
    ///  exists (e.g. git reinstalled with another layout) or "~/bin" was created, it is dropped from the cache and the
    ///  configured command is used.
    /// </remarks>
    internal static string? Resolve(string configuredGitCommand)
    {
        CacheEntry entry = Cache.GetOrAdd(configuredGitCommand, command => new CacheEntry(ResolveUncached(command), GetTickCount()));
        if (entry.RealBinary is null)
        {
            return null;
        }

        long now = GetTickCount();
        if (now - entry.CheckedAt < RecheckIntervalMilliseconds)
        {
            return entry.RealBinary;
        }

        if (IsUsable(entry.RealBinary))
        {
            Cache.TryUpdate(configuredGitCommand, entry with { CheckedAt = now }, entry);
            return entry.RealBinary;
        }

        Cache.TryRemove(new KeyValuePair<string, CacheEntry>(configuredGitCommand, entry));
        return null;
    }

    private static bool IsUsable(string realBinary)
    {
        try
        {
            return File.Exists(realBinary) && !HomeBinExists();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///  Gets whether "~/bin" exists, which the real binary puts in front of its own folders in PATH.
    /// </summary>
    private static bool HomeBinExists()
    {
        string? home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE");
        }

        return !string.IsNullOrEmpty(home) && Directory.Exists(Path.Combine(home, "bin"));
    }

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

            // The launcher starts the binary of its own build: if there are several, which one is not known.
            string[] candidates = [.. RealBinaryFolders
                .Select(folder => Path.Combine(root.FullName, folder, "bin", "git.exe"))
                .Where(File.Exists)];
            if (candidates.Length == 1 && !HomeBinExists())
            {
                return candidates[0];
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

    private sealed record CacheEntry(string? RealBinary, long CheckedAt);
}
