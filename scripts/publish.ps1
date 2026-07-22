[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$solutionPath = Join-Path $projectRoot 'CodexAccountSwitcher.sln'
$projectPath = Join-Path $projectRoot 'src\CodexAccountSwitcher\CodexAccountSwitcher.csproj'
$publishDirectory = Join-Path $projectRoot 'dist\CodexAccountSwitcher'
$helperPath = Join-Path $projectRoot 'vendor\codex-auth\codex-auth.exe'
$helperManifestPath = Join-Path $projectRoot 'vendor\codex-auth\manifest.json'
$expectedHelperSha256 = '7E8E79976FE6A106B200860738C81636D18C9EAAB0196F342C53FDAAA5791F11'

function Assert-DotnetSucceeded {
    param(
        [Parameter(Mandatory)]
        [string]$Operation
    )

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

foreach ($path in @($dotnet, $solutionPath, $projectPath, $helperPath, $helperManifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file is missing: $path"
    }
}

& $dotnet restore $solutionPath
Assert-DotnetSucceeded -Operation 'dotnet restore'

& $dotnet build $solutionPath -c Release
Assert-DotnetSucceeded -Operation 'dotnet build'

& $dotnet test $solutionPath -c Release
Assert-DotnetSucceeded -Operation 'dotnet test'

& $dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $publishDirectory
Assert-DotnetSucceeded -Operation 'dotnet publish'

$actualHelperSha256 = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash
if (-not [string]::Equals($actualHelperSha256, $expectedHelperSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "codex-auth.exe SHA-256 mismatch. Expected $expectedHelperSha256 but found $actualHelperSha256."
}

$toolsDirectory = Join-Path $publishDirectory 'tools'
New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
Copy-Item -LiteralPath $helperPath -Destination (Join-Path $toolsDirectory 'codex-auth.exe') -Force
Copy-Item -LiteralPath $helperManifestPath -Destination (Join-Path $toolsDirectory 'manifest.json') -Force

Write-Host "Published framework-dependent win-x64 distribution to $publishDirectory"
