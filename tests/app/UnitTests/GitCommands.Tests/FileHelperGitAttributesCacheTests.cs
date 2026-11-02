using CommonTestUtils;
using GitCommands;
using GitExtensions.Extensibility.Git;
using NSubstitute;

namespace GitCommandsTests;

[TestFixture]
public class FileHelperGitAttributesCacheTests
{
    [Test]
    public void TryGetCachedGitAttribute_should_consume_cached_entry_on_first_hit()
    {
        IGitModule module = Substitute.For<IGitModule>();
        module.WorkingDir.Returns(@"C:\fake\repo");

        FileHelper.CacheGitAttribute(module, "sample.bin", true);

        FileHelper.TryGetCachedGitAttribute(module, "sample.bin", out bool? result).Should().BeTrue();
        result.Should().BeTrue();

        FileHelper.TryGetCachedGitAttribute(module, "sample.bin", out bool? secondResult).Should().BeFalse();
        secondResult.Should().BeNull();
    }

    [Test]
    public void PreloadGitAttributes_should_populate_cache_from_upstream_method()
    {
        using GitModuleTestHelper helper = new();

        FileHelper.PreloadGitAttributes(helper.Module, "test.txt");

        FileHelper.TryGetCachedGitAttribute(helper.Module, "test.txt", out bool? isBinary).Should().BeTrue();
        isBinary.Should().BeNull();

        FileHelper.TryGetCachedGitAttribute(helper.Module, "test.txt", out _).Should().BeFalse();
    }
}
