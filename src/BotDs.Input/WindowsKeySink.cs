using System.Runtime.InteropServices;

namespace BotDs.Input;

// ═══════════════════════════════════════════════════════════════
// Foreground Provider abstraction
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Abstracts Win32 foreground-window queries so WindowsKeySink
/// can be tested without a real game process.
/// </summary>
public interface IForegroundProvider
{
    /// <summary>PID of the process owning the current foreground window, or 0.</summary>
    int GetForegroundPid();

    /// <summary>
    /// Returns true if the virtual key is currently physically held.
    /// (Equivalent to <c>(GetAsyncKeyState(vk) &amp; 0x8000) != 0</c>.)
    /// </summary>
    bool IsKeyHeld(ushort vk);
}

/// <summary>
/// Production foreground provider using real Win32 calls.
/// </summary>
public sealed class WindowsForegroundProvider : IForegroundProvider
{
    public int GetForegroundPid()
    {
        nint hwnd = NativeInput.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return 0;
        _ = NativeInput.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    public bool IsKeyHeld(ushort vk)
    {
        short state = NativeInput.GetAsyncKeyState((int)vk);
        return (state & 0x8000) != 0;
    }
}

// ═══════════════════════════════════════════════════════════════
// Input Injector abstraction
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Abstracts the low-level keyboard injection so WindowsKeySink
/// can be tested without calling real SendInput.
/// </summary>
public interface IInputInjector
{
    /// <summary>
    /// Inject a batch of keyboard INPUT structures into the system.
    /// Returns true if all events were successfully injected.
    /// </summary>
    bool Inject(WindowsKeySink.INPUT[] inputs, int count);
}

/// <summary>
/// Production input injector using Win32 SendInput.
/// </summary>
public sealed class WindowsInputInjector : IInputInjector
{
    public bool Inject(WindowsKeySink.INPUT[] inputs, int count)
    {
        if (count == 0) return true;
        uint sent = NativeInput.SendInput(
            (uint)count,
            ref inputs[0],
            Marshal.SizeOf<WindowsKeySink.INPUT>());
        return sent == count;
    }
}

// ═══════════════════════════════════════════════════════════════
// WindowsKeySink
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Windows key sink using SendInput to inject keyboard chords.
/// Validates foreground ownership and held-key state before dispatch.
/// Latches a fault if the foreground changes mid-dispatch.
/// </summary>
public sealed class WindowsKeySink : IKeySink, IDisposable
{
    private volatile bool _faulted;
    private volatile bool _disposed;
    private readonly object _lock = new();
    private readonly int _boundPid;
    private readonly IForegroundProvider _fg;
    private readonly IInputInjector _injector;

    /// <summary>Minimum interval between key-down and key-up, in milliseconds.</summary>
    private readonly int _chordPressMs;

    public bool IsReady => !_faulted && !_disposed;
    public int BoundPid => _boundPid;

    /// <summary>
    /// Create a Windows key sink bound to the given process.
    /// </summary>
    /// <param name="boundPid">PID of the target game process.</param>
    /// <param name="foregroundProvider">Foreground query provider (null = production Windows provider).</param>
    /// <param name="injector">Input injector (null = production SendInput injector).</param>
    /// <param name="chordPressMs">Milliseconds between key-down and key-up (default 30). Clamped to 5-200.</param>
    public WindowsKeySink(
        int boundPid,
        IForegroundProvider? foregroundProvider = null,
        IInputInjector? injector = null,
        int chordPressMs = 30)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(boundPid, 0);
        _boundPid = boundPid;
        _fg = foregroundProvider ?? new WindowsForegroundProvider();
        _injector = injector ?? new WindowsInputInjector();
        _chordPressMs = Math.Clamp(chordPressMs, 5, 200);
    }

    // ── IKeySink ──────────────────────────────────────────────

    public bool DispatchKey(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var parsed = KeyBindingGrammar.Parse(key);
        if (parsed is null)
            return false;

        return DispatchChord(parsed.Value.Modifiers, parsed.Value.Key, ct);
    }

    public void LatchFault(string reason)
    {
        lock (_lock) _faulted = true;
    }

    // ── Chord dispatch ────────────────────────────────────────

    private bool DispatchChord(HashSet<string> modifiers, string key, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_faulted || _disposed)
                return false;

            // ── Foreground validation ─────────────────────────
            int fgPid = _fg.GetForegroundPid();
            if (fgPid != _boundPid)
                return false;

            // ── Resolve virtual key ───────────────────────────
            ushort? vk = VkMapper.Resolve(key);
            if (vk is null)
                return false;

            // ── Held-key check ────────────────────────────────
            if (_fg.IsKeyHeld(vk.Value))
                return false;

            // Check modifier keys aren't physically held (they should come from SendInput, not the user)
        if (modifiers.Count == 0)
        {
            // No modifiers requested — reject if any modifier is held (user finger on shift/ctrl/alt)
            if (_fg.IsKeyHeld(VK_SHIFT) || _fg.IsKeyHeld(VK_CONTROL) || _fg.IsKeyHeld(VK_MENU))
                return false;
        }
        else
        {
            // Binding uses modifiers — reject if any of the binding's own modifiers are physically held
            // (would cause sticky modifier when the bot releases but the user doesn't)
            if (modifiers.Contains("Shift") && _fg.IsKeyHeld(VK_SHIFT)) return false;
            if (modifiers.Contains("Ctrl") && _fg.IsKeyHeld(VK_CONTROL)) return false;
            if (modifiers.Contains("Alt") && _fg.IsKeyHeld(VK_MENU)) return false;
        }

            // ── Send chord ────────────────────────────────────
            if (!SendKeyChord(vk.Value, modifiers, ct))
            {
                _faulted = true;
                return false;
            }

            // ── Post-dispatch foreground recheck ──────────────
            int fgPidAfter = _fg.GetForegroundPid();
            if (fgPidAfter != _boundPid)
            {
                _faulted = true;
                return false; // key was sent but foreground changed — fault
            }

            return true;
        }
    }

    private bool SendKeyChord(ushort vk, HashSet<string> modifiers, CancellationToken ct)
    {
        bool hasShift = modifiers.Contains("Shift");
        bool hasCtrl = modifiers.Contains("Ctrl");
        bool hasAlt = modifiers.Contains("Alt");

        // ── Build down events (modifiers first, then key) ────
        Span<INPUT> downInputs = stackalloc INPUT[4]; // up to 3 modifiers + 1 key
        int downCount = 0;
        if (hasShift) downInputs[downCount++] = MakeKeyInput(VK_SHIFT, KeyDown);
        if (hasCtrl) downInputs[downCount++] = MakeKeyInput(VK_CONTROL, KeyDown);
        if (hasAlt) downInputs[downCount++] = MakeKeyInput(VK_MENU, KeyDown);
        downInputs[downCount++] = MakeKeyInput(vk, KeyDown);

        if (!_injector.Inject(downInputs[..downCount].ToArray(), downCount))
            return false;

        // ── Delay between press and release ──────────────────
        if (_chordPressMs > 0 && !ct.IsCancellationRequested)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < _chordPressMs && !ct.IsCancellationRequested)
                Thread.SpinWait(10);
        }

        if (ct.IsCancellationRequested)
        {
            // Best-effort: release the key even after cancellation
            Span<INPUT> upCleanup = stackalloc INPUT[4];
            int upC = 0;
            upCleanup[upC++] = MakeKeyInput(vk, KeyUp);
            if (hasAlt) upCleanup[upC++] = MakeKeyInput(VK_MENU, KeyUp);
            if (hasCtrl) upCleanup[upC++] = MakeKeyInput(VK_CONTROL, KeyUp);
            if (hasShift) upCleanup[upC++] = MakeKeyInput(VK_SHIFT, KeyUp);
            _injector.Inject(upCleanup[..upC].ToArray(), upC);
            return false;
        }

        // ── Build up events (key first, then modifiers reversed) ──
        Span<INPUT> upInputs = stackalloc INPUT[4]; // 1 key + up to 3 modifiers
        int upCount = 0;
        upInputs[upCount++] = MakeKeyInput(vk, KeyUp);
        if (hasAlt) upInputs[upCount++] = MakeKeyInput(VK_MENU, KeyUp);
        if (hasCtrl) upInputs[upCount++] = MakeKeyInput(VK_CONTROL, KeyUp);
        if (hasShift) upInputs[upCount++] = MakeKeyInput(VK_SHIFT, KeyUp);

        if (!_injector.Inject(upInputs[..upCount].ToArray(), upCount))
            return false;

        return true;
    }

    // ── INPUT helpers ─────────────────────────────────────────

    private const uint KeyDown = 0x0000;
    private const uint KeyUp = 0x0002;

    private static INPUT MakeKeyInput(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero,
            },
        },
    };

    // ── Virtual key constants ─────────────────────────────────

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12; // Alt

    // ── IDisposable ───────────────────────────────────────────

    public void Dispose()
    {
        lock (_lock) _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsKeySink));
    }

    // ═══════════════════════════════════════════════════════════
    // VK code mapper (static utility)
    // ═══════════════════════════════════════════════════════════

    private static class VkMapper
    {
        private static readonly Dictionary<string, ushort> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            // Digits
            ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
            ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
            // Letters
            ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
            ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
            ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
            ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
            ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59,
            ["Z"] = 0x5A,
            // Function keys
            ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
            ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
            ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
            // Numpad
            ["Numpad0"] = 0x60, ["Numpad1"] = 0x61, ["Numpad2"] = 0x62,
            ["Numpad3"] = 0x63, ["Numpad4"] = 0x64, ["Numpad5"] = 0x65,
            ["Numpad6"] = 0x66, ["Numpad7"] = 0x67, ["Numpad8"] = 0x68,
            ["Numpad9"] = 0x69,
            // Common keys
            ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D,
            ["Escape"] = 0x1B, ["Esc"] = 0x1B,
            ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Insert"] = 0x2D,
            ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
            // Symbols / OEM
            ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD,
            ["\\"] = 0xDC, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC,
            ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0,
            // Modifiers (should be parsed as modifiers, not keys, but included for safety)
            ["Shift"] = VK_SHIFT, ["Ctrl"] = VK_CONTROL, ["Alt"] = VK_MENU,
        };

        public static ushort? Resolve(string key)
        {
            if (Map.TryGetValue(key, out ushort vk))
                return vk;

            // Fallback: try "VK_<name>" enum parse
            if (key.StartsWith("VK_", StringComparison.OrdinalIgnoreCase))
            {
                string enumName = key[3..];
                if (Map.TryGetValue(enumName, out ushort vk2))
                    return vk2;
            }

            return null;
        }
    }

    // ── Native structs shared with NativeInput (public for IInputInjector) ─

    public const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}

// ═══════════════════════════════════════════════════════════════
// Native P/Invoke methods (shared by WindowsKeySink and WindowsForegroundProvider)
// ═══════════════════════════════════════════════════════════════

internal static class NativeInput
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(
        uint cInputs,
        ref WindowsKeySink.INPUT pInputs,
        int cbSize);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(
        nint hWnd,
        out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
}
