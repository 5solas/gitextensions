using System.Diagnostics;
using GitCommands;

namespace GitCommandsTests.Git;

[TestFixture]
public sealed class GitBinaryResolverTests
{
    private string _root = null!;
    private string? _originalHome;
    private string? _originalMsystem;
    private long _now;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        GitBinaryResolver.ClearCache();

        // A HOME without "bin", the redirection is not used if "~/bin" exists
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        Directory.CreateDirectory(Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("HOME", Path.Combine(_root, "home"));

        // The redirection is not used if MSYSTEM is set (e.g. the tests are run in Git Bash)
        _originalMsystem = Environment.GetEnvironmentVariable("MSYSTEM");
        Environment.SetEnvironmentVariable("MSYSTEM", null);

        _now = 1_000_000;
        GitBinaryResolver.GetTickCount = () => _now;
    }

    [TearDown]
    public void TearDown()
    {
        GitBinaryResolver.GetTickCount = () => Environment.TickCount64;
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Environment.SetEnvironmentVariable("MSYSTEM", _originalMsystem);
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
    public void Resolve_returns_null_if_several_real_binaries_exist()
    {
        // The launcher starts one of them, which one is not known
        string launcher = FakeInstall();
        Touch("mingw64", "bin", "git.exe");

        GitBinaryResolver.Resolve(launcher).Should().BeNull();
    }

    [Test]
    public void Resolve_returns_null_if_home_bin_exists()
    {
        // The real binary puts "~/bin" in front of its own folders in PATH, the launcher behind them
        string launcher = FakeInstall();
        Directory.CreateDirectory(Path.Combine(_root, "home", "bin"));

        GitBinaryResolver.Resolve(launcher).Should().BeNull();
    }

    [Test]
    public void Resolve_uses_userprofile_if_home_is_not_set()
    {
        string launcher = FakeInstall();
        string? originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable("HOME", null);
            Environment.SetEnvironmentVariable("USERPROFILE", Path.Combine(_root, "home"));
            Directory.CreateDirectory(Path.Combine(_root, "home", "bin"));

            GitBinaryResolver.Resolve(launcher).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
        }
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

        // Not checked again within the interval
        File.Delete(expected);
        _now += 29_000;

        GitBinaryResolver.Resolve(launcher).Should().Be(expected);
    }

    [Test]
    public void Resolve_drops_a_real_binary_which_disappeared()
    {
        string launcher = FakeInstall();
        string expected = Path.Combine(_root, "ucrt64", "bin", "git.exe");
        GitBinaryResolver.Resolve(launcher).Should().Be(expected);

        File.Delete(expected);
        _now += 31_000;

        GitBinaryResolver.Resolve(launcher).Should().BeNull();

        // The negative result is resolved again after the same interval
        Touch("mingw64", "bin", "git.exe");
        _now += 29_000;
        GitBinaryResolver.Resolve(launcher).Should().BeNull();

        _now += 2_000;
        GitBinaryResolver.Resolve(launcher).Should().Be(Path.Combine(_root, "mingw64", "bin", "git.exe"));
    }

    [TestCase("MINGW64")]
    [TestCase("UCRT64")]
    public void Resolve_returns_null_if_msystem_is_set(string msystem)
    {
        // The real binary does not set up PATH then: sh hooks fail with "cannot spawn"
        string launcher = FakeInstall();
        Environment.SetEnvironmentVariable("MSYSTEM", msystem);

        GitBinaryResolver.Resolve(launcher).Should().BeNull();
    }

    [Test]
    public void Resolve_rechecks_msystem_after_the_interval()
    {
        string launcher = FakeInstall();
        string expected = Path.Combine(_root, "ucrt64", "bin", "git.exe");
        GitBinaryResolver.Resolve(launcher).Should().Be(expected);

        Environment.SetEnvironmentVariable("MSYSTEM", "UCRT64");
        _now += 31_000;
        GitBinaryResolver.Resolve(launcher).Should().BeNull();

        Environment.SetEnvironmentVariable("MSYSTEM", null);
        _now += 31_000;
        GitBinaryResolver.Resolve(launcher).Should().Be(expected);
    }

    [Test]
    public void Resolve_applies_all_rules_after_the_interval()
    {
        string launcher = FakeInstall();
        GitBinaryResolver.Resolve(launcher).Should().NotBeNull();

        // A second layout was installed: which binary the launcher starts is not known
        Touch("mingw64", "bin", "git.exe");
        _now += 31_000;

        GitBinaryResolver.Resolve(launcher).Should().BeNull();
    }

    [Test]
    public void Resolve_drops_the_real_binary_if_home_bin_was_created()
    {
        string launcher = FakeInstall();
        GitBinaryResolver.Resolve(launcher).Should().NotBeNull();

        Directory.CreateDirectory(Path.Combine(_root, "home", "bin"));
        _now += 31_000;

        GitBinaryResolver.Resolve(launcher).Should().BeNull();
    }

    [Test]
    public void Resolver_is_not_public_api()
    {
        typeof(GitBinaryResolver).IsPublic.Should().BeFalse();
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
