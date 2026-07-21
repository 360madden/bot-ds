using BotDs.Input;

namespace BotDs.Tests;

internal sealed class TestLiveKeySink(int boundPid = 4242) : IKeySink
{
    private bool _faulted;

    public bool SupportsLiveInput { get; init; } = true;
    public bool IsReady => !_faulted && Ready;
    public int BoundPid { get; init; } = boundPid;
    public bool Ready { get; set; } = true;
    public bool DispatchResult { get; set; } = true;
    public bool ThrowOnDispatch { get; set; }
    public int DispatchCount { get; private set; }
    public List<string> Keys { get; } = [];

    public bool DispatchKey(string key, CancellationToken ct = default)
    {
        DispatchCount++;
        Keys.Add(key);
        if (ThrowOnDispatch)
            throw new InvalidOperationException("Test sink must not be called.");
        return DispatchResult;
    }

    public void LatchFault(string reason)
    {
        _ = reason;
        _faulted = true;
    }
}
