# LightShot Translate - publish script
# Usage:
#   .\publish.ps1                 framework-dependent (small; the target PC needs the .NET 8 Desktop Runtime)
#   .\publish.ps1 -SelfContained  bundles the .NET runtime and the VC++ runtime DLLs: the target PC needs NOTHING installed
param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
$sc = if ($SelfContained) { "true" } else { "false" }

# Do NOT enable PublishTrimmed (WinForms relies on reflection)
# Do NOT use PublishSingleFile (the ONNX Runtime native dll must stay on disk)
function Invoke-Publish([string]$r2r) {
    & dotnet publish -c Release -r win-x64 --self-contained $sc -o publish "-p:PublishReadyToRun=$r2r"
    return ($LASTEXITCODE -eq 0)
}

# ReadyToRun = precompiled code: less JIT work, lower memory, faster startup. Needs the runtime pack from NuGet (internet).
if (-not (Invoke-Publish "true")) {
    Write-Warning "ReadyToRun publish failed (offline?). Retrying without ReadyToRun..."
    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    if (-not (Invoke-Publish "false")) { throw "dotnet publish failed" }
}

# ---- VC++ runtime, app-local (ONNX Runtime needs it; a clean PC may not have it) ----
# Source priority: 1) Visual Studio's redistributable folder (VC\Redist\MSVC\<ver>\x64\Microsoft.VC14*.CRT, the documented
# app-local source)  2) this machine's System32 (the installed VC++ 2015-2022 x64 redistributable).
# The chosen files, their versions and SHA256 are written to publish\redist-manifest.txt so every package is traceable.
$crtFiles = @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll")
$minCrt = [version]"14.29"     # VS2019 16.10+ ; recommended: the latest 14.4x

function Find-CrtSourceDir {
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    $cands = foreach ($r in $roots) {
        Get-ChildItem -Path (Join-Path $r "Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC14*.CRT") -Directory -ErrorAction SilentlyContinue
    }
    $best = $cands | Where-Object { Test-Path (Join-Path $_.FullName "vcruntime140_1.dll") } |
        Sort-Object { $v = (Get-Item (Join-Path $_.FullName "msvcp140.dll")).VersionInfo; [version]("{0}.{1}.{2}.{3}" -f $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart, $v.FilePrivatePart) } -Descending |
        Select-Object -First 1
    if ($best) { return $best.FullName }
    if ([Environment]::Is64BitProcess) { return (Join-Path $env:WINDIR "System32") }
    return (Join-Path $env:WINDIR "Sysnative")
}

$crtSrc = Find-CrtSourceDir
$manifest = @("LightShot Translate - bundled VC++ runtime (app-local)", "Source: $crtSrc", "Built: $(Get-Date -Format s)", "")
$missing = @()
foreach ($n in $crtFiles) {
    $src = Join-Path $crtSrc $n
    if (-not (Test-Path $src)) { $missing += $n; continue }
    $dst = Join-Path publish $n
    Copy-Item $src $dst -Force
    $ver = (Get-Item $dst).VersionInfo.FileVersion
    $sha = (Get-FileHash $dst -Algorithm SHA256).Hash
    $manifest += "{0,-20} {1,-16} {2}" -f $n, $ver, $sha
    try { if ([version]($ver -split " ")[0] -lt $minCrt) { Write-Warning "$n version $ver is older than $minCrt - update the VC++ 2015-2022 x64 redistributable and publish again." } } catch { }
}
$manifest | Set-Content (Join-Path publish "redist-manifest.txt") -Encoding UTF8
if ($missing.Count -gt 0) {
    Write-Warning ("Not found in ${crtSrc}: " + ($missing -join ", "))
    Write-Warning "Install the VC++ 2015-2022 x64 redistributable on this machine (https://aka.ms/vs/17/release/vc_redist.x64.exe) and publish again,"
    Write-Warning "otherwise the package will NOT run on PCs that lack it."
}
Write-Host ("VC++ runtime source: " + $crtSrc)

# ---- sanity checks ----
foreach ($f in @("LightShotTranslate.exe", "lst-ocr.exe", "onnxruntime.dll", "models\ch_PP-OCRv4_det_infer.onnx", "models\ch_PP-OCRv4_rec_infer.onnx", "models\ppocr_keys_v4.txt")) {
    if (-not (Test-Path (Join-Path publish $f))) { throw "Missing in publish folder: $f" }
}
if ($SelfContained -and -not (Test-Path (Join-Path publish "coreclr.dll"))) { throw "Self-contained publish is missing coreclr.dll" }

$size = [math]::Round(((Get-ChildItem publish -Recurse | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host ""
Write-Host "Done. Output folder: $PSScriptRoot\publish  ($size MB)"
Write-Host "Next: build_installer.bat builds the installer, or zip the publish folder yourself."
