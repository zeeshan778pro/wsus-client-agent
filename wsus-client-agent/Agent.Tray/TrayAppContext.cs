using Agent.Shared;
using Microsoft.Win32;

namespace Agent.Tray;

/// <summary>
/// The tray icon itself, plus the poll timer that keeps its color/
/// tooltip in sync with what the service (a separate process, running
/// as SYSTEM) has written to the shared local SQLite DB. This process
/// never talks to WSUS or WUA directly - it only ever reads status/
/// writes command rows, same as every other consumer of LocalDb.
/// </summary>
public class TrayAppContext : ApplicationContext
{
    private readonly LocalDb _db = new();
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private UpdatesForm? _updatesForm;

    public TrayAppContext()
    {
        HideNativeWindowsUpdatePage();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Software Updates...", null, (_, _) => ShowUpdatesForm());
        menu.Items.Add("Check for Updates Now", null, (_, _) => _db.EnqueueCommand("ScanNow"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "WSUS Client Agent",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowUpdatesForm();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _pollTimer.Tick += (_, _) => RefreshIcon();
        _pollTimer.Start();
        RefreshIcon();
    }

    private void RefreshIcon()
    {
        try
        {
            var updates = _db.GetUpdates();
            var status = _db.GetStatus();
            int pending = updates.Count(u => u.Status is "Pending" or "ReadyToInstall" or "Failed");

            _icon.Icon = pending > 0 ? SystemIcons.Warning : SystemIcons.Application;
            _icon.Text = status.PendingReboot
                ? "WSUS Client Agent - restart required"
                : pending > 0
                    ? $"WSUS Client Agent - {pending} update(s) pending"
                    : "WSUS Client Agent - up to date";

            if (status.PendingReboot)
            {
                _icon.BalloonTipTitle = "Restart required";
                _icon.BalloonTipText = "Updates have been installed - please restart when convenient.";
            }
        }
        catch
        {
            // The DB briefly locked by the service mid-write is not worth
            // surfacing to the user - the next 30s tick will just retry.
        }
    }

    private void ShowUpdatesForm()
    {
        if (_updatesForm is { IsDisposed: false })
        {
            _updatesForm.Activate();
            return;
        }
        _updatesForm = new UpdatesForm(_db);
        _updatesForm.Show();
    }

    private void ExitApp()
    {
        _icon.Visible = false;
        Application.Exit();
    }

    /// <summary>
    /// "SettingsPageVisibility=hide:windowsupdate" under the CURRENT
    /// user's hive is the documented way to remove the Windows Update
    /// page from Settings entirely (it shows "This setting is managed
    /// by your organization" if reached another way). This is per-user,
    /// not machine-wide, so a GPO User Configuration policy would be
    /// the fleet-wide equivalent - setting it here too means a machine
    /// this agent is installed on is covered even without that GPO, and
    /// it's applied every logon (whenever the tray app starts), so it
    /// can't be missed for a profile that existed before install.ps1 ran.
    /// </summary>
    private static void HideNativeWindowsUpdatePage()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\WindowsUpdate");
            var existing = key.GetValue("SettingsPageVisibility") as string ?? "";
            if (!existing.Contains("windowsupdate"))
            {
                var combined = string.IsNullOrEmpty(existing) ? "hide:windowsupdate" : existing.TrimEnd(';') + ";hide:windowsupdate";
                key.SetValue("SettingsPageVisibility", combined, RegistryValueKind.String);
            }
        }
        catch
        {
            // Non-fatal - the agent still works, the native page just
            // wouldn't be hidden on this particular profile.
        }
    }
}
