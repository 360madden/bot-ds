using BotDs.Core;

// Thin CLI: resolve RIFT paths and deploy BotDsBridge to shell MyDocuments AddOns.
// Usage:
//   BotDs.Tools paths
//   BotDs.Tools deploy-addon [--force]

string command = args.Length > 0 ? args[0] : "paths";
bool force = args.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));

try
{
    return command.ToLowerInvariant() switch
    {
        "paths" or "print-paths" => PrintPaths(),
        "deploy-addon" or "deploy" => DeployAddon(force),
        "help" or "-h" or "--help" => PrintHelp(),
        _ => Fail($"Unknown command '{command}'. Use: paths | deploy-addon"),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 1;
}

static int PrintHelp()
{
    Console.WriteLine("""
        BotDs.Tools — RIFT local path utilities

        Commands:
          paths              Print shell MyDocuments and addon deploy paths
          deploy-addon       Copy addons/BotDsBridge/BotDsBridge into user AddOns
          deploy-addon --force
                             Deploy even if AddOns tree looks empty (not recommended)

        Durable fact: player addons load from
          {MyDocuments}\RIFT\Interface\AddOns\
        where MyDocuments = Environment.SpecialFolder.MyDocuments
        (OneDrive-backed on this machine). See docs/rift-local-paths.md.
        """);
    return 0;
}

static int PrintPaths()
{
    Console.WriteLine("MyDocuments       = " + RiftLocalPaths.MyDocuments);
    Console.WriteLine("UserAddOns        = " + RiftLocalPaths.UserAddOnsDirectory);
    Console.WriteLine("BotDsBridgeTarget = " + RiftLocalPaths.BotDsBridgeDirectory);
    Console.WriteLine("SavedVariables    = " + RiftLocalPaths.SavedVariablesRoot);
    Console.WriteLine("AddOnsExists      = " + Directory.Exists(RiftLocalPaths.UserAddOnsDirectory));
    Console.WriteLine("LooksLikeRealTree = " + RiftLocalPaths.LooksLikeRealAddOnsTree());
    Console.WriteLine("DiscouragedIfUsed = " +
        RiftLocalPaths.IsDiscouragedDeployPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "RIFT", "Interface", "AddOns")));
    return 0;
}

static int DeployAddon(bool force)
{
    string repoRoot = FindRepoRoot();
    string source = Path.Combine(repoRoot, "addons", "BotDsBridge", "BotDsBridge");
    if (!Directory.Exists(source))
        return Fail("Source addon folder not found: " + source);

    string toc = Path.Combine(source, "RiftAddon.toc");
    string main = Path.Combine(source, "main.lua");
    if (!File.Exists(toc) || !File.Exists(main))
        return Fail("Source must contain RiftAddon.toc and main.lua");

    string? tocError = ValidateRiftAddonToc(toc);
    if (tocError is not null)
        return Fail(tocError);

    string addOns = RiftLocalPaths.UserAddOnsDirectory;
    string dest = RiftLocalPaths.BotDsBridgeDirectory;

    if (RiftLocalPaths.IsDiscouragedDeployPath(addOns))
        return Fail("Resolved AddOns path is marked discouraged: " + addOns);

    if (!force && !RiftLocalPaths.LooksLikeRealAddOnsTree(addOns))
    {
        return Fail(
            "AddOns tree does not look like the real client load path (no sibling addons). " +
            "Expected siblings such as JAB/ReaderBridge under: " + addOns +
            ". Refusing deploy. Pass --force only if you are certain.");
    }

    Directory.CreateDirectory(dest);
    foreach (string file in Directory.GetFiles(source))
    {
        string name = Path.GetFileName(file);
        string target = Path.Combine(dest, name);
        File.Copy(file, target, overwrite: true);
    }

    // Verify hashes
    foreach (string name in new[] { "RiftAddon.toc", "main.lua" })
    {
        string s = Path.Combine(source, name);
        string d = Path.Combine(dest, name);
        if (!File.Exists(d))
            return Fail("Deploy missing file: " + d);
        byte[] a = File.ReadAllBytes(s);
        byte[] b = File.ReadAllBytes(d);
        if (a.Length != b.Length || !a.AsSpan().SequenceEqual(b))
            return Fail("Hash mismatch after copy: " + name);
    }

    Console.WriteLine("Deployed BotDsBridge -> " + dest);
    Console.WriteLine("MyDocuments         = " + RiftLocalPaths.MyDocuments);
    Console.WriteLine("Sibling check       = " + RiftLocalPaths.LooksLikeRealAddOnsTree());
    Console.WriteLine("In RIFT run: /reloadui");
    return 0;
}

static string FindRepoRoot()
{
    string? dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && dir is not null; i++)
    {
        if (File.Exists(Path.Combine(dir, "BotDs.sln")))
            return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }

    dir = Directory.GetCurrentDirectory();
    for (int i = 0; i < 8 && dir is not null; i++)
    {
        if (File.Exists(Path.Combine(dir, "BotDs.sln")))
            return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }

    throw new InvalidOperationException("BotDs.sln not found from " + AppContext.BaseDirectory);
}

static int Fail(string message)
{
    Console.Error.WriteLine("ERROR: " + message);
    return 1;
}

/// <summary>
/// RIFT only lists addons whose TOC has non-empty Identifier, Name, and Email.
/// Empty Email = "" is treated as missing and the addon never appears in the list.
/// </summary>
static string? ValidateRiftAddonToc(string tocPath)
{
    string text = File.ReadAllText(tocPath);
    foreach (string field in new[] { "Identifier", "Name", "Email" })
    {
        // Match: Field = "value"  (value must be non-whitespace)
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            $@"^\s*{field}\s*=\s*""([^""]*)""",
            System.Text.RegularExpressions.RegexOptions.Multiline
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return $"RiftAddon.toc missing required field {field} (addon will not appear in RIFT AddOns list).";
        if (string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return $"RiftAddon.toc field {field} is empty (addon will not appear in RIFT AddOns list).";
    }

    return null;
}
