#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Path)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$destination = Join-Path $PSScriptRoot 'signed-uninstaller'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath $Path -Destination (Join-Path $destination 'Uninstall.exe') -Force
