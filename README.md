# MyCompiler

MyCompiler is a native Windows compiler workbench with an editor, workspace explorer, live toolchain detection, integrated build output, build/run cancellation, and an original art background.

Version 1.1 adds reliable standalone `.py` / `.pyw` to `.exe` packaging and improves concurrent compiler-output capture.

Version 1.2 adds a read-only PATH Doctor plus visible, copyable setup/update instructions on every Toolchains card. The `Tools` folder also contains a standalone persistent-PATH audit.

Version 1.3 adds **Decompile**: recover readable code from your own `.exe`, `.dll`, `.apk` and `.jar` files when the source is lost.

Version 1.4+ adds **⚡ GitHub Hub** and **⛶ Full Screen Mode**:
- **⚡ GitHub Instant Fetch, Build & Run**: Browse **GitHub Trending** (`Today` / `Week` / `Month`), curated category feeds (**`★ Windows Apps`**, **`🎮 Decomp / Recomp`** game ports, **`🤖 Antigravity / MCP`** skills & servers), live repository search, or paste/drop any GitHub repository, single source file (`/blob/`), or Gist URL for zero-command automated download, smart workspace classification, prebuilt Windows Release binary fast-path, build, and execution.
- **⛶ Full Screen Mode**: Click **`⛶`** in the title bar or press `F11` (and `Esc` to exit) for a distraction-free borderless workbench.

## Start

Run `MyCompiler.exe`. Open or drop a source file/folder (or click **⚡ GitHub**), then use:

- `F6` — build or validate
- `F5` — build and run
- `F11` — toggle Full Screen mode
- `Esc` — close modal / stop an active process / exit Full Screen
- `Ctrl+S` — save
- `Ctrl+F` — find in the current file

The executable is self-contained; Windows does not need a separate .NET installation to launch it.

## PATH Doctor

Choose **PATH** above the Toolchains list to print a read-only report in Build Output. It checks invalid and duplicate PATH folders and shows the exact executable MyCompiler resolves for each available toolchain. Each card now displays a setup or update instruction with a copy button. These commands are intended for PowerShell; MyCompiler never installs or uninstalls toolchains automatically.

You can also double-click `Tools\Run-PATH-Test.cmd` for the same kind of persistent machine/user PATH check outside MyCompiler. A program being absent from PATH is not, by itself, a reason to uninstall it. Repair PATH first when the installation is otherwise healthy.

## Decompile

Choose **⇲ Decompile** or drop an `.exe`, `.dll`, `.apk`, `.aab`, `.dex` or `.jar` onto the window. MyCompiler detects the binary type, runs the matching tool, and opens the result as a workspace. Output goes to `%LOCALAPPDATA%\MyCompiler\Decompiled\<name>_<timestamp>`.

| Binary | Tool | You get |
|---|---|---|
| .NET assembly, single-file app, or apphost + `.dll` | ilspycmd | C# project per app assembly (built-in single-file bundle extractor; framework DLLs skipped) |
| PyInstaller `.exe` | pyinstxtractor + pycdc | `.py` source; falls back to a `dis` bytecode listing when pycdc doesn't support the Python version yet |
| `.apk` / `.aab` / `.dex` / `.jar` | jadx | Java sources and decoded resources/manifest |
| Native C/C++/Rust/Go `.exe`/`.dll` | Ghidra headless | One approximate `.c` file (not original source; analysis can take minutes) |

Install or update the tools with `Tools\Install-Decompilers.ps1` (per-user, no PATH changes, needs Java 21 for jadx/Ghidra). Each tool has a readiness card in Toolchains with a copyable setup command. Only decompile software you own or are licensed to inspect.

## Supported recipes

MyCompiler recognizes .NET solutions/projects, Cargo, Go modules, npm/TypeScript packages, Maven, Gradle, CMake, and Make projects. Single-file recipes cover C#, F#, Visual Basic, C, C++, Rust, Go, Java, Kotlin, Scala, Swift, Zig, Dart, Pascal, Nim, D, Haskell, OCaml, TypeScript, JavaScript, Python, Ruby, PHP, PowerShell, Bash, Lua, R, and Elixir.

### Python → EXE

Open a `.py` or `.pyw` file and choose **Build** or **Build & run**. MyCompiler packages it as a standalone Windows executable with PyInstaller and writes the result to `dist\script-name.exe` beside the source file. `.pyw` files are packaged without a console window. The Toolchains panel includes a separate **Python → EXE** readiness check. If it is unavailable, install the packager once with `py -3 -m pip install pyinstaller` and refresh Toolchains.

MyCompiler orchestrates the real compiler or interpreter for each language. Install the toolchains you use and ensure their executable is on `PATH`; the Toolchains panel shows what is available. For uncommon build systems, enter an executable and arguments in the custom command lane at the bottom. Custom commands run directly without an interactive shell, except `.cmd`/`.bat` launchers required by Windows tools such as npm.

## Build from source

Requirements: Windows 10/11 and .NET SDK 9.

```powershell
dotnet build MyCompiler.csproj -c Release
dotnet run --project MyCompiler.csproj
```

Build artifacts for compiled single files are cached under `%LOCALAPPDATA%\MyCompiler\BuildCache` so source folders stay clean.

## Notes

- Installed language toolchains are not bundled with MyCompiler.
- Running build scripts or programs executes code from the opened workspace; use trusted source code.
- Some project types do not have an unambiguous default executable. MyCompiler builds them and reports that a run recipe is not defined.
- The original background artwork is bundled as `Assets/compiler-art.png`.
