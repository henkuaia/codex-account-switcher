namespace CodexAccountSwitcher.Tests;

public sealed class WpfInterfaceContractTests
{
    [Theory]
    [InlineData("5H")]
    [InlineData("five-hour")]
    [InlineData("Settings")]
    [InlineData("tray behavior")]
    [InlineData("LinearGradientBrush")]
    [InlineData("RadialGradientBrush")]
    public void Production_xaml_excludes_forbidden_content(string forbidden)
    {
        var xaml = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    FindDirectory("src", "CodexAccountSwitcher"),
                    "*.xaml",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.DoesNotContain(forbidden, xaml, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindDirectory(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}
