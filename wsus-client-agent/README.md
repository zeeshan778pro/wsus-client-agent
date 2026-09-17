# WSUS Client Agent

A standalone Windows service + tray app that pulls approved updates
directly from WSUS (via the local Windows Update Agent COM API - the
same technique `Push-WSUSPatchAction.ps1` uses) and shows them in its
own "Software Center"-style window instead of Windows Update's native
Settings page - closer to how a real SCCM client behaves.

**This is deliberately independent of the VMInvDB app.** It has no
network dependency on it, no shared database, and no server component
at all. Its only state is a local SQLite file each machine creates for
itself at `C:\ProgramData\WsusClientAgent\agent.db`. It keeps working
even if VMInvDB is offline, upgraded, or removed entirely.

## How it works

- **Agent.Service** (runs as SYSTEM, a real Windows service): on a
  timer (default every 4 hours, `Agent:ScanIntervalHours` in
  `Agent.Service/appsettings.json`), searches WSUS via
  `Microsoft.Update.Session` for updates that are `IsInstalled=0 and
  IsHidden=0`, and writes what it finds into the local SQLite DB. It
  also polls that same DB every 15 seconds for command rows the tray
  app has queued (install now / scan now) and acts on them.
- **Agent.Tray** (runs in the logged-in user's session, launched by a
  Scheduled Task at every logon): a NotifyIcon that changes color when
  updates are pending, plus a window listing them with checkboxes and
  Install Selected / Install All / Check Now buttons. It never talks
  to WSUS or WUA directly - it only reads status from the shared local
  DB and writes command rows for the service to pick up.
- **Agent.Shared**: the `LocalDb` class both of the above use - this
  IS the entire IPC mechanism between the two processes (no named
  pipes, no sockets), and the entire persistence layer (no external
  database of any kind).

## Locking down the native Windows Update UI

`install.ps1` applies these machine-wide (`HKLM`, so no per-profile
gaps) under `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate`:

- `WUServer`/`WUStatusServer`/`UseWUServer=1` - `CreateUpdateSearcher()`,
  whether called by this agent or by Windows itself, resolves against
  your WSUS server instead of public Windows Update.
- `NoAutoUpdate=1` - disables the built-in Automatic Updates client's
  own background scanning, so it doesn't compete with this agent.
- `SetDisableUXWUAccess=1` - disables the interactive "Check for
  updates" button (and related actions) in Settings > Windows Update,
  so a user can't trigger a scan+install there that bypasses this
  agent's tray flow entirely. Deliberately chosen over hiding the whole
  page (`SettingsPageVisibility=hide:windowsupdate`, used by an earlier
  version of this agent): that approach also hid the Update History
  sub-page, and crucially `NoAutoUpdate` alone does NOT stop a user
  from manually clicking "Check for updates" and having Windows
  download+install on its own. This way, Settings > Windows Update
  (including its history) stays visible and readable, but only this
  agent can actually trigger an install.

## Building

Requires the **.NET 8 SDK** (not just the runtime) on whatever machine
you build on - install from https://dotnet.microsoft.com/download if
`dotnet --version` doesn't already show an 8.x version. Building from
VS Code just means running these from its integrated terminal (no
Visual Studio needed):

```
cd wsus-client-agent
dotnet build
```

## Packaging a distributable Setup.exe (do this once, on your dev machine)

End users/admins deploying to a client machine should never see
PowerShell flags like `-SkipBuild` - that's what **Agent.Installer**
(built as `Setup.exe`) is for: a small GUI that only asks for the WSUS
server address, then runs `install.ps1 -SkipBuild` under the hood with
that value filled in automatically. `Setup.exe` requests admin
elevation itself (via its manifest), so double-clicking it is enough -
no "Run as Administrator" reminder needed.

Publish all three deployable projects, then assemble one folder with
everything `Setup.exe` expects to find next to itself:

```powershell
cd wsus-client-agent
dotnet publish Agent.Service\Agent.Service.csproj -c Release -r win-x64 --self-contained true -o Agent.Service\bin\Release\net8.0-windows\win-x64\publish
dotnet publish Agent.Tray\Agent.Tray.csproj       -c Release -r win-x64 --self-contained true -o Agent.Tray\bin\Release\net8.0-windows\win-x64\publish
dotnet publish Agent.Installer\Agent.Installer.csproj -c Release -r win-x64 --self-contained true -o Deploy

# Deploy\ now has Setup.exe + its runtime files. Add what it needs alongside it:
Copy-Item install.ps1 Deploy\
Copy-Item uninstall.ps1 Deploy\
Copy-Item -Recurse -Force Agent.Service Deploy\Agent.Service
Copy-Item -Recurse -Force Agent.Tray Deploy\Agent.Tray
```

`Deploy\` is now a complete, self-contained package - copy that whole
folder to a client machine and double-click `Deploy\Setup.exe`.

- **Not installed yet**: it prompts for the WSUS server address, shows
  live progress, and installs.
- **Already installed** (detected by checking whether
  `C:\Program Files\WsusClientAgent\Service\Agent.Service.exe`
  exists): it shows an **Uninstall** button instead (with optional
  checkboxes to also remove local data / the WSUS policy - same as
  `uninstall.ps1`'s own flags), plus an **Install** button if you just
  want to re-point it at a different WSUS server without uninstalling
  first.

Both buttons are just `Setup.exe` running `install.ps1`/`uninstall.ps1`
under the hood with `-SkipBuild` and the right flags filled in. The
scripted-only equivalent (for a fleet rollout via `Invoke-Command`/
PsExec instead of clicking through `Setup.exe` on each machine):

```powershell
.\install.ps1 -WsusServerUrl "http://your-wsus-server:8530" -SkipBuild
.\uninstall.ps1                          # leaves local data + WSUS policy in place
.\uninstall.ps1 -RemoveData -RemovePolicy  # full cleanup
```

### Advanced: installing without the GUI

`install.ps1` still works directly if you'd rather script a fleet
rollout (PsExec, `Invoke-Command`, a login script) than have someone
click through `Setup.exe` on each machine:

```powershell
.\install.ps1 -WsusServerUrl "http://your-wsus-server:8530" -SkipBuild
```

## Known limitations (v1)

- No central reporting/dashboard across machines - each agent is
  fully local and independent, by design (see top of this file). If
  you later want fleet-wide visibility, that would be a genuinely
  separate management system with its own separate database, not a
  bolt-on to this agent or to VMInvDB.
- WUA install results are mapped to Installed/Failed using only the
  two "succeeded" result codes (2 = orcSucceeded, 3 =
  orcSucceededWithErrors); anything else is treated as Failed with the
  raw WUA result code recorded.
- The per-user "hide native page" registry value only takes effect for
  profiles that have actually logged in and run the tray app at least
  once (see "Hiding the native Windows Update page" above).
