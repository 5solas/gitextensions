using System.IO.Abstractions;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitCommands.Logging;
using NSubstitute;

namespace GitCommandsTests.Git;
public class GitDirectoryResolverTests
{
    private readonly string _workingDir = @"c:\dev\repo";
    private string _gitWorkingDir = null!;
    private string _gitFile = null!;
    private FileBase _file = null!;
    private DirectoryBase _directory = null!;
    private IFileSystem _fileSystem = null!;
    private GitDirectoryResolver _resolver = null!;

    [SetUp]
    public void Setup()
    {
        _gitFile = Path.Combine(_workingDir, ".git");
        _gitWorkingDir = _gitFile.EnsureTrailingPathSeparator();

        _file = Substitute.For<FileBase>();
        _directory = Substitute.For<DirectoryBase>();
        _fileSystem = Substitute.For<IFileSystem>();
        _fileSystem.Directory.Returns(_directory);
        _fileSystem.File.Returns(_file);

        _directory.Exists(_workingDir).Returns(true);

        _resolver = new GitDirectoryResolver(_fileSystem);
    }

    [Test]
    public void Resolve_should_throw_if_path_is_null()
    {
        ((Action)(() => _resolver.Resolve(null!))).Should().Throw<ArgumentNullException>();
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t")]
    public void Resolve_should_return_empty_string_if_working_dir_is_empty(string workingDir)
    {
        _resolver.Resolve(workingDir).Should().BeEmpty();
    }

    [Test]
    public void Resolve_should_return_original_path_if_git_file_or_folder_absent()
    {
        _directory.Exists(_gitWorkingDir).Returns(false);
        _file.Exists(Arg.Any<string>()).Returns(false);

        _resolver.Resolve(_workingDir).Should().Be(_workingDir);
    }

    [Test]
    public void Resolve_should_return_path_to_git_folder_if_present()
    {
        _directory.Exists(_gitWorkingDir).Returns(true);
        _file.Exists(Arg.Any<string>()).Returns(true);

        _resolver.Resolve(_workingDir).Should().Be(_gitWorkingDir);
    }

    [Platform(Include = "Win")]
    [Test]
    public void Resolve_should_return_path_from_git_file_if_present()
    {
        _file.Exists(_gitFile).Returns(true);
        _file.ReadLines(_gitFile).Returns(new[] { "", " ", @"gitdir: c:/dev/repo/.git/modules/Externals/Git.hub", "text" });

        _resolver.Resolve(_workingDir).Should().Be(@"c:\dev\repo\.git\modules\Externals\Git.hub\");

        _directory.DidNotReceive().Exists(_gitWorkingDir);
    }

    [Test]
    public void Resolve_should_return_resolved_full_path_from_git_file_if_present()
    {
        _file.Exists(_gitFile).Returns(true);
        _file.ReadLines(_gitFile).Returns(new[] { "", " ", @"gitdir: ../.git/modules/Externals/Git.hub", "text" });

        _resolver.Resolve(_workingDir).Should().Be(@"c:\dev\.git\modules\Externals\Git.hub\");

        _directory.DidNotReceive().Exists(_gitWorkingDir);
    }

    [Platform(Include = "Win")]
    [Test]
    public void Resolve_should_reconstruct_wsl_unc_path_for_posix_gitdir_in_linked_worktree()
    {
        // Regression test for #13272: committing from a linked worktree in a WSL repo.
        // The working tree is accessed via a \\wsl$ UNC path, but the .git file's "gitdir:"
        // line points at a POSIX absolute path (as written by the Linux git binary) that
        // lives under the main repo's .git, on a different top-level directory.
        // The resolver must convert that POSIX path back into the \\wsl$ UNC path so that
        // COMMITMESSAGE is written to the same location git reads it from.
        string workingDir = @"\\wsl$\Ubuntu\home\user\source\MyRepo.wt\ACTIVE";
        string gitFile = Path.Combine(workingDir, ".git");

        _file.Exists(gitFile).Returns(true);
        _file.ReadLines(gitFile).Returns(new[] { "gitdir: /home/user/source/MyRepo/.git/worktrees/ACTIVE" });

        _resolver.Resolve(workingDir)
            .Should().Be(@"\\wsl$\Ubuntu\home\user\source\MyRepo\.git\worktrees\ACTIVE\");

        _directory.DidNotReceive().Exists(gitFile.EnsureTrailingPathSeparator());
    }

    [Platform(Include = "Win")]
    [Test]
    public void Resolve_should_map_wsl_mnt_path_to_windows_drive()
    {
        // A /mnt/<drive> path (WSL view of a Windows drive) must map back to the drive letter.
        string workingDir = @"\\wsl$\Ubuntu\mnt\c\dev\repo";
        string gitFile = Path.Combine(workingDir, ".git");

        _file.Exists(gitFile).Returns(true);
        _file.ReadLines(gitFile).Returns(new[] { "gitdir: /mnt/c/dev/repo/.git/worktrees/ACTIVE" });

        _resolver.Resolve(workingDir)
            .Should().Be(@"C:\dev\repo\.git\worktrees\ACTIVE\");
    }

    [Platform(Include = "Win")]
    [Test]
    public void Resolve_should_return_unc_gitdir_path_unchanged()
    {
        // A fully qualified UNC gitdir path is already usable and must be returned as-is.
        _file.Exists(_gitFile).Returns(true);
        _file.ReadLines(_gitFile).Returns(new[] { @"gitdir: \\wsl$\Ubuntu\home\user\source\MyRepo\.git\worktrees\ACTIVE" });

        _resolver.Resolve(_workingDir)
            .Should().Be(@"\\wsl$\Ubuntu\home\user\source\MyRepo\.git\worktrees\ACTIVE\");
    }

    [Test]
    public void Resolve_non_bare_repository_real_filesystem()
    {
        _resolver = new GitDirectoryResolver();
        using GitModuleTestHelper helper = new();
        _resolver.Resolve(helper.Module.WorkingDir).Should().Be(helper.Module.WorkingDirGitDir);
    }

    [Test]
    public void Resolve_submodule_real_filesystem()
    {
        using GitModuleTestHelper helper = new();
        string submodulePath = Path.Combine(helper.Module.WorkingDir, "External", "Git.hub");
        helper.CreateFile(submodulePath, ".git", "\r \r\ngitdir: ../../.git/modules/Externals/Git.hub\r\ntext");
        _resolver = new GitDirectoryResolver();

        _resolver.Resolve(submodulePath).Should().Be($@"{helper.Module.WorkingDirGitDir}modules\Externals\Git.hub\");
        _resolver.Resolve(helper.Module.WorkingDir).Should().Be(helper.Module.WorkingDirGitDir);
    }

    [Test]
    public void ResolveCommonDir_should_throw_if_path_is_null()
    {
        ((Action)(() => _resolver.ResolveCommonDir(null!))).Should().Throw<ArgumentNullException>();
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t")]
    public void ResolveCommonDir_should_return_empty_string_if_path_is_empty(string gitDir)
    {
        _resolver.ResolveCommonDir(gitDir).Should().BeEmpty();
    }

    [Test]
    public void ResolveCommonDir_should_return_original_path_if_commondir_absent()
    {
        _file.Exists(Arg.Any<string>()).Returns(false);

        _resolver.ResolveCommonDir(_gitWorkingDir).Should().Be(_gitWorkingDir);
    }

    [Platform(Include = "Win")]
    [Test]
    public void ResolveCommonDir_should_return_resolved_path_for_relative_commondir()
    {
        string worktreeGitDir = @"c:\dev\repo\.git\worktrees\wt1";
        string commondirFile = Path.Combine(worktreeGitDir, "commondir");

        _file.Exists(commondirFile).Returns(true);
        _file.ReadLines(commondirFile).Returns(new[] { "../.." });

        _resolver.ResolveCommonDir(worktreeGitDir).Should().Be(@"c:\dev\repo\.git\");
    }

    [Platform(Include = "Win")]
    [Test]
    public void ResolveCommonDir_should_return_resolved_path_for_absolute_commondir()
    {
        string worktreeGitDir = @"c:\dev\repo\.git\worktrees\wt1";
        string commondirFile = Path.Combine(worktreeGitDir, "commondir");

        _file.Exists(commondirFile).Returns(true);
        _file.ReadLines(commondirFile).Returns(new[] { @"c:\dev\main\.git" });

        _resolver.ResolveCommonDir(worktreeGitDir).Should().Be(@"c:\dev\main\.git\");
    }

    [Platform(Include = "Win")]
    [Test]
    public void ResolveCommonDir_should_reconstruct_wsl_unc_path_for_posix_commondir()
    {
        string worktreeGitDir = @"\\wsl$\Ubuntu\home\user\repo\.git\worktrees\wt1";
        string commondirFile = Path.Combine(worktreeGitDir, "commondir");

        _file.Exists(commondirFile).Returns(true);
        _file.ReadLines(commondirFile).Returns(new[] { "/home/user/repo/.git" });

        _resolver.ResolveCommonDir(worktreeGitDir)
            .Should().Be(@"\\wsl$\Ubuntu\home\user\repo\.git\");
    }

    [Platform(Include = "Win")]
    [Test]
    public void ResolveCommonDir_should_map_wsl_mnt_path_to_windows_drive()
    {
        string worktreeGitDir = @"\\wsl$\Ubuntu\mnt\c\dev\repo\.git\worktrees\wt1";
        string commondirFile = Path.Combine(worktreeGitDir, "commondir");

        _file.Exists(commondirFile).Returns(true);
        _file.ReadLines(commondirFile).Returns(new[] { "/mnt/c/dev/repo/.git" });

        _resolver.ResolveCommonDir(worktreeGitDir)
            .Should().Be(@"C:\dev\repo\.git\");
    }

    [Test]
    public void ResolveCommonDir_normal_repository_real_filesystem()
    {
        using GitModuleTestHelper helper = new();
        CommandLog.Clear();
        string commonDir = helper.Module.GitCommonDirectory;
        CommandLog.Commands.Should().BeEmpty();

        string gitOutput = helper.Module.GitExecutable.GetOutput("rev-parse --git-common-dir").Trim();
        string expected = Path.GetFullPath(Path.Combine(helper.Module.WorkingDir, gitOutput));
        commonDir.TrimEnd('/', '\\').Should().Be(expected.TrimEnd('/', '\\'));
    }

    [Test]
    public void ResolveCommonDir_linked_worktree_real_filesystem()
    {
        using GitModuleTestHelper helper = new();
        helper.CreateFile(helper.Module.WorkingDir, "file.txt", "content");
        helper.Module.GitExecutable.Execute("add file.txt");
        helper.Module.GitExecutable.Execute("commit -m \"Initial commit\"");

        string worktreePath = Path.Combine(helper.TemporaryPath, "wt1");
        helper.Module.GitExecutable.Execute($"worktree add \"{worktreePath}\"");

        GitModule worktreeModule = new(new GitExecutorProvider(new GitDirectoryResolver()), worktreePath);
        CommandLog.Clear();
        string worktreeCommonDir = worktreeModule.GitCommonDirectory;
        CommandLog.Commands.Should().BeEmpty();

        string gitOutput = worktreeModule.GitExecutable.GetOutput("rev-parse --git-common-dir").Trim();
        string expected = Path.GetFullPath(Path.Combine(worktreePath, gitOutput));
        worktreeCommonDir.TrimEnd('/', '\\').Should().Be(expected.TrimEnd('/', '\\'));
    }

    [Test]
    public void ResolveCommonDir_bare_repository_and_worktree_real_filesystem()
    {
        using GitModuleTestHelper helper = new();
        helper.CreateFile(helper.Module.WorkingDir, "file.txt", "content");
        helper.Module.GitExecutable.Execute("add file.txt");
        helper.Module.GitExecutable.Execute("commit -m \"Initial commit\"");

        string barePath = Path.Combine(helper.TemporaryPath, "bare.git");
        Directory.CreateDirectory(barePath);
        GitModule bareModule = new(new GitExecutorProvider(new GitDirectoryResolver()), barePath);
        bareModule.Init(bare: true, shared: false);
        helper.Module.GitExecutable.Execute($"push \"{barePath.ToPosixPath()}\" HEAD:main");

        CommandLog.Clear();
        string bareCommonDir = bareModule.GitCommonDirectory;
        CommandLog.Commands.Should().BeEmpty();
        string bareGitOutput = bareModule.GitExecutable.GetOutput("rev-parse --git-common-dir").Trim();
        string expectedBare = Path.GetFullPath(Path.Combine(barePath, bareGitOutput));
        bareCommonDir.TrimEnd('/', '\\').Should().Be(expectedBare.TrimEnd('/', '\\'));

        string bareWorktreePath = Path.Combine(helper.TemporaryPath, "bare-wt");
        bareModule.GitExecutable.Execute($"worktree add \"{bareWorktreePath}\" main");

        GitModule bareWorktreeModule = new(new GitExecutorProvider(new GitDirectoryResolver()), bareWorktreePath);
        CommandLog.Clear();
        string bareWorktreeCommonDir = bareWorktreeModule.GitCommonDirectory;
        CommandLog.Commands.Should().BeEmpty();
        string bareWorktreeGitOutput = bareWorktreeModule.GitExecutable.GetOutput("rev-parse --git-common-dir").Trim();
        string expectedBareWorktree = Path.GetFullPath(Path.Combine(bareWorktreePath, bareWorktreeGitOutput));
        bareWorktreeCommonDir.TrimEnd('/', '\\').Should().Be(expectedBareWorktree.TrimEnd('/', '\\'));
    }

    [Test]
    public void ResolveCommonDir_submodule_real_filesystem()
    {
        using GitModuleTestHelper helper = new();
        string submodulePath = Path.Combine(helper.Module.WorkingDir, "External", "Sub");
        string gitdirFolder = Path.Combine(helper.Module.WorkingDir, ".git", "modules", "External", "Sub");
        Directory.CreateDirectory(gitdirFolder);
        helper.CreateFile(submodulePath, ".git", "gitdir: ../../.git/modules/External/Sub\r\n");

        GitModule subModule = new(new GitExecutorProvider(new GitDirectoryResolver()), submodulePath);
        CommandLog.Clear();
        string subCommonDir = subModule.GitCommonDirectory;
        CommandLog.Commands.Should().BeEmpty();

        subCommonDir.TrimEnd('/', '\\').Should().Be(gitdirFolder.TrimEnd('/', '\\'));
    }
}
