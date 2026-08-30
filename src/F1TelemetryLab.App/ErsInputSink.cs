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

public interface IErsInputSink
{
    ErsInputResult Tap(ErsInputDirection direction, ErsAutopilotOptions options);
    bool EmergencyStopRequested(ErsAutopilotOptions options);
}

public sealed class WindowsKeyboardErsInputSink : IErsInputSink
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

    public ErsInputResult Tap(ErsInputDirection direction, ErsAutopilotOptions options)
    {
        if (!OperatingSystem.IsWindows())
            return ErsInputResult.Error("Live ERS input is available only in the Windows build.");

        var foreground = ForegroundProcess();
        if (foreground is null)
            return ErsInputResult.Wait("No foreground process was detected; no key was sent.");
        if (!LooksLikeF1(foreground.Value.ProcessName, foreground.Value.WindowTitle))
            return ErsInputResult.Wait($"F1 25 is not the foreground window ({foreground.Value.ProcessName}); no key was sent.");

        var virtualKey = direction == ErsInputDirection.Increase
            ? options.IncreaseVirtualKey
            : options.DecreaseVirtualKey;
        if (virtualKey is < 1 or > ushort.MaxValue)
            return ErsInputResult.Error($"Invalid virtual key: {virtualKey}.");

        var inputs = new[]
        {
            KeyboardInput((ushort)virtualKey, 0),
            KeyboardInput((ushort)virtualKey, KeyUp)
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != (uint)inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            return ErsInputResult.Error($"SendInput wrote {sent}/{inputs.Length} events: {new Win32Exception(error).Message}");
        }

        return ErsInputResult.Ok($"Tapped {ErsProfileStore.VirtualKeyName(virtualKey)} ({direction}).");
    }

    public bool EmergencyStopRequested(ErsAutopilotOptions options) =>
        OperatingSystem.IsWindows() &&
        (GetAsyncKeyState(options.EmergencyStopVirtualKey) & 0x8000) != 0;

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

    private static Input KeyboardInput(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
