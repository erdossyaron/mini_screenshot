using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>Közös segédek: színek, kerekítés, DPI, animáció.</summary>
internal static class Ui
{
    public static readonly Color Bg = Color.FromArgb(24, 24, 27);
    public static readonly Color BgHover = Color.FromArgb(39, 39, 44);
    public static readonly Color Text = Color.FromArgb(244, 244, 245);
    public static readonly Color Muted = Color.FromArgb(140, 140, 150);
    public static readonly Color Accent = Color.FromArgb(99, 102, 241);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Windows 11 natív, élsimított lekerekítés és árnyék.</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2;      // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int dark = 1;       // sötét keret
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    public static float ScaleFor(Rectangle screenBounds)
    {
        var c = new Point(screenBounds.X + screenBounds.Width / 2, screenBounds.Y + screenBounds.Height / 2);
        var mon = MonitorFromPoint(c, 2);
        return GetDpiForMonitor(mon, 0, out uint x, out _) == 0 ? x / 96f : 1f;
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    /// <summary>Egyszerű időalapú animáció 0→1 között, ease-out görbével.</summary>
    public static void Animate(int ms, Action<double> step, Action? done = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = new System.Windows.Forms.Timer { Interval = 10 };
        t.Tick += (_, _) =>
        {
            double k = Math.Min(1, sw.ElapsedMilliseconds / (double)ms);
            step(1 - Math.Pow(1 - k, 3));
            if (k >= 1) { t.Stop(); t.Dispose(); done?.Invoke(); }
        };
        step(0);
        t.Start();
    }
}

/// <summary>Alap lebegő ablak: keret nélkül, mindig felül, tálcán nem látszik.</summary>
internal class FloatingForm : Form
{
    protected readonly float S;   // DPI-szorzó az adott kijelzőn

    protected FloatingForm(Rectangle screenBounds)
    {
        S = Ui.ScaleFor(screenBounds);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ui.Bg;
        DoubleBuffered = true;
        Opacity = 0;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW: Alt+Tab-ban se látszik
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.RoundCorners(Handle);
    }

    // A méretet mi számoljuk kijelzőnként, a WinForms ne skálázza át
    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    public void FadeClose()
    {
        if (IsDisposed) return;
        double from = Opacity;
        Ui.Animate(110, k => { if (!IsDisposed) Opacity = from * (1 - k); }, () => { if (!IsDisposed) Close(); });
    }
}

/// <summary>Jobb alsó sarokban felcsúszó előnézet. Kattintásra megmutatja a fájlt.</summary>
internal sealed class ToastForm : FloatingForm
{
    private readonly Bitmap _thumb;
    private readonly string _file;
    private readonly Rectangle _thumbRect;
    private readonly System.Windows.Forms.Timer _life = new() { Interval = 2200 };

    private ToastForm(System.Windows.Forms.Screen screen, Bitmap shot, string file) : base(screen.Bounds)
    {
        _file = file;
        int pad = (int)(8 * S);
        int tw = (int)(240 * S);
        int th = Math.Max(1, tw * shot.Height / shot.Width);
        _thumb = new Bitmap(tw, th);
        using (var g = Graphics.FromImage(_thumb))
        {
            Ui.Prepare(g);
            g.DrawImage(shot, 0, 0, tw, th);
        }
        shot.Dispose();

        _thumbRect = new Rectangle(pad, pad, tw, th);
        var size = new Size(tw + pad * 2, th + pad + (int)(34 * S));
        var wa = screen.WorkingArea;
        int m = (int)(20 * S);
        Bounds = new Rectangle(wa.Right - size.Width - m, wa.Bottom - size.Height - m, size.Width, size.Height);
        Cursor = Cursors.Hand;

        _life.Tick += (_, _) => { _life.Stop(); FadeClose(); };
    }

    protected override bool ShowWithoutActivation => true;

    public static void Show(System.Windows.Forms.Screen screen, Bitmap shot, string file)
    {
        var t = new ToastForm(screen, shot, file);
        t.Show();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        int top = Top;
        Ui.Animate(180, k => { Opacity = 0.98 * k; Top = top + (int)((1 - k) * 16 * S); });
        _life.Start();
    }

    // Egér fölötte: nem tűnik el
    protected override void OnMouseEnter(EventArgs e) => _life.Stop();
    protected override void OnMouseLeave(EventArgs e) => _life.Start();

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_file}\"");
        FadeClose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Prepare(g);
        using (var clip = Ui.Rounded(_thumbRect, 6 * S))
        {
            g.SetClip(clip);
            g.DrawImageUnscaled(_thumb, _thumbRect.Location);
            g.ResetClip();
        }

        int y = _thumbRect.Bottom;
        var row = new Rectangle(_thumbRect.X + (int)(2 * S), y, _thumbRect.Width, Height - y);
        using var font = new Font("Segoe UI", 12 * S, GraphicsUnit.Pixel);
        using var dot = new SolidBrush(Ui.Accent);
        float d = 7 * S;
        g.FillEllipse(dot, row.X + 2 * S, row.Y + (row.Height - d) / 2, d, d);
        TextRenderer.DrawText(g, "Mentve · vágólapon", font,
            new Rectangle(row.X + (int)(16 * S), row.Y, row.Width, row.Height), Ui.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _thumb.Dispose(); _life.Dispose(); }
        base.Dispose(disposing);
    }
}
