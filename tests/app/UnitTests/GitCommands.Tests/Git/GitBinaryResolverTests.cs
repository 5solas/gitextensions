using System.Diagnostics;
using GitCommands;

namespace GitCommandsTests.Git;

[TestFixture]
public sealed class GitBinaryResolverTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        GitBinaryResolver.ClearCache();
    }

    [TearDown]
    public void TearDown()
    {
        GitBinaryResolver.ClearCache();
        Directory.Delete(_root, recursive: true);
    }

    private string Touch(params string[] parts)
    {
        string path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private string FakeInstall(string realFolder = "ucrt64", bool withSh = true, string launcherFolder = "cmd")
    {
        string launcher = Touch(launcherFolder, "git.exe");
        Touch(realFolder, "bin", "git.exe");
        if (withSh)
        {
            Touch("usr", "bin", "sh.exe");
        }

        return launcher;
    }

    [TestCase("cmd")]
    [TestCase("bin")]
    public void Resolve_returns_real_binary_for_launcher(string launcherFolder)
    {
        string launcher = FakeInstall(launcherFolder: launcherFolder);

        GitBinaryResolver.Resolve(launcher).Should().Be(Path.Combine(_root, "ucrt64", "bin", "git.exe"));
    }

    [Test]
    public void Resolve_requires_sh_exe()
    {
        string launcher = FakeInstall(withSh: false);

        GitBinaryResolver.Resolve(launcher).Should().BeNull();
    }

    [Test]
    public void Resolve_prefers_ucrt64_over_mingw64()
    {
        string launcher = FakeInstall();
        Touch("mingw64", "bin", "git.exe");

        GitBinaryResolver.Resolve(launcher).Should().Be(Path.Combine(_root, "ucrt64", "bin", "git.exe"));
    }

    [TestCase("mingw64")]
    [TestCase("clangarm64")]
    [TestCase("mingw32")]
    public void Resolve_supports_other_layouts(string folder)
    {
        string launcher = FakeInstall(realFolder: folder);

        GitBinaryResolver.Resolve(launcher).Should().Be(Path.Combine(_root, folder, "bin", "git.exe"));
    }

    [Test]
    public void Resolve_ignores_git_cmd()
    {
        FakeInstall();
        string gitCmd = Touch("cmd", "git.cmd");

        GitBinaryResolver.Resolve(gitCmd).Should().BeNull();
    }

    [Test]
    public void Resolve_ignores_real_binary_itself()
    {
        FakeInstall();

        GitBinaryResolver.Resolve(Path.Combine(_root, "ucrt64", "bin", "git.exe")).Should().BeNull();
    }

    [Test]
    public void Resolve_ignores_missing_launcher_and_unknown_structure()
    {
        string custom = Touch("tools", "git.exe");

        GitBinaryResolver.Resolve(custom).Should().BeNull();
        GitBinaryResolver.Resolve(Path.Combine(_root, "cmd", "git.exe")).Should().BeNull();
    }

    [Test]
    public void Resolve_follows_scoop_shim()
    {
        string install = Path.Combine(_root, "apps", "git", "current");
        Directory.CreateDirectory(Path.Combine(install, "cmd"));
        Directory.CreateDirectory(Path.Combine(install, "ucrt64", "bin"));
        Directory.CreateDirectory(Path.Combine(install, "usr", "bin"));
        File.WriteAllText(Path.Combine(install, "cmd", "git.exe"), "x");
        File.WriteAllText(Path.Combine(install, "ucrt64", "bin", "git.exe"), "x");
        File.WriteAllText(Path.Combine(install, "usr", "bin", "sh.exe"), "x");
        string shims = Path.Combine(_root, "shims");
        Directory.CreateDirectory(shims);
        File.WriteAllText(Path.Combine(shims, "git.exe"), "shim");
        File.WriteAllText(Path.Combine(shims, "git.shim"), $"path = \"{Path.Combine(install, "cmd", "git.exe")}\"\r\n");

        GitBinaryResolver.Resolve(Path.Combine(shims, "git.exe")).Should().Be(Path.Combine(install, "ucrt64", "bin", "git.exe"));
    }

    [Test]
    public void Resolve_finds_bare_name_in_path()
    {
        string launcher = FakeInstall();
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(launcher));

            GitBinaryResolver.Resolve("git").Should().Be(Path.Combine(_root, "ucrt64", "bin", "git.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Test]
    public void Resolve_caches_result_per_setting_string()
    {
        string launcher = FakeInstall();
        string expected = Path.Combine(_root, "ucrt64", "bin", "git.exe");
        GitBinaryResolver.Resolve(launcher).Should().Be(expected);

        File.Delete(expected);

        GitBinaryResolver.Resolve(launcher).Should().Be(expected);
    }

    [Test]
    public void TryRedirect_changes_start_file_name()
    {
        string launcher = FakeInstall();
        ProcessStartInfo info = new() { FileName = launcher };

        GitBinaryResolver.TryRedirect(info, launcher, enabled: true);

        info.FileName.Should().Be(Path.Combine(_root, "ucrt64", "bin", "git.exe"));
    }

    [Test]
    public void TryRedirect_does_nothing_when_disabled()
    {
        string launcher = FakeInstall();
        ProcessStartInfo info = new() { FileName = launcher };

        GitBinaryResolver.TryRedirect(info, launcher, enabled: false);

        info.FileName.Should().Be(launcher);
    }

    [Test]
    public void TryRedirect_ignores_other_executables_and_shell_execute()
    {
        string launcher = FakeInstall();
        ProcessStartInfo other = new() { FileName = Path.Combine(_root, "cmd", "gitk.exe") };
        ProcessStartInfo shell = new() { FileName = launcher, UseShellExecute = true };

        GitBinaryResolver.TryRedirect(other, launcher, enabled: true);
        GitBinaryResolver.TryRedirect(shell, launcher, enabled: true);

        other.FileName.Should().Be(Path.Combine(_root, "cmd", "gitk.exe"));
        shell.FileName.Should().Be(launcher);
    }

    [Test]
    public void TryRedirect_ignores_executable_that_is_not_the_configured_git()
    {
        string launcher = FakeInstall();
        ProcessStartInfo info = new() { FileName = launcher };

        GitBinaryResolver.TryRedirect(info, "git-something-else", enabled: true);

        info.FileName.Should().Be(launcher);
    }
}
