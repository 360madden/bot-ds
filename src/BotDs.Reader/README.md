# BotDs.Reader

Process-memory reader for the BotDs V5 double-buffer telemetry protocol. Provides Windows x64 process attachment, readable-region enumeration, V5 sentinel scanning, candidate selection, stable double-buffer reads with CRC validation, session/sequence continuity tracking, staleness detection, and privacy-safe scanner metrics.

Targets `net10.0-windows`. Requires x64 host and x64 target.

## Architecture overview

```
ProcessSelector  -->  V5ScannerService  -->  IMemoryReader (Windows or test fake)
                          |                         |
                          |  V5SentinelScanner      |  ReadProcessMemory
                          |  V5FrameSelector        |  VirtualQueryEx
                          |  StableReader           |
                          |  SessionTracker         |
                          |  V5Crc32                |
                          |  V5Parser               |
                          |  V5HealthMapper         |
                          |                         |
                     ScannerReadResult      RegionEnumerationResult
                     ScannerMetrics
```

## V5 protocol parsing and CRC

- **V5Crc32**: CRC-32/ISO-HDLC (polynomial `0xEDB88320` reflected, init `0xFFFFFFFF`, final xor `0xFFFFFFFF`). Matches zlib/gzip/PNG.
  - `ValidateBuffer(buffer, out computedCrc)` validates a full 8192-byte slot: CRC covers header bytes 0–23 plus the payload slice (0 to `PayloadLength` bytes). The stored CRC field at header offset 24 is outside the covered range.
  - `WriteCrc(buffer, payloadLength)` writes the CRC after zeroing the field.
  - `Compute(data)` and `ComputeCombined(first, second)` are available for lower-level callers.
- **V5Parser**: bounded, defensive parser for the V5 buffer format.
  - `ParseAndValidate(buffer, bufferIndex)` validates CRC then parses. Returns a `V5ParseResult` with a structured `V5ParseFailure` enum.
  - `Parse(buffer, bufferIndex)` parses without CRC validation (caller must validate first).
  - Every field that can be invalid produces a distinct `V5ParseFailure`: CRC mismatch, protocol version, reserved non-zero, flags reserved bits, section mask reserved bits, duplicate/out-of-order sections, mask mismatch, trailing data, and per-section failures (truncation, count-exceeds-max, string-too-long, etc.).
- **V5Protocol.cs**: wire-format constants: sentinel magic (`BotDsV05`), region total size (16400), buffer slot size (8192), header struct (`V5BufferHeader`, 28 bytes, `[StructLayout(Sequential, Pack=1)]`), sentinel struct (`V5Sentinel`, 16 bytes), TLV section header, ability/aura fixed records, parsed domain records (`ParsedV5Frame`, `ParsedProviderInfo`, `ParsedUnitState`, `ParsedAbilityState`, `ParsedAuraState`).

## StableReader

`StableReader` manages the double-buffer protocol on each read cycle:

1. Copies each 8192-byte slot into a local buffer. `ReadProcessMemory` is not an atomic publication primitive; CRC validation detects inconsistent snapshots only when the producer follows the stable mutable-region contract.
2. Validates CRC on each buffer independently.
3. Parses each CRC-valid buffer independently.
4. Selects the newer frame via `V5FrameSelector.Select`: same-session wrap-aware sequence ordering (RFC 1982) or cross-session producer-frame-time comparison.
5. Evaluates session/sequence continuity via `SessionTracker`.
6. Checks frame staleness against `localMaxAge` (or the emitter's reported `MaxTelemetryAgeMs`).
7. Maps transport-level health to `ProviderHealth` (Healthy, Degraded, Stale, Faulted, Disconnected).

### SessionTracker

- Tracks `SessionId`, `LastSequence`, `TrustedHighWaterMark`, and degraded state.
- Sequence ordering uses IETF RFC 1982 uint wrap arithmetic (half-range comparison).
- Ambiguous ordering (`diff == 0x80000000`) is **fail-closed**.
- Degraded mode: rejects any sequence not clearly after the trusted high-water mark until recovery.
- Outputs `ContinuityResult`: Valid, Gap, SequenceDecrement, SessionRestart, SequenceWrap.

### Staleness

Age is computed from the last sequence-change timestamp using the injected `TimeProvider` (monotonic). A frame is stale when `age > localMaxAge` (or the emitter's `MaxTelemetryAgeMs`, default 500ms if the emitter reports zero).

### Immutable Lua backing-address limitation

The Reader uses `ReadProcessMemory`; it cannot follow Lua heap references. `V5ScannerService` can relocate a region after a cached frame becomes invalid or stale, but that only handles occasional movement when a complete newer image remains discoverable. The current addon may create a new string allocation for every field write, leave stale copies until garbage collection, and expose no in-place CRC-last update. Repeated scans cannot make that publication model reliable. Live operation therefore requires a stable mutable region or a transport redesign.

## ProcessSelector: exact PID and name rules

`ProcessSelector` is an immutable `record` with two optional fields:

| Field        | Rules |
|-------------|-------|
| `ProcessId` | Positive `int?`. Authoritative when present. |
| `ProcessName`| Normalized basename (`.exe` stripped, lowercased). Rejects path separators, wildcards, empty-after-normalization. |

**Resolution rules:**

1. **PID only** (`ProcessId` set, `ProcessName` null): Opens the PID directly. Name verification is **not** performed; the real Windows factory trusts the caller, and injected factories handle identity themselves.
2. **PID + Name** (`ProcessId` and `ProcessName` both set): Opens the PID, then verifies the process image basename matches `ProcessName`. Verification uses `QueryFullProcessImageNameW` (real factory) or `Process.GetProcessById` (fallback path). Mismatch throws `ReaderException(ProcessNameMismatch)`.
3. **Name only** (`ProcessName` set, `ProcessId` null): Enumerates all processes via `Process.GetProcesses()`. Requires **exactly one** match. Zero matches return `ProcessNotFound`; multiple matches return `ProcessAmbiguous`. Name-only resolution requires the default `WindowsMemoryReaderFactory`; injected test factories throw `ProcessNotFound`.
4. **Both null**: Invalid; `IsValid` returns `false`.

**No default RIFT process name.** Callers must supply an explicit `ProcessSelector`; the library never assumes a particular executable name.

`NormalizeName(string name)` strips `.exe` suffix (case-insensitive) and lowercases. `NameMatches(string imageName)` normalizes both sides before comparison.

## V5ScannerService: construction and local max age

```csharp
public V5ScannerService(
    ProcessSelector selector,
    TimeSpan localMaxAge,
    SentinelScannerOptions? scannerOptions = null,
    IMemoryReaderFactory? readerFactory = null,
    TimeProvider? timeProvider = null)
```

- **`selector`** – Must be valid (`IsValid == true`), otherwise throws `ArgumentException`.
- **`localMaxAge`** – Must be positive (`> TimeSpan.Zero`), otherwise throws `ArgumentOutOfRangeException`. Overrides the emitter's `MaxTelemetryAgeMs` for staleness checks. Choose a value that balances freshness against the game's frame rate (e.g., 500ms–2000ms).
- **`scannerOptions`** – Optional `SentinelScannerOptions` (defaults: `ChunkSizeBytes = 1 MiB`, `MaxRawMatches = 256`, `MaxCandidates = 32`). Must pass `Validate()`.
- **`readerFactory`** – Optional `IMemoryReaderFactory` (defaults to `WindowsMemoryReaderFactory`). The primary test seam.
- **`timeProvider`** – Optional `TimeProvider` (defaults to `TimeProvider.System`). Injectable for deterministic time in tests.

### Read cycle

`ScannerReadResult Read(CancellationToken ct)` is the main entry point. Each call:

1. Acquires the internal `SemaphoreSlim` (serializes reads; not re-entrant).
2. Ensures attachment (re-attaches if the process exited or was never opened).
3. Checks process liveness via `WaitForSingleObject(handle, 0)`.
4. Validates the cached candidate address (re-reads sentinel + parses both slots). On mismatch, invalidates the cache.
5. If no valid cache: runs a full `V5SentinelScanner.Scan` over all readable committed regions.
6. Calls `V5FrameSelector.SelectBest` to pick the best candidate from scan results.
7. Performs a double-buffer `StableReader.Read`.
8. If stale: attempts a relocation scan (full re-scan), subject to stale-backoff gating.
9. Returns `ScannerReadResult` with the frame, health, metrics, and failure code.

### Region scanning

`V5SentinelScanner.Scan` enumerates readable committed regions via `IMemoryReader.QueryReadableRegions`, then scans each region in configurable chunks (`ChunkSizeBytes`) for the `BotDsV05` magic bytes. Raw magic matches are validated by reading the full 16-byte sentinel header (checking `TotalSize == 16400` and `BufferSlotSize == 8192`). Valid sentinels have both 8192-byte buffer slots read and CRC-validated. Candidates with at least one valid frame are collected, deduplicated by protocol identity, and capped at `MaxCandidates`.

### Candidate ambiguity

`V5FrameSelector.SelectBest` resolves multiple candidates:
- Deduplicates candidates with equivalent complete header identity.
- Finds a unique candidate that is strictly newer than all others (by session + sequence or producer frame time).
- Returns `Ambiguous` if two or more conflicting candidates are tied for newest, or if a single candidate's own two slots are ambiguous.
- Ambiguous results map to `ReaderFailureCode.CandidateAmbiguous`.

### Relocation

When the `StableReader` reports `ProviderHealth.Stale` and no full scan has been performed in the current read cycle, the service runs a full **relocation scan** with `V5SentinelScanner.Scan` to find a newer buffer location. The previously cached stale address is retained during the scan so that if the scan is incomplete, the stale candidate can still be revalidated on the next cycle.

### Stale backoff

After a relocation scan still yields a stale result, `V5ScannerService` activates a 5-second **stale backoff**. During backoff, subsequent reads use the cached (stale but validated) candidate without triggering another full scan. This prevents a permanently-stale candidate from triggering a scan on every read cycle. Backoff resets when any read returns non-stale.

### Failure codes

All failures are surfaced through `ScannerReadResult.FailureCode` (`ReaderFailureCode` enum):

| Code | Meaning |
|------|---------|
| `None` | Success (usable frame). |
| `InvalidSelector` | ProcessSelector did not pass validation. |
| `ProcessNotFound` | PID not found or name-only resolution found zero matches. |
| `ProcessAmbiguous` | Name-only resolution found multiple matches. |
| `ProcessNameMismatch` | PID+Name: image basename did not match. |
| `AccessDenied` | Windows denied the requested process rights. |
| `OpenFailure` | Any other OpenProcess or attachment failure. |
| `UnsupportedArchitecture` | Host or target not AMD64, or host not 64-bit. |
| `ProcessExit` | Target process has exited. |
| `QueryFailure` | Region enumeration failed (VirtualQueryEx error). |
| `ReadFailure` | ReadProcessMemory failed. |
| `SentinelNotFound` | No `BotDsV05` magic found in any readable region. |
| `CandidateInvalid` | Sentinel found but no valid frame in either slot. |
| `CandidateAmbiguous` | Multiple conflicting candidates, or ambiguous slot ordering. |
| `CandidateLimitExceeded` | Raw magic match or candidate count exceeded configured limits. |
| `StaleTelemetry` | Frame has not advanced within `localMaxAge`. |
| `ContinuityDegraded` | Sequence gap or wrap detected; frame is diagnostic and fails closed for action evaluation. |
| `SequenceDiscontinuity` | Sequence went backwards within a session (frame faulted). |
| `InternalError` | Unexpected exception during read. |

Diagnostic results (`IsUsable == false`) carry the frame data alongside the failure code; callers can inspect `FailureCode` and `ReadResult.FailureDetail`.

### Metrics

`ScannerMetrics` is a privacy-safe immutable record. All fields are counters and durations; no addresses, paths, names, or memory content are exposed.

Key counters: `FullScanCount`, `CacheHitCount`, `CacheMissCount`, `RegionsEnumerated`, `EligibleRegionsScanned`, `BytesScanned`, `RawMagicMatchesFound`, `ExactSentinelMatchesFound`, `ValidCandidatesFound`, `CandidateLimitHits`, `ReadFailures`, `AttachmentCount`, `ReadCycleFailures`. Durations: `TotalScanDuration`, `TotalReadDuration`. Timestamps: `LastScanUtc`, `LastReadCycleUtc`.

Access via `V5ScannerService.Metrics` (acquires and releases the internal gate internally).

## Minimal Windows rights

`NativeMethods.MinimalProcessRights` requests exactly three access rights:
- `PROCESS_QUERY_INFORMATION` (0x0400): for `QueryFullProcessImageNameW` and `IsWow64Process2`.
- `PROCESS_VM_READ` (0x0010): for `ReadProcessMemory` and `VirtualQueryEx`.
- `SYNCHRONIZE` (0x00100000): for `WaitForSingleObject` liveness checks.

No write, create-thread, or VM-operation rights are requested.

## x64 requirements

- **Host must be AMD64**: `GetNativeSystemInfo` must report `PROCESSOR_ARCHITECTURE_AMD64` (9) and the calling process must be 64-bit (`nint.Size == 8`).
- **Target must be AMD64**: Both the process machine and native machine from `IsWow64Process2` must be `IMAGE_FILE_MACHINE_AMD64` (0x8664).
- Violations throw `ReaderException(UnsupportedArchitecture)` at attachment time.

## Cancellation and disposal

- `V5ScannerService.Read(CancellationToken ct)` observes `ct` at multiple points: before attachment, after each region in a scan, before and after stable reads. Throws `OperationCanceledException` on cancellation.
- `V5ScannerService` implements `IDisposable`. `Dispose()` is idempotent and thread-safe (fast-path volatile check before acquiring the gate). Disposal detaches the reader, resets internal state, and nulls the `StableReader`. The internal `SemaphoreSlim` is **not** disposed (retained for process lifetime).
- After disposal, `Read()` throws `ObjectDisposedException`.

## Test seams

The library supports deterministic, in-process testing through two injectable abstractions:

1. **`IMemoryReaderFactory`**: `V5ScannerService` accepts an optional factory (defaults to `WindowsMemoryReaderFactory`). Tests inject `FakeMemoryReaderFactory` backed by `FakeMemoryCatalog` (page-based fake process memory). A `ThrowingMemoryReaderFactory` is also available for failure-injection tests.
2. **`TimeProvider`**: injected into `V5ScannerService` and `StableReader`. Tests use `FakeTimeProvider` to control monotonic timestamps for deterministic staleness and backoff behavior.

`BotDs.Tests` has `InternalsVisibleTo` access via the `.csproj`. Test fakes (`FakeMemoryReader`, `FakeMemoryReaderFactory`, `FakeMemoryCatalog`) are defined in the test project, not in the Reader library.

## Not yet registered in BotDs.App

`V5ScannerService` is **not** registered in the `BotDs.App` DI container (`Program.cs`). The App currently registers `SnapshotPublisher`, `ProfileService`, `ControllerStateMachine`, and `EvaluatorLoop`, but no Reader components. Integration into the hosted evaluator loop is planned but not yet implemented.

## SentinelScannerOptions

```csharp
public sealed record SentinelScannerOptions
{
    public int ChunkSizeBytes { get; init; } = 1_048_576;  // 256 .. 64 MiB
    public int MaxRawMatches { get; init; } = 256;           // 1 .. 10_000
    public int MaxCandidates { get; init; } = 32;             // 1 .. 1_000
}
```

`Validate()` enforces the ranges above. Larger chunk sizes reduce `ReadProcessMemory` calls but increase per-read buffer allocation; larger limits tolerate more noise at the cost of scan completeness (limits exceeded set `ScanIncompleteCause` flags).

## Usage example

```csharp
using BotDs.Reader;
using BotDs.Reader.V5;

// Supply the actual game executable basename; the library has no default.
var selector = new ProcessSelector
{
    ProcessName = "actual-process-name" // Case-insensitive; ".exe" suffix optional.
};

// Create the scanner service with a 2-second local max age.
using var scanner = new V5ScannerService(
    selector,
    localMaxAge: TimeSpan.FromSeconds(2));

// Read loop
using var cts = new CancellationTokenSource();

while (!cts.Token.IsCancellationRequested)
{
    ScannerReadResult result = scanner.Read(cts.Token);

    if (result.IsUsable && result.Frame is { } frame)
    {
        Console.WriteLine($"Session={frame.Provider?.SessionId}, Seq={frame.Header.Sequence}");
        if (frame.Player is { } player)
            Console.WriteLine($"Player: {player.Name} Lv{player.Level} HP={player.HealthCurrent}/{player.HealthMaximum}");
        if (frame.Target is { } target)
            Console.WriteLine($"Target: {target.Name} HP={target.HealthCurrent}/{target.HealthMaximum} Relation={target.Relation}");
    }
    else
    {
        Console.WriteLine($"Unusable: code={result.FailureCode}, detail={result.ReadResult.FailureDetail}");
    }

    // Inspect cumulative metrics (no addresses exposed)
    ScannerMetrics m = scanner.Metrics;
    Console.WriteLine($"Scans={m.FullScanCount} CacheHits={m.CacheHitCount} Failures={m.ReadCycleFailures}");

    await Task.Delay(100, cts.Token);
}
```

### Test example (inside BotDs.Tests using its internal fakes)

```csharp
// Build a fake process with a valid sentinel and slot
var catalog = new FakeMemoryCatalog();
byte[] sentinel = ScannerTestHelpers.BuildSentinelBytes();
byte[] slot = ScannerTestHelpers.BuildSlot(sequence: 1, sessionId: Guid.NewGuid());
byte[] region = new byte[V5Constants.RegionTotalSize];
Array.Copy(sentinel, 0, region, 0, sentinel.Length);
Array.Copy(slot, 0, region, V5Constants.BufferAOffset, slot.Length);
catalog.AddPage(baseAddress: (nint)0x10000, region);

var factory = new FakeMemoryReaderFactory();
factory.RegisterProcess(pid: 42, catalog);

var selector = new ProcessSelector { ProcessId = 42 };

using var scanner = new V5ScannerService(
    selector,
    localMaxAge: TimeSpan.FromSeconds(10),
    readerFactory: factory);

ScannerReadResult result = scanner.Read(CancellationToken.None);
// result.IsUsable == true
// result.Frame!.Header.Sequence == 1
```
