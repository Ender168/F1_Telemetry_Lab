using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace F1TelemetryLab;

public sealed class RaceEngineerOverlayWindow : Window
{
    private sealed class WidgetView
    {
        public required string Id { get; init; }
        public required double BaseWidth { get; init; }
        public required Border Border { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Value { get; init; }
        public required Control Editor { get; init; }
        public double Scale { get; set; } = 1;
    }

    private readonly bool _russian;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<string, WidgetView> _widgets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Border _editorBar;
    private OverlayLayout _layout = new();
    private string? _dragWidgetId;
    private Point _dragPointerStart;
    private Point _dragWidgetStart;
    private bool _editMode;
    private double _screenWidth = 1920;
    private double _screenHeight = 1080;

    public RaceEngineerOverlayWindow(bool russian)
    {
        _russian = russian;
        Title = "F1 Telemetry Lab Race Engineer";
        Width = _screenWidth;
        Height = _screenHeight;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        AddWidget("laps", russian ? "ПОСЛЕДНИЕ КРУГИ" : "LAST LAPS", 390);
        AddWidget("tyres", russian ? "ШИНЫ" : "TYRES", 390);
        AddWidget("pit", russian ? "ПИТ-СТОП" : "PIT STOP", 420);
        AddWidget("ers-energy", russian ? "ERS · ЭНЕРГИЯ" : "ERS · ENERGY", 445);
        AddWidget("ers-tactical", russian ? "ERS · СИТУАЦИЯ" : "ERS · TACTICAL", 360);
        AddWidget("ers-action", russian ? "ERS · КОМАНДА" : "ERS · ACTION", 445);

        _editorBar = BuildEditorBar();
        _canvas.Children.Add(_editorBar);
        Canvas.SetLeft(_editorBar, 20);
        Canvas.SetTop(_editorBar, 18);
        Content = _canvas;

        Opened += (_, _) =>
        {
            ConfigureForPrimaryScreen();
            SetEditMode(false);
        };
        Closed += (_, _) => SaveLayout();
        UpdateSnapshot(RaceEngineerSnapshot.Waiting);
    }

    public bool IsEditMode => _editMode;

    public void ToggleEditMode() => SetEditMode(!_editMode);

    public void SetEditMode(bool value)
    {
        _editMode = value;
        _editorBar.IsVisible = value;
        foreach (var widget in _widgets.Values) widget.Editor.IsVisible = value;
        WindowsOverlayInterop.SetClickThrough(this, !value);
        ShowActivated = value;
        if (value)
        {
            Activate();
            Topmost = true;
        }
        else
        {
            SaveLayout();
        }
    }

    public void UpdateSnapshot(RaceEngineerSnapshot snapshot)
    {
        var display = RaceEngineerText.Format(snapshot, _russian);
        SetWidget("laps", display.Laps, LapsColour(snapshot));
        SetWidget("tyres", display.Tyres, TyresColour(snapshot.Tyres));
        SetWidget("pit", display.Pit, PitColour(snapshot.Pit));
        SetWidget("ers-energy", RaceEngineerText.FormatErsEnergy(snapshot.Ers, _russian), EnergyColour(snapshot.Ers));
        SetWidget("ers-tactical", RaceEngineerText.FormatErsTactical(snapshot.Ers, _russian), TacticalColour(snapshot.Ers));
        SetWidget("ers-action", RaceEngineerText.FormatErsAction(snapshot.Ers, _russian), ActionColour(snapshot.Ers));

        ToolTip.SetTip(_widgets["tyres"].Border, snapshot.Tyres.Reason);
        ToolTip.SetTip(_widgets["pit"].Border, snapshot.Pit.Reason);
        ToolTip.SetTip(_widgets["ers-energy"].Border, snapshot.Ers.Reason);
        ToolTip.SetTip(_widgets["ers-tactical"].Border, snapshot.Ers.Reason);
        ToolTip.SetTip(_widgets["ers-action"].Border, snapshot.Ers.Reason);
    }

    private void ConfigureForPrimaryScreen()
    {
        var screen = Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var scale = Math.Max(0.5, screen.Scaling);
            Position = area.Position;
            _screenWidth = area.Width / scale;
            _screenHeight = area.Height / scale;
            Width = _screenWidth;
            Height = _screenHeight;
        }
        _canvas.Width = _screenWidth;
        _canvas.Height = _screenHeight;
        _layout = OverlayLayoutService.Load(_screenWidth, _screenHeight);
        ApplyLayout();
    }

    private void AddWidget(string id, string titleText, double baseWidth)
    {
        var title = new TextBlock
        {
            Text = titleText,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = Cyan
        };
        var value = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        var smaller = SmallButton("−");
        var larger = SmallButton("+");
        var hide = SmallButton("×");
        var editor = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { smaller, larger, hide }
        };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(title);
        Grid.SetColumn(editor, 1);
        heading.Children.Add(editor);
        var border = new Border
        {
            Width = baseWidth,
            Background = PanelBackground,
            BorderBrush = Cyan,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9),
            Child = new StackPanel { Spacing = 5, Children = { heading, value } }
        };
        var widget = new WidgetView
        {
            Id = id,
            BaseWidth = baseWidth,
            Border = border,
            Title = title,
            Value = value,
            Editor = editor
        };
        _widgets.Add(id, widget);
        _canvas.Children.Add(border);

        smaller.Click += (_, _) => ChangeScale(id, -0.1);
        larger.Click += (_, _) => ChangeScale(id, 0.1);
        hide.Click += (_, _) => SetVisible(id, false);
        border.PointerPressed += (_, args) => StartDrag(id, args);
        border.PointerMoved += (_, args) => ContinueDrag(id, args);
        border.PointerReleased += (_, args) => FinishDrag(id, args);
        border.PointerWheelChanged += (_, args) =>
        {
            if (!_editMode) return;
            ChangeScale(id, args.Delta.Y > 0 ? 0.05 : -0.05);
            args.Handled = true;
        };
    }

    private Border BuildEditorBar()
    {
        var reset = new Button { Content = _russian ? "Сбросить" : "Reset", MinWidth = 85 };
        var showAll = new Button { Content = _russian ? "Показать все" : "Show all", MinWidth = 105 };
        var lockLayout = new Button { Content = _russian ? "Закрепить" : "Lock", MinWidth = 90 };
        reset.Click += (_, _) =>
        {
            _layout = OverlayLayoutService.Default(_screenWidth, _screenHeight);
            ApplyLayout();
            SaveLayout();
        };
        showAll.Click += (_, _) =>
        {
            foreach (var item in _layout.Widgets) item.Visible = true;
            ApplyLayout();
            SaveLayout();
        };
        lockLayout.Click += (_, _) => SetEditMode(false);
        return new Border
        {
            Background = Brush(0xF21A202A),
            BorderBrush = Cyan,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = _russian ? "РАСКЛАДКА OVERLAY" : "OVERLAY LAYOUT",
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    },
                    reset,
                    showAll,
                    lockLayout
                }
            }
        };
    }

    private void ApplyLayout()
    {
        foreach (var item in _layout.Widgets)
        {
            if (!_widgets.TryGetValue(item.Id, out var widget)) continue;
            widget.Scale = Math.Clamp(item.Scale, 0.75, 1.75);
            widget.Border.IsVisible = item.Visible;
            Canvas.SetLeft(widget.Border, Math.Clamp(item.X, 0, Math.Max(0, _screenWidth - 120)));
            Canvas.SetTop(widget.Border, Math.Clamp(item.Y, 0, Math.Max(0, _screenHeight - 60)));
            ApplyScale(widget);
        }
    }

    private void ChangeScale(string id, double delta)
    {
        if (!_widgets.TryGetValue(id, out var widget)) return;
        widget.Scale = Math.Clamp(widget.Scale + delta, 0.75, 1.75);
        LayoutFor(id).Scale = widget.Scale;
        ApplyScale(widget);
        SaveLayout();
    }

    private static void ApplyScale(WidgetView widget)
    {
        widget.Border.Width = widget.BaseWidth * widget.Scale;
        widget.Title.FontSize = 12 * widget.Scale;
        widget.Value.FontSize = 17 * widget.Scale;
    }

    private void SetVisible(string id, bool visible)
    {
        if (!_widgets.TryGetValue(id, out var widget)) return;
        widget.Border.IsVisible = visible;
        LayoutFor(id).Visible = visible;
        SaveLayout();
    }

    private void StartDrag(string id, PointerPressedEventArgs args)
    {
        if (!_editMode || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragWidgetId = id;
        _dragPointerStart = args.GetPosition(_canvas);
        var widget = _widgets[id];
        _dragWidgetStart = new Point(Canvas.GetLeft(widget.Border), Canvas.GetTop(widget.Border));
        args.Pointer.Capture(widget.Border);
        args.Handled = true;
    }

    private void ContinueDrag(string id, PointerEventArgs args)
    {
        if (!_editMode || !string.Equals(_dragWidgetId, id, StringComparison.OrdinalIgnoreCase)) return;
        var point = args.GetPosition(_canvas);
        var x = Math.Clamp(_dragWidgetStart.X + point.X - _dragPointerStart.X, 0, Math.Max(0, _screenWidth - 120));
        var y = Math.Clamp(_dragWidgetStart.Y + point.Y - _dragPointerStart.Y, 0, Math.Max(0, _screenHeight - 60));
        var widget = _widgets[id];
        Canvas.SetLeft(widget.Border, x);
        Canvas.SetTop(widget.Border, y);
        var layout = LayoutFor(id);
        layout.X = x;
        layout.Y = y;
        args.Handled = true;
    }

    private void FinishDrag(string id, PointerReleasedEventArgs args)
    {
        if (!string.Equals(_dragWidgetId, id, StringComparison.OrdinalIgnoreCase)) return;
        args.Pointer.Capture(null);
        _dragWidgetId = null;
        SaveLayout();
        args.Handled = true;
    }

    private OverlayWidgetLayout LayoutFor(string id)
    {
        var result = _layout.Widgets.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (result is not null) return result;
        result = new OverlayWidgetLayout { Id = id };
        _layout.Widgets.Add(result);
        return result;
    }

    private void SaveLayout()
    {
        try
        {
            _layout.ScreenWidth = _screenWidth;
            _layout.ScreenHeight = _screenHeight;
            OverlayLayoutService.Save(_layout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The overlay remains usable even when its optional layout cannot be persisted.
        }
    }

    private void SetWidget(string id, string text, IBrush colour)
    {
        var widget = _widgets[id];
        widget.Value.Text = text;
        widget.Border.BorderBrush = colour;
        widget.Title.Foreground = colour;
    }

    private static IBrush LapsColour(RaceEngineerSnapshot snapshot)
    {
        var laps = snapshot.LastLaps.TakeLast(2).ToArray();
        if (laps.Length == 0) return Cyan;
        if (!laps[^1].Clean) return Red;
        return laps.Length == 2 && laps[^1].LapTimeMs <= laps[^2].LapTimeMs ? Green : Amber;
    }

    private static IBrush TyresColour(TyreLifeAdvice value)
    {
        if (!value.Available) return Cyan;
        var ratio = value.WorstWearPct / Math.Max(1, value.SafeWearLimitPct);
        return ratio switch
        {
            >= 0.93 => Red,
            >= 0.80 => Orange,
            >= 0.60 => Amber,
            _ => Green
        };
    }

    private static IBrush PitColour(PitPositionAdvice value) => !value.Available
        ? Cyan
        : value.NearbyCars >= 3 ? Red : value.NearbyCars >= 1 ? Amber : Green;

    private static IBrush EnergyColour(ErsRaceAdvice value) => value.EnergyState switch
    {
        ErsEnergyState.Critical => Red,
        ErsEnergyState.Conserve => Amber,
        ErsEnergyState.Surplus => Green,
        _ => Cyan
    };

    private static IBrush TacticalColour(ErsRaceAdvice value) => value.TacticalMode switch
    {
        ErsTacticalMode.Attack => Magenta,
        ErsTacticalMode.Defend => Orange,
        _ => Cyan
    };

    private static IBrush ActionColour(ErsRaceAdvice value) => value.TargetMode switch
    {
        ErsDeployMode.Boost => Magenta,
        ErsDeployMode.Hotlap => Orange,
        ErsDeployMode.None => Green,
        _ => Cyan
    };

    private static Button SmallButton(string content) => new()
    {
        Content = content,
        Width = 26,
        Height = 24,
        Padding = new Thickness(0),
        FontWeight = FontWeight.Bold
    };

    private static readonly IBrush PanelBackground = Brush(0xE8141820);
    private static readonly IBrush Cyan = Brush(0xFF31D7FF);
    private static readonly IBrush Green = Brush(0xFF42FF9A);
    private static readonly IBrush Amber = Brush(0xFFFFD43B);
    private static readonly IBrush Orange = Brush(0xFFFF8A2B);
    private static readonly IBrush Red = Brush(0xFFFF3B55);
    private static readonly IBrush Magenta = Brush(0xFFFF42D0);

    private static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF)));
}

internal static class WindowsOverlayInterop
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;

    public static void SetClickThrough(Window window, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle();
        if (handle is null || !string.Equals(handle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase)) return;
        var current = GetWindowLongPtr(handle.Handle, GwlExStyle).ToInt64();
        var updated = enabled
            ? current | WsExTransparent | WsExNoActivate
            : current & ~WsExTransparent & ~WsExNoActivate;
        SetWindowLongPtr(handle.Handle, GwlExStyle, new IntPtr(updated));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
