using BotDs.Core;

namespace BotDs.App.Services;

public sealed class ProfileService
{
    private readonly string _profilesDirectory;
    private readonly ILogger<ProfileService> _log;
    private IReadOnlyDictionary<string, CombatProfile> _cache =
        new Dictionary<string, CombatProfile>(StringComparer.OrdinalIgnoreCase);
    private string? _activeProfileId;
    private readonly object _lock = new();

    public ProfileService(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<ProfileService> log)
    {
        _log = log;
        string? configured = configuration.GetValue<string>("BotDs:Profiles:Directory");
        string relativeOrAbsolute = string.IsNullOrWhiteSpace(configured) ? "profiles" : configured;
        _profilesDirectory = Path.GetFullPath(relativeOrAbsolute, environment.ContentRootPath);
    }

    public IReadOnlyList<string> AvailableProfileIds
    {
        get
        {
            lock (_lock)
                return [.. _cache.Keys.Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    public string DirectoryPath => _profilesDirectory;

    public string? ActiveProfileId
    {
        get { lock (_lock) return _activeProfileId; }
    }

    public CombatProfile? ActiveProfile
    {
        get
        {
            lock (_lock)
                return _activeProfileId is not null &&
                    _cache.TryGetValue(_activeProfileId, out CombatProfile? profile)
                    ? profile
                    : null;
        }
    }

    public CombatProfile? GetProfile(string id)
    {
        lock (_lock)
            return _cache.TryGetValue(id, out CombatProfile? profile) ? profile : null;
    }

    public bool SetActiveProfile(string id)
    {
        lock (_lock)
        {
            if (!_cache.ContainsKey(id)) return false;
            _activeProfileId = id;
            _log.LogInformation("Active profile set to {ProfileId}", id);
            return true;
        }
    }

    public bool ClearActiveProfile()
    {
        lock (_lock)
        {
            if (_activeProfileId is null) return false;
            _activeProfileId = null;
            _log.LogInformation("Active profile cleared");
            return true;
        }
    }

    public async Task<ProfileReloadResult> ReloadAsync(CancellationToken ct = default)
    {
        string searchPath = _profilesDirectory;
        if (!Directory.Exists(searchPath))
        {
            _log.LogWarning("Profiles directory not found: {Path}; clearing cache and active profile", searchPath);
            lock (_lock)
            {
                _cache = new Dictionary<string, CombatProfile>(StringComparer.OrdinalIgnoreCase);
                _activeProfileId = null;
            }
            return new ProfileReloadResult(false, [$"Profiles directory not found: {searchPath}"], []);
        }

        var loaded = new Dictionary<string, CombatProfile>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // id -> first file name
        var errors = new List<string>();
        string[] files;

        try
        {
            files = Directory.GetFiles(searchPath, "*.json");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            string error = $"Profiles directory could not be enumerated: {exception.Message}";
            _log.LogWarning(exception, "{Error}; clearing cache and active profile", error);
            lock (_lock)
            {
                _cache = new Dictionary<string, CombatProfile>(StringComparer.OrdinalIgnoreCase);
                _activeProfileId = null;
            }
            return new ProfileReloadResult(false, [error], []);
        }

        foreach (string file in files)
        {
            ProfileValidationResult result = await CombatProfileLoader.LoadAsync(file, ct);
            if (result.IsValid && result.Profile is not null)
            {
                string fileName = Path.GetFileName(file);
                if (seenIds.TryGetValue(result.Profile.Id, out string? previousFile))
                {
                    errors.Add($"Duplicate profile id '{result.Profile.Id}' in '{fileName}' and '{previousFile}'.");
                    _log.LogWarning(
                        "Duplicate profile id {ProfileId} in {File} (first seen in {PreviousFile})",
                        result.Profile.Id, fileName, previousFile);
                }
                else
                {
                    seenIds[result.Profile.Id] = fileName;
                    loaded[result.Profile.Id] = result.Profile;
                    _log.LogInformation("Loaded profile {ProfileId} from {File}",
                        result.Profile.Id, fileName);
                }
            }
            else
            {
                _log.LogWarning("Invalid profile in {File}: {Errors}",
                    Path.GetFileName(file), string.Join("; ", result.Errors));
            }
        }

        if (errors.Count > 0)
        {
            _log.LogWarning("Profile reload aborted due to duplicate ids; previous cache preserved");
            return new ProfileReloadResult(false, errors, []);
        }

        lock (_lock)
        {
            _cache = loaded;
            if (_activeProfileId is not null && !loaded.ContainsKey(_activeProfileId))
            {
                _log.LogWarning("Active profile {ProfileId} no longer available", _activeProfileId);
                _activeProfileId = null;
            }
        }

        return new ProfileReloadResult(true, [], [.. loaded.Keys]);
    }
}

public sealed record ProfileReloadResult(
    bool Success,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> LoadedProfileIds);
