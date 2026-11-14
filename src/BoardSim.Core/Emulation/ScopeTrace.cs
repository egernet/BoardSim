using BoardSim.Core.Analysis;

namespace BoardSim.Core.Emulation;

/// <summary>What an oscilloscope probe on a net sees. Times are the chip's virtual time in ns.</summary>
public abstract record ScopeTrace
{
    /// <summary>Where the signal comes from, e.g. "U1.GPIO4" or "Y1 crystal".</summary>
    public string Source { get; init; } = "";
}

/// <summary>A steady level.</summary>
public sealed record DcTrace(double? Volts) : ScopeTrace;

/// <summary>A digital signal from a GPIO output: its edges, at the voltages this net sees.</summary>
public sealed record EdgeTrace(IReadOnlyList<(long T, bool High)> Edges, double High, double Low, long NowNs) : ScopeTrace;

/// <summary>The last RMT transmission (LED data) on this net.</summary>
public sealed record RmtTrace(EmulatedBoard.RmtCapture Frame, double High, double Low) : ScopeTrace;

/// <summary>A sine wave, e.g. a crystal oscillating.</summary>
public sealed record SineTrace(double Hz, double Min, double Max) : ScopeTrace;

/// <summary>What a scope's measure function would show.</summary>
public sealed record Measurement(double? Hz, double? DutyPercent, double? Min, double? Max)
{
    public static Measurement Of(ScopeTrace? trace) => trace switch
    {
        DcTrace d => new(null, null, d.Volts, d.Volts),
        SineTrace s => new(s.Hz, 50, s.Min, s.Max),
        RmtTrace r => Rmt(r),
        EdgeTrace e => Edges(e),
        _ => new(null, null, null, null),
    };

    private static Measurement Rmt(RmtTrace r)
    {
        double total = 0, high = 0;
        var n = 0;
        foreach (var item in r.Frame.Items)
        {
            var (h, l) = ItemHighLow(item);
            if (l * r.Frame.TickNs > 5000) continue;   // the reset pulse is not a bit
            total += h + l;
            high += h;
            n++;
        }
        if (n == 0 || total == 0) return new(null, null, r.Low, r.High);
        var periodNs = total / n * r.Frame.TickNs;
        return new(1e9 / periodNs, high / total * 100, r.Low, r.High);
    }

    /// <summary>Frequency from the median time between rising edges, duty from high time over the same span.</summary>
    private static Measurement Edges(EdgeTrace e)
    {
        var rises = new List<long>();
        for (var i = 1; i < e.Edges.Count; i++)
            if (e.Edges[i].High && !e.Edges[i - 1].High) rises.Add(e.Edges[i].T);
        if (rises.Count < 2) return new(null, null, e.Low, e.High);
        var periods = rises.Zip(rises.Skip(1), (a, b) => (double)(b - a)).Where(p => p > 0).OrderBy(p => p).ToList();
        if (periods.Count == 0) return new(null, null, e.Low, e.High);
        var period = periods[periods.Count / 2];
        double highNs = 0;
        for (var i = 0; i + 1 < e.Edges.Count; i++)
            if (e.Edges[i].High && e.Edges[i].T >= rises[0] && e.Edges[i + 1].T <= rises[^1])
                highNs += e.Edges[i + 1].T - e.Edges[i].T;
        var span = rises[^1] - rises[0];
        return new(1e9 / period, span > 0 ? highNs / span * 100 : null, e.Low, e.High);
    }

    public static (uint High, uint Low) ItemHighLow(uint item)
    {
        var d0 = item & 0x7fff;
        var l0 = (item >> 15) & 1;
        var d1 = (item >> 16) & 0x7fff;
        return l0 == 1 ? (d0, d1) : (d1, d0);
    }

    /// <summary>Time for about four periods, rounded up to a 1-2-5 step per division (10 divisions).</summary>
    public double? AutoPerDivSeconds()
    {
        if (Hz is not { } f || f <= 0) return null;
        return Snap(4 / f / 10);
    }

    public static double Snap(double perDiv)
    {
        var exp = Math.Floor(Math.Log10(perDiv));
        var b = Math.Pow(10, exp);
        foreach (var m in new[] { 1.0, 2, 5, 10 })
            if (perDiv <= m * b * 1.0001) return m * b;
        return 10 * b;
    }
}
