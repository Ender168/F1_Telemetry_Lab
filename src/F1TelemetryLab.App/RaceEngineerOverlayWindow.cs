using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace F1TelemetryLab;

public sealed class RaceEngineerOverlayWindow : Window
{
    private readonly bool _russian;
    private readonly TextBlock _laps = ValueBlock();
    private readonly TextBlock _tyres = ValueBlock();
    private readonly TextBlock _pit = ValueBlock();
    private readonly TextBlock _ers = ValueBlock();
    private readonly TextBlock _confidence = new() { Foreground = Brush(0xAEBBD0), FontSize = 12, TextWrapping = TextWrapping.Wrap };

    public RaceEngineerOverlayWindow(bool russian)
    {
        _russian = russian;
        Title = "F1 Telemetry Lab Race Engineer";
        Width = 680;
        Height = 360;
        MinWidth = 520;
        MinHeight = 280;
        CanResize = true;
        Topmost = true;
        ShowInTaskbar = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Position = new PixelPoint(30, 80);

        var close = new Button
        {
            Content = "×",
            Width = 32,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brush(0x2A313C),
            Foreground = Brushes.White
        };
        close.Click += (_, _) => Hide();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = russian ? "Гоночный инженер" : "Race Engineer",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var content = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                header,
                Card(russian ? "Последние круги" : "Last laps", _laps),
                Card(russian ? "Шины" : "Tyres", _tyres),
                Card(russian ? "Пит-стоп" : "Pit stop", _pit),
                Card("ERS", _ers),
                _confidence
            }
        };
        Content = new Border
        {
            Background = Brush(0xE6161B22),
            BorderBrush = Brush(0x596B82),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
        PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
        };
        UpdateSnapshot(RaceEngineerSnapshot.Waiting);
    }

    public void UpdateSnapshot(RaceEngineerSnapshot snapshot)
    {
        var display = RaceEngineerText.Format(snapshot, _russian);
        _laps.Text = display.Laps;
        _tyres.Text = display.Tyres;
        _pit.Text = display.Pit;
        _ers.Text = display.Ers;
        _confidence.Text = display.Confidence;
        ToolTip.SetTip(_tyres, snapshot.Tyres.Reason);
        ToolTip.SetTip(_pit, snapshot.Pit.Reason);
        ToolTip.SetTip(_ers, snapshot.Ers.Reason);
    }

    private static Border Card(string title, TextBlock value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("115,*"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush(0xF4BF75),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return new Border
        {
            Background = Brush(0xD91D2630),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6),
            Child = grid
        };
    }

    private static TextBlock ValueBlock() => new()
    {
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF)));
}
