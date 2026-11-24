using System.Diagnostics;
using System.Text;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitCommands.Git.Broker;
using GitCommands.Git.Extensions;
using GitCommands.Logging;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUI;
using NSubstitute;

namespace GitCommandsTests.Git.Broker;

/// <summary>
///  Compares the attributes of the persistent process with the ones of a process per request.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class CheckAttrTests
{
    private const string Command = "check-attr --stdin -z diff text crlf eol";
    private const string AttributesFile = ".gitattributes";

    private readonly Dictionary<string, string?> _originalVariables = [];
    private GitBrokerTestScope _scope = null!;

    private string WorkingDir => _scope.Module.WorkingDir;

    [SetUp]
    public void SetUp()
    {
        _scope = new GitBrokerTestScope();

        // Independent of the attributes of the user
        SetVariable("XDG_CONFIG_HOME", Path.Combine(WorkingDir, "..", "xdg"));
        _scope.Commit("file", "text");
        AgeIndex();
        _scope.ReadConfig();
        CommandLog.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        foreach ((string name, string? value) in _originalVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _scope.Dispose();
    }

    [Test]
    public void Setting_should_be_enabled_by_default()
    {
        AppSettings.UseGitBrokerForAttributes.Default.Should().BeTrue();
    }

    [TestCase("a.txt")]
    [TestCase("dir with space/a b.txt")]
    [TestCase("폴더/한글 파일.txt")]
    [TestCase("missing/folder/file.txt")]
    [TestCase("sub/a.txt")]
    [TestCase("sub/deeper/a.txt")]
    [TestCase("sub")]
    [TestCase("a.set")]
    [TestCase("a.unset")]
    [TestCase("a.value")]
    [TestCase("a.binary")]
    [TestCase("[a]*.txt")]
    [TestCase("-dash")]
    [TestCase(".gitattributes")]
    public void Attributes_should_be_the_same_as_without_broker(string path)
    {
        WriteAttributes("", "*.txt text\n*.set diff text crlf eol\n*.unset -diff -text -crlf -eol\n*.value diff=csharp text=auto crlf=input eol=lf\n*.binary binary\n-dash text\n");
        WriteAttributes("sub", "*.txt !text diff=html\n/deeper/a.txt -text\n");
        WriteAttributes("dir with space", "*.txt diff=python\n");
        WriteAttributes("폴더", "*.txt -diff\n");

        AssertSame(path);
        CountStarts().Should().Be(1);
    }

    [Test]
    public void Attributes_of_many_files_should_be_read_by_one_process()
    {
        WriteAttributes("", "*.txt text\n");
        WriteAttributes("sub", "*.txt -text\n");

        for (int i = 0; i < 5; i++)
        {
            Read($"a{i}.txt").Should().Contain("text\0set");
            Read($"sub/a{i}.txt").Should().Contain("text\0unset");
        }

        CommandLog.Commands.Should().ContainSingle().Which.Arguments.Should().EndWith(Command);
    }

    [Test]
    public void Binary_verdict_should_be_the_same_as_without_broker()
    {
        WriteAttributes("", "*.a -diff\n*.b diff=csharp\n*.c text\n*.d -text\n*.e eol=crlf\n*.f diff=unknown\n*.g crlf\n*.h -crlf\n*.i binary\n");

        foreach (string path in "abcdefghi".Select(c => $"file.{c}").Append("file.png").Append("file.txt"))
        {
            AppSettings.UseGitBrokerForAttributes.Value = false;
            bool expected = GitBrokerTestScope.InBackground(() => FileHelper.IsBinaryFileName(_scope.Module, path));
            AppSettings.UseGitBrokerForAttributes.Value = true;
            CommandLog.Clear();

            GitBrokerTestScope.InBackground(() => FileHelper.IsBinaryFileName(_scope.Module, path)).Should().Be(expected, path);

            CommandLog.Commands.Should().NotContain(c => !c.Arguments.EndsWith(Command), "the persistent process must be used");
        }
    }

    [TestCase("")]
    [TestCase("sub")]
    [TestCase("sub/deeper")]
    public void Edited_attributes_of_a_folder_should_be_read_again(string folder)
    {
        WriteAttributes(folder, "*.txt text=old\n");
        Read("sub/deeper/a.txt").Should().Contain("text\0old");

        // The same folder is asked again: git would not notice the change. The same size.
        WriteAttributes(folder, "*.txt text=new\n", age: TimeSpan.FromMinutes(30));
        Read("sub/deeper/a.txt").Should().Contain("text\0new");
        AssertSame("sub/deeper/a.txt");

        File.Delete(Path.Combine(WorkingDir, folder, AttributesFile));
        Read("sub/deeper/a.txt").Should().Contain("text\0unspecified");
        AssertSame("sub/deeper/a.txt");
    }

    [TestCase("")]
    [TestCase("sub")]
    [TestCase("sub/deeper")]
    public void Created_attributes_of_a_folder_should_be_read(string folder)
    {
        Read("sub/deeper/a.txt").Should().Contain("text\0unspecified");

        WriteAttributes(folder, "*.txt text=new\n");

        Read("sub/deeper/a.txt").Should().Contain("text\0new");
        CountStarts().Should().Be(2);
        AssertSame("sub/deeper/a.txt");
    }

    [Test]
    public void Just_edited_attributes_should_be_read_by_git_itself()
    {
        Read("a.txt").Should().Contain("text\0unspecified");

        for (int i = 0; i < 5; i++)
        {
            WriteAttributes("", $"*.txt text=v{i}\n", age: TimeSpan.Zero);
            Read("a.txt").Should().Contain($"text\0v{i}");
        }

        CountStarts().Should().Be(1, "another change within the resolution of the file time would not be noticed");
    }

    [Test]
    public void Edited_info_attributes_should_be_read_again()
    {
        string file = Path.Combine(_scope.Module.WorkingDirGitDir, "info", "attributes");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Read("sub/a.txt").Should().Contain("text\0unspecified");

        WriteAged(file, "*.txt text=old\n");
        Read("sub/a.txt").Should().Contain("text\0old");
        WriteAged(file, "*.txt text=new\n", TimeSpan.FromMinutes(30));
        Read("sub/a.txt").Should().Contain("text\0new");
        CountStarts().Should().Be(3);
    }

    [Test]
    public void Edited_default_global_attributes_should_be_read_again()
    {
        string file = Path.Combine(WorkingDir, "..", "xdg", "git", "attributes");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Read("sub/a.txt").Should().Contain("text\0unspecified");

        WriteAged(file, "*.txt text=old\n");
        Read("sub/a.txt").Should().Contain("text\0old");
        WriteAged(file, "*.txt text=new\n", TimeSpan.FromMinutes(30));
        Read("sub/a.txt").Should().Contain("text\0new");
        CountStarts().Should().Be(3);
    }

    [Test]
    public void Edited_configured_attributes_should_be_read_again([Values] bool isRelative)
    {
        string file = Path.Combine(WorkingDir, "global attributes");
        WriteAged(file, "*.txt text=old\n");
        _scope.Git($"config core.attributesFile \"{(isRelative ? "global attributes" : file.ToPosixPath())}\"");
        _scope.ReadConfig();
        CommandLog.Clear();

        Read("sub/a.txt").Should().Contain("text\0old");
        WriteAged(file, "*.txt text=new\n", TimeSpan.FromMinutes(30));
        Read("sub/a.txt").Should().Contain("text\0new");
        CountStarts().Should().Be(2);
    }

    [Test]
    public void Changed_core_attributesFile_should_be_used_at_once()
    {
        string file = Path.Combine(WorkingDir, "configured");
        WriteAged(file, "*.txt text=new\n");
        Read("a.txt").Should().Contain("text\0unspecified");

        // Outside of the application, which does not know about it yet
        _scope.Git($"config core.attributesFile {file.ToPosixPath().Quote()}");
        Read("a.txt").Should().Contain("text\0new");

        _scope.ReadConfig();
        CommandLog.Clear();
        Read("a.txt").Should().Contain("text\0new");
        CountStarts().Should().Be(1);
    }

    [Test]
    public void Attributes_in_the_index_should_be_used_if_the_file_is_not_checked_out()
    {
        Read("sub/a.txt").Should().Contain("text\0unspecified");

        _scope.AddToIndex("100644", _scope.AddBlob("*.txt text=index\n"u8.ToArray()), $"sub/{AttributesFile}");
        Read("sub/a.txt").Should().Contain("text\0index", "the index has just been changed");
        AgeIndex();
        Read("sub/a.txt").Should().Contain("text\0index");
        AssertSame("sub/a.txt");
        CountStarts().Should().Be(2);
    }

    [Test]
    public void System_attributes_should_be_observed()
    {
        string systemFile = _scope.Git("var GIT_ATTR_SYSTEM");

        IReadOnlyList<string>? paths = AttributeFiles.GetSystemFiles(AppSettings.GitCommand);

        paths.Should().NotBeNull();
        paths!.Any(path => Path.GetFullPath(path).Equals(Path.GetFullPath(systemFile), StringComparison.OrdinalIgnoreCase) || IsSameFile(path, systemFile))
            .Should().BeTrue();

        static bool IsSameFile(string path, string other)
            => File.Exists(path) && File.Exists(other) && path.EndsWith(@"etc\gitattributes")
                && File.ReadAllText(path) == File.ReadAllText(other) && File.GetLastWriteTimeUtc(path) == File.GetLastWriteTimeUtc(other);
    }

    [Test]
    public void System_attributes_should_be_unknown_for_an_unknown_installation()
    {
        AttributeFiles.GetSystemFiles(Path.Combine(WorkingDir, "git.exe")).Should().BeNull();
        AttributeFiles.GetSystemFiles("not-existing-git").Should().BeNull();
    }

    [TestCase("core.attributesFile", "~user/attributes")]
    [TestCase("core.attributesFile", "%(prefix)/attributes")]
    [TestCase("core.attributesFile", "/etc/attributes")]
    [TestCase("core.attributesFile", ":(optional)attributes")]
    [TestCase("attr.tree", "HEAD")]
    [TestCase("core.worktree", "../other")]
    [TestCase("core.bare", "true")]
    public void Unsupported_config_should_be_left_to_git(string key, string value)
    {
        Read("a.txt");
        CountStarts().Should().Be(1);
        Directory.CreateDirectory(Path.Combine(WorkingDir, "other"));
        File.AppendAllText(Path.Combine(_scope.Module.WorkingDirGitDir, "config"), $"\n[{key.Split('.')[0]}]\n\t{key.Split('.')[1]} = {value}\n");
        _scope.ReadConfig();
        CommandLog.Clear();

        GitBrokerTestScope.InBackground(() => GitBroker.WithAttributes(_scope.Module, "a.txt")).Should().BeSameAs(_scope.Module);

        CountStarts().Should().Be(0);
    }

    [TestCase("GIT_ATTR_SOURCE", "HEAD")]
    [TestCase("GIT_INDEX_FILE", "other")]
    public void Unsupported_environment_should_be_left_to_git(string name, string value)
    {
        Read("a.txt");
        CountStarts().Should().Be(1);
        SetVariable(name, value);

        // Also if the config has been read with this environment
        for (int i = 0; i < 2; i++)
        {
            CommandLog.Clear();
            GitBrokerTestScope.InBackground(() => GitBroker.WithAttributes(_scope.Module, "a.txt")).Should().BeSameAs(_scope.Module);
            CountStarts().Should().Be(0);
            _scope.ReadConfig();
        }
    }

    [Test]
    public void Config_which_cannot_be_verified_should_be_left_to_git()
    {
        string? original = AppSettings.GetString(AppSettings.RepoSnapshotMemoryLimit.FullPath, defaultValue: null);
        try
        {
            AppSettings.RepoSnapshotMemoryLimit.Value = 0;
            _scope.ReadConfig();

            GitBrokerTestScope.InBackground(() => GitBroker.WithAttributes(_scope.Module, "a.txt")).Should().BeSameAs(_scope.Module);
        }
        finally
        {
            AppSettings.SetString(AppSettings.RepoSnapshotMemoryLimit.FullPath, original!);
        }
    }

    [TestCase("../a.txt")]
    [TestCase("./a.txt")]
    [TestCase("sub//a.txt")]
    [TestCase("sub/")]
    [TestCase("sub\\a.txt")]
    [TestCase("a\nb")]
    [TestCase("a\"b")]
    [TestCase("C:/a.txt")]
    [TestCase("/a.txt")]
    [TestCase("sub /a.txt")]
    [TestCase("sub./a.txt")]
    public void Special_paths_should_be_left_to_git(string path)
    {
        Read("a.txt");
        CountStarts().Should().Be(1);

        GitBrokerTestScope.InBackground(() => GitBroker.WithAttributes(_scope.Module, path)).Should().BeSameAs(_scope.Module);

        Read("a.txt");
        CountStarts().Should().Be(1, "the process must not be affected");
    }

    [Test]
    public void Invalid_unicode_should_be_left_to_git()
    {
        // Not possible as an argument of an attribute
        Special_paths_should_be_left_to_git($"a{(char)0xD800}b");
    }

    [Test]
    public void Disabled_broker_should_not_be_used([Values] bool disableAll)
    {
        (disableAll ? AppSettings.UseGitBroker : AppSettings.UseGitBrokerForAttributes).Value = false;

        GitBroker.WithAttributes(_scope.Module, "a.txt").Should().BeSameAs(_scope.Module);
        Read("a.txt").Should().Contain("text\0unspecified");

        CountStarts().Should().Be(0);
    }

    [Test]
    public void Mocked_module_should_not_be_used()
    {
        IGitModule module = Substitute.For<IGitModule>();

        GitBrokerTestScope.InBackground(() => GitBroker.WithAttributes(module, "a.txt")).Should().BeSameAs(module);
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void Process_should_not_be_used_on_the_UI_thread()
    {
        ThreadHelper.JoinableTaskContext.IsOnMainThread.Should().BeTrue();

        GitBroker.WithAttributes(_scope.Module, "a.txt").Should().BeSameAs(_scope.Module);

        CountStarts().Should().Be(0);
        Read("a.txt");
        CountStarts().Should().Be(1);
    }

    [Test]
    public void Process_should_be_replaced_if_killed()
    {
        Read("a.txt");
        CommandLogEntry command = CommandLog.Commands.Single(c => c.Arguments.EndsWith(Command));
        using (Process process = Process.GetProcessById(command.ProcessId!.Value))
        {
            IndexRefreshGate.IsPersistentProcess(process.TryGetCommandLine()).Should().BeTrue();
            process.Kill();
            process.WaitForExit();
        }

        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process has been killed");

        AssertSame("a.txt");
        CountStarts().Should().Be(2);
    }

    [Test]
    public void Process_should_not_write_the_index()
    {
        // The modification time of the file in the index is outdated: a command which refreshes the index would write it
        File.WriteAllText(Path.Combine(WorkingDir, "file"), "text");
        string index = Path.Combine(_scope.Module.WorkingDirGitDir, "index");
        byte[] content = File.ReadAllBytes(index);
        DateTime time = File.GetLastWriteTimeUtc(index);

        Read("file");
        CountStarts().Should().Be(1);
        GitBroker.ReleaseAll();
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process must end");

        File.GetLastWriteTimeUtc(index).Should().Be(time);
        File.ReadAllBytes(index).Should().Equal(content);
        File.Exists($"{index}.lock").Should().BeFalse();
    }

    [Test]
    public void Process_should_not_run_the_fsmonitor_hook()
    {
        string marker = _scope.SetFsMonitorHook();
        AgeIndex();
        _scope.ReadConfig();
        CommandLog.Clear();

        Read("file").Should().Contain("text\0unspecified");
        CountStarts().Should().Be(1);

        File.Exists(marker).Should().BeFalse();
    }

    [Test]
    public void Both_processes_should_be_ended_together()
    {
        Read("a.txt");
        GitBrokerTestScope.InBackground(() => GitBroker.Pool.TryRequest(_scope.GitExecutable, (session, ct) => session.GetInfo("HEAD:file", ct)?.Type, CancellationToken.None, out string? type) ? type : null)
            .Should().Be("blob");
        GitBrokerStats.RunningProcesses.Should().Be(2);

        GitBroker.ReleaseAll();

        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the processes must end");
    }

    [Test]
    public void Idle_process_should_release_the_working_directory_soon()
    {
        Read("a.txt");
        GitBrokerStats.RunningProcesses.Should().Be(1);

        // The current directory of the process is the working directory, which cannot be moved or deleted meanwhile
        Stopwatch stopwatch = Stopwatch.StartNew();
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process must end when idle");

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        string workingDir = WorkingDir.TrimEnd('\\');
        Directory.Move(workingDir, $"{workingDir}-moved");
        Directory.Move($"{workingDir}-moved", workingDir);
    }

    [Test]
    public void Worktree_should_use_the_info_attributes_of_the_repository()
    {
        string file = Path.Combine(_scope.Module.WorkingDirGitDir, "info", "attributes");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string worktree = $"{WorkingDir.TrimEnd('\\')}-worktree";
        _scope.Git($"worktree add --detach \"{worktree}\" HEAD");
        GitModule module = new(new GitExecutorProvider(new GitDirectoryResolver()), worktree);
        File.SetLastWriteTimeUtc(Path.Combine(module.WorkingDirGitDir, "index"), DateTime.UtcNow.AddHours(-1));
        module.GetEffectiveSetting("core.autocrlf");
        CommandLog.Clear();
        Read("a.txt", module).Should().Contain("text\0unspecified");

        WriteAged(file, "*.txt text=info\n");

        Read("a.txt", module).Should().Contain("text\0info");
        CountStarts().Should().Be(2);
        GC.KeepAlive(module);
    }

    [Test]
    public void Unexpected_response_should_not_be_used()
    {
        string[] responses =
        [
            "other\0diff\0set\0other\0text\0set\0other\0crlf\0set\0other\0eol\0set\0",
            "a\0diff\0set\0a\0text\0set\0a\0eol\0set\0a\0crlf\0set\0",
            "a\0diff\0set\0",
            "",
        ];

        foreach (string response in responses)
        {
            using CheckAttrSession session = new(new CannedProcess(Encoding.UTF8.GetBytes(response)), AttributeFiles.CreateForTest(WorkingDir));
            Action request = () => session.Query("a", CancellationToken.None);
            request.Should().Throw<GitBrokerException>();
            session.IsStopped.Should().BeTrue();
        }
    }

    private static void WriteAged(string file, string content, TimeSpan? age = null)
    {
        File.WriteAllText(file, content);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - (age ?? TimeSpan.FromHours(1)));
    }

    private static int CountStarts() => CommandLog.Commands.Count(c => c.Arguments.EndsWith(Command));

    private static string ReadWithGit(IGitModule module, string path)
    {
        GitArgumentBuilder args = new("check-attr") { "-z", "diff", "text", "crlf", "eol", "--", path.Quote() };
        return module.GitExecutable.Execute(args, throwOnErrorExit: false).StandardOutput;
    }

    private void SetVariable(string name, string value)
    {
        _originalVariables.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private void AgeIndex() => File.SetLastWriteTimeUtc(Path.Combine(_scope.Module.WorkingDirGitDir, "index"), DateTime.UtcNow.AddHours(-1));

    private void WriteAttributes(string folder, string content, TimeSpan? age = null)
    {
        Directory.CreateDirectory(Path.Combine(WorkingDir, folder));
        WriteAged(Path.Combine(WorkingDir, folder, AttributesFile), content, age);
    }

    private string Read(string path, IGitModule? module = null)
        => GitBrokerTestScope.InBackground(() => ReadWithGit(GitBroker.WithAttributes(module ?? _scope.Module, path), path));

    private void AssertSame(string path)
    {
        string expected = ReadWithGit(_scope.Module, path);
        IGitModule module = GitBrokerTestScope.InBackground(() => GitBroker.WithAttributes(_scope.Module, path));
        module.Should().NotBeSameAs(_scope.Module, "the persistent process must be used");
        ReadWithGit(module, path).Should().Be(expected);
        expected.Should().NotBeEmpty();
    }
}
