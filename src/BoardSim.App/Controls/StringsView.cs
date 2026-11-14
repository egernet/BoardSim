using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BoardSim.Core.Emulation;

namespace BoardSim.App.Controls;

/// <summary>The LED strings the firmware drives, one glowing row per connector pin.</summary>
public sealed class StringsView : Control
{
    private static readonly Typeface Font = new(new FontFamily("Inter, Segoe UI, Helvetica, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);
    private List<(string Label, LedColor[] Leds, bool Stale)> _rows = new();

    public void Update(IEnumerable<LedString> strings)
    {
        var now = DateTime.Now;
        _rows = strings.OrderBy(s => s.Pin.Part.Reference.Length).ThenBy(s => s.Pin.Part.Reference)
            .Select(s => ($"{s.Pin.Part.Reference} · {s.Pin.Net.DisplayName}", s.Leds.ToArray(), (now - s.LastFrame).TotalSeconds > 2))
            .ToList();
        Height = _rows.Count == 0 ? 0 : 34 + _rows.Count * 30;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (_rows.Count == 0) return;
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0d1016"), 0.92), new Pen(new SolidColorBrush(Color.Parse("#2a313d")), 1), bounds, 12, 12);
        var title = new FormattedText("LED strings", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 12, new SolidColorBrush(Color.Parse("#8b93a1")));
        ctx.DrawText(title, new Point(14, 9));

        const double labelWidth = 120;
        var y = 34.0;
        foreach (var (label, leds, stale) in _rows)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 12,
                new SolidColorBrush(Color.Parse(stale ? "#5b6270" : "#e6e9ef")));
            ctx.DrawText(text, new Point(14, y + 15 - text.Height / 2));
            var n = Math.Max(leds.Length, 1);
            var step = Math.Max(2.5, (Bounds.Width - labelWidth - 24) / n);
            var r = Math.Clamp(step * 0.36, 1.2, 6);
            for (var i = 0; i < leds.Length; i++)
            {
                var l = leds[i];
                // White channel folded in, as WS2812 strings do in the firmware.
                var c = Color.FromRgb((byte)Math.Min(255, l.R + l.W), (byte)Math.Min(255, l.G + l.W), (byte)Math.Min(255, l.B + l.W));
                var x = labelWidth + i * step + step / 2;
                var center = new Point(x, y + 15);
                var level = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
                if (level > 0.02 && !stale)
                {
                    var glow = new RadialGradientBrush
                    {
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb((byte)(160 * level), c.R, c.G, c.B), 0),
                            new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                        },
                    };
                    ctx.DrawEllipse(glow, null, center, r * 3, r * 3);
                }
                var dot = level > 0.02 ? Boost(c) : Color.Parse("#22262e");
                ctx.DrawEllipse(new SolidColorBrush(dot, stale ? 0.4 : 1), null, center, r, r);
            }
            y += 30;
        }
    }

    /// <summary>LEDs look brighter than their PWM value; lift dim colours so they read on screen.</summary>
    private static Color Boost(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max == 0) return c;
        var k = Math.Min(255.0 / max, 1 + (255 - max) / 255.0 * 2.2);
        return Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k));
    }
}
