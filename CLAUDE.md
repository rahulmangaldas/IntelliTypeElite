# IntelliType Elite

Custom keymapper for a Microsoft Wireless Desktop Elite keyboard, restores the keyboard's vendor-specific buttons and scroll wheel that Windows has no generic driver for.

## Rule: don't build
Don't run `dotnet build`/`publish`, don't stop/restart the running app, and don't
regenerate the installed/autostart copy — for any reason — unless explicitly asked to
in that turn. Editing source/config files is fine.

## Layout
This is `%USERPROFILE%\src\IntelliTypeElite\` — this folder is the actual git repo
root (`%USERPROFILE%\src` itself is just your general projects folder, holding several
unrelated repos).
- The real app (WinForms tray app, .NET 10, `net10.0-windows`). Ships as a
  framework-dependent build (requires the .NET 10 runtime on the target machine) —
  not self-contained, to keep the GitHub release asset small.
- `support\HidDiscovery\` (git-ignored, not part of this repo) — throwaway console tool
  used to reverse-engineer the keyboard's raw HID report format. Keep around in case
  more buttons ever need mapping. Targets `net10.0-windows`, same as the main app.
- `..\NuGet.Config` (at `%USERPROFILE%\src\`, not part of this repo) — redirects NuGet
  source + package cache off `C:\` for any .NET project under `%USERPROFILE%\src`.
- Public repo: https://github.com/rahulmangaldas/IntelliTypeElite

## Toolchain
See `..\CLAUDE.md` (at `%USERPROFILE%\src\`) for the portable .NET SDK location and
env vars — shared across this project and `HidDiscovery`.

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

The extracted installer (`support\IntelliType Pro 8.20.469.0\`, git-ignored — it's
Microsoft's installer, not ours to redistribute) is kept locally for reference;
re-extract from `support\IntelliType Pro 8.20.469.0 Setup.exe` if it's missing, or
re-download from `web.archive.org` (search the original filename
`ITPx64_1033_8.20.469.0.exe` — the official download was pulled). `commands.xml` also
documents ~130 apps' per-application command overrides (Word, Outlook, Notepad, Paint,
etc.) beyond what's wired into `config.json`, in case more app-specific behavior is
wanted later.

## config.json
- The **live** config is at `%USERPROFILE%\.config\IntelliTypeElite\config.json`
  (`%XDG_CONFIG_HOME%` if set, else `%USERPROFILE%\.config`). Edit that file directly,
  not the one in this repo — it's **hot-reloaded** on save (`ConfigWatcher.cs`), no
  restart needed for button/action/override edits. Changing `device.vendorId`/
  `productId` does still require a restart (governs which HID collections get opened).
- This repo's `config.json`/`config.schema.json` are just the bundled **default
  template**, copied next to whichever exe you run (`CopyToOutputDirectory` in the
  `.csproj`). On first run ever, `Program.cs` seeds the live copy at
  `%USERPROFILE%\.config\IntelliTypeElite\` from that bundled default if it doesn't
  already exist. Both the dev build and the installed autostart copy end up sharing
  that same one live config, which is the point — edit it once, it applies everywhere.
- `config.schema.json` documents the full shape with per-field descriptions and is
  wired up via `config.json`'s `"$schema"` key — gives VS Code autocomplete/validation
  for free, no extension needed. Keep it in sync when changing `Config.cs`.
- Each button has a default `action`, optional `appOverrides` (keyed by process name,
  matches foreground window; matching is case-insensitive, but write new entries
  lowercase — e.g. `"outlook"` not `"OUTLOOK"`), and optional `modifierOverrides`
  (Shift/Ctrl/Alt/Win held at press time, checked via `GetAsyncKeyState`). Resolution
  order: app match first, then modifier match within that scope (or the top-level
  scope if no app matched), then the plain default. Nesting is app → mod, not mod → app.
- Action types: `None` (no-op), `Keystroke` (`SendInput`, combo syntax `"ctrl+shift+t"`),
  `Launch` (`Process.Start`, `path`/`args`), `CloseWindow` (posts `WM_SYSCOMMAND`/
  `SC_CLOSE` to the foreground window directly — used for Close's default instead of
  simulating Alt+F4, since packaged/UWP apps like Calculator don't reliably react to a
  synthetic Alt+F4 keypress), `ShowFavorites` (overlay listing MyFavorites1-5), and
  `Sequence` (`steps`: keystrokes, typed text and pauses, run on a background task, one
  at a time — see Mac mode below). An unknown key token in a combo sends nothing.

## Logging
Disabled by default — `IntelliTypeElite.exe` only writes `IntelliTypeElite.log` (and
shows the tray "Open log" menu item) when launched with `/log`.

## File unblocker
`FileUnblocker.cs` strips the Mark of the Web (`Zone.Identifier` stream) from safe file
types so Windows stops warning about downloads. Config section `fileUnblocker`
(hot-reloaded; off by default — the bundled template ships `enabled: false`, so the real
folder list lives only in the live config).
- Only extensions in `extensions` are touched (default: PDF, common image, video and audio
  types). Executables, scripts, Office files and archives keep their mark.
- Real-time: one `FileSystemWatcher` per folder (Created/Changed/Renamed), debounced per
  file by `settleMs` because browsers write, rename, then stamp the mark. Sharing
  violations are retried (5×, 2 s apart).
- Safety net: a sweep at startup (and whenever the section changes) plus every
  `sweepIntervalMinutes`. Needed because watcher buffers can overflow and a watched
  folder can be deleted/recreated (the `Error` event triggers a sweep, and every sweep
  first recreates any watcher that died). The tray item "Unblock files now" runs the
  same sweep; a sweep requested while one is running makes it loop once more.
- Config is normalized on load: `settleMs` clamped to 0..60000, `sweepIntervalMinutes` to
  0..10080, a null list/section treated as empty/defaults. A section that still can't be
  applied leaves the feature off (logged with `/log`) and is retried on the next reload.
- Read-only files are left marked on purpose (deleting their stream is denied; clearing the
  read-only flag would change metadata OneDrive syncs). Logged once per path.
- `%USERPROFILE%` here is `C:\Users\<name>`, which has no Documents/OneDrive folders, so
  the live config uses `%OneDriveCommercial%` / `%OneDriveConsumer%` (note `%OneDrive%`
  is the *business* root on this machine).
- Folders are `%VAR%`-expanded and junctions resolved to the real path (same reason as
  `ConfigWatcher`). Junctions *inside* a watched folder are not followed (the home folder
  is full of junctions into OneDrive; following them double-processes files). A folder
  nested inside another watched folder is dropped.
- OneDrive cloud-only placeholders and folders (`RECALL_ON_*` attributes) are skipped so
  nothing gets downloaded. The stream is removed with `DeleteFileW(path + ":Zone.Identifier")`;
  no mark means "file not found", which is the normal no-op.

## Mac mode (ShareMouse)
The keyboard is shared with a Mac through ShareMouse. When the Mac has the keyboard,
`MacModeDetector` flips the app into "Mac mode": a button uses its `macAction` (instead of
`action` and every `appOverrides`/`modifierOverrides`), and does nothing if it has none.
Only `Keystroke`, `Sequence` and `None` are honored there — `Launch`/`CloseWindow`/
`ShowFavorites` would act on the PC.

- **Detection** is a stateless check at press time, driven by the top-level `layout`
  (omit it to disable Mac mode): ShareMouse is running with an established TCP connection
  on port 6555 (so a sleeping/offline Mac doesn't count; the answer is cached ~2 s) AND the
  Windows cursor sits on a PC-screen edge stretch that borders a `macos` monitor in
  `layout.monitors`. A `Sequence` started in Mac mode re-checks before every step and
  stops if the cursor has come back to the PC. When the
  mouse crosses to the Mac, the Windows cursor freezes at the edge it left through while
  raw mouse movement keeps arriving. Nothing else was usable: the cursor-visible flag,
  ShareMouse's windows, its TCP connection (one peer, port 6555) and its I/O counters
  don't change when you cross over.
- **Layout syntax**: rows top to bottom, items left to right, e.g.
  `[["windows:3840x2160", "macos:3840x2160"], ["none:25%", "macos:1920x1080"]]`.
  `none:N%` is an inline empty gap, N% of the first monitor's width in row 1. Exactly one
  `windows` entry (the primary screen). Edge overlaps are computed as fractions, so the
  declared sizes only need correct *relative* proportions. Limits: items in a row are
  top-aligned and a row starts below the tallest item of the row above, so a smaller Mac
  screen that ShareMouse centres or bottom-aligns beside the PC can't be described; and
  `none:N%` needs a monitor in the first row to measure against.
- **Keys**: ShareMouse maps Win → Cmd, so `cmd` is an alias of the Windows key. It does
  forward keystrokes injected with `SendInput`, but does **not** forward the special
  keys (Back/Forward/Mail/Home/Calculator/Sleep do nothing on the Mac) — hence the
  `macAction`s on those buttons. Launching a Mac app = a `Sequence` that presses
  `cmd+space`, types the name, then Return (tune the `delayMs` values if Spotlight drops
  characters). Sequences run on a background task, one at a time.
- Verified by hand: while on the Mac ShareMouse swallows the Win key locally (no Start
  menu / Win+key shortcuts fire on the PC), and a physical Win+[ does the Mac's Cmd+[.
- Open: whether the special keys also fire on the PC while you're on the Mac isn't known,
  nor whether *injected* `[`/`]`/arrow keys (sent with no scan code) reach the Mac the way
  the physical ones do.

## Autostart
Runs at login via a shortcut in `shell:startup`
(`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\IntelliTypeElite.lnk`),
pointing at a stable published copy — **not** the dev build in `bin\` — at
`%USERPROFILE%\.local\share\IntelliTypeElite\` (framework-dependent, `dotnet publish
-c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`). Rebuild/
republish that copy after any code change you want live at next login:
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o %USERPROFILE%\.local\share\IntelliTypeElite
```
It shares the same live config as the dev build (see `## config.json` above) — no
separate copy to keep in sync.

## Known quirks
- A directory that's any shell's current working directory can't be renamed/deleted on
  Windows — `cd` out in every open shell before restructuring folders.
- A previously-run `dotnet build`/app process can hold a lock on `bin\...\*.exe`,
  breaking the next build with `MSB3027`. Stop the process first.
