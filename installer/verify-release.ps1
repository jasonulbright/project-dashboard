#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version,
    [string] $ExpectedSubjectCommonName = 'Jason Ulbright',
    [switch] $SkipSignatureCheck,
    [switch] $SmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($SmokeTest -and $env:GITHUB_ACTIONS -ne 'true') {
    throw 'Install/uninstall smoke tests require a disposable GitHub Actions runner.'
}
if ($SmokeTest -and $SkipSignatureCheck) { throw 'The release smoke test requires signed artifacts.' }

function Assert-Signature([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing file: $Path." }
    if ($SkipSignatureCheck) { return }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { throw "Invalid signature on ${Path}: $($signature.StatusMessage)" }
    $signer = $signature.SignerCertificate.GetNameInfo(
        [Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($signer -cne $ExpectedSubjectCommonName) { throw "Unexpected signer on ${Path}: $signer." }
    if ($null -eq $signature.TimeStamperCertificate) { throw "Missing timestamp on $Path." }
    Write-Host "Verified $Path (signer: $signer; timestamp: $($signature.TimeStamperCertificate.Subject))."
}

function Assert-SameFile([string] $Expected, [string] $Actual) {
    if (-not (Test-Path -LiteralPath $Actual -PathType Leaf)) { throw "Missing packaged file: $Actual." }
    if ((Get-FileHash -LiteralPath $Expected).Hash -ne (Get-FileHash -LiteralPath $Actual).Hash) {
        throw "Packaged bytes differ: $Actual."
    }
}

$payload = Join-Path $PSScriptRoot 'payload'
$installer = Join-Path $PSScriptRoot "ProjectDashboard-Setup-$Version.exe"
$portable = Join-Path $PSScriptRoot "ProjectDashboard-Portable-$Version.zip"
$uninstaller = Join-Path $PSScriptRoot 'signed-uninstaller\Uninstall.exe'
$scratch = Join-Path $PSScriptRoot ('release-verification.local\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

foreach ($name in @('ProjectDashboard.exe', 'ProjectDashboard.dll')) {
    $path = Join-Path $payload $name
    Assert-Signature $path
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
    if ($actualVersion -ne "$Version.0") { throw "Unexpected version on ${name}: $actualVersion." }
}
Assert-Signature $installer
Assert-Signature $uninstaller
foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Assert-SameFile (Join-Path (Split-Path -Parent $PSScriptRoot) $name) (Join-Path $payload $name)
}
if (Test-Path -LiteralPath (Join-Path $payload 'portable.marker')) { throw 'Installer payload contains portable.marker.' }

Expand-Archive -LiteralPath $portable -DestinationPath $scratch
$portableRoot = Join-Path $scratch "ProjectDashboard-Portable-$Version"
$payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
$archiveFiles = @(Get-ChildItem -LiteralPath $scratch -File -Recurse)
if ($archiveFiles.Count -ne $payloadFiles.Count + 1) { throw 'Portable archive has an unexpected file count.' }
foreach ($file in $payloadFiles) {
    if ($file.Name -match '(?i)(\.Tests\.dll|testhost|xunit|testplatform)') { throw "Test runtime in payload: $($file.Name)." }
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
    Assert-SameFile $file.FullName (Join-Path $portableRoot $relative)
}
if (-not (Test-Path -LiteralPath (Join-Path $portableRoot 'portable.marker') -PathType Leaf)) {
    throw 'Portable archive has no portable.marker.'
}
Write-Host "Verified portable archive: $($payloadFiles.Count) payload files and portable.marker."

if ($SmokeTest) {
    $installRoot = Join-Path $scratch 'installed'
    if (Test-Path -LiteralPath $installRoot) { throw 'Smoke installation target already exists.' }
    $setup = Start-Process -FilePath $installer -ArgumentList '/S', "/D=$installRoot" -WindowStyle Hidden -PassThru
    if (-not $setup.WaitForExit(60000)) { $setup.Kill(); throw 'Installer timed out.' }
    if ($setup.ExitCode -ne 0) { throw "Installer failed: $($setup.ExitCode)." }
    foreach ($file in $payloadFiles) {
        Assert-SameFile $file.FullName (Join-Path $installRoot ([IO.Path]::GetRelativePath($payload, $file.FullName)))
    }
    Assert-SameFile $uninstaller (Join-Path $installRoot 'Uninstall.exe')
    if (Test-Path -LiteralPath (Join-Path $installRoot 'portable.marker')) { throw 'Installed app selects portable mode.' }

    foreach ($appRoot in @($portableRoot, $installRoot)) {
        $data = Join-Path $scratch ([Guid]::NewGuid().ToString('N'))
        $projects = Join-Path $data 'projects'
        New-Item -ItemType Directory -Path $projects -Force | Out-Null
        @{
            ProjectsRootPath = $projects
            EnableGitHubDiscovery = $false
            EnableUpdateCheck = $false
            EnableScheduledFetch = $false
            GhPath = (Join-Path $data 'missing-gh.exe')
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
        $app = Start-Process -FilePath (Join-Path $appRoot 'ProjectDashboard.exe') -WindowStyle Hidden -PassThru `
            -Environment @{ PD_DATA_DIR = $data }
        try {
            if ($app.WaitForExit(8000)) { throw "Packaged app exited during startup: $($app.ExitCode)." }
            $log = Join-Path $data 'log.txt'
            if (Test-Path -LiteralPath $log) {
                if ((Get-Content -LiteralPath $log -Raw) -match '\[ERROR\]|Startup failed|Unhandled .*exception') {
                    throw "Packaged app logged a startup error: $log."
                }
            }
            $app.Refresh()
            if ($app.MainWindowHandle -eq 0) { throw 'Packaged app did not create its main window.' }
            Write-Host "Packaged app launched: $appRoot."
        }
        finally {
            if (-not $app.HasExited) {
                $closed = $app.CloseMainWindow()
                if (-not $closed -or -not $app.WaitForExit(10000)) { $app.Kill(); $app.WaitForExit() }
            }
        }
    }

    $resolvedInstall = [IO.Path]::GetFullPath($installRoot)
    if ([IO.Path]::GetDirectoryName($resolvedInstall) -ne [IO.Path]::GetFullPath($scratch)) {
        throw 'Uninstall target is outside the verification workspace.'
    }
    $remove = Start-Process -FilePath (Join-Path $installRoot 'Uninstall.exe') -ArgumentList '/S', "_?=$installRoot" `
        -WindowStyle Hidden -PassThru
    if (-not $remove.WaitForExit(60000)) { $remove.Kill(); throw 'Uninstaller timed out.' }
    if ($remove.ExitCode -ne 0) { throw "Uninstaller failed: $($remove.ExitCode)." }
    if (Test-Path -LiteralPath (Join-Path $installRoot 'ProjectDashboard.exe')) { throw 'Uninstaller left the application installed.' }
    Write-Host 'Installation, application startup, and uninstallation smoke tests passed.'
}
