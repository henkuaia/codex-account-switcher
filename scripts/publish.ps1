[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$solutionPath = Join-Path $projectRoot 'CodexAccountSwitcher.sln'
$projectPath = Join-Path $projectRoot 'src\CodexAccountSwitcher\CodexAccountSwitcher.csproj'
$distDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
$finalDirectory = [IO.Path]::GetFullPath((Join-Path $distDirectory 'CodexAccountSwitcher'))
$stagingDirectory = [IO.Path]::GetFullPath((Join-Path $distDirectory ('.CodexAccountSwitcher-staging-' + [Guid]::NewGuid().ToString('N'))))
$backupDirectory = [IO.Path]::GetFullPath((Join-Path $distDirectory ('.CodexAccountSwitcher-backup-' + [Guid]::NewGuid().ToString('N'))))
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

function Assert-StrictChildDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Parent,
        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $relativePath = Get-NormalizedRelativePath -Parent $Parent -Candidate $Candidate
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        $relativePath -eq '.' -or
        [IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -eq '..' -or
        $relativePath.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -or
        $relativePath.StartsWith("..$([IO.Path]::AltDirectorySeparatorChar)", [StringComparison]::Ordinal)) {
        throw "Directory '$Candidate' is not a strict child of '$Parent'."
    }
}

function Get-NormalizedRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$Parent,
        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $normalizedParent = [IO.Path]::GetFullPath($Parent)
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate)
    $getRelativePathMethod = [IO.Path].GetMethod('GetRelativePath', [Type[]]@([string], [string]))
    if ($null -ne $getRelativePathMethod) {
        return [IO.Path]::GetRelativePath($normalizedParent, $normalizedCandidate)
    }

    # Windows PowerShell 5 targets .NET Framework, which lacks Path.GetRelativePath.
    $parentUri = [Uri]::new($normalizedParent.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar)
    $candidateUri = [Uri]::new($normalizedCandidate)
    return [Uri]::UnescapeDataString($parentUri.MakeRelativeUri($candidateUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Remove-ValidatedStagingDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-StrictChildDirectory -Parent $distDirectory -Candidate $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

foreach ($path in @($dotnet, $solutionPath, $projectPath, $helperPath, $helperManifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file is missing: $path"
    }
}

Assert-StrictChildDirectory -Parent $distDirectory -Candidate $finalDirectory
Assert-StrictChildDirectory -Parent $distDirectory -Candidate $stagingDirectory
Assert-StrictChildDirectory -Parent $distDirectory -Candidate $backupDirectory

$actualHelperSha256 = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash
if (-not [string]::Equals($actualHelperSha256, $expectedHelperSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "codex-auth.exe SHA-256 mismatch. Expected $expectedHelperSha256 but found $actualHelperSha256."
}

$previousFinalMoved = $false
$finalReplacementComplete = $false

try {
    New-Item -ItemType Directory -Force -Path $distDirectory, $stagingDirectory | Out-Null

    & $dotnet restore $solutionPath
    Assert-DotnetSucceeded -Operation 'dotnet restore'

    & $dotnet build $solutionPath -c Release --no-restore
    Assert-DotnetSucceeded -Operation 'dotnet build'

    & $dotnet test $solutionPath -c Release --no-restore
    Assert-DotnetSucceeded -Operation 'dotnet test'

    & $dotnet publish $projectPath -c Release -r win-x64 --self-contained false --no-restore -o $stagingDirectory
    Assert-DotnetSucceeded -Operation 'dotnet publish'

    $stagingToolsDirectory = Join-Path $stagingDirectory 'tools'
    New-Item -ItemType Directory -Force -Path $stagingToolsDirectory | Out-Null
    Copy-Item -LiteralPath $helperPath -Destination (Join-Path $stagingToolsDirectory 'codex-auth.exe') -Force
    Copy-Item -LiteralPath $helperManifestPath -Destination (Join-Path $stagingToolsDirectory 'manifest.json') -Force

    if (Test-Path -LiteralPath $finalDirectory) {
        Move-Item -LiteralPath $finalDirectory -Destination $backupDirectory
        $previousFinalMoved = $true
    }

    Move-Item -LiteralPath $stagingDirectory -Destination $finalDirectory
    $finalReplacementComplete = $true

    if ($previousFinalMoved -and (Test-Path -LiteralPath $backupDirectory)) {
        try {
            Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        }
        catch {
            Write-Warning "Published distribution is ready, but the previous backup could not be removed."
        }
    }

    Write-Host "Published framework-dependent win-x64 distribution to $finalDirectory"
}
catch {
    $publishError = $_
    if ($previousFinalMoved -and
        -not $finalReplacementComplete -and
        (Test-Path -LiteralPath $backupDirectory) -and
        -not (Test-Path -LiteralPath $finalDirectory)) {
        Move-Item -LiteralPath $backupDirectory -Destination $finalDirectory
    }

    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-ValidatedStagingDirectory -Path $stagingDirectory
    }

    throw $publishError
}
