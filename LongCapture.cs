using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>
/// Hosszú kép felvétele: a kijelölt terület élő marad, a felhasználó maga görget benne
/// (egérgörgő, görgetősáv, billentyű – bármi), akár gyorsan is, vagy egérrel húzza (pásztázza)
/// a területet, pl. egy Miro táblát. Automatikus módban mindkét összerakó fut: amíg csak függőleges
/// a mozgás, görgetés az eredmény (rögzített fejléc / oldalsáv / lábléc kezelésével); amint
/// oldalirányú mozgás is volt, pásztázás (kétirányú kép, a mozdulatlan eszköztárak nélkül).
/// A mód az eszköztáron át kézzel is választható.
///
/// Két háttérszál dolgozik:
/// - a rögzítő szál kb. 30 ms-onként pillanatképet készít a területről (csak ha változott), és sorba teszi;
/// - az összefűző szál a sorból illeszti és hozzáfűzi a képkockákat: a rögzített részek (fejléc,
///   oldalsáv, lábléc) a helyükön maradnak, a görgő rész lefelé "kifolyik".
/// Így a rögzítés sosem vár az összefűzésre. Stop / Enter után a sor maradékát még feldolgozza,
/// majd a kész kép a szerkesztőbe megy. Mégse / Esc eldobja.
///
/// Ez az ablak csak a keretet, az előnézetet és az eszköztárat rajzolja, pixelenként átlátszó rétegként.
/// Az átlátszó részeken az egér (és a görgő) átmegy az alatta lévő ablakba, és a réteg kimarad
/// a képernyőképből (WDA_EXCLUDEFROMCAPTURE), így nem kerül rá a felvételre.
/// </summary>
internal sealed class LongCapture : Form
{
    public event Action<LongCapture, Bitmap>? Finished;
    public event Action? Cancelled;

    public System.Windows.Forms.Screen TargetScreen { get; }

    // ---- Finomhangolható konstansok ----
    private const int CaptureMs = 30;       // ilyen gyakran készül pillanatkép
    private const int StartDelayMs = 250;   // a fagyasztott réteg eltűnése után ennyit vár az első képkockával
    private const int UiMs = 110;           // a réteg legfeljebb ilyen gyakran rajzolódik újra
    private const int MaxQueue = 12;        // ennyi feldolgozatlan képkocka várhat (memória)
    private const int LostWarnMs = 700;     // ennyi ideig tartó illeszthetetlenség után szól

    private static readonly Color Rec = Color.FromArgb(239, 68, 68);
    private static readonly Color Warn = Color.FromArgb(245, 158, 11);

    private readonly float S;
    private readonly int W, H;
    private readonly Rectangle _band;        // a felvett sáv a képernyőn (helyi koordináta)
    private readonly object _lock = new();   // az összerakót az összefűző szál és a rajzolás is használja

    // ---- Mód: görgetés vagy pásztázás ----
    private enum Mode { Auto, Scroll, Pan }
    private IStitcher _stitch = null!;       // a működő összerakó (a sáv saját koordinátáiban, bal széle = 0)
    private LongStitcher? _scroll;           // görgetéses összerakó (automatikus módban párhuzamosan fut)
    private PanStitcher? _pan;               // pásztázó összerakó (automatikus módban párhuzamosan fut)
    private Mode _mode = Mode.Auto;
    private volatile int _switchTo = -1;     // a felhasználó módot váltott: újraindul a felvétel
    private const int PanMinSideways = 24;   // ennyi oldalirányú elmozdulás után a pásztázás az eredmény

    private readonly BlockingCollection<Bitmap> _queue = new(MaxQueue);
    private Thread? _capThread, _procThread;
    private volatile bool _stopCapture, _cancelled, _maxReached, _dirty;
    private long _lastProcessedAt, _lastMatchAt;
    private bool _finishing, _done, _blink;
    private int _frames, _queued;
    private string? _endStatus;
    private readonly List<string> _log = new();   // csak az összefűző szál írja (és a lezárás, utána)

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = UiMs };
    private readonly List<(string id, Rectangle r)> _buttons = new();
    private string? _hover;

    public LongCapture(System.Windows.Forms.Screen screen, Rectangle sel)
    {
        TargetScreen = screen;
        S = Ui.ScaleFor(screen.Bounds);
        W = screen.Bounds.Width;
        H = screen.Bounds.Height;

        // A sáv: a kijelölés, a munkaterületen belül (a tálca sosem tartalom).
        // Ha túl kicsi, az illesztéshez kibővítjük (szélességben, illetve magasságban a munkaterületre).
        var wa = new Rectangle(screen.WorkingArea.X - screen.Bounds.X, screen.WorkingArea.Y - screen.Bounds.Y,
                               screen.WorkingArea.Width, screen.WorkingArea.Height);
        var band = Rectangle.Intersect(sel, wa);
        if (band.Width < 40 * S) band = new Rectangle(wa.X, band.Y, wa.Width, band.Height);
        if (band.Height < 120 * S) band = new Rectangle(band.X, wa.Y, band.Width, wa.Height);
        _band = band;

        // A képkocka csak a sáv (nem a teljes képernyőszélesség): kevesebb másolás, gyorsabb rögzítés
        Reset(Mode.Auto);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = screen.Bounds;

        _timer.Tick += (_, _) => UiTick();
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
        _capThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Hosszú kép: rögzítés", Priority = ThreadPriority.AboveNormal };
        _procThread = new Thread(ProcessLoop) { IsBackground = true, Name = "Hosszú kép: összefűzés" };
        _capThread.Start();
        _procThread.Start();
        _timer.Start();
    }

    // ================= Rögzítés (háttérszál) =================

    private void CaptureLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Thread.Sleep(StartDelayMs);
        long[]? lastSig = null;
        try
        {
            while (!_stopCapture)
            {
                long t0 = sw.ElapsedMilliseconds;
                // Ha az összefűzés lemaradt, nem halmozunk: a következő körben az aktuális állapotot vesszük fel
                if (_queue.Count < MaxQueue)
                {
                    var frame = CaptureBand();
                    var sig = QuickSignature(frame);
                    if (lastSig == null || !sig.AsSpan().SequenceEqual(lastSig))
                    {
                        lastSig = sig;
                        _queue.Add(frame);
                        Interlocked.Increment(ref _queued);
                    }
                    else frame.Dispose();
                }
                int wait = CaptureMs - (int)(sw.ElapsedMilliseconds - t0);
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch { /* a képernyő elérhetetlen (zárolás, UAC): a felvétel az eddigiekkel zárul */ }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    // ================= Összefűzés (háttérszál) =================

    private void ProcessLoop()
    {
        foreach (var frame in _queue.GetConsumingEnumerable())
        {
            using (frame)
            {
                if (_cancelled || _maxReached) continue;
                LongStitcher.Result r;
                string line;
                lock (_lock)
                {
                    if (_switchTo >= 0)
                    {
                        Reset((Mode)_switchTo);
                        _switchTo = -1;
                        _log.Add($"mód váltva: {ModeName}, újraindul");
                    }
                    r = AddFrame(frame);
                    line = $"#{++_frames} → {r}, {_stitch.FrameInfo}";
                }
                _log.Add(line);
                long now = Environment.TickCount64;
                Volatile.Write(ref _lastProcessedAt, now);
                if (r == LongStitcher.Result.Continue) Volatile.Write(ref _lastMatchAt, now);
                if (r == LongStitcher.Result.Max) _maxReached = true;
                _dirty = true;
            }
        }
    }

    /// <summary>Új felvétel a megadott móddal (a zárat a hívó tartja, vagy még nincs szál).</summary>
    private void Reset(Mode mode)
    {
        _scroll?.Dispose();
        _pan?.Dispose();
        _mode = mode;
        _scroll = mode == Mode.Pan ? null : new LongStitcher(_band.Width, new Rectangle(0, _band.Top, _band.Width, _band.Height), S);
        _pan = mode == Mode.Scroll ? null : new PanStitcher(_band.Width, _band.Height);
        _stitch = (IStitcher?)_scroll ?? _pan!;
    }

    /// <summary>
    /// Automatikus módban mindkét összerakó megkapja a képkockát. Amíg csak függőleges a mozgás,
    /// a görgetéses az eredmény; amint a nézet oldalra is elmozdult, a pásztázó (és a görgetéses leáll).
    /// </summary>
    private LongStitcher.Result AddFrame(Bitmap frame)
    {
        var rs = _scroll?.Add(frame) ?? LongStitcher.Result.Continue;
        var rp = _pan?.Add(frame) ?? LongStitcher.Result.Continue;
        if (_mode == Mode.Auto && _scroll != null && _pan!.SidewaysReach >= PanMinSideways)
        {
            _stitch = _pan;
            _scroll.Dispose();
            _scroll = null;
            _log.Add("mód: pásztázás (oldalirányú mozgás)");
        }
        return ReferenceEquals(_stitch, _pan) ? rp : rs;
    }

    private bool IsPan => _stitch is PanStitcher;

    private string ModeName => (_mode == Mode.Auto ? "automatikus → " : "") + (IsPan ? "pásztázás" : "görgetés");

    private bool Lost => Volatile.Read(ref _lastProcessedAt) - Volatile.Read(ref _lastMatchAt) > LostWarnMs;

    private string Status =>
        _finishing ? "Összefűzés…"
        : IsPan ? (Lost ? "Nem találom a helyet – húzd vissza egy kicsit"
                              : !_stitch.Calibrated ? "Húzd a területet (pásztázás)" : "Húzd tovább, a végén Stop")
        : Lost ? "Nem találom a helyet – görgess vissza egy kicsit"
        : !_stitch.Calibrated ? (_mode == Mode.Auto ? "Görgess vagy húzd a területet" : "Görgess a kijelölt területen")
        : "Görgess, a végén Stop";

    /// <summary>Gyors ujjlenyomat a "változott-e" vizsgálathoz (soronként néhány minta).</summary>
    private static long[] QuickSignature(Bitmap frame)
    {
        var data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int rows = frame.Height, step = Math.Max(1, frame.Width / 64);
            var sig = new long[rows];
            for (int y = 0; y < rows; y++)
            {
                long s = 0;
                for (int x = 0; x < frame.Width; x += step)
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
        var bmp = new Bitmap(_band.Width, _band.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.X + _band.X, b.Y + _band.Top, 0, 0, _band.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    // ================= Lezárás =================

    /// <summary>
    /// Stop gomb / Enter: a rögzítés leáll, a sorban várakozó képkockák még bekerülnek,
    /// majd a kész kép a szerkesztőbe megy.
    /// </summary>
    public void Finish()
    {
        if (_finishing || _done) return;
        _finishing = true;
        _endStatus ??= "Stop";
        _stopCapture = true;
        Present();
        Task.Run(() =>
        {
            _capThread?.Join();
            _procThread?.Join();
            Bitmap bmp;
            lock (_lock)
            {
                if (_cancelled) return;
                WriteLog();
                bmp = _stitch.Compose();
            }
            try
            {
                BeginInvoke(() =>
                {
                    if (_cancelled || IsDisposed) { bmp.Dispose(); return; }
                    _done = true;
                    _timer.Stop();
                    Finished?.Invoke(this, bmp);
                });
            }
            catch (InvalidOperationException) { bmp.Dispose(); }   // közben bezárták
        });
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        StopThreads();
        Cancelled?.Invoke();
    }

    /// <summary>Mégse: a szálak leállnak, a sor maradéka feldolgozás nélkül kiürül.</summary>
    private void StopThreads()
    {
        _cancelled = true;
        _stopCapture = true;
        _timer.Stop();
        _capThread?.Join(2000);
        _procThread?.Join(2000);
    }

    private void UiTick()
    {
        if (_done) return;
        if (_maxReached && !_finishing) { _endStatus = "Elérte a magassági korlátot"; Finish(); }
        bool blink = Environment.TickCount64 / 500 % 2 == 0;
        if (blink != _blink) { _blink = blink; _dirty = true; }
        if (_dirty) Present();
    }

    /// <summary>Napló a legutóbbi felvételről: %LOCALAPPDATA%\Programs\mini_screenshot\hosszu_kep.log</summary>
    private void WriteLog()
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Program.InstallPath)!, "hosszu_kep.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  vége: {_endStatus}",
                $"sáv {_band} (képernyőn), mód: {ModeName}",
                _stitch.Summary,
                $"rögzített képkocka {_queued}, feldolgozott {_frames}" };
            lines.AddRange(_log);
            File.WriteAllLines(path, lines);
        }
        catch { /* a napló nem kritikus */ }
    }

    // ================= Egér: csak a gombok =================

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var b = _buttons.FirstOrDefault(x => x.r.Contains(e.Location));
        if (b.id != _hover) { _hover = b.id; Present(); }
        Cursor = b.id != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { _hover = null; Present(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_finishing) return;
        var b = _buttons.FirstOrDefault(x => x.r.Contains(e.Location));
        if (b.id == "stop") Finish();
        else if (b.id == "cancel") Cancel();
        else if (b.id == "mode")
        {
            // Körbe: automatikus → görgetés → pásztázás; a felvétel a mostani nézettől újraindul
            _switchTo = (int)(_mode switch { Mode.Auto => Mode.Scroll, Mode.Scroll => Mode.Pan, _ => Mode.Auto });
            _dirty = true;
        }
    }

    // ================= Rajzolás =================

    private void Present()
    {
        if (!IsHandleCreated || _done || IsDisposed) return;
        _dirty = false;
        using var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // A felvett terület kerete, kicsit kívül, hogy ne takarja a tartalmat (és a görgetősávot)
            var col = Lost && !_finishing ? Warn : Rec;
            using (var pen = new Pen(Color.FromArgb(230, col), 2 * S))
                g.DrawRectangle(pen, Rectangle.Inflate(_band, (int)(2 * S), (int)(2 * S)));

            // Lezárás közben az összefűzés fogja a zárat: az előnézet ilyenkor kimarad
            if (Monitor.TryEnter(_lock, _finishing ? 0 : 200))
            {
                try { DrawPreview(g); }
                finally { Monitor.Exit(_lock); }
            }
            DrawToolbar(g);
        }
        Layered.Update(this, bmp);
    }

    /// <summary>Az eddig felvett kép kicsinyítve, a magassággal: a kijelölés mellett, ha van hely.</summary>
    private void DrawPreview(Graphics g)
    {
        var ps = _stitch.PreviewSize;
        int bw = ps.Width, bh = ps.Height;
        if (bw <= 0 || bh <= 0) return;
        float maxW = (IsPan ? 220 : 70) * S, maxH = H * 0.55f;
        float scale = Math.Min(maxW / bw, maxH / bh);
        float pw = bw * scale, ph = bh * scale, pad = 6 * S, labelH = 26 * S, gap = 16 * S;

        float x, y = Math.Max(22 * S, _band.Top) + labelH;
        if (W - _band.Right >= pw + 2 * pad + 2 * gap) x = _band.Right + gap + pad;           // jobbra mellette
        else if (_band.Left >= pw + 2 * pad + 2 * gap) x = _band.Left - gap - pad - pw;       // balra mellette
        else { x = W - 22 * S - pw; y = 22 * S + labelH; }                                    // jobb felső sarok
        y = Math.Min(y, H - ph - pad - 22 * S);

        var bg = new RectangleF(x - pad, y - pad - labelH, pw + 2 * pad, ph + 2 * pad + labelH);
        using (var path = Ui.Rounded(bg, 10 * S))
        using (var b = new SolidBrush(Color.FromArgb(235, Ui.Bg)))
            g.FillPath(b, path);

        g.InterpolationMode = InterpolationMode.Bilinear;
        _stitch.DrawPreview(g, new RectangleF(x, y, pw, ph));

        string label = _stitch.SizeLabel;
        using var font = new Font("Segoe UI Semibold", 11.5f * S, GraphicsUnit.Pixel);
        using var tb = new SolidBrush(Ui.Text);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, tb, new RectangleF(bg.X, bg.Y + 2 * S, bg.Width, labelH), sf);
    }

    /// <summary>A kijelölés alatt (vagy fölött): ● REC  állapot · [■ Stop (Enter)] [Mégse (Esc)]</summary>
    private void DrawToolbar(Graphics g)
    {
        _buttons.Clear();
        using var font = new Font("Segoe UI Semibold", 13 * S, GraphicsUnit.Pixel);
        using var recFont = new Font("Segoe UI", 12.5f * S, FontStyle.Bold, GraphicsUnit.Pixel);
        using var stFont = new Font("Segoe UI", 12.5f * S, GraphicsUnit.Pixel);
        float h = 44 * S, pad = 5 * S, gap = 4 * S, bh = h - 2 * pad, dot = 10 * S, outer = 14 * S;

        const string recText = "REC";
        string status = Status;
        float recW = g.MeasureString(recText, recFont).Width;
        float stW = 14 * S + dot + 6 * S + recW + 6 * S + g.MeasureString(status, stFont).Width + 14 * S;
        string modeText = _mode switch
        {
            Mode.Pan => "Mód: pásztázás",
            Mode.Scroll => "Mód: görgetés",
            _ => IsPan ? "Mód: automatikus (pásztázás)" : "Mód: automatikus",
        };
        float modeW = g.MeasureString(modeText, stFont).Width + 24 * S;
        float stopW = 18 * S + g.MeasureString("Stop  (Enter)", font).Width + 30 * S;
        float cancelW = g.MeasureString("Mégse  (Esc)", font).Width + 28 * S;
        float total = pad * 2 + stW + modeW + stopW + cancelW + gap * 3;

        // Helye: a kijelölés alatt; ha ott nincs hely, fölötte; végső esetben a kijelölésen belül alul
        float x = Math.Clamp(_band.Left + (_band.Width - total) / 2, outer, Math.Max(outer, W - total - outer));
        float y = _band.Bottom + outer;
        if (y + h > H - outer) y = _band.Top - h - outer;
        if (y < outer) y = _band.Bottom - h - outer;

        var bg = new RectangleF(x, y, total, h);
        using (var path = Ui.Rounded(bg, h / 2))
        using (var b = new SolidBrush(Color.FromArgb(240, Ui.Bg)))
            g.FillPath(b, path);

        // Villogó piros pont + REC: a felvétel fut (figyelmeztetéskor narancs)
        var col = Lost && !_finishing ? Warn : Rec;
        float cx = x + pad + 14 * S, cy = y + h / 2;
        using (var db = new SolidBrush(Color.FromArgb(_blink || _finishing ? 255 : 90, col)))
            g.FillEllipse(db, cx, cy - dot / 2, dot, dot);
        float tx = cx + dot + 6 * S;
        using (var rb = new SolidBrush(col))
            g.DrawString(recText, recFont, rb, new PointF(tx, cy - recFont.Height / 2f));
        using (var mb = new SolidBrush(Ui.Text))
            g.DrawString(status, stFont, mb, new PointF(tx + recW + 6 * S, cy - stFont.Height / 2f));

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        float bx = x + pad + stW + gap;

        // Mód: kattintásra a másik módra vált (újraindul a felvétel)
        var mode = new RectangleF(bx, y + pad, modeW, bh);
        using (var p = Ui.Rounded(mode, bh / 2))
        using (var mf = new SolidBrush(_hover == "mode" ? Color.FromArgb(55, 55, 64) : Color.FromArgb(38, 38, 44)))
            g.FillPath(mf, p);
        using (var tb = new SolidBrush(_hover == "mode" ? Color.White : Color.FromArgb(200, 200, 210)))
            g.DrawString(modeText, stFont, tb, mode, sf);
        _buttons.Add(("mode", Rectangle.Round(mode)));
        bx += modeW + gap;

        // Stop: kitöltött gomb, benne egy négyzet ikon
        var stop = new RectangleF(bx, y + pad, stopW, bh);
        using (var p = Ui.Rounded(stop, bh / 2))
        using (var fb = new SolidBrush(_hover == "stop" ? ControlPaint.Light(Ui.Accent, 0.25f) : Ui.Accent))
            g.FillPath(fb, p);
        float sq = 10 * S, sqX = stop.X + 16 * S;
        using (var sp = Ui.Rounded(new RectangleF(sqX, stop.Y + (bh - sq) / 2, sq, sq), 2 * S))
        using (var wb = new SolidBrush(Color.White))
        {
            g.FillPath(wb, sp);
            g.DrawString("Stop  (Enter)", font, wb, new RectangleF(sqX + sq, stop.Y, stop.Right - sqX - sq, bh), sf);
        }
        _buttons.Add(("stop", Rectangle.Round(stop)));
        bx += stopW + gap;

        var cancel = new RectangleF(bx, y + pad, cancelW, bh);
        if (_hover == "cancel")
        {
            using var p = Ui.Rounded(cancel, bh / 2);
            using var hb = new SolidBrush(Color.FromArgb(45, 45, 52));
            g.FillPath(hb, p);
        }
        using (var tb = new SolidBrush(_hover == "cancel" ? Color.White : Color.FromArgb(200, 200, 210)))
            g.DrawString("Mégse  (Esc)", font, tb, cancel, sf);
        _buttons.Add(("cancel", Rectangle.Round(cancel)));
    }

    private bool _disposed;

    protected override void Dispose(bool disposing)
    {
        // Close() már felszabadítja az ablakot, utána a hívó még egyszer Dispose()-t hív: csak egyszer fusson
        if (disposing && !_disposed)
        {
            _disposed = true;
            StopThreads();
            lock (_lock)
            {
                _scroll?.Dispose();
                _pan?.Dispose();
            }
            while (_queue.TryTake(out var f)) f.Dispose();
            _queue.Dispose();
            _timer.Dispose();
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
