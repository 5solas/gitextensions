using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;

namespace GitCommandsTests.Git;

[NonParallelizable]
public sealed class FsMonitorDaemonTests
{
    [TestCase(@"C:\repo\.git\", @"\\.\pipe\C_\repo\.git\fsmonitor--daemon.ipc")]
    [TestCase(@"d:/work dir/repo/.git/worktrees/x", @"\\.\pipe\d_\work dir\repo\.git\worktrees\x\fsmonitor--daemon.ipc")]
    [TestCase(@"\\server\share\repo\.git\", null)]
    [TestCase(@"\\wsl$\Ubuntu\repo\.git", null)]
    [TestCase(".git", null)]
    [TestCase("", null)]
    public void GetPipeName_should_return_the_name_used_by_git(string gitDir, string? expected)
    {
        FsMonitorDaemon.GetPipeName(gitDir).Should().Be(expected);
    }

    [TestCase("true", true)]
    [TestCase("TRUE", true)]
    [TestCase("yes", true)]
    [TestCase("on", true)]
    [TestCase("1", true)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase(".git/hooks/query-watchman", false)]
    public void IsEnabled_should_detect_the_builtin_daemon(string? value, bool expected)
    {
        FsMonitorDaemon.IsEnabled(value).Should().Be(expected);
    }

    [Test]
    public void IsEnabled_should_not_run_git()
    {
        using GitModuleTestHelper helper = new();
        helper.Module.GitExecutable.GetOutput("config core.fsmonitor true");
        GitModule module = new(new GitExecutorProvider(new GitDirectoryResolver()), helper.Module.WorkingDir);
        int commandCount = GitCommands.Logging.CommandLog.Commands.Count();

        module.InvalidateGitSettings();
        FsMonitorDaemon.IsEnabled(module).Should().BeNull();
        GitCommands.Logging.CommandLog.Commands.Count().Should().Be(commandCount);

        module.GetEffectiveSetting("core.fsmonitor").Should().Be("true");
        FsMonitorDaemon.IsEnabled(module).Should().BeTrue();
        FsMonitorDaemon.IsEnabled(NSubstitute.Substitute.For<IGitModule>()).Should().BeNull();
    }

    [Test]
    public void Daemon_should_be_detected_and_stopped()
    {
        using GitModuleTestHelper helper = new("repo Ünï");
        string pipeName = FsMonitorDaemon.GetPipeName(helper.Module.WorkingDirGitDir)!;
        FsMonitorDaemon.GetPipeNames().Should().NotContain(pipeName);

        helper.Module.GitExecutable.Execute("fsmonitor--daemon start", throwOnErrorExit: true);
        try
        {
            FsMonitorDaemon.GetPipeNames().Contains(pipeName).Should().BeTrue();
            FsMonitorDaemon.GetPipeNames().Contains(pipeName).Should().BeTrue("the daemon must not be affected by the check");
        }
        finally
        {
            FsMonitorDaemon.Stop(helper.Module.WorkingDir);
        }

        FsMonitorDaemon.GetPipeNames().Contains(pipeName).Should().BeFalse();

        // Neither a missing daemon nor a missing directory is an error
        FsMonitorDaemon.Stop(helper.Module.WorkingDir);
        FsMonitorDaemon.Stop(Path.Combine(helper.Module.WorkingDir, "missing"));
    }
}
