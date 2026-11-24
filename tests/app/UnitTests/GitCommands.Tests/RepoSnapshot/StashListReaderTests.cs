using System.Text.Json;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitCommands.Logging;
using GitCommands.RepoSnapshot;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitCommandsTests.RepoSnapshot;

[TestFixture]
[NonParallelizable]
public sealed class StashListReaderTests
{
    private static readonly IGitExecutorProvider _executorProvider = new GitExecutorProvider(new GitDirectoryResolver());

    private GitModuleTestHelper _helper = null!;

    private string GitDir => Path.Combine(_helper.Module.WorkingDir, ".git");

    private string StashLog => Path.Combine(GitDir, "logs", "refs", "stash");

    [SetUp]
    public void SetUp()
    {
        _helper = new GitModuleTestHelper();
        RunGit("commit --allow-empty -m initial");
        _helper.CreateRepoFile("file.txt", "initial");
        RunGit("add file.txt");
        RunGit("commit -m file");
        for (int i = 0; i < 3; i++)
        {
            CreateStash($"Stash {i}");
        }
    }

    [TearDown]
    public void TearDown()
    {
        AppSettings.RepoSnapshotMemoryLimit.Value = AppSettings.RepoSnapshotMemoryLimit.Default;
        _helper.Dispose();
    }

    [Test]
    public void Stashes_should_be_reused_if_nothing_changed()
    {
        ReadAndCheck(expectedStashLists: 1).Should().HaveCount(3);
        ReadAndCheck(expectedStashLists: 0).Should().HaveCount(3);
        ReadAndCheck(expectedStashLists: 0).Should().HaveCount(3);
    }

    [Test]
    public void Stashes_should_be_read_again_if_stash_is_added()
    {
        ReadAndCheck(expectedStashLists: 1);
        CreateStash("Stash 3");
        ReadAndCheck(expectedStashLists: 1).Should().HaveCount(4);
        ReadAndCheck(expectedStashLists: 0).Should().HaveCount(4);
    }

    [Test]
    public void Stashes_should_be_read_again_if_stash_is_dropped([Values(0, 1, 2)] int index)
    {
        ReadAndCheck(expectedStashLists: 1);
        RunGit($"stash drop {index}");
        ReadAndCheck(expectedStashLists: 1).Should().HaveCount(2);
    }

    [Test]
    public void Stashes_should_be_read_again_if_stash_message_is_edited_without_changing_size_and_time()
    {
        ReadAndCheck(expectedStashLists: 1);
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(StashLog);
        string[] lines = File.ReadAllLines(StashLog);

        // Exchange the older two stashes, the tip and the size of the log are unchanged
        (lines[0], lines[1]) = (lines[1], lines[0]);
        File.WriteAllText(StashLog, string.Join('\n', lines) + '\n');
        File.SetLastWriteTimeUtc(StashLog, lastWriteTime);

        ReadAndCheck(expectedStashLists: 1);
    }

    [Test]
    public void Stashes_should_not_be_reused_if_the_last_stash_is_dropped()
    {
        ReadAndCheck(expectedStashLists: 1);
        RunGit("stash clear");
        ReadAndCheck(expectedStashLists: 0).Should().BeEmpty();
        CreateStash("New stash");
        ReadAndCheck(expectedStashLists: 1).Should().ContainSingle();
    }

    [Test]
    public void Stashes_should_be_read_again_if_mailmap_changed()
    {
        string author = ReadAndCheck(expectedStashLists: 1).First().Author!;
        File.WriteAllText(
            Path.Combine(_helper.Module.WorkingDir, ".mailmap"),
            $"Mapped Name <mapped@example.com> <{_helper.Module.GetEffectiveSetting("user.email")}>\n");

        GitRevision stash = ReadAndCheck(expectedStashLists: 1).First();
        stash.Author.Should().Be("Mapped Name").And.NotBe(author);
    }

    [Test]
    public void Stashes_should_be_read_again_if_configured_mailmap_changed()
    {
        string mailmap = Path.Combine(_helper.TemporaryPath, "mailmap.txt");
        File.WriteAllText(mailmap, "");
        RunGit($"config mailmap.file \"{mailmap.ToPosixPath()}\"");
        _helper.Module.InvalidateGitSettings();
        ReadAndCheck(expectedStashLists: 1);
        ReadAndCheck(expectedStashLists: 0);

        File.WriteAllText(mailmap, $"Mapped Name <mapped@example.com> <{_helper.Module.GetEffectiveSetting("user.email")}>\n");

        ReadAndCheck(expectedStashLists: 1).First().Author.Should().Be("Mapped Name");
    }

    [Test]
    public void Stashes_should_not_be_reused_with_mailmap_blob()
    {
        RunGit("config mailmap.blob HEAD:.mailmap");
        _helper.Module.InvalidateGitSettings();
        ReadAndCheck(expectedStashLists: 1);
        ReadAndCheck(expectedStashLists: 1);
    }

    [Test]
    public void Stashes_should_be_read_again_if_log_config_changed()
    {
        ReadAndCheck(expectedStashLists: 1);
        RunGit("config log.date iso");
        _helper.Module.InvalidateGitSettings();
        ReadAndCheck(expectedStashLists: 1);
    }

    [Test]
    public void Stashes_should_be_read_again_if_grafts_changed()
    {
        ObjectId stashId = ReadAndCheck(expectedStashLists: 1).First().ObjectId;
        string grafts = Path.Combine(GitDir, "info", "grafts");
        Directory.CreateDirectory(Path.GetDirectoryName(grafts)!);
        File.WriteAllText(grafts, $"{stashId}\n");

        ReadAndCheck(expectedStashLists: 1);
    }

    [Test]
    public void Stashes_should_be_read_again_if_commit_is_replaced()
    {
        ObjectId stashId = ReadAndCheck(expectedStashLists: 1).First().ObjectId;
        RunGit($"update-ref refs/replace/{stashId} {RunGit("rev-parse HEAD").Trim()}");

        ReadAndCheck(expectedStashLists: 1);
        ReadAndCheck(expectedStashLists: 0);
    }

    [Test]
    public void Reused_stashes_should_not_be_affected_by_changes_of_the_grid()
    {
        IReadOnlyCollection<GitRevision> first = ReadAndCheck(expectedStashLists: 1);
        IReadOnlyCollection<GitRevision> second = ReadAndCheck(expectedStashLists: 0);
        foreach (GitRevision stash in first.Concat(second))
        {
            stash.ParentIds = [stash.FirstParentId];
            stash.ReflogSelector = null;
            stash.Body = "changed";
            stash.Notes = "changed";
        }

        // Compares with git
        ReadAndCheck(expectedStashLists: 0);
    }

    [Test]
    public void Stashes_should_be_reused_in_linked_worktree()
    {
        string worktree = Path.Combine(_helper.TemporaryPath, "linked");
        RunGit($"worktree add \"{worktree.ToPosixPath()}\"");
        GitModule module = new(_executorProvider, worktree);

        ReadAndCheck(expectedStashLists: 1, module).Should().HaveCount(3);
        ReadAndCheck(expectedStashLists: 0, module).Should().HaveCount(3);
        RunGit("stash drop 1");
        ReadAndCheck(expectedStashLists: 1, module).Should().HaveCount(2);
    }

    [Test]
    public void Stashes_should_not_be_reused_if_disabled()
    {
        AppSettings.RepoSnapshotMemoryLimit.Value = 0;
        ReadAndCheck(expectedStashLists: 1);
        ReadAndCheck(expectedStashLists: 1);
    }

    [Test]
    public void Stashes_should_not_be_stored_if_cancelled()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        GitModule module = new(_executorProvider, _helper.Module.WorkingDir);
        Action read = () => StashListReader.GetStashes(module, module.GetRefs(RefsFilter.NoFilter), cancellation.Token);
        read.Should().Throw<OperationCanceledException>();

        ReadAndCheck(expectedStashLists: 1).Should().HaveCount(3);
    }

    [Test]
    public void Failed_git_should_not_be_stored()
    {
        ReadAndCheck(expectedStashLists: 1);
        File.Delete(StashLog);

        // The ref exists, but there is no log
        ReadAndCheck(expectedStashLists: 1);
        ReadAndCheck(expectedStashLists: 1);
    }

    private static string Serialize(IEnumerable<GitRevision> revisions)
        => JsonSerializer.Serialize(revisions.Select(r => new
        {
            Id = r.ObjectId.ToString(),
            Parents = r.ParentIds!.Select(p => p.ToString()),
            Tree = r.TreeId.ToString(),
            r.Author,
            r.AuthorEmail,
            r.AuthorUnixTime,
            r.Committer,
            r.CommitterEmail,
            r.CommitUnixTime,
            r.Subject,
            r.Body,
            r.HasMultiLineMessage,
            r.Notes,
            r.ReflogSelector,
            r.IsAutostash,
            Refs = r.Refs.Count
        }));

    private void CreateStash(string message)
    {
        _helper.CreateRepoFile("file.txt", message);
        RunGit($"stash push -m \"{message}\"");
    }

    /// <summary>
    ///  Reads the stashes with a new module and compares them with the stashes read from git.
    /// </summary>
    private IReadOnlyCollection<GitRevision> ReadAndCheck(int expectedStashLists, GitModule? module = null)
    {
        module ??= new(_executorProvider, _helper.Module.WorkingDir);
        IReadOnlyList<IGitRef> refs = module.GetRefs(RefsFilter.NoFilter);

        CommandLog.Clear();
        IReadOnlyCollection<GitRevision> stashes = StashListReader.GetStashes(module, refs, CancellationToken.None);
        CommandLog.Commands.Count(c => c.Arguments.StartsWith("stash list")).Should().Be(expectedStashLists);

        if (stashes.Count > 0)
        {
            Serialize(stashes).Should().Be(Serialize(new RevisionReader(module).GetStashes(CancellationToken.None)));
        }

        return stashes;
    }

    private string RunGit(string arguments) => _helper.Module.GitExecutable.GetOutput(arguments);
}
