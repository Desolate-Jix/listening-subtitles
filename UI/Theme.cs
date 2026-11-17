namespace ListeningSubtitles.UI;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(20, 27, 38);
    public static readonly Color Surface = Color.FromArgb(32, 42, 56);
    public static readonly Color Ink = Color.FromArgb(242, 245, 250);
    public static readonly Color Muted = Color.FromArgb(167, 184, 201);
    public static readonly Color Mint = Color.FromArgb(112, 231, 195);
    public static void Apply(Form form)
    {
        form.Font = new Font("Microsoft YaHei UI", 10);
        form.BackColor = Background; form.ForeColor = Ink;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Icon = SystemIcons.Application;
    }
    public static Button Button(string text, EventHandler action, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new(76, 34), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Mint : Surface, ForeColor = primary ? Background : Ink, Margin = new(4), Padding = new(4) };
        button.FlatAppearance.BorderSize = 0;
        button.EnabledChanged += (_, _) => button.BackColor = button.Enabled ? (primary ? Mint : Surface) : Color.FromArgb(138, 152, 168);
        button.Click += action; return button;
    }
    public static RichTextBox TextArea(int size, Color color) => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false,
        BackColor = Background, ForeColor = color, Font = new Font("Microsoft YaHei UI", size),
        ScrollBars = RichTextBoxScrollBars.Vertical
    };
}
