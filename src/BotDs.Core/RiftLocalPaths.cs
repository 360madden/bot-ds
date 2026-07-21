namespace BotDs.Core;

/// <summary>
/// Durable resolution of RIFT user-data paths on this machine.
/// Always uses shell MyDocuments (OneDrive-backed here). Never invents
/// %USERPROFILE%\Documents or Glyph Live\Interface\Addons as the addon load path.
/// See docs/rift-local-paths.md.
/// </summary>
public static class RiftLocalPaths
{
    /// <summary>Relative path under MyDocuments for player addons.</summary>
    public const string AddOnsRelativePath = @"RIFT\Interface\AddOns";

    /// <summary>BotDsBridge folder name under AddOns.</summary>
    public const string BotDsBridgeFolderName = "BotDsBridge";

    /// <summary>
    /// Shell known-folder Documents. On this workstation equals
    /// C:\Users\mrkoo\OneDrive\Documents (not a non-redirected Documents tree).
    /// </summary>
    public static string MyDocuments
    {
        get
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "Environment.SpecialFolder.MyDocuments is empty; cannot resolve RIFT paths.");
            return path;
        }
    }

    /// <summary>
    /// Player addon load directory: {MyDocuments}\RIFT\Interface\AddOns
    /// </summary>
    public static string UserAddOnsDirectory =>
        Path.GetFullPath(Path.Combine(MyDocuments, AddOnsRelativePath));

    /// <summary>
    /// Deploy target for BotDsBridge: {UserAddOnsDirectory}\BotDsBridge
    /// </summary>
    public static string BotDsBridgeDirectory =>
        Path.GetFullPath(Path.Combine(UserAddOnsDirectory, BotDsBridgeFolderName));

    /// <summary>
    /// SavedVariables root under the same MyDocuments tree as addons.
    /// </summary>
    public static string SavedVariablesRoot =>
        Path.GetFullPath(Path.Combine(MyDocuments, "RIFT", "SavedVariables"));

    /// <summary>
    /// True when the AddOns directory looks like a real client load path
    /// (contains sibling addons other than BotDsBridge).
    /// </summary>
    public static bool LooksLikeRealAddOnsTree(string? addOnsDirectory = null)
    {
        string dir = addOnsDirectory ?? UserAddOnsDirectory;
        if (!Directory.Exists(dir))
            return false;

        foreach (string path in Directory.EnumerateDirectories(dir))
        {
            string name = Path.GetFileName(path);
            if (!string.Equals(name, BotDsBridgeFolderName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Paths that must not be treated as the primary player-addon load location
    /// unless they resolve to the same full path as <see cref="UserAddOnsDirectory"/>.
    /// </summary>
    public static bool IsDiscouragedDeployPath(string candidateFullPath)
    {
        string full = Path.GetFullPath(candidateFullPath);
        string canonical = UserAddOnsDirectory;

        if (string.Equals(full, canonical, StringComparison.OrdinalIgnoreCase))
            return false;

        // Non-shell Documents tree under profile
        string profileDocs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "RIFT", "Interface", "AddOns");
        if (string.Equals(Path.GetFullPath(profileDocs), full, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFullPath(profileDocs), canonical, StringComparison.OrdinalIgnoreCase))
            return true;

        // Glyph install tree
        if (full.Contains(@"Glyph\Games\RIFT", StringComparison.OrdinalIgnoreCase)
            && full.Contains(@"Interface\Addons", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
