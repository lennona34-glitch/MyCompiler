[CmdletBinding()]
param()

$ErrorActionPreference = 'SilentlyContinue'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathEntries = @(($machinePath + ';' + $userPath) -split ';' |
    ForEach-Object { [Environment]::ExpandEnvironmentVariables($_.Trim().Trim('"')) } |
    Where-Object { $_ })
$pathExtensionValue = [Environment]::GetEnvironmentVariable('PATHEXT', 'Machine')
if (-not $pathExtensionValue) { $pathExtensionValue = '.EXE;.CMD;.BAT' }
$pathExtensions = @($pathExtensionValue -split ';')

function Find-OnPersistentPath {
    param([string[]]$Names)
    $matches = New-Object 'System.Collections.Generic.List[string]'
    foreach ($name in $Names) {
        if ([IO.Path]::IsPathRooted($name)) {
            if (Test-Path -LiteralPath $name -PathType Leaf) { $matches.Add($name) }
            continue
        }
        foreach ($folder in $pathEntries) {
            if (-not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
            $direct = Join-Path $folder $name
            if ([IO.Path]::HasExtension($name)) {
                if (Test-Path -LiteralPath $direct -PathType Leaf) { $matches.Add($direct) }
                continue
            }
            foreach ($extension in $pathExtensions) {
                $candidate = $direct + $extension.ToLowerInvariant()
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $matches.Add($candidate) }
            }
            if (Test-Path -LiteralPath $direct -PathType Leaf) { $matches.Add($direct) }
        }
    }
    return @($matches | Select-Object -Unique)
}

$catalog = @(
    [pscustomobject]@{ Name='.NET'; Names=@('dotnet'); Install='winget install --id Microsoft.DotNet.SDK.9 -e' },
    [pscustomobject]@{ Name='C / C++'; Names=@('clang','gcc','cl'); Install='winget install --id MSYS2.MSYS2 -e' },
    [pscustomobject]@{ Name='Rust'; Names=@('cargo','rustc'); Install='winget install --id Rustlang.Rustup -e' },
    [pscustomobject]@{ Name='Go'; Names=@('go'); Install='winget install --id GoLang.Go -e' },
    [pscustomobject]@{ Name='Java'; Names=@('javac'); Install='winget install --id EclipseAdoptium.Temurin.21.JDK -e' },
    [pscustomobject]@{ Name='Kotlin'; Names=@('kotlinc'); Install='https://kotlinlang.org/docs/command-line.html' },
    [pscustomobject]@{ Name='Scala'; Names=@('scalac'); Install='winget install --id Scala.Scala.2 -e' },
    [pscustomobject]@{ Name='Swift'; Names=@('swiftc'); Install='winget install --id Swift.Toolchain -e' },
    [pscustomobject]@{ Name='Zig'; Names=@('zig'); Install='winget install --id zig.zig -e' },
    [pscustomobject]@{ Name='Dart'; Names=@('dart'); Install='winget install --id Google.DartSDK -e' },
    [pscustomobject]@{ Name='Pascal'; Names=@('fpc'); Install='winget install --id FreePascal.FreePascalCompiler -e' },
    [pscustomobject]@{ Name='Nim'; Names=@('nim'); Install='https://nim-lang.org/install_windows.html' },
    [pscustomobject]@{ Name='D'; Names=@('dmd','ldc2'); Install='winget install --id Dlang.DMD -e' },
    [pscustomobject]@{ Name='Haskell'; Names=@('ghc'); Install='https://www.haskell.org/ghcup/install/' },
    [pscustomobject]@{ Name='OCaml'; Names=@('ocamlopt'); Install='winget install --id Diskuv.OCaml -e' },
    [pscustomobject]@{ Name='TypeScript'; Names=@('tsc','npx'); Install='npm install --global typescript' },
    [pscustomobject]@{ Name='JavaScript'; Names=@('node'); Install='winget install --id OpenJS.NodeJS.LTS -e' },
    [pscustomobject]@{ Name='Python'; Names=@('py','python'); Install='winget install --id Python.Python.3.14 -e' },
    [pscustomobject]@{ Name='Ruby'; Names=@('ruby'); Install='winget install --id RubyInstallerTeam.RubyWithDevKit.3.4 -e' },
    [pscustomobject]@{ Name='PHP'; Names=@('php'); Install='winget install --id PHP.PHP.8.4 -e' },
    [pscustomobject]@{ Name='PowerShell'; Names=@('pwsh','powershell'); Install='winget install --id Microsoft.PowerShell -e' },
    [pscustomobject]@{ Name='Shell'; Names=@('C:\msys64\ucrt64\bin\bash.exe','C:\Program Files\Git\bin\bash.exe','bash'); Install='winget install --id Git.Git -e' },
    [pscustomobject]@{ Name='Lua'; Names=@('luac','lua'); Install='winget install --id DEVCOM.Lua -e' },
    [pscustomobject]@{ Name='R'; Names=@('Rscript'); Install='winget install --id RProject.R -e' },
    [pscustomobject]@{ Name='Elixir'; Names=@('elixir','elixirc'); Install='https://elixir-lang.org/install.html#windows' }
)

Write-Host ''
Write-Host 'MyCompiler PATH Test (read only)' -ForegroundColor Cyan
Write-Host 'Nothing will be installed, uninstalled, or changed.' -ForegroundColor DarkGray
Write-Host ''

$invalid = @($pathEntries | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Container) } | Select-Object -Unique)
$duplicates = @($pathEntries |
    Group-Object { $_.TrimEnd('\') } |
    Where-Object Count -gt 1 |
    ForEach-Object Name)

Write-Host "Persistent PATH folders: $($pathEntries.Count)" -ForegroundColor White
if ($invalid.Count -eq 0) { Write-Host '  [OK] No missing PATH folders.' -ForegroundColor Green }
else { foreach ($entry in $invalid) { Write-Host "  [BAD] Missing folder: $entry" -ForegroundColor Yellow } }
if ($duplicates.Count -eq 0) { Write-Host '  [OK] No duplicate PATH folders.' -ForegroundColor Green }
else { foreach ($entry in $duplicates) { Write-Host "  [DUP] $entry" -ForegroundColor Yellow } }

Write-Host ''
Write-Host 'Toolchains' -ForegroundColor White
$ready = 0
foreach ($tool in $catalog) {
    $matches = @(Find-OnPersistentPath -Names $tool.Names)
    if ($matches.Count -gt 0) {
        $ready++
        Write-Host ("  [OK]  {0,-18} {1}" -f $tool.Name, $matches[0]) -ForegroundColor Green
        if ($matches.Count -gt 1) {
            foreach ($extra in $matches | Select-Object -Skip 1) {
                Write-Host ("        alternate: {0}" -f $extra) -ForegroundColor DarkYellow
            }
        }
    } else {
        Write-Host ("  [---] {0,-18} not found" -f $tool.Name) -ForegroundColor DarkGray
        Write-Host ("        setup: {0}" -f $tool.Install) -ForegroundColor Gray
    }
}

$python = @(Find-OnPersistentPath -Names @('py','python') | Select-Object -First 1)
$pyInstallerReady = $false
if ($python.Count -gt 0) {
    $pythonArgs = @()
    if ([IO.Path]::GetFileNameWithoutExtension($python[0]) -ieq 'py') { $pythonArgs += '-3' }
    $version = & $python[0] @pythonArgs -m PyInstaller --version 2>$null
    $pyInstallerReady = $LASTEXITCODE -eq 0
}
if ($pyInstallerReady) {
    $ready++
    Write-Host ("  [OK]  {0,-18} PyInstaller $version" -f 'Python -> EXE') -ForegroundColor Green
} else {
    Write-Host ("  [---] {0,-18} not found" -f 'Python -> EXE') -ForegroundColor DarkGray
    Write-Host '        setup: py -3 -m pip install pyinstaller' -ForegroundColor Gray
}

Write-Host ''
Write-Host "Ready: $ready / $($catalog.Count + 1)" -ForegroundColor Cyan
Write-Host 'Blank does not necessarily indicate a fault; it usually means that language is not installed.' -ForegroundColor DarkGray
Write-Host 'After installing or repairing PATH, close and reopen MyCompiler.' -ForegroundColor DarkGray
Write-Host ''
