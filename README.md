# IntelliType Elite

A lightweight tray app that restores the extra buttons and scroll wheel functionality on a Microsoft
Wireless Desktop Elite keyboard.

## Why

Microsoft dropped support for this keyboard long ago in `IntelliType Pro 8.2`; `Microsoft Mouse & Keyboard Center` doesn't support it officially—only the buttons work, the scroll wheel doesn't—and it frequently BSOD's in `dc3d.sys` driver. This app reads the keyboard's HID reports directly and maps them to actions of your choosing: no dependencies, no MMKC, no crashing driver.

## Requirements

- Windows
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installing

Download `IntelliTypeElite.zip` from the [latest release](https://github.com/rahulmangaldas/IntelliTypeElite/releases/latest), extract it anywhere, and run `IntelliTypeElite.exe`; on first run it copies `config.json` to `%USERPROFILE%\.config\IntelliTypeElite\config.json`, which is the copy you actually edit afterward.

## Configuring

Edit `config.json`: changes hot-reload, no restart needed. See `config.schema.json` for the full shape (editors like VS Code will give you autocomplete). Each button supports:

- A default action: `None`, `Keystroke`, `Launch`, or `CloseWindow`
- `appOverrides` — different behavior per foreground app
- `modifierOverrides` — different behavior when Shift/Ctrl/Alt is held

## Logging

Run with `/log` to enable a log file and "Open log" in the app menu.

## Building from source

```
dotnet build
```
