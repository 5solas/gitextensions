using System.IO;
using System.Runtime.InteropServices;
using LibGhostty;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Process-wide lazy singleton providing access to the libghostty-vt native library
///  and additional native bindings.
/// </summary>
internal static class GhosttySession
{
    private const string EnvironmentVariableName = "LIBGHOSTTY_NATIVE_DIR";

    private static readonly string[] RequiredNativeFiles =
    [
        "ghostty-vt.dll",
        "conpty.dll",
        "OpenConsole.exe"
    ];

    private static readonly Lazy<string?> LazyNativeDirectory = new(ResolveNativeDirectory);
    private static readonly Lazy<GhosttyNativeLibrary> LazyLibrary = new(InitializeLibrary);
    private static readonly Lazy<GhosttyNativeExtras> LazyExtras = new(() => new(Library));

    /// <summary>
    ///  Gets a value indicating whether libghostty-vt is supported in the current environment
    ///  (Windows x64 with required native binaries present). Never throws an exception.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                {
                    return false;
                }

                return NativeDirectory is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    ///  Gets the resolved directory containing the libghostty-vt and ConPTY native assets,
    ///  or <see langword="null"/> if not found.
    /// </summary>
    public static string? NativeDirectory => LazyNativeDirectory.Value;

    /// <summary>
    ///  Gets the process-wide <see cref="GhosttyNativeLibrary"/> instance.
    /// </summary>
    public static GhosttyNativeLibrary Library => LazyLibrary.Value;

    /// <summary>
    ///  Gets the process-wide <see cref="GhosttyNativeExtras"/> instance.
    /// </summary>
    public static GhosttyNativeExtras Extras => LazyExtras.Value;

    private static GhosttyNativeLibrary InitializeLibrary()
    {
        string? nativeDirectory = NativeDirectory;
        if (nativeDirectory is null)
        {
            throw new FileNotFoundException("Required libghostty native assets were not found.");
        }

        Environment.SetEnvironmentVariable(EnvironmentVariableName, nativeDirectory);
        return GhosttyNativeLibrary.LoadFromPackage();
    }

    private static string? ResolveNativeDirectory()
    {
        string? environmentDirectory = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrEmpty(environmentDirectory) && ContainsRequiredFiles(environmentDirectory))
        {
            return Path.GetFullPath(environmentDirectory);
        }

        string baseDirectory = AppContext.BaseDirectory;
        if (ContainsRequiredFiles(baseDirectory))
        {
            string resolvedPath = Path.GetFullPath(baseDirectory);
            Environment.SetEnvironmentVariable(EnvironmentVariableName, resolvedPath);
            return resolvedPath;
        }

        string runtimesDirectory = Path.Combine(baseDirectory, "runtimes", "win-x64", "native");
        if (ContainsRequiredFiles(runtimesDirectory))
        {
            string resolvedPath = Path.GetFullPath(runtimesDirectory);
            Environment.SetEnvironmentVariable(EnvironmentVariableName, resolvedPath);
            return resolvedPath;
        }

        return null;
    }

    private static bool ContainsRequiredFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        foreach (string fileName in RequiredNativeFiles)
        {
            if (!File.Exists(Path.Combine(directoryPath, fileName)))
            {
                return false;
            }
        }

        return true;
    }
}
