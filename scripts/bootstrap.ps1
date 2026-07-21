[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DotnetVersion = '9.0.316'
$CodexAuthUrl = 'https://github.com/Loongphy/codex-auth/releases/download/v0.2.10/codex-auth-Windows-X64.zip'
$CodexAuthSha256 = 'CDF2C4D9CC827C91C24EB4C032B9F6792F581B42808DF5DB167C39B255EA7108'
$CodexAuthExeSha256 = '7E8E79976FE6A106B200860738C81636D18C9EAAB0196F342C53FDAAA5791F11'

$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$dotnetExtractionProbe = Join-Path $ProjectRoot '.tools\dotnet\sdk\9.0.316\Sdks\Microsoft.NET.Sdk.BlazorWebAssembly\targets\Microsoft.NET.Sdk.BlazorWebAssembly.ServiceWorkerAssetsManifest.5_0.targets'
$temporaryDriveName = $null
$substExecutable = Join-Path $env:SystemRoot 'System32\subst.exe'
$effectiveProjectRoot = $ProjectRoot

if ($dotnetExtractionProbe.Length -ge 260) {
    $usedDriveNames = @(Get-PSDrive -PSProvider FileSystem | Select-Object -ExpandProperty Name)
    $temporaryDriveName = @('Z', 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R') |
        Where-Object { $_ -notin $usedDriveNames } |
        Select-Object -First 1
    if ($null -eq $temporaryDriveName) {
        throw 'No unused drive letter is available for the temporary local SDK installation mapping.'
    }

    & $substExecutable "$temporaryDriveName`:" $ProjectRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create temporary drive $temporaryDriveName`: for the local SDK installation."
    }

    $effectiveProjectRoot = "$temporaryDriveName`:\"
}

try {
    $ToolsDirectory = Join-Path $effectiveProjectRoot '.tools'
    $DotnetDirectory = Join-Path $ToolsDirectory 'dotnet'
    $DotnetInstallScript = Join-Path $ToolsDirectory 'dotnet-install.ps1'
    $CodexAuthDirectory = Join-Path $effectiveProjectRoot 'vendor\codex-auth'
    $CodexAuthArchive = Join-Path $CodexAuthDirectory 'codex-auth-Windows-X64.zip'
    $CodexAuthExe = Join-Path $CodexAuthDirectory 'codex-auth.exe'
    $DotnetSdkCompletionFile = Join-Path $DotnetDirectory 'sdk\9.0.316\Sdks\Microsoft.NET.Sdk.BlazorWebAssembly\targets\Microsoft.NET.Sdk.BlazorWebAssembly.ServiceWorkerAssetsManifest.5_0.targets'

    New-Item -ItemType Directory -Force -Path $ToolsDirectory, $CodexAuthDirectory | Out-Null
    if ((Test-Path -LiteralPath $DotnetDirectory -PathType Container) -and
        -not (Test-Path -LiteralPath $DotnetSdkCompletionFile -PathType Leaf)) {
        Remove-Item -LiteralPath $DotnetDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $DotnetDirectory | Out-Null

    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $DotnetInstallScript
    & $DotnetInstallScript -Version $DotnetVersion -InstallDir $DotnetDirectory -Architecture x64 -NoPath
    if ($LASTEXITCODE -ne 0) {
        throw "The local .NET SDK installation failed with exit code $LASTEXITCODE."
    }

    $dotnetExe = Join-Path $DotnetDirectory 'dotnet.exe'
    $dotnetSdkVersionFile = Join-Path $DotnetDirectory "sdk\$DotnetVersion\.version"
    if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) {
        throw "The local .NET SDK installation did not produce dotnet.exe in $DotnetDirectory."
    }
    if (-not (Test-Path -LiteralPath $dotnetSdkVersionFile -PathType Leaf)) {
        throw "The local .NET SDK installation did not produce $dotnetSdkVersionFile."
    }
    $dotnetSdkMarker = @(Get-Content -LiteralPath $dotnetSdkVersionFile)
    if ($dotnetSdkMarker -notcontains $DotnetVersion -or $dotnetSdkMarker -notcontains 'win-x64') {
        throw "The local .NET SDK version marker does not identify $DotnetVersion for win-x64."
    }

    $hadMultilevelLookup = Test-Path Env:DOTNET_MULTILEVEL_LOOKUP
    $previousMultilevelLookup = $env:DOTNET_MULTILEVEL_LOOKUP
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    try {
        $sdkList = @(& $dotnetExe --list-sdks)
        if ($LASTEXITCODE -ne 0) {
            throw "The local .NET SDK validation failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        if ($hadMultilevelLookup) {
            $env:DOTNET_MULTILEVEL_LOOKUP = $previousMultilevelLookup
        }
        else {
            Remove-Item Env:DOTNET_MULTILEVEL_LOOKUP
        }
    }

    $expectedSdkEntry = "$DotnetVersion [$(Join-Path $DotnetDirectory 'sdk')]"
    if ($sdkList.Count -ne 1 -or $sdkList[0].Trim() -ne $expectedSdkEntry) {
        throw "Expected exactly '$expectedSdkEntry' from the local SDK, got '$($sdkList -join '; ')'."
    }

    Invoke-WebRequest -Uri $CodexAuthUrl -OutFile $CodexAuthArchive
    $archiveHash = (Get-FileHash -LiteralPath $CodexAuthArchive -Algorithm SHA256).Hash
    if (-not [string]::Equals($archiveHash, $CodexAuthSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "codex-auth archive SHA-256 mismatch. Expected $CodexAuthSha256 but found $archiveHash."
    }

    $temporaryExtractDirectory = Join-Path ([IO.Path]::GetTempPath()) ("codex-auth-{0}" -f [Guid]::NewGuid())
    New-Item -ItemType Directory -Force -Path $temporaryExtractDirectory | Out-Null

    try {
        Expand-Archive -LiteralPath $CodexAuthArchive -DestinationPath $temporaryExtractDirectory
        $executableCandidates = @(Get-ChildItem -LiteralPath $temporaryExtractDirectory -Filter 'codex-auth.exe' -File -Recurse)
        if ($executableCandidates.Count -ne 1) {
            throw "Expected exactly one codex-auth.exe in $CodexAuthArchive, found $($executableCandidates.Count)."
        }

        $executableHash = (Get-FileHash -LiteralPath $executableCandidates[0].FullName -Algorithm SHA256).Hash
        if (-not [string]::Equals($executableHash, $CodexAuthExeSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "codex-auth executable SHA-256 mismatch. Expected $CodexAuthExeSha256 but found $executableHash."
        }

        Move-Item -LiteralPath $executableCandidates[0].FullName -Destination $CodexAuthExe -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryExtractDirectory -Recurse -Force
    }
}
finally {
    if ($null -ne $temporaryDriveName) {
        & $substExecutable "$temporaryDriveName`:" /D
        if ($LASTEXITCODE -ne 0) {
            throw "Could not remove temporary drive $temporaryDriveName`: after the local SDK installation."
        }
    }
}
