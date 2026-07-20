using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotDs.App.Services;

/// <summary>
/// Atomically persisted local settings. Reads from a JSON file on startup,
/// writes via atomic temp-file replacement. Combat-affecting settings are
/// guarded by the controller's configuration lease.
/// </summary>
public sealed record BotDsSettings
{
    public ScannerSettings Scanner { get; init; } = new();
    public EvaluatorSettings Evaluator { get; init; } = new();
    public DashboardSettings Dashboard { get; init; } = new();
    public LoggingSettings Logging { get; init; } = new();

    public static BotDsSettings Default => new();
}

public sealed record ScannerSettings
{
    public string? ProcessName { get; init; } = "rift";
    public int? ProcessId { get; init; }
    public int ReadIntervalMs { get; init; } = 50;
    public int LocalMaxAgeMs { get; init; } = 500;
}

public sealed record EvaluatorSettings
{
    public int MaximumTelemetryAgeMs { get; init; } = 500;
    public int EvaluationIntervalMs { get; init; } = 100;
}

public sealed record DashboardSettings
{
    public int UpdateIntervalMs { get; init; } = 2000;
    public int MaxLogEntries { get; init; } = 500;
}

public sealed record LoggingSettings
{
    public int RetainedFileCountLimit { get; init; } = 14;
}

/// <summary>
/// Loads, validates, and atomically persists BotDsSettings from a local JSON file.
/// Uses a temp-file + replace strategy to prevent corruption on write failure.
/// </summary>
public sealed class LocalSettingsService
{
    private readonly string _filePath;
    private readonly ILogger<LocalSettingsService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BotDsSettings _current = BotDsSettings.Default;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public LocalSettingsService(string filePath, ILogger<LocalSettingsService> log)
    {
        _filePath = filePath;
        _log = log;
        Load();
    }

    public BotDsSettings Current
    {
        get { try { _gate.Wait(); return _current; } finally { _gate.Release(); } }
    }

    /// <summary>
    /// Atomically persist merged settings. Merges the proposed settings
    /// with the current snapshot so partial updates don't reset unrelated
    /// sections. Returns null on success, or a list of validation errors.
    /// The previous snapshot is preserved on failure.
    /// </summary>
    public IReadOnlyList<string>? TrySave(BotDsSettings proposed)
    {
        var merged = Merge(Current, proposed);
        var errors = Validate(merged);
        if (errors.Count > 0) return errors;

        _gate.Wait();
        try
        {
            string temp = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(merged, JsonOptions);
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
            _current = merged;
            _log.LogInformation("Settings saved to {Path}", _filePath);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist settings");
            return new[] { "Failed to persist settings: " + ex.Message };
        }
        finally { _gate.Release(); }
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            _log.LogInformation("No settings file at {Path}; using defaults", _filePath);
            return;
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<BotDsSettings>(json, JsonOptions);
            if (loaded is not null)
            {
                var errors = Validate(loaded);
                if (errors.Count > 0)
                {
                    _log.LogWarning("Settings file has validation errors, using defaults: {Errors}",
                        string.Join("; ", errors));
                    return;
                }
                _current = loaded;
                _log.LogInformation("Settings loaded from {Path}", _filePath);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load settings from {Path}; using defaults", _filePath);
        }
    }

    /// <summary>
    /// Merge proposed settings into current, preferring non-default values from proposed.
    /// </summary>
    private static BotDsSettings Merge(BotDsSettings current, BotDsSettings proposed)
    {
        return current with
        {
            Scanner = MergeScanner(current.Scanner, proposed.Scanner),
            Evaluator = MergeEvaluator(current.Evaluator, proposed.Evaluator),
            Dashboard = MergeDashboard(current.Dashboard, proposed.Dashboard),
            Logging = MergeLogging(current.Logging, proposed.Logging),
        };
    }

    private static ScannerSettings MergeScanner(ScannerSettings current, ScannerSettings proposed)
    {
        return new ScannerSettings
        {
            ProcessName = proposed.ProcessName ?? current.ProcessName,
            ProcessId = proposed.ProcessId ?? current.ProcessId,
            ReadIntervalMs = proposed.ReadIntervalMs != 50 ? proposed.ReadIntervalMs : current.ReadIntervalMs,
            LocalMaxAgeMs = proposed.LocalMaxAgeMs != 500 ? proposed.LocalMaxAgeMs : current.LocalMaxAgeMs,
        };
    }

    private static EvaluatorSettings MergeEvaluator(EvaluatorSettings current, EvaluatorSettings proposed)
    {
        return new EvaluatorSettings
        {
            MaximumTelemetryAgeMs = proposed.MaximumTelemetryAgeMs != 500
                ? proposed.MaximumTelemetryAgeMs : current.MaximumTelemetryAgeMs,
            EvaluationIntervalMs = proposed.EvaluationIntervalMs != 100
                ? proposed.EvaluationIntervalMs : current.EvaluationIntervalMs,
        };
    }

    private static DashboardSettings MergeDashboard(DashboardSettings current, DashboardSettings proposed)
    {
        return new DashboardSettings
        {
            UpdateIntervalMs = proposed.UpdateIntervalMs != 2000
                ? proposed.UpdateIntervalMs : current.UpdateIntervalMs,
            MaxLogEntries = proposed.MaxLogEntries != 500
                ? proposed.MaxLogEntries : current.MaxLogEntries,
        };
    }

    private static LoggingSettings MergeLogging(LoggingSettings current, LoggingSettings proposed)
    {
        return new LoggingSettings
        {
            RetainedFileCountLimit = proposed.RetainedFileCountLimit != 14
                ? proposed.RetainedFileCountLimit : current.RetainedFileCountLimit,
        };
    }

    /// <summary>
    /// Validate settings against allowed ranges. Returns empty list if valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(BotDsSettings s)
    {
        var errors = new List<string>();

        if (s.Scanner.ReadIntervalMs is < 10 or > 5000)
            errors.Add("Scanner.ReadIntervalMs must be 10-5000");
        if (s.Scanner.LocalMaxAgeMs is < 50 or > 10000)
            errors.Add("Scanner.LocalMaxAgeMs must be 50-10000");

        if (s.Evaluator.MaximumTelemetryAgeMs is < 100 or > 5000)
            errors.Add("Evaluator.MaximumTelemetryAgeMs must be 100-5000");
        if (s.Evaluator.EvaluationIntervalMs is < 10 or > 2000)
            errors.Add("Evaluator.EvaluationIntervalMs must be 10-2000");

        if (s.Dashboard.UpdateIntervalMs is < 100 or > 10000)
            errors.Add("Dashboard.UpdateIntervalMs must be 100-10000");
        if (s.Dashboard.MaxLogEntries is < 50 or > 10000)
            errors.Add("Dashboard.MaxLogEntries must be 50-10000");

        if (s.Logging.RetainedFileCountLimit is < 1 or > 90)
            errors.Add("Logging.RetainedFileCountLimit must be 1-90");

        return errors.AsReadOnly();
    }
}
