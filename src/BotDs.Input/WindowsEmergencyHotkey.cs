using System.Runtime.InteropServices;

namespace BotDs.Input;

/// <summary>
/// Registers a process-global emergency hotkey on a dedicated message-only window thread.
/// </summary>
public sealed class WindowsEmergencyHotkey : IEmergencyHotkey
{
    private const int HotkeyId = 0x42D5; // arbitrary app-local id
    private const int WmHotkey = 0x0312;
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private nint _hwnd;
    private Action? _onTriggered;
    private volatile bool _registered;
    private volatile bool _disposeRequested;
    private string? _lastError;
    private bool _disposed;

    public WindowsEmergencyHotkey(string binding = "Ctrl+Shift+F12")
    {
        Binding = string.IsNullOrWhiteSpace(binding) ? "Ctrl+Shift+F12" : binding.Trim();
    }

    public string Binding { get; }
    public bool IsRegistered => _registered;
    public string? LastError => _lastError;

    public bool TryRegister(Action onTriggered)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onTriggered);

        if (!VirtualKeyMap.TryParseHotkey(Binding, out uint modifiers, out ushort vk, out string? parseError))
        {
            _lastError = parseError;
            return false;
        }

        lock (_lock)
        {
            UnregisterUnlocked();
            _onTriggered = onTriggered;
            _disposeRequested = false;
            _ready.Reset();
            _lastError = null;

            _thread = new Thread(() => MessageLoop(modifiers, vk))
            {
                IsBackground = true,
                Name = "BotDs.EmergencyHotkey",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                _lastError = "Emergency hotkey message thread did not start in time.";
                UnregisterUnlocked();
                return false;
            }

            if (!_registered)
            {
                _lastError ??= "Emergency hotkey registration failed.";
                UnregisterUnlocked();
                return false;
            }

            return true;
        }
    }

    public void Unregister()
    {
        lock (_lock) UnregisterUnlocked();
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            UnregisterUnlocked();
            _disposed = true;
        }
        _ready.Dispose();
    }

    private void UnregisterUnlocked()
    {
        _disposeRequested = true;
        nint hwnd = Interlocked.Exchange(ref _hwnd, 0);
        if (hwnd != 0)
        {
            // Post close so the message loop can unregister and exit cleanly.
            _ = NativeHotkey.PostMessage(hwnd, WmClose, 0, 0);
        }

        Thread? thread = _thread;
        _thread = null;
        if (thread is not null && thread.IsAlive)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                // Best-effort; thread is background and will exit on process end.
            }
        }

        _registered = false;
        _onTriggered = null;
    }

    private void MessageLoop(uint modifiers, ushort virtualKey)
    {
        try
        {
            // Message-only window (parent HWND_MESSAGE = -3).
            nint hwnd = NativeHotkey.CreateWindowEx(
                0,
                "Message",
                "BotDs.EmergencyHotkey",
                0,
                0, 0, 0, 0,
                new nint(-3),
                0,
                NativeHotkey.GetModuleHandle(null),
                0);

            if (hwnd == 0)
            {
                // Fallback: create a tiny hidden overlapped window if Message class fails.
                hwnd = NativeHotkey.CreateWindowEx(
                    0,
                    "STATIC",
                    "BotDs.EmergencyHotkey",
                    0,
                    0, 0, 0, 0,
                    0,
                    0,
                    NativeHotkey.GetModuleHandle(null),
                    0);
            }

            if (hwnd == 0)
            {
                _lastError = $"CreateWindowEx failed (win32={Marshal.GetLastWin32Error()}).";
                _ready.Set();
                return;
            }

            Interlocked.Exchange(ref _hwnd, hwnd);

            if (!NativeHotkey.RegisterHotKey(hwnd, HotkeyId, modifiers, virtualKey))
            {
                _lastError = $"RegisterHotKey failed for '{Binding}' (win32={Marshal.GetLastWin32Error()}). Another app may own this hotkey.";
                _ = NativeHotkey.DestroyWindow(hwnd);
                Interlocked.Exchange(ref _hwnd, 0);
                _ready.Set();
                return;
            }

            _registered = true;
            _ready.Set();

            while (!_disposeRequested && NativeHotkey.GetMessage(out NativeHotkey.MSG msg, 0, 0, 0) > 0)
            {
                if (msg.message == WmHotkey && msg.wParam == HotkeyId)
                {
                    Action? handler = _onTriggered;
                    try { handler?.Invoke(); }
                    catch
                    {
                        // Handlers must not tear down the message pump.
                    }
                    continue;
                }

                if (msg.message is WmClose or WmDestroy)
                    break;

                _ = NativeHotkey.TranslateMessage(ref msg);
                _ = NativeHotkey.DispatchMessage(ref msg);
            }

            _ = NativeHotkey.UnregisterHotKey(hwnd, HotkeyId);
            _ = NativeHotkey.DestroyWindow(hwnd);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _ready.Set();
        }
        finally
        {
            _registered = false;
            Interlocked.Exchange(ref _hwnd, 0);
            _ready.Set();
        }
    }

    private static class NativeHotkey
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(nint hWnd, int id);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            nint hWndParent,
            nint hMenu,
            nint hInstance,
            nint lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessage(ref MSG lpMsg);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandle(string? lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public nint hwnd;
            public int message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }
    }
}
