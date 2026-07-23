using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Views;

internal static partial class AddAccountOutputFormatter
{
    internal static string Format(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = AnsiCsiRegex().Replace(text, string.Empty);

        return clean switch
        {
            "OpenAI's command-line coding agent" => "OpenAI 命令行编程助手",
            "Follow these steps to sign in with ChatGPT using device code authorization:" => "请按以下步骤使用设备验证码登录 ChatGPT：",
            "1. Open this link in your browser and sign in to your account" => "1. 在浏览器中打开以下链接并登录账号",
            "Continue only if you started this login in Codex. If a website or another person gave you this code, cancel." => "安全提醒：只有在你本人从 Codex 发起本次登录时才继续。如果验证码来自网站或其他人，请取消。",
            _ when WelcomeLineRegex().Match(clean) is { Success: true } welcome => $"欢迎使用 Codex {welcome.Groups["version"].Value}",
            _ when ExpiryLineRegex().Match(clean) is { Success: true } expiry => $"2. 输入以下一次性验证码（{expiry.Groups["minutes"].Value} 分钟内有效）",
            _ => clean,
        };
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiCsiRegex();

    [GeneratedRegex(@"^Welcome to Codex (?<version>\[v[^\]]+\])$")]
    private static partial Regex WelcomeLineRegex();

    [GeneratedRegex(@"^2\. Enter this one-time code \(expires in (?<minutes>\d+) minutes\)$")]
    private static partial Regex ExpiryLineRegex();
}
