param([Parameter(Mandatory = $true)][string]$Out)
# Packs link\NDZ_PfxLink into <Out>\@NDZ_PfxLink\addons\NDZ_PfxLink.pbo with DayZ Tools' AddonBuilder (prefix
# NDZ_PfxLink, scripts and config as text). AddonBuilder returns 0 even when it fails, so its log decides.
$ErrorActionPreference = "Stop"
$tools = (Get-ItemProperty "HKCU:\Software\Bohemia Interactive\Dayz Tools" -ErrorAction SilentlyContinue).path
if (-not $tools) { throw "DayZ Tools not found (HKCU:\Software\Bohemia Interactive\Dayz Tools)" }
$builder = Join-Path $tools "Bin\AddonBuilder\AddonBuilder.exe"
$src = Join-Path $PSScriptRoot "NDZ_PfxLink"
$dst = Join-Path $Out "@NDZ_PfxLink\addons"
New-Item -ItemType Directory -Force $dst | Out-Null
$ErrorActionPreference = "Continue"
$log = & $builder $src $dst -packonly "-prefix=NDZ_PfxLink" -clear 2>&1 | Out-String
$ErrorActionPreference = "Stop"
if ($log -notmatch "Build Successful") { $log; throw "AddonBuilder failed" }
Get-Item (Join-Path $dst "NDZ_PfxLink.pbo")
