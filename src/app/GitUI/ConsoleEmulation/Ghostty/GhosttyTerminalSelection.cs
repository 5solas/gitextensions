namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Tracks a text selection in terminal grid coordinates (column, row).
/// </summary>
internal sealed class GhosttyTerminalSelection
{
    /// <summary>
    ///  Gets a value indicating whether a selection is currently active.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    ///  Gets the anchor position where the selection started.
    /// </summary>
    public (int Col, int Row) Anchor { get; private set; }

    /// <summary>
    ///  Gets the head position representing the current moving endpoint of the selection.
    /// </summary>
    public (int Col, int Row) Head { get; private set; }

    /// <summary>
    ///  Gets the normalized selection start and end coordinates in stream order (row first, then column).
    /// </summary>
    public ((int Col, int Row) Start, (int Col, int Row) End) Normalized
    {
        get
        {
            if (Anchor.Row < Head.Row || (Anchor.Row == Head.Row && Anchor.Col <= Head.Col))
            {
                return (Anchor, Head);
            }

            return (Head, Anchor);
        }
    }

    /// <summary>
    ///  Gets a value indicating whether the selection is empty (inactive or zero-length).
    /// </summary>
    public bool IsEmpty => !IsActive || Anchor == Head;

    /// <summary>
    ///  Begins a new selection at the specified grid cell coordinates.
    /// </summary>
    public void Begin(int column, int row)
    {
        Anchor = (column, row);
        Head = (column, row);
        IsActive = true;
    }

    /// <summary>
    ///  Extends the active selection endpoint to the specified grid cell coordinates.
    /// </summary>
    public void Extend(int column, int row)
    {
        if (!IsActive)
        {
            Begin(column, row);
            return;
        }

        Head = (column, row);
    }

    /// <summary>
    ///  Clears the active selection and resets coordinates.
    /// </summary>
    public void Clear()
    {
        IsActive = false;
        Anchor = default;
        Head = default;
    }

    /// <summary>
    ///  Determines whether the specified grid cell coordinates fall within the active selection.
    /// </summary>
    public bool Contains(int column, int row)
    {
        if (!IsActive)
        {
            return false;
        }

        var (start, end) = Normalized;

        if (row < start.Row || row > end.Row)
        {
            return false;
        }

        if (row == start.Row && column < start.Col)
        {
            return false;
        }

        if (row == end.Row && column > end.Col)
        {
            return false;
        }

        return true;
    }
}
