using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttyTerminalSelectionTests
{
    [Test]
    public void Begin_should_activate_selection_and_initialize_anchor_and_head()
    {
        GhosttyTerminalSelection selection = new();
        selection.IsActive.Should().BeFalse();

        selection.Begin(5, 2);

        selection.IsActive.Should().BeTrue();
        selection.Anchor.Should().Be((5, 2));
        selection.Head.Should().Be((5, 2));
    }

    [Test]
    public void Extend_should_update_head_and_preserve_anchor()
    {
        GhosttyTerminalSelection selection = new();
        selection.Begin(5, 2);
        selection.Extend(12, 4);

        selection.IsActive.Should().BeTrue();
        selection.Anchor.Should().Be((5, 2));
        selection.Head.Should().Be((12, 4));
    }

    [Test]
    public void Extend_without_begin_should_start_selection()
    {
        GhosttyTerminalSelection selection = new();
        selection.Extend(3, 1);

        selection.IsActive.Should().BeTrue();
        selection.Anchor.Should().Be((3, 1));
        selection.Head.Should().Be((3, 1));
    }

    [Test]
    public void Normalized_should_order_start_and_end_in_forward_drag()
    {
        GhosttyTerminalSelection selection = new();
        selection.Begin(2, 1);
        selection.Extend(10, 3);

        var (start, end) = selection.Normalized;
        start.Should().Be((2, 1));
        end.Should().Be((10, 3));
    }

    [Test]
    public void Normalized_should_order_start_and_end_in_backward_drag_same_row()
    {
        GhosttyTerminalSelection selection = new();
        selection.Begin(15, 2);
        selection.Extend(4, 2);

        var (start, end) = selection.Normalized;
        start.Should().Be((4, 2));
        end.Should().Be((15, 2));
    }

    [Test]
    public void Normalized_should_order_start_and_end_in_backward_drag_multi_row()
    {
        GhosttyTerminalSelection selection = new();
        selection.Begin(20, 5);
        selection.Extend(5, 2);

        var (start, end) = selection.Normalized;
        start.Should().Be((5, 2));
        end.Should().Be((20, 5));
    }

    [Test]
    public void Contains_should_return_false_when_not_active()
    {
        GhosttyTerminalSelection selection = new();

        selection.Contains(0, 0).Should().BeFalse();
        selection.Contains(5, 5).Should().BeFalse();
    }

    [Test]
    public void Contains_should_include_boundary_and_interior_cells_for_single_row()
    {
        GhosttyTerminalSelection selection = new();
        selection.Begin(3, 2);
        selection.Extend(8, 2);

        selection.Contains(3, 2).Should().BeTrue();
        selection.Contains(5, 2).Should().BeTrue();
        selection.Contains(8, 2).Should().BeTrue();

        selection.Contains(2, 2).Should().BeFalse();
        selection.Contains(9, 2).Should().BeFalse();
        selection.Contains(5, 1).Should().BeFalse();
        selection.Contains(5, 3).Should().BeFalse();
    }

    [Test]
    public void Contains_should_include_boundary_and_interior_cells_for_multi_row()
    {
        GhosttyTerminalSelection selection = new();
        selection.Begin(10, 2);
        selection.Extend(5, 4);

        // Row 1: completely outside
        selection.Contains(10, 1).Should().BeFalse();

        // Row 2 (start row): columns >= 10
        selection.Contains(9, 2).Should().BeFalse();
        selection.Contains(10, 2).Should().BeTrue();
        selection.Contains(50, 2).Should().BeTrue();

        // Row 3 (intermediate full row): all columns
        selection.Contains(0, 3).Should().BeTrue();
        selection.Contains(25, 3).Should().BeTrue();
        selection.Contains(79, 3).Should().BeTrue();

        // Row 4 (end row): columns <= 5
        selection.Contains(0, 4).Should().BeTrue();
        selection.Contains(5, 4).Should().BeTrue();
        selection.Contains(6, 4).Should().BeFalse();

        // Row 5: completely outside
        selection.Contains(0, 5).Should().BeFalse();
    }

    [Test]
    public void Clear_should_reset_active_state_and_coordinates()
    {
        GhosttyTerminalSelection selection = new();
        selection.Begin(2, 3);
        selection.Extend(5, 4);

        selection.Clear();

        selection.IsActive.Should().BeFalse();
        selection.Anchor.Should().Be((0, 0));
        selection.Head.Should().Be((0, 0));
        selection.Contains(3, 3).Should().BeFalse();
    }

    [Test]
    public void IsEmpty_should_return_true_when_inactive_or_anchor_equals_head()
    {
        GhosttyTerminalSelection selection = new();
        selection.IsEmpty.Should().BeTrue();

        selection.Begin(4, 4);
        selection.IsEmpty.Should().BeTrue();

        selection.Extend(5, 4);
        selection.IsEmpty.Should().BeFalse();

        selection.Clear();
        selection.IsEmpty.Should().BeTrue();
    }
}
