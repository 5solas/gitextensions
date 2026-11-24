using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;
using GitUI.LeftPanel;

namespace GitUITests.LeftPanel;

public sealed class MainWorktreeReaderTests
{
    private ReferenceRepository _repository = null!;

    private GitModule Module => _repository.Module;

    [SetUp]
    public void SetUp()
    {
        _repository = new ReferenceRepository();
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
    }

    [TestCase("")]
    [TestCase("checkout -q -b feature/a-b")]
    [TestCase("checkout -q --detach")]
    [TestCase("pack-refs --all")]
    public void TryRead_should_equal_git(string arguments)
    {
        if (arguments.Length > 0)
        {
            Module.GitExecutable.GetOutput(arguments);
        }

        MainWorktreeReader.TryRead(Module, out GitWorktree? worktree).Should().BeTrue();

        worktree!.Path.Should().BeEquivalentTo(Module.GetWorktrees().Single().Path);
        (worktree with { Path = "" }).Should().Be(Module.GetWorktrees().Single() with { Path = "" });
    }

    [Test]
    public void TryRead_should_fail_for_linked_worktrees()
    {
        string path = Path.TrimEndingDirectorySeparator(Module.WorkingDir) + "-linked";
        Module.GitExecutable.GetOutput($"worktree add -q --detach \"{path}\"");

        MainWorktreeReader.TryRead(Module, out _).Should().BeFalse();
        MainWorktreeReader.TryRead(new GitModule(new GitExecutorProvider(new GitDirectoryResolver()), path), out _).Should().BeFalse();

        Module.GitExecutable.GetOutput($"worktree remove --force \"{path}\"");
        MainWorktreeReader.TryRead(Module, out _).Should().BeTrue();
    }

    [Test]
    public void TryRead_should_fail_for_unborn_branch()
    {
        Module.GitExecutable.GetOutput("symbolic-ref HEAD refs/heads/unborn");

        MainWorktreeReader.TryRead(Module, out _).Should().BeFalse();
    }

    [Test]
    public void TryRead_should_fail_for_unknown_HEAD()
    {
        File.WriteAllText(Path.Combine(Module.WorkingDirGitDir, "HEAD"), "ref: refs/heads/.invalid\n");

        MainWorktreeReader.TryRead(Module, out _).Should().BeFalse();
    }
}
