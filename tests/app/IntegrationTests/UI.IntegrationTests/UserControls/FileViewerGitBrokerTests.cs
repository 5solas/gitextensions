using CommonTestUtils;
using GitCommands;
using GitCommands.Git.Broker;
using GitCommands.Logging;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.Editor;
using GitUI.HelperDialogs;
using NUnit.Framework;

namespace GitExtensions.UITests.UserControls;

/// <summary>
///  Checks the use of the persistent git process by the file viewer and its end before a git command is run in a dialog.
/// </summary>
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class FileViewerGitBrokerTests
{
    private const int TimeoutMilliseconds = 30_000;

    private ReferenceRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new ReferenceRepository();
        GitModule.GitCommandCache.Clear();
        GitBroker.Cache.Clear();

        // Like the start of the application: the first git process must not be started by several threads at the same time
        _repository.Module.GitExecutable.GetOutput("--version");

        // The persistent process is not used before the config has been read by the application
        _repository.Module.GetEffectiveSetting("core.autocrlf");
    }

    [TearDown]
    public void TearDown()
    {
        GitBroker.ReleaseAll();
        GitBroker.Cache.Clear();
        UITest.ProcessUntil("Persistent processes ended", () => GitBrokerStats.RunningProcesses == 0, TimeoutMilliseconds);
        _repository.Dispose();
    }

    [Test]
    public void View_should_not_start_processes_for_the_content()
    {
        string[] files = ["first.txt", "second.txt", "third.txt"];
        foreach (string file in files)
        {
            _repository.CreateCommit($"Add {file}", $"content of {file}", file);
        }

        ObjectId commitId = _repository.Module.GetCurrentCheckout();

        // The attributes are read by git itself while the index has just been changed
        File.SetLastWriteTimeUtc(Path.Combine(_repository.Module.WorkingDirGitDir, "index"), DateTime.UtcNow.AddHours(-1));
        Dictionary<string, string> texts = [];
        List<CommandLogEntry> commands = [];

        RunFileViewer(async fileViewer =>
        {
            foreach (string file in files)
            {
                commands.AddRange(await ViewAsync(fileViewer, file, commitId));
                texts[file] = fileViewer.GetText();
            }
        });

        texts.Should().BeEquivalentTo(files.ToDictionary(f => f, f => $"content of {f}"));
        commands.Should().HaveCount(2)
            .And.ContainSingle(c => c.Arguments.EndsWith("cat-file --batch-command"))
            .And.ContainSingle(c => c.Arguments.Contains("check-attr --stdin"));
        commands.Should().OnlyContain(c => !c.IsOnMainThread);
    }

    [Test]
    public void View_should_show_the_same_text_if_the_broker_is_disabled()
    {
        string? original = AppSettings.GetString(AppSettings.UseGitBroker.FullPath, defaultValue: null);
        _repository.CreateCommit("Add file", "content of file", "file.txt");
        ObjectId commitId = _repository.Module.GetCurrentCheckout();
        string? text = null;
        IReadOnlyList<CommandLogEntry> commands = [];

        try
        {
            AppSettings.UseGitBroker.Value = false;
            RunFileViewer(async fileViewer =>
            {
                commands = await ViewAsync(fileViewer, "file.txt", commitId);
                text = fileViewer.GetText();
            });
        }
        finally
        {
            AppSettings.SetString(AppSettings.UseGitBroker.FullPath, original!);
        }

        text.Should().Be("content of file");
        commands.Select(c => c.Arguments.Split(' ')[0]).Should().BeEquivalentTo("ls-tree", "check-attr", "cat-file");
    }

    [Test]
    public void FormProcess_should_end_the_persistent_processes_before_the_command()
    {
        string commitId = _repository.CreateCommit("Add file", "content of file", "file.txt");
        GitModule module = _repository.Module;
        bool isFound = ThreadHelper.JoinableTaskFactory.Run(() => Task.Run(() => GitBroker.TryGetFileTreeId(module, ObjectId.Parse(commitId), "file.txt", out _, out _, CancellationToken.None)));
        isFound.Should().BeTrue();
        GitBroker.Pool.SessionCount.Should().Be(1);

        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), module);
        using SessionCountingFormProcess form = new(commands, module.WorkingDir);
        Action start = form.Start;

        start.Should().Throw<NotSupportedException>("the command must not be run");
        form.SessionCount.Should().Be(0);
    }

    private static async Task<IReadOnlyList<CommandLogEntry>> ViewAsync(FileViewer fileViewer, string file, ObjectId commitId)
    {
        bool isLoaded = false;
        fileViewer.TextLoaded += OnTextLoaded;
        CommandLog.Clear();
        _ = fileViewer.ViewGitItemAsync(new GitItemStatus(file) { IsTracked = true }, commitId);
        UITest.ProcessUntil($"{file} loaded", () => isLoaded, TimeoutMilliseconds);
        fileViewer.TextLoaded -= OnTextLoaded;
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(TimeoutMilliseconds));
        // The git settings are read once
        return [.. CommandLog.Commands.Where(c => !c.Arguments.StartsWith("config list"))];

        void OnTextLoaded(object? sender, EventArgs e) => isLoaded = true;
    }

    private void RunFileViewer(Func<FileViewer, Task> runTestAsync)
    {
        TestGitUICommandsSource uiCommandsSource = new(new GitUICommands(GlobalServiceContainer.CreateDefaultMockServiceContainer(), _repository.Module));
        using UITestThreadExceptions threadExceptions = new();

        UITest.RunControl(
            createControl: form => new FileViewer { Dock = DockStyle.Fill, Parent = form, UICommandsSource = uiCommandsSource },
            runTestAsync);

        threadExceptions.ThrowIfAny();
    }

    private sealed class SessionCountingFormProcess(IGitUICommands commands, string workingDir)
        : FormProcess(commands, "--version", workingDir, input: null, useDialogSettings: false)
    {
        public int SessionCount { get; private set; } = -1;

        public void Start() => ProcessCallback!(this);

        protected override void BeforeProcessStart()
        {
            SessionCount = GitBroker.Pool.SessionCount;
            throw new NotSupportedException();
        }
    }
}
