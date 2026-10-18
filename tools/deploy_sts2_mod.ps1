<#
.SYNOPSIS
    Builds the BalatroBridge mod and installs it into a Slay the Spire 2 folder's mods directory.

.DESCRIPTION
    Generates Jimbo's content classes and resource pack from your Balatro copy
    (tools\generate_balatro_content.py, tools\build_mod_pck.py), compiles against the target
    game's own DLLs, then copies BalatroBridge.dll, BalatroBridge.pck and the manifest to
    <GameDir>\mods\BalatroBridge\. On first install it also writes BalatroBridge.config.json
    pointing at the bridged Balatro build (tools\build_balatro.py). An existing config is never
    overwritten, so your edits survive redeploys.

.EXAMPLE
    .\tools\deploy_sts2_mod.ps1
    Installs into the project's copy at games\Slay the Spire 2.

.EXAMPLE
    .\tools\deploy_sts2_mod.ps1 -GameDir "F:\SteamLibrary\steamapps\common\Slay the Spire 2"
    Installs into the Steam install instead.
#>
param(
    [string]$GameDir = (Join-Path $PSScriptRoot "..\games\Slay the Spire 2"),
    [string]$BalatroExe = (Join-Path $PSScriptRoot "..\build\balatro\Balatro.exe"),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$GameDir = (Resolve-Path $GameDir).Path
if (-not (Test-Path (Join-Path $GameDir "SlayTheSpire2.exe"))) {
    throw "SlayTheSpire2.exe not found in $GameDir"
}

# Jimbo's content comes from Balatro itself, so it is made fresh rather than committed.
python (Join-Path $PSScriptRoot "generate_balatro_content.py")
if ($LASTEXITCODE -ne 0) { throw "generating Jimbo's content failed" }
python (Join-Path $PSScriptRoot "build_mod_pck.py")
if ($LASTEXITCODE -ne 0) { throw "building BalatroBridge.pck failed" }

$project = Join-Path $PSScriptRoot "..\sts2-mod\BalatroBridge\BalatroBridge.csproj"
dotnet build $project -c $Configuration --nologo -v minimal "-p:Sts2Dir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "BalatroBridge build failed" }

$out = Join-Path $PSScriptRoot "..\sts2-mod\BalatroBridge\bin\$Configuration\net9.0"
$modDir = Join-Path $GameDir "mods\BalatroBridge"
New-Item -ItemType Directory -Force $modDir | Out-Null
Copy-Item (Join-Path $out "BalatroBridge.dll"), (Join-Path $out "BalatroBridge.json") $modDir -Force
Copy-Item (Join-Path $PSScriptRoot "..\sts2-mod\BalatroBridge\bin\BalatroBridge.pck") $modDir -Force

$config = Join-Path $modDir "BalatroBridge.config.json"
if (-not (Test-Path $config)) {
    $settings = [ordered]@{
        port        = 47291
        balatro_exe = [System.IO.Path]::GetFullPath($BalatroExe)
        auto_launch = $true
        hidden      = $true
    }
    [System.IO.File]::WriteAllText($config, ($settings | ConvertTo-Json))
    Write-Host "Wrote $config"
}
if (-not (Test-Path $BalatroExe)) {
    Write-Warning "No bridged Balatro at $BalatroExe yet; run: python tools\build_balatro.py"
}

Write-Host "Installed BalatroBridge to $modDir"
