using System.Collections.Frozen;
using System.Drawing;
using CommonTestUtils;
using GitCommands;
using GitCommands.Config;
using GitCommands.Git;
using GitCommands.Logging;
using GitCommands.Settings;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitRemote = GitExtensions.Extensibility.Git.Remote;

namespace GitCommandsTests.Settings;

[TestFixture]
public sealed class GitConfigSnapshotTests
{
    [Test]
    public void GitConfigSnapshot_Parse_should_parse_scope_key_and_values()
    {
        string raw = "system\0core.autocrlf\ntrue\0global\0user.name\nJohn Doe\0local\0user.name\nJane Doe\0local\0core.bare\0";
        GitConfigSnapshot snapshot = GitConfigSnapshot.Parse(raw);

        snapshot.Entries.Count.Should().Be(4);
        snapshot.GetEffectiveValue("core.autocrlf").Should().Be("true");
        snapshot.GetEffectiveValue("user.name").Should().Be("Jane Doe");
        snapshot.GetEffectiveValue("core.bare").Should().Be("true");

        snapshot.GetLocalValue("user.name").Should().Be("Jane Doe");
        snapshot.GetLocalValue("core.autocrlf").Should().BeNull();
        snapshot.GetLocalValue("core.bare").Should().Be("true");
    }

    [Test]
    public void GitConfigSnapshot_should_detect_insteadOf()
    {
        string rawWithout = "local\0remote.origin.url\nhttps://example.com/repo.git\0";
        GitConfigSnapshot snapshotWithout = GitConfigSnapshot.Parse(rawWithout);
        snapshotWithout.HasInsteadOf.Should().BeFalse();

        string rawWithInsteadOf = "global\0url.https://github.com/.insteadOf\ngit@github.com:\0";
        GitConfigSnapshot snapshotWith = GitConfigSnapshot.Parse(rawWithInsteadOf);
        snapshotWith.HasInsteadOf.Should().BeTrue();

        string rawWithPushInsteadOf = "global\0url.https://github.com/.pushInsteadOf\ngit@github.com:\0";
        GitConfigSnapshot snapshotPush = GitConfigSnapshot.Parse(rawWithPushInsteadOf);
        snapshotPush.HasInsteadOf.Should().BeTrue();
    }

    [Test]
    public void Scope_local_should_match_local_includes_with_includeIf()
    {
        using GitModuleTestHelper helper = new();
        string gitDir = Path.Combine(helper.Module.WorkingDir, ".git");

        File.WriteAllText(Path.Combine(gitDir, "included.config"),
            "[user]\n\tname = Included Name\n\temail = included@example.com\n");
        File.AppendAllText(Path.Combine(gitDir, "config"),
            "[includeIf \"gitdir:**/\"]\n\tpath = included.config\n");

        string outputWithScope = helper.Module.GitExecutable.GetGitSettingsWithScope();
        GitConfigSnapshot snapshot = GitConfigSnapshot.Parse(outputWithScope);

        string legacyLocalOutput = helper.Module.GitExecutable.GetGitSettings(GitSettingLevel.Local);
        GitConfigSnapshot legacySnapshot = GitConfigSnapshot.CreateFromLegacy("", legacyLocalOutput);

        snapshot.GetLocalValue(SettingKeyString.UserName).Should().Be(legacySnapshot.GetLocalValue(SettingKeyString.UserName));
        snapshot.GetLocalValue(SettingKeyString.UserEmail).Should().Be(legacySnapshot.GetLocalValue(SettingKeyString.UserEmail));
        snapshot.GetLocalValues(SettingKeyString.UserName).Should().Equal(legacySnapshot.GetLocalValues(SettingKeyString.UserName));
    }

    [Test]
    public async Task Remotes_equivalence_tests_against_real_git()
    {
        using GitModuleTestHelper helper = new();
        string gitDir = Path.Combine(helper.Module.WorkingDir, ".git");
        string configFile = Path.Combine(gitDir, "config");

        // Remote configuration covering:
        // - Single remote
        // - Multiple remotes (sorting)
        // - 1 pushurl and 2 pushurls
        // - Remote name with dot
        // - Remote without url (name only in git config)
        // - Local path url
        string configContent = """

            [remote "zebra"]
                url = https://example.com/zebra.git
            [remote "apple"]
                url = https://example.com/apple.git
                pushurl = https://example.com/apple-push1.git
                pushurl = https://example.com/apple-push2.git
            [remote "sub.with.dot"]
                url = https://example.com/dot.git
            [remote "nourl"]
                fetch = +refs/heads/*:refs/remotes/nourl/*
            [remote "localrepo"]
                url = C:/BareRepo
            """;
        File.AppendAllText(configFile, configContent);

        helper.Module.InvalidateGitSettings();

        // 1. GetRemoteNames should match "git remote" (ordinal sorted, includes nourl)
        IReadOnlyList<string> remoteNamesFromModule = helper.Module.GetRemoteNames();
        string gitRemoteOutput = helper.Module.GitExecutable.Execute("remote").StandardOutput;
        List<string> expectedRemoteNames = gitRemoteOutput.LazySplit('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        remoteNamesFromModule.Should().Equal(expectedRemoteNames);
        remoteNamesFromModule.Should().Contain("nourl");
        remoteNamesFromModule.Should().Contain("sub.with.dot");

        // 2. GetRemotesAsync should match parsed "git remote -v" (excludes nourl, converts local path)
        IReadOnlyList<GitRemote> remotesFromModule = await helper.Module.GetRemotesAsync();

        // Nourl has no url, so it should be excluded
        remotesFromModule.Any(remote => remote.Name == "nourl").Should().BeFalse();

        // Find each remote and verify fields
        GitRemote appleRemote = remotesFromModule.Single(remote => remote.Name == "apple");
        appleRemote.FetchUrl.Should().Be("https://example.com/apple.git");
        appleRemote.PushUrls.Should().Equal("https://example.com/apple-push1.git", "https://example.com/apple-push2.git");

        GitRemote dotRemote = remotesFromModule.Single(remote => remote.Name == "sub.with.dot");
        dotRemote.FetchUrl.Should().Be("https://example.com/dot.git");
        dotRemote.PushUrls.Should().Equal("https://example.com/dot.git");

        GitRemote localRepository = remotesFromModule.Single(remote => remote.Name == "localrepo");
        string expectedLocalPath = helper.Module.GetWindowsPath("C:/BareRepo").ToPosixPath();
        localRepository.FetchUrl.Should().Be(expectedLocalPath);
        localRepository.PushUrls.Should().Equal(expectedLocalPath);

        GitRemote zebraRemote = remotesFromModule.Single(remote => remote.Name == "zebra");
        zebraRemote.FetchUrl.Should().Be("https://example.com/zebra.git");
        zebraRemote.PushUrls.Should().Equal("https://example.com/zebra.git");

        // Verify ordering: must be ordinal sorted by name
        remotesFromModule.Select(remote => remote.Name).Should().Equal(remotesFromModule.Select(remote => remote.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Test]
    public async Task GetRemotesAsync_with_insteadOf_should_trigger_fallback_and_log_remote_v()
    {
        using GitModuleTestHelper helper = new();
        string gitDir = Path.Combine(helper.Module.WorkingDir, ".git");
        string configFile = Path.Combine(gitDir, "config");

        File.AppendAllText(configFile, """

            [remote "origin"]
                url = https://github.com/gitextensions/gitextensions.git
            [url "git@github.com:"]
                insteadOf = https://github.com/
            """);

        helper.Module.InvalidateGitSettings();
        CommandLog.Clear();

        IReadOnlyList<GitRemote> remotes = await helper.Module.GetRemotesAsync();

        remotes.Count.Should().Be(1);
        remotes[0].Name.Should().Be("origin");

        // CommandLog must record the "remote -v" fallback process invocation
        CommandLog.Commands
            .Any(command => command.WorkingDir == helper.Module.WorkingDir && command.Arguments.Contains("remote -v"))
            .Should().BeTrue();
    }

    [Test]
    public async Task Process_budget_test_should_reduce_process_count_from_6_to_1()
    {
        using GitModuleTestHelper helper = new();
        string gitDir = Path.Combine(helper.Module.WorkingDir, ".git");
        string configFile = Path.Combine(gitDir, "config");

        File.AppendAllText(configFile, """

            [remote "origin"]
                url = https://example.com/origin.git
            """);

        helper.Module.InvalidateGitSettings();
        CommandLog.Clear();

        // Scenario:
        // 1) config lookup
        // 2) GetRemoteNames x3
        // 3) GetRemotesAsync
        // 4) GetRemoteColors
        string effectiveUserName = helper.Module.GetEffectiveSetting("user.name");
        IReadOnlyList<string> remoteNamesFirst = helper.Module.GetRemoteNames();
        IReadOnlyList<string> remoteNamesSecond = helper.Module.GetRemoteNames();
        IReadOnlyList<string> remoteNamesThird = helper.Module.GetRemoteNames();
        IReadOnlyList<GitRemote> remotes = await helper.Module.GetRemotesAsync();
        FrozenDictionary<string, Color> remoteColors = helper.Module.GetRemoteColors();

        // Filter commands executed in this test repo
        List<CommandLogEntry> gitCommands = CommandLog.Commands
            .Where(command => command.WorkingDir == helper.Module.WorkingDir)
            .ToList();

        // Under the new snapshot provider architecture, only 1 git process is executed:
        // "config list --show-scope --includes --null"
        // (Previously 6 processes: config list + GetRemoteNames x3 + GetRemotesAsync + GetRemoteColors)
        gitCommands.Count.Should().Be(1);
        gitCommands[0].Arguments.Should().Contain("--show-scope");
    }

    [Test]
    public async Task Invalidate_should_preload_snapshot_in_background_after_delay()
    {
        const int delayMilliseconds = 400;
        using GitModuleTestHelper helper = new();

        // 1. Initial load
        string initialUserName = helper.Module.GetEffectiveSetting(SettingKeyString.UserName);
        initialUserName.Should().Be("author");

        CommandLog.Clear();

        // 2. Invalidate and wait for background reload (250ms delay)
        helper.Module.InvalidateGitSettings();
        await Task.Delay(delayMilliseconds);

        // 3. Verify that the background reload executed on a background thread (not on the main thread)
        List<CommandLogEntry> backgroundCommands = CommandLog.Commands
            .Where(command => command.WorkingDir == helper.Module.WorkingDir && command.Arguments.Contains("--show-scope"))
            .ToList();
        backgroundCommands.Count.Should().Be(1);
        backgroundCommands[0].IsOnMainThread.Should().BeFalse();

        // 4. Subsequent read on the current thread should consume the cached snapshot without launching any git process
        CommandLog.Clear();
        string cachedUserName = helper.Module.GetEffectiveSetting(SettingKeyString.UserName);
        cachedUserName.Should().Be("author");

        List<CommandLogEntry> subsequentCommands = CommandLog.Commands
            .Where(command => command.WorkingDir == helper.Module.WorkingDir)
            .ToList();
        subsequentCommands.Should().BeEmpty();
    }

    [Test]
    public async Task Invalidate_multiple_times_consecutively_should_load_snapshot_only_once()
    {
        const int delayMilliseconds = 400;
        using GitModuleTestHelper helper = new();

        // Initial load
        helper.Module.GetEffectiveSetting(SettingKeyString.UserName);

        CommandLog.Clear();

        // 3 consecutive invalidations
        helper.Module.InvalidateGitSettings();
        helper.Module.InvalidateGitSettings();
        helper.Module.InvalidateGitSettings();

        await Task.Delay(delayMilliseconds);

        List<CommandLogEntry> gitCommands = CommandLog.Commands
            .Where(command => command.WorkingDir == helper.Module.WorkingDir && command.Arguments.Contains("--show-scope"))
            .ToList();

        // Generation counter should ensure only the last invalidation triggers a reload
        gitCommands.Count.Should().Be(1);
        gitCommands[0].IsOnMainThread.Should().BeFalse();
    }

    [Test]
    public void Invalidate_read_immediately_should_return_updated_setting_synchronously()
    {
        using GitModuleTestHelper helper = new();

        // Initial check
        helper.Module.GetEffectiveSetting(SettingKeyString.UserName).Should().Be("author");

        // Update the setting directly in repo config
        helper.Module.SetSetting(SettingKeyString.UserName, "new_author_name");
        helper.Module.InvalidateGitSettings();

        // Immediate synchronous read before background reload finishes must return the updated value
        string updatedUserName = helper.Module.GetEffectiveSetting(SettingKeyString.UserName);
        updatedUserName.Should().Be("new_author_name");
    }

    [Test]
    public void GitConfigSnapshotProviderCache_GetOrAdd_should_cache_and_reuse_provider()
    {
        using GitModuleTestHelper helper = new();

        GitConfigSnapshotProvider provider1 = GitConfigSnapshotProviderCache.GetOrAdd(helper.Module.GitExecutable);
        GitConfigSnapshotProvider provider2 = GitConfigSnapshotProviderCache.GetOrAdd(helper.Module.GitExecutable);

        provider1.Should().BeSameAs(provider2);
    }
}
