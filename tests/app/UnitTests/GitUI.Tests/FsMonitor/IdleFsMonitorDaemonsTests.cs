using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.FsMonitor;
using NSubstitute;

namespace GitUITests.FsMonitor;

public sealed class IdleFsMonitorDaemonsTests
{
    private readonly Dictionary<string, bool?> _enabled = [];
    private readonly HashSet<string> _pipeNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stopped = [];
    private readonly List<Func<Task>> _scheduled = [];
    private readonly object _window = new();
    private bool _isActive;
    private int _pipeEnumerations;
    private Action<string>? _onStop;
    private IdleFsMonitorDaemons _daemons = null!;

    [SetUp]
    public void SetUp()
    {
        _enabled.Clear();
        _pipeNames.Clear();
        _stopped.Clear();
        _scheduled.Clear();
        _isActive = true;
        _pipeEnumerations = 0;
        _onStop = null;
        _daemons = new IdleFsMonitorDaemons
        {
            IsActive = () => _isActive,
            GetMaxDaemons = () => 2,
            IsEnabledInConfig = module => _enabled[Name(module)],
            GetPipeNames = () =>
            {
                ++_pipeEnumerations;
                return _pipeNames;
            },
            Stop = workingDir =>
            {
                _stopped.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDir)));
                _onStop?.Invoke(workingDir);
            },
            RunInBackground = _scheduled.Add,
            Delay = () => Task.CompletedTask
        };
    }

    [Test]
    public async Task Daemons_of_the_least_recently_used_repositories_should_be_stopped()
    {
        Open("a", "b", "c", "d");

        await RunScheduledAsync();

        _stopped.Should().BeEquivalentTo("a", "b");
        _pipeEnumerations.Should().Be(1, "only the last request must be handled");
    }

    [Test]
    public async Task Using_a_repository_again_should_keep_its_daemon()
    {
        Open("a", "b", "c", "A", "d");

        await RunScheduledAsync();

        _stopped.Should().BeEquivalentTo("b", "c");
    }

    [Test]
    public async Task Daemon_should_be_stopped_once()
    {
        Open("a", "b", "c");
        await RunScheduledAsync();
        Open("d", "c");
        await RunScheduledAsync();
        _stopped.Should().BeEquivalentTo("a", "b");

        // a daemon restarted by another application must not be stopped again
        await RunScheduledAsync();
        Open("d");
        await RunScheduledAsync();
        _stopped.Should().HaveCount(2);

        // but it counts again after being opened
        Open("a", "e", "f");
        await RunScheduledAsync();
        _stopped.Should().Contain("c").And.Contain("d");
        _stopped.Count(n => n == "a").Should().Be(2);
    }

    [Test]
    public async Task Nothing_should_be_done_up_to_the_limit()
    {
        Open("a", "b");

        await RunScheduledAsync();

        _pipeEnumerations.Should().Be(0);
        _stopped.Should().BeEmpty();
    }

    [Test]
    public async Task Repositories_without_running_daemon_should_not_count()
    {
        Open("a", "b", "c", "d");
        _pipeNames.Remove(PipeName("c"));

        await RunScheduledAsync();

        _stopped.Should().BeEquivalentTo("a");
    }

    [TestCase(false)]
    [TestCase(null)]
    public async Task Daemon_should_not_be_stopped_if_not_known_to_be_enabled(bool? isEnabled)
    {
        _enabled["a"] = isEnabled;
        Open("a", "b", "c", "d");

        await RunScheduledAsync();

        _stopped.Should().BeEquivalentTo("b");
    }

    [Test]
    public async Task Known_config_should_be_kept_if_the_config_is_invalidated()
    {
        Open("a");
        await RunScheduledAsync();
        _enabled["a"] = null;
        Open("b", "c");

        await RunScheduledAsync();

        _stopped.Should().BeEquivalentTo("a");
    }

    [Test]
    public async Task Daemon_of_open_repository_should_not_be_stopped()
    {
        object otherWindow = new();
        Open(otherWindow, "a");
        Open("b", "c", "d");

        await RunScheduledAsync();
        _stopped.Should().BeEquivalentTo("b");

        _daemons.OnClosed(otherWindow);
        await RunScheduledAsync();
        _stopped.Should().BeEquivalentTo("b", "a");
    }

    [Test]
    public async Task Repository_opened_while_stopping_should_be_kept()
    {
        Open("a", "b", "c", "d");
        _onStop = workingDir => Open(new object(), workingDir.Contains(@"\a") ? "b" : "a");

        await RunScheduledAsync(1);

        _stopped.Should().ContainSingle();
    }

    [Test]
    public async Task Failure_should_not_affect_other_daemons()
    {
        _onStop = _ => throw new InvalidOperationException();
        Open("a", "b", "c", "d");

        await RunScheduledAsync();

        _stopped.Should().BeEquivalentTo("a", "b");
    }

    [Test]
    public async Task Nothing_should_be_done_if_disabled()
    {
        Open("a", "b", "c");
        _isActive = false;
        Open("d", "e");
        _scheduled.Should().HaveCount(3);

        await RunScheduledAsync();

        _stopped.Should().BeEmpty();
        _pipeEnumerations.Should().Be(0);
    }

    [Test]
    public async Task Invalid_working_dir_should_be_ignored()
    {
        IGitModule module = Substitute.For<IGitModule>();
        module.WorkingDir.Returns("");
        module.WorkingDirGitDir.Returns("");
        _daemons.OnOpened(_window, module);
        Open("a", "b", "c");

        await RunScheduledAsync();

        _stopped.Should().BeEquivalentTo("a");
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public async Task Attach_should_track_the_window()
    {
        IGitUICommands commands = Substitute.For<IGitUICommands>();
        IGitUICommandsSource source = Substitute.For<IGitUICommandsSource>();
        source.UICommands.Returns(commands);
        using Control window = new();

        // The commands of a form are set by its constructor, i.e. the event for the first repository has already been raised
        IGitModule firstModule = CreateModule("a");
        commands.Module.Returns(firstModule);
        _daemons.Attach(source, window);
        foreach (string name in new[] { "b", "c" })
        {
            IGitModule module = CreateModule(name);
            commands.Module.Returns(module);
            source.UICommandsChanged += Raise.EventWith(new GitUICommandsChangedEventArgs(oldCommands: null));
        }

        await RunScheduledAsync();
        _stopped.Should().BeEquivalentTo("a");

        window.Dispose();
        Open("d", "e");
        await RunScheduledAsync();
        _stopped.Should().BeEquivalentTo("a", "b", "c");
    }

    [Test]
    public void Settings_should_have_the_expected_defaults()
    {
        AppSettings.StopIdleFsMonitorDaemons.Default.Should().BeTrue();
        AppSettings.MaxFsMonitorDaemons.Default.Should().Be(5);
    }

    private void Open(params string[] names) => Open(_window, names);

    private void Open(object window, params string[] names)
    {
        foreach (string name in names)
        {
            _daemons.OnOpened(window, CreateModule(name));
        }
    }

    private IGitModule CreateModule(string name)
    {
        IGitModule module = Substitute.For<IGitModule>();
        module.WorkingDir.Returns($@"C:\repos\{name}\");
        module.WorkingDirGitDir.Returns($@"C:\repos\{name}\.git\");
        _enabled.TryAdd(Name(module), true);
        _pipeNames.Add(PipeName(name));
        return module;
    }

    private static string Name(IGitModule module) => module.WorkingDir.Split('\\')[2].ToLowerInvariant();

    private static string PipeName(string name) => FsMonitorDaemon.GetPipeName($@"C:\repos\{name}\.git")!;

    private async Task RunScheduledAsync(int max = int.MaxValue)
    {
        for (int i = 0; i < max && _scheduled.Count > 0; ++i)
        {
            Func<Task>[] scheduled = [.. _scheduled];
            _scheduled.Clear();
            foreach (Func<Task> action in scheduled)
            {
                await action();
            }
        }
    }
}
