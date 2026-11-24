using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;

namespace GitCommandsTests.Git;

public sealed class IndexRefreshGateTests
{
    [TestCase(@"""C:\Program Files\Git\mingw64\bin\git.exe"" --git-dir=""C:/a b/.git"" cat-file --batch-command", true)]
    [TestCase(@"git.exe --git-dir=C:/repo/.git cat-file --batch-command", true)]
    [TestCase(@"git -c core.x=y -C ""C:\a b"" --no-optional-locks cat-file --batch", true)]
    [TestCase(@"git cat-file --batch-check=%(objectname)", true)]
    [TestCase(@"""C:\Program Files\Git\mingw64\libexec\git-core\git.exe"" fsmonitor--daemon run --detach --ipc-threads=8", true)]
    [TestCase(@"git cat-file blob HEAD:file", false)]
    [TestCase(@"git status", false)]
    [TestCase(@"git commit -m "" cat-file --batch""", false)]
    [TestCase(@"git add cat-file --batch", false)]
    [TestCase(@"git --git-dir="" cat-file --batch"" commit", false)]
    [TestCase(@"git -C cat-file --batch", false)]
    [TestCase(null, false)]
    public void IsPersistentProcess_should_detect_processes_which_do_not_lock_the_index(string? commandLine, bool expected)
    {
        IndexRefreshGate.IsPersistentProcess(commandLine).Should().Be(expected);
    }

    [Test]
    public void IsSafe_should_be_false_if_index_is_locked()
    {
        using GitModuleTestHelper helper = new();
        File.WriteAllText(Path.Combine(helper.Module.WorkingDirGitDir, "index.lock"), "");

        IndexRefreshGate.IsSafe(helper.Module).Should().BeFalse();
    }

    [TestCase(@"\\wsl$\Ubuntu\home\user\repo\")]
    [TestCase(@"\\wsl.localhost\Ubuntu\home\user\repo\")]
    public void IsSafe_should_be_false_for_WSL(string workingDir)
    {
        IGitModule module = NSubstitute.Substitute.For<IGitModule>();
        NSubstitute.SubstituteExtensions.Returns(module.WorkingDir, workingDir);

        IndexRefreshGate.IsSafe(module).Should().BeFalse();
    }
}
