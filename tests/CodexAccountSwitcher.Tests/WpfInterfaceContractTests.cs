using System.Xml.Linq;

namespace CodexAccountSwitcher.Tests;

public sealed class WpfInterfaceContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void App_resources_define_approved_palette_and_corner_radii()
    {
        var document = LoadXaml("src", "CodexAccountSwitcher", "App.xaml");
        var expectedColors = new Dictionary<string, string>
        {
            ["WindowBackgroundColor"] = "#F4F6F7",
            ["SurfaceColor"] = "#FBFCFC",
            ["BorderColor"] = "#D9DEE2",
            ["PrimaryColor"] = "#2D6678",
            ["ActiveBackgroundColor"] = "#EDF5F1",
            ["ActiveBorderColor"] = "#C9DED3",
            ["ActiveTextColor"] = "#315C48",
            ["WarningColor"] = "#CF9D39",
        };

        foreach (var (key, value) in expectedColors)
        {
            var resource = Assert.Single(document.Descendants(Presentation + "Color"),
                element => string.Equals((string?)element.Attribute(Xaml + "Key"), key, StringComparison.Ordinal));
            Assert.Equal(value, resource.Value.Trim());
        }

        var cornerRadii = document.Descendants(Presentation + "Setter")
            .Where(element => string.Equals(
                (string?)element.Attribute("Property"),
                "CornerRadius",
                StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Value"))
            .ToArray();
        Assert.Contains("6", cornerRadii);
        Assert.Contains("7", cornerRadii);
    }

    [Fact]
    public void Main_window_preserves_compact_list_and_action_contract()
    {
        var document = LoadXaml("src", "CodexAccountSwitcher", "MainWindow.xaml");
        var window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("440", (string?)window.Attribute("Width"));
        Assert.Equal("440", (string?)window.Attribute("MinWidth"));
        Assert.Equal("440", (string?)window.Attribute("MaxWidth"));
        Assert.Equal("480", (string?)window.Attribute("MinHeight"));
        Assert.Equal("720", (string?)window.Attribute("MaxHeight"));
        Assert.Contains(document.Descendants(Presentation + "ScrollViewer"), scroll =>
            scroll.Descendants(Presentation + "ItemsControl").Any());

        var text = File.ReadAllText(FindFile("src", "CodexAccountSwitcher", "MainWindow.xaml"));
        Assert.Contains("Segoe Fluent Icons", text, StringComparison.Ordinal);
        Assert.Contains("Refresh quota", text, StringComparison.Ordinal);
        Assert.Contains("Add account", text, StringComparison.Ordinal);
        Assert.Contains("Remove account", text, StringComparison.Ordinal);
        Assert.Contains("Close", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5H", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Settings", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tray behavior", text, StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadXaml(params string[] relativePath) =>
        XDocument.Load(FindFile(relativePath));

    private static string FindFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}
