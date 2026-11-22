using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>
/// A hosszú kép összerakása képkockákból (felület nélkül, ezért külön is tesztelhető).
///
/// 1. Kalibrálás: az első három képkockából (indulás + két görgetés) felismeri, mi nem mozdul:
///    fent a fejléc, lent a lábléc, oldalt a rögzített oldalsáv. A többi a görgő terület ("törzs").
/// 2. Illesztés és összefűzés csak a törzsben történik, így a rögzített részek nem zavarják.
/// 3. A kész kép: fejléc egyszer felül → a törzs "lefolyik" → lábléc egyszer alul.
///    Az oldalsáv csak az első képernyő magasságáig látszik, alatta a saját háttérszínével folytatódik.
/// </summary>
internal sealed class LongStitcher : IDisposable
{
    public enum Result { Continue, End, Lost, Gap, Max }

    public const int MaxHeight = 25000;

    private readonly int _w;                 // képkocka szélessége (a képernyő)
    private readonly Rectangle _band;        // a kijelölés a képernyőn; a képkocka = a sáv sorai, teljes szélességben
    private readonly int _minBody;

    private readonly List<Bitmap> _calib = new();
    private StitchCanvas? _canvas;
    private Rectangle _body;                 // görgő terület a képernyőn
    private Bitmap? _header, _footer, _sideFill, _sideFirst, _last;
    private FrameSig? _lastSig;
    private int _s, _still, _lastS, _guard;
    private bool _flushed;

    /// <summary>
    /// A törzs alsó ekkora részéből nem veszünk át sorokat menet közben: itt szoktak lebegni
    /// a gombok ("le a végére"), és itt van az elhalványítás a beviteli doboz fölött.
    /// A sorok később, feljebb görgetve kerülnek be; a legvégén egyszer ez a sáv is.
    /// </summary>
    private const double BottomGuardRatio = 0.3;

    public bool Calibrated => _canvas != null;
    public int Position => _s;
    public string MatchInfo => _canvas == null ? "" : $"szavazat {_canvas.LastVotes}/{_canvas.LastInformative}";
    public int LastDelta { get; private set; }
    public Rectangle Body => _body;
    public int HeaderRows => _body.Top - _band.Top;
    public int FooterRows => _band.Bottom - _body.Bottom;

    /// <summary>A kész kép várható magassága (az előnézethez és a számlálóhoz).</summary>
    public int TotalHeight => _canvas == null ? _band.Height : HeaderRows + _canvas.Height + FooterRows;

    public LongStitcher(int screenWidth, Rectangle band, int minBody)
    {
        _w = screenWidth;
        _band = band;
        _minBody = minBody;
        _body = band;
    }

    // ================= Képkockák feldolgozása =================

    /// <summary>A következő képkocka (az első az indulás, görgetés előtt).</summary>
    public Result Add(Bitmap frame)
    {
        if (_canvas == null)
        {
            _calib.Add(new Bitmap(frame));
            if (_calib.Count < 3) return Result.Continue;
            return Calibrate();
        }
        return Process(frame);
    }

    private Result Calibrate()
    {
        var (f0, f1, f2) = (_calib[0], _calib[1], _calib[2]);
        var r = new Rectangle(_band.X, 0, _band.Width, _band.Height);
        int[] a = Pixels(f0, r), b = Pixels(f1, r), c = Pixels(f2, r);
        int w = r.Width, h = r.Height;

        bool Same(int i) => a[i] == b[i] && b[i] == c[i];
        bool RowStatic(int y)
        {
            for (int x = 0, i = y * w; x < w; x += 2, i += 2) if (!Same(i)) return false;
            return true;
        }

        // Fejléc és lábléc: a sáv tetején és alján egybefüggően mozdulatlan sorok
        int hdr = 0;
        while (hdr < h && RowStatic(hdr)) hdr++;
        int ftr = 0;
        while (ftr < h - hdr && RowStatic(h - 1 - ftr)) ftr++;

        if (h - hdr - ftr < _minBody)
        {
            // Szinte semmi nem mozdult: nem görgethető → ami látszik, az a kép
            if (hdr >= h - 1) return Result.End;
            hdr = ftr = 0;
        }

        // Oldalsáv: a törzs sorain belül a bal és jobb szélen mozdulatlan oszlopok
        bool ColStatic(int x)
        {
            for (int y = hdr; y < h - ftr; y += 2) if (!Same(y * w + x)) return false;
            return true;
        }
        int left = 0;
        while (left < w && ColStatic(left)) left++;
        int right = 0;
        while (right < w - left && ColStatic(w - 1 - right)) right++;
        if (w - left - right < _minBody) left = right = 0;

        // Finomítás a görgő oszlopon belül: a csak ott rögzített sávok (pl. lebegő beviteli doboz alul,
        // ragadós címsor felül) is fejléc/lábléc. Kis tűréssel (villogó kurzor, apró ikon).
        bool RowStaticIn(int y)
        {
            int moving = 0, total = 0;
            for (int x = left, i = y * w + left; x < w - right; x += 2, i += 2)
            {
                total++;
                if (!Same(i) && ++moving > total / 50 + 2) return false;
            }
            return true;
        }
        int hdr2 = hdr, ftr2 = ftr;
        while (hdr2 < h - ftr2 && RowStaticIn(hdr2)) hdr2++;
        while (ftr2 < h - hdr2 && RowStaticIn(h - 1 - ftr2)) ftr2++;
        if (h - hdr2 - ftr2 >= _minBody) { hdr = hdr2; ftr = ftr2; }

        _body = Rectangle.FromLTRB(_band.Left + left, _band.Top + hdr, _band.Right - right, _band.Bottom - ftr);
        _guard = (int)(_body.Height * BottomGuardRatio);

        // Fejléc (teljes sávszélesség), az oldalsáv első képernyője és a folytatásához az utolsó sora
        _header = Crop(f0, new Rectangle(_band.X, 0, _band.Width, hdr));
        _sideFirst = Crop(f0, new Rectangle(_band.X, _body.Top - _band.Top, _band.Width, _body.Height));
        _sideFill = Crop(f0, new Rectangle(_band.X, _body.Bottom - _band.Top - 1, _band.Width, 1));

        // Az összefűzés a törzs oszlopaival illeszt; az első képernyő a görgetés = 0 állapot.
        // A törzs alsó sávjából (lebegő gombok, elhalványítás) még nem veszünk át sorokat.
        _canvas = new StitchCanvas(_w, _body.Left, _body.Right);
        var fs0 = _canvas.RowSignatures(f0);
        _canvas.PutRows(f0, _band.Top, 0, _body.Top, _body.Bottom - _guard, fs0);
        KeepLast(f0, fs0);
        UpdateFooter(f0);

        var res = Process(f1);
        if (res is Result.Continue or Result.Lost) res = Process(f2);

        foreach (var f in _calib) f.Dispose();
        _calib.Clear();
        return res;
    }

    private Result Process(Bitmap frame)
    {
        var c = _canvas!;
        var fs = c.RowSignatures(frame);
        int? m = c.Match(fs, _band.Top, _s, _band.Height * 2);
        if (m == null) return Result.Lost;

        int ds = m.Value - _s;
        LastDelta = ds;
        if (ds < 0)
        {
            // Visszagörgettünk (hibajavítás): csak a helyet frissítjük, ez nem "oldal vége"
            _s = m.Value;
            return Result.Continue;
        }
        if (ds == 0)
            return ++_still >= 3 ? Result.End : Result.Continue;   // háromszor nem mozdult → oldal vége
        _still = 0;
        _s = m.Value;

        // Új sorok csak a törzs biztonságos részéből (az alsó sáv nélkül), folytonosan az eddigi aljához
        int visTop = _s + _body.Top, visBot = _s + _body.Bottom - _guard;
        if (c.Bottom < visTop) return Result.Gap;
        if (visBot > c.Bottom)
            c.PutRows(frame, _band.Top, _s, c.Bottom, Math.Min(visBot, c.Top + MaxHeight), fs);

        KeepLast(frame, fs);
        UpdateFooter(frame);
        return TotalHeight >= MaxHeight ? Result.Max : Result.Continue;
    }

    /// <summary>Az utolsó képkocka megőrzése: a végén innen jön a törzs legalsó sávja.</summary>
    private void KeepLast(Bitmap frame, FrameSig fs)
    {
        _last?.Dispose();
        _last = new Bitmap(frame);
        _lastSig = fs;
        _lastS = _s;
    }

    /// <summary>A végén a törzs addig kihagyott alsó sávja is bekerül (egyszer, az utolsó képkockából).</summary>
    private void Flush()
    {
        if (_canvas == null || _last == null || _lastSig == null || _flushed) return;
        _flushed = true;
        int to = _lastS + _body.Bottom;
        if (to > _canvas.Bottom && _canvas.Bottom >= _lastS + _body.Top)
            _canvas.PutRows(_last, _band.Top, _lastS, _canvas.Bottom, Math.Min(to, _canvas.Top + MaxHeight), _lastSig);
    }

    /// <summary>A lábléc mindig a legutolsó képkockából jön (az oldal alja).</summary>
    private void UpdateFooter(Bitmap frame)
    {
        _footer?.Dispose();
        _footer = FooterRows > 0 ? Crop(frame, new Rectangle(_band.X, _body.Bottom - _band.Top, _band.Width, FooterRows)) : null;
    }

    // ================= A kész kép =================

    public Bitmap Compose()
    {
        if (_canvas == null)
        {
            // Nem jutott el a kalibrálásig: az utolsó képkocka sávja
            var last = _calib.Count > 0 ? _calib[^1] : null;
            return last != null ? Crop(last, new Rectangle(_band.X, 0, _band.Width, _band.Height)) : new Bitmap(1, 1);
        }

        Flush();
        int hdr = HeaderRows, ftr = FooterRows, bodyH = _canvas.Height, outW = _band.Width;
        var bmp = new Bitmap(outW, Math.Max(1, hdr + bodyH + ftr), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        if (_header != null && hdr > 0) g.DrawImageUnscaled(_header, 0, 0);

        using (var body = _canvas.Render(_canvas.Top, _canvas.Bottom, _band.X, outW))
            g.DrawImageUnscaled(body, 0, hdr);

        // Oldalsáv: az első képernyőn pontosan úgy, ahogy látszott (az indulókép), alatta a saját
        // utolsó sorával folytatódik (nem ismétlődik)
        int leftW = _body.Left - _band.Left, rightX = _body.Right - _band.Left, rightW = _band.Right - _body.Right;
        int firstScreen = Math.Min(_body.Height, bodyH), rest = bodyH - firstScreen;
        if (_sideFirst != null)
        {
            if (leftW > 0)
                g.DrawImage(_sideFirst, new Rectangle(0, hdr, leftW, firstScreen), new Rectangle(0, 0, leftW, firstScreen), GraphicsUnit.Pixel);
            if (rightW > 0)
                g.DrawImage(_sideFirst, new Rectangle(rightX, hdr, rightW, firstScreen), new Rectangle(rightX, 0, rightW, firstScreen), GraphicsUnit.Pixel);
        }
        if (rest > 0 && _sideFill != null)
        {
            int y0 = hdr + firstScreen;
            if (leftW > 0)
                g.DrawImage(_sideFill, new Rectangle(0, y0, leftW, rest), new Rectangle(0, 0, leftW, 1), GraphicsUnit.Pixel);
            if (rightW > 0)
                g.DrawImage(_sideFill, new Rectangle(rightX, y0, rightW, rest), new Rectangle(rightX, 0, rightW, 1), GraphicsUnit.Pixel);
        }

        if (_footer != null && ftr > 0) g.DrawImageUnscaled(_footer, 0, hdr + bodyH);
        return bmp;
    }

    /// <summary>Kicsinyített előnézet (a törzs), a jobb oldali csíkhoz.</summary>
    public void DrawPreview(Graphics g, RectangleF dest)
    {
        if (_canvas == null) return;
        _canvas.DrawPreview(g, dest, _canvas.Top, _canvas.Bottom, _band.X, _band.Width);
    }

    // ================= Segédek =================

    private static int[] Pixels(Bitmap bmp, Rectangle r)
    {
        var data = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[r.Width * r.Height];
            for (int y = 0; y < r.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * r.Width, r.Width);
            return px;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static Bitmap Crop(Bitmap src, Rectangle r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        if (r.Width <= 0 || r.Height <= 0) return bmp;
        using var g = Graphics.FromImage(bmp);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        return bmp;
    }

    public void Dispose()
    {
        foreach (var f in _calib) f.Dispose();
        _calib.Clear();
        _canvas?.Dispose();
        _header?.Dispose();
        _footer?.Dispose();
        _sideFill?.Dispose();
        _sideFirst?.Dispose();
        _last?.Dispose();
    }
}

/// <summary>
/// A hosszú kép rögzítésének vezérlése (felület nélkül, tesztelhető).
/// Eldönti a következő görgetést, és hiba esetén javít, ahelyett hogy leállna:
/// - sikertelen illesztés (animáció, betöltés): újra megnézi, majd kicsit visszagörget;
/// - túl nagy ugrás (a program gyorsította a görgetést): visszagörget, és kisebb lépéssel folytatja;
/// - az oldal végét csak többszöri, mozdulatlan próbálkozás után fogadja el.
/// </summary>
internal sealed class LongController
{
    public enum Next { Scroll, Recheck, Finish }

    private const double StepRatio = 0.4;   // egy lépés a görgő terület kb. ekkora része (bő átfedés)
    private const int MaxNotches = 8;

    private readonly LongStitcher _st;
    private readonly int _bandHeight;
    private int _notches = 1, _retries, _backs, _gaps, _step;

    public int WheelDelta { get; private set; } = -120;   // a következő görgetés (negatív = lefelé)
    public string Status { get; private set; } = "Görgetés és rögzítés…";
    public List<string> Log { get; } = new();

    public LongController(LongStitcher st, int bandHeight)
    {
        _st = st;
        _bandHeight = bandHeight;
    }

    /// <summary>Az első képkocka (görgetés előtt).</summary>
    public void Start(Bitmap first)
    {
        _st.Add(first);
        Log.Add($"indulás, sáv {_bandHeight} px");
    }

    public Next OnFrame(Bitmap frame)
    {
        _step++;
        var r = _st.Add(frame);
        Log.Add($"#{_step} görgetés {WheelDelta} → {r}, elmozdulás {_st.LastDelta}, hely {_st.Position}, {_st.MatchInfo}, magasság {_st.TotalHeight}");

        switch (r)
        {
            case LongStitcher.Result.Continue:
                _retries = 0;
                if (_st.LastDelta > 0)
                {
                    _backs = 0;
                    _gaps = 0;
                    // Lépésméret: a mért px/fokozat alapján a görgő terület kb. 40%-a
                    int area = _st.Calibrated ? _st.Body.Height : _bandHeight;
                    double pxPerNotch = _st.LastDelta / (double)Math.Max(1, -WheelDelta / 120);
                    _notches = Math.Clamp((int)(area * StepRatio / Math.Max(1, pxPerNotch)), 1, MaxNotches);
                }
                WheelDelta = -120 * _notches;
                return Next.Scroll;

            case LongStitcher.Result.Lost:
                // Előbb csak újranézzük (lehet, hogy még betölt / animál)
                if (++_retries <= 3) { Log.Add("  nem illeszkedik, újranézem"); return Next.Recheck; }
                // Utána kicsit visszagörgetünk, hogy legyen átfedés
                if (++_backs <= 3)
                {
                    _retries = 0;
                    _notches = Math.Max(1, _notches / 2);
                    WheelDelta = 120 * Math.Max(1, _notches);
                    Log.Add("  nem illeszkedik, visszagörgetek");
                    return Next.Scroll;
                }
                Status = "Nem sikerült követni a görgetést";
                return Next.Finish;

            case LongStitcher.Result.Gap:
                // Túl nagyot ugrott: vissza, és felére csökkentett lépéssel tovább
                if (++_gaps > 6) { Status = "A görgetés túl nagyokat ugrik"; return Next.Finish; }
                _notches = Math.Max(1, _notches / 2);
                WheelDelta = 120 * Math.Max(1, _notches);
                Log.Add("  túl nagy ugrás, visszagörgetek");
                return Next.Scroll;

            case LongStitcher.Result.Max:
                Status = "Elérte a magassági korlátot";
                return Next.Finish;

            default:
                Status = "Az oldal végére ért";
                return Next.Finish;
        }
    }
}
