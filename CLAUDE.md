# IntelliType Elite

Custom keymapper for a Microsoft Wireless Desktop Elite keyboard, replacing Microsoft
Mouse and Keyboard Center (whose `dc3d.sys` filter driver was causing BSODs, and which
has been uninstalled). Restores the keyboard's vendor-specific buttons and scroll wheel
that Windows has no generic driver for.

## Layout
This is `~/src/IntelliTypeElite/` — this folder is the actual git repo root (`~/src`
itself is just your general projects folder, holding several unrelated repos).
- The real app (WinForms tray app, .NET 10, `net10.0-windows`). Ships as a
  framework-dependent build (requires the .NET 10 runtime on the target machine) —
  not self-contained, to keep the GitHub release asset small.
- `../HidDiscovery/` (sibling folder, not part of this repo) — throwaway console tool
  used to reverse-engineer the keyboard's raw HID report format. Keep around in case
  more buttons ever need mapping. Targets `net10.0-windows`, same as the main app.
- `../NuGet.Config` (at `~/src/`, not part of this repo) — redirects NuGet source +
  package cache off `C:\` for any .NET project under `~/src`.
- Public repo: https://github.com/rahulmangaldas/IntelliTypeElite

## Toolchain
See `../CLAUDE.md` (at `~/src/`) for the portable .NET SDK location and env vars —
shared across this project and `HidDiscovery`.

## Device
Wireless Desktop Elite receiver: VID `0x045E`, PID `0x008A`. It's a composite HID
device — button/wheel data can live on any of several enumerated collections sharing
that VID/PID, so `HidListener` opens and reads all of them in parallel rather than
guessing which one is active.

Button/wheel reports use report ID 4 (buttons) and 6 (wheel). Mapping (byte offset +
bit mask per button) was reverse-engineered empirically via `HidDiscovery` against the
real hardware and is authoritative in `config.json` — cross-referencing Microsoft's
official IntelliType 8.2 installer (`itypedevices.xml`, `commands.xml`) confirmed which
buttons are semantically what (My Favorites 1-5, Favorites Legend, and the F1-F12 "F-Lock
off" command row: Help/Undo/Redo/New/Open/Close/Reply/Forward/Send/Spell/Save/Print).
F-Lock itself sends no HID event — it's a local firmware-only toggle.

The extracted installer (`IntelliType Pro 8.20.469.0/`, git-ignored — it's Microsoft's
installer, not ours to redistribute) is kept locally for reference; re-extract from
`IntelliType Pro 8.20.469.0 Setup.exe` if it's missing, or re-download from
`web.archive.org` (search the original filename `ITPx64_1033_8.20.469.0.exe` — the
official download was pulled). `commands.xml` also documents ~130 apps' per-application
command overrides (Word, Outlook, Notepad, Paint, etc.) beyond what's wired into
`config.json`, in case more app-specific behavior is wanted later.

## config.json
- The **live** config is at `~\.config\IntelliTypeElite\config.json` (`XDG_CONFIG_HOME`
  if set, else `Environment.SpecialFolder.UserProfile` + `.config`). Edit that file
  directly, not the one in this repo — it's **hot-reloaded** on save
  (`ConfigWatcher.cs`), no restart needed for button/action/override edits. Changing
  `device.vendorId`/`productId` does still require a restart (governs which HID
  collections get opened).
- This repo's `config.json`/`config.schema.json` are just the bundled **default
  template**, copied next to whichever exe you run (`CopyToOutputDirectory` in the
  `.csproj`). On first run ever, `Program.cs` seeds the live copy at
  `~/.config/IntelliTypeElite/` from that bundled default if it doesn't already exist.
  Both the dev build and the installed autostart copy end up sharing that same one live
  config, which is the point — edit it once, it applies everywhere.
- `config.schema.json` documents the full shape with per-field descriptions and is
  wired up via `config.json`'s `"$schema"` key — gives VS Code autocomplete/validation
  for free, no extension needed. Keep it in sync when changing `Config.cs`.
- Each button has a default `action`, optional `appOverrides` (keyed by process name,
  matches foreground window; matching is case-insensitive, but write new entries
  lowercase — e.g. `"outlook"` not `"OUTLOOK"`), and optional `modifierOverrides`
  (Shift/Ctrl/Alt held at press time, checked via `GetAsyncKeyState`). Resolution
  order: app match first, then modifier match within that scope (or the top-level
  scope if no app matched), then the plain default. Nesting is app → mod, not mod → app.
- Action types: `None` (no-op), `Keystroke` (`SendInput`, combo syntax `"ctrl+shift+t"`),
  `Launch` (`Process.Start`, `path`/`args`), `CloseWindow` (posts `WM_SYSCOMMAND`/
  `SC_CLOSE` to the foreground window directly — used for Close's default instead of
  simulating Alt+F4, since packaged/UWP apps like Calculator don't reliably react to a
  synthetic Alt+F4 keypress).

## Logging
Disabled by default — `IntelliTypeElite.exe` only writes `keymapper.log` (and shows the
tray "Open log" menu item) when launched with `/log`.

## Autostart
Runs at login via a shortcut in `shell:startup`
(`~\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\IntelliTypeElite.lnk`),
pointing at a stable published copy — **not** the dev build in `bin/` — at
`~\.local\share\IntelliTypeElite\` (framework-dependent, `dotnet publish -c Release -r
win-x64 --self-contained false -p:PublishSingleFile=true`). Rebuild/republish that copy
after any code change you want live at next login:
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ~\.local\share\IntelliTypeElite
```
It shares the same live config as the dev build (see `## config.json` above) — no
separate copy to keep in sync.

## Known quirks
- A directory that's any shell's current working directory can't be renamed/deleted on
  Windows — `cd` out in every open shell before restructuring folders.
- A previously-run `dotnet build`/app process can hold a lock on `bin\...\*.exe`,
  breaking the next build with `MSB3027`. Stop the process first.
