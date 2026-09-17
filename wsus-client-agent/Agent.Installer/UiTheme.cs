namespace Agent.Installer;

/// <summary>
/// Small, self-contained flat/modern theme - default WinForms controls
/// (gray 3D borders, MS Sans Serif-era sizing) read as distinctly
/// dated, so every form in this project pulls its colors/fonts from
/// here instead of leaving controls at their defaults. Deliberately
/// duplicated from Agent.Tray's copy rather than shared via
/// Agent.Shared (a plain net8.0 library, not Windows-Forms-aware) -
/// same "small enough to just repeat" call as _get_winrm_env on the
/// VMInvDB side.
/// </summary>
internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(0xF6, 0xF7, 0xFB);
    public static readonly Color CardBackground = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE3, 0xE6, 0xEC);
    public static readonly Color TextPrimary = Color.FromArgb(0x1B, 0x1F, 0x27);
    public static readonly Color TextSecondary = Color.FromArgb(0x6B, 0x72, 0x80);
    public static readonly Color Accent = Color.FromArgb(0x2F, 0x6F, 0xED);
    public static readonly Color AccentHover = Color.FromArgb(0x25, 0x5D, 0xCC);
    public static readonly Color Success = Color.FromArgb(0x1E, 0xA5, 0x5A);
    public static readonly Color Warning = Color.FromArgb(0xE0, 0x8E, 0x0B);
    public static readonly Color Danger = Color.FromArgb(0xD9, 0x3B, 0x3B);
    public static readonly Color RowAlternate = Color.FromArgb(0xFA, 0xFB, 0xFD);
    public static readonly Color ConsoleBackground = Color.FromArgb(0x14, 0x16, 0x1A);
    public static readonly Color ConsoleText = Color.FromArgb(0xC9, 0xD1, 0xD9);

    public static Font Base(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style);

    public static Font Heading(float size = 16f) => Base(size, FontStyle.Bold);

    public static void StylePrimaryButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = Base(9.5f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        b.Height = 36;
        b.Padding = new Padding(16, 0, 16, 0);
        b.MouseEnter += (_, _) => { if (b.Enabled) b.BackColor = AccentHover; };
        b.MouseLeave += (_, _) => { if (b.Enabled) b.BackColor = Accent; };
        b.EnabledChanged += (_, _) => b.BackColor = b.Enabled ? Accent : Border;
    }

    public static void StyleDangerButton(Button b)
    {
        StylePrimaryButton(b);
        b.BackColor = Danger;
        b.MouseEnter += (_, _) => { if (b.Enabled) b.BackColor = Color.FromArgb(0xB8, 0x2E, 0x2E); };
        b.MouseLeave += (_, _) => { if (b.Enabled) b.BackColor = Danger; };
    }

    public static void StyleSecondaryButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = Color.White;
        b.ForeColor = TextPrimary;
        b.Font = Base();
        b.Cursor = Cursors.Hand;
        b.Height = 36;
        b.Padding = new Padding(16, 0, 16, 0);
        b.MouseEnter += (_, _) => b.BackColor = RowAlternate;
        b.MouseLeave += (_, _) => b.BackColor = Color.White;
    }

    public static void StyleTextBox(TextBox t)
    {
        t.BorderStyle = BorderStyle.None;
        t.Font = Base(10f);
        t.ForeColor = TextPrimary;
        t.BackColor = Color.White;
    }

    public static Panel BorderedInput(TextBox textBox)
    {
        StyleTextBox(textBox);
        textBox.Dock = DockStyle.Fill;
        var panel = new Panel
        {
            BackColor = Color.White,
            Padding = new Padding(10, 8, 10, 8),
            BorderStyle = BorderStyle.FixedSingle,
        };
        panel.Controls.Add(textBox);
        return panel;
    }
}
