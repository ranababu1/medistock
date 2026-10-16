<#
.SYNOPSIS
    Builds the self-contained MediStock release artefact and publishes it to GitHub Releases.

.DESCRIPTION
    Publishes MediStock.Desktop as a self-contained, single-file win-x64 build, checks that the
    output actually looks like an application, writes a SHA-256 manifest, zips the lot into
    artifacts\, and then - if the GitHub CLI is authenticated - creates the GitHub release and
    uploads the zip.

    The version is stamped into the artefact name and into the release tag. It must match
    <Version> in Directory.Build.props, which is the single source of truth.

.PARAMETER Version
    The version being released, without a leading 'v' - for example 0.5.0.

.PARAMETER Notes
    Path to a markdown file whose contents become the release body. Optional; when omitted the
    matching section of CHANGELOG.md is sent instead.

.PARAMETER SkipRelease
    Build and zip the artefact but do not create the GitHub release.

.PARAMETER AllowDirtyTree
    Release even though the working tree has uncommitted changes.

.EXAMPLE
    .\scripts\release\publish-release.ps1 -Version 0.5.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Notes,

    [switch]$SkipRelease,

    [switch]$AllowDirtyTree
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --------------------------------------------------------------------------- paths
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot 'src\MediStock.Desktop\MediStock.Desktop.csproj'
$props = Join-Path $repoRoot 'Directory.Build.props'
$changelog = Join-Path $repoRoot 'CHANGELOG.md'
$artifacts = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifacts "publish\$Version"
$zipPath = Join-Path $artifacts "MediStock-$Version-win-x64.zip"

function Write-Step([string]$text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

# --------------------------------------------------------------------------- preflight
Write-Step 'Preflight'

if (-not (Test-Path $project)) { throw "Cannot find $project. Run this from inside the repository." }

[xml]$propsXml = Get-Content $props
$declaredVersion = $propsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if ($declaredVersion -ne $Version) {
    throw "Directory.Build.props declares version '$declaredVersion' but you asked for '$Version'. " +
          'Bump <Version> in Directory.Build.props first - it is the single source of truth.'
}
Write-Ok "Directory.Build.props declares $declaredVersion"

Push-Location $repoRoot
try {
    $dirty = git status --porcelain
    if ($dirty -and -not $AllowDirtyTree) {
        Write-Host $dirty
        throw 'The working tree has uncommitted changes. Commit them, or pass -AllowDirtyTree for a throwaway build.'
    }
    if ($dirty) { Write-Warn2 'working tree is dirty; -AllowDirtyTree was passed' } else { Write-Ok 'working tree is clean' }

    $head = (git rev-parse --short HEAD).Trim()
    Write-Ok "HEAD is $head"

    $existingTag = git tag --list "v$Version"
    if ($existingTag) { Write-Warn2 "tag v$Version already exists locally" }
}
finally {
    Pop-Location
}

# --------------------------------------------------------------------------- build
Write-Step 'Testing'

dotnet test (Join-Path $repoRoot 'MediStock.slnx') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The test suite failed. Not releasing.' }
Write-Ok 'all tests passed'

# --------------------------------------------------------------------------- publish
Write-Step "Publishing $Version for win-x64"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $publishDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# --------------------------------------------------------------------------- sanity check
Write-Step 'Checking the published output'

$exe = Join-Path $publishDir 'MediStock.Desktop.exe'
if (-not (Test-Path $exe)) {
    Get-ChildItem $publishDir | Select-Object Name, Length
    throw 'The published folder has no MediStock.Desktop.exe.'
}
Write-Ok "MediStock.Desktop.exe is $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB"

$settings = Join-Path $publishDir 'appsettings.json'
if (-not (Test-Path $settings)) { throw 'appsettings.json was not published next to the executable.' }
Write-Ok 'appsettings.json is beside the executable'

$native = Get-ChildItem $publishDir -Filter '*.dll' | Measure-Object
if ($native.Count -gt 12) {
    Write-Warn2 "$($native.Count) loose assemblies were published; the single-file bundle may not have taken effect."
}

# The product version must be what we asked for, or the Support tab will lie to a pharmacist.
$fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($fileVersion -notlike "$Version*") {
    throw "The executable reports product version '$fileVersion' but $Version was requested."
}
Write-Ok "executable reports product version $fileVersion"

# --------------------------------------------------------------------------- manifest and zip
Write-Step 'Writing the manifest and the archive'

$hashLines = Get-ChildItem $publishDir -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
$hashLines | Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
Write-Ok "SHA256SUMS.txt covers $($hashLines.Count) file(s)"

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Ok "$([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB -> $zipPath"

# --------------------------------------------------------------------------- release notes
if (-not $Notes) {
    $Notes = Join-Path $artifacts "release-notes-$Version.md"
    $body = "MediStock $Version"

    if (Test-Path $changelog) {
        $lines = Get-Content $changelog
        $matches = $lines | Select-String -Pattern "^## \[$([regex]::Escape($Version))\]"
        if (-not $matches) { $matches = $lines | Select-String -Pattern '^## \[Unreleased\]' }

        if ($matches) {
            $start = $matches[0].LineNumber
            $body = $lines[$start..($lines.Count - 1)] -join "`n"
        }
    }

    @"
# MediStock $Version

$body

## Downloads

- ``MediStock-$Version-win-x64.zip`` - self-contained 64-bit Windows build. No .NET installation is
  needed; SQL Server Express LocalDB or any reachable SQL Server instance is.

## Verify your download

```powershell
Get-FileHash .\MediStock-$Version-win-x64.zip -Algorithm SHA256
```

Compare the result with ``SHA256SUMS.txt`` in this release.
"@ | Set-Content $Notes -Encoding utf8
}
Write-Ok "release notes: $Notes"

# --------------------------------------------------------------------------- gh release
if ($SkipRelease) {
    Write-Step 'Skipping the GitHub release (-SkipRelease)'
    Write-Ok "artefact ready: $zipPath"
    return
}

Write-Step 'Creating the GitHub release'

$gh = Get-Command gh -ErrorAction SilentlyContinue
$canRelease = $false

if (-not $gh) {
    Write-Warn2 'the GitHub CLI (gh) is not installed - run: winget install --id GitHub.cli'
}
else {
    & gh auth status *> $null
    $canRelease = $LASTEXITCODE -eq 0
    if (-not $canRelease) { Write-Warn2 'gh is not authenticated - run: gh auth login' }
}

if ($canRelease) {
    Push-Location $repoRoot
    try {
        $target = (git rev-parse HEAD).Trim()
        & gh release create "v$Version" $zipPath (Join-Path $artifacts 'SHA256SUMS.txt') `
            --title "MediStock $Version" `
            --notes-file $Notes `
            --target $target
        if ($LASTEXITCODE -ne 0) {
            Write-Warn2 'gh release create failed. The artefact is still on disk; create the release by hand.'
        }
        else {
            Write-Ok "released v$Version"
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host ''
Write-Host "Artefact : $zipPath" -ForegroundColor Cyan
Write-Host "Notes    : $Notes" -ForegroundColor Cyan
Write-Host "Remember to push the commit and the tag:  git push origin main --follow-tags" -ForegroundColor Cyan
