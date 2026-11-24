using System.Runtime.CompilerServices;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitCommands.Logging;
using GitCommands.Settings;
using GitExtensions.Extensibility.Git;

namespace GitCommandsTests.RepoSnapshot;

/// <summary>
///  Reading the git config again (by another <see cref="GitModule"/>) must not start git if nothing has changed,
///  and must never return old values.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ConfigSnapshotReuseTests
{
    private const string SettingName = "hj.test";
    private const string GlobalConfigVariable = "GIT_CONFIG_GLOBAL";

    private static readonly IGitExecutorProvider _executorProvider = new GitExecutorProvider(new GitDirectoryResolver());

    private GitModuleTestHelper _helper = null!;
    private string _workingDir = null!;
    private string? _originalGlobalConfig;

    private string GitDir => Path.Combine(_workingDir, ".git");

    private string ConfigFile => Path.Combine(GitDir, "config");

    [SetUp]
    public void SetUp()
    {
        _originalGlobalConfig = Environment.GetEnvironmentVariable(GlobalConfigVariable);
        _helper = new GitModuleTestHelper();

        // Another working directory than the one of the module of the helper, which keeps its settings alive
        _workingDir = Path.Combine(_helper.TemporaryPath, "copy");
        CopyDirectory(_helper.Module.WorkingDir, _workingDir);
        File.AppendAllText(ConfigFile, "[hj]\n\ttest = initial\n");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(GlobalConfigVariable, _originalGlobalConfig);
        AppSettings.RepoSnapshotMemoryLimit.Value = AppSettings.RepoSnapshotMemoryLimit.Default;
        _helper.Dispose();
    }

    [Test]
    public void Config_should_be_reused_if_all_modules_have_been_collected()
    {
        ReadAndCollect().Should().Be("initial");

        ReadAndCollect(expectedConfigLists: 0).Should().Be("initial");
        ReadAndCollect(expectedConfigLists: 0).Should().Be("initial");
    }

    [Test]
    public void Config_should_be_reused_while_another_module_is_alive()
    {
        GitModule module = CreateModule();
        module.GetEffectiveSetting(SettingName).Should().Be("initial");

        ReadAndCollect(expectedConfigLists: 0).Should().Be("initial");
        GC.KeepAlive(module);
    }

    [Test]
    public void Config_should_be_read_again_if_local_config_changed([Values] bool keepModuleAlive)
    {
        GitModule? module = keepModuleAlive ? CreateModule() : null;
        module?.GetEffectiveSetting(SettingName).Should().Be("initial");
        ReadAndCollect();

        // Same size, same time
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(ConfigFile);
        File.WriteAllText(ConfigFile, File.ReadAllText(ConfigFile).Replace("initial", "changed"));
        File.SetLastWriteTimeUtc(ConfigFile, lastWriteTime);

        ReadAndCollect(expectedConfigLists: 1).Should().Be("changed");
        ReadAndCollect(expectedConfigLists: 0).Should().Be("changed");
        GC.KeepAlive(module);
    }

    [Test]
    public void Config_should_be_read_again_if_worktree_config_is_created()
    {
        File.AppendAllText(ConfigFile, "[extensions]\n\tworktreeConfig = true\n");
        ReadAndCollect().Should().Be("initial");

        File.WriteAllText(Path.Combine(GitDir, "config.worktree"), "[hj]\n\ttest = worktree\n");

        ReadAndCollect(expectedConfigLists: 1).Should().Be("worktree");
    }

    [Test]
    public void Config_should_be_read_again_if_included_file_changed()
    {
        string included = CreateIncludedFile("included", age: TimeSpan.FromHours(1));
        File.AppendAllText(ConfigFile, "[include]\n\tpath = included.config\n");
        ReadAndCollect().Should().Be("included");
        ReadAndCollect(expectedConfigLists: 0).Should().Be("included");

        File.WriteAllText(included, "[hj]\n\ttest = modified\n");

        ReadAndCollect(expectedConfigLists: 1).Should().Be("modified");
    }

    [Test]
    public void Config_should_be_read_again_if_missing_included_file_is_created()
    {
        File.AppendAllText(ConfigFile, "[include]\n\tpath = included.config\n");
        ReadAndCollect().Should().Be("initial");
        ReadAndCollect(expectedConfigLists: 0).Should().Be("initial");

        CreateIncludedFile("included", age: TimeSpan.FromHours(1));

        ReadAndCollect(expectedConfigLists: 1).Should().Be("included");
    }

    [Test]
    public void Config_should_be_read_again_if_branch_of_conditional_include_is_checked_out()
    {
        CreateIncludedFile("onbranch", age: TimeSpan.FromHours(1));
        File.AppendAllText(ConfigFile, "[includeIf \"onbranch:other\"]\n\tpath = included.config\n");
        ReadAndCollect().Should().Be("initial");
        ReadAndCollect(expectedConfigLists: 0).Should().Be("initial");

        File.WriteAllText(Path.Combine(GitDir, "HEAD"), "ref: refs/heads/other\n");

        ReadAndCollect(expectedConfigLists: 1).Should().Be("onbranch");
    }

    [Test]
    public void Config_should_be_read_again_if_global_config_changed()
    {
        const string globalSettingName = "hjglobal.test";
        string globalConfig = Path.Combine(_helper.TemporaryPath, "global.config");
        File.WriteAllText(globalConfig, "[hjglobal]\n\ttest = initial\n");
        Environment.SetEnvironmentVariable(GlobalConfigVariable, globalConfig);
        ReadAndCollect(settingName: globalSettingName).Should().Be("initial");
        ReadAndCollect(settingName: globalSettingName, expectedConfigLists: 0).Should().Be("initial");

        File.WriteAllText(globalConfig, "[hjglobal]\n\ttest = changed\n");
        ReadAndCollect(settingName: globalSettingName, expectedConfigLists: 1).Should().Be("changed");

        Environment.SetEnvironmentVariable(GlobalConfigVariable, _originalGlobalConfig);
        ReadAndCollect(settingName: globalSettingName, expectedConfigLists: 1).Should().BeEmpty();
    }

    [Test]
    public void Config_should_be_read_again_if_environment_changed()
    {
        ReadAndCollect().Should().Be("initial");
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", SettingName);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "environment");

            ReadAndCollect(expectedConfigLists: 1).Should().Be("environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", null);
        }
    }

    [Test]
    public void Config_should_be_read_again_if_git_directory_is_moved()
    {
        ReadAndCollect().Should().Be("initial");

        string otherGitDir = Path.Combine(_helper.TemporaryPath, "other.git");
        Directory.Move(GitDir, otherGitDir);
        File.WriteAllText(GitDir, $"gitdir: {otherGitDir.ToPosixPath()}\n");
        string otherConfig = Path.Combine(otherGitDir, "config");
        File.WriteAllText(otherConfig, File.ReadAllText(otherConfig).Replace("initial", "replaced"));

        ReadAndCollect(expectedConfigLists: 1).Should().Be("replaced");
        ReadAndCollect(expectedConfigLists: 0).Should().Be("replaced");
    }

    [Test]
    public void Config_should_be_read_again_after_invalidate()
    {
        ReadAndCollect().Should().Be("initial");

        GitModule module = CreateModule();
        module.InvalidateGitSettings();
        CommandLog.Clear();
        module.GetEffectiveSetting(SettingName).Should().Be("initial");
        CountConfigLists().Should().Be(1);
    }

    [Test]
    public void Config_should_not_be_reused_if_included_file_is_recent()
    {
        // Whether git has read this content of the file is not known for sure
        CreateIncludedFile("included", age: TimeSpan.Zero);
        File.AppendAllText(ConfigFile, "[include]\n\tpath = included.config\n");
        ReadAndCollect().Should().Be("included");

        ReadAndCollect(expectedConfigLists: 1).Should().Be("included");
    }

    [Test]
    public void Config_should_not_be_reused_if_disabled()
    {
        AppSettings.RepoSnapshotMemoryLimit.Value = 0;
        ReadAndCollect().Should().Be("initial");

        ReadAndCollect(expectedConfigLists: 1).Should().Be("initial");
    }

    [Test]
    public void Reused_config_should_be_the_same_as_read_from_git()
    {
        File.AppendAllText(
            ConfigFile,
            "[remote \"origin\"]\n\turl = https://example.com/a.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n\tfetch = +refs/tags/*:refs/tags/*\n[core]\n\tnovalue\n");
        ReadAndCollect();

        CommandLog.Clear();
        GitModule module = CreateModule();
        GitConfigSnapshot reused = module.ConfigSnapshotProvider.GetSnapshot();
        CountConfigLists().Should().Be(0);

        GitConfigSnapshot expected = GitConfigSnapshot.Parse(module.GitExecutable.GetGitSettingsWithScope());
        reused.Entries.Should().Equal(expected.Entries);
        reused.RemoteNames.Should().Equal(expected.RemoteNames);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static int CountConfigLists()
        => CommandLog.Commands.Count(c => c.Arguments.Contains("config list") || c.Arguments.Contains("config --list"));

    private string CreateIncludedFile(string value, TimeSpan age)
    {
        string path = Path.Combine(GitDir, "included.config");
        File.WriteAllText(path, $"[hj]\n\ttest = {value}\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private GitModule CreateModule() => new(_executorProvider, _workingDir);

    private string ReadAndCollect(int? expectedConfigLists = null, string settingName = SettingName)
    {
        CommandLog.Clear();
        string value = ReadSetting(settingName);
        if (expectedConfigLists is int expected)
        {
            CountConfigLists().Should().Be(expected);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string ReadSetting(string settingName) => CreateModule().GetEffectiveSetting(settingName);
}
