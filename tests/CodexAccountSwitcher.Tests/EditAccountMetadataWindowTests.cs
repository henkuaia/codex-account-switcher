using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher.Tests;

public sealed class EditAccountMetadataWindowTests
{
    [Theory]
    [InlineData("", "0", null, 0)]
    [InlineData("$40", "3", "40", 3)]
    [InlineData("40.25", "12", "40.25", 12)]
    public void Valid_values_parse_to_metadata(
        string quotaText,
        string resetText,
        string? expectedQuotaText,
        int expectedResets)
    {
        var succeeded = EditAccountMetadataWindow.TryParseMetadata(
            quotaText,
            resetText,
            out var metadata,
            out var error);

        Assert.True(succeeded);
        var expectedQuota = expectedQuotaText is null
            ? (decimal?)null
            : decimal.Parse(expectedQuotaText, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new AccountMetadata(expectedQuota, expectedResets), metadata);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("-1", "0")]
    [InlineData("1.234", "0")]
    [InlineData("invalid", "0")]
    [InlineData("40", "-1")]
    [InlineData("40", "1.5")]
    public void Invalid_values_return_inline_validation_error(string quotaText, string resetText)
    {
        var succeeded = EditAccountMetadataWindow.TryParseMetadata(
            quotaText,
            resetText,
            out _,
            out var error);

        Assert.False(succeeded);
        Assert.NotEmpty(error);
    }
}
