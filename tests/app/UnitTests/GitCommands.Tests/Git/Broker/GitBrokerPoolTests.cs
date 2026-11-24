using System.Diagnostics;
using GitCommands;
using GitCommands.Git.Broker;
using GitCommands.Logging;
using GitExtensions.Extensibility.Git;
using GitUI;

namespace GitCommandsTests.Git.Broker;

[TestFixture]
[NonParallelizable]
public sealed class GitBrokerPoolTests
{
    private const string CatFileCommand = "cat-file --batch-command";

    private GitBrokerTestScope _scope = null!;
    private GitBrokerPool _pool = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = new GitBrokerTestScope();
        _scope.Commit("file", "text");
        _pool = new GitBrokerPool(TimeSpan.FromMinutes(1), maxSessions: 2);
    }

    [TearDown]
    public void TearDown()
    {
        _pool.ReleaseAll();
        _scope.Dispose();
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "no process may remain");
    }

    [Test]
    public void TryRequest_should_reuse_the_process()
    {
        int starts = GitBrokerStats.Starts;
        CommandLog.Clear();

        for (int i = 0; i < 5; i++)
        {
            Request().Should().Be("blob");
        }

        (GitBrokerStats.Starts - starts).Should().Be(1);
        CommandLog.Commands.Should().ContainSingle(c => c.Arguments.EndsWith(CatFileCommand));
    }

    [Test]
    public void TryRequest_should_support_concurrent_requests()
    {
        int starts = GitBrokerStats.Starts;

        string?[] results = GitBrokerTestScope.InBackground(() => Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(RequestInBackground))));

        results.Should().AllBe("blob");
        (GitBrokerStats.Starts - starts).Should().Be(1);
    }

    [Test]
    public void TryRequest_should_limit_the_number_of_processes()
    {
        using GitBrokerTestScope scope2 = new();
        using GitBrokerTestScope scope3 = new();
        GitBrokerTestScope[] scopes = [_scope, scope2, scope3];
        foreach (GitBrokerTestScope scope in scopes)
        {
            scope.Commit("file", "text");
            Request(scope).Should().Be("blob");
        }

        _pool.SessionCount.Should().Be(2);
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 2, "the least recently used process must end");

        int starts = GitBrokerStats.Starts;
        Request(scope2).Should().Be("blob");
        Request(scope3).Should().Be("blob");
        GitBrokerStats.Starts.Should().Be(starts);
    }

    [Test]
    public void Process_should_end_when_idle_and_be_started_again()
    {
        GitBrokerPool pool = new(TimeSpan.FromMilliseconds(300), maxSessions: 2);
        int starts = GitBrokerStats.Starts;
        Request(pool: pool).Should().Be("blob");

        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process must end when idle");
        pool.SessionCount.Should().Be(0);

        Request(pool: pool).Should().Be("blob");
        (GitBrokerStats.Starts - starts).Should().Be(2);
        pool.ReleaseAll();
    }

    [Test]
    public void Requests_should_keep_the_process_alive()
    {
        GitBrokerPool pool = new(TimeSpan.FromSeconds(2), maxSessions: 2);
        int starts = GitBrokerStats.Starts;

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(4))
        {
            Request(pool: pool).Should().Be("blob");
            Thread.Sleep(100);
        }

        (GitBrokerStats.Starts - starts).Should().Be(1);
        pool.ReleaseAll();
    }

    [Test]
    public void Killed_process_should_be_replaced_by_the_next_request()
    {
        CommandLog.Clear();
        Request().Should().Be("blob");
        Kill();
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process has been killed");

        Request().Should().Be("blob");
    }

    [Test]
    public void Process_killed_before_the_request_is_sent_should_not_provide_a_result()
    {
        CommandLog.Clear();
        GitBrokerTestScope.InBackground(() => _pool.TryRequest(
            _scope.GitExecutable,
            (session, cancellationToken) =>
            {
                Kill();
                return session.GetInfo("HEAD:file", cancellationToken)?.Type;
            },
            CancellationToken.None,
            out string? type) ? type : null).Should().BeNull("the caller has to run git on its own");

        Request().Should().Be("blob");
    }

    [Test]
    public void Cancelled_request_should_not_affect_the_next_one()
    {
        using CancellationTokenSource cancellation = new();
        Action request = () => GitBrokerTestScope.InBackground(() => _pool.TryRequest<string>(
            _scope.GitExecutable,
            (session, cancellationToken) =>
            {
                // Like a cancellation while the response is read
                session.Dispose();
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            },
            cancellation.Token,
            out _));

        request.Should().Throw<OperationCanceledException>();
        Request().Should().Be("blob");
    }

    [Test]
    public void Repeated_failures_should_disable_the_repository_for_some_time()
    {
        long now = 1_000_000;
        GitBrokerPool pool = new(TimeSpan.FromMinutes(1), maxSessions: 2, getTickCount: () => now);

        for (int i = 0; i < 3; i++)
        {
            GitBrokerTestScope.InBackground(() => pool.TryRequest<string>(
                _scope.GitExecutable,
                (_, _) => throw new GitBrokerException("test"),
                CancellationToken.None,
                out _)).Should().BeFalse();
        }

        int starts = GitBrokerStats.Starts;
        Request(pool: pool).Should().BeNull();
        GitBrokerStats.Starts.Should().Be(starts);

        now += 5 * 60_000;
        Request(pool: pool).Should().Be("blob");
        pool.ReleaseAll();
    }

    [Test]
    public void Failures_with_successful_requests_between_should_not_disable_the_repository()
    {
        for (int i = 0; i < 5; i++)
        {
            GitBrokerTestScope.InBackground(() => _pool.TryRequest<string>(
                _scope.GitExecutable,
                (_, _) => throw new GitBrokerException("test"),
                CancellationToken.None,
                out _)).Should().BeFalse();
            Request().Should().Be("blob");
        }
    }

    [Test]
    public void ReleaseAll_during_a_request_should_not_count_as_a_failure()
    {
        for (int i = 0; i < 5; i++)
        {
            GitBrokerTestScope.InBackground(() => _pool.TryRequest<string>(
                _scope.GitExecutable,
                (session, _) =>
                {
                    _pool.ReleaseAll();
                    return session.GetInfo("HEAD:file", CancellationToken.None)?.Type;
                },
                CancellationToken.None,
                out _)).Should().BeFalse();
        }

        Request().Should().Be("blob");
    }

    [Test]
    public void ReleaseAll_should_end_all_processes()
    {
        Request().Should().Be("blob");
        _pool.SessionCount.Should().Be(1);

        _pool.ReleaseAll();

        _pool.SessionCount.Should().Be(0);
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process must end");
    }

    [Test]
    public void TryRequest_should_throw_if_cancelled_and_stay_usable()
    {
        Request().Should().Be("blob");

        Action request = () => GitBrokerTestScope.InBackground(() => _pool.TryRequest(
            _scope.GitExecutable,
            (session, cancellationToken) => session.GetInfo("HEAD:file", cancellationToken)?.Type,
            new CancellationToken(canceled: true),
            out _));

        request.Should().Throw<OperationCanceledException>();
        Request().Should().Be("blob");
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void TryRequest_should_not_do_anything_on_the_UI_thread()
    {
        int starts = GitBrokerStats.Starts;
        ThreadHelper.JoinableTaskContext.IsOnMainThread.Should().BeTrue("the test must be run on the main thread");

        _pool.TryRequest(_scope.GitExecutable, (session, ct) => session.GetInfo("HEAD:file", ct)?.Type, CancellationToken.None, out _).Should().BeFalse();

        GitBrokerStats.Starts.Should().Be(starts);
        Request().Should().Be("blob");
    }

    [TestCase("extensions.partialClone", "origin")]
    [TestCase("remote.origin.promisor", "true")]
    [TestCase("extensions.refStorage", "reftable")]
    [TestCase("extensions.objectFormat", "sha1")]
    public void TryRequest_should_not_support_special_repositories_also_if_changed_later(string key, string value)
    {
        Request().Should().Be("blob");

        File.AppendAllText(Path.Combine(_scope.Module.WorkingDirGitDir, "config"), FormatConfig(key, value));
        int starts = GitBrokerStats.Starts;

        Request().Should().BeNull();
        GitBrokerStats.Starts.Should().Be(starts);
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process must end");

        static string FormatConfig(string key, string value)
        {
            string[] parts = key.Split('.');
            string section = parts.Length == 3 ? $"[{parts[0]} \"{parts[1]}\"]" : $"[{parts[0]}]";
            return $"\n{section}\n\t{parts[^1]} = {value}\n";
        }
    }

    [Test]
    public void TryRequest_should_not_support_other_executables()
    {
        int starts = GitBrokerStats.Starts;
        string workingDir = _scope.Module.WorkingDir;

        Request(new Executable(() => AppSettings.GitCommand, workingDir, "-d Ubuntu --exec git ")).Should().BeNull("WSL");
        Request(new Executable(AppSettings.GitCommand, @"\\wsl$\Ubuntu\home\user\repo\")).Should().BeNull("WSL");
        Request(new Executable(AppSettings.GitCommand, Path.Combine(workingDir, "missing"))).Should().BeNull();
        Request(new Executable(AppSettings.GitCommand, "")).Should().BeNull();
        Request(new Executable(Path.Combine(workingDir, "git.exe"), workingDir)).Should().BeNull();

        GitBrokerStats.Starts.Should().Be(starts);
        this.Request().Should().Be("blob");

        string? Request(Executable executable)
            => GitBrokerTestScope.InBackground(() => _pool.TryRequest(executable, (session, ct) => session.GetInfo("HEAD:file", ct)?.Type, CancellationToken.None, out string? result) ? result : null);
    }

    [TestCase("GIT_DIR")]
    [TestCase("GIT_COMMON_DIR")]
    [TestCase("GIT_WORK_TREE")]
    public void TryRequest_should_not_support_a_repository_set_by_the_environment(string name)
    {
        Request().Should().Be("blob");
        Environment.SetEnvironmentVariable(name, _scope.Module.WorkingDirGitDir);
        try
        {
            Request().Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Test]
    public void TryRequest_should_support_worktrees()
    {
        ObjectId commitId = ObjectId.Parse(_scope.Git("rev-parse HEAD"));
        string worktree = $"{_scope.Module.WorkingDir.TrimEnd('\\')}-worktree";
        _scope.Git($"worktree add --detach \"{worktree}\" HEAD");

        GitBrokerTestScope.InBackground(() => _pool.TryRequest(
            new Executable(AppSettings.GitCommand, worktree),
            (session, ct) => session.GetInfo($"{commitId}:file", ct)?.Type,
            CancellationToken.None,
            out string? type) ? type : null).Should().Be("blob");
    }

    [Test]
    public void Gc_should_remove_the_packs_after_the_release([Values("gc --prune=now", "repack -adf")] string command)
    {
        _scope.Git("repack -adq");
        string packFolder = Path.Combine(_scope.Module.WorkingDirGitDir, "objects", "pack");
        string[] oldPacks = Directory.GetFiles(packFolder, "*.pack");
        oldPacks.Should().ContainSingle();
        Request().Should().Be("blob");
        _scope.Commit("file2", "text2");

        _pool.ReleaseAll();
        _scope.Git(command);

        string[] newPacks = Directory.GetFiles(packFolder, "*.pack");
        newPacks.Should().ContainSingle().Which.Should().NotBe(oldPacks[0]);
        _scope.Git("fsck").Should().NotContain("error");
        Request().Should().Be("blob");
    }

    private static void Kill()
    {
        using Process process = Process.GetProcessById(CommandLog.Commands.Single(c => c.Arguments.EndsWith(CatFileCommand)).ProcessId!.Value);
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    private string? Request(GitBrokerTestScope? scope = null, GitBrokerPool? pool = null)
        => GitBrokerTestScope.InBackground(() => RequestInBackground(scope, pool));

    private string? RequestInBackground() => RequestInBackground(scope: null, pool: null);

    private string? RequestInBackground(GitBrokerTestScope? scope, GitBrokerPool? pool)
        => (pool ?? _pool).TryRequest(
            (scope ?? _scope).GitExecutable,
            (session, cancellationToken) => session.GetInfo("HEAD:file", cancellationToken)?.Type,
            CancellationToken.None,
            out string? type) ? type : null;
}
