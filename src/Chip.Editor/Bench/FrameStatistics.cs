namespace Chip.Editor.Bench;

public readonly record struct FrameDistribution(double MedianMs, double P95Ms, double MaxMs);

public static class FrameStatistics
{
    public static FrameDistribution Summarize(double[] samples)
    {
        if (samples.Length == 0) throw new ArgumentException("At least one frame is required", nameof(samples));
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        double median = sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        return new(median, sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
    }
}
