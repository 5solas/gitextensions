using GitCommands;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls.RevisionGrid.Reachability;
using NSubstitute;

namespace GitUITests.UserControls.RevisionGrid.Reachability;

[TestFixture]
public class RefSnapshotTests
{
    private readonly ObjectId _master = ObjectId.Random();
    private readonly ObjectId _feature = ObjectId.Random();
    private string _gitDir = null!;

    [SetUp]
    public void SetUp()
    {
        _gitDir = Path.Combine(Path.GetTempPath(), "RefSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_gitDir);
        File.WriteAllText(Path.Combine(_gitDir, "HEAD"), "ref: refs/heads/master\n");
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_gitDir, recursive: true);
    }

    [Test]
    public void Read_should_detect_attached_and_detached_head()
    {
        RefSnapshot attached = RefSnapshot.Read(Refs(), _gitDir, _gitDir, tagSortSetting: "");
        attached.Head.Should().Be(HeadState.Attached);
        attached.CanReproduceBranches.Should().BeTrue();
        attached.CanReproduceTagOrder.Should().BeTrue();

        File.WriteAllText(Path.Combine(_gitDir, "HEAD"), $"{_feature}\n");
        RefSnapshot detached = RefSnapshot.Read(Refs(), _gitDir, _gitDir, tagSortSetting: null);
        detached.Head.Should().Be(HeadState.Detached(_feature));
        detached.CanReproduceBranches.Should().BeTrue();
    }

    [Test]
    public void Read_should_restore_remote_head_removed_by_GE()
    {
        // GitModule.ParseRefs() removes origin/HEAD, origin/master points to the same commit
        WriteRef("refs/remotes/origin/HEAD", "ref: refs/remotes/origin/master");

        RefSnapshot refs = RefSnapshot.Read(Refs(("refs/remotes/origin/master", _master), ("refs/remotes/origin/feature", _feature)), _gitDir, _gitDir, tagSortSetting: "");

        refs.Branches.Should().Equal(
            new KeyValuePair<string, ObjectId>("refs/remotes/origin/HEAD", _master),
            new KeyValuePair<string, ObjectId>("refs/remotes/origin/feature", _feature),
            new KeyValuePair<string, ObjectId>("refs/remotes/origin/master", _master));
    }

    [Test]
    public void Read_should_ignore_broken_remote_head()
    {
        WriteRef("refs/remotes/origin/HEAD", "ref: refs/remotes/origin/missing");

        RefSnapshot refs = RefSnapshot.Read(Refs(("refs/remotes/origin/master", _master)), _gitDir, _gitDir, tagSortSetting: "");

        refs.Branches.Select(b => b.Key).Should().Equal("refs/remotes/origin/master");
        refs.CanReproduceBranches.Should().BeTrue();
    }

    [Test]
    public void Read_should_restore_packed_regular_remote_head()
    {
        File.WriteAllText(
            Path.Combine(_gitDir, "packed-refs"),
            $"# pack-refs with: peeled fully-peeled sorted\n{_master} refs/remotes/upstream/HEAD\n{_master} refs/remotes/upstream/master\n");

        RefSnapshot refs = RefSnapshot.Read(Refs(("refs/remotes/upstream/master", _master)), _gitDir, _gitDir, tagSortSetting: "");

        refs.Branches.Select(b => b.Key).Should().Equal("refs/remotes/upstream/HEAD", "refs/remotes/upstream/master");
    }

    [Test]
    public void Read_should_not_reproduce_branches_with_reftable()
    {
        Directory.CreateDirectory(Path.Combine(_gitDir, "reftable"));

        RefSnapshot refs = RefSnapshot.Read(Refs(("refs/heads/master", _master)), _gitDir, _gitDir, tagSortSetting: "");

        refs.CanReproduceBranches.Should().BeFalse();
        refs.Head.IsKnown.Should().BeFalse();
    }

    [Test]
    public void Read_should_not_reproduce_tag_order_with_tag_sort()
    {
        RefSnapshot.Read(Refs(), _gitDir, _gitDir, tagSortSetting: "-creatordate").CanReproduceTagOrder.Should().BeFalse();
    }

    [Test]
    public void Snapshot_should_classify_refs()
    {
        RefSnapshot refs = new(
            Refs(
                ("refs/heads/master", _master),
                ("refs/tags/annotated^{}", _master),
                ("refs/tags/light", _feature),
                ("refs/stash", _feature),
                ("refs/notes/commits", _feature),
                ("refs/heads/zero", ObjectId.Parse(new string('0', 40)))),
            HeadState.Attached);

        refs.Branches.Select(b => b.Key).Should().Equal("refs/heads/master");
        refs.Tags.Should().Equal(
            new RefSnapshot.TagRef("annotated", _master, IsAnnotated: true),
            new RefSnapshot.TagRef("light", _feature, IsAnnotated: false));
    }

    private static IReadOnlyList<IGitRef> Refs(params (string Name, ObjectId Id)[] refs)
    {
        IGitModule module = Substitute.For<IGitModule>();
        return refs.Select(r => (IGitRef)new GitRef(module, r.Id, r.Name)).ToList();
    }

    private void WriteRef(string name, string content)
    {
        string path = Path.Combine(_gitDir, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content + "\n");
    }
}
