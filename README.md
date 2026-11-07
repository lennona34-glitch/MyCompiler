# MyCompiler 🏛️

> **The Universal Native Windows Compiler Workbench & AI Refactor Studio**  
> *Zero-configuration development, GitHub instant fetch & run, Gemini AI code transformation, Antigravity skill detection, and multi-engine binary decompilation.*

![MyCompiler Icon](Assets/mycompiler.png)

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue?style=flat-square&logo=windows)](https://github.com/lennona34-glitch/MyCompiler)
[![Runtime](https://img.shields.io/badge/.NET-9.0%20WPF%20(Self--Contained)-512BD4?style=flat-square&logo=dotnet)](https://github.com/lennona34-glitch/MyCompiler)
[![AI Powered](https://img.shields.io/badge/AI%20Studio-Google%20Gemini%20%2F%20OpenAI-4E88FF?style=flat-square&logo=google)](https://aistudio.google.com/)
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)

---

## ✨ What is MyCompiler?

**MyCompiler** transforms Windows into an effortless developer playground. Whether you're hacking on a single-file C# / Python script, exploring trending open-source projects from GitHub, de-obfuscating binary releases, or using artificial intelligence to modernize and re-theme legacy codebases, MyCompiler handles the toolchains, builds, execution, and git workflow without terminal friction.

---

## 🚀 Key Highlights in Version 1.4

### 1. ⚡ MyCompiler AI Studio (Powered by Google Gemini)
Never get stuck on compiler errors, missing dependencies, or outdated code again:
- **Free Google Gemini Integration**: Sub-second streaming using Google's active `gemini-3.5-flash` and `gemini-3.5-flash-lite` models. Click **"Get Free Gemini Key ↗"** to generate an API key in seconds directly from Google AI Studio.
- **One-Click Quick Actions**:
  - `⚡ Refactor Code`: Modernize architecture, adopt idioms, optimize speed, and clean types.
  - `💙 Make Neon Blue`: Transform dull or warm/orange UI accent themes into radiant cyan and neon blue glassmorphism.
  - `🌙 Give Dark Mode`: Convert any UI codebase into sleek obsidian/navy dark mode.
  - `🔤 Modern Font`: Update typography to modern developer typefaces (*Cascadia Code*, *Segoe UI Variable*, *JetBrains Mono*).
  - `🐛 Fix Build Error`: Automatically captures the latest compilation diagnostic from the output console and prompts the AI to resolve it.
  - `📖 Explain Code`: Deconstructs complex algorithms, decompiled C/Java outputs, or entire modules in crystal-clear English.
  - `🔀 Git Fork & Push`: Generates conventional commit messages (`feat: ...`, `refactor: ...`) and commits & pushes your refactored code to GitHub.

#### What to do after clicking "⚡ Apply to Code"?
Once you click **"⚡ Apply to Code"**, the AI's proposed solution is immediately written into your active editor buffer! MyCompiler then presents 3 immediate actions right at your fingertips:
1. **`✕ Close & Review in Editor`**: Closes the AI Studio overlay so you can view, edit, and explore the freshly injected code with syntax highlighting.
2. **`▶ Build & Run (F5)`**: Closes AI Studio and immediately triggers compilation and execution. Test your AI changes in real time!
3. **`🔀 Commit to Git`**: Opens the Git Fork & Commit panel to commit your refactored code with an AI-drafted commit message and push it to your GitHub fork.
*(Made an accidental change? Just click **`↩ Undo Edit`** to instantly restore your original code).*

---

### 2. 🌐 GitHub Instant Fetch, Build & Run
- **Zero Command-Line Repo Exploration**: Paste any GitHub repository URL (`github.com/owner/repo`), file blob (`/blob/main/program.cs`), or Gist to instantly download, categorize, build, and run.
- **Trending & Search Feeds**:
  - Filter by `Today`, `Week`, `Month`.
  - Explore curated channels: **`★ Windows Apps`**, **`⚡ Recomp`** & **`🎮 Decomp`** game ports, and **`🤖 Antigravity / MCP`** skills & tools.
- **`✨ AI Refactor` on Every Card**: See an open-source project you want to enhance? Click `✨ AI Refactor` right on the search card. MyCompiler clones the repository, loads the main entry point, and opens AI Studio with a tailored refactoring prompt ready to go.
- **Prebuilt Windows Release Fast-Path**: If a repository includes prebuilt `.exe` releases inside zips, MyCompiler detects and extracts them automatically.

---

### 3. 🤖 Antigravity Agent Skills Auto-Detection & Installer
- Automatically scans repositories for `SKILL.md` or `skills.md` files.
- Built-in terminal command: type `install` in the bottom custom command bar to automatically register the discovered skills into your global agent environment (`~/.gemini/config/skills/`).

---

### 4. ⇲ Multi-Engine Decompiler Suite
Recover readable source code from existing binaries with built-in decompiler bridges:

| Binary Type | Engine | Output Produced |
|---|---|---|
| **.NET Executable / Assembly** (`.exe`, `.dll`) | `ilspycmd` | Clean C# project per assembly (with single-file bundle extraction) |
| **Android Package / Archive** (`.apk`, `.jar`, `.dex`) | `jadx` | Java source code, decoded layouts, and `AndroidManifest.xml` |
| **Python Standalone Binary** (`PyInstaller .exe`) | `pyinstxtractor` + `pycdc` | Python (`.py`) scripts and decompiled bytecode |
| **Native C / C++ / Rust / Go Binary** (`.exe`, `.dll`) | `Ghidra Headless` | Approximate C source via automated Ghidra decompiler script |

*Run `Tools\Install-Decompilers.ps1` to configure and update all decompiler engines in one step.*

---

### 5. 🏛️ New Visual Identity: White Marble on Obsidian Black
MyCompiler features a brand new sculptural app icon: an **"M" crafted from Italian white Carrara marble** with organic slate veins, chiseled bevels, specular lighting, and a soft ambient drop shadow resting on a sleek obsidian black pedestal.

---

## ⌨️ Essential Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| **`F5`** | **Build & Run** (Compiles code and runs executable immediately) |
| **`F6`** | **Build Only** (Compiles/validates without executing) |
| **`F11`** | **Toggle Full Screen Mode** (Distraction-free borderless workbench) |
| **`Ctrl + Enter`** | **Generate with AI** (Inside AI Studio) |
| **`Ctrl + S`** | **Save Active File** |
| **`Ctrl + F`** | **Find in File** |
| **`Esc`** | **Close Overlay / Stop Process / Exit Full Screen** |

---

## 🛠️ Supported Languages & Toolchains

MyCompiler orchestrates your system's native toolchains with intelligent project recipes:
- **.NET Solutions & Files**: C#, F#, Visual Basic (`dotnet`, `csc`, `fsc`)
- **Systems Languages**: C, C++, Rust (Cargo), Go, Zig, Swift, D, Nim
- **JVM Languages**: Java, Kotlin, Scala, Groovy (`javac`, `mvn`, `gradle`)
- **Scripting & Dynamic**: Python, Ruby, PHP, PowerShell, Bash, Lua, R, Elixir
- **Web & Modern Stacks**: TypeScript, JavaScript (`npm`, `pnpm`, `bun`, `deno`)
- **Python to Standalone EXE**: Packages any `.py` or `.pyw` into a portable Windows executable using PyInstaller in 1 click.

---

## 📦 Building from Source

### Requirements
- **Windows 10 / 11** (x64)
- **.NET 9.0 SDK**

### Build & Run
```powershell
# Clone the repository
git clone https://github.com/lennona34-glitch/MyCompiler.git
cd MyCompiler

# Build Release
dotnet build MyCompiler.csproj -c Release

# Publish Single-File Binary
dotnet publish MyCompiler.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o bin\Publish
```

The resulting standalone executable will be generated at `bin\Publish\MyCompiler.exe`.

---

## ⚖️ License & Attribution

- Released under the [MIT License](LICENSE).
- Powered by Google Gemini AI & the open-source community.
