using System.Diagnostics;
using System.Text;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git.Broker;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;

namespace GitCommandsTests.Git.Broker;

/// <summary>
///  Provides a repository with objects, isolates a test from the sessions and contents of other tests
///  and restores the changed settings.
/// </summary>
internal sealed class GitBrokerTestScope : IDisposable
{
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

    // The stored values: null if the setting is not set, so the settings file does not get an entry
    private readonly string? _originalUseGitBroker = AppSettings.GetString(AppSettings.UseGitBroker.FullPath, defaultValue: null);
    private readonly string? _originalMemoryLimit = AppSettings.GetString(AppSettings.GitBrokerMemoryLimit.FullPath, defaultValue: null);
    private readonly GitModuleTestHelper _helper = new("broker repo");

    public GitBrokerTestScope()
    {
        // Like the start of the application
        Git("--version");
        Git("config core.autocrlf false");
        GitModule.GitCommandCache.Clear();
        GitBroker.Cache.Clear();
        ReadConfig();
    }

    public GitModule Module => _helper.Module;

    public Executable GitExecutable => (Executable)Module.GitExecutable;

    /// <summary>
    ///  The broker does not start anything on the UI thread.
    /// </summary>
    public static T InBackground<T>(Func<T> func) => ThreadHelper.JoinableTaskFactory.Run(() => Task.Run(func));

    public static T InBackground<T>(Func<Task<T>> func) => ThreadHelper.JoinableTaskFactory.Run(() => Task.Run(func));

    public static void WaitUntil(Func<bool> condition, string because)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            stopwatch.Elapsed.Should().BeLessThan(_waitTimeout, because);
            Thread.Sleep(10);
        }
    }

    /// <summary>
    ///  Reads the config as the application does when a repository is opened, the broker depends on it.
    /// </summary>
    public void ReadConfig()
    {
        Module.InvalidateGitSettings();
        Module.GetEffectiveSetting("core.autocrlf");
    }

    /// <summary>
    ///  Lets the repository include a config with a promisor remote: git would run a command to fetch missing objects.
    /// </summary>
    /// <returns>The file which is created by the command.</returns>
    public string IncludePromisorRemote()
    {
        string marker = Path.Combine(_helper.TemporaryPath, "marker.txt");
        File.WriteAllText(
            Path.Combine(Module.WorkingDirGitDir, "extra.cfg"),
            $"[remote \"x\"]\n\turl = ssh://localhost/repo\n\tpromisor = true\n[core]\n\tsshCommand = \"echo run > '{marker.ToPosixPath()}' #\"\n");
        File.AppendAllText(Path.Combine(Module.WorkingDirGitDir, "config"), "\n[include]\n\tpath = extra.cfg\n");
        return marker;
    }

    public string Git(string arguments) => Module.GitExecutable.GetOutput(arguments).Trim();

    public ObjectId AddBlob(byte[] content)
    {
        string file = Path.Combine(_helper.TemporaryPath, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(file, content);
        return ObjectId.Parse(Git($"hash-object -w --no-filters -- {file.Quote()}"));
    }

    /// <summary>
    ///  Adds the files to the index, without any working tree file, and creates a commit.
    /// </summary>
    public ObjectId Commit(params (string Path, string Content)[] files)
    {
        foreach ((string path, string content) in files)
        {
            AddToIndex("100644", AddBlob(Encoding.UTF8.GetBytes(content)), path);
        }

        Git("commit --quiet --allow-empty -m commit");
        return ObjectId.Parse(Git("rev-parse HEAD"));
    }

    public ObjectId Commit(string path, string content) => Commit((path, content));

    public void AddToIndex(string mode, ObjectId objectId, string path)
        => Git($"update-index --add --cacheinfo {$"{mode},{objectId},{path}".Quote()}");

    public CatFileSession StartSession() => CatFileSession.Start(GitExecutable, Module.WorkingDirGitDir);

    public void Dispose()
    {
        AppSettings.SetString(AppSettings.UseGitBroker.FullPath, _originalUseGitBroker!);
        AppSettings.SetString(AppSettings.GitBrokerMemoryLimit.FullPath, _originalMemoryLimit!);
        GitBroker.ReleaseAll();
        GitBroker.Cache.Clear();
        WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "no process may use the repository");
        _helper.Dispose();
    }
}
