using BotDs.App.Services;
using BotDs.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

public sealed class ProfileServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ProfileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "profile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static string WriteProfile(string dir, string id, string? fileName = null)
    {
        var profile = new CombatProfile
        {
            Id = id,
            Enabled = true,
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 1, MaximumLevel = 50 },
            Abilities = new Dictionary<string, AbilityBinding>
            {
                ["strike"] = new() { AbilityId = "ability-1", Key = "1" },
            },
            Rules =
            [
                new CombatRule
                {
                    Id = $"{id}-rule",
                    Ability = "strike",
                },
            ],
        };

        string json = System.Text.Json.JsonSerializer.Serialize(profile, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

        string path = Path.Combine(dir, fileName ?? $"{id}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private ProfileService CreateService(string? dir = null)
    {
        string target = dir ?? _tempDir;
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Profiles:Directory"] = target,
            })
            .Build();

        IHostEnvironment env = new TestHostEnvironment(Path.GetTempPath());
        return new ProfileService(config, env, NullLogger<ProfileService>.Instance);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
            EnvironmentName = "Development";
            ApplicationName = "Test";
            ContentRootFileProvider = null!;
            EnvironmentName = "Development";
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
    }

    // --- Tests ---

    [Fact]
    public async Task ReloadAsync_MissingDirectory_PreservesCacheAndActiveProfile()
    {
        string profilesDir = Path.Combine(_tempDir, "profiles");
        Directory.CreateDirectory(profilesDir);
        ProfileService svc = CreateService(profilesDir);

        // Pre-populate: load a profile and set it active.
        WriteProfile(profilesDir, "stale");
        ProfileReloadResult initial = await svc.ReloadAsync();
        Assert.True(initial.Success);
        svc.SetActiveProfile("stale");
        Assert.NotNull(svc.ActiveProfileId);

        // Remove the profiles directory entirely.
        Directory.Delete(profilesDir, recursive: true);

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Equal("stale", svc.ActiveProfileId);
        Assert.Single(svc.AvailableProfileIds);
        Assert.NotNull(svc.GetProfile("stale"));
    }

    [Fact]
    public async Task ReloadAsync_DuplicateIds_RejectedAndPreviousCachePreserved()
    {
        ProfileService svc = CreateService();

        // Load initial profiles so there is a previous cache.
        WriteProfile(_tempDir, "alpha");
        WriteProfile(_tempDir, "beta");
        ProfileReloadResult initial = await svc.ReloadAsync();
        Assert.True(initial.Success);
        Assert.Equal(2, svc.AvailableProfileIds.Count);

        // Write two files that both declare id "gamma".
        WriteProfile(_tempDir, "gamma", "gamma-1.json");
        WriteProfile(_tempDir, "gamma", "gamma-2.json");

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("gamma", StringComparison.OrdinalIgnoreCase));
        // Previous cache is preserved.
        Assert.Equal(2, svc.AvailableProfileIds.Count);
        Assert.NotNull(svc.GetProfile("alpha"));
        Assert.NotNull(svc.GetProfile("beta"));
    }

    [Fact]
    public async Task ReloadAsync_ValidProfiles_SwapsAtomically()
    {
        ProfileService svc = CreateService();

        WriteProfile(_tempDir, "first");
        ProfileReloadResult r1 = await svc.ReloadAsync();
        Assert.True(r1.Success);
        Assert.Single(svc.AvailableProfileIds);
        Assert.Equal("first", svc.AvailableProfileIds[0]);

        // Add a second profile and reload.
        WriteProfile(_tempDir, "second");
        ProfileReloadResult r2 = await svc.ReloadAsync();
        Assert.True(r2.Success);
        Assert.Equal(2, svc.AvailableProfileIds.Count);
        Assert.Contains("first", svc.AvailableProfileIds);
        Assert.Contains("second", svc.AvailableProfileIds);
        Assert.Equal(2, r2.LoadedProfileIds.Count);
    }

    [Fact]
    public async Task ReloadAsync_ActiveProfileRemoved_ClearsActiveSelection()
    {
        ProfileService svc = CreateService();

        WriteProfile(_tempDir, "keep");
        WriteProfile(_tempDir, "drop");
        await svc.ReloadAsync();
        svc.SetActiveProfile("drop");
        Assert.Equal("drop", svc.ActiveProfileId);

        // Remove the "drop" profile file.
        File.Delete(Path.Combine(_tempDir, "drop.json"));

        ProfileReloadResult result = await svc.ReloadAsync();
        Assert.True(result.Success);
        Assert.Null(svc.ActiveProfileId);
        Assert.NotNull(svc.GetProfile("keep"));
    }

    [Fact]
    public async Task ReloadAsync_EmptyDirectory_ReturnsSuccessWithNoProfiles()
    {
        ProfileService svc = CreateService();

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.True(result.Success);
        Assert.Empty(svc.AvailableProfileIds);
        Assert.Null(svc.ActiveProfileId);
    }

    [Fact]
    public async Task ReloadAsync_DuplicateIds_DoesNotPartiallySwap()
    {
        ProfileService svc = CreateService();

        // Start with one good profile.
        WriteProfile(_tempDir, "solo");
        await svc.ReloadAsync();
        Assert.Single(svc.AvailableProfileIds);

        // Now write two files with the same ID plus a third unique one.
        WriteProfile(_tempDir, "dup", "dup-a.json");
        WriteProfile(_tempDir, "dup", "dup-b.json");
        WriteProfile(_tempDir, "extra");

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        // Cache must still be the original single profile.
        Assert.Single(svc.AvailableProfileIds);
        Assert.NotNull(svc.GetProfile("solo"));
        Assert.Null(svc.GetProfile("extra"));
    }

    [Fact]
    public async Task ReloadAsync_InvalidJson_PreservesPreviousCacheAndActive()
    {
        ProfileService svc = CreateService();

        // Load valid profiles and set one active.
        WriteProfile(_tempDir, "alpha");
        WriteProfile(_tempDir, "beta");
        ProfileReloadResult initial = await svc.ReloadAsync();
        Assert.True(initial.Success);
        svc.SetActiveProfile("alpha");
        Assert.Equal("alpha", svc.ActiveProfileId);

        // Add an invalid JSON file.
        File.WriteAllText(Path.Combine(_tempDir, "broken.json"), "{ not valid json }}");

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        // Previous cache is fully preserved.
        Assert.Equal(2, svc.AvailableProfileIds.Count);
        Assert.NotNull(svc.GetProfile("alpha"));
        Assert.NotNull(svc.GetProfile("beta"));
        // Active selection is preserved.
        Assert.Equal("alpha", svc.ActiveProfileId);
    }

    [Fact]
    public async Task ReloadAsync_SemanticallyInvalidProfile_PreservesPreviousCacheAndActive()
    {
        ProfileService svc = CreateService();

        // Load valid profile and set it active.
        WriteProfile(_tempDir, "good");
        ProfileReloadResult initial = await svc.ReloadAsync();
        Assert.True(initial.Success);
        svc.SetActiveProfile("good");
        Assert.Equal("good", svc.ActiveProfileId);

        // Add a semantically invalid profile (valid JSON, fails validation).
        // Empty abilities and empty rules trigger validation errors.
        string badJson = "{\"id\":\"bad-semantic\",\"character\":{\"calling\":\"Warrior\"},\"abilities\":{},\"rules\":[]}";
        File.WriteAllText(Path.Combine(_tempDir, "bad-semantic.json"), badJson);

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        // Previous cache is preserved.
        Assert.Single(svc.AvailableProfileIds);
        Assert.NotNull(svc.GetProfile("good"));
        // Active selection is preserved.
        Assert.Equal("good", svc.ActiveProfileId);
    }

    [Fact]
    public async Task ReloadAsync_MissingDirectory_ErrorDoesNotContainAbsolutePath()
    {
        string profilesDir = Path.Combine(_tempDir, "nonexistent");
        // Intentionally do NOT create the directory.
        ProfileService svc = CreateService(profilesDir);

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        foreach (string error in result.Errors)
        {
            Assert.DoesNotContain(profilesDir, error);
        }
    }

    [Fact]
    public async Task ReloadAsync_InvalidJson_ErrorMessageContainsBasenameOnly()
    {
        ProfileService svc = CreateService();

        // Load a valid profile first so there is a previous cache.
        WriteProfile(_tempDir, "valid");
        await svc.ReloadAsync();

        // Add an invalid JSON file.
        File.WriteAllText(Path.Combine(_tempDir, "bad.json"), "}}}invalid{{");

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        // Error message should reference the basename, not the full path.
        Assert.Contains(result.Errors, e => e.Contains("bad.json", StringComparison.Ordinal));
        // Error message should NOT contain the full directory path.
        string fullPath = Path.Combine(_tempDir, "bad.json");
        Assert.DoesNotContain(result.Errors, e => e.Contains(fullPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReloadAsync_InvalidJson_ErrorMessageIsBounded()
    {
        ProfileService svc = CreateService();

        // Load a valid profile first so there is a previous cache.
        WriteProfile(_tempDir, "valid");
        await svc.ReloadAsync();

        // Add an invalid JSON file with very long content.
        string longContent = new string('x', 10000);
        File.WriteAllText(Path.Combine(_tempDir, "junk.json"), longContent);

        ProfileReloadResult result = await svc.ReloadAsync();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        // Each error message should be bounded.
        foreach (string error in result.Errors)
        {
            Assert.True(error.Length <= 300,
                $"Error message exceeds bound: {error.Length} chars");
        }
    }
}
