using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexAccountSwitcher.Converters;

namespace CodexAccountSwitcher.Tests;

public sealed class QuotaRemainingBrushConverterTests
{
    [Theory]
    [InlineData(0, "#FFD9534F")]
    [InlineData(25, "#FFE07B46")]
    [InlineData(50, "#FFE6A23C")]
    [InlineData(75, "#FF7EAB50")]
    [InlineData(100, "#FF16B364")]
    public void Converts_anchor_and_interpolated_colors(double value, string expected)
    {
        var brush = Convert(value);

        Assert.Equal(expected, brush.Color.ToString());
        Assert.True(brush.IsFrozen);
    }

    [Theory]
    [InlineData(-10, "#FFD9534F")]
    [InlineData(110, "#FF16B364")]
    public void Clamps_values_to_color_endpoints(double value, string expected)
    {
        Assert.Equal(expected, Convert(value).Color.ToString());
    }

    [Fact]
    public void Invalid_or_missing_values_use_neutral_color()
    {
        var converter = new QuotaRemainingBrushConverter();
        var inputs = new object?[]
        {
            null,
            DependencyProperty.UnsetValue,
            "not-a-number",
            double.NaN,
            double.PositiveInfinity,
        };

        foreach (var input in inputs)
        {
            var brush = Assert.IsType<SolidColorBrush>(
                converter.Convert(input!, typeof(Brush), string.Empty, CultureInfo.InvariantCulture));
            Assert.Equal("#FF667178", brush.Color.ToString());
        }
    }

    [Fact]
    public void Convert_back_is_not_supported()
    {
        var converter = new QuotaRemainingBrushConverter();

        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(
                Brushes.Red,
                typeof(double),
                string.Empty,
                CultureInfo.InvariantCulture));
    }

    private static SolidColorBrush Convert(double value) => Assert.IsType<SolidColorBrush>(
        new QuotaRemainingBrushConverter().Convert(
            value,
            typeof(Brush),
            string.Empty,
            CultureInfo.InvariantCulture));
}
