#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the portable Project Dashboard archive.

.DESCRIPTION
    Publishes the app into installer\payload — the same directory project-dashboard.nsi
    packs into the installer — then zips that payload plus a portable marker file into
    ProjectDashboard-Portable-<version>.zip.

    The version is read from the !define VERSION line in project-dashboard.nsi and
    verified against <Version> in the project file; a mismatch aborts the build.

.PARAMETER Configuration
    Build configuration passed to dotnet publish. Defaults to Release.

.PARAMETER OutputDirectory
    Where the .zip is written. Defaults to the installer directory, beside the
    installer executable.

.EXAMPLE
    pwsh -File installer\build-portable.ps1

.EXAMPLE
    pwsh -File installer\build-portable.ps1 -OutputDirectory C:\dist
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot     = Split-Path -Parent $installerDir
$nsiFile      = Join-Path $installerDir 'project-dashboard.nsi'
$projectFile  = Join-Path $repoRoot 'src\ProjectDashboard\ProjectDashboard.csproj'
$payloadDir   = Join-Path $installerDir 'payload'

if (-not $OutputDirectory) { $OutputDirectory = $installerDir }

# The installer's !define is the single version definition for packaging; the project
# file stamps the binaries. A drift between them would ship a zip whose name disagrees
# with the assembly version inside it.
$nsiText = Get-Content -LiteralPath $nsiFile -Raw
if ($nsiText -notmatch '(?m)^\s*!define\s+VERSION\s+"([^"]+)"') {
    throw "No !define VERSION found in $nsiFile."
}
$version = $Matches[1]

[xml] $projectXml = Get-Content -LiteralPath $projectFile -Raw
$projectVersionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $projectVersionNode) {
    throw "No <Version> element found in $projectFile."
}
if ($projectVersionNode.InnerText.Trim() -ne $version) {
    throw "Version mismatch: $nsiFile says $version, $projectFile says $($projectVersionNode.InnerText.Trim())."
}

Write-Host "Building Project Dashboard $version ($Configuration)"

if (Test-Path -LiteralPath $payloadDir) {
    Remove-Item -LiteralPath $payloadDir -Recurse -Force
}

dotnet publish $projectFile `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $payloadDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# The installer packs payload\*.* wholesale and the portable archive is a copy of the
# payload, so a license file placed here travels with both release assets. The MIT terms
# on this app and on every redistributed binary require that.
foreach ($noticeName in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $notice = Join-Path $repoRoot $noticeName
    if (-not (Test-Path -LiteralPath $notice)) {
        throw "Missing $noticeName at $repoRoot; the release assets must ship it."
    }
    Copy-Item -LiteralPath $notice -Destination (Join-Path $payloadDir $noticeName) -Force
}

$archiveName = "ProjectDashboard-Portable-$version"
$stageDir    = Join-Path $installerDir $archiveName
$zipPath     = Join-Path $OutputDirectory "$archiveName.zip"

if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}

# A partial zip written straight to $zipPath is an openable archive under the release
# asset name; the staging tree left behind on failure would be picked up by a later
# `git add -A`.
$partialPath = "$zipPath.partial"
try {
    New-Item -ItemType Directory -Path $stageDir | Out-Null

    # The archive carries exactly what the installer packs (payload\*.*) plus the marker.
    # The installer must never see the marker: an installed copy has to keep using the
    # per-user data location.
    Copy-Item -Path (Join-Path $payloadDir '*') -Destination $stageDir -Recurse -Force

    Set-Content -LiteralPath (Join-Path $stageDir 'portable.marker') -Encoding utf8 -Value @'
This file makes Project Dashboard keep its settings, cache, log, and project
metadata in the data folder beside ProjectDashboard.exe.

Delete it to use the standard per-user location instead.
'@

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stageDir,
        $partialPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)

    Move-Item -LiteralPath $partialPath -Destination $zipPath -Force
}
finally {
    # A file another process holds open fails the zip and then fails this cleanup too.
    # Under $ErrorActionPreference = 'Stop' an unguarded removal is terminating: it
    # aborts the rest of this block, so the .partial survives beside the release asset,
    # and on the success path it fails the script after the zip is already correct.
    if (Test-Path -LiteralPath $stageDir) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    }
}

$zip = Get-Item -LiteralPath $zipPath
Write-Host ("Wrote {0} ({1:N1} MB)" -f $zip.FullName, ($zip.Length / 1MB))
