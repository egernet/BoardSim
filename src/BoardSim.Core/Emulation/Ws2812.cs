namespace BoardSim.Core.Emulation;

/// <summary>An LED colour as sent on the wire, already put in R, G, B (and W) order.</summary>
public readonly record struct LedColor(byte R, byte G, byte B, byte W = 0)
{
    public static readonly LedColor Off = new(0, 0, 0);
}

/// <summary>
/// Decodes WS2812/SK6812 bit streams from RMT items. Each item is a high time
/// followed by a low time; a long high is a 1, a short high a 0, and a long low
/// (over 5 µs) is the reset that ends the frame.
/// </summary>
public static class Ws2812
{
    /// <summary>Bits per LED: 24 for RGB strings, 32 for RGBW.</summary>
    public static IReadOnlyList<LedColor> Decode(IReadOnlyList<uint> items, double tickNs, string order = "GRB")
    {
        var bits = new List<bool>(items.Count);
        foreach (var item in items)
        {
            var d0 = item & 0x7fff;
            var l0 = (item >> 15) & 1;
            var d1 = (item >> 16) & 0x7fff;
            // Normalise to (high time, low time); some encoders start low.
            var (high, low) = l0 == 1 ? (d0, d1) : (d1, d0);
            var highNs = high * tickNs;
            var lowNs = low * tickNs;
            if (highNs >= 150 && highNs <= 2000) bits.Add(highNs > 600);
            if (lowNs > 5000 || high == 0) break;
        }

        var perLed = order.Length * 8;
        var leds = new List<LedColor>(bits.Count / perLed);
        for (var i = 0; i + perLed <= bits.Count; i += perLed)
        {
            byte r = 0, g = 0, b = 0, w = 0;
            for (var c = 0; c < order.Length; c++)
            {
                byte value = 0;
                for (var k = 0; k < 8; k++) value = (byte)((value << 1) | (bits[i + c * 8 + k] ? 1 : 0));
                switch (char.ToUpperInvariant(order[c]))
                {
                    case 'R': r = value; break;
                    case 'G': g = value; break;
                    case 'B': b = value; break;
                    case 'W': w = value; break;
                }
            }
            leds.Add(new LedColor(r, g, b, w));
        }
        return leds;
    }
}
