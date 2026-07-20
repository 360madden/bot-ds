using BotDs.Input;

namespace BotDs.Tests;

public sealed class KeySinkTests
{
    [Theory]
    [InlineData("1", "1", 0)]
    [InlineData("Shift+F1", "F1", 1)]
    [InlineData("Ctrl+Shift+X", "X", 2)]
    [InlineData("Alt+F4", "F4", 1)]
    public void KeyBindingGrammar_parses_valid_bindings(string binding, string expectedKey, int expectedModifiers)
    {
        var result = KeyBindingGrammar.Parse(binding);
        Assert.NotNull(result);
        Assert.Equal(expectedKey, result.Value.Key);
        Assert.Equal(expectedModifiers, result.Value.Modifiers.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Shift+Ctrl+Alt+Super+X")]
    [InlineData("Unknown+Key")]
    public void KeyBindingGrammar_rejects_invalid_bindings(string binding)
    {
        Assert.Null(KeyBindingGrammar.Parse(binding));
    }

    [Fact]
    public void KeyBindingGrammar_rejects_null()
    {
        Assert.Null(KeyBindingGrammar.Parse(null!));
    }

    [Fact]
    public void FakeKeySink_dispatches_valid_keys()
    {
        var sink = new FakeKeySink();
        Assert.True(sink.IsReady);
        Assert.True(sink.DispatchKey("1"));
        Assert.Single(sink.History);
        Assert.Equal("1", sink.History[0].RawBinding);
    }

    [Fact]
    public void FakeKeySink_rejects_invalid_bindings()
    {
        var sink = new FakeKeySink();
        Assert.False(sink.DispatchKey(""));
        Assert.Empty(sink.History);
    }

    [Fact]
    public void FakeKeySink_rejects_after_fault()
    {
        var sink = new FakeKeySink();
        sink.LatchFault("Test fault");
        Assert.False(sink.IsReady);
        Assert.False(sink.DispatchKey("1"));
        Assert.Empty(sink.History);
    }

    [Fact]
    public void FakeKeySink_records_multiple_dispatches()
    {
        var sink = new FakeKeySink();
        sink.DispatchKey("1");
        sink.DispatchKey("Shift+2");
        sink.DispatchKey("Ctrl+A");
        Assert.Equal(3, sink.History.Count);
    }

    [Fact]
    public void KeyBindingGrammar_detects_duplicate_modifiers()
    {
        Assert.Null(KeyBindingGrammar.Parse("Shift+Shift+X"));
    }

    [Fact]
    public void KeyBindingGrammar_allows_case_insensitive_modifiers()
    {
        var result = KeyBindingGrammar.Parse("shift+ctrl+1");
        Assert.NotNull(result);
        Assert.Equal(2, result.Value.Modifiers.Count);
    }
}
