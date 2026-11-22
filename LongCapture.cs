using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>
/// Hosszú kép rögzítése: az app maga görgeti lefelé a kijelölés alatti ablakot, lépésenként.
/// Minden lépés után megvárja, hogy a tartalom megálljon, rögzít, illeszt és hozzáfűz.
/// Leáll az oldal végén (nem mozdul tovább), a magassági korlátnál, vagy Kész / Enter / Esc gombra.
///
/// Ez az ablak csak a keretet, az előnézetet és a gombokat rajzolja, pixelenként átlátszó rétegként,
/// és kimarad a képernyőképből (WDA_EXCLUDEFROMCAPTURE), így nem kerül rá a képre.
/// A kész kép a szerkesztőbe megy, ott lehet pontosan vágni és rajzolni.
/// </summary>
internal sealed class LongCapture : Form
{
    public event Action<LongCapture, Bitmap>? Finished;
    public event Action? Cancelled;

    public System.Windows.Forms.Screen TargetScreen { get; }

    // ---- Finomhangolható konstansok ----
    public const int MaxHeight = 25000;     // a kész kép felső korlátja (px)
    private const int TickMs = 70;          // ellenőrzés gyakorisága várakozás közben
    private const int SettleTimeoutMs = 1500;   // ennyi után akkor is továbblép, ha még mozog (pl. animáció)
    private const int MinSettleMs = 180;    // görgetés után legalább ennyit vár (sima görgetés, betöltés)

    private enum State { Start, Scroll, Settle, Done }

    private readonly float S;
    private readonly int W, H;
    private readonly Rectangle _band;        // a rögzített sáv a képernyőn (helyi koordináta)
    private readonly LongStitcher _stitch;   // fejléc / oldalsáv / lábléc felismerése és összefűzés
    private readonly IntPtr _target;
    private readonly Point _targetPt;

    private State _state = State.Start;
    private readonly LongController _ctl;    // lépésméret, hibajavítás, leállás
    private long _settleStart;
    private long[]? _lastSig;
    private string _status = "Görgetés és rögzítés…";

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = TickMs };
    private readonly List<(string id, Rectangle r)> _buttons = new();
    private string? _hover;

    public LongCapture(System.Windows.Forms.Screen screen, Rectangle sel, IntPtr target, Point targetPt)
    {
        TargetScreen = screen;
        S = Ui.ScaleFor(screen.Bounds);
        W = screen.Bounds.Width;
        H = screen.Bounds.Height;
        _target = target;
        _targetPt = targetPt;

        // A sáv: a kijelölés, a munkaterületen belül (a tálca sosem tartalom).
        // Ha túl alacsony, az illesztéshez a munkaterület teljes magasságát használjuk.
        var wa = new Rectangle(screen.WorkingArea.X - screen.Bounds.X, screen.WorkingArea.Y - screen.Bounds.Y,
                               screen.WorkingArea.Width, screen.WorkingArea.Height);
        var band = Rectangle.Intersect(sel, wa);
        if (band.Width < 40 * S) band = new Rectangle(wa.X, band.Y, wa.Width, band.Height);
        if (band.Height < 240 * S) band = new Rectangle(band.X, wa.Y, band.Width, wa.Height);
        _band = band;

        _stitch = new LongStitcher(W, _band, (int)(80 * S));
        _ctl = new LongController(_stitch, _band.Height);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = screen.Bounds;

        _timer.Tick += (_, _) => Step();
    }

    // ================= Réteges, kattintás-átengedő, képből kizárt ablak =================

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8;   // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;
    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, 0x11);   // WDA_EXCLUDEFROMCAPTURE
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = TargetScreen.Bounds;
        Present();
        _settleStart = Environment.TickCount64;
        _timer.Start();
    }

    // ================= A rögzítő ciklus =================

    private void Step()
    {
        switch (_state)
        {
            case State.Start:
                // Indulás: a fagyasztott réteg után kis szünet, majd az első (görgetés előtti) képkocka
                if (Environment.TickCount64 - _settleStart < 200) break;
                using (var first = CaptureBand()) _ctl.Start(first);
                _state = State.Scroll;
                break;

            case State.Scroll:
                if (!IsWindow(_target)) { _status = "Az ablak bezárult"; Finish(); return; }
                PostWheel(_ctl.WheelDelta);
                _settleStart = Environment.TickCount64;
                _lastSig = null;
                _state = State.Settle;
                break;

            case State.Settle:
                long waited = Environment.TickCount64 - _settleStart;
                using (var frame = CaptureBand())
                {
                    var sig = QuickSignature(frame);
                    bool stable = _lastSig != null && sig.AsSpan().SequenceEqual(_lastSig);
                    _lastSig = sig;
                    // Akkor dolgozzuk fel, ha két egymás utáni kép azonos (megállt), vagy letelt az idő
                    if ((stable && waited >= MinSettleMs) || waited >= SettleTimeoutMs)
                        Process(frame);
                }
                break;
        }
    }

    private void Process(Bitmap frame)
    {
        switch (_ctl.OnFrame(frame))
        {
            case LongController.Next.Finish:
                _status = _ctl.Status;
                Finish();
                return;
            case LongController.Next.Recheck:
                // Nem görget, csak kicsit később újranézi (betöltés, animáció)
                _settleStart = Environment.TickCount64;
                _lastSig = null;
                _state = State.Settle;
                break;
            default:
                _state = State.Scroll;
                break;
        }
        Present();
    }

    /// <summary>Napló a legutóbbi rögzítésről: %LOCALAPPDATA%\Programs\mini_screenshot\hosszu_kep.log</summary>
    private void WriteLog()
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Program.InstallPath)!, "hosszu_kep.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  vége: {_status}",
                $"sáv {_band}, törzs {_stitch.Body}, fejléc {_stitch.HeaderRows}, lábléc {_stitch.FooterRows}" };
            lines.AddRange(_ctl.Log);
            File.WriteAllLines(path, lines);
        }
        catch { /* a napló nem kritikus */ }
    }

    /// <summary>Gyors ujjlenyomat a "megállt-e már" vizsgálathoz (soronként néhány minta).</summary>
    private long[] QuickSignature(Bitmap frame)
    {
        var data = frame.LockBits(new Rectangle(_band.X, 0, _band.Width, frame.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int rows = frame.Height, step = Math.Max(1, _band.Width / 24);
            var sig = new long[rows];
            for (int y = 0; y < rows; y++)
            {
                long s = 0;
                for (int x = 0; x < _band.Width; x += step)
                    s = s * 31 + Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4);
                sig[y] = s;
            }
            return sig;
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }

    private Bitmap CaptureBand()
    {
        var b = TargetScreen.Bounds;
        var bmp = new Bitmap(W, _band.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.X, b.Y + _band.Top, 0, 0, new Size(W, _band.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    private void PostWheel(int delta)
    {
        var wParam = (IntPtr)(int)((uint)(ushort)(short)delta << 16);
        var lParam = (IntPtr)(((_targetPt.Y & 0xFFFF) << 16) | (_targetPt.X & 0xFFFF));
        PostMessage(_target, 0x020A, wParam, lParam);   // WM_MOUSEWHEEL
    }

    /// <summary>Leállítás és a kész kép átadása (Kész gomb, Enter, oldal vége, korlát).</summary>
    public void Finish()
    {
        if (_state == State.Done) return;
        _state = State.Done;
        _timer.Stop();
        WriteLog();
        var bmp = _stitch.Compose();
        Finished?.Invoke(this, bmp);
    }

    // ================= Egér: csak a gombok =================

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var b = _buttons.FirstOrDefault(x => x.r.Contains(e.Location));
        if (b.id != _hover) { _hover = b.id; Present(); }
        Cursor = b.id != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var b = _buttons.FirstOrDefault(x => x.r.Contains(e.Location));
        if (b.id == "done") Finish();
        else if (b.id == "cancel") { _state = State.Done; _timer.Stop(); Cancelled?.Invoke(); }
    }

    // ================= Rajzolás =================

    private void Present()
    {
        if (!IsHandleCreated || _state == State.Done) return;
        using var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // A rögzített sáv kerete (ugyanaz a fehér, mint a vágásnál)
            using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f * S))
                g.DrawRectangle(pen, _band);

            DrawPreview(g);
            DrawToolbar(g);
        }
        Layered.Update(this, bmp);
    }

    /// <summary>Jobb oldali előnézet az eddig rögzített képpel és a magassággal.</summary>
    private void DrawPreview(Graphics g)
    {
        // Az előnézet a görgő törzset mutatja, a számláló a kész kép teljes magasságát
        int bw = _band.Width, total = _stitch.TotalHeight;
        int bh = _stitch.Calibrated ? total - _stitch.HeaderRows - _stitch.FooterRows : _band.Height;
        if (bw <= 0 || bh <= 0) return;
        float maxW = 70 * S, maxH = H * 0.55f;
        float scale = Math.Min(maxW / bw, maxH / bh);
        float pw = bw * scale, ph = bh * scale, pad = 6 * S, labelH = 26 * S;
        float x = W - 22 * S - pw, y = 22 * S + labelH;

        var bg = new RectangleF(x - pad, y - pad - labelH, pw + 2 * pad, ph + 2 * pad + labelH);
        using (var path = Ui.Rounded(bg, 10 * S))
        using (var b = new SolidBrush(Color.FromArgb(235, Ui.Bg)))
            g.FillPath(b, path);

        g.InterpolationMode = InterpolationMode.Bilinear;
        _stitch.DrawPreview(g, new RectangleF(x, y, pw, ph));

        string label = total.ToString("#,0", CultureInfo.GetCultureInfo("hu-HU")) + " px";
        using var font = new Font("Segoe UI Semibold", 11.5f * S, GraphicsUnit.Pixel);
        using var tb = new SolidBrush(Ui.Text);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, tb, new RectangleF(bg.X, bg.Y + 2 * S, bg.Width, labelH), sf);
    }

    /// <summary>Alul középen: ● állapot · [Kész (Enter)] [Mégse (Esc)]</summary>
    private void DrawToolbar(Graphics g)
    {
        _buttons.Clear();
        using var font = new Font("Segoe UI Semibold", 13 * S, GraphicsUnit.Pixel);
        using var stFont = new Font("Segoe UI", 12.5f * S, GraphicsUnit.Pixel);
        float h = 44 * S, pad = 5 * S, gap = 4 * S, bh = h - 2 * pad, dot = 8 * S;

        float stW = g.MeasureString(_status, stFont).Width + dot + 34 * S;
        float doneW = g.MeasureString("Kész  (Enter)", font).Width + 28 * S;
        float cancelW = g.MeasureString("Mégse  (Esc)", font).Width + 28 * S;
        float total = pad * 2 + stW + doneW + cancelW + gap * 2;
        float x = (W - total) / 2, y = H - h - 70 * S;

        var bg = new RectangleF(x, y, total, h);
        using (var path = Ui.Rounded(bg, h / 2))
        using (var b = new SolidBrush(Color.FromArgb(240, Ui.Bg)))
            g.FillPath(b, path);

        // Lüktető pont jelzi, hogy fut
        float cx = x + pad + 14 * S, cy = y + h / 2;
        int pulse = (int)(155 + 100 * Math.Abs(Math.Sin(Environment.TickCount64 / 250.0)));
        using (var db = new SolidBrush(Color.FromArgb(pulse, Ui.Accent)))
            g.FillEllipse(db, cx - dot / 2, cy - dot / 2, dot, dot);
        using (var mb = new SolidBrush(Ui.Text))
            g.DrawString(_status, stFont, mb, new PointF(cx + dot, cy - stFont.Height / 2f));

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        float bx = x + pad + stW + gap;
        foreach (var (id, text, accent, w) in new[] { ("done", "Kész  (Enter)", true, doneW), ("cancel", "Mégse  (Esc)", false, cancelW) })
        {
            var r = new RectangleF(bx, y + pad, w, bh);
            bx += w + gap;
            bool hover = _hover == id;
            Color? fill = accent ? (hover ? ControlPaint.Light(Ui.Accent, 0.25f) : Ui.Accent)
                                 : hover ? Color.FromArgb(45, 45, 52) : null;
            if (fill != null)
            {
                using var p = Ui.Rounded(r, bh / 2);
                using var fb = new SolidBrush(fill.Value);
                g.FillPath(fb, p);
            }
            using var tb = new SolidBrush(accent || hover ? Color.White : Color.FromArgb(200, 200, 210));
            g.DrawString(text, font, tb, r, sf);
            _buttons.Add((id, Rectangle.Round(r)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _stitch.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Réteges ablak frissítése pixelenkénti átlátszósággal (UpdateLayeredWindow).</summary>
internal static class Layered
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    public static void Update(Form f, Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { W = bmp.Width, H = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = f.Left, Y = f.Top };
            var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };   // AC_SRC_ALPHA
            UpdateLayeredWindow(f.Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);   // ULW_ALPHA
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
