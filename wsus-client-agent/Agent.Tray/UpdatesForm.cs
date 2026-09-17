using Agent.Shared;

namespace Agent.Tray;

/// <summary>
/// The Software Center-style shell - blue header, left sidebar (Updates
/// / Installation Status), and a content panel that swaps between the
/// two. Built entirely in code (no .resx/.Designer.cs) so the UI stays
/// plain, reviewable source. "Installation Status" exists specifically
/// because Setup.exe locks down the native Windows Update page's own
/// history view (see install.ps1's SetDisableUXWUAccess) - this is
/// where that history actually lives now, read from LocalDb's
/// permanent `history` table rather than the transient pending list.
/// </summary>
public class UpdatesForm : Form
{
    private readonly LocalDb _db;
    private readonly Panel _navUpdates;
    private readonly Panel _navHistory;
    private readonly Label _navUpdatesBadge;
    private readonly Panel _updatesView;
    private readonly Panel _historyView;
    private readonly DataGridView _grid;
    private readonly FlowLayoutPanel _historyList;
    private readonly Label _headerTitle;
    private readonly Label _headerMeta;
    private readonly Button _installSelectedBtn;
    private readonly Button _installAllBtn;
    private readonly Button _scanNowBtn;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _showingHistory;

    public UpdatesForm(LocalDb db)
    {
        _db = db;

        Text = "WSUS Client Agent";
        Width = 760;
        Height = 520;
        MinimumSize = new Size(620, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.Background;
        Font = UiTheme.Base();

        // ── Header ───────────────────────────────────────────────────
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = UiTheme.Accent };
        var headerLabel = new Label
        {
            Text = "WSUS Client Agent",
            ForeColor = Color.White,
            Font = UiTheme.Base(11f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 15),
        };
        header.Controls.Add(headerLabel);

        // ── Sidebar ──────────────────────────────────────────────────
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 168, BackColor = Color.FromArgb(0xF1, 0xF2, 0xF6), Padding = new Padding(0, 10, 0, 0) };
        var sidebarBorder = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = UiTheme.Border };
        sidebar.Controls.Add(sidebarBorder);

        _navUpdatesBadge = new Label { AutoSize = true, Font = UiTheme.Base(7.5f, FontStyle.Bold), ForeColor = Color.White };
        _navUpdates = BuildNavItem("🔄  Updates");
        _navUpdates.Controls.Add(_navUpdatesBadge);
        _navUpdates.Click += (_, _) => ShowUpdates();

        _navHistory = BuildNavItem("📋  Installation Status");
        _navHistory.Click += (_, _) => ShowHistory();

        sidebar.Controls.Add(_navHistory);
        sidebar.Controls.Add(_navUpdates);

        // ── Content: Updates view ───────────────────────────────────
        _updatesView = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.CardBackground, Visible = true };

        _headerTitle = new Label { Text = "Updates", Font = UiTheme.Base(13f, FontStyle.Bold), ForeColor = UiTheme.TextPrimary, AutoSize = true, Location = new Point(20, 16) };
        _headerMeta = new Label { Font = UiTheme.Base(8.5f), ForeColor = UiTheme.TextSecondary, AutoSize = true, Location = new Point(20, 42) };

        _grid = new DataGridView
        {
            Location = new Point(0, 68),
            Dock = DockStyle.Fill,
            BackgroundColor = UiTheme.CardBackground,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = UiTheme.Border,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 30 },
            Font = UiTheme.Base(8.75f),
        };
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(0xFA, 0xFB, 0xFD),
            ForeColor = UiTheme.TextSecondary,
            Font = UiTheme.Base(7.75f, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xEA, 0xF1, 0xFE);
        _grid.DefaultCellStyle.SelectionForeColor = UiTheme.TextPrimary;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.RowAlternate;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "col_check", HeaderText = "", FillWeight = 6, MinimumWidth = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "col_title", HeaderText = "Title", FillWeight = 44, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "col_kb", HeaderText = "KB", FillWeight = 14, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "col_severity", HeaderText = "Severity", FillWeight = 14, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "col_size", HeaderText = "Size (MB)", FillWeight = 12, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "col_status", HeaderText = "Status", FillWeight = 14, ReadOnly = true });

        var gridWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 68, 20, 0) };
        gridWrap.Controls.Add(_grid);

        var updatesFooter = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(20, 12, 20, 12) };
        _installSelectedBtn = new Button { Text = "Install Selected", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        UiTheme.StylePrimaryButton(_installSelectedBtn);
        _installSelectedBtn.Click += (_, _) => InstallSelected();
        _installAllBtn = new Button { Text = "Install All", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        UiTheme.StyleSecondaryButton(_installAllBtn);
        _installAllBtn.Click += (_, _) => { _db.EnqueueCommand("InstallAll"); RefreshCurrentView(); };
        _scanNowBtn = new Button { Text = "Check for Updates Now", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        UiTheme.StyleSecondaryButton(_scanNowBtn);
        _scanNowBtn.Click += (_, _) => { _db.EnqueueCommand("ScanNow"); RefreshCurrentView(); };
        updatesFooter.Controls.Add(_installSelectedBtn);
        updatesFooter.Controls.Add(_installAllBtn);
        updatesFooter.Controls.Add(_scanNowBtn);

        _updatesView.Controls.Add(gridWrap);
        _updatesView.Controls.Add(updatesFooter);
        _updatesView.Controls.Add(_headerMeta);
        _updatesView.Controls.Add(_headerTitle);

        // ── Content: Installation Status view ───────────────────────
        _historyView = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.CardBackground, Visible = false };
        var historyTitle = new Label { Text = "Installation Status", Font = UiTheme.Base(13f, FontStyle.Bold), ForeColor = UiTheme.TextPrimary, AutoSize = true, Location = new Point(20, 16) };
        var historyMeta = new Label
        {
            Text = "Every update this agent has installed, with the outcome.",
            Font = UiTheme.Base(8.5f), ForeColor = UiTheme.TextSecondary,
            AutoSize = false, Location = new Point(20, 42), Size = new Size(600, 20),
        };
        _historyList = new FlowLayoutPanel
        {
            Location = new Point(0, 74), Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(20, 0, 20, 0),
        };
        var historyWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 74, 0, 0) };
        historyWrap.Controls.Add(_historyList);
        _historyView.Controls.Add(historyWrap);
        _historyView.Controls.Add(historyMeta);
        _historyView.Controls.Add(historyTitle);

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_updatesView);
        content.Controls.Add(_historyView);

        Controls.Add(content);
        Controls.Add(sidebar);
        Controls.Add(header);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _refreshTimer.Tick += (_, _) => RefreshCurrentView();
        _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Stop();

        ShowUpdates();
    }

    private Panel BuildNavItem(string text)
    {
        var row = new Panel { Height = 40, Dock = DockStyle.Top, Cursor = Cursors.Hand };
        var label = new Label { Text = text, Font = UiTheme.Base(9f, FontStyle.Bold), ForeColor = UiTheme.TextPrimary, AutoSize = true, Location = new Point(14, 11) };
        row.Controls.Add(label);
        row.Tag = label;
        return row;
    }

    private void SetNavActive(Panel active, Panel inactive)
    {
        active.BackColor = UiTheme.Accent;
        ((Label)active.Tag!).ForeColor = Color.White;
        inactive.BackColor = Color.Transparent;
        ((Label)inactive.Tag!).ForeColor = UiTheme.TextPrimary;
    }

    private void ShowUpdates()
    {
        _showingHistory = false;
        SetNavActive(_navUpdates, _navHistory);
        _updatesView.Visible = true;
        _updatesView.BringToFront();
        _historyView.Visible = false;
        RefreshCurrentView();
    }

    private void ShowHistory()
    {
        _showingHistory = true;
        SetNavActive(_navHistory, _navUpdates);
        _historyView.Visible = true;
        _historyView.BringToFront();
        _updatesView.Visible = false;
        RefreshCurrentView();
    }

    private void InstallSelected()
    {
        var ids = new List<string>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells["col_check"].Value is true) ids.Add((string)row.Tag!);
        }
        if (ids.Count == 0)
        {
            MessageBox.Show(this, "Check at least one update first.", "Software Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _db.EnqueueCommand("InstallSelected", string.Join(",", ids));
        RefreshCurrentView();
    }

    private void RefreshCurrentView()
    {
        var updates = _db.GetUpdates();
        var status = _db.GetStatus();

        int pendingCount = updates.Count(u => u.Status is "Pending" or "ReadyToInstall" or "Failed");
        _navUpdatesBadge.Text = pendingCount > 0 ? pendingCount.ToString() : "";
        _navUpdatesBadge.Visible = pendingCount > 0;
        _navUpdatesBadge.BackColor = _showingHistory ? UiTheme.Danger : Color.White;
        _navUpdatesBadge.ForeColor = _showingHistory ? Color.White : UiTheme.Accent;
        _navUpdatesBadge.Padding = new Padding(6, 1, 6, 1);
        _navUpdatesBadge.Region = new Region(RoundedRect(_navUpdatesBadge.ClientRectangle, 8));
        PositionBadge();

        if (_showingHistory) RefreshHistoryList();
        else RefreshUpdatesGrid(updates, status);
    }

    private void PositionBadge()
    {
        var updatesLabel = (Label)_navUpdates.Tag!;
        _navUpdatesBadge.Location = new Point(updatesLabel.Right + 8, 13);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void RefreshUpdatesGrid(List<UpdateRecord> updates, AgentStatus status)
    {
        var previouslyChecked = new HashSet<string>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells["col_check"].Value is true) previouslyChecked.Add((string)row.Tag!);
        }

        _grid.Rows.Clear();
        foreach (var u in updates)
        {
            int i = _grid.Rows.Add(previouslyChecked.Contains(u.UpdateId), u.Title, u.KbArticle ?? "", u.Severity ?? "", u.SizeMb?.ToString("0.#") ?? "", u.Status);
            _grid.Rows[i].Tag = u.UpdateId;
            if (u.Status == "Failed") _grid.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Danger;
            else if (u.Status == "Installing" || u.Status == "Downloading") _grid.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Warning;
        }

        var parts = new List<string>();
        parts.Add(status.LastScanAt.HasValue ? $"Last checked: {status.LastScanAt.Value.ToLocalTime():g}" : "Not checked yet");
        if (status.ScanInProgress) parts.Add("checking now…");
        if (status.InstallInProgress) parts.Add("installing…");
        if (status.PendingReboot) parts.Add("RESTART REQUIRED");
        if (!string.IsNullOrEmpty(status.LastError)) parts.Add($"Last error: {status.LastError}");
        _headerMeta.Text = string.Join("   ·   ", parts);

        bool busy = status.ScanInProgress || status.InstallInProgress;
        _installAllBtn.Enabled = !busy && updates.Any(u => u.Status is "Pending" or "ReadyToInstall" or "Failed");
        _installSelectedBtn.Enabled = !busy;
        _scanNowBtn.Enabled = !busy;
    }

    private void RefreshHistoryList()
    {
        var history = _db.GetHistory();
        _historyList.SuspendLayout();
        _historyList.Controls.Clear();

        if (history.Count == 0)
        {
            _historyList.Controls.Add(new Label { Text = "Nothing installed yet.", ForeColor = UiTheme.TextSecondary, Font = UiTheme.Base(9f), AutoSize = true, Margin = new Padding(0, 20, 0, 0) });
        }
        else
        {
            foreach (var h in history)
            {
                _historyList.Controls.Add(BuildHistoryRow(h));
            }
        }
        _historyList.ResumeLayout();
    }

    private Panel BuildHistoryRow(HistoryEntry h)
    {
        var row = new Panel { Width = _historyList.ClientSize.Width - 4, Height = 40, Margin = new Padding(0, 0, 0, 0) };
        row.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        var divider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = UiTheme.Border };
        var pill = new Label
        {
            Text = h.Status,
            Font = UiTheme.Base(7.5f, FontStyle.Bold),
            Location = new Point(0, 10),
            AutoSize = true,
            Padding = new Padding(8, 3, 8, 3),
            BackColor = h.Status == "Failed" ? Color.FromArgb(0xFD, 0xEA, 0xEA) : Color.FromArgb(0xE7, 0xF6, 0xED),
            ForeColor = h.Status == "Failed" ? UiTheme.Danger : UiTheme.Success,
        };
        var title = new Label
        {
            Text = h.KbArticle != null ? $"{h.Title} ({h.KbArticle})" : h.Title,
            Font = UiTheme.Base(8.75f), ForeColor = UiTheme.TextPrimary,
            AutoSize = false, Location = new Point(96, 11), Height = 20,
            Width = row.Width - 96 - 140, AutoEllipsis = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        var when = new Label
        {
            Text = h.OccurredAt.ToLocalTime().ToString("g"),
            Font = UiTheme.Base(8f), ForeColor = UiTheme.TextSecondary,
            AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        row.Controls.Add(when);
        row.Controls.Add(title);
        row.Controls.Add(pill);
        row.Controls.Add(divider);
        row.Layout += (_, _) => { when.Location = new Point(row.Width - when.Width, 12); };
        if (!string.IsNullOrEmpty(h.Detail)) new ToolTip().SetToolTip(title, h.Detail);
        return row;
    }
}
