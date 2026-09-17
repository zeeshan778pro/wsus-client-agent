using Agent.Shared;

namespace Agent.Tray;

/// <summary>
/// The "Software Center"-style window - the whole point of this agent
/// being separate from Windows Update's own Settings page. Built
/// entirely in code (no .resx/.Designer.cs) so the UI is plain,
/// reviewable source rather than opaque designer-generated markup.
/// </summary>
public class UpdatesForm : Form
{
    private readonly LocalDb _db;
    private readonly ListView _list;
    private readonly Label _statusLabel;
    private readonly Button _installSelectedBtn;
    private readonly Button _installAllBtn;
    private readonly Button _scanNowBtn;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public UpdatesForm(LocalDb db)
    {
        _db = db;

        Text = "Software Updates";
        Width = 720;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 360);

        _list = new ListView
        {
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            Dock = DockStyle.Top,
            Height = 340,
        };
        _list.Columns.Add("Title", 320);
        _list.Columns.Add("KB", 80);
        _list.Columns.Add("Severity", 90);
        _list.Columns.Add("Size (MB)", 80);
        _list.Columns.Add("Status", 100);

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(6, 4, 0, 0) };

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var closeBtn = new Button { Text = "Close", AutoSize = true };
        closeBtn.Click += (_, _) => Close();
        _scanNowBtn = new Button { Text = "Check for Updates Now", AutoSize = true };
        _scanNowBtn.Click += (_, _) => { _db.EnqueueCommand("ScanNow"); RefreshList(); };
        _installAllBtn = new Button { Text = "Install All", AutoSize = true };
        _installAllBtn.Click += (_, _) => { _db.EnqueueCommand("InstallAll"); RefreshList(); };
        _installSelectedBtn = new Button { Text = "Install Selected", AutoSize = true };
        _installSelectedBtn.Click += (_, _) => InstallSelected();
        buttonPanel.Controls.Add(closeBtn);
        buttonPanel.Controls.Add(_scanNowBtn);
        buttonPanel.Controls.Add(_installAllBtn);
        buttonPanel.Controls.Add(_installSelectedBtn);

        Controls.Add(_list);
        Controls.Add(buttonPanel);
        Controls.Add(_statusLabel);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Stop();

        RefreshList();
    }

    private void InstallSelected()
    {
        var ids = _list.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (ids.Count == 0)
        {
            MessageBox.Show(this, "Check at least one update first.", "Software Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _db.EnqueueCommand("InstallSelected", string.Join(",", ids));
        RefreshList();
    }

    private void RefreshList()
    {
        var updates = _db.GetUpdates();
        var status = _db.GetStatus();

        // Preserve check state across refreshes by update ID, since the
        // list is fully rebuilt every tick to reflect live status changes.
        var previouslyChecked = _list.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var u in updates)
        {
            var item = new ListViewItem(u.Title) { Tag = u.UpdateId, Checked = previouslyChecked.Contains(u.UpdateId) };
            item.SubItems.Add(u.KbArticle ?? "");
            item.SubItems.Add(u.Severity ?? "");
            item.SubItems.Add(u.SizeMb?.ToString("0.#") ?? "");
            item.SubItems.Add(u.Status);
            if (u.Status == "Failed") item.ForeColor = Color.Firebrick;
            else if (u.Status == "Installed") item.ForeColor = Color.SeaGreen;
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        var parts = new List<string>();
        parts.Add(status.LastScanAt.HasValue ? $"Last checked: {status.LastScanAt.Value.ToLocalTime():g}" : "Not checked yet");
        if (status.ScanInProgress) parts.Add("checking now...");
        if (status.InstallInProgress) parts.Add("installing...");
        if (status.PendingReboot) parts.Add("RESTART REQUIRED");
        if (!string.IsNullOrEmpty(status.LastError)) parts.Add($"Last error: {status.LastError}");
        _statusLabel.Text = string.Join("  |  ", parts);

        bool busy = status.ScanInProgress || status.InstallInProgress;
        _installAllBtn.Enabled = !busy && updates.Any(u => u.Status is "Pending" or "ReadyToInstall" or "Failed");
        _installSelectedBtn.Enabled = !busy;
        _scanNowBtn.Enabled = !busy;
    }
}
