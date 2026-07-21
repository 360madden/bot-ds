using BotDs.Core;

namespace BotDs.Tests;

public sealed class RiftLocalPathsTests
{
    [Fact]
    public void MyDocuments_matches_shell_special_folder()
    {
        string expected = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, RiftLocalPaths.MyDocuments);
    }

    [Fact]
    public void UserAddOnsDirectory_is_under_MyDocuments_RIFT_Interface_AddOns()
    {
        string addOns = RiftLocalPaths.UserAddOnsDirectory;
        Assert.StartsWith(RiftLocalPaths.MyDocuments, addOns, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"RIFT\Interface\AddOns", addOns, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(RiftLocalPaths.MyDocuments, "RIFT", "Interface", "AddOns")),
            addOns);
    }

    [Fact]
    public void BotDsBridgeDirectory_is_child_of_UserAddOns()
    {
        Assert.Equal(
            Path.Combine(RiftLocalPaths.UserAddOnsDirectory, "BotDsBridge"),
            RiftLocalPaths.BotDsBridgeDirectory);
    }

    [Fact]
    public void Glyph_install_addons_tree_is_discouraged()
    {
        string glyph = @"C:\Program Files (x86)\Glyph\Games\RIFT\Live\Interface\Addons";
        Assert.True(RiftLocalPaths.IsDiscouragedDeployPath(glyph));
    }

    [Fact]
    public void Canonical_UserAddOns_is_not_discouraged()
    {
        Assert.False(RiftLocalPaths.IsDiscouragedDeployPath(RiftLocalPaths.UserAddOnsDirectory));
    }

    [Fact]
    public void Non_shell_profile_Documents_is_discouraged_when_different_from_MyDocuments()
    {
        string profileDocs = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "RIFT", "Interface", "AddOns"));
        string shell = RiftLocalPaths.UserAddOnsDirectory;
        if (string.Equals(profileDocs, shell, StringComparison.OrdinalIgnoreCase))
        {
            // Paths coincide (no OneDrive split) — not discouraged
            Assert.False(RiftLocalPaths.IsDiscouragedDeployPath(profileDocs));
            return;
        }

        Assert.True(RiftLocalPaths.IsDiscouragedDeployPath(profileDocs));
    }

    [Fact]
    public void LooksLikeRealAddOnsTree_true_when_siblings_present_on_this_machine()
    {
        // On the developer workstation the OneDrive AddOns tree has many siblings.
        // If the tree is missing (CI), the method simply returns false — not a failure.
        if (!Directory.Exists(RiftLocalPaths.UserAddOnsDirectory))
            return;

        bool looksReal = RiftLocalPaths.LooksLikeRealAddOnsTree();
        // If MyDocuments is OneDrive RIFT tree, expect true
        if (RiftLocalPaths.MyDocuments.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            Assert.True(looksReal);
    }
}
