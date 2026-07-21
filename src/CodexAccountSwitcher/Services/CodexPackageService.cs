using System.IO;
using System.Text.Json;

namespace CodexAccountSwitcher.Services;

public sealed record CodexPackageInfo(
    string PackageFamilyName,
    string AppUserModelId,
    string InstallLocation,
    string MainExecutablePath,
    string CliDirectory);

public sealed class CodexPackageService
{
    private const string InvalidOutputMessage = "The Codex package discovery output is invalid.";
    private const string DiscoveryScript = """
        $ErrorActionPreference = 'Stop'
        $package = Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1
        if ($null -eq $package) {
            Write-Output 'null'
            exit 0
        }
        $manifest = Get-AppxPackageManifest -Package $package
        [pscustomobject]@{
            PackageFamilyName = [string]$package.PackageFamilyName
            InstallLocation = [string]$package.InstallLocation
            Applications = @(
                $manifest.Package.Applications.Application | ForEach-Object {
                    [pscustomobject]@{
                        Id = [string]$_.Id
                        Executable = [string]$_.Executable
                    }
                }
            )
        } | ConvertTo-Json -Compress -Depth 4
        """;

    private readonly IProcessRunner _processRunner;

    public CodexPackageService() : this(new ProcessRunner())
    {
    }

    internal CodexPackageService(IProcessRunner processRunner) =>
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public async Task<CodexPackageInfo> DiscoverAsync(CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunCapturedAsync(
            new ProcessRequest(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", DiscoveryScript]),
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Codex package discovery failed.");
        }

        PackagePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PackagePayload>(result.StandardOutput);
        }
        catch (JsonException)
        {
            throw new InvalidDataException(InvalidOutputMessage);
        }

        if (payload is null)
        {
            throw new InvalidOperationException("The Codex package is not installed.");
        }

        if (payload.Applications is null ||
            string.IsNullOrWhiteSpace(payload.PackageFamilyName) ||
            string.IsNullOrWhiteSpace(payload.InstallLocation) ||
            !Path.IsPathFullyQualified(payload.InstallLocation))
        {
            throw new InvalidDataException(InvalidOutputMessage);
        }

        if (payload.Applications.Count != 1)
        {
            throw new InvalidOperationException(
                "The Codex package manifest must contain exactly one application.");
        }

        var application = payload.Applications[0];
        if (string.IsNullOrWhiteSpace(application.Id) ||
            string.IsNullOrWhiteSpace(application.Executable) ||
            Path.IsPathFullyQualified(application.Executable))
        {
            throw new InvalidDataException(InvalidOutputMessage);
        }

        string installLocation;
        string mainExecutablePath;
        try
        {
            installLocation = Path.GetFullPath(payload.InstallLocation);
            mainExecutablePath = Path.GetFullPath(Path.Combine(installLocation, application.Executable));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(InvalidOutputMessage);
        }

        var executableDirectory = Path.GetDirectoryName(mainExecutablePath);
        if (!IsUnderDirectory(mainExecutablePath, installLocation) ||
            !string.Equals(
                Path.GetFileName(mainExecutablePath),
                "ChatGPT.exe",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(executableDirectory))
        {
            throw new InvalidDataException(InvalidOutputMessage);
        }

        return new CodexPackageInfo(
            payload.PackageFamilyName,
            $"{payload.PackageFamilyName}!{application.Id}",
            installLocation,
            mainExecutablePath,
            Path.Combine(executableDirectory, "resources"));
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return !Path.IsPathFullyQualified(relativePath) &&
               !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class PackagePayload
    {
        public string? PackageFamilyName { get; init; }

        public string? InstallLocation { get; init; }

        public List<ApplicationPayload>? Applications { get; init; }
    }

    private sealed class ApplicationPayload
    {
        public string? Id { get; init; }

        public string? Executable { get; init; }
    }
}
