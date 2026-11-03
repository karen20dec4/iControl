using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Small on-screen overlay that mirrors the macOS indicator.
// The process is per-monitor DPI aware (see iControl.manifest), so the layout,
// drawn in 96-DPI units, is scaled by hand; the point-sized font scales by itself.
sealed class Osd : Form
{
    const int HideAfterMs = 800;

    string label = "";
    int percent = -1; // below zero draws the label with no bar
    float scale = 0f; // resolved on first show, once the handle exists
    readonly System.Windows.Forms.Timer hideTimer;

    int S(int value)
    {
        return (int)Math.Round(value * scale);
    }

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.85;
        Size = new Size(280, 94);
        DoubleBuffered = true;
        Visible = false;

        hideTimer = new System.Windows.Forms.Timer();
        hideTimer.Interval = HideAfterMs;
        hideTimer.Tick += delegate { hideTimer.Stop(); Visible = false; };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public void ShowLevel(string text, int value)
    {
        label = text;
        percent = value;
        if (scale <= 0f)
        {
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size(S(280), S(94));
        }
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - S(80));
        if (!Visible) Show();
        TopMost = true;
        Invalidate();
        hideTimer.Stop();
        hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Black);

        string text = percent >= 0 ? label + "  " + percent + "%" : label;
        using (Font f = new Font("Segoe UI", 11f, FontStyle.Regular))
        using (Brush b = new SolidBrush(Color.White))
            g.DrawString(text, f, b, S(20), S(14));

        Rectangle track = new Rectangle(S(20), S(52), Width - S(40), S(14));
        using (Brush dim = new SolidBrush(Color.FromArgb(70, 70, 70)))
            g.FillRectangle(dim, track);

        if (percent > 0)
        {
            int w = (int)Math.Round(track.Width * (percent / 100.0));
            if (w > 0)
                using (Brush fill = new SolidBrush(Color.White))
                    g.FillRectangle(fill, new Rectangle(track.X, track.Y, w, track.Height));
        }
    }
}
