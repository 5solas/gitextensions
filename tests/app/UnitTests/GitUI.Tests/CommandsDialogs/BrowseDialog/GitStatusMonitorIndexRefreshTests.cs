using System.Text;
using CommonTestUtils;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.CommandsDialogs.BrowseDialog;
using NSubstitute;

namespace GitUITests.CommandsDialogs.BrowseDialog;

[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class GitStatusMonitorIndexRefreshTests
{
    private const int TimeoutMilliseconds = 10_000;
    private const string NoLocks = "--no-optional-locks";

    private string _workingDir = null!;
    private FakeExecutable _executable = null!;
    private IGitModule _module = null!;
    private IGitUICommands _commands = null!;
    private IndexRefreshPolicy _policy = null!;
    private bool _isSafe;
    private bool _safetyCheckedOnUIThread;
    private GitStatusMonitor _monitor = null!;
    private int _updateCount;

    [SetUp]
    public void SetUp()
    {
        _workingDir = Path.Combine(Path.GetTempPath(), $"ge-monitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workingDir, ".git"));
        _executable = new FakeExecutable();
        _module = Substitute.For<IGitModule>();
        _module.WorkingDir.Returns(_workingDir + Path.DirectorySeparatorChar);
        _module.WorkingDirGitDir.Returns(Path.Combine(_workingDir, ".git") + Path.DirectorySeparatorChar);
        _module.GitExecutable.Returns(_executable);
        _commands = Substitute.For<IGitUICommands>();
        _commands.Module.Returns(_module);
        IGitUICommandsSource source = Substitute.For<IGitUICommandsSource>();
        source.UICommands.Returns(_commands);

        _isSafe = true;
        _safetyCheckedOnUIThread = false;
        _updateCount = 0;
        _policy = new IndexRefreshPolicy(() => Environment.TickCount64);
        _monitor = new GitStatusMonitor(source, () => false)
        {
            IndexRefreshPolicy = _policy,
            IsIndexRefreshSafe = _ =>
            {
                _safetyCheckedOnUIThread = ThreadHelper.JoinableTaskContext.IsOnMainThread;
                return _isSafe;
            }
        };
        _monitor.GitWorkingDirectoryStatusChanged += (s, e) => _updateCount += e is null ? 0 : 1;
    }

    [TearDown]
    public void TearDown()
    {
        _monitor.Dispose();
        Directory.Delete(_workingDir, recursive: true);
    }

    [Test]
    public void Update_should_refresh_index_after_slow_status()
    {
        _policy.OnCompletedWithoutLocks(_module.WorkingDir, TimeSpan.FromMilliseconds(150));

        Update(expectedCount: 1);
        Update(expectedCount: 2);

        _executable.NoLocks.Should().Equal(false, true);
        _safetyCheckedOnUIThread.Should().BeFalse();
    }

    [Test]
    public void Update_should_measure_the_status()
    {
        _executable.Delay = 200;
        Update(expectedCount: 1);
        _executable.Delay = 0;
        Update(expectedCount: 2);
        Update(expectedCount: 3);

        _executable.NoLocks.Should().Equal(true, false, true);
    }

    [Test]
    public void Update_should_not_refresh_index_if_not_safe()
    {
        _isSafe = false;
        _policy.OnCompletedWithoutLocks(_module.WorkingDir, TimeSpan.FromSeconds(1));

        Update(expectedCount: 1);

        _executable.NoLocks.Should().Equal(true);
    }

    [Test]
    public void Update_should_not_refresh_index_if_disabled_by_setting()
    {
        AppSettings.RefreshIndexAfterSlowStatus.Default.Should().BeTrue();
        string? original = AppSettings.GetString(AppSettings.RefreshIndexAfterSlowStatus.FullPath, defaultValue: null);
        try
        {
            AppSettings.RefreshIndexAfterSlowStatus.Value = false;
            _policy.OnCompletedWithoutLocks(_module.WorkingDir, TimeSpan.FromSeconds(1));
            Update(expectedCount: 1);

            // The slow update is remembered
            _executable.Delay = 200;
            Update(expectedCount: 2);
            AppSettings.RefreshIndexAfterSlowStatus.Value = true;
            Update(expectedCount: 3);
        }
        finally
        {
            AppSettings.SetString(AppSettings.RefreshIndexAfterSlowStatus.FullPath, original!);
        }

        _executable.NoLocks.Should().Equal(true, true, false);
    }

    [Test]
    public void Update_should_not_refresh_index_while_repository_is_modified_by_GE()
    {
        Update(expectedCount: 1);
        _policy.OnCompletedWithoutLocks(_module.WorkingDir, TimeSpan.FromSeconds(1));
        _commands.RepoChangedNotifier.IsLocked.Returns(true);
        _monitor.RequestRefresh();
        WinFormsTestHelper.ProcessEventsFor(500);

        _executable.NoLocks.Should().Equal(true);
    }

    [Test]
    public void Update_should_fall_back_without_locks_if_refresh_fails()
    {
        _executable.FailWithLocks = true;
        _policy.OnCompletedWithoutLocks(_module.WorkingDir, TimeSpan.FromSeconds(1));

        Update(expectedCount: 1);

        _executable.NoLocks.Should().Equal(false, true);
        _monitor.Active.Should().BeTrue();
    }

    [Test]
    public void Failed_refresh_should_not_count_as_error()
    {
        // The monitor is stopped by the third error in a row: one by the refresh must not count
        _executable.FailWithLocks = true;
        _executable.FailuresWithoutLocks = 2;
        _policy.OnCompletedWithoutLocks(_module.WorkingDir, TimeSpan.FromSeconds(1));

        Update(expectedCount: 1);

        _executable.NoLocks.Should().Equal(false, true, true, true);
        _monitor.Active.Should().BeTrue();
    }

    [Test]
    public void Refresh_should_not_be_killed()
    {
        _executable.Delay = 1000;
        _policy.OnCompletedWithoutLocks(_module.WorkingDir, TimeSpan.FromSeconds(1));
        _monitor.RequestRefresh();
        WinFormsTestHelper.ProcessUntil("Command started", () => _executable.NoLocks.Length == 1, TimeoutMilliseconds);
        _executable.Delay = 0;

        Update(expectedCount: 1);

        _executable.Tokens[0].CanBeCanceled.Should().BeFalse("killing git would leave index.lock");
        _executable.Tokens[1].CanBeCanceled.Should().BeTrue();
        _executable.NoLocks.Should().Equal(false, true);
        WinFormsTestHelper.ProcessEventsFor(1200);
        _updateCount.Should().Be(1, "the result of the superseded command must be dropped");
    }

    [Test]
    public void GetUpdateState_should_report_expected_updates()
    {
        _executable.Delay = 500;
        _monitor.GetUpdateState(_module, 1500).Should().Be(StatusUpdateState.Scheduled);
        _monitor.GetUpdateState(Substitute.For<IGitModule>(), 1500).Should().Be(StatusUpdateState.None);

        WinFormsTestHelper.ProcessUntil("Command started", () => _executable.NoLocks.Length == 1, TimeoutMilliseconds);
        _monitor.GetUpdateState(_module, 1500).Should().Be(StatusUpdateState.Running);

        WinFormsTestHelper.ProcessUntil("Command ended", () => _updateCount == 1, TimeoutMilliseconds);
        WinFormsTestHelper.ProcessEventsFor(100);
        _monitor.GetUpdateState(_module, 1500).Should().Be(StatusUpdateState.None);

        _monitor.RequestRefresh();
        _monitor.GetUpdateState(_module, 1500).Should().Be(StatusUpdateState.Scheduled);
        _monitor.Active = false;
        _monitor.GetUpdateState(_module, 1500).Should().Be(StatusUpdateState.None);
    }

    private void Update(int expectedCount)
    {
        _monitor.RequestRefresh();
        WinFormsTestHelper.ProcessUntil("Status updated", () => _updateCount >= expectedCount, TimeoutMilliseconds);

        // The command is marked as completed after the event
        WinFormsTestHelper.ProcessEventsFor(50);
    }

    private sealed class FakeExecutable : IExecutable
    {
        private readonly List<(bool NoLocks, CancellationToken Token)> _calls = [];

        public int Delay { get; set; }
        public bool FailWithLocks { get; set; }
        public int FailuresWithoutLocks { get; set; }

        public bool[] NoLocks => [.. Calls.Select(c => c.NoLocks)];
        public CancellationToken[] Tokens => [.. Calls.Select(c => c.Token)];

        public string Command => "git.exe";
        public string WorkingDir => ".";
        public string PrefixArguments => "";

        private (bool NoLocks, CancellationToken Token)[] Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public bool Exists() => true;

        public IProcess Start(ArgumentString arguments, bool createWindow, bool redirectInput, bool redirectOutput, Encoding? outputEncoding, bool useShellExecute = false, bool throwOnErrorExit = true, CancellationToken cancellationToken = default)
        {
            bool noLocks = arguments.ToString().Contains(GitStatusMonitorIndexRefreshTests.NoLocks);
            lock (_calls)
            {
                _calls.Add((noLocks, cancellationToken));
            }

            if (noLocks ? FailuresWithoutLocks-- > 0 : FailWithLocks)
            {
                throw new InvalidOperationException("Unable to create index.lock");
            }

            return new FakeProcess(Delay);
        }
    }

    private sealed class FakeProcess(int delay) : IProcess
    {
        public StreamWriter StandardInput { get; } = new(new MemoryStream());
        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public string StandardError => "";

        public void Dispose()
        {
        }

        public void Kill(bool entireProcessTree)
        {
        }

        public int WaitForExit() => throw new NotSupportedException();

        public Task<int> WaitForExitAsync() => WaitForExitAsync(CancellationToken.None);

        public async Task<int> WaitForExitAsync(CancellationToken token)
        {
            await Task.Delay(delay, token);
            return 0;
        }

        public void WaitForInputIdle()
        {
        }
    }
}
