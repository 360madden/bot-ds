using System.Text;
using System.Text.Json;
using BotDs.Core;

namespace BotDs.Tests;

public sealed class ReplayEnvelopeStoreTests : IDisposable
{
    private readonly string _dir;

    public ReplayEnvelopeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"botds-replay-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SaveLoad_RoundTrip_PreservesTicksAndExpectedDispatch()
    {
        ReplayEnvelope original = BuildTinyEnvelope();
        string path = Path.Combine(_dir, "cycle.json");
        await ReplayEnvelopeStore.SaveAsync(path, original);
        ReplayEnvelope loaded = await ReplayEnvelopeStore.LoadAsync(path);

        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(original.Frames.Count, loaded.Frames.Count);
        Assert.Equal(original.Frames[0].Tick, loaded.Frames[0].Tick);
        Assert.Equal(original.Frames[0].ExpectedDispatch!.AbilityId, loaded.Frames[0].ExpectedDispatch!.AbilityId);
    }

    [Fact]
    public async Task Load_CheckedInFixture_Succeeds()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tests", "BotDs.Tests", "Fixtures", "replay-combat-cycle.json");
        Assert.True(File.Exists(path), "fixture missing: " + path);
        ReplayEnvelope env = await ReplayEnvelopeStore.LoadAsync(path);
        Assert.True(env.Frames.Count >= 2);
        Assert.Equal(ReplayEnvelope.CurrentVersion, env.Version);
    }

    [Fact]
    public async Task Load_WrongVersion_Throws()
    {
        string path = Path.Combine(_dir, "bad-version.json");
        await File.WriteAllTextAsync(path, """{"v":99,"frames":[{"tick":0,"elapsedMs":0,"snapshot":{"provider":{"health":"Healthy","sequence":1,"ageMs":0,"sessionId":"x","protocolVersion":"5","producerFrameMilliseconds":0}}}]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => ReplayEnvelopeStore.LoadAsync(path));
    }

    [Fact]
    public async Task Load_Oversized_Throws()
    {
        string path = Path.Combine(_dir, "huge.json");
        // Write just over MaxFileBytes without allocating huge string of valid JSON structure...
        // Use sparse file-like padding: create file with length > max via FileStream SetLength if supported.
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(ReplayEnvelope.MaxFileBytes + 1);
            fs.WriteByte((byte)'{');
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ReplayEnvelopeStore.LoadAsync(path));
    }

    [Fact]
    public async Task Load_CorruptJson_Throws()
    {
        string path = Path.Combine(_dir, "corrupt.json");
        await File.WriteAllTextAsync(path, "{ not json", Encoding.UTF8);
        await Assert.ThrowsAnyAsync<Exception>(() => ReplayEnvelopeStore.LoadAsync(path));
    }

    private static ReplayEnvelope BuildTinyEnvelope()
    {
        const string abilityId = "A01BB8C035B6A96DD";
        var abilities = new Dictionary<string, ReplayAbilityState>
        {
            [abilityId] = new ReplayAbilityState
            {
                Id = abilityId,
                Available = true,
                Usable = true,
                InRange = true,
                CooldownRemainingMs = 0,
            },
        };

        ReplaySnapshot snap = new()
        {
            Provider = new ReplayProviderStatus
            {
                Health = ProviderHealth.Healthy,
                Sequence = 1,
                AgeMs = 0,
                SessionId = "offline-test",
                ProducerFrameMilliseconds = 100,
            },
            Player = new ReplayUnitState
            {
                Id = "p1",
                Name = "Atank",
                Level = 45,
                Calling = "warrior",
                IsPlayer = true,
                Relation = "friendly",
                CurrentHealth = 100,
                MaxHealth = 100,
                InCombat = true,
            },
            Target = new ReplayUnitState
            {
                Id = "t1",
                Name = "Mob",
                Level = 40,
                IsPlayer = false,
                Relation = "hostile",
                CurrentHealth = 50,
                MaxHealth = 100,
                InCombat = true,
            },
            Abilities = abilities,
            IsAbilitiesKnown = true,
        };

        return ReplayEnvelope.Create(
        [
            new ReplayFrame
            {
                Tick = 0,
                ElapsedMs = 0,
                Snapshot = snap,
                ExpectedDispatch = new ReplayDispatch
                {
                    RuleId = "rule-a",
                    AbilityAlias = "a",
                    AbilityId = abilityId,
                    Key = "1",
                },
            },
            new ReplayFrame
            {
                Tick = 1,
                ElapsedMs = 100,
                Snapshot = snap with
                {
                    Provider = snap.Provider with { Sequence = 2 },
                    Abilities = new Dictionary<string, ReplayAbilityState>
                    {
                        [abilityId] = new ReplayAbilityState
                        {
                            Id = abilityId,
                            Available = true,
                            Usable = false,
                            InRange = true,
                            CooldownRemainingMs = 2000,
                        },
                    },
                },
            },
        ]);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BotDs.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("repo root not found");
    }
}
