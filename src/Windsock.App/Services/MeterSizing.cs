using System.Windows;
using Windsock.App.ViewModels;
using Windsock.App.Views;
using Windsock.Core.Docking;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>How a meter was laid out for a bar, and how big that made it.</summary>
/// <param name="Arrangement">How its readings are laid out.</param>
/// <param name="Length">How long it is along its bar, in pixels.</param>
/// <param name="Across">How thick it is across its bar, in pixels.</param>
/// <param name="Natural">The readings' own size, in device-independent pixels.</param>
public readonly record struct MeterFit(MeterArrangement Arrangement, int Length, int Across, Size Natural);

/// <summary>
/// Lays a meter's readings out for the bar they are on and works out how big
/// the meter is: stacked down a side, on one line on a thin bar, shrunk to fit
/// where even that is too wide.
/// </summary>
/// <remarks>
/// Public and on its own so the offscreen harness lays a meter out exactly as
/// the taskbar does, by calling this, rather than by a copy that drifts.
/// </remarks>
public static class MeterSizing
{
    /// <summary>Room beside the readings, as the meter has always had.</summary>
    public const double Padding = 4;

    /// <summary>Room above and below readings stacked down a vertical bar.</summary>
    public const double EndRoom = 4;

    /// <summary>
    /// How thick a floating meter is on a monitor without a taskbar: what it
    /// is on Windows 11's own bar at 100%, 48 less two insets of 3.
    /// </summary>
    public const double BareThickness = 42;

    /// <summary>The thickness a meter has across a monitor with no taskbar, at a DPI.</summary>
    public static int Bare(int dpi) => (int)Math.Round(BareThickness * Math.Max(96, dpi) / 96d);

    /// <summary>
    /// Lays the readings out for a bar on <paramref name="edge"/> (nothing for
    /// no bar) with <paramref name="across"/> pixels of thickness at
    /// <paramref name="dpi"/>, and says how big the meter is.
    /// </summary>
    public static MeterFit Fit(
        TaskbarMeterWindow window,
        TaskbarMeterViewModel model,
        TaskbarSettings settings,
        TaskbarEdge? edge,
        int across,
        int dpi)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);

        double scale = Math.Max(96, dpi) / 96d;
        double room = across / scale;
        bool vertical = edge is TaskbarEdge.Left or TaskbarEdge.Right;
        int places = settings.Rows * settings.Columns;
        bool[] filled = new bool[places];

        for (int each = 0; each < places; each++)
        {
            filled[each] = each < settings.Slots.Count && settings.Slots[each].IsFilled;
        }

        MeterArrangement arrangement = MeterLayout.Choose(edge, settings.Rows, settings.Columns, room, model.LineHeight);
        int[] order = MeterLayout.Order(arrangement, settings.Rows, settings.Columns, filled);
        (int rows, int columns) = MeterLayout.Shape(arrangement, settings.Rows, settings.Columns, order.Length);

        model.Shrink = 1;
        model.Arrange(order, rows, columns, unitBelow: vertical);

        Size natural = window.Natural();
        double shrink = vertical ? MeterLayout.Shrink(natural.Width, room) : MeterLayout.Shrink(natural.Height, room);

        if (shrink < 1)
        {
            model.Shrink = shrink;
            natural = window.Natural();

            // Margins do not shrink with the text, so one pass can land a
            // pixel or two over; a second, by what is still over, does not.
            double over = vertical ? natural.Width : natural.Height;

            if (over > room + 0.5 && shrink > MeterLayout.SmallestShrink)
            {
                model.Shrink = Math.Max(MeterLayout.SmallestShrink, Math.Floor(shrink * room / over * 100) / 100);
                natural = window.Natural();
            }
        }

        int length;

        if (vertical)
        {
            length = (int)Math.Ceiling((natural.Height + EndRoom) * scale);
        }
        else if (settings.FitToContents)
        {
            double wide = Math.Clamp(natural.Width + Padding, TaskbarSettings.MinimumWidth, TaskbarSettings.MaximumWidth);
            length = (int)(Math.Ceiling(wide * scale / 4) * 4);
        }
        else
        {
            length = (int)Math.Round(settings.Width * scale);
        }

        return new MeterFit(arrangement, length, across, natural);
    }
}
