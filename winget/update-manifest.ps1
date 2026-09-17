# Writes Windows Package Manager manifests for a published GitHub release.
#   .\winget\update-manifest.ps1 -Version 1.4.0
# The installer URL and SHA-256 come from the release itself, so they always match the
# exe GitHub Actions built. Validate with:  winget validate winget\manifests\r\RoyMutwiri\MachineGauges\<version>
param([Parameter(Mandatory)] [string] $Version)
$ErrorActionPreference = 'Stop'

$id = 'RoyMutwiri.MachineGauges'
$api = "https://api.github.com/repos/Roy-Mutwiri/MachineGauges/releases/tags/v$Version"
$release = Invoke-RestMethod $api -Headers @{ 'User-Agent' = 'MachineGauges-winget'; Accept = 'application/vnd.github+json' }
$asset = $release.assets | Where-Object name -eq 'MachineGauges.exe' | Select-Object -First 1
if (-not $asset) { throw "Release v$Version has no MachineGauges.exe" }
if ($asset.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "Release asset has no SHA-256 digest" }
$sha = $Matches[1].ToUpperInvariant()
$date = ([datetime]$release.published_at).ToString('yyyy-MM-dd')

$dir = Join-Path $PSScriptRoot "manifests\r\RoyMutwiri\MachineGauges\$Version"
New-Item -ItemType Directory -Force $dir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

[IO.File]::WriteAllText((Join-Path $dir "$id.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@, $utf8)

[IO.File]::WriteAllText((Join-Path $dir "$id.installer.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.17763.0
InstallerType: exe
Scope: user
InstallModes:
- interactive
- silent
InstallerSwitches:
  Silent: --install --quiet
  SilentWithProgress: --install --quiet
UpgradeBehavior: install
ReleaseDate: $date
AppsAndFeaturesEntries:
- DisplayName: MachineGauges
  Publisher: MachineGauges
  ProductCode: MachineGauges
InstallationMetadata:
  DefaultInstallLocation: '%LOCALAPPDATA%\Programs\MachineGauges'
Installers:
- Architecture: x64
  InstallerUrl: $($asset.browser_download_url)
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@, $utf8)

[IO.File]::WriteAllText((Join-Path $dir "$id.locale.en-US.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Roy Mutwiri
PublisherUrl: https://github.com/Roy-Mutwiri
PublisherSupportUrl: https://github.com/Roy-Mutwiri/MachineGauges/issues
PackageName: MachineGauges
PackageUrl: https://github.com/Roy-Mutwiri/MachineGauges
License: Freeware
ShortDescription: Live CPU, RAM, GPU, disk and network gauges pinned to the top of your screen, matching Task Manager.
Description: |-
  MachineGauges shows animated gauges for CPU, memory, GPU, disk and network usage in a click-through strip at the top of the screen, using the same performance counters as Task Manager.
  Press Ctrl+Shift+G for a details panel with history charts, per-core CPU, every GPU and disk, and top processes.
  Includes alerts, a CSV history log, optional FPS, ping, battery and clock gauges, and automatic updates. No admin rights needed.
Tags:
- cpu
- gpu
- hardware-monitor
- overlay
- performance
- system-monitor
- task-manager
ReleaseNotesUrl: $($release.html_url)
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@, $utf8)

"Wrote winget manifests for $Version to $dir (sha256 $sha)"
