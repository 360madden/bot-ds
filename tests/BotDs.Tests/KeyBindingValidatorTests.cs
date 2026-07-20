using BotDs.Core;

namespace BotDs.Tests;

public sealed class KeyBindingValidatorTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("A")]
    [InlineData("Z")]
    [InlineData("F1")]
    [InlineData("F12")]
    [InlineData("Space")]
    [InlineData("Escape")]
    [InlineData("Left")]
    [InlineData("NumPad0")]
    [InlineData("Oem1")]
    public void Valid_single_keys_pass(string binding)
    {
        Assert.Null(KeyBindingValidator.Validate(binding));
    }

    [Theory]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+C")]
    [InlineData("Alt+F4")]
    [InlineData("Shift+Ctrl+X")]
    [InlineData("Ctrl+Shift+1")]
    [InlineData("Alt+Shift+F1")]
    public void Valid_modifier_combinations_pass(string binding)
    {
        Assert.Null(KeyBindingValidator.Validate(binding));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_rejected(string binding)
    {
        Assert.NotNull(KeyBindingValidator.Validate(binding));
    }

    [Fact]
    public void Null_rejected()
    {
        Assert.NotNull(KeyBindingValidator.Validate(null));
    }

    [Theory]
    [InlineData("Ctrl+Shift+Alt+Shift+X")] // duplicate modifier
    [InlineData("NotAKey")]
    [InlineData("@#$")]
    [InlineData("Shift+NotAKey")]
    [InlineData("UnknownMod+X")]
    [InlineData("Shift+Ctrl+Alt+Super+X")] // too many parts
    public void Invalid_bindings_rejected(string binding)
    {
        Assert.NotNull(KeyBindingValidator.Validate(binding));
    }

    [Fact]
    public void ValidateAll_collects_all_binding_errors()
    {
        var profile = new CombatProfile
        {
            Id = "test",
            Character = new CharacterRequirements { Calling = "Warrior" },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slice"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true },
                ["bash"] = new AbilityBinding { AbilityId = "1002", Key = "notvalid", Enabled = true },
                ["charge"] = new AbilityBinding { AbilityId = "1003", Key = "", Enabled = true },
                ["disabled"] = new AbilityBinding { AbilityId = "1004", Key = "alsobad", Enabled = false },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slice", Enabled = true },
            },
        };

        var errors = KeyBindingValidator.ValidateAll(profile);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("bash"));
        Assert.Contains(errors, e => e.Contains("charge"));
        Assert.DoesNotContain(errors, e => e.Contains("disabled")); // disabled bindings aren't validated
    }

    [Fact]
    public void Valid_profile_produces_no_key_errors()
    {
        var profile = new CombatProfile
        {
            Id = "test",
            Character = new CharacterRequirements { Calling = "Warrior" },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slice"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true },
                ["bash"] = new AbilityBinding { AbilityId = "1002", Key = "Shift+2", Enabled = true },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slice", Enabled = true },
            },
        };

        var errors = KeyBindingValidator.ValidateAll(profile);
        Assert.Empty(errors);
    }
}
