using GitUI.CommandsDialogs.BrowseDialog;

namespace GitUITests.CommandsDialogs.BrowseDialog;

public sealed class IndexRefreshPolicyTests
{
    private const string Repo = @"C:\repo\";
    private static readonly TimeSpan _fast = TimeSpan.FromMilliseconds(149);
    private static readonly TimeSpan _slow = TimeSpan.FromMilliseconds(150);

    private long _now;
    private IndexRefreshPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        _now = 1_000_000;
        _policy = new IndexRefreshPolicy(() => _now);
    }

    [Test]
    public void TryPermit_should_permit_one_refresh_after_slow_status()
    {
        _policy.TryPermit(Repo, () => true).Should().BeFalse();

        _policy.OnCompletedWithoutLocks(Repo, _slow);
        _policy.TryPermit(Repo, () => true).Should().BeTrue();
        _policy.TryPermit(Repo, () => true).Should().BeFalse();
    }

    [Test]
    public void TryPermit_should_consider_the_latest_status_only()
    {
        _policy.OnCompletedWithoutLocks(Repo, _slow);
        _policy.OnCompletedWithoutLocks(Repo, _fast);

        _policy.TryPermit(Repo, () => true).Should().BeFalse();
    }

    [Test]
    public void TryPermit_should_retry_if_it_was_not_safe()
    {
        _policy.OnCompletedWithoutLocks(Repo, _slow);

        _policy.TryPermit(Repo, () => false).Should().BeFalse();
        _policy.TryPermit(Repo, () => true).Should().BeTrue();
    }

    [Test]
    public void TryPermit_should_not_check_the_safety_without_need()
    {
        _policy.OnCompletedWithoutLocks(Repo, _fast);

        _policy.TryPermit(Repo, () => throw new InvalidOperationException()).Should().BeFalse();
    }

    [TestCase(false, 300_000)]
    [TestCase(true, 600_000)]
    public void TryPermit_should_respect_cooldown(bool hasFailed, int cooldown)
    {
        _policy.OnCompletedWithoutLocks(Repo, _slow);
        _policy.TryPermit(Repo, () => true).Should().BeTrue();
        if (hasFailed)
        {
            _policy.OnRefreshFailed(Repo);
        }

        _policy.OnCompletedWithoutLocks(Repo, _slow);

        _now += cooldown - 1;
        _policy.TryPermit(Repo, () => true).Should().BeFalse();
        _now++;
        _policy.TryPermit(Repo, () => true).Should().BeTrue();
    }

    [Test]
    public void TryPermit_should_respect_cooldown_after_other_refresh()
    {
        _policy.OnLocksAllowed(Repo);
        _policy.OnCompletedWithoutLocks(Repo, _slow);

        _policy.TryPermit(Repo, () => true).Should().BeFalse();
    }

    [Test]
    public void State_should_not_be_shared_by_repositories()
    {
        _policy.OnCompletedWithoutLocks(Repo, _slow);
        _policy.TryPermit(@"C:\other\", () => true).Should().BeFalse();

        _policy.OnLocksAllowed(Repo);
        _policy.OnCompletedWithoutLocks(@"C:\other\", _slow);
        _policy.TryPermit(@"C:\other\", () => true).Should().BeTrue();
    }
}
