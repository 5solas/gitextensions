using GitCommands;
using GitCommands.RepoSnapshot;

namespace GitCommandsTests.RepoSnapshot;

[TestFixture]
public sealed class RepoSnapshotStoreTests
{
    private long _maxBytes;

    [SetUp]
    public void SetUp() => _maxBytes = 100;

    [Test]
    public void TryGet_should_return_stored_parts_of_each_type()
    {
        RepoSnapshotStore store = CreateStore();
        PartA partA = new(10);
        PartB partB = new(20);
        store.Set("repo", partA);
        store.Set("repo", partB);

        store.TryGet("repo", out PartA? foundA).Should().BeTrue();
        foundA.Should().BeSameAs(partA);
        store.TryGet("repo", out PartB? foundB).Should().BeTrue();
        foundB.Should().BeSameAs(partB);
        store.TryGet("other", out PartA? _).Should().BeFalse();
        store.Count.Should().Be(1);
        store.TotalBytes.Should().Be(30);
    }

    [Test]
    public void Key_should_be_case_insensitive()
    {
        RepoSnapshotStore store = CreateStore();
        store.Set(@"C:\Repo", new PartA(10));

        store.TryGet(@"c:\repo", out PartA? _).Should().BeTrue();
    }

    [TestCase(@"C:\repo", @"C:\repo")]
    [TestCase(@"C:\repo\", @"C:\repo")]
    [TestCase("C:/repo/", @"C:\repo")]
    [TestCase(@"C:\other\..\repo\", @"C:\repo")]
    [TestCase(@"C:\", @"C:\")]
    public void TryGetKey_should_normalize_the_working_directory(string workingDir, string expected)
    {
        RepoSnapshotStore.TryGetKey(workingDir, out string? key).Should().BeTrue();
        key.Should().Be(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("C:\\invalid\0path")]
    public void TryGetKey_should_not_throw(string? workingDir)
    {
        RepoSnapshotStore.TryGetKey(workingDir, out _).Should().BeFalse();
    }

    [Test]
    public void Set_should_replace_the_part()
    {
        RepoSnapshotStore store = CreateStore();
        PartA second = new(30);
        store.Set("repo", new PartA(90));
        store.Set("repo", second);

        store.TryGet("repo", out PartA? found).Should().BeTrue();
        found.Should().BeSameAs(second);
        store.TotalBytes.Should().Be(30);
    }

    [Test]
    public void Set_should_remove_least_recently_used_repositories_if_memory_limit_is_exceeded()
    {
        RepoSnapshotStore store = CreateStore();
        store.Set("a", new PartA(40));
        store.Set("b", new PartA(40));
        store.TryGet("a", out PartA? _).Should().BeTrue();

        store.Set("c", new PartA(40));

        store.TryGet("b", out PartA? _).Should().BeFalse();
        store.TryGet("a", out PartA? _).Should().BeTrue();
        store.TryGet("c", out PartA? _).Should().BeTrue();
        store.TotalBytes.Should().Be(80);
    }

    [Test]
    public void Set_should_remove_least_recently_used_repositories_if_count_is_exceeded()
    {
        RepoSnapshotStore store = new(() => _maxBytes, maxRepositories: 2);
        store.Set("a", new PartA(1));
        store.Set("b", new PartA(1));
        store.Set("a", new PartB(1));

        store.Set("c", new PartA(1));

        store.Count.Should().Be(2);
        store.TryGet("b", out PartA? _).Should().BeFalse();
        store.TryGet("a", out PartA? _).Should().BeTrue();
    }

    [Test]
    public void Set_should_not_store_parts_larger_than_the_limit_and_keep_the_others()
    {
        RepoSnapshotStore store = CreateStore();
        store.Set("a", new PartA(50));
        store.Set("b", new PartA(10));

        store.Set("b", new PartA(101));

        // The replaced part is outdated
        store.TryGet("b", out PartA? _).Should().BeFalse();
        store.TryGet("a", out PartA? _).Should().BeTrue();
        store.TotalBytes.Should().Be(50);
        store.Count.Should().Be(1);
    }

    [Test]
    public void Limit_0_should_disable_the_store()
    {
        RepoSnapshotStore store = CreateStore();
        store.Set("a", new PartA(50));

        _maxBytes = 0;
        store.IsEnabled.Should().BeFalse();
        store.TryGet("a", out PartA? _).Should().BeFalse();
        store.Set("a", new PartA(0));
        store.Count.Should().Be(0);
        store.TotalBytes.Should().Be(0);
    }

    [Test]
    public void Lowered_limit_should_be_respected()
    {
        RepoSnapshotStore store = CreateStore();
        store.Set("a", new PartA(50));
        store.Set("b", new PartA(50));

        _maxBytes = 60;
        store.TryGet("a", out PartA? _).Should().BeFalse();
        store.TryGet("b", out PartA? _).Should().BeTrue();
        store.TotalBytes.Should().Be(50);
    }

    [Test]
    public void Remove_should_remove_only_the_part()
    {
        RepoSnapshotStore store = CreateStore();
        store.Set("a", new PartA(10));
        store.Set("a", new PartB(20));

        store.Remove<PartA>("a");
        store.TryGet("a", out PartA? _).Should().BeFalse();
        store.TryGet("a", out PartB? _).Should().BeTrue();
        store.TotalBytes.Should().Be(20);

        store.Remove<PartB>("a");
        store.Remove<PartB>("a");
        store.Count.Should().Be(0);
        store.TotalBytes.Should().Be(0);
    }

    [Test]
    public void Store_should_be_thread_safe()
    {
        RepoSnapshotStore store = CreateStore();
        Parallel.For(0, 10_000, i =>
        {
            string key = $"repo{i % 7}";
            switch (i % 4)
            {
                case 0:
                    store.Set(key, new PartA(i % 60));
                    break;
                case 1:
                    store.Set(key, new PartB(i % 30));
                    break;
                case 2:
                    store.TryGet(key, out PartA? _);
                    break;
                default:
                    store.Remove<PartB>(key);
                    break;
            }
        });

        store.TotalBytes.Should().BeInRange(0, _maxBytes);
        for (int i = 0; i < 7; i++)
        {
            store.Remove<PartA>($"repo{i}");
            store.Remove<PartB>($"repo{i}");
        }

        store.TotalBytes.Should().Be(0);
        store.Count.Should().Be(0);
    }

    [Test]
    [NonParallelizable]
    public void Default_store_should_use_the_setting()
    {
        AppSettings.RepoSnapshotMemoryLimit.Default.Should().Be(1024);
        using RepoSnapshotTestScope scope = new();

        AppSettings.RepoSnapshotMemoryLimit.Value = AppSettings.RepoSnapshotMemoryLimit.Default;
        RepoSnapshotStore.Default.IsEnabled.Should().BeTrue();

        AppSettings.RepoSnapshotMemoryLimit.Value = 0;
        RepoSnapshotStore.Default.IsEnabled.Should().BeFalse();

        AppSettings.RepoSnapshotMemoryLimit.Value = -1;
        RepoSnapshotStore.Default.IsEnabled.Should().BeFalse();
    }

    [Test]
    [NonParallelizable]
    public void Test_scope_should_restore_the_stored_setting([Values(null, "0", "42")] string? stored)
    {
        string name = AppSettings.RepoSnapshotMemoryLimit.FullPath;
        string? original = AppSettings.GetString(name, defaultValue: null);
        try
        {
            AppSettings.SetString(name, stored!);
            using (new RepoSnapshotTestScope())
            {
                AppSettings.RepoSnapshotMemoryLimit.Value = 1;
                RepoSnapshotStore.Default.Set("repo", new PartA(1));
            }

            AppSettings.GetString(name, defaultValue: null).Should().Be(stored);
            RepoSnapshotStore.Default.Count.Should().Be(0);
        }
        finally
        {
            AppSettings.SetString(name, original!);
        }
    }

    private RepoSnapshotStore CreateStore() => new(() => _maxBytes);

    private sealed record PartA(long EstimatedBytes) : IRepoSnapshotPart;

    private sealed record PartB(long EstimatedBytes) : IRepoSnapshotPart;
}
