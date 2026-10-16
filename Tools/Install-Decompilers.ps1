<#
.SYNOPSIS
    Installs the decompiler toolchain used by MyCompiler's Decompile feature.
.DESCRIPTION
    Everything is installed per-user under %LOCALAPPDATA%\MyCompiler\Decompilers
    (ilspycmd goes to the standard dotnet global tools folder). Nothing touches PATH.
    Re-running the script updates each tool to the latest release.
.PARAMETER Tool
    all (default), ilspy, jadx, apktool, ghidra, pyinstxtractor, pycdc
#>
param(
    [ValidateSet('all', 'ilspy', 'jadx', 'apktool', 'ghidra', 'pyinstxtractor', 'pycdc')]
    [string]$Tool = 'all'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Join-Path $env:LOCALAPPDATA 'MyCompiler\Decompilers'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$headers = @{ 'User-Agent' = 'MyCompiler-Decompiler-Setup' }

function Get-LatestAsset([string]$repo, [string]$pattern) {
    $release = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$repo/releases/latest"
    $asset = $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
    if (-not $asset) { throw "No asset matching '$pattern' in $repo $($release.tag_name)" }
    return $asset
}

function Install-Zip([string]$name, [string]$repo, [string]$pattern) {
    $asset = Get-LatestAsset $repo $pattern
    Write-Host "-> $name  $($asset.name)"
    $zip = Join-Path $env:TEMP $asset.name
    Invoke-WebRequest -Headers $headers -Uri $asset.browser_download_url -OutFile $zip
    $target = Join-Path $root $name
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Expand-Archive -Path $zip -DestinationPath $target -Force
    Remove-Item $zip -Force
    Write-Host "  installed to $target"
}

function Install-Ilspy {
    Write-Host '-> ilspycmd (.NET decompiler)'
    $list = dotnet tool list --global 2>$null | Out-String
    if ($list -match 'ilspycmd') { dotnet tool update --global ilspycmd --version 9.* } else { dotnet tool install --global ilspycmd --version 9.* }
}

function Install-Jadx { Install-Zip 'jadx' 'skylot/jadx' '^jadx-[\d\.]+\.zip$' }

function Install-Apktool {
    $asset = Get-LatestAsset 'iBotPeaches/Apktool' '^apktool_[\d\.]+\.jar$'
    Write-Host "-> apktool  $($asset.name)"
    $target = Join-Path $root 'apktool'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Invoke-WebRequest -Headers $headers -Uri $asset.browser_download_url -OutFile (Join-Path $target 'apktool.jar')
    Write-Host "  installed to $target"
}

function Install-Ghidra {
    if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
        Write-Host '  Java not found - installing Temurin 21 JDK via winget'
        winget install --id EclipseAdoptium.Temurin.21.JDK -e --accept-source-agreements --accept-package-agreements
    }
    Install-Zip 'ghidra' 'NationalSecurityAgency/ghidra' '^ghidra_.*_PUBLIC_.*\.zip$'
}

function Install-PyInstxtractor {
    Write-Host '-> pyinstxtractor (PyInstaller archive extractor)'
    $target = Join-Path $root 'pyinstxtractor'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Invoke-WebRequest -Headers $headers -OutFile (Join-Path $target 'pyinstxtractor.py') `
        -Uri 'https://raw.githubusercontent.com/extremecoders-re/pyinstxtractor/master/pyinstxtractor.py'
    Write-Host "  installed to $target"
}

function Install-Pycdc {
    Write-Host '-> pycdc (Python bytecode decompiler, built from source)'
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue) -or -not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Host '  skipped: needs git + cmake + a C++ compiler. .pyc files will still be extracted.'
        return
    }
    $src = Join-Path $env:TEMP 'pycdc-src'
    if (Test-Path $src) { Remove-Item -Recurse -Force $src }
    git clone --depth 1 https://github.com/zrax/pycdc.git $src
    cmake -S $src -B "$src\build" -DCMAKE_BUILD_TYPE=Release
    cmake --build "$src\build" --config Release
    $exe = Get-ChildItem "$src\build" -Recurse -Filter pycdc.exe | Select-Object -First 1
    if (-not $exe) { Write-Host '  build did not produce pycdc.exe'; return }
    $target = Join-Path $root 'pycdc'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item $exe.FullName $target -Force
    Write-Host "  installed to $target"
}

$steps = [ordered]@{
    ilspy          = { Install-Ilspy }
    jadx           = { Install-Jadx }
    apktool        = { Install-Apktool }
    ghidra         = { Install-Ghidra }
    pyinstxtractor = { Install-PyInstxtractor }
    pycdc          = { Install-Pycdc }
}

foreach ($key in $steps.Keys) {
    if ($Tool -ne 'all' -and $Tool -ne $key) { continue }
    try { & $steps[$key] } catch { Write-Warning "$key failed: $($_.Exception.Message)" }
}
Write-Host ''
Write-Host 'Done. Press refresh in MyCompiler''s Toolchains panel to refresh.'
