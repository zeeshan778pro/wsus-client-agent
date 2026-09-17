<#
.SYNOPSIS
    Builds (if needed) and installs the WSUS Client Agent on this
    machine: registers Agent.Service as a Windows service, schedules
    Agent.Tray to launch at every user logon, and applies the same
    "point Windows Update at WSUS + hide the native Settings page"
    policy a real SCCM-managed machine would have.

.DESCRIPTION
    This agent is deliberately independent of any other application -
    its only state is the local SQLite file it creates itself under
    C:\ProgramData\WsusClientAgent, and it has no dependency on any
    central server (see Agent.Service/WuaScanner.cs - it talks to WSUS
    directly via the local Windows Update Agent COM API).

.PARAMETER WsusServerUrl
    e.g. "http://wsusserver.domain.local:8530" - required so machine
    policy actually points Windows Update at your WSUS server. Without
    this, CreateUpdateSearcher() would resolve against public Windows
    Update instead.

.PARAMETER SourceRoot
    Path to this wsus-client-agent checkout (defaults to this script's
    own folder). Only needed if running install.ps1 from somewhere else.

.PARAMETER SkipBuild
    Pass this if you've already run `dotnet publish` yourself and just
    want install.ps1 to copy/register what's already in
    Agent.Service\bin\Release\net8.0-windows\win-x64\publish and
    Agent.Tray\bin\Release\net8.0-windows\win-x64\publish.

.EXAMPLE
    .\install.ps1 -WsusServerUrl "http://wsus01.amg.local:8530"
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$WsusServerUrl,

    [string]$SourceRoot = $PSScriptRoot,

    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "install.ps1 must be run from an elevated (Administrator) PowerShell session."
}

$InstallDir      = "C:\Program Files\WsusClientAgent"
$ServiceInstall  = Join-Path $InstallDir "Service"
$TrayInstallDir  = Join-Path $InstallDir "Tray"
$DataDir         = "C:\ProgramData\WsusClientAgent"
$ServiceName     = "WsusClientAgent"
$TaskName        = "WsusClientAgentTray"

# ── 1. Build ──────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "[1/5] Publishing Agent.Service and Agent.Tray (self-contained win-x64)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $SourceRoot "Agent.Service\Agent.Service.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $SourceRoot "Agent.Service\bin\Release\net8.0-windows\win-x64\publish")
    if ($LASTEXITCODE -ne 0) { throw "Agent.Service publish failed." }
    dotnet publish (Join-Path $SourceRoot "Agent.Tray\Agent.Tray.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $SourceRoot "Agent.Tray\bin\Release\net8.0-windows\win-x64\publish")
    if ($LASTEXITCODE -ne 0) { throw "Agent.Tray publish failed." }
} else {
    Write-Host "[1/5] Skipping build (using existing publish output)." -ForegroundColor Cyan
}

# ── 2. Stop/remove any existing install (safe re-run) ───────────────────
Write-Host "[2/5] Stopping any existing service/task..." -ForegroundColor Cyan
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
Get-Process -Name "Agent.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force

# ── 3. Copy files ────────────────────────────────────────────────────
Write-Host "[3/5] Copying published files to $InstallDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $ServiceInstall -Force | Out-Null
New-Item -ItemType Directory -Path $TrayInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $SourceRoot "Agent.Service\bin\Release\net8.0-windows\win-x64\publish\*") -Destination $ServiceInstall -Recurse -Force
Copy-Item -Path (Join-Path $SourceRoot "Agent.Tray\bin\Release\net8.0-windows\win-x64\publish\*") -Destination $TrayInstallDir -Recurse -Force

# Shared local DB folder - both the service (SYSTEM) and the tray app
# (a standard logged-in user) need to read/write it.
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
icacls $DataDir /grant "BUILTIN\Users:(OI)(CI)M" | Out-Null

# ── 4. Register the service + logon task ────────────────────────────
Write-Host "[4/5] Registering service and scheduled task..." -ForegroundColor Cyan
$serviceExe = Join-Path $ServiceInstall "Agent.Service.exe"
New-Service -Name $ServiceName -BinaryPathName "`"$serviceExe`"" -DisplayName "WSUS Client Agent" `
    -Description "Scans WSUS for approved updates and installs them - reports through its own tray app, not Windows Update's native UI." `
    -StartupType Automatic | Out-Null
Start-Service -Name $ServiceName

$trayExe = Join-Path $TrayInstallDir "Agent.Tray.exe"
$action    = New-ScheduledTaskAction -Execute $trayExe
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -GroupId "BUILTIN\Users" -RunLevel Limited
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# Also start it right now for whoever's already logged in, rather than
# waiting for their next logon.
if ($env:USERNAME -ne "SYSTEM") {
    Start-Process -FilePath $trayExe
}

# ── 5. Point Windows Update at WSUS + hide the native page ──────────
Write-Host "[5/5] Applying WSUS + Windows Update policy..." -ForegroundColor Cyan
$wuKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"
$auKey = "$wuKey\AU"
New-Item -Path $wuKey -Force | Out-Null
New-Item -Path $auKey -Force | Out-Null
Set-ItemProperty -Path $wuKey -Name "WUServer" -Value $WsusServerUrl
Set-ItemProperty -Path $wuKey -Name "WUStatusServer" -Value $WsusServerUrl
Set-ItemProperty -Path $auKey -Name "UseWUServer" -Value 1 -Type DWord
# The built-in Automatic Updates client no longer scans/notifies on its
# own - this agent is now the only thing driving WUA on this machine.
Set-ItemProperty -Path $auKey -Name "NoAutoUpdate" -Value 1 -Type DWord
Restart-Service -Name wuauserv -Force -ErrorAction SilentlyContinue

# The per-user "hide the Settings > Windows Update page" registry value
# is applied by Agent.Tray itself on every startup (see
# TrayAppContext.HideNativeWindowsUpdatePage) so it covers every
# profile that ever logs in, not just the one running this script - a
# domain GPO doing the same thing (User Configuration) is the more
# scalable fleet-wide equivalent if you're rolling this out broadly.

Write-Host ""
Write-Host "Done. Service '$ServiceName' is running; the tray icon should appear for the current user now, and for every future logon." -ForegroundColor Green
Write-Host "First scan happens on the service's own schedule (default: every 4 hours, see appsettings.json) - use the tray icon's 'Check for Updates Now' to trigger one immediately." -ForegroundColor Green
