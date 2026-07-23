using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher.Tests;

public sealed class AddAccountOutputFormatterTests
{
    [Theory]
    [InlineData("Welcome to Codex [v0.145.0-alpha.30]", "欢迎使用 Codex [v0.145.0-alpha.30]")]
    [InlineData("OpenAI's command-line coding agent", "OpenAI 命令行编程助手")]
    [InlineData("Follow these steps to sign in with ChatGPT using device code authorization:", "请按以下步骤使用设备验证码登录 ChatGPT：")]
    [InlineData("1. Open this link in your browser and sign in to your account", "1. 在浏览器中打开以下链接并登录账号")]
    [InlineData("2. Enter this one-time code (expires in 15 minutes)", "2. 输入以下一次性验证码（15 分钟内有效）")]
    [InlineData("Continue only if you started this login in Codex. If a website or another person gave you this code, cancel.", "安全提醒：只有在你本人从 Codex 发起本次登录时才继续。如果验证码来自网站或其他人，请取消。")]
    public void Formats_known_device_login_lines(string input, string expected)
    {
        Assert.Equal(expected, AddAccountOutputFormatter.Format(input));
    }

    [Theory]
    [InlineData("Welcome to Codex [v0.200.1]", "欢迎使用 Codex [v0.200.1]")]
    [InlineData("Welcome to Codex [v1.0.0-beta.2+build.5]", "欢迎使用 Codex [v1.0.0-beta.2+build.5]")]
    public void Preserves_the_version_in_welcome_lines(string input, string expected)
    {
        Assert.Equal(expected, AddAccountOutputFormatter.Format(input));
    }

    [Theory]
    [InlineData("2. Enter this one-time code (expires in 7 minutes)", "2. 输入以下一次性验证码（7 分钟内有效）")]
    [InlineData("2. Enter this one-time code (expires in 120 minutes)", "2. 输入以下一次性验证码（120 分钟内有效）")]
    public void Preserves_the_expiry_minutes(string input, string expected)
    {
        Assert.Equal(expected, AddAccountOutputFormatter.Format(input));
    }

    [Fact]
    public void Strips_screenshot_shaped_ansi_sequences_before_translating()
    {
        var formatted = AddAccountOutputFormatter.Format(
            "\u001b[90mWelcome to Codex [v0.145.0-alpha.30]\u001b[0m");

        Assert.Equal("欢迎使用 Codex [v0.145.0-alpha.30]", formatted);
        Assert.DoesNotContain('\u001b', formatted);
        Assert.DoesNotContain("90m", formatted);
        Assert.DoesNotContain("0m", formatted);
    }

    [Fact]
    public void Strips_multiple_csi_sequences_without_rewriting_unknown_text()
    {
        var formatted = AddAccountOutputFormatter.Format(
            "  \u001b[90mfuture \u001b[1;94mCLI\u001b[0m text\u001b[2K");

        Assert.Equal("  future CLI text", formatted);
        Assert.DoesNotContain('\u001b', formatted);
        Assert.DoesNotContain("90m", formatted);
        Assert.DoesNotContain("1;94m", formatted);
        Assert.DoesNotContain("0m", formatted);
        Assert.DoesNotContain("2K", formatted);
    }

    [Theory]
    [InlineData("https://auth.openai.com/codex/device?user_code=ABCD-EFGH")]
    [InlineData("ABCD-EFGH")]
    [InlineData("unknown future CLI text")]
    [InlineData("")]
    [InlineData("  indented unknown text")]
    public void Preserves_urls_codes_unknown_text_blank_lines_and_indentation(string input)
    {
        Assert.Equal(input, AddAccountOutputFormatter.Format(input));
    }

    [Fact]
    public void Rejects_null_text()
    {
        Assert.Throws<ArgumentNullException>(() => AddAccountOutputFormatter.Format(null!));
    }
}
