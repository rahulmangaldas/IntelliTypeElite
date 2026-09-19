# IntelliType Elite

A lightweight tray app that restores the extra buttons and scroll wheel on a Microsoft
Wireless Desktop Elite keyboard, without needing Microsoft Mouse and Keyboard Center.

## Why

MMKC's `dc3d.sys` driver caused repeated BSODs on this keyboard's volume keys. This app
reads the keyboard's HID reports directly and maps them to actions of your choosing —
no MMKC, no crashing driver.

## Requirements

- Windows
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Install

Download `IntelliTypeElite.exe` and `config.json` from the
[latest release](https://github.com/rahulmangaldas/IntelliTypeElite/releases/latest)
into the same folder, then run the exe.

## Configuring buttons

Edit `config.json` — changes hot-reload automatically, no restart needed. See
`config.schema.json` for the full shape (editors like VS Code will give you
autocomplete). Each button supports:

- A default action: `None`, `Keystroke`, `Launch`, or `CloseWindow`
- `appOverrides` — different behavior per foreground app
- `modifierOverrides` — different behavior when Shift/Ctrl/Alt is held

## Logging

Off by default. Run with `/log` to enable a log file and the tray menu's "Open log" item.

## Building from source

```
dotnet build
```

See `CLAUDE.md` for toolchain/device details.
