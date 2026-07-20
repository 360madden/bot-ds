namespace BotDs.Reader;

/// <summary>
/// Privacy-safe scanner metrics. Counters and durations only.
/// No addresses, paths, names, or memory content are ever exposed.
/// </summary>
public sealed record ScannerMetrics
{
    /// <summary>Number of full relocation scans performed.</summary>
    public long FullScanCount { get; init; }

    /// <summary>Number of cache-hit reads (no scan needed).</summary>
    public long CacheHitCount { get; init; }

    /// <summary>Number of cache-miss reads that triggered a scan.</summary>
    public long CacheMissCount { get; init; }

    /// <summary>Total memory regions enumerated across all scans.</summary>
    public long RegionsEnumerated { get; init; }

    /// <summary>Total eligible regions scanned (readable committed).</summary>
    public long EligibleRegionsScanned { get; init; }

    /// <summary>Total bytes scanned across all scans.</summary>
    public long BytesScanned { get; init; }

    /// <summary>Total raw magic matches found (before sentinel-header validation).</summary>
    public long RawMagicMatchesFound { get; init; }

    /// <summary>Total exact sentinel headers found (before slot validation).</summary>
    public long ExactSentinelMatchesFound { get; init; }

    /// <summary>Number of valid candidates discovered.</summary>
    public long ValidCandidatesFound { get; init; }

    /// <summary>Number of times candidate limit was hit.</summary>
    public long CandidateLimitHits { get; init; }

    /// <summary>Number of failed memory reads.</summary>
    public long ReadFailures { get; init; }

    /// <summary>Number of process attachments performed.</summary>
    public long AttachmentCount { get; init; }

    /// <summary>Cumulative monotonic time spent in full scans.</summary>
    public TimeSpan TotalScanDuration { get; init; }

    /// <summary>Cumulative monotonic time spent in read cycles.</summary>
    public TimeSpan TotalReadDuration { get; init; }

    /// <summary>Timestamp of the most recent scan completion.</summary>
    public DateTime LastScanUtc { get; init; }

    /// <summary>Number of failed read cycles (any cause).</summary>
    public long ReadCycleFailures { get; init; }

    /// <summary>Timestamp of the most recent read cycle.</summary>
    public DateTime LastReadCycleUtc { get; init; }

    /// <summary>Number of small-window rescans that found the sentinel.</summary>
    public long SmallWindowHits { get; init; }

    /// <summary>Number of small-window rescans that failed to find the sentinel.</summary>
    public long SmallWindowMisses { get; init; }

    public static ScannerMetrics Empty => new();

    public ScannerMetrics WithIncrements(
        long? fullScanDelta = null,
        long? cacheHitDelta = null,
        long? cacheMissDelta = null,
        long? regionsEnumeratedDelta = null,
        long? eligibleScannedDelta = null,
        long? bytesScannedDelta = null,
        long? rawMagicMatchesDelta = null,
        long? exactSentinelMatchesDelta = null,
        long? validCandidatesDelta = null,
        long? candidateLimitHitsDelta = null,
        long? readFailuresDelta = null,
        long? attachmentDelta = null,
        TimeSpan? scanDurationDelta = null,
        TimeSpan? readDurationDelta = null,
        long? readCycleFailuresDelta = null,
        long? smallWindowHitsDelta = null,
        long? smallWindowMissesDelta = null,
        DateTime? lastScanUtc = null,
        DateTime? lastReadCycleUtc = null)
    {
        return this with
        {
            FullScanCount = FullScanCount + (fullScanDelta ?? 0),
            CacheHitCount = CacheHitCount + (cacheHitDelta ?? 0),
            CacheMissCount = CacheMissCount + (cacheMissDelta ?? 0),
            RegionsEnumerated = RegionsEnumerated + (regionsEnumeratedDelta ?? 0),
            EligibleRegionsScanned = EligibleRegionsScanned + (eligibleScannedDelta ?? 0),
            BytesScanned = BytesScanned + (bytesScannedDelta ?? 0),
            RawMagicMatchesFound = RawMagicMatchesFound + (rawMagicMatchesDelta ?? 0),
            ExactSentinelMatchesFound = ExactSentinelMatchesFound + (exactSentinelMatchesDelta ?? 0),
            ValidCandidatesFound = ValidCandidatesFound + (validCandidatesDelta ?? 0),
            CandidateLimitHits = CandidateLimitHits + (candidateLimitHitsDelta ?? 0),
            ReadFailures = ReadFailures + (readFailuresDelta ?? 0),
            AttachmentCount = AttachmentCount + (attachmentDelta ?? 0),
            TotalScanDuration = TotalScanDuration + (scanDurationDelta ?? TimeSpan.Zero),
            TotalReadDuration = TotalReadDuration + (readDurationDelta ?? TimeSpan.Zero),
            ReadCycleFailures = ReadCycleFailures + (readCycleFailuresDelta ?? 0),
            SmallWindowHits = SmallWindowHits + (smallWindowHitsDelta ?? 0),
            SmallWindowMisses = SmallWindowMisses + (smallWindowMissesDelta ?? 0),
            LastScanUtc = lastScanUtc ?? LastScanUtc,
            LastReadCycleUtc = lastReadCycleUtc ?? LastReadCycleUtc,
        };
    }

    /// <summary>
    /// Copy with updated timestamps only (no counter changes).
    /// </summary>
    public ScannerMetrics WithTimestamps(DateTime lastScanUtc, DateTime lastReadCycleUtc)
    {
        return this with { LastScanUtc = lastScanUtc, LastReadCycleUtc = lastReadCycleUtc };
    }
}
