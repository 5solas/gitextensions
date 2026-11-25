// Lock ordering: _lock is a leaf lock, git and the file system are not accessed while it is held.
using System.Diagnostics;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;

namespace GitUI.FsMonitor;

/// <summary>
///  Stops the fsmonitor daemons of repositories which have been opened by this process but have not been used recently.
/// </summary>
/// <remarks>
///  Git starts a daemon again when it is needed, e.g. by another application which uses the repository.
/// </remarks>
internal sealed class IdleFsMonitorDaemons
{
    /// <summary>Opening a repository must not be delayed, and repositories might be switched quickly.</summary>
    private static readonly TimeSpan _delay = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();

    // The open repositories by window
    private readonly Dictionary<object, IGitModule> _open = [];

    // The most recently used repository is the first one
    private readonly LinkedList<Repository> _recent = [];
    private int _generation;

    public static IdleFsMonitorDaemons Default { get; } = new();

    internal Func<bool> IsActive { get; init; } = () => AppSettings.StopIdleFsMonitorDaemons.Value;
    internal Func<int> GetMaxDaemons { get; init; } = () => AppSettings.MaxFsMonitorDaemons.Value;
    internal Func<IGitModule, bool?> IsEnabledInConfig { get; init; } = FsMonitorDaemon.IsEnabled;
    internal Func<IReadOnlySet<string>> GetPipeNames { get; init; } = FsMonitorDaemon.GetPipeNames;
    internal Action<string> Stop { get; init; } = FsMonitorDaemon.Stop;

    // Do not use a tracked task: closing the application must not wait for it
    internal Action<Func<Task>> RunInBackground { get; init; } = static action => _ = Task.Run(action);
    internal Func<Task> Delay { get; init; } = static () => Task.Delay(_delay);

    /// <summary>
    ///  Tracks the repository which is open in the window.
    /// </summary>
    public void Attach(IGitUICommandsSource commandsSource, Control window)
    {
        commandsSource.UICommandsChanged += (_, _) => OnOpened(window, commandsSource.UICommands.Module);
        window.Disposed += (_, _) => OnClosed(window);
    }

    internal void OnOpened(object window, IGitModule module)
    {
        OnClosing(window);

        string workingDir = module.WorkingDir;
        if (!string.IsNullOrEmpty(workingDir) && FsMonitorDaemon.GetPipeName(module.WorkingDirGitDir) is string pipeName)
        {
            lock (_lock)
            {
                _open[window] = module;
                Repository? repository = Find(pipeName);
                if (repository is not null)
                {
                    _recent.Remove(repository);
                }

                _recent.AddFirst(repository ?? new Repository(workingDir, pipeName));
            }
        }

        Schedule();
    }

    internal void OnClosed(object window)
    {
        OnClosing(window);
        Schedule();
    }

    /// <summary>
    ///  Stops the daemons, must be called in the background.
    /// </summary>
    internal void StopIdleDaemons()
    {
        IGitModule[] openModules;
        lock (_lock)
        {
            openModules = [.. _open.Values];
        }

        foreach (IGitModule module in openModules)
        {
            UpdateIsEnabled(module);
        }

        int maxDaemons = Math.Max(0, GetMaxDaemons());
        Repository[] repositories;
        lock (_lock)
        {
            repositories = [.. _recent];
        }

        if (repositories.Length <= maxDaemons)
        {
            return;
        }

        IReadOnlySet<string> pipeNames = GetPipeNames();
        foreach (Repository repository in repositories.Where(r => pipeNames.Contains(r.PipeName)).Skip(maxDaemons))
        {
            lock (_lock)
            {
                // It may have been opened again in the meantime
                if (repository.IsEnabled is not true
                    || _recent.TakeWhile(r => r != repository).Count() < maxDaemons
                    || _open.Values.Any(module => IsSame(module, repository)))
                {
                    continue;
                }

                _recent.Remove(repository);
            }

            try
            {
                Stop(repository.WorkingDir);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"{nameof(IdleFsMonitorDaemons)}: The daemon of {repository.WorkingDir} cannot be stopped: {ex.Message}");
            }
        }
    }

    private void OnClosing(object window)
    {
        IGitModule? module;
        lock (_lock)
        {
            if (!_open.Remove(window, out module))
            {
                return;
            }
        }

        // The config has been read while the repository was open
        UpdateIsEnabled(module);
    }

    private void Schedule()
    {
        if (!IsActive())
        {
            return;
        }

        int generation = Interlocked.Increment(ref _generation);
        RunInBackground(async () =>
        {
            await Delay();
            if (generation == Volatile.Read(ref _generation) && IsActive())
            {
                StopIdleDaemons();
            }
        });
    }

    private void UpdateIsEnabled(IGitModule module)
    {
        if (IsEnabledInConfig(module) is not bool isEnabled || FsMonitorDaemon.GetPipeName(module.WorkingDirGitDir) is not string pipeName)
        {
            return;
        }

        lock (_lock)
        {
            if (Find(pipeName) is Repository repository)
            {
                repository.IsEnabled = isEnabled;
            }
        }
    }

    private Repository? Find(string pipeName)
        => _recent.FirstOrDefault(r => string.Equals(r.PipeName, pipeName, StringComparison.OrdinalIgnoreCase));

    private static bool IsSame(IGitModule module, Repository repository)
        => string.Equals(FsMonitorDaemon.GetPipeName(module.WorkingDirGitDir), repository.PipeName, StringComparison.OrdinalIgnoreCase);

    private sealed class Repository(string workingDir, string pipeName)
    {
        public string WorkingDir { get; } = workingDir;

        public string PipeName { get; } = pipeName;

        /// <summary>
        ///  Whether the daemon is enabled in the config, <see langword="null"/> if unknown.
        /// </summary>
        public bool? IsEnabled { get; set; }
    }
}
