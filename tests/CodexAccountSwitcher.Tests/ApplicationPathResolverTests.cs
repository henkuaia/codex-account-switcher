namespace CodexAccountSwitcher.Tests;

public sealed class ApplicationPathResolverTests
{
    [Fact]
    public void Published_tools_path_takes_precedence_over_debug_vendor_fallback()
    {
        using var directory = new TemporaryDirectory();
        var baseDirectory = Path.Combine(directory.Path, "publish");
        var bundled = Path.Combine(baseDirectory, "tools", "codex-auth.exe");
        var vendor = Path.Combine(directory.Path, "vendor", "codex-auth", "codex-auth.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        Directory.CreateDirectory(Path.GetDirectoryName(vendor)!);
        File.WriteAllText(bundled, string.Empty);
        File.WriteAllText(vendor, string.Empty);

        var resolved = ApplicationPathResolver.ResolveHelperPath(baseDirectory);

        Assert.Equal(bundled, resolved);
    }

#if DEBUG
    [Fact]
    public void Debug_vendor_fallback_walks_ancestors_from_base_directory()
    {
        using var directory = new TemporaryDirectory();
        var baseDirectory = Path.Combine(
            directory.Path,
            "src",
            "CodexAccountSwitcher",
            "bin",
            "x64",
            "Debug",
            "net9.0-windows");
        var vendor = Path.Combine(directory.Path, "vendor", "codex-auth", "codex-auth.exe");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(vendor)!);
        File.WriteAllText(vendor, string.Empty);

        var resolved = ApplicationPathResolver.ResolveHelperPath(baseDirectory);

        Assert.Equal(vendor, resolved);
    }
#endif
}
