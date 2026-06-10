# com.unity.ide.devin

Unity Editor integration for [Devin IDE](https://devin.ai).

Registers Devin as an external script editor in Unity — auto-discovery, file opening with line navigation, and `.sln`/`.slnx` + `.csproj` generation for IntelliSense via OmniSharp.

## Installation

Add to your project's `Packages/manifest.json`:

```json
"com.unity.ide.devin": "https://github.com/DoomSalat/com.unity.ide.devin.git#via-reflection"
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
- One of the following delegate editor plugins installed in the project:
  - `com.unity.ide.visualstudio` (Visual Studio Tools)
  - `com.unity.ide.cursor` (Cursor)
  - `com.unity.ide.rider` (Rider)
  - or any VS Code-based Unity plugin

## Setup

1. Open **Edit → Preferences → External Tools**
2. Set **External Script Editor** to **Devin**
3. In the Devin section, select the **delegate editor** (used for `.csproj`/`.sln` generation)
4. Click **Regenerate project files**

## How it works

Devin cannot generate `.csproj`/`.sln` files on its own — it delegates that to another installed IDE plugin (Visual Studio Tools, Cursor, Rider, etc.). This approach avoids duplicating complex project generation logic.

**The current-editor bypass:** every VS-family plugin (VS Tools, Cursor, Windsurf) guards its `SyncAll()` behind a check that the plugin itself is the current editor. Since Devin is selected, those guards fail. This plugin bypasses them by calling `ProjectGenerator.Sync()` directly via reflection:

1. **`_discoverInstallations` fast path** — reads the delegate plugin's own async discovery result and calls `Sync()` on the discovered installation's generator.
2. **`GeneratorFactory` path** (VS Tools only) — reads `GeneratorFactory._legacyStyleProjectGeneration` directly. Produces `.sln` regardless of VS version (avoids `.slnx` which OmniSharp 1.39 doesn't support).
3. **Static field scan** — scans all types in the delegate assembly for a static `IGenerator` field. Catches VS Code-based plugins (Cursor, Windsurf) that store a `_generator` field on their installation class.
4. **`SyncAll()` direct fallback** — used for editors outside the VS family (e.g. Rider).

After sync, `omnisharp.json` is written with the path to the generated solution file (`.sln` or `.slnx`), and `Directory.Build.props` is written to prevent MSBuild from resolving .NET Framework targeting packs.

## Features

- **Auto-detects Devin** installation on Windows, macOS, and Linux
- **Opens files** at the correct line and column via `--goto`
- **Solution generation** delegated to the installed IDE plugin — supports `.sln` and `.slnx`
- **`omnisharp.json`** auto-generated pointing to the actual generated solution file
- **`Directory.Build.props`** auto-generated for .NET Core SDK MSBuild compatibility
- **Works without the IDE installed** — project files are generated via reflection even when Devin, Cursor, or VS is not installed on the current machine

## Delegate editor priority

When no delegate is manually selected, the plugin auto-selects in this order: **Rider → VS Code family → Visual Studio**.

The selected delegate is shown in **Edit → Preferences → External Tools** under the Devin section.

## Based on

Forked from [com.unity.ide.windsurf](https://github.com/Asuta/com.unity.ide.windsurf), which is itself based on the Unity VS Code integration pattern.
