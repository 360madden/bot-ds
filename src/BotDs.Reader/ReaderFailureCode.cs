namespace BotDs.Reader;

public enum ReaderFailureCode
{
    None = 0,

    // Selector
    InvalidSelector,
    ProcessNotFound,
    ProcessAmbiguous,
    ProcessNameMismatch,

    // Attachment
    AccessDenied,
    OpenFailure,
    UnsupportedArchitecture,

    // Runtime
    ProcessExit,
    QueryFailure,
    ReadFailure,

    // Scan
    SentinelNotFound,
    CandidateInvalid,
    CandidateAmbiguous,
    CandidateLimitExceeded,

    // Frame usability
    StaleTelemetry,
    ContinuityDegraded,
    SequenceDiscontinuity,

    // Internal
    InternalError,
}

public sealed class ReaderException : Exception
{
    public ReaderFailureCode FailureCode { get; }
    public ReaderException(ReaderFailureCode code, string? msg = null) : base(msg ?? code.ToString()) { FailureCode = code; }
    public ReaderException(ReaderFailureCode code, string? msg, Exception? inner) : base(msg ?? code.ToString(), inner) { FailureCode = code; }
}

internal static class ReaderDiagnosticSanitizer
{
    private const int MaxLen = 200;
    internal static string Sanitize(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        var s = System.Text.RegularExpressions.Regex.Replace(detail, @"0x[0-9A-Fa-f]{4,16}", "(addr)");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?:[A-Za-z]:[\\/]|\\\\)[^,;\r\n]*", "(path)");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b", "(id)");
        return s.Length > MaxLen ? s[..MaxLen] : s;
    }
}
