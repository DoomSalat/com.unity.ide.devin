# com.unity.ide.devin

Unity Editor integration for [Devin IDE](https://devin.ai).

Registers Devin as an external script editor in Unity — auto-discovery, file opening with line navigation, and self-contained `.csproj` / `.sln` generation for IntelliSense via OmniSharp.

## Installation

Add to your project's `Packages/manifest.json`:

```json
"com.unity.ide.devin": "https://github.com/DoomSalat/com.unity.ide.devin.git"
```

Or as a local package:

```json
"com.unity.ide.devin": "file:/path/to/com.unity.ide.devin"
```

## Requirements

- Unity 2021.3+
- Devin IDE installed at:
  - **Windows:** `%LOCALAPPDATA%\Programs\Devin\Devin.exe`
  - **macOS:** `/Applications/Devin.app`
  - **Linux:** `/usr/bin/devin` or `/usr/local/bin/devin`
- .NET SDK 6.0+ (for OmniSharp project loading — no Visual Studio required)

## Setup

1. Open **Edit → Preferences → External Tools**
2. Set **External Script Editor** to **Devin**
3. Configure which package types to generate `.csproj` for (Embedded, Local, Git, etc.)
4. Click **Regenerate project files**

## Features

- **Auto-detects Devin** installation on Windows, macOS, and Linux
- **Opens files** at the correct line and column via `--goto`
- **Self-contained `.csproj` generation** — SDK-style projects compatible with .NET Core SDK MSBuild (no Visual Studio required)
- **`.sln` generation** scoped to selected package types
- **`omnisharp.json`** auto-generated with the correct solution path
- **`Directory.Build.props`** auto-generated with settings that prevent MSBuild from resolving .NET Framework targeting packs
- **Configurable** per package type: Embedded, Local, Registry, Git, Built-in, Player assemblies

## OmniSharp notes

Generated projects use `<Project Sdk="Microsoft.NET.Sdk">` with `<TargetFramework>net471</TargetFramework>`. Combined with the generated `Directory.Build.props`, this allows OmniSharp to load the solution using only the .NET Core SDK — Visual Studio is not required.

Assembly references not included in `.csproj` generation (e.g. Registry packages when Registry is disabled) are resolved as `HintPath` references pointing to `Library/ScriptAssemblies/`.

## Based on

Forked from [com.unity.ide.windsurf](https://github.com/Asuta/com.unity.ide.windsurf), which is itself based on the Unity VS Code integration pattern.
