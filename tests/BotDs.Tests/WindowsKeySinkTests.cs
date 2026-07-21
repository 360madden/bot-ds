using BotDs.Input;

namespace BotDs.Tests;

public sealed class WindowsKeySinkTests
{
    /// <summary>
    /// Fake input injector that always succeeds and records calls.
    /// </summary>
    private sealed class FakeInputInjector : IInputInjector
    {
        public readonly List<WindowsKeySink.INPUT[]> Calls = [];
        public readonly Queue<InputInjectionResult> Results = [];

        public InputInjectionResult Inject(WindowsKeySink.INPUT[] inputs, int count)
        {
            Calls.Add(inputs[..count]);
            return Results.TryDequeue(out InputInjectionResult result)
                ? result
                : new InputInjectionResult(count);
        }
    }

    private static FakeInputInjector CreateInjector() => new();

    // ═══════════════════════════════════════════════════════════
    // Foreground validation
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Dispatch_succeeds_when_foreground_matches()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.True(sink.IsReady);
        Assert.True(sink.DispatchKey("1"));
    }

    [Fact]
    public void Dispatch_fails_when_foreground_mismatch()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 11111);
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey("1"));
        Assert.True(sink.IsReady); // still ready — mismatch is not a fault
    }

    [Fact]
    public void Dispatch_fails_when_foreground_is_zero()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 0);
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey("F1"));
    }

    // ═══════════════════════════════════════════════════════════
    // Held-key rejection
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Dispatch_fails_when_target_key_is_held()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        fg.SetKeyHeld(0x31); // VK_1
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey("1"));
    }

    [Fact]
    public void Dispatch_fails_when_modifier_held_and_no_modifiers_in_binding()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        fg.SetKeyHeld(0x10); // VK_SHIFT
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        // "1" has no modifiers — should reject because SHIFT is held
        Assert.False(sink.DispatchKey("1"));
    }

    [Fact]
    public void Dispatch_with_modifiers_fails_when_other_modifier_held()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        // Any user-held modifier blocks bot-owned key-up cleanup.
        fg.SetKeyHeld(0x10); // VK_SHIFT
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey("Ctrl+1"));
    }

    [Fact]
    public void Dispatch_fails_when_binding_modifier_is_physically_held()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        // Ctrl is physically held, and binding is "Ctrl+1" — reject to avoid sticky modifier
        fg.SetKeyHeld(0x11); // VK_CONTROL
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey("Ctrl+1"));
    }

    [Fact]
    public void Dispatch_fails_when_alt_modifier_is_physically_held()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        // Alt is physically held, binding is "Alt+F4" — reject
        fg.SetKeyHeld(0x12); // VK_MENU (Alt)
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey("Alt+F4"));
    }

    // ═══════════════════════════════════════════════════════════
    // Post-dispatch foreground change → fault
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Post_dispatch_foreground_change_latches_fault()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        int callCount = 0;
        // Pre-check (call 1) passes → dispatch happens
        // Post-check (call 2) fails → fault latched
        fg.GetForegroundPid = () =>
        {
            callCount++;
            return callCount <= 1 ? 12345 : 88888;
        };

        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        // Dispatch fails the post-check and latches fault
        bool result = sink.DispatchKey("1");
        Assert.False(result);
        Assert.False(sink.IsReady);

        // Subsequent dispatch is rejected because of fault
        Assert.False(sink.DispatchKey("2"));
    }

    // ═══════════════════════════════════════════════════════════
    // Fault & dispose lifecycle
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void LatchFault_makes_sink_not_ready()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.True(sink.IsReady);
        sink.LatchFault("Test fault");
        Assert.False(sink.IsReady);
        Assert.False(sink.DispatchKey("1"));
    }

    [Fact]
    public void Dispose_makes_DispatchKey_throw()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        var sink = new WindowsKeySink(12345, fg, CreateInjector());
        sink.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sink.DispatchKey("1"));
    }

    [Fact]
    public void Invalid_pid_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsKeySink(0, injector: CreateInjector()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsKeySink(-1, injector: CreateInjector()));
    }

    // ═══════════════════════════════════════════════════════════
    // Invalid key bindings
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Dispatch_rejects_invalid_key_binding()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey(""));
        Assert.False(sink.DispatchKey("Super+Shift+V"));
    }

    [Fact]
    public void Dispatch_rejects_unknown_key_name()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.False(sink.DispatchKey("F13"));
        Assert.False(sink.DispatchKey("UnknownKey"));
    }

    // ═══════════════════════════════════════════════════════════
    // BoundPid
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BoundPid_matches_constructor_pid()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        using var sink = new WindowsKeySink(12345, fg, CreateInjector());

        Assert.Equal(12345, sink.BoundPid);
        Assert.True(sink.SupportsLiveInput);
    }

    // ═══════════════════════════════════════════════════════════
    // Injector receives correct INPUT structures
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Injector_receives_correct_down_then_up_calls()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        var injector = new FakeInputInjector();
        using var sink = new WindowsKeySink(12345, fg, injector);

        Assert.True(sink.DispatchKey("Shift+A"));

        // Should have two calls: down events, then up events
        Assert.Equal(2, injector.Calls.Count);

        // First call: Shift down, A down = 2 events
        WindowsKeySink.INPUT[] down = injector.Calls[0];
        Assert.Equal(2, down.Length);
        Assert.Equal(0x10u, down[0].u.ki.wVk); // Shift down
        Assert.Equal(0u, down[0].u.ki.dwFlags); // KEYEVENTF_KEYDOWN
        Assert.Equal(0x41u, down[1].u.ki.wVk); // A down
        Assert.Equal(0u, down[1].u.ki.dwFlags); // KEYEVENTF_KEYDOWN

        // Second call: A up, Shift up = 2 events (reversed order)
        WindowsKeySink.INPUT[] up = injector.Calls[1];
        Assert.Equal(2, up.Length);
        Assert.Equal(0x41u, up[0].u.ki.wVk); // A up
        Assert.Equal(0x0002u, up[0].u.ki.dwFlags); // KEYEVENTF_KEYUP
        Assert.Equal(0x10u, up[1].u.ki.wVk); // Shift up
        Assert.Equal(0x0002u, up[1].u.ki.dwFlags); // KEYEVENTF_KEYUP
    }

    [Fact]
    public void Injector_not_called_on_foreground_mismatch()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 11111);
        var injector = new FakeInputInjector();
        using var sink = new WindowsKeySink(12345, fg, injector);

        Assert.False(sink.DispatchKey("1"));
        Assert.Empty(injector.Calls);
    }

    [Fact]
    public void Injector_not_called_when_key_held()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        fg.SetKeyHeld(0x31); // VK_1
        var injector = new FakeInputInjector();
        using var sink = new WindowsKeySink(12345, fg, injector);

        Assert.False(sink.DispatchKey("1"));
        Assert.Empty(injector.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Partial_down_runs_full_reverse_cleanup_and_latches_fault(int sentCount)
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        var injector = new FakeInputInjector();
        injector.Results.Enqueue(new InputInjectionResult(sentCount, 5));
        injector.Results.Enqueue(new InputInjectionResult(2));
        using var sink = new WindowsKeySink(12345, fg, injector);

        Assert.False(sink.DispatchKey("Shift+A"));
        Assert.False(sink.IsReady);
        Assert.Equal(2, injector.Calls.Count);
        AssertReleaseOrder(injector.Calls[1], 0x41, 0x10);
    }

    [Fact]
    public void Partial_key_up_runs_one_full_cleanup_and_latches_fault()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        var injector = new FakeInputInjector();
        injector.Results.Enqueue(new InputInjectionResult(2));
        injector.Results.Enqueue(new InputInjectionResult(1, 5));
        injector.Results.Enqueue(new InputInjectionResult(2));
        using var sink = new WindowsKeySink(12345, fg, injector);

        Assert.False(sink.DispatchKey("Shift+A"));
        Assert.False(sink.IsReady);
        Assert.Equal(3, injector.Calls.Count);
        AssertReleaseOrder(injector.Calls[1], 0x41, 0x10);
        AssertReleaseOrder(injector.Calls[2], 0x41, 0x10);
    }

    [Fact]
    public void Cancellation_after_down_runs_full_cleanup_and_latches_fault()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        var injector = new FakeInputInjector();
        using var sink = new WindowsKeySink(12345, fg, injector);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(sink.DispatchKey("Ctrl+A", cts.Token));
        Assert.False(sink.IsReady);
        Assert.Equal(2, injector.Calls.Count);
        AssertReleaseOrder(injector.Calls[1], 0x41, 0x11);
    }

    [Fact]
    public void Cleanup_failure_still_runs_once_and_latches_fault()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        var injector = new FakeInputInjector();
        injector.Results.Enqueue(new InputInjectionResult(0, 5));
        injector.Results.Enqueue(new InputInjectionResult(0, 5));
        using var sink = new WindowsKeySink(12345, fg, injector);

        Assert.False(sink.DispatchKey("Alt+A"));
        Assert.False(sink.IsReady);
        Assert.Equal(2, injector.Calls.Count);
    }

    [Fact]
    public void Three_modifier_chord_uses_exact_down_and_reverse_up_order()
    {
        var fg = new FakeForegroundProvider(foregroundPid: 12345);
        var injector = new FakeInputInjector();
        using var sink = new WindowsKeySink(12345, fg, injector);

        Assert.True(sink.DispatchKey("Shift+Ctrl+Alt+A"));
        Assert.Equal(new ushort[] { 0x10, 0x11, 0x12, 0x41 },
            injector.Calls[0].Select(input => input.u.ki.wVk));
        Assert.Equal(new ushort[] { 0x41, 0x12, 0x11, 0x10 },
            injector.Calls[1].Select(input => input.u.ki.wVk));
    }

    private static void AssertReleaseOrder(WindowsKeySink.INPUT[] inputs, params ushort[] keys)
    {
        Assert.Equal(keys, inputs.Select(input => input.u.ki.wVk));
        Assert.All(inputs, input => Assert.Equal(0x0002u, input.u.ki.dwFlags));
    }
}

/// <summary>
/// Fake foreground provider for WindowsKeySink tests.
/// Supports lambda-based GetForegroundPid for stateful scenarios
/// (e.g., foreground change mid-dispatch).
/// </summary>
file sealed class FakeForegroundProvider : IForegroundProvider
{
    private readonly HashSet<ushort> _heldKeys = [];
    public int ForegroundPid { get; set; }
    public Func<int>? GetForegroundPid { private get; set; }

    public FakeForegroundProvider(int foregroundPid = 9999)
    {
        ForegroundPid = foregroundPid;
    }

    int IForegroundProvider.GetForegroundPid() =>
        GetForegroundPid?.Invoke() ?? ForegroundPid;

    public bool IsKeyHeld(ushort vk) => _heldKeys.Contains(vk);

    public void SetKeyHeld(ushort vk) => _heldKeys.Add(vk);
    public void ClearKeyHeld(ushort vk) => _heldKeys.Remove(vk);
    public void ClearAllHeld() => _heldKeys.Clear();
}
