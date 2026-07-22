using System.Diagnostics;

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
        Assert.Contains("[IO.Path]::GetRelativePath", script, StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet restore $solutionPath\n    Assert-DotnetSucceeded -Operation 'dotnet restore'",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet build $solutionPath -c Release --no-restore\n    Assert-DotnetSucceeded -Operation 'dotnet build'",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet test $solutionPath -c Release --no-restore\n    Assert-DotnetSucceeded -Operation 'dotnet test'",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $dotnet publish $projectPath -c Release -r win-x64 --self-contained false --no-restore -o $stagingDirectory\n    Assert-DotnetSucceeded -Operation 'dotnet publish'",
            normalizedScript,
            StringComparison.Ordinal);
        AssertInOrder(
            script,
            "$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))",
            "$dotnet = Join-Path $projectRoot '.tools\\dotnet\\dotnet.exe'",
            "$actualHelperSha256 = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash",
            "if (-not [string]::Equals($actualHelperSha256, $expectedHelperSha256, [StringComparison]::OrdinalIgnoreCase))",
            "New-Item -ItemType Directory -Force -Path $distDirectory, $stagingDirectory",
            "& $dotnet restore $solutionPath",
            "Assert-DotnetSucceeded -Operation 'dotnet restore'",
            "& $dotnet build $solutionPath -c Release --no-restore",
            "Assert-DotnetSucceeded -Operation 'dotnet build'",
            "& $dotnet test $solutionPath -c Release --no-restore",
            "Assert-DotnetSucceeded -Operation 'dotnet test'",
            "& $dotnet publish $projectPath -c Release -r win-x64 --self-contained false --no-restore -o $stagingDirectory",
            "Assert-DotnetSucceeded -Operation 'dotnet publish'",
            "Copy-Item -LiteralPath $helperPath -Destination (Join-Path $stagingToolsDirectory 'codex-auth.exe') -Force",
            "Copy-Item -LiteralPath $helperManifestPath -Destination (Join-Path $stagingToolsDirectory 'manifest.json') -Force",
            "Move-Item -LiteralPath $stagingDirectory -Destination $finalDirectory");
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

    [Fact]
    public void Publish_replaces_the_final_distribution_without_stale_files()
    {
        using var fixture = PublishFixture.Create(validHelper: true);
        fixture.CreatePreviousFinalDistribution();

        var result = fixture.RunPublish();

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(Path.Combine(fixture.FinalDirectory, "stale-marker.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.FinalDirectory, "CodexAccountSwitcher.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.FinalDirectory, "tools", "codex-auth.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.FinalDirectory, "tools", "manifest.json")));
    }

    [Fact]
    public void Publish_hash_mismatch_leaves_the_previous_final_distribution_untouched()
    {
        using var fixture = PublishFixture.Create(validHelper: false);
        fixture.CreatePreviousFinalDistribution();

        var result = fixture.RunPublish();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SHA-256 mismatch", result.Output, StringComparison.Ordinal);
        Assert.Equal("stale", File.ReadAllText(Path.Combine(fixture.FinalDirectory, "stale-marker.txt")));
        Assert.Equal(
            "previous-helper",
            File.ReadAllText(Path.Combine(fixture.FinalDirectory, "tools", "codex-auth.exe")));
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

    private sealed class PublishFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _dotnetLink;

        private PublishFixture(string root)
        {
            _root = root;
            _dotnetLink = Path.Combine(_root, ".tools", "dotnet");
            FinalDirectory = Path.Combine(_root, "dist", "CodexAccountSwitcher");
        }

        public string FinalDirectory { get; }

        public static PublishFixture Create(bool validHelper)
        {
            var repositoryRoot = FindRepositoryRoot();
            var root = Path.Combine(
                Path.GetTempPath(),
                $"CodexAccountSwitcher-publish-{Guid.NewGuid():N}");
            var fixture = new PublishFixture(root);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "CodexAccountSwitcher"));
            Directory.CreateDirectory(Path.Combine(root, "vendor", "codex-auth"));
            Directory.CreateDirectory(Path.Combine(root, ".tools"));

            File.Copy(
                Path.Combine(repositoryRoot, "scripts", "publish.ps1"),
                Path.Combine(root, "scripts", "publish.ps1"));
            CreateJunction(
                fixture._dotnetLink,
                Path.Combine(repositoryRoot, ".tools", "dotnet"),
                root);
            File.WriteAllText(
                Path.Combine(root, "CodexAccountSwitcher.sln"),
                """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CodexAccountSwitcher", "src\CodexAccountSwitcher\CodexAccountSwitcher.csproj", "{C6A2BFBE-8AFE-4D38-BFBC-15D1DD7084F8}"
                EndProject
                Global
                    GlobalSection(SolutionConfigurationPlatforms) = preSolution
                        Debug|Any CPU = Debug|Any CPU
                        Release|Any CPU = Release|Any CPU
                    EndGlobalSection
                    GlobalSection(ProjectConfigurationPlatforms) = postSolution
                        {C6A2BFBE-8AFE-4D38-BFBC-15D1DD7084F8}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                        {C6A2BFBE-8AFE-4D38-BFBC-15D1DD7084F8}.Debug|Any CPU.Build.0 = Debug|Any CPU
                        {C6A2BFBE-8AFE-4D38-BFBC-15D1DD7084F8}.Release|Any CPU.ActiveCfg = Release|Any CPU
                        {C6A2BFBE-8AFE-4D38-BFBC-15D1DD7084F8}.Release|Any CPU.Build.0 = Release|Any CPU
                    EndGlobalSection
                EndGlobal
                """);
            File.WriteAllText(
                Path.Combine(root, "src", "CodexAccountSwitcher", "CodexAccountSwitcher.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                    <UseSharedCompilation>false</UseSharedCompilation>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(root, "src", "CodexAccountSwitcher", "Program.cs"),
                "System.Console.WriteLine(\"fixture\");");
            File.Copy(
                Path.Combine(repositoryRoot, "vendor", "codex-auth", "manifest.json"),
                Path.Combine(root, "vendor", "codex-auth", "manifest.json"));

            var fixtureHelper = Path.Combine(root, "vendor", "codex-auth", "codex-auth.exe");
            if (validHelper)
            {
                File.Copy(
                    Path.Combine(repositoryRoot, "vendor", "codex-auth", "codex-auth.exe"),
                    fixtureHelper);
            }
            else
            {
                File.WriteAllText(fixtureHelper, "hash-mismatch");
            }

            return fixture;
        }

        public void CreatePreviousFinalDistribution()
        {
            Directory.CreateDirectory(Path.Combine(FinalDirectory, "tools"));
            File.WriteAllText(Path.Combine(FinalDirectory, "stale-marker.txt"), "stale");
            File.WriteAllText(Path.Combine(FinalDirectory, "tools", "codex-auth.exe"), "previous-helper");
        }

        public ProcessResult RunPublish()
        {
            var callerDirectory = Path.Combine(_root, "caller");
            Directory.CreateDirectory(callerDirectory);
            return RunProcess(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    Path.Combine(_root, "scripts", "publish.ps1"),
                ],
                callerDirectory);
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            var relativeLink = Path.GetRelativePath(_root, _dotnetLink);
            if (Path.IsPathRooted(relativeLink) || relativeLink == ".." || relativeLink.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Fixture junction is not a child of the fixture root.");
            }

            if (Directory.Exists(_dotnetLink))
            {
                if (new DirectoryInfo(_dotnetLink).LinkTarget is null)
                {
                    throw new InvalidOperationException("Fixture dotnet directory is not a junction.");
                }

                Directory.Delete(_dotnetLink);
            }

            Directory.Delete(_root, recursive: true);
        }

        private static void CreateJunction(string linkPath, string targetPath, string workingDirectory)
        {
            var result = RunProcess(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"New-Item -ItemType Junction -Path {QuotePowerShell(linkPath)} -Target {QuotePowerShell(targetPath)} | Out-Null",
                ],
                workingDirectory);
            if (result.ExitCode != 0 || !Directory.Exists(linkPath))
            {
                throw new InvalidOperationException($"Could not create fixture junction: {result.Output}");
            }
        }

        private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {fileName}.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{fileName} did not exit within two minutes.");
            }

            Task.WaitAll(outputTask, errorTask);
            return new ProcessResult(process.ExitCode, outputTask.Result + errorTask.Result);
        }

        private static string QuotePowerShell(string path) =>
            $"'{path.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
