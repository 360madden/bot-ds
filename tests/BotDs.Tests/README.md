# BotDs.Tests

Unit tests for the BotDs combat-bot stack. The suite covers core contracts, protocol parsing, scanner memory operations, controller state, profile management, dashboard security, and snapshot publishing. As of the latest verified run the suite contains **378 passing tests**.

## Commands

```bash
# Build (requires Windows x64; project targets net10.0-windows)
dotnet build BotDs.sln --no-restore

# Restore packages first if needed
dotnet restore BotDs.sln

# Run all tests
dotnet test BotDs.sln --no-restore

# Run a single test class
dotnet test tests/BotDs.Tests/BotDs.Tests.csproj --filter "FullyQualifiedName~CombatEvaluatorTests"

# Run a single test method
dotnet test tests/BotDs.Tests/BotDs.Tests.csproj --filter "DisplayName~Evaluate_StaleProvider_ReturnsTelemetryStale"

# Format check
dotnet format BotDs.sln --verify-no-changes --no-restore
```

The test project targets `net10.0-windows` and runs on Windows x64 only. Several tests in `ScannerAdversarialTests` (self-process smoke, `ReadExact` bounds validation) contain explicit platform guards (`OperatingSystem.IsWindows()` / `nint.Size != 8`) and silently skip on unsupported platforms.

## Test categories

### Core / profile / evaluator

| File | Classes | Focus |
|------|---------|-------|
| `CoreTests.cs` | `CombatProfileLoaderTests`, `CombatEvaluatorTests` | Profile validation (version, calling, abilities, rules, level ranges, null/empty/duplicate handling), evaluator state transitions (stale provider, dead player, wrong calling, missing target, aura predicates, required-binding reconciliation, disabled-profile stops). |
| `CoreSafetyTests.cs` | `CoreSafetyTests` | Elapsed-time-aware frame staleness (`ReceivedAtUtc` + monotonic clock), future/negative `Age` fail-closed, profile `Build` identity enforcement (V5 cannot observe it), degraded/provider health fail-closed. |
| `TelemetrySafetyTests.cs` | `TelemetrySafetyTests` | `UnitState.IsAvailable` with null health, aura-known rejection semantics (required/forbidden player/target auras with unknown vs known-empty vs known-present lists), V5HealthMapper aura-section-mask-to-known mapping, Faulted-health preservation, `TelemetryFrame` default unknown aura state. |

### Protocol / parser

| File | Classes | Focus |
|------|---------|-------|
| `ProtocolTests.cs` | `V5Crc32Tests`, `V5ParserTests`, `V5HealthMapperTests`, `SessionTrackerTests`, `SessionTrackerWrapTests`, `StableReaderTests` | CRC-32 computation and validation, V5 buffer slot parsing (version, reserved fields, flags, sections mask, section ordering, duplicate sections, length mismatches, heartbeat validation), ProviderInfo/Abilities/Auras wire decoding, unit flag mapping, session sequence continuity (gap, restart, decrement, wrap, high-water mark), double-buffer selection, freshness/TimestampProvider integration, wall-clock rollback resistance. |

### Telemetry completeness

Covered by `TelemetrySafetyTests.cs` (see above). Contracts guarded: null-current-health unavailability, aura-known flag propagation from protocol mask bits through mapper to evaluator, Faulted health/detail preservation across mapper boundaries.

### Scanner / native / adversarial

| File | Classes | Focus |
|------|---------|-------|
| `ScannerTests.cs` | `ProcessSelectorTests`, `ScannerMetricsTests`, `NativeMemoryFilteringTests`, `V5SentinelScannerTests`, `V5FrameSelectorTests`, `V5ScannerServiceTests`, `NativeErrorMappingTests`, `WindowsMemoryReaderRangeTests` + helpers (`FakeMemoryCatalog`, `FakeMemoryReader`, `FakeMemoryReaderFactory`, `ScannerTestHelpers`) | Process selector validation, memory protection filtering, sentinel scanning with fake memory (single candidate, chunk-boundary splits, cross-region magic, limits, ProducerFrameMs mismatch, missing ProviderInfo), frame selection (same-session ordering, cross-session producer time, ambiguous half-range, cyclic ordering), scanner service lifecycle (cache hit, stale relocation, process exit, incomplete scan, backoff, reattach generation, disposal, cancellation, failure-detail sanitization), Win32 error mapping, native struct layout validation. |
| `ScannerAdversarialTests.cs` | `ScannerAdversarialTests` | Chunk-boundary magic splits at every offset, carry-over reset across region gaps, incomplete enumeration, raw/candidate limit fails-closed, bad magic/version rejection, no false positives on random bytes, mid-region read failures, CRC corruption (both/one slots), candidate deduplication, concurrent read+dispose races, process-loss races, `ReadExact` address overflow/negative/out-of-bounds, factory name binding, `MapWin32Error` coverage, self-process attach+enumerate smoke test (Windows x64 only). |

### Controller

| File | Classes | Focus |
|------|---------|-------|
| `ControllerStateMachineTests.cs` | `ControllerStateMachineTests` | Emergency-stop latch, `ClearStop` semantics, stale evaluation rejection, generation-based invalidation, inconsistent `StopReason` enforcement, configuration lease (blocks arm, idempotent dispose, invalidates evaluations, concurrency guard). |

### Profile service

| File | Classes | Focus |
|------|---------|-------|
| `ProfileServiceTests.cs` | `ProfileServiceTests` | Directory reload (missing dir preserves cache, duplicate IDs rejected atomically, valid swap, active-profile removal, empty dir, invalid JSON / semantically invalid profile preserves previous cache, and privacy-safe malformed-load diagnostics). |

### Dashboard middleware

| File | Classes | Focus |
|------|---------|-------|
| `DashboardSecurityMiddlewareTests.cs` | `DashboardSecurityMiddlewareTests` | Loopback-only enforcement, null/remote/non-local-Host rejection, cross-origin `Origin` header rejection, API vs control token tiering, empty-token fail-closed, IPv4-mapped loopback acceptance. |

### Snapshot publisher

| File | Classes | Focus |
|------|---------|-------|
| `SnapshotPublisherTests.cs` | `SnapshotPublisherTests` | Age accumulation and reset on publish, monotonic aging, wall-clock rollback resistance, age overflow saturation, `ReceivedAtUtc` tracking, default constructor initial state, provider field preservation, multiple `Latest` calls consistency. |

## Deterministic time / fake-memory approach

### TimeProvider

Tests that depend on elapsed time or freshness thresholds inject a `TimeProvider` rather than reading `DateTimeOffset.UtcNow` or `Environment.TickCount64`.

**`ControllableTimeProvider`** (defined in `ProtocolTests.cs`, used across scanner tests):

```csharp
internal sealed class ControllableTimeProvider : TimeProvider
{
    private long _ticks;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    public void SetTicks(long ticks) => _ticks = ticks;
}
```

- Overrides `GetTimestamp()` with a manually-advancing tick counter.
- Sets `TimestampFrequency` to `TimeSpan.TicksPerSecond` so `GetElapsedTime` returns accurate `TimeSpan` values from tick deltas.
- Used by `StableReader`, `V5ScannerService`, and `CombatEvaluator` freshness tests.

**`TestTimeProvider`** (defined in `CoreSafetyTests.cs`):

```csharp
private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
```

- Overrides `GetUtcNow()` for evaluator-level staleness tests that compare wall-clock time directly.
- Used by `CombatEvaluator` tests that feed `ReceivedAtUtc` + elapsed computation.

**`FakeTimeProvider`** (defined in `SnapshotPublisherTests.cs`):

- Overrides both `GetTimestamp()` and `GetUtcNow()`.
- Provides `Advance(TimeSpan)` (both monotonic and wall clock) and `AdvanceMonotonicOnly(TimeSpan)` (monotonic only) and `SetUtcNow(DateTimeOffset)` (wall clock only) for testing age behavior under wall-clock rollback.

### Fake memory

Scanner tests avoid real process attachment by using an in-memory catalog and reader abstraction.

**`FakeMemoryCatalog`**: A `Dictionary<nint, byte[]>` that maps base addresses to page data. Supports `AddPage`, `ModifyPage`, `MarkEnumerationIncomplete`, and multi-page contiguous reads that span page boundaries.

**`FakeMemoryReader`** : Implements `IMemoryReader` backed by a catalog. Supports `ReadExact` (spanning pages), `QueryReadableRegions`, process liveness simulation (`Kill()`), and disposal semantics.

**`FakeMemoryReaderFactory`** : Implements `IMemoryReaderFactory`. Registers multiple fake processes, supports `KillProcess` to simulate exit, and tracks the last reader instance for verification.

**`ThrowingMemoryReaderFactory`** : Always throws a configured `ReaderException` on `Open`. Used for failure-detail sanitization and error-path tests.

These fakes let every scanner and protocol test run deterministically without a live game process.

## Windows-only self-process smoke boundary

`ScannerAdversarialTests.SelfProcess_AttachAndEnumerate_Succeeds` is a **self-process smoke test** that:

1. Guards on `OperatingSystem.IsWindows()` and `nint.Size != 8` (x64 only).
2. Attaches to the test runner's own process via `WindowsMemoryReader.Attach(Environment.ProcessId)`.
3. Enumerates readable memory regions and asserts the list is non-empty and complete.
4. Verifies at least one region base address is above 4 GiB (confirming x64 user-space layout).
5. Verifies `Dispose()` is idempotent.

This smoke test and the three bounds tests below are the only tests that attach to a real process. All other scanner tests use the `FakeMemoryCatalog`/`FakeMemoryReader` infrastructure and are fully deterministic.

Three additional `ReadExact` bounds-validation tests in the same file also attach to the self-process and guard on the same platform check: they verify that reads at overflow, out-of-range, and negative addresses throw `ReaderException` with `ReadFailure`.

## How to add regression tests

1. **Identify the contract or failure mode.** Write a test that describes the expected behavior in terms of observable state transitions, return values, or rejection reasons -- not implementation details.

2. **Follow the existing category.** Place the test in the file that owns the subsystem:
   - Profile validation / evaluator logic: `CoreTests.cs` or `CoreSafetyTests.cs`
   - Telemetry completeness / aura semantics: `TelemetrySafetyTests.cs`
   - V5 wire format / parser / session tracking / double-buffer: `ProtocolTests.cs`
   - Scanner / sentinel / frame selection / service lifecycle: `ScannerTests.cs`
   - Adversarial scanner edge cases: `ScannerAdversarialTests.cs`
   - Controller state machine: `ControllerStateMachineTests.cs`
   - Profile reload / active selection: `ProfileServiceTests.cs`
   - Dashboard security: `DashboardSecurityMiddlewareTests.cs`
   - Snapshot age / publish: `SnapshotPublisherTests.cs`

3. **Use fake infrastructure.** For anything touching "memory" or "time", use `FakeMemoryCatalog`/`FakeMemoryReader`/`FakeMemoryReaderFactory` and `ControllableTimeProvider`. Never depend on wall-clock time for assertions.

4. **Guard platform-specific tests.** If a test requires Windows x64 native APIs, wrap the body with:
   ```csharp
   if (!OperatingSystem.IsWindows()) return;
   if (nint.Size != 8) return;
   ```

5. **Name for the contract.** Test names should describe the contract, e.g.:
   - `Evaluate_StaleProvider_ReturnsTelemetryStale`
   - `Scan_BothSlotsCorruptCRC_Rejected`
   - `ApplyEvaluation_StaleResultCannotMoveControllerOutOfDisarmed`

6. **Assert stop reasons, not internal paths.** Prefer checking `StopReason`, `ControllerState`, `ProviderHealth`, `ReaderFailureCode`, and rejection reason strings over internal field values.

7. **Run and verify.**
   ```bash
   dotnet test tests/BotDs.Tests/BotDs.Tests.csproj
   ```
   All tests should pass. If a test is intentionally written against a not-yet-implemented contract (as noted in some `TelemetrySafetyTests`), add a comment explaining this and track it until the implementation lands.
