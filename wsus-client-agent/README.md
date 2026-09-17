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

## Hiding the native Windows Update page

Two things make this behave like an SCCM-managed machine instead of a
normal one:

1. **Machine-wide**: `install.ps1` sets the standard WSUS policy keys
   (`WUServer`/`WUStatusServer`/`UseWUServer` under
   `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate`) so
   `CreateUpdateSearcher()` - whether called by this agent or by
   Windows itself - resolves against your WSUS server, and disables
   the built-in Automatic Updates client's own scanning
   (`NoAutoUpdate=1`) so it doesn't compete with this agent.
2. **Per-user**: `Agent.Tray` sets
   `HKCU\Software\Microsoft\Windows\CurrentVersion\WindowsUpdate\SettingsPageVisibility
   = hide:windowsupdate` on every startup (every logon), which removes
   the Windows Update page from Settings entirely for that profile.
   This is per-user by nature; if you're rolling this out fleet-wide, a
   domain GPO doing the same thing under User Configuration reaches
   profiles that haven't logged into an agent-installed machine yet,
   but the tray app self-applying it means a single machine works
   correctly on its own even without that GPO.

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

## Installing on a target machine

You don't need to build on every target machine - `install.ps1` can do
it for you (self-contained publish, so the target machine doesn't need
the .NET runtime installed either), or you can publish once and copy
the `publish` output to install with `-SkipBuild`.

```powershell
# From an elevated PowerShell prompt on the target machine, with this
# whole wsus-client-agent folder present (or copy just the publish
# output + install.ps1 -SkipBuild if you built elsewhere):
.\install.ps1 -WsusServerUrl "http://your-wsus-server:8530"
```

To remove it:

```powershell
.\uninstall.ps1                          # leaves local data + WSUS policy in place
.\uninstall.ps1 -RemoveData -RemovePolicy  # full cleanup
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
