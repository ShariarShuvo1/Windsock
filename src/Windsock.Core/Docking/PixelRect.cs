namespace Windsock.Core.Docking;

/// <summary>
/// A rectangle in physical screen pixels, written the way Windows writes a
/// RECT: the left and top edges are inside it, the right and bottom are not.
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>How wide it is, never less than zero.</summary>
    public int Width => Math.Max(0, Right - Left);

    /// <summary>How tall it is, never less than zero.</summary>
    public int Height => Math.Max(0, Bottom - Top);

    /// <summary>Whether it covers no pixels at all.</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>The middle, across.</summary>
    public int CentreX => Left + ((Right - Left) / 2);

    /// <summary>The middle, down.</summary>
    public int CentreY => Top + ((Bottom - Top) / 2);

    /// <summary>A rectangle from its corner and its size.</summary>
    public static PixelRect FromSize(int left, int top, int width, int height) =>
        new(left, top, left + width, top + height);

    /// <summary>The same rectangle, moved.</summary>
    public PixelRect Offset(int across, int down) =>
        new(Left + across, Top + down, Right + across, Bottom + down);

    /// <summary>The same rectangle in the coordinates of another's corner.</summary>
    public PixelRect RelativeTo(PixelRect origin) => Offset(-origin.Left, -origin.Top);

    /// <summary>Whether a point is inside.</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Whether another rectangle lies wholly inside this one.</summary>
    public bool Contains(PixelRect other) =>
        !other.IsEmpty
        && other.Left >= Left && other.Top >= Top
        && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>The part the two have in common, empty when they do not meet.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        PixelRect common = new(
            Math.Max(Left, other.Left),
            Math.Max(Top, other.Top),
            Math.Min(Right, other.Right),
            Math.Min(Bottom, other.Bottom));

        return common.IsEmpty ? default : common;
    }

    /// <summary>Whether the two share any pixel.</summary>
    public bool Overlaps(PixelRect other) => !Intersect(other).IsEmpty;

    /// <summary>How many pixels it covers.</summary>
    public long Area => (long)Width * Height;

    /// <summary>
    /// The nearest place for a rectangle of this size inside
    /// <paramref name="room"/>, moved as little as it can be. A rectangle
    /// larger than the room keeps its top-left corner in it.
    /// </summary>
    public PixelRect Within(PixelRect room)
    {
        int left = Math.Max(room.Left, Math.Min(Left, room.Right - Width));
        int top = Math.Max(room.Top, Math.Min(Top, room.Bottom - Height));

        return FromSize(left, top, Width, Height);
    }
}
