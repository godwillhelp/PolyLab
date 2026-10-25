param(
    [Parameter(Mandatory = $true)][ValidateSet("pbog", "pfx", "launcher")][string]$Id,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Out = "dist"
)
# Builds one release asset the way the launcher installs it: a self-contained single-file win-x64 publish (users need no
# .NET) with tool.json, LICENSE and THIRD_PARTY.md, zipped as <ExeStem>-<version>-win-x64.zip; for the launcher also the
# Inno Setup installer PolyLab-<version>-setup.exe. The release workflow runs it for each tag. Returns the release title,
# the files to attach and whether the version is a prerelease.
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "version '$Version' is not x.y.z or x.y.z-pre" }
$root = Split-Path $PSScriptRoot -Parent
$tool = @{
    pbog     = @{ Project = "src\PBOGPacker\PBOG_Packer.csproj"; Exe = "PBOG_Packer.exe"; Title = "PBOG Packer" }
    pfx      = @{ Project = "src\ParticleStudio\Pfx.App\Pfx.App.csproj"; Exe = "Particle_Studio.exe"; Title = "Particle Studio" }
    launcher = @{ Project = "src\Launcher\PolyLab.Launcher.csproj"; Exe = "PolyLab.exe"; Title = "Poly Lab" }
}[$Id]
$stem = [IO.Path]::GetFileNameWithoutExtension($tool.Exe)
$dist = [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).Path, $Out))
$pub = Join-Path $dist $stem
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }

dotnet publish (Join-Path $root $tool.Project) -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    "-p:Version=$Version" -o $pub | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$manifest = [ordered]@{ id = $Id; version = $Version; exe = $tool.Exe }
if ($Id -eq "pbog") {
    # PBOG Packer's pinned GRAFT release; the launcher downloads the release this names.
    $pin = Select-String -Path (Join-Path $root "src\PBOGPacker\Core\GraftRuntime.cs") -Pattern 'const string Pinned = "(v[^"]+)"'
    if (-not $pin) { throw "GraftRuntime.Pinned not found" }
    $manifest.graft = $pin.Matches[0].Groups[1].Value
}
[IO.File]::WriteAllText((Join-Path $pub "tool.json"), ($manifest | ConvertTo-Json))
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "THIRD_PARTY.md") $pub

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $dist "$stem-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($pub, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$files = @($zip)

if ($Id -eq "launcher") {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$pub" "/O$dist" (Join-Path $root "installer\PolyLab.iss") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
    $files += Join-Path $dist "PolyLab-$Version-setup.exe"
}

[pscustomobject]@{ Title = "$($tool.Title) $Version"; Files = $files; Prerelease = $Version.Contains("-") }
