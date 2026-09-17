<#
.SYNOPSIS
    Removes the WSUS Client Agent from this machine - service,
    scheduled task, installed files. Local data (C:\ProgramData\
    WsusClientAgent) and the WSUS/Windows-Update registry policy are
    left in place by default; pass -RemoveData / -RemovePolicy to also
    clean those up.
#>
param(
    [switch]$RemoveData,
    [switch]$RemovePolicy
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "uninstall.ps1 must be run from an elevated (Administrator) PowerShell session."
}

$ServiceName = "WsusClientAgent"
$TaskName    = "WsusClientAgentTray"
$InstallDir  = "C:\Program Files\WsusClientAgent"
$DataDir     = "C:\ProgramData\WsusClientAgent"

Write-Host "Stopping service and tray app..." -ForegroundColor Cyan
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
}
Get-Process -Name "Agent.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($RemoveData -and (Test-Path $DataDir)) {
    Remove-Item -Path $DataDir -Recurse -Force
}

if ($RemovePolicy) {
    Remove-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate" -Recurse -Force -ErrorAction SilentlyContinue
    Restart-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
    Write-Host "Note: the per-user 'hide Windows Update page' registry value (HKCU\Software\Microsoft\Windows\CurrentVersion\WindowsUpdate\SettingsPageVisibility) is NOT removed automatically for every profile - clear it by hand per user if needed." -ForegroundColor Yellow
}

Write-Host "Uninstalled." -ForegroundColor Green
