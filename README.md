# IntelliType Elite

A lightweight tray app that restores the extra buttons and scroll wheel functionality on a Microsoft
Wireless Desktop Elite keyboard.

## Why

Microsoft dropped support for this keyboard long ago in `IntelliType Pro 8.2`; `Microsoft Mouse & Keyboard Center` doesn't support it officially—only the buttons work, the scroll wheel doesn't—and it frequently BSOD's in `dc3d.sys` driver. This app reads the keyboard's HID reports directly and maps them to actions of your choosing: no dependencies, no MMKC, no crashing driver.

## Requirements

- Windows
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installing

Download `IntelliTypeElite.zip` from the [latest release](https://github.com/rahulmangaldas/IntelliTypeElite/releases/latest), extract it anywhere, and run `IntelliTypeElite.exe`; it copies  `config.json` to `%USERPROFILE%\.config\IntelliTypeElite\config.json` on first run and uses that copy.

## Configuring

Edit `config.json`: changes hot-reload, no restart needed. See `config.schema.json` for the schema (JSON aware editors like Visual Studio Code will give you autocomplete and syntax highlighting). Each button supports:

- A default action: `None`, `Keystroke`, `Launch`, `CloseWindow`, or `Sequence` (a series of keystrokes, typed text and pauses)
- `appOverrides` — different behavior per app
- `modifierOverrides` — different behavior when Shift/Ctrl/Alt/Win is held
- `macAction` — what the button does while your Mac has the keyboard (see below)

## Sharing the keyboard with a Mac (ShareMouse)

If you share this keyboard with a Mac through [ShareMouse](https://www.sharemouse.com/), the app can tell when the Mac has the keyboard and send Mac shortcuts instead. ShareMouse doesn't forward this keyboard's special keys (Back, Forward, Mail, Calculator…) to the Mac, so without this they do nothing there.

Describe your monitor arrangement in `config.json` (omit `layout` to turn Mac mode off):

```json
"layout": {
  "monitors": [
    ["windows:3840x2160", "macos:3840x2160"]
  ]
}
```

Rows run top to bottom and items left to right, as `machine:WxH`. Exactly one `windows` entry (your primary screen) and any number of `macos` entries. `none:25%` is an empty gap, 25% of the first monitor's width in the first row, for monitors that don't line up. Only the relative proportions matter, so use whatever units you like. Mac mode is on while ShareMouse is connected and your cursor is parked on the edge of the PC screen that faces a Mac monitor, which is where ShareMouse leaves it when the mouse crosses over.

Then give buttons a `macAction`. In Mac mode it replaces the button's action and all its overrides; a button without one does nothing. Only `Keystroke`, `Sequence` and `None` work there, since anything else would run on the PC. `cmd` is the Windows key, which ShareMouse maps to Cmd:

```json
{ "name": "F2_Undo", ..., "macAction": { "type": "Keystroke", "keys": "cmd+z" } },
{ "name": "Mail", ..., "macAction": { "type": "Sequence", "steps": [
    { "keys": "cmd+space", "delayMs": 100 },
    { "text": "mail", "delayMs": 100 },
    { "keys": "enter" } ] } }
```

The second one launches a Mac app through Spotlight. Raise the `delayMs` values if Spotlight drops letters. A sequence stops if you move the cursor back to the PC partway through, and a second press while one is running is ignored.

Keys accepted in `keys` include `ctrl`, `alt`, `shift`, `win`/`cmd`, `enter`, `space`, `tab`, `esc`, `backspace`, `delete`, the arrow, `home`/`end` and `pageup`/`pagedown` keys, `[`, `]`, letters, digits and `F1`–`F24`. An unrecognized key sends nothing rather than part of the combo.

## Unblocking downloaded files

Windows tags files that come from the internet with a "Mark of the Web", which is what triggers the "this file may be harmful" warnings and Protected View. The optional file unblocker removes that mark from safe file types (PDF, images, video and audio) in the folders you list, as soon as they appear, and leaves everything else (executables, scripts, Office files, archives) marked. It's off by default:

```json
"fileUnblocker": {
  "enabled": true,
  "folders": [ "%USERPROFILE%\\Downloads" ],
  "includeSubfolders": true
}
```

`%VAR%` references in `folders` are expanded. Also configurable: `extensions` (replaces the default list), `settleMs` (how long a new file must be quiet before it's touched, default 1500), and `sweepOnStart` / `sweepIntervalMinutes` (a full pass at startup and every 120 minutes by default, as a safety net in case the watcher misses something). "Unblock files now" in the tray menu runs a pass on demand.

Junctions and symlinks inside a watched folder aren't followed, OneDrive files that are online-only are skipped so nothing gets downloaded, and read-only files are left marked.

## Logging

Run with `/log` to enable a log file and "Open log" in the app menu.

## Building from source

```
dotnet build
```
