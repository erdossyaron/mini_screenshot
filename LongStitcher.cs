using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>
/// A hosszú kép összerakása képkockákból (felület nélkül, ezért külön is tesztelhető).
///
/// 1. Kalibrálás: az első három, valóban elgörgetett képkockából felismeri, mi nem mozdul
///    (egy animáció vagy villogó kurzor nem görgetés, az ilyen képkockát kihagyja):
///    fent a fejléc, lent a lábléc, oldalt a rögzített oldalsáv. A többi a görgő terület ("törzs").
/// 2. Illesztés és összefűzés csak a törzsben történik, így a rögzített részek nem zavarják.
/// 3. A kész kép: fejléc egyszer felül → a törzs "lefolyik" → lábléc egyszer alul.
///    Az oldalsáv csak az első képernyő magasságáig látszik, alatta a saját háttérszínével folytatódik.
/// 4. Görgetősáv: a csúszka a tartalomtól eltérően mozog, ezért összefűzve hosszú csíkká "kenődne".
///    A felvétel közben gyűjtött bizonyítékok alapján ezeket az oszlopokat a kész képen
///    a szomszédos tartalom-oszloppal tölti ki (a hosszú képen a görgetősávnak nincs értelme).
/// </summary>
internal sealed class LongStitcher : IStitcher
{
    public enum Result { Continue, Lost, Gap, Max }

    public const int MaxHeight = 25000;

    private readonly int _w;                 // képkocka szélessége (a képernyő)
    private readonly Rectangle _band;        // a kijelölés a képernyőn; a képkocka = a sáv sorai, teljes szélességben
    private readonly int _minBody;
    private readonly int _edgeZone;          // a törzs szélein ekkora sávban keresünk görgetősávot
    private readonly int _sideReach;         // az oldalsávban a törzs melletti ekkora részt figyeljük
    private readonly int _minThumb;          // a csúszka mozgásából adódó egybefüggő eltérés legalább ekkora
    private readonly int _maxBar;            // ennél szélesebb "mozgó oszlopcsoport" nem görgetősáv
    private readonly int _stripW;            // illesztési csíkszélesség
    private readonly StitchCanvas _pre;      // kalibrálás előtt: a teljes sávon becsli az elmozdulást
    private readonly List<FrameSig> _calibSig = new();

    private readonly List<Bitmap> _calib = new();
    private StitchCanvas? _canvas;
    private Rectangle _body;                 // görgő terület a képernyőn
    private Bitmap? _header, _footer, _sideFill, _sideFirst, _last;
    private FrameSig? _lastSig;
    private int _s, _lastS, _guard, _hdrStrict, _left, _right;
    private int _vel, _lostStreak;           // utolsó elmozdulás képkockánként; egymás utáni illeszthetetlenek
    private readonly int _minTol;
    private bool _flushed;

    // Görgetősáv-bizonyítékok, a sáv oszlopaira (0 = a sáv bal széle)
    private int[]? _f0Px;                    // az első képkocka pixelei (az oldalsáv változásához)
    private readonly int[] _edgeHits;        // hányszor nem követte a tartalom mozgását (törzs széle)
    private readonly int[] _sideRun;         // a leghosszabb egybefüggő eltérés az első képkockához képest (oldalsáv)

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

    // ---- IStitcher: előnézet és napló ----
    public Size PreviewSize => new(_band.Width, Calibrated ? TotalHeight - HeaderRows - FooterRows : _band.Height);
    public string SizeLabel => TotalHeight.ToString("#,0", System.Globalization.CultureInfo.GetCultureInfo("hu-HU")) + " px";
    public string FrameInfo => $"elmozdulás {LastDelta}, hely {Position}, {MatchInfo}, magasság {TotalHeight}";
    public string Summary => $"görgetés, törzs {Body} (sávon belül), fejléc {HeaderRows}, lábléc {FooterRows}, " +
                             $"görgetősáv: {(ScrollbarInfo is { Length: > 0 } sb ? sb : "nincs")}";

    /// <summary>A naplóhoz: a görgetősávnak felismert oszlopcsoportok (a sáv bal szélétől).</summary>
    public string ScrollbarInfo => string.Join(", ", ScrollbarRuns().Select(r => $"{r.x0}–{r.x1}"));

    public LongStitcher(int screenWidth, Rectangle band, float scale)
    {
        _w = screenWidth;
        _band = band;
        _minBody = (int)(80 * scale);
        _edgeZone = (int)(40 * scale);
        _sideReach = (int)(60 * scale);
        _minThumb = (int)(30 * scale);
        _maxBar = (int)(32 * scale);
        _stripW = (int)(56 * scale);
        _minTol = (int)(40 * scale);
        _pre = new StitchCanvas(screenWidth, band.Left, band.Right, _stripW);
        _body = band;
        _edgeHits = new int[band.Width];
        _sideRun = new int[band.Width];
    }

    // ================= Képkockák feldolgozása =================

    /// <summary>
    /// A következő képkocka (az első a felvétel indulása). Kalibrálás előtt csak azokat gyűjti,
    /// amelyek az előzőhöz képest egyértelműen elgördültek; az animáció, kiemelés, kurzor kimarad.
    /// </summary>
    public Result Add(Bitmap frame)
    {
        if (_canvas == null)
        {
            var fs = _pre.RowSignatures(frame);
            if (_calib.Count > 0)
            {
                var d = _pre.EstimateShift(_calibSig[^1], fs);
                if (d == null) return Result.Continue;   // még nem görgettek: várunk
                LastDelta = d.Value;
            }
            _calib.Add(new Bitmap(frame));
            _calibSig.Add(fs);
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
            if (hdr >= h - 1)
            {
                // Szinte semmi nem mozdult: az első képkocka marad, a többit később újra próbáljuk
                for (int i = _calib.Count - 1; i >= 1; i--) { _calib[i].Dispose(); _calib.RemoveAt(i); _calibSig.RemoveAt(i); }
                return Result.Lost;
            }
            hdr = ftr = 0;
        }
        _hdrStrict = hdr;

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
        _left = left;
        _right = right;

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
        _f0Px = a;

        // Fejléc (teljes sávszélesség), az oldalsáv első képernyője, és a folytatásához oszloponként
        // a leggyakoribb szín (az utolsó sor helyett, mert ott lehet csúszka vagy gomb is)
        _header = Crop(f0, new Rectangle(_band.X, 0, _band.Width, hdr));
        _sideFirst = Crop(f0, new Rectangle(_band.X, _body.Top - _band.Top, _band.Width, _body.Height));
        _sideFill = SideFill(_sideFirst, left, right);

        // Az összefűzés a törzs oszlopaival illeszt; az első képernyő a görgetés = 0 állapot.
        // A törzs alsó sávjából (lebegő gombok, elhalványítás) még nem veszünk át sorokat.
        _canvas = new StitchCanvas(_w, _body.Left, _body.Right, _stripW);
        var fs0 = _canvas.RowSignatures(f0);
        _canvas.PutRows(f0, _band.Top, 0, _body.Top, _body.Bottom - _guard, fs0);
        KeepLast(f0, fs0);
        UpdateFooter(f0);

        var res = Process(f1);
        if (res is Result.Continue or Result.Lost) res = Process(f2);

        foreach (var f in _calib) f.Dispose();
        _calib.Clear();
        _calibSig.Clear();
        return res;
    }

    private Result Process(Bitmap frame)
    {
        var c = _canvas!;
        CheckSides(frame);
        var fs = c.RowSignatures(frame);
        // A várt hely: az utolsó sebességgel továbbgörgetve (kihagyott képkockákkal együtt)
        int predicted = _s + _vel * (_lostStreak + 1);
        int tol = Math.Max(_minTol, Math.Abs(_vel) * (_lostStreak + 1) / 2);
        int? m = c.Match(fs, _band.Top, _s, _band.Height * 2, predicted, tol);
        if (m == null && _last != null && _lastSig != null && c.EstimateShift(_lastSig, fs) is int d && d > 0)
        {
            // Gyors görgetés: a már rögzített sorokkal nincs elég átfedés, de az előző képkockával
            // (annak alsó, még át nem vett sávjával együtt) van. Ezt a sávot most átvesszük, és folytatjuk.
            int to = Math.Min(_lastS + _body.Bottom, c.Top + MaxHeight);
            if (to > c.Bottom && c.Bottom >= _lastS + _body.Top)
                c.PutRows(_last, _band.Top, _lastS, c.Bottom, to, _lastSig);
            m = _lastS + d;
        }
        if (m == null) { _lostStreak++; return Result.Lost; }

        int ds = m.Value - _s;
        LastDelta = ds;
        _vel = ds / (_lostStreak + 1);
        _lostStreak = 0;
        if (ds != 0) CheckEdges(frame, m.Value);   // még a hozzáfűzés előtt, a régi sorokkal vetjük össze
        if (ds <= 0)
        {
            // Visszagörgetés vagy nincs elmozdulás (kurzor, kiemelés): csak a helyet frissítjük
            _s = m.Value;
            return Result.Continue;
        }
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

    // ================= Görgetősáv felismerése =================

    /// <summary>
    /// Oldalsáv: a mozdulatlannak vélt oszlopok a törzs mellett. Ha később egy keskeny csoportjuk
    /// hosszan, egybefüggően eltér az első képkockától, az a csúszka (pl. a görgetéskor előugró sáv).
    /// </summary>
    private void CheckSides(Bitmap frame)
    {
        if (_f0Px == null) return;
        int w = _band.Width, y0 = _body.Top - _band.Top, n = _body.Height;
        if (n <= 0) return;
        foreach (var (xa, xb) in new[] { (Math.Max(0, _left - _sideReach), _left), (w - _right, Math.Min(w, w - _right + _sideReach)) })
        {
            int cw = xb - xa;
            if (cw <= 0) continue;
            var px = Pixels(frame, new Rectangle(_band.X + xa, y0, cw, n));
            for (int x = 0; x < cw; x++)
            {
                int run = 0, best = 0;
                for (int y = 0; y < n; y++)
                {
                    run = Differs(px[y * cw + x], _f0Px[(y0 + y) * w + xa + x]) ? run + 1 : 0;
                    if (run > best) best = run;
                }
                if (best > _sideRun[xa + x] && (best < _minThumb || Flat(px, cw, x, n)))
                    _sideRun[xa + x] = best;
            }
        }
    }

    /// <summary>
    /// Törzs szélei: a képkocka oszlopait összeveti a már rögzített (ugyanarra a helyre eső) sorokkal.
    /// A tartalom egyezik, a csúszka nem, mert máshogy mozog. A soronkénti zajt (kiemelés, animáció)
    /// a szélső oszlopok mediánja kiszűri, így csak a kiugró oszlopok számítanak.
    /// </summary>
    private void CheckEdges(Bitmap frame, int s)
    {
        var c = _canvas!;
        int d0 = Math.Max(s + _body.Top, c.Top), d1 = Math.Min(s + _body.Bottom, c.Bottom);
        int n = d1 - d0, zw = Math.Min(_edgeZone, _body.Width / 4);
        if (n < 16 || zw < 2) return;

        // Szélenként külön medián: ha az egyik szélen animáció fut, az nem emeli a másik küszöbét
        foreach (int zx in new[] { _body.Left, _body.Right - zw })
        {
            int[] cur = Pixels(frame, new Rectangle(zx, d0 - s - _band.Top, zw, n));
            int[] old;
            using (var o = c.Render(d0, d1, zx, zw)) old = Pixels(o, new Rectangle(0, 0, zw, n));
            var mis = new int[zw];
            for (int x = 0; x < zw; x++)
                for (int y = 0; y < n; y++)
                    if (Differs(cur[y * zw + x], old[y * zw + x])) mis[x]++;
            int thr = mis.OrderBy(v => v).ElementAt(zw / 2) + Math.Max(6, n / 50);
            for (int x = 0; x < zw; x++)
            {
                int bx = zx - _band.X + x;
                // A csúszka sávja néhány egyszínű felület (sín + csúszka); az animált tartalom tarka
                if (mis[x] >= thr && bx >= 0 && bx < _edgeHits.Length && Flat(cur, zw, x, n)) _edgeHits[bx]++;
            }
        }
    }

    /// <summary>Az oszlop pixeleinek legalább 75%-a a három leggyakoribb színből áll (görgetősáv jellegű).</summary>
    private static bool Flat(int[] px, int stride, int x, int n)
    {
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < n; y++)
        {
            int v = px[y * stride + x] & 0xFFFFFF;
            counts[v] = counts.GetValueOrDefault(v) + 1;
        }
        int top3 = counts.Values.OrderByDescending(v => v).Take(3).Sum();
        return top3 * 4 >= n * 3;
    }

    private static bool Differs(int p, int q)
    {
        int d = Math.Abs((p & 0xFF) - (q & 0xFF)) + Math.Abs(((p >> 8) & 0xFF) - ((q >> 8) & 0xFF))
              + Math.Abs(((p >> 16) & 0xFF) - ((q >> 16) & 0xFF));
        return d > 24;
    }

    /// <summary>A görgetősávnak ítélt, keskeny oszlopcsoportok [x0, x1) a sáv bal szélétől.</summary>
    private List<(int x0, int x1)> ScrollbarRuns()
    {
        var runs = new List<(int x0, int x1)>();
        if (_canvas == null) return runs;
        int w = _band.Width;
        bool Dyn(int x) => _edgeHits[x] >= 2 || _sideRun[x] >= _minThumb;
        // Görgetősáv csak a görgő terület jobb szélén vagy attól jobbra lehet (a bal szélen inkább animáció)
        int i = Math.Max(0, _body.Right - _band.X - _edgeZone);
        while (i < w)
        {
            if (!Dyn(i)) { i++; continue; }
            int x0 = i, x1 = i + 1;
            // 1–2 px rés (a csúszka szegélye, élsimítás) nem bontja ketté a csoportot
            for (int j = i + 1; j < w && j <= x1 + 2; j++) if (Dyn(j)) x1 = j + 1;
            if (x1 - x0 >= 2 && x1 - x0 <= _maxBar) runs.Add((x0, x1));
            i = x1;
        }
        return runs;
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
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (_header != null && hdr > 0) g.DrawImageUnscaled(_header, 0, 0);

            using (var body = _canvas.Render(_canvas.Top, _canvas.Bottom, _band.X, outW))
                g.DrawImageUnscaled(body, 0, hdr);

            // Oldalsáv: az első képernyőn pontosan úgy, ahogy látszott (az indulókép), alatta
            // oszloponként a saját leggyakoribb színével folytatódik (nem ismétlődik)
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
        }

        EraseScrollbars(bmp);
        return bmp;
    }

    /// <summary>
    /// A görgetősáv oszlopait a tartalom felőli szomszéd oszloppal tölti ki, a fejléc alatt végig
    /// (a lábléc is az utolsó képkockából jön, ott is lehet csúszka).
    /// </summary>
    private void EraseScrollbars(Bitmap bmp)
    {
        int y0 = _hdrStrict, hgt = bmp.Height - y0, outW = bmp.Width;
        if (hgt <= 0) return;
        int center = (_body.Left + _body.Right) / 2 - _band.X;
        foreach (var (x0, x1) in ScrollbarRuns())
        {
            bool rightOfCenter = (x0 + x1) / 2 > center;
            int nx = rightOfCenter ? x0 - 1 : x1;
            if (nx < 0 || nx >= outW) nx = rightOfCenter ? x1 : x0 - 1;
            if (nx < 0 || nx >= outW) continue;
            using var col = Crop(bmp, new Rectangle(nx, y0, 1, hgt));
            using var g = Graphics.FromImage(bmp);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(col, new Rectangle(x0, y0, x1 - x0, hgt), new Rectangle(0, 0, 1, hgt), GraphicsUnit.Pixel);
        }
    }

    /// <summary>Kicsinyített előnézet (a törzs), a jobb oldali csíkhoz.</summary>
    public void DrawPreview(Graphics g, RectangleF dest)
    {
        if (_canvas == null) return;
        _canvas.DrawPreview(g, dest, _canvas.Top, _canvas.Bottom, _band.X, _band.Width);
    }

    // ================= Segédek =================

    /// <summary>Egysoros kép: az oldalsáv oszlopaiban oszloponként a leggyakoribb szín.</summary>
    private static Bitmap SideFill(Bitmap side, int left, int right)
    {
        int w = side.Width, h = side.Height;
        var px = Pixels(side, new Rectangle(0, 0, w, h));
        var bmp = new Bitmap(w, 1, PixelFormat.Format32bppArgb);
        var counts = new Dictionary<int, int>();
        for (int x = 0; x < w; x++)
        {
            if (x >= left && x < w - right) continue;
            counts.Clear();
            int best = px[x], bestN = 0;
            for (int y = 0; y < h; y++)
            {
                int v = px[y * w + x];
                int c = counts.GetValueOrDefault(v) + 1;
                counts[v] = c;
                if (c > bestN) { bestN = c; best = v; }
            }
            bmp.SetPixel(x, 0, Color.FromArgb(best | unchecked((int)0xFF000000)));
        }
        return bmp;
    }

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
        _pre.Dispose();
        _header?.Dispose();
        _footer?.Dispose();
        _sideFill?.Dispose();
        _sideFirst?.Dispose();
        _last?.Dispose();
    }
}
