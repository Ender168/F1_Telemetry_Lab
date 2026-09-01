namespace F1TelemetryLab;

/// <summary>
/// Linear interpolation for telemetry series ordered by travelled distance.
/// A maximum gap prevents the UI from drawing invented data across long packet losses.
/// </summary>
public static class DistanceSeriesInterpolator
{
    public static double? Linear<T>(
        IReadOnlyList<T> sortedSource,
        double distanceM,
        Func<T, double> distanceSelector,
        Func<T, double> valueSelector,
        double maximumGapM = 100)
    {
        if (sortedSource.Count == 0 || maximumGapM <= 0) return null;

        var firstDistance = distanceSelector(sortedSource[0]);
        var lastDistance = distanceSelector(sortedSource[^1]);
        if (distanceM < firstDistance || distanceM > lastDistance) return null;

        var low = 0;
        var high = sortedSource.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var pointDistance = distanceSelector(sortedSource[middle]);
            if (Math.Abs(pointDistance - distanceM) < 0.000001)
                return FiniteOrNull(valueSelector(sortedSource[middle]));
            if (pointDistance < distanceM) low = middle + 1;
            else high = middle - 1;
        }

        if (high < 0 || low >= sortedSource.Count) return null;
        var before = sortedSource[high];
        var after = sortedSource[low];
        var beforeDistance = distanceSelector(before);
        var afterDistance = distanceSelector(after);
        var span = afterDistance - beforeDistance;
        if (span <= 0 || span > maximumGapM) return null;

        var beforeValue = valueSelector(before);
        var afterValue = valueSelector(after);
        if (!double.IsFinite(beforeValue) || !double.IsFinite(afterValue)) return null;
        var fraction = (distanceM - beforeDistance) / span;
        return beforeValue + (afterValue - beforeValue) * fraction;
    }

    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;
}
