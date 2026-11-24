using GitCommands.Git.Broker;
using GitExtensions.Extensibility.Git;

namespace GitCommandsTests.Git.Broker;

[TestFixture]
public sealed class BlobMemoryCacheTests
{
    private static readonly ObjectId _id1 = ObjectId.Parse("1111111111111111111111111111111111111111");
    private static readonly ObjectId _id2 = ObjectId.Parse("2222222222222222222222222222222222222222");
    private static readonly ObjectId _id3 = ObjectId.Parse("3333333333333333333333333333333333333333");

    [Test]
    public void TryGet_should_return_the_content_of_the_repository()
    {
        BlobMemoryCache cache = new(() => 1000);
        byte[] content = [1, 2, 3];

        cache.Add("repo", _id1, content);

        cache.TryGet("repo", _id1, out byte[]? found).Should().BeTrue();
        found.Should().BeSameAs(content);
        cache.TryGet("other", _id1, out _).Should().BeFalse();
        cache.TryGet("repo", _id2, out _).Should().BeFalse();
    }

    [Test]
    public void Add_should_remove_the_least_recently_used_contents()
    {
        BlobMemoryCache cache = new(() => 2 * (100 + BlobMemoryCache.OverheadBytes));
        cache.Add("repo", _id1, new byte[100]);
        cache.Add("repo", _id2, new byte[100]);
        cache.TryGet("repo", _id1, out _).Should().BeTrue();

        cache.Add("repo", _id3, new byte[100]);

        cache.TryGet("repo", _id2, out _).Should().BeFalse();
        cache.TryGet("repo", _id1, out _).Should().BeTrue();
        cache.TryGet("repo", _id3, out _).Should().BeTrue();
        cache.TotalBytes.Should().Be(2 * (100 + BlobMemoryCache.OverheadBytes));
    }

    [Test]
    public void Add_should_not_keep_large_contents()
    {
        BlobMemoryCache cache = new(() => 100_000, maxBytesPerBlob: 10);

        cache.Add("repo", _id1, new byte[11]);
        cache.Add("repo", _id2, new byte[10]);

        cache.TryGet("repo", _id1, out _).Should().BeFalse();
        cache.TryGet("repo", _id2, out _).Should().BeTrue();
        BlobMemoryCache.MaxBytesPerBlob.Should().Be(16 * 1024 * 1024);
    }

    [Test]
    public void Add_should_count_the_same_blob_once()
    {
        BlobMemoryCache cache = new(() => 100_000);

        cache.Add("repo", _id1, new byte[10]);
        cache.Add("repo", _id1, new byte[10]);

        cache.TotalBytes.Should().Be(10 + BlobMemoryCache.OverheadBytes);
    }

    [Test]
    public void Limit_should_be_applied_when_changed()
    {
        long limit = 100_000;
        BlobMemoryCache cache = new(() => limit);
        cache.Add("repo", _id1, new byte[10]);

        limit = 0;

        cache.TryGet("repo", _id1, out _).Should().BeFalse();
        cache.TotalBytes.Should().Be(0);
        cache.Add("repo", _id1, new byte[10]);
        cache.TotalBytes.Should().Be(0);
    }

    [Test]
    public void Empty_contents_should_be_limited_too()
    {
        BlobMemoryCache cache = new(() => BlobMemoryCache.OverheadBytes);

        cache.Add("repo", _id1, []);
        cache.Add("repo", _id2, []);

        cache.TryGet("repo", _id1, out _).Should().BeFalse();
        cache.TryGet("repo", _id2, out _).Should().BeTrue();
    }
}
