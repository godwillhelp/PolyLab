param([Parameter(Mandatory = $true)][string]$Out)
# Packs link\PolyLab_PfxLink into <Out>\@PolyLab_PfxLink\addons\PolyLab_PfxLink.pbo with DayZ Tools' AddonBuilder (prefix
# PolyLab_PfxLink, scripts and config as text). AddonBuilder returns 0 even when it fails, so its log decides.
$ErrorActionPreference = "Stop"
$tools = (Get-ItemProperty "HKCU:\Software\Bohemia Interactive\Dayz Tools" -ErrorAction SilentlyContinue).path
if (-not $tools) { throw "DayZ Tools not found (HKCU:\Software\Bohemia Interactive\Dayz Tools)" }
$builder = Join-Path $tools "Bin\AddonBuilder\AddonBuilder.exe"
$src = Join-Path $PSScriptRoot "PolyLab_PfxLink"
$dst = Join-Path $Out "@PolyLab_PfxLink\addons"
New-Item -ItemType Directory -Force $dst | Out-Null
$ErrorActionPreference = "Continue"
$log = & $builder $src $dst -packonly "-prefix=PolyLab_PfxLink" -clear 2>&1 | Out-String
$ErrorActionPreference = "Stop"
if ($log -notmatch "Build Successful") { $log; throw "AddonBuilder failed" }
Get-Item (Join-Path $dst "PolyLab_PfxLink.pbo")
