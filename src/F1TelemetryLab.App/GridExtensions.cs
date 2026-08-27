using Avalonia.Controls;

namespace F1TelemetryLab;

public static class GridExtensions
{
    public static T WithGridRow<T>(this T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
