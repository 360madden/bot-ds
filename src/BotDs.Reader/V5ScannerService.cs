using System.ComponentModel;
using System.Diagnostics;
using BotDs.Core;
using BotDs.Reader.V5;

namespace BotDs.Reader;

public sealed record ScannerReadResult
{
    public StableReadResult ReadResult { get; init; } = null!;
    public int AttachmentPid { get; init; }
    public long AttachmentGeneration { get; init; }
    public ScannerMetrics Metrics { get; init; } = ScannerMetrics.Empty;
    public ReaderFailureCode FailureCode { get; init; }
    public bool IsUsable => ReadResult.IsUsable;
    public ParsedV5Frame? Frame => ReadResult.Frame;

    public static ScannerReadResult Healthy(StableReadResult rr, int pid, long gen, ScannerMetrics m)
    {
        if (!rr.IsUsable) throw new ArgumentException("Not usable", nameof(rr));
        return new() { ReadResult = rr, AttachmentPid = pid, AttachmentGeneration = gen, Metrics = m, FailureCode = ReaderFailureCode.None };
    }

    public static ScannerReadResult Failure(ProviderHealth h, ReaderFailureCode c, string? d, int pid, long gen, ScannerMetrics m)
    {
        if (c == ReaderFailureCode.None) throw new ArgumentException("Need code", nameof(c));
        var rr = h switch
        {
            ProviderHealth.Disconnected => StableReadResult.Disconnected(Sanitize(d)),
            ProviderHealth.Faulted => StableReadResult.Faulted(Sanitize(d)),
            _ => throw new ArgumentOutOfRangeException(nameof(h)),
        };
        return new() { ReadResult = rr, AttachmentPid = pid, AttachmentGeneration = gen, Metrics = m, FailureCode = c };
    }

    public static ScannerReadResult Diagnostic(StableReadResult rr, ReaderFailureCode c, int pid, long gen, ScannerMetrics m)
    {
        if (rr.IsUsable) throw new ArgumentException("Usable not diagnostic", nameof(rr));
        if (c == ReaderFailureCode.None) throw new ArgumentException("Need code", nameof(c));
        return new() { ReadResult = rr with { FailureDetail = Sanitize(rr.FailureDetail) }, AttachmentPid = pid, AttachmentGeneration = gen, Metrics = m, FailureCode = c };
    }

    internal static string Sanitize(string? d) => ReaderDiagnosticSanitizer.Sanitize(d);
}

public sealed class V5ScannerService : IDisposable
{
    private readonly ProcessSelector _selector;
    private readonly SentinelScannerOptions _scannerOpts;
    private readonly TimeSpan _localMaxAge;
    private readonly IMemoryReaderFactory _readerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IMemoryReader? _reader;
    private StableReader? _stableReader;
    private nint _cachedAddr;
    private bool _hasCached;
    private int _attachPid;
    private long _attachGen;
    private ScannerMetrics _metrics = ScannerMetrics.Empty;
    private volatile bool _disposed;

    // Stale backoff: after a relocation scan still yields stale, suppress rescans
    // for a bounded interval to prevent a permanent-stale candidate from triggering
    // a full scan on every read cycle.
    private bool _staleBackoffActive;
    private long _lastStaleTimestamp;
    private static readonly TimeSpan StaleBackoffInterval = TimeSpan.FromSeconds(5);

    public V5ScannerService(ProcessSelector selector, TimeSpan localMaxAge,
        SentinelScannerOptions? scannerOptions = null, IMemoryReaderFactory? readerFactory = null,
        TimeProvider? timeProvider = null)
    {
        if (!selector.IsValid) throw new ArgumentException("Invalid selector", nameof(selector));
        if (localMaxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(localMaxAge));
        _selector = selector; _localMaxAge = localMaxAge;
        _scannerOpts = scannerOptions ?? new SentinelScannerOptions(); _scannerOpts.Validate();
        _readerFactory = readerFactory ?? new WindowsMemoryReaderFactory();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stableReader = new StableReader(_localMaxAge, _timeProvider);
    }

    public int AttachmentPid => Volatile.Read(ref _attachPid);
    public long AttachmentGeneration => Volatile.Read(ref _attachGen);
    public ScannerMetrics Metrics { get { try { _gate.Wait(); return _metrics; } finally { _gate.Release(); } } }

    public ScannerReadResult Read(CancellationToken ct = default)
    {
        _gate.Wait(ct);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(V5ScannerService));
            var d = new MetricDeltas();
            long t0 = _timeProvider.GetTimestamp();
            try
            {
                ct.ThrowIfCancellationRequested();

                if (!EnsureAttachedLocked())
                    return Fail(d, ProviderHealth.Disconnected, ReaderFailureCode.ProcessNotFound, "No attach", t0);
                if (!_reader!.CheckLiveness())
                { DetachLocked(); return Fail(d, ProviderHealth.Disconnected, ReaderFailureCode.ProcessExit, "Exited", t0); }

                V5Candidate? cand = null;
                bool usedCache = false;

                if (_hasCached)
                {
                    cand = V5SentinelScanner.ValidateCandidate(_reader, _cachedAddr, ct);
                    if (cand is not null) usedCache = true;
                    else { _hasCached = false; _cachedAddr = 0; }
                }

                if (cand is null)
                {
                    d.CacheMiss = 1;
                    var sr = V5SentinelScanner.Scan(_reader, _scannerOpts, ct);
                    d.FullScan = 1; d.Regions = sr.TotalRegions; d.Eligible = sr.TotalRegions;
                    d.Bytes = sr.BytesScanned; d.RawMatch = sr.RawMagicMatches; d.ExactSent = sr.ExactSentinelMatches;
                    d.Candidates = sr.Candidates.Count; d.ReadFails += sr.ReadFailures; d.ScanDur = sr.Duration;

                    if (sr.Incomplete)
                    {
                        d.CacheMiss = 1;
                        if ((sr.IncompleteCause & (ScanIncompleteCause.RawMatchLimitExceeded | ScanIncompleteCause.CandidateLimitExceeded)) != 0)
                            d.CandidateLimit = 1;
                        // Process-loss race: recheck liveness; if dead, detach + Disconnected.
                        if (!_reader.CheckLiveness())
                        { DetachLocked(); return Fail(d, ProviderHealth.Disconnected, ReaderFailureCode.ProcessExit, "Process exited during scan", t0); }
                        return Fail(d, ProviderHealth.Faulted, MapIncomplete(sr.IncompleteCause), "Scan incomplete", t0);
                    }
                    if (sr.Candidates.Count == 0)
                    {
                        d.CacheMiss = 1;
                        if (sr.ExactSentinelMatches > 0) return Fail(d, ProviderHealth.Faulted, ReaderFailureCode.CandidateInvalid, "Slot invalid", t0);
                        return Fail(d, ProviderHealth.Disconnected, ReaderFailureCode.SentinelNotFound, "No sentinel", t0);
                    }

                    var sel = V5FrameSelector.SelectBest(sr.Candidates, out V5Candidate? best);
                    if (sel != V5SelectionResult.Selected || best is null)
                    {
                        d.CacheMiss = 1;
                        var c = sel == V5SelectionResult.Ambiguous ? ReaderFailureCode.CandidateAmbiguous : ReaderFailureCode.CandidateInvalid;
                        return Fail(d, ProviderHealth.Faulted, c, "Selection failed", t0);
                    }
                    cand = best; _cachedAddr = cand.BaseAddress; _hasCached = true;
                }

                ct.ThrowIfCancellationRequested();
                StableReadResult srr = DoStableRead(cand);
                ct.ThrowIfCancellationRequested();

                if (srr.TransportHealth == ProviderHealth.Disconnected)
                { _hasCached = false; _cachedAddr = 0; d.CacheMiss = 1; return Fail(d, ProviderHealth.Faulted, ReaderFailureCode.InternalError, "Transport disconnect", t0); }

                if (srr.TransportHealth == ProviderHealth.Stale)
                {
                    long staleNow = _timeProvider.GetTimestamp();

                    // This read already relocated the candidate. Keep that stale
                    // candidate cached and do not run a second full scan in one cycle.
                    if (d.FullScan > 0)
                    {
                        BeginStaleBackoff(staleNow);
                        return StaleDiagnostic(d, srr, t0);
                    }

                    TimeSpan elapsed = _staleBackoffActive
                        ? _timeProvider.GetElapsedTime(_lastStaleTimestamp)
                        : TimeSpan.MaxValue;
                    if (_staleBackoffActive
                        && elapsed >= TimeSpan.Zero
                        && elapsed < StaleBackoffInterval)
                    {
                        d.CacheHit = usedCache ? 1 : 0;
                        return StaleDiagnostic(d, srr, t0);
                    }

                    // Retain the validated stale address during relocation. If the
                    // scan is incomplete, backoff can still revalidate this candidate
                    // instead of initiating a full scan on every read.
                    d.CacheMiss = 1;
                    var sr2 = V5SentinelScanner.Scan(_reader, _scannerOpts, ct);
                    BeginStaleBackoff(_timeProvider.GetTimestamp());
                    d.FullScan++; d.Regions += sr2.TotalRegions; d.Eligible += sr2.TotalRegions;
                    d.Bytes += sr2.BytesScanned; d.RawMatch += sr2.RawMagicMatches; d.ExactSent += sr2.ExactSentinelMatches;
                    d.Candidates += sr2.Candidates.Count; d.ReadFails += sr2.ReadFailures; d.ScanDur += sr2.Duration;

                    if (sr2.Incomplete)
                    {
                        if ((sr2.IncompleteCause & (ScanIncompleteCause.RawMatchLimitExceeded | ScanIncompleteCause.CandidateLimitExceeded)) != 0)
                            d.CandidateLimit = 1;
                        if (!_reader.CheckLiveness())
                        { DetachLocked(); return Fail(d, ProviderHealth.Disconnected, ReaderFailureCode.ProcessExit, "Process exited during relo scan", t0); }
                        return Fail(d, ProviderHealth.Faulted, MapIncomplete(sr2.IncompleteCause), "Relo incomplete", t0);
                    }
                    if (sr2.Candidates.Count == 0)
                    {
                        if (sr2.ExactSentinelMatches > 0) return Fail(d, ProviderHealth.Faulted, ReaderFailureCode.CandidateInvalid, "Relo slot invalid", t0);
                        return Fail(d, ProviderHealth.Disconnected, ReaderFailureCode.SentinelNotFound, "Relo no sentinel", t0);
                    }

                    var sel2 = V5FrameSelector.SelectBest(sr2.Candidates, out V5Candidate? relo);
                    if (sel2 != V5SelectionResult.Selected || relo is null)
                    {
                        var c2 = sel2 == V5SelectionResult.Ambiguous ? ReaderFailureCode.CandidateAmbiguous : ReaderFailureCode.CandidateInvalid;
                        return Fail(d, ProviderHealth.Faulted, c2, "Relo selection failed", t0);
                    }
                    cand = relo; _cachedAddr = cand.BaseAddress; _hasCached = true;
                    ct.ThrowIfCancellationRequested();
                    srr = DoStableRead(cand);
                    ct.ThrowIfCancellationRequested();
                }

                if (srr.TransportHealth == ProviderHealth.Disconnected)
                { _hasCached = false; _cachedAddr = 0; d.CacheMiss = 1; return Fail(d, ProviderHealth.Faulted, ReaderFailureCode.InternalError, "Transport disc", t0); }
                if (srr.TransportHealth == ProviderHealth.Faulted)
                { _hasCached = false; _cachedAddr = 0; }

                if (srr.TransportHealth != ProviderHealth.Stale)
                    ResetStaleBackoff();

                if (usedCache && d.FullScan == 0)
                    d.CacheHit = 1;
                d.ReadCycleFails = srr.IsUsable ? 0 : 1;
                _metrics = Apply(d, t0);
                if (srr.IsUsable) return ScannerReadResult.Healthy(srr, _attachPid, _attachGen, _metrics);
                ReaderFailureCode fc = srr.TransportHealth switch
                {
                    ProviderHealth.Disconnected => ReaderFailureCode.ProcessExit,
                    ProviderHealth.Faulted => srr.Continuity == ContinuityResult.SequenceDecrement ? ReaderFailureCode.SequenceDiscontinuity : ReaderFailureCode.CandidateInvalid,
                    ProviderHealth.Stale => ReaderFailureCode.StaleTelemetry,
                    ProviderHealth.Degraded => ReaderFailureCode.ContinuityDegraded,
                    _ => ReaderFailureCode.InternalError,
                };
                return ScannerReadResult.Diagnostic(srr, fc, _attachPid, _attachGen, _metrics);
            }
            catch (OperationCanceledException) { d.ReadCycleFails = 1; _metrics = Apply(d, t0); throw; }
            catch (ReaderException ex)
            {
                d.ReadCycleFails = 1; _hasCached = false; _cachedAddr = 0;
                if (ex.FailureCode is ReaderFailureCode.ReadFailure or ReaderFailureCode.ProcessExit) d.ReadFails++;
                bool disc = ex.FailureCode is ReaderFailureCode.ProcessExit or ReaderFailureCode.ProcessNotFound;
                if (disc) DetachLocked();
                return Fail(d, disc ? ProviderHealth.Disconnected : ProviderHealth.Faulted, ex.FailureCode, ex.Message, t0);
            }
            catch (Exception ex)
            { d.ReadCycleFails = 1; return Fail(d, ProviderHealth.Faulted, ReaderFailureCode.InternalError, ex.Message, t0); }
        }
        finally { _gate.Release(); }
    }

    private StableReadResult DoStableRead(V5Candidate c)
    {
        var la = new byte[V5Constants.BufferSlotSize]; var lb = new byte[V5Constants.BufferSlotSize];
        return _stableReader!.Read(
            s => { _reader!.ReadExact(c.BaseAddress + V5Constants.BufferAOffset, la, V5Constants.BufferSlotSize); la.CopyTo(s); },
            s => { _reader!.ReadExact(c.BaseAddress + V5Constants.BufferBOffset, lb, V5Constants.BufferSlotSize); lb.CopyTo(s); },
            _timeProvider.GetUtcNow());
    }

    private ScannerReadResult Fail(MetricDeltas d, ProviderHealth h, ReaderFailureCode c, string detail, long t0)
    { d.ReadCycleFails = 1; _metrics = Apply(d, t0); return ScannerReadResult.Failure(h, c, Sanitize(detail), _attachPid, _attachGen, _metrics); }

    private ScannerReadResult StaleDiagnostic(MetricDeltas d, StableReadResult result, long t0)
    {
        d.ReadCycleFails = 1;
        _metrics = Apply(d, t0);
        return ScannerReadResult.Diagnostic(result, ReaderFailureCode.StaleTelemetry,
            _attachPid, _attachGen, _metrics);
    }

    private void BeginStaleBackoff(long timestamp)
    {
        _staleBackoffActive = true;
        _lastStaleTimestamp = timestamp;
    }

    private void ResetStaleBackoff()
    {
        _staleBackoffActive = false;
        _lastStaleTimestamp = 0;
    }

    private ScannerMetrics Apply(MetricDeltas d, long t0) => _metrics.WithIncrements(
        fullScanDelta: d.FullScan, cacheHitDelta: d.CacheHit, cacheMissDelta: d.CacheMiss,
        regionsEnumeratedDelta: d.Regions, eligibleScannedDelta: d.Eligible, bytesScannedDelta: d.Bytes,
        rawMagicMatchesDelta: d.RawMatch, exactSentinelMatchesDelta: d.ExactSent,
        validCandidatesDelta: d.Candidates, candidateLimitHitsDelta: d.CandidateLimit,
        readFailuresDelta: d.ReadFails, scanDurationDelta: d.ScanDur,
        readDurationDelta: _timeProvider.GetElapsedTime(t0), readCycleFailuresDelta: d.ReadCycleFails,
        lastScanUtc: d.FullScan > 0 ? _timeProvider.GetUtcNow().UtcDateTime : _metrics.LastScanUtc,
        lastReadCycleUtc: _timeProvider.GetUtcNow().UtcDateTime);

    private struct MetricDeltas
    {
        public long FullScan, CacheHit, CacheMiss, Regions, Eligible, Bytes;
        public long RawMatch, ExactSent, Candidates, CandidateLimit, ReadFails, ReadCycleFails;
        public TimeSpan ScanDur;
    }

    private static ReaderFailureCode MapIncomplete(ScanIncompleteCause c)
    {
        if ((c & ScanIncompleteCause.RawMatchLimitExceeded) != 0) return ReaderFailureCode.CandidateLimitExceeded;
        if ((c & ScanIncompleteCause.CandidateLimitExceeded) != 0) return ReaderFailureCode.CandidateLimitExceeded;
        if ((c & ScanIncompleteCause.RegionEnumerationFailed) != 0) return ReaderFailureCode.QueryFailure;
        if ((c & ScanIncompleteCause.ReadFailure) != 0) return ReaderFailureCode.ReadFailure;
        return ReaderFailureCode.InternalError;
    }

    private bool EnsureAttachedLocked()
    {
        if (_reader is not null && _reader.CheckLiveness()) return true;
        DetachLocked();
        if (!TryResolve(out int pid)) return false;
        try
        {
            _reader = _readerFactory.Open(pid, _selector.ProcessName);
            _attachPid = pid; _attachGen++; _hasCached = false; _cachedAddr = 0;
            _stableReader?.Reset(); _metrics = _metrics.WithIncrements(attachmentDelta: 1);
            return true;
        }
        catch (ReaderException) { DetachLocked(); throw; }
        catch (Exception ex) { DetachLocked(); throw new ReaderException(ReaderFailureCode.OpenFailure, Sanitize(ex.Message), ex); }
    }

    private bool TryResolve(out int pid)
    {
        pid = 0;
        if (!_selector.IsValid) return false;
        if (_selector.ProcessId is > 0)
        {
            pid = _selector.ProcessId.Value;
            // Name verification via System.Diagnostics.Process is only needed
            // when using the default (real Windows) factory. Injected factories
            // handle identity assertion themselves.
            if (_selector.ProcessName is not null && _readerFactory is WindowsMemoryReaderFactory)
                VerifyName(pid, _selector.ProcessName);
            return true;
        }
        if (_selector.ProcessName is not null)
        {
            // Name-only resolution requires real Process enumeration only
            // for the default factory
            if (_readerFactory is WindowsMemoryReaderFactory)
            { pid = ResolveName(_selector.ProcessName); return true; }
            // For test factories, we need to work differently — the caller must
            // resolve the PID. For now, name-only requires the real factory.
            throw new ReaderException(ReaderFailureCode.ProcessNotFound,
                "Name-only resolution requires the default Windows factory");
        }
        return false;
    }

    private static void VerifyName(int pid, string name)
    {
        Process? p = null;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { throw new ReaderException(ReaderFailureCode.ProcessNotFound, "PID not found"); }
        catch (InvalidOperationException) { throw new ReaderException(ReaderFailureCode.ProcessExit, "Exited"); }
        catch (Win32Exception we) { throw new ReaderException(NativeMethods.MapWin32Error(we.NativeErrorCode), $"win32={we.NativeErrorCode}"); }
        try
        {
            if (p is null) throw new ReaderException(ReaderFailureCode.ProcessNotFound, "PID not found");
            string actualName;
            try { actualName = p.ProcessName; }
            catch (Win32Exception exception)
            {
                throw new ReaderException(
                    NativeMethods.MapWin32Error(exception.NativeErrorCode),
                    $"win32={exception.NativeErrorCode}");
            }
            catch (InvalidOperationException)
            {
                throw new ReaderException(ReaderFailureCode.ProcessExit, "Exited");
            }
            if (!new ProcessSelector { ProcessName = name }.NameMatches(actualName))
                throw new ReaderException(ReaderFailureCode.ProcessNameMismatch, "Name mismatch");
        }
        finally { p?.Dispose(); }
    }

    private static int ResolveName(string name)
    {
        var tgt = ProcessSelector.NormalizeName(name);
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { throw new ReaderException(ReaderFailureCode.OpenFailure, Sanitize(ex.Message)); }
        Process? match = null; bool amb = false;
        foreach (var p in all)
        {
            bool nm; try { nm = ProcessSelector.NormalizeName(p.ProcessName) == tgt; } catch { nm = false; }
            if (!nm) { p.Dispose(); continue; }
            if (match is not null) { amb = true; p.Dispose(); } else match = p;
        }
        if (amb) { match?.Dispose(); throw new ReaderException(ReaderFailureCode.ProcessAmbiguous, "Ambiguous"); }
        if (match is null) throw new ReaderException(ReaderFailureCode.ProcessNotFound, "No match");
        int pid = match.Id; match.Dispose(); return pid;
    }

    private void DetachLocked()
    {
        var r = _reader; _reader = null; _attachPid = 0; _stableReader?.Reset();
        _hasCached = false; _cachedAddr = 0;
        ResetStaleBackoff();
        r?.Dispose();
    }

    public void Dispose()
    {
        // Fast-path check: if already disposed, return immediately without touching the semaphore.
        if (_disposed) return;

        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            DetachLocked();
            _stableReader = null;
        }
        finally { _gate.Release(); }
        // SemaphoreSlim retained for process lifetime; not disposed.
    }

    internal static string Sanitize(string? d) => ReaderDiagnosticSanitizer.Sanitize(d);
}
