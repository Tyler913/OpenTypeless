namespace TypelessCore;

/// <summary>Where the recording capsule shows while you dictate (General → Capsule position).</summary>
public enum HudPosition
{
    /// <summary>Centred near the bottom of the screen, above the taskbar or the Dock: where it always was, and the default.</summary>
    Bottom,
    /// <summary>Centred near the top of the screen, below the menu bar; the live preview and notices open downwards.</summary>
    Top,
    /// <summary>
    /// Just below the line being typed into, or above it when there's no room below, where UI Automation / Accessibility
    /// says where that is; at the bottom of the screen where it doesn't.
    /// </summary>
    Caret,
}

public static class HudPositions
{
    public static readonly HudPosition[] All = Enum.GetValues<HudPosition>();

    /// <summary>The name it's saved under, the same as on macOS.</summary>
    public static string RawValue(this HudPosition position) => position switch
    {
        HudPosition.Top => "top",
        HudPosition.Caret => "caret",
        _ => "bottom",
    };

    /// <summary>The saved choice; the bottom for none, or for one this version can't read (saved by a newer one, or damaged).</summary>
    public static HudPosition Parse(string? raw)
    {
        foreach (var position in All)
        {
            if (position.RawValue() == raw) return position;
        }
        return HudPosition.Bottom;
    }

    public static string Title(this HudPosition position) => position switch
    {
        HudPosition.Top => L("屏幕顶部", "Top of the screen"),
        HudPosition.Caret => L("光标附近", "Near the text cursor"),
        _ => L("屏幕底部", "Bottom of the screen"),
    };
}

/// <summary>
/// A rectangle on the screens with y growing downwards from the top of the main screen, as Windows and Accessibility
/// give them: physical pixels on Windows, points on macOS (which flips AppKit's frames into it).
/// </summary>
public readonly record struct HudRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MaxX => X + Width;
    public double MinY => Y;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    /// <summary>The area the two have in common; 0 when they don't overlap.</summary>
    public double Overlap(HudRect other) =>
        Math.Max(0, Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX)) * Math.Max(0, Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY));

    /// <summary>Whether the point is in it: its left and top edges are, its right and bottom edges belong to the next one.</summary>
    public bool Contains(HudPoint point) => MinX <= point.X && point.X < MaxX && MinY <= point.Y && point.Y < MaxY;
}

public readonly record struct HudPoint(double X, double Y);

public readonly record struct HudSize(double Width, double Height);

/// <summary>
/// Where the capsule goes: centred on <see cref="CenterX"/>, with one edge on <see cref="EdgeY"/>, growing away from it.
/// A capsule at the top (or below the text cursor) hangs from its top edge, so the live preview and a notice that wraps
/// open downwards; one at the bottom (or above the cursor) stands on its bottom edge and they open upwards. Either way
/// the edge nearest the screen's edge, or the line being typed into, stays put.
/// </summary>
/// <param name="GrowsDown">
/// The capsule's top edge is on <see cref="EdgeY"/> and it grows downwards, the live preview below it; else its bottom
/// edge is, and it grows upwards, the preview above it.
/// </param>
public readonly record struct HudAnchor(double CenterX, double EdgeY, bool GrowsDown)
{
    /// <summary>The capsule's frame at its size now.</summary>
    public HudRect Frame(double width, double height) => new(CenterX - width / 2, GrowsDown ? EdgeY : EdgeY - height, width, height);
}

/// <summary>
/// Where the recording capsule goes on the screen: which screen, and where on it. Pure arithmetic, the same on both
/// platforms (testdata/hud-placement-cases.json); each app measures the screens, the capsule and the text cursor itself.
/// </summary>
public static class HudPlacement
{
    /// <summary>The platform's distances, in the units of the rectangles.</summary>
    /// <param name="Inset">
    /// From the edge of the screen's work area (above the taskbar, below the menu bar) to the capsule, at the top or the
    /// bottom.
    /// </param>
    /// <param name="Gap">Between the line being typed into and the capsule.</param>
    /// <param name="Margin">Kept clear inside the work area on every side, near the text cursor.</param>
    public readonly record struct Metrics(double Inset, double Gap, double Margin);

    /// <summary>
    /// The anchor for <paramref name="position"/> on the screen whose work area is <paramref name="area"/>.
    ///
    /// <paramref name="extent"/>: the most room the capsule takes from its edge, with the live preview or a notice of three
    /// lines, and whatever else the platform draws round it. Near the text cursor all of it must fit on the side it goes:
    /// below <paramref name="caret"/> (the line being typed into, the selection, or the focused field where that's all
    /// there is), else above it, so the capsule never covers it, and centred on it as far as the screen's sides allow.
    /// Without a cursor on this screen, or with no room on either side (a field as tall as the screen), it's the bottom
    /// of the screen.
    /// </summary>
    public static HudAnchor Anchor(HudPosition position, HudRect area, HudSize extent, HudRect? caret, Metrics metrics)
    {
        var bottom = new HudAnchor(area.MidX, area.MaxY - metrics.Inset, GrowsDown: false);
        switch (position)
        {
            case HudPosition.Bottom:
                return bottom;
            case HudPosition.Top:
                return new HudAnchor(area.MidX, area.MinY + metrics.Inset, GrowsDown: true);
            default:
                if (caret is not { } line || !IsUsable(line)
                    || !(area.MinX <= line.MidX && line.MidX <= area.MaxX && area.MinY <= line.MidY && line.MidY <= area.MaxY))
                {
                    return bottom;
                }
                var low = area.MinX + metrics.Margin + extent.Width / 2;
                var high = area.MaxX - metrics.Margin - extent.Width / 2;
                var centerX = low <= high ? Math.Min(Math.Max(line.MidX, low), high) : area.MidX;
                var below = line.MaxY + metrics.Gap;
                if (below + extent.Height <= area.MaxY - metrics.Margin) return new HudAnchor(centerX, below, GrowsDown: true);
                var above = line.MinY - metrics.Gap;
                if (above - extent.Height >= area.MinY + metrics.Margin) return new HudAnchor(centerX, above, GrowsDown: false);
                return bottom;
        }
    }

    /// <summary>
    /// Which of <paramref name="screens"/> (their whole frames) the capsule goes on: the one with the text cursor in it,
    /// else the one holding most of the focused window, else the one under the mouse. Null when none of them does: the
    /// caller takes the main screen.
    /// </summary>
    public static int? Screen(IReadOnlyList<HudRect> screens, HudRect? caret, HudRect? window, HudPoint? mouse)
    {
        if (caret is { } line && IsUsable(line))
        {
            for (var i = 0; i < screens.Count; i++)
            {
                if (screens[i].Contains(new HudPoint(line.MidX, line.MidY))) return i;
            }
        }
        if (window is { Width: > 0, Height: > 0 } focused)
        {
            int? best = null;
            var most = 0.0;
            for (var i = 0; i < screens.Count; i++)
            {
                var shared = screens[i].Overlap(focused);
                if (shared > most)
                {
                    most = shared;
                    best = i;
                }
            }
            if (best != null) return best;
        }
        if (mouse is { } point)
        {
            for (var i = 0; i < screens.Count; i++)
            {
                if (screens[i].Contains(point)) return i;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a rectangle read for the text cursor says anything: finite, and as tall as a line at least a little. An app
    /// that can't tell often answers with an empty rectangle instead.
    /// </summary>
    private static bool IsUsable(HudRect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && rect.Width >= 0 && rect.Height > 0;
}
