using System.Globalization;

namespace IISMonitor.Core.Metrics;

public static class MetricFormatter
{
    public const string Missing = "—";

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(MetricUnit unit, double? value)
    {
        if (value is not { } v || double.IsNaN(v))
            return Missing;

        return unit switch
        {
            MetricUnit.Percent => v.ToString("0.0", CultureInfo.CurrentCulture) + " %",
            MetricUnit.Bytes => Bytes(v),
            MetricUnit.BytesPerSecond => Bytes(v) + "/s",
            MetricUnit.Count => v.ToString(v % 1 == 0 ? "N0" : "N1", CultureInfo.CurrentCulture),
            MetricUnit.PerSecond => v.ToString(v >= 100 ? "N0" : "0.0", CultureInfo.CurrentCulture) + "/s",
            MetricUnit.Milliseconds => v >= 10_000
                ? (v / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " s"
                : v.ToString("N0", CultureInfo.CurrentCulture) + " ms",
            _ => v.ToString(CultureInfo.CurrentCulture),
        };
    }

    public static string Bytes(double bytes)
    {
        var unit = 0;
        var value = Math.Abs(bytes);
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : value >= 100 ? "0" : "0.0";
        return (bytes < 0 ? "-" : "") + value.ToString(format, CultureInfo.CurrentCulture) + " " + ByteUnits[unit];
    }

    /// <summary>Divisor and suffix for a chart axis showing values of this unit.</summary>
    public static (double Divisor, string Label) AxisScale(MetricUnit unit, double maxValue) => unit switch
    {
        MetricUnit.Bytes or MetricUnit.BytesPerSecond => ByteScale(maxValue, unit == MetricUnit.BytesPerSecond ? "/s" : ""),
        MetricUnit.Percent => (1, "%"),
        MetricUnit.Milliseconds => (1, "ms"),
        MetricUnit.PerSecond => (1, "per sec"),
        _ => (1, ""),
    };

    private static (double, string) ByteScale(double maxValue, string suffix)
    {
        var unit = 0;
        var divisor = 1.0;
        while (maxValue / divisor >= 1024 && unit < ByteUnits.Length - 1)
        {
            divisor *= 1024;
            unit++;
        }

        return (divisor, ByteUnits[unit] + suffix);
    }
}
