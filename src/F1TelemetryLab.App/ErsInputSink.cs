using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace F1TelemetryLab;

public sealed record ErsInputResult(bool Success, bool Retryable, string Message)
{
    public static ErsInputResult Ok(string message) => new(true, false, message);
    public static ErsInputResult Wait(string message) => new(false, true, message);
    public static ErsInputResult Error(string message) => new(false, false, message);
}

public interface IErsInputSink : IDisposable
{
    ErsInputResult Tap(ErsInputDirection direction, ErsAutopilotOptions options, DateTimeOffset now);
    ErsInputResult? Poll(DateTimeOffset now);
    bool EmergencyStopRequested(ErsAutopilotOptions options);
}

public sealed class WindowsKeyboardErsInputSink : IErsInputSink
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint ScanCode = 0x0008;
    private const uint MapVkToVsc = 0;
    private readonly object _sync = new();
    private ushort _pressedScanCode;
    private int _pressedVirtualKey;
    private DateTimeOffset _releaseAt;
    private bool _disposed;

    public ErsInputResult Tap(ErsInputDirection direction, ErsAutopilotOptions options, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows())
            return ErsInputResult.Error("Live ERS input is available only in the Windows build.");

        lock (_sync)
        {
            if (_disposed) return ErsInputResult.Error("ERS keyboard input is already closed.");

            var release = ReleaseIfDue(now);
            if (release is { Success: false } releaseError) return releaseError;
            if (_pressedScanCode != 0)
                return ErsInputResult.Wait($"{ErsProfileStore.VirtualKeyName(_pressedVirtualKey)} is still held; waiting before another ERS command.");

            var foreground = ForegroundProcess();
            if (foreground is null)
                return ErsInputResult.Wait("No foreground process was detected; no key was sent.");
            if (!LooksLikeF1(foreground.Value.ProcessName, foreground.Value.WindowTitle))
                return ErsInputResult.Wait($"F1 25 is not the foreground window ({foreground.Value.ProcessName}); no key was sent.");

            var virtualKey = direction == ErsInputDirection.Increase
                ? options.IncreaseVirtualKey
                : options.DecreaseVirtualKey;
            if (virtualKey is < 1 or > byte.MaxValue)
                return ErsInputResult.Error($"Invalid virtual key: {virtualKey}.");

            var scanCode = (ushort)MapVirtualKey((uint)virtualKey, MapVkToVsc);
            if (scanCode == 0)
                return ErsInputResult.Error($"No hardware scan code exists for {ErsProfileStore.VirtualKeyName(virtualKey)}.");

            var down = new[] { KeyboardInput(scanCode, ScanCode) };
            var sent = SendInput(1, down, Marshal.SizeOf<Input>());
            if (sent != 1)
            {
                var error = Marshal.GetLastWin32Error();
                return ErsInputResult.Error($"SendInput key-down wrote {sent}/1 events: {new Win32Exception(error).Message}");
            }

            _pressedScanCode = scanCode;
            _pressedVirtualKey = virtualKey;
            _releaseAt = now.AddMilliseconds(Math.Clamp(options.KeyHoldMilliseconds, 30, 250));
            return ErsInputResult.Ok(
                $"Pressed {ErsProfileStore.VirtualKeyName(virtualKey)} scan-code 0x{scanCode:X2} for {Math.Clamp(options.KeyHoldMilliseconds, 30, 250)} ms ({direction}).");
        }
    }

    public ErsInputResult? Poll(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (_sync)
        {
            if (_disposed) return null;
            return ReleaseIfDue(now);
        }
    }

    public bool EmergencyStopRequested(ErsAutopilotOptions options) =>
        OperatingSystem.IsWindows() &&
        (GetAsyncKeyState(options.EmergencyStopVirtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_sync)
        {
            if (_disposed) return;
            if (_pressedScanCode != 0)
            {
                _ = SendInput(1, new[] { KeyboardInput(_pressedScanCode, ScanCode | KeyUp) }, Marshal.SizeOf<Input>());
                ClearPressedKey();
            }
            _disposed = true;
        }
    }

    private ErsInputResult? ReleaseIfDue(DateTimeOffset now)
    {
        if (_pressedScanCode == 0 || now < _releaseAt) return null;

        var scanCode = _pressedScanCode;
        var virtualKey = _pressedVirtualKey;
        var up = new[] { KeyboardInput(scanCode, ScanCode | KeyUp) };
        var sent = SendInput(1, up, Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            var error = Marshal.GetLastWin32Error();
            return ErsInputResult.Error($"SendInput key-up wrote {sent}/1 events: {new Win32Exception(error).Message}");
        }

        ClearPressedKey();
        return ErsInputResult.Ok($"Released {ErsProfileStore.VirtualKeyName(virtualKey)} scan-code 0x{scanCode:X2}.");
    }

    private void ClearPressedKey()
    {
        _pressedScanCode = 0;
        _pressedVirtualKey = 0;
        _releaseAt = default;
    }

    private static bool LooksLikeF1(string processName, string windowTitle) =>
        processName.Contains("F1_25", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("F1 25", StringComparison.OrdinalIgnoreCase) ||
        windowTitle.Contains("F1 25", StringComparison.OrdinalIgnoreCase) ||
        windowTitle.Contains("F1® 25", StringComparison.OrdinalIgnoreCase);

    private static (string ProcessName, string WindowTitle)? ForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return (process.ProcessName, process.MainWindowTitle ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static Input KeyboardInput(ushort scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = 0,
                ScanCode = scanCode,
                Flags = flags
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
