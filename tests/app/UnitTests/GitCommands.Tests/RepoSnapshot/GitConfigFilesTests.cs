using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitCommands.RepoSnapshot;
using GitCommands.Settings;

namespace GitCommandsTests.RepoSnapshot;

[TestFixture]
[NonParallelizable]
public sealed class GitConfigFilesTests
{
    private string? _originalHome;
    private GitModuleTestHelper _helper = null!;

    private string WorkingDir => _helper.Module.WorkingDir;

    private string GitDir => Path.Combine(WorkingDir, ".git");

    [SetUp]
    public void SetUp()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _helper = new GitModuleTestHelper();

        // Starting git sets the variable
        Environment.SetEnvironmentVariable("HOME", Path.Combine(Path.GetTempPath(), "hj-home"));
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        _helper.Dispose();
    }

    [Test]
    public void ParseGitSettingsWithOrigin_should_parse_scope_origin_key_and_value()
    {
        Commands.ParseGitSettingsWithOrigin("system\0file:C:/git/etc/gitconfig\0core.autocrlf\ntrue\0local\0file:.git/config\0a.multi\nline 1\nline 2\0local\0file:.git/config\0core.bare\0")
            .Should().Equal(
                new GitConfigEntryOrigin(new GitConfigEntry("system", "core.autocrlf", "true"), "file:C:/git/etc/gitconfig"),
                new GitConfigEntryOrigin(new GitConfigEntry("local", "a.multi", "line 1\nline 2"), "file:.git/config"),
                new GitConfigEntryOrigin(new GitConfigEntry("local", "core.bare", "true"), "file:.git/config"));
        Commands.ParseGitSettingsWithOrigin("").Should().BeEmpty();
    }

    [Test]
    public void All_files_should_contain_system_global_local_and_worktree_config()
    {
        GetAllFiles().Should().Contain(
        [
            @"C:\git\etc\gitconfig",
            Path.Combine(Path.GetTempPath(), "hj-home", ".gitconfig"),
            Path.Combine(Path.GetTempPath(), "hj-home", ".config", "git", "config"),
            Path.Combine(GitDir, "config"),
            Path.Combine(GitDir, "config.worktree")
        ]);
    }

    [TestCase("include.path", "~/sub/a.config", "hj-home\\sub\\a.config")]
    [TestCase("includeif.gitdir:C:/work/.path", "~/a.config", "hj-home\\a.config")]
    public void All_files_should_contain_included_files_of_the_home_directory(string key, string value, string expected)
    {
        GetAllFiles(("local", key, value, "file:.git/config")).Should().Contain(Path.Combine(Path.GetTempPath(), expected));
    }

    [Test]
    public void All_files_should_contain_included_files_relative_to_the_including_file()
    {
        GetAllFiles(("local", "include.path", "sub/a.config", "file:.git/config")).Should().Contain(Path.Combine(GitDir, "sub", "a.config"));
        GetAllFiles(("global", "include.path", "../a.config", "file:D:/x/y/global.config")).Should().Contain(@"D:\x\a.config");
        GetAllFiles(("global", "include.path", @"D:\absolute.config", "file:D:/x/y/global.config")).Should().Contain(@"D:\absolute.config");
    }

    [Test]
    public void All_files_should_contain_HEAD_only_for_include_depending_on_the_branch()
    {
        string head = Path.Combine(GitDir, "HEAD");
        GetAllFiles().Should().NotContain(head);
        GetAllFiles(("local", "includeif.gitdir:x.path", "a.config", "file:.git/config")).Should().NotContain(head);
        GetAllFiles(("local", "includeif.onbranch:Main/**.path", "a.config", "file:.git/config")).Should().Contain(head);
    }

    [TestCase("local", "include.path", "~other/a.config", "file:.git/config", Description = "home directory of another user")]
    [TestCase("local", "include.path", "%(prefix)/a.config", "file:.git/config", Description = "installation directory of git")]
    [TestCase("local", "include.path", "/etc/a.config", "file:.git/config", Description = "POSIX path")]
    [TestCase("command", "include.path", "a.config", "command line:", Description = "relative to unknown directory")]
    [TestCase("local", "a.b", "c", "blob:HEAD:config", Description = "not a file")]
    [TestCase("system", "a.b", "c", "file:/etc/gitconfig", Description = "POSIX path")]
    [TestCase("unknown", "a.b", "c", "", Description = "unknown origin")]
    public void All_files_should_be_unknown(string scope, string key, string value, string origin)
    {
        GetAllFiles((scope, key, value, origin)).Should().BeNull();
    }

    [Test]
    public void All_files_should_be_unknown_if_git_did_not_read_the_system_or_the_local_config()
    {
        GitConfigFiles.TryResolve(WorkingDir, out GitConfigFiles? files).Should().BeTrue();
        GitConfigEntryOrigin system = new(new GitConfigEntry("system", "a.b", "c"), "file:C:/git/etc/gitconfig");
        GitConfigEntryOrigin local = new(new GitConfigEntry("local", "a.b", "c"), "file:.git/config");
        GitConfigEntryOrigin otherLocal = new(new GitConfigEntry("local", "a.b", "c"), "file:C:/other/.git/config");

        files!.TryGetAllFiles([system, local]).Should().NotBeNull();
        files.TryGetAllFiles([local]).Should().BeNull();
        files.TryGetAllFiles([system]).Should().BeNull();
        files.TryGetAllFiles([system, otherLocal]).Should().BeNull();
    }

    [TestCase("")]
    [TestCase("relative")]
    [TestCase(@"\\wsl$\Ubuntu\home\user\repo\")]
    [TestCase(@"\\wsl.localhost\Ubuntu\home\user\repo\")]
    public void TryResolve_should_fail(string workingDir)
    {
        GitConfigFiles.TryResolve(workingDir, out _).Should().BeFalse();
    }

    [Test]
    public void Files_of_the_config_of_a_real_repository_should_be_known()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);

        ConfigSnapshotPart.Load(_helper.Module.GitExecutable, out ConfigSnapshotPart? part).Should().NotBeNull();

        part.Should().NotBeNull();
        part!.IsCurrent().Should().BeTrue();
        part.Files.Should().Contain(Path.Combine(GitDir, "config"));
        part.EstimatedBytes.Should().BePositive();
    }

    private IReadOnlyList<string>? GetAllFiles(params (string Scope, string Key, string Value, string Origin)[] entries)
    {
        GitConfigFiles.TryResolve(WorkingDir, out GitConfigFiles? files).Should().BeTrue();
        return files!.TryGetAllFiles(
        [
            new GitConfigEntryOrigin(new GitConfigEntry("system", "a.b", "c"), "file:C:/git/etc/gitconfig"),
            new GitConfigEntryOrigin(new GitConfigEntry("local", "a.b", "c"), "file:.git/config"),
            .. entries.Select(e => new GitConfigEntryOrigin(new GitConfigEntry(e.Scope, e.Key, e.Value), e.Origin))
        ]);
    }
}
