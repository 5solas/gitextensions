using System.Drawing;
using AwesomeAssertions;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttyColorSchemesTests
{
    [Test]
    public void All_should_not_be_empty_and_have_unique_names()
    {
        GhosttyColorSchemes.All.Should().NotBeEmpty();
        GhosttyColorSchemes.All.Select(scheme => scheme.Name).Should().OnlyHaveUniqueItems(name => name.ToLowerInvariant());
        GhosttyColorSchemes.Names.Should().BeEquivalentTo(GhosttyColorSchemes.All.Select(scheme => scheme.Name));
    }

    [Test]
    public void All_should_be_ordered_by_name()
    {
        GhosttyColorSchemes.All.Select(scheme => scheme.Name)
            .Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Test]
    public void All_schemes_should_have_sixteen_palette_colors()
    {
        foreach (GhosttyColorScheme scheme in GhosttyColorSchemes.All)
        {
            scheme.Palette.Should().HaveCount(16, scheme.Name);
        }
    }

    [Test]
    public void TryGet_should_ignore_case()
    {
        GhosttyColorSchemes.TryGet("tomorrow night")!.Name.Should().Be("Tomorrow Night");
    }

    [TestCase("nope")]
    [TestCase("")]
    [TestCase(null)]
    public void TryGet_should_return_null_for_unknown_name(string? name)
    {
        GhosttyColorSchemes.TryGet(name).Should().BeNull();
    }

    [Test]
    public void Resolve_should_fall_back_to_default_by_mode()
    {
        GhosttyColorSchemes.Resolve(null, darkMode: true).Name.Should().Be("Tomorrow Night");
        GhosttyColorSchemes.Resolve(null, darkMode: false).Name.Should().Be("Tomorrow");
        GhosttyColorSchemes.Resolve("nope", darkMode: false).Name.Should().Be("Tomorrow");
    }

    [Test]
    public void Resolve_should_prefer_configured_name()
    {
        GhosttyColorSchemes.Resolve("Dracula", darkMode: true).Name.Should().Be("Dracula");
    }

    [Test]
    public void Known_colors_should_match_source_values()
    {
        GhosttyColorScheme tomorrowNight = GhosttyColorSchemes.TryGet("Tomorrow Night")!;
        tomorrowNight.Background.Should().Be(Color.FromArgb(0x1d, 0x1f, 0x21));
        tomorrowNight.Palette[1].Should().Be(Color.FromArgb(0xcc, 0x66, 0x66));

        GhosttyColorSchemes.TryGet("Ghostty Default")!.Palette[1].Should().Be(Color.FromArgb(204, 102, 102));
    }

    [Test]
    public void Constructor_should_reject_wrong_palette_size()
    {
        Action action = () => _ = new GhosttyColorScheme("x", Color.White, Color.Black, Color.White, Color.White, Color.Black, [Color.Black]);

        action.Should().Throw<ArgumentException>();
    }
}
