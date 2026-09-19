# IntelliType Elite

A lightweight tray app that restores the extra buttons and scroll wheel functionality on a Microsoft
Wireless Desktop Elite keyboard.

## Why

Microsoft dropped support for this keyboard long ago in `IntelliType Pro 8.2`; `Microsoft Mouse & Keyboard Center` doesn't support it officially—only the buttons work, the scroll wheel doesn't—and it frequently BSOD's in `dc3d.sys` driver. This app reads the keyboard's HID reports directly and maps them to actions of your choosing: no dependencies, no MMKC, no crashing driver.

## Requirements

- Windows
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installing

Download `IntelliTypeElite.exe` and `config.json` from the [latest release](https://github.com/rahulmangaldas/IntelliTypeElite/releases/latest) into the same folder and run the exe.

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
