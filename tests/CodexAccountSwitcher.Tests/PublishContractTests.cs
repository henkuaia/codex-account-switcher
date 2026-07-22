namespace CodexAccountSwitcher.Tests;

public sealed class PublishContractTests
{
    [Fact]
    public void Publish_script_uses_the_pinned_sdk_and_stages_the_verified_helper()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "publish.ps1"));
        var normalizedScript = script.ReplaceLineEndings("\n");

        Assert.DoesNotContain("Get-Location", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Location", script, StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet restore $solutionPath\nAssert-DotnetSucceeded -Operation 'dotnet restore'",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet build $solutionPath -c Release\nAssert-DotnetSucceeded -Operation 'dotnet build'",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet test $solutionPath -c Release\nAssert-DotnetSucceeded -Operation 'dotnet test'",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $publishDirectory\nAssert-DotnetSucceeded -Operation 'dotnet publish'",
            normalizedScript,
            StringComparison.Ordinal);
        AssertInOrder(
            script,
            "$projectRoot = Split-Path -Parent $PSScriptRoot",
            "$dotnet = Join-Path $projectRoot '.tools\\dotnet\\dotnet.exe'",
            "& $dotnet restore $solutionPath",
            "Assert-DotnetSucceeded -Operation 'dotnet restore'",
            "& $dotnet build $solutionPath -c Release",
            "Assert-DotnetSucceeded -Operation 'dotnet build'",
            "& $dotnet test $solutionPath -c Release",
            "Assert-DotnetSucceeded -Operation 'dotnet test'",
            "& $dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $publishDirectory",
            "Assert-DotnetSucceeded -Operation 'dotnet publish'",
            "$actualHelperSha256 = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash",
            "if (-not [string]::Equals($actualHelperSha256, $expectedHelperSha256, [StringComparison]::OrdinalIgnoreCase))",
            "Copy-Item -LiteralPath $helperPath -Destination (Join-Path $toolsDirectory 'codex-auth.exe') -Force",
            "Copy-Item -LiteralPath $helperManifestPath -Destination (Join-Path $toolsDirectory 'manifest.json') -Force");
        Assert.Contains(
            "7E8E79976FE6A106B200860738C81636D18C9EAAB0196F342C53FDAAA5791F11",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_documents_manual_safe_account_management()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "README.md"));

        Assert.Contains(".NET 9 Desktop Runtime", readme, StringComparison.Ordinal);
        Assert.Contains("device login", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("weekly", readme, StringComparison.Ordinal);
        Assert.Contains("monthly", readme, StringComparison.Ordinal);
        Assert.Contains("不展示五小时额度", readme, StringComparison.Ordinal);
        Assert.Contains("unofficial endpoint", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("停止所有正在进行的 Codex 工作", readme, StringComparison.Ordinal);
        Assert.Contains("安全关闭 Codex", readme, StringComparison.Ordinal);
        Assert.Contains("CCSwitch", readme, StringComparison.Ordinal);
        Assert.Contains("OpenAI Official", readme, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%\\.codex\\accounts", readme, StringComparison.Ordinal);
        Assert.Contains("未加密存储", readme, StringComparison.Ordinal);
        Assert.Contains("pre-test `.codex` backup", readme, StringComparison.Ordinal);
        Assert.Contains("tray", readme, StringComparison.Ordinal);
        Assert.Contains("automatic", readme, StringComparison.Ordinal);
        Assert.Contains("hot switch", readme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexAccountSwitcher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void AssertInOrder(string content, params string[] snippets)
    {
        var searchStart = 0;
        foreach (var snippet in snippets)
        {
            var index = content.IndexOf(snippet, searchStart, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Could not find '{snippet}' after index {searchStart}.");
            searchStart = index + snippet.Length;
        }
    }
}
