using BotDs.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

public sealed class LocalSettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger<LocalSettingsService> _log;

    public LocalSettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _log = new NullLogger<LocalSettingsService>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public void Defaults_are_returned_when_no_file_exists()
    {
        var svc = new LocalSettingsService(TempFile("nonexistent.json"), _log);
        var current = svc.Current;

        Assert.Equal(50, current.Scanner.ReadIntervalMs);
        Assert.Equal(500, current.Scanner.LocalMaxAgeMs);
        Assert.Equal(500, current.Evaluator.MaximumTelemetryAgeMs);
        Assert.Equal(100, current.Evaluator.EvaluationIntervalMs);
        Assert.Equal(2000, current.Dashboard.UpdateIntervalMs);
        Assert.Equal(500, current.Dashboard.MaxLogEntries);
        Assert.Equal(14, current.Logging.RetainedFileCountLimit);
    }

    [Fact]
    public void Round_trip_preserves_values()
    {
        string path = TempFile("roundtrip.json");
        var svc = new LocalSettingsService(path, _log);

        var proposed = BotDsSettings.Default with
        {
            Scanner = new ScannerSettings
            {
                ProcessName = "rift_x64",
                ProcessId = 1234,
                ReadIntervalMs = 75,
                LocalMaxAgeMs = 300,
            },
            Evaluator = new EvaluatorSettings
            {
                MaximumTelemetryAgeMs = 250,
                EvaluationIntervalMs = 50,
            },
            Dashboard = new DashboardSettings
            {
                UpdateIntervalMs = 1000,
                MaxLogEntries = 1000,
            },
            Logging = new LoggingSettings
            {
                RetainedFileCountLimit = 30,
            },
        };

        Assert.Null(svc.TrySave(proposed));

        // Reload from disk
        var svc2 = new LocalSettingsService(path, _log);
        var loaded = svc2.Current;

        Assert.Equal(75, loaded.Scanner.ReadIntervalMs);
        Assert.Equal(300, loaded.Scanner.LocalMaxAgeMs);
        Assert.Equal(250, loaded.Evaluator.MaximumTelemetryAgeMs);
        Assert.Equal(50, loaded.Evaluator.EvaluationIntervalMs);
    }

    [Fact]
    public void Partial_PUT_preserves_unrelated_sections()
    {
        string path = TempFile("partial.json");
        var svc = new LocalSettingsService(path, _log);

        // Save initial state
        var initial = BotDsSettings.Default with
        {
            Scanner = new ScannerSettings { ReadIntervalMs = 100, LocalMaxAgeMs = 600 },
            Dashboard = new DashboardSettings { UpdateIntervalMs = 3000, MaxLogEntries = 800 },
        };
        Assert.Null(svc.TrySave(initial));

        // Now save a partial update — only change scanner interval, leave others at defaults
        var partial = BotDsSettings.Default with
        {
            Scanner = new ScannerSettings { ReadIntervalMs = 200 },
        };
        Assert.Null(svc.TrySave(partial));

        var current = svc.Current;
        // Scanner: ReadIntervalMs should update to 200 (non-default), LocalMaxAgeMs should remain 600 (previous value preserved because proposed default 500 was treated as "not set")
        Assert.Equal(200, current.Scanner.ReadIntervalMs);
        Assert.Equal(600, current.Scanner.LocalMaxAgeMs);
        // Dashboard: default values in partial are treated as "not set", so previous values preserved
        Assert.Equal(3000, current.Dashboard.UpdateIntervalMs);
        Assert.Equal(800, current.Dashboard.MaxLogEntries);
    }

    [Fact]
    public void Validation_rejects_out_of_range_values()
    {
        var svc = new LocalSettingsService(TempFile("validate.json"), _log);

        var invalid = BotDsSettings.Default with
        {
            Scanner = new ScannerSettings { ReadIntervalMs = 5, LocalMaxAgeMs = 20 },
            Evaluator = new EvaluatorSettings { MaximumTelemetryAgeMs = 50, EvaluationIntervalMs = 5000 },
        };

        var errors = svc.TrySave(invalid);
        Assert.NotNull(errors);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("ReadIntervalMs"));
        Assert.Contains(errors, e => e.Contains("LocalMaxAgeMs"));
        Assert.Contains(errors, e => e.Contains("MaximumTelemetryAgeMs"));
        Assert.Contains(errors, e => e.Contains("EvaluationIntervalMs"));
    }

    [Fact]
    public void Atomic_write_preserves_existing_file_when_save_fails()
    {
        string path = TempFile("atomic.json");
        var svc1 = new LocalSettingsService(path, _log);

        var valid = BotDsSettings.Default with
        {
            Scanner = new ScannerSettings { ReadIntervalMs = 200, LocalMaxAgeMs = 700 },
        };
        Assert.Null(svc1.TrySave(valid));
        Assert.True(File.Exists(path));

        // Attempt to save an invalid settings object — should fail and preserve the existing file
        var invalid = BotDsSettings.Default with
        {
            Scanner = new ScannerSettings { ReadIntervalMs = 5 },
        };
        var errors = svc1.TrySave(invalid);
        Assert.NotNull(errors);

        // Existing file should still contain the previous valid settings
        Assert.True(File.Exists(path));
        var svc2 = new LocalSettingsService(path, _log);
        Assert.Equal(200, svc2.Current.Scanner.ReadIntervalMs);
        Assert.Equal(700, svc2.Current.Scanner.LocalMaxAgeMs);
    }

    [Fact]
    public void Corrupted_file_falls_back_to_defaults()
    {
        string path = TempFile("corrupt.json");
        File.WriteAllText(path, "{ invalid json @@@@");

        var svc = new LocalSettingsService(path, _log);
        Assert.Equal(50, svc.Current.Scanner.ReadIntervalMs);
        Assert.Equal(500, svc.Current.Evaluator.MaximumTelemetryAgeMs);
    }

    [Fact]
    public void Static_validate_rejects_invalid_ranges()
    {
        var errors = LocalSettingsService.Validate(new BotDsSettings
        {
            Scanner = new ScannerSettings { ReadIntervalMs = 5 },
        });
        Assert.Single(errors);

        errors = LocalSettingsService.Validate(new BotDsSettings
        {
            Dashboard = new DashboardSettings { MaxLogEntries = 10 },
        });
        Assert.Single(errors);

        errors = LocalSettingsService.Validate(BotDsSettings.Default);
        Assert.Empty(errors);
    }

    // Minimal null-logger for test use
    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
