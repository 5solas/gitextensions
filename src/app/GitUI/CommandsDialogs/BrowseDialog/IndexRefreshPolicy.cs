// Lock ordering: _lock is a leaf lock, the check whether a refresh is safe is called outside of it.
namespace GitUI.CommandsDialogs.BrowseDialog;

/// <summary>
///  Decides when a status update is allowed to write the refreshed index.
/// </summary>
/// <remarks>
///  Without writing the index, <c>git status</c> has to compare the content of each file whose modification time
///  has changed, again and again.
/// </remarks>
internal sealed class IndexRefreshPolicy(Func<long> getMilliseconds)
{
    internal const int SlowStatusMilliseconds = 150;
    internal const int CooldownMilliseconds = 5 * 60 * 1000;
    internal const int FailureCooldownMilliseconds = 10 * 60 * 1000;

    private readonly Lock _lock = new();
    private string? _workingDir;
    private bool _isSlow;
    private long? _lastRefresh;
    private int _cooldown = CooldownMilliseconds;

    /// <summary>
    ///  Permits one refresh if the previous update without locks was slow, the cooldown has elapsed and it is safe.
    /// </summary>
    public bool TryPermit(string workingDir, Func<bool> isSafe)
    {
        lock (_lock)
        {
            SetWorkingDir(workingDir);
            if (!_isSlow || (_lastRefresh is long lastRefresh && getMilliseconds() - lastRefresh < _cooldown))
            {
                return false;
            }
        }

        if (!isSafe())
        {
            return false;
        }

        StartCooldown(workingDir, CooldownMilliseconds);
        return true;
    }

    /// <summary>
    ///  Starts the cooldown if the index is refreshed for another reason.
    /// </summary>
    public void OnLocksAllowed(string workingDir) => StartCooldown(workingDir, CooldownMilliseconds);

    public void OnRefreshFailed(string workingDir) => StartCooldown(workingDir, FailureCooldownMilliseconds);

    /// <summary>
    ///  Records the duration of an update which did not write the index.
    /// </summary>
    public void OnCompletedWithoutLocks(string workingDir, TimeSpan duration)
    {
        lock (_lock)
        {
            SetWorkingDir(workingDir);
            _isSlow = duration.TotalMilliseconds >= SlowStatusMilliseconds;
        }
    }

    private void StartCooldown(string workingDir, int cooldown)
    {
        lock (_lock)
        {
            SetWorkingDir(workingDir);
            _isSlow = false;
            _lastRefresh = getMilliseconds();
            _cooldown = cooldown;
        }
    }

    private void SetWorkingDir(string workingDir)
    {
        if (_workingDir != workingDir)
        {
            _workingDir = workingDir;
            _isSlow = false;
            _lastRefresh = null;
            _cooldown = CooldownMilliseconds;
        }
    }
}
