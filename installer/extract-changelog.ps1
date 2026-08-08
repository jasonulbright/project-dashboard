#Requires -Version 7.0
<#
.SYNOPSIS
    Extracts one version's section from CHANGELOG.md for use as a release body.

.DESCRIPTION
    Finds the "## [<version>] - <date>" heading and returns everything up to the next
    "## " heading. An absent heading, or a heading with no body under it, is an error:
    a release published with an empty body is not recoverable, because published
    assets and notes are never rewritten.

.PARAMETER Version
    Version to extract, with or without a leading "v".

.PARAMETER ChangelogPath
    CHANGELOG.md to read. Defaults to the file at the repository root.

.PARAMETER OutputPath
    File to write the extracted section to. Without it the section goes to stdout.

.EXAMPLE
    pwsh -File installer\extract-changelog.ps1 -Version 2.0.0

.EXAMPLE
    pwsh -File installer\extract-changelog.ps1 -Version v2.0.0 -OutputPath notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $ChangelogPath,

    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ChangelogPath) {
    $ChangelogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'
}

if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    throw "Changelog not found: $ChangelogPath."
}

$wanted = $Version.TrimStart('v', 'V')
$lines  = Get-Content -LiteralPath $ChangelogPath

# Headings are "## [<version>] - <date>"; the bracket form is matched literally so that
# 1.2.0 never matches 1.2.0.1.
$headingPattern = '^##\s+\[' + [regex]::Escape($wanted) + '\]'

$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $headingPattern) {
        $start = $i
        break
    }
}

if ($start -lt 0) {
    throw "No '## [$wanted]' heading in $ChangelogPath."
}

$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') {
        $end = $i
        break
    }
}

# A reversed range would silently select lines above the heading, so an empty section
# is detected before slicing rather than after.
$body = ''
if ($end -gt ($start + 1)) {
    $body = ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
}

if (-not $body) {
    throw "The '## [$wanted]' section in $ChangelogPath has no body."
}

if ($OutputPath) {
    $outputDir = Split-Path -Parent $OutputPath
    if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }
    Set-Content -LiteralPath $OutputPath -Value $body -Encoding utf8
    Write-Host "Wrote $($body.Length) characters of $wanted notes to $OutputPath."
}
else {
    $body
}
