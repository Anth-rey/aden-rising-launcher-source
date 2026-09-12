# Aden Rising Launcher

The Windows launcher for [Aden Rising](https://adenrising.com), a free
Lineage II (Interlude) server. It installs and updates the game client, keeps
itself up to date, and starts the game.

Official builds are signed as **Open Source Developer** (Certum) and published
at `https://download.adenrising.com/launcher/AdenRisingLauncher.exe`. This
repository is the source they are built from.

## What it does on your computer

- **Installs and updates the client** in the folder you choose. Every file is
  checked against a manifest by SHA-256; only what changed is downloaded. Your
  own settings files (key bindings, window positions, screenshots) are left
  alone.
- **Updates itself**: it compares its version with
  `download.adenrising.com/launcher/latest.json` and replaces its own `.exe`
  when there is a newer one (hash-checked before it is used).
- **Checks the core client files before Play** (`L2.exe`, `engine.dll`,
  `Core.dll`, `l2.ini`, `Interface.u`) against the install record, and refuses
  to start a client whose files were swapped.
- **Marks the game windows it starts**: a one-shot file
  `.updater\launch.token` in the game folder, written right before the game
  starts and removed three seconds later. The Aden Rising `L2.exe` looks for it
  and sends you to the launcher when you start the game some other way.
- **Stays one instance**: starting it twice brings the running one forward.

It needs no administrator rights of its own, installs no service or driver,
does not start with Windows, and does not read or change any other program.

## What it sends to the server

When a game window it started connects to the game server, the launcher sends
one report to `https://adenrising.com/api/launcher/launch` so the server can
tell its own windows from anything else that tries to log in. The report holds:

- the **local port** of that window's connection to the game server, found in
  the TCP connection table Windows keeps for every process,
- the **process id** of the game window,
- the **client and launcher versions**,
- a **computer id**: a SHA-256 hash of the Windows machine GUID, the system
  drive's volume serial and the processor name. It is the same after a
  reinstall and says nothing about who you are; the server uses it to count
  how many windows one computer has open,
- the **names** of running programs that match a short list of known bot and
  cheat tools (see `WatchedProcesses` in `updater/Core/LaunchReport.cs`), and
  the file names of libraries loaded into the game that come from neither the
  game folder nor Windows. Only names, never contents.

Nothing else is collected: no accounts, passwords, files or browsing data. A
report that cannot be sent never stops the game; whether a window without one
may enter the world is the server's decision.

## Building

Requires the .NET 9 SDK on Windows.

```
cd updater
dotnet build
dotnet publish -c Release -o ../publish
```

The window-report key is not in this repository (see `NOTICE.md`). A publish
without it stops with an error; `-p:AllowKeylessPublish=true` builds a launcher
that works but does not report windows, so the game server treats them as not
started by the official launcher.

## Licence

The source is under the MIT licence (`LICENSE`). The Aden Rising name, logo and
artwork are not; see `NOTICE.md`.
