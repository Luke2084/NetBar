# NetBar

NetBar is a lightweight Windows desktop network speed overlay. It stays above the taskbar, shows real-time download and upload speed, and stores basic preferences locally.

Current version: `0.0.1`

## Features

- Real-time upload and download speed display
- Transparent, borderless floating window
- Light, dark, or system-following text color
- Optional startup shortcut
- Optional automatic update checks from GitHub Releases
- Left-button horizontal drag with saved position
- Right-click context menu for common settings

## Requirements

- Windows 10 or later
- .NET 10 SDK for development

## Build

```powershell
dotnet restore NetBar/NetBar.csproj
dotnet build NetBar/NetBar.csproj -c Release
```

## Publish a Windows executable

```powershell
dotnet publish NetBar/NetBar.csproj -c Release -p:PublishProfile=win-x64
```

The packaged files are written to:

```text
NetBar/bin/Release/net10.0-windows/win-x64/publish/
```

## GitHub Actions package

The `Build Windows EXE` workflow builds a self-contained `win-x64` package and uploads `NetBar-win-x64.zip` as a workflow artifact.

Run it from the **Actions** tab with **workflow_dispatch**, or push to `master`. Pushing a tag like `v1.0.0` also attaches the zip file to the GitHub Release.

## Updates

NetBar checks `https://github.com/Luke2084/NetBar/releases/latest` when automatic updates are enabled. If the latest release tag is newer than the running app version and includes `NetBar-win-x64.zip`, NetBar downloads it, closes, replaces the installed files, and starts again.

Use semantic version tags such as `v0.0.2`, `v0.1.0`, or `v1.0.0` for releases.

## Settings

User settings are stored under:

```text
%LOCALAPPDATA%\NetBar\config.json
```
