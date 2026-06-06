# com.unity.ide.devin

Unity Editor integration for [Devin IDE](https://devin.ai).

Adds Devin as an external script editor option in Unity — auto-discovery, file opening with line navigation, and `.csproj` generation for IntelliSense.

## Installation

Add to your project's `Packages/manifest.json`:

```json
"com.unity.ide.devin": "https://github.com/DoomSalat/com.unity.ide.devin.git"
```

## Setup

1. Open **Edit → Preferences → External Tools**
2. Set **External Script Editor** to **Devin**
3. Click **Regenerate project files**

## Requirements

- Unity 2021.3+
- Devin IDE installed at `%LOCALAPPDATA%\Programs\Devin\Devin.exe` (Windows) or `/Applications/Devin.app` (macOS)
- `com.unity.ide.visualstudio` package (used for project file generation on Unity 6+)

## Features

- Auto-detects Devin installation on Windows, macOS, and Linux
- Opens scripts at the correct line and column (`--goto`)
- Opens the Unity project as a workspace automatically
- Configurable `.csproj` generation per package type (Embedded, Local, Registry, Git, Built-in, Player)

## Based on

Forked from [com.unity.ide.windsurf](https://github.com/Asuta/com.unity.ide.windsurf) which is itself based on the Unity VS Code integration pattern.
