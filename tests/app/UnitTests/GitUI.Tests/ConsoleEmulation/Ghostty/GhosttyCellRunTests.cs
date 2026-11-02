using System.Drawing;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttyCellRunTests
{
    [Test]
    public void Build_should_return_empty_list_for_empty_input()
    {
        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(Array.Empty<GhosttyCell>());
        runs.Should().BeEmpty();
    }

    [Test]
    public void Build_should_throw_on_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => GhosttyCellRun.Build(null!));
    }

    [Test]
    public void Build_should_merge_three_cells_with_same_style_into_one_run_of_width_three()
    {
        Color fg = Color.White;
        Color bg = Color.Black;
        GhosttyCell[] cells =
        [
            new("A", 0, fg, bg),
            new("B", 0, fg, bg),
            new("C", 0, fg, bg)
        ];

        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(cells);

        runs.Should().HaveCount(1);
        runs[0].StartColumn.Should().Be(0);
        runs[0].ColumnCount.Should().Be(3);
        runs[0].Text.Should().Be("ABC");
        runs[0].Fg.Should().Be(fg);
        runs[0].Bg.Should().Be(bg);
    }

    [Test]
    public void Build_should_split_runs_when_colors_change()
    {
        GhosttyCell[] cells =
        [
            new("A", 0, Color.Red, Color.Black),
            new("B", 0, Color.Blue, Color.Black)
        ];

        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(cells);

        runs.Should().HaveCount(2);
        runs[0].StartColumn.Should().Be(0);
        runs[0].ColumnCount.Should().Be(1);
        runs[0].Text.Should().Be("A");
        runs[0].Fg.Should().Be(Color.Red);

        runs[1].StartColumn.Should().Be(1);
        runs[1].ColumnCount.Should().Be(1);
        runs[1].Text.Should().Be("B");
        runs[1].Fg.Should().Be(Color.Blue);
    }

    [Test]
    public void Build_should_split_runs_when_styles_change()
    {
        GhosttyCell[] cells =
        [
            new("A", 0, Color.White, Color.Black, Bold: true),
            new("B", 0, Color.White, Color.Black, Bold: false)
        ];

        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(cells);

        runs.Should().HaveCount(2);
        runs[0].Bold.Should().BeTrue();
        runs[1].Bold.Should().BeFalse();
    }

    [Test]
    public void Build_should_include_spacer_tail_in_preceding_run_for_wide_characters()
    {
        GhosttyCell[] cells =
        [
            new("한", GhosttyNativeConstants.CellWideWide, Color.White, Color.Black),
            new(string.Empty, GhosttyNativeConstants.CellWideSpacerTail, Color.White, Color.Black)
        ];

        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(cells);

        runs.Should().HaveCount(1);
        runs[0].StartColumn.Should().Be(0);
        runs[0].ColumnCount.Should().Be(2);
        runs[0].Text.Should().Be("한");
        runs[0].Fg.Should().Be(Color.White);
        runs[0].Bg.Should().Be(Color.Black);
    }

    [Test]
    public void Build_should_exchange_fg_and_bg_for_inverse_cells()
    {
        Color fg = Color.White;
        Color bg = Color.Black;
        GhosttyCell[] cells =
        [
            new("X", 0, fg, bg, Inverse: true)
        ];

        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(cells);

        runs.Should().HaveCount(1);
        runs[0].StartColumn.Should().Be(0);
        runs[0].ColumnCount.Should().Be(1);
        runs[0].Text.Should().Be("X");
        runs[0].Fg.Should().Be(bg);
        runs[0].Bg.Should().Be(fg);
    }

    [Test]
    public void Build_should_treat_empty_cells_as_spaces_and_merge_consecutive_empty_cells()
    {
        GhosttyCell[] cells =
        [
            new(string.Empty, 0, Color.White, Color.Black),
            new(string.Empty, 0, Color.White, Color.Black),
            new(string.Empty, 0, Color.White, Color.Black)
        ];

        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(cells);

        runs.Should().HaveCount(1);
        runs[0].StartColumn.Should().Be(0);
        runs[0].ColumnCount.Should().Be(3);
        runs[0].Text.Should().Be("   ");
    }

    [Test]
    public void Build_should_preserve_spacing_between_characters()
    {
        GhosttyCell[] cells =
        [
            new("A", 0, Color.White, Color.Black),
            new(string.Empty, 0, Color.White, Color.Black),
            new("B", 0, Color.White, Color.Black)
        ];

        IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(cells);

        runs.Should().HaveCount(1);
        runs[0].StartColumn.Should().Be(0);
        runs[0].ColumnCount.Should().Be(3);
        runs[0].Text.Should().Be("A B");
    }
}
