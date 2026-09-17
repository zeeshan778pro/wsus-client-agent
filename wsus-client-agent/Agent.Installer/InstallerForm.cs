using System.Diagnostics;

namespace Agent.Installer;

/// <summary>
/// A thin GUI wrapper around install.ps1/uninstall.ps1 - deliberately
/// doesn't reimplement the actual install/uninstall steps in C#, so
/// there's only ever one place that logic lives and can drift. This
/// form's job is: detect whether the agent is already on this machine
/// and show the right action (Install vs. Uninstall), collect the one
/// thing an end user actually needs to provide (the WSUS server, for
/// install), then run the matching script with -SkipBuild baked in -
/// never exposed as something to type.
///
/// Expects, next to Setup.exe (this app's published output):
///   install.ps1, uninstall.ps1
///   Agent.Service\bin\Release\net8.0-windows\win-x64\publish\...
///   Agent.Tray\bin\Release\net8.0-windows\win-x64\publish\...
/// i.e. exactly what a `dotnet publish` of the whole solution followed
/// by copying this project's own publish output into the same root
/// folder produces - see README.md's packaging step.
/// </summary>
public class InstallerForm : Form
{
    private const string InstallMarkerPath = @"C:\Program Files\WsusClientAgent\Service\Agent.Service.exe";

    private readonly bool _alreadyInstalled;
    private readonly Label _subtitle;
    private readonly TextBox _wsusServerBox;
    private readonly CheckBox _removeDataCheck;
    private readonly CheckBox _removePolicyCheck;
    private readonly Panel _uninstallOptionsPanel;
    private readonly Button _actionBtn;
    private readonly Button _closeBtn;
    private readonly TextBox _log;
    private readonly Label _statusLabel;
    private readonly PictureBox _statusDot;

    public InstallerForm()
    {
        _alreadyInstalled = File.Exists(InstallMarkerPath);

        Text = "WSUS Client Agent - Setup";
        Width = 680;
        Height = 560;
        BackColor = UiTheme.Background;
        Font = UiTheme.Base();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        // ── Header ───────────────────────────────────────────────────
        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.White };
        var accentStripe = new Panel { Dock = DockStyle.Bottom, Height = 3, BackColor = UiTheme.Accent };
        var title = new Label
        {
            Text = "WSUS Client Agent",
            Font = UiTheme.Heading(),
            ForeColor = UiTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(24, 18),
        };
        _subtitle = new Label
        {
            Text = _alreadyInstalled
                ? "Already installed on this machine. Uninstall it below, or reinstall to point it at a different WSUS server."
                : "Installs the background service and tray app that pull approved updates from your WSUS server.",
            Font = UiTheme.Base(9f),
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Location = new Point(24, 48),
            Size = new Size(620, 34),
        };
        header.Controls.Add(title);
        header.Controls.Add(_subtitle);
        header.Controls.Add(accentStripe);

        // ── Body card ────────────────────────────────────────────────
        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background, Padding = new Padding(24, 18, 24, 0) };

        var formCard = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = UiTheme.CardBackground, Padding = new Padding(16) };
        formCard.Paint += (_, e) => DrawCardBorder(e, formCard);
        var wsusLabel = new Label
        {
            Text = "WSUS SERVER ADDRESS",
            Font = UiTheme.Base(7.5f, FontStyle.Bold),
            ForeColor = UiTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(16, 6),
        };
        _wsusServerBox = new TextBox { PlaceholderText = "e.g. wsus01.company.local:8530 or 10.1.2.3:8530" };
        var wsusInputPanel = UiTheme.BorderedInput(_wsusServerBox);
        wsusInputPanel.Location = new Point(16, 26);
        wsusInputPanel.Size = new Size(formCard.Width - 32, 36);
        wsusInputPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        formCard.Controls.Add(wsusInputPanel);
        formCard.Controls.Add(wsusLabel);

        var hint = new Label
        {
            Text = "Ask your IT admin if you're not sure. Typically port 8530 (or 8531 for HTTPS).",
            Dock = DockStyle.Top,
            Height = 28,
            Font = UiTheme.Base(8f),
            ForeColor = UiTheme.TextSecondary,
        };

        _uninstallOptionsPanel = new Panel { Dock = DockStyle.Top, Height = _alreadyInstalled ? 30 : 0, Visible = _alreadyInstalled };
        _removeDataCheck = new CheckBox { Text = "Also remove local update history/settings", AutoSize = true, Font = UiTheme.Base(8.5f), ForeColor = UiTheme.TextPrimary, Location = new Point(0, 4) };
        _removePolicyCheck = new CheckBox { Text = "Also remove WSUS/Windows Update policy", AutoSize = true, Font = UiTheme.Base(8.5f), ForeColor = UiTheme.TextPrimary, Location = new Point(280, 4) };
        _uninstallOptionsPanel.Controls.Add(_removeDataCheck);
        _uninstallOptionsPanel.Controls.Add(_removePolicyCheck);

        var logLabel = new Label { Text = "ACTIVITY LOG", Dock = DockStyle.Top, Height = 22, Font = UiTheme.Base(7.5f, FontStyle.Bold), ForeColor = UiTheme.TextSecondary, Padding = new Padding(0, 6, 0, 0) };

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Font = GetConsoleFont(),
            BackColor = UiTheme.ConsoleBackground,
            ForeColor = UiTheme.ConsoleText,
        };
        var logCard = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.ConsoleBackground, Padding = new Padding(12) };
        logCard.Controls.Add(_log);

        body.Controls.Add(logCard);
        body.Controls.Add(logLabel);
        body.Controls.Add(_uninstallOptionsPanel);
        body.Controls.Add(hint);
        body.Controls.Add(formCard);

        // ── Footer ───────────────────────────────────────────────────
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Color.White };
        var footerTop = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Border };
        _statusDot = new PictureBox { Size = new Size(8, 8), Location = new Point(24, 28), BackColor = Color.Transparent };
        _statusDot.Paint += (_, e) => e.Graphics.FillEllipse(new SolidBrush(UiTheme.TextSecondary), 0, 0, 8, 8);
        _statusLabel = new Label { Text = "Ready.", Font = UiTheme.Base(8.5f), ForeColor = UiTheme.TextSecondary, AutoSize = true, Location = new Point(38, 24) };

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 380, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 14, 20, 0) };
        _closeBtn = new Button { Text = "Close", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        UiTheme.StyleSecondaryButton(_closeBtn);
        _closeBtn.Click += (_, _) => Close();

        _actionBtn = new Button { Text = _alreadyInstalled ? "Uninstall" : "Install", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        if (_alreadyInstalled) UiTheme.StyleDangerButton(_actionBtn); else UiTheme.StylePrimaryButton(_actionBtn);
        _actionBtn.Click += async (_, _) =>
        {
            if (_alreadyInstalled) await RunUninstallAsync();
            else await RunInstallAsync();
        };

        buttonPanel.Controls.Add(_closeBtn);
        buttonPanel.Controls.Add(_actionBtn);

        if (_alreadyInstalled)
        {
            var reinstallBtn = new Button { Text = "Reinstall", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
            UiTheme.StylePrimaryButton(reinstallBtn);
            reinstallBtn.Click += async (_, _) => await RunInstallAsync();
            buttonPanel.Controls.Add(reinstallBtn);
        }

        footer.Controls.Add(buttonPanel);
        footer.Controls.Add(_statusLabel);
        footer.Controls.Add(_statusDot);
        footer.Controls.Add(footerTop);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
    }

    /// <summary>Windows silently substitutes a default font for an
    /// unrecognized family name rather than throwing - checking the
    /// resulting Font.Name against what was asked for is the only way
    /// to tell whether "Cascadia Mono" (bundled with Windows Terminal,
    /// not guaranteed present) actually resolved.</summary>
    private static Font GetConsoleFont()
    {
        var font = new Font("Cascadia Mono", 8.5f);
        return font.Name == "Cascadia Mono" ? font : new Font(FontFamily.GenericMonospace, 8.5f);
    }

    private static void DrawCardBorder(PaintEventArgs e, Control card)
    {
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
    }

    private async Task RunInstallAsync()
    {
        var wsusServer = _wsusServerBox.Text.Trim();
        if (string.IsNullOrEmpty(wsusServer))
        {
            MessageBox.Show(this, "Enter your WSUS server address first.", "WSUS Client Agent Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // A bare hostname/IP is a common thing to type - default to the
        // standard WSUS HTTP port so "wsus01.company.local" alone works
        // rather than silently failing later for missing a scheme.
        var wsusUrl = wsusServer.Contains("://") ? wsusServer : $"http://{wsusServer}:8530";

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "install.ps1");
        if (!File.Exists(scriptPath))
        {
            AppendLog($"ERROR: install.ps1 not found next to Setup.exe ({scriptPath}). This installer package is incomplete.");
            return;
        }

        await RunScriptAsync(
            scriptPath,
            $"-WsusServerUrl \"{wsusUrl}\" -SkipBuild",
            $"Installing WSUS Client Agent, pointed at {wsusUrl}",
            "Done. The tray icon should appear shortly for the current user, and on every future logon.",
            "WSUS Client Agent installed successfully.",
            "Installing");
    }

    private async Task RunUninstallAsync()
    {
        var confirm = MessageBox.Show(
            this,
            "Remove the WSUS Client Agent from this machine?",
            "WSUS Client Agent Setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "uninstall.ps1");
        if (!File.Exists(scriptPath))
        {
            AppendLog($"ERROR: uninstall.ps1 not found next to Setup.exe ({scriptPath}). This installer package is incomplete.");
            return;
        }

        var args = new List<string>();
        if (_removeDataCheck.Checked) args.Add("-RemoveData");
        if (_removePolicyCheck.Checked) args.Add("-RemovePolicy");

        await RunScriptAsync(
            scriptPath,
            string.Join(' ', args),
            "Uninstalling WSUS Client Agent...",
            "Done. The service, scheduled task, and installed files have been removed.",
            "WSUS Client Agent uninstalled.",
            "Uninstalling");
    }

    private async Task RunScriptAsync(string scriptPath, string scriptArgs, string startMessage, string successMessage, string successTitle, string progressVerb)
    {
        _actionBtn.Enabled = false;
        _closeBtn.Enabled = false;
        _wsusServerBox.Enabled = false;
        SetStatus(UiTheme.Accent, $"{progressVerb}...");
        _log.Clear();
        AppendLog(startMessage);
        AppendLog("");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {scriptArgs}",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        int exitCode;
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: Could not run {Path.GetFileName(scriptPath)} - {ex.Message}");
            exitCode = -1;
        }

        if (exitCode == 0)
        {
            SetStatus(UiTheme.Success, "Complete.");
            AppendLog("");
            AppendLog(successMessage);
            MessageBox.Show(this, successMessage, successTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            SetStatus(UiTheme.Danger, $"Failed (exit code {exitCode}) - see the log above.");
            MessageBox.Show(this, "This didn't complete successfully - see the log for details.", "WSUS Client Agent Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        _actionBtn.Enabled = true;
        _closeBtn.Enabled = true;
        _wsusServerBox.Enabled = true;
    }

    private void SetStatus(Color dotColor, string text)
    {
        _statusLabel.Text = text;
        _statusDot.Invalidate();
        _statusDot.Tag = dotColor;
        _statusDot.Paint -= PaintStatusDot;
        _statusDot.Paint += PaintStatusDot;
    }

    private void PaintStatusDot(object? sender, PaintEventArgs e)
    {
        var color = _statusDot.Tag as Color? ?? UiTheme.TextSecondary;
        e.Graphics.FillEllipse(new SolidBrush(color), 0, 0, 8, 8);
    }

    private void AppendLog(string line)
    {
        if (_log.InvokeRequired)
        {
            _log.Invoke(() => AppendLog(line));
            return;
        }
        _log.AppendText(line + Environment.NewLine);
    }
}
