using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>Egy képkocka lenyomata: soronként, függőleges csíkonként.</summary>
internal sealed class FrameSig
{
    public readonly long[,] Row;     // [csík, sor]
    public readonly bool[,] Info;    // a sor nem egyszínű ebben a csíkban
    public int Height => Row.GetLength(1);
    public FrameSig(int strips, int h) { Row = new long[strips, h]; Info = new bool[strips, h]; }
}

/// <summary>
/// Görgetéses rögzítés összefűzött képe dokumentum-koordinátában.
/// Dokumentum-sor = képernyő-sor + görgetési pozíció.
///
/// Illesztés: a szélességet 4 függőleges csíkra bontja, és csíkonként 8 egymás utáni sor
/// együttes lenyomatával szavaztat. Így egy animált sarok vagy egy felugró sáv csak a saját
/// csíkját és sorait rontja el, a többi szavazat eldönti a helyet. Csak egyértelmű győztest fogad el.
/// </summary>
internal sealed class StitchCanvas : IDisposable
{
    public const int ChunkH = 512;
    private const int Strips = 4;
    private const int Shingle = 8;             // ennyi egymás utáni sor együtt alkot egy lenyomatot
    private const int ThumbScale = 8;
    private const int MaxIndexPerKey = 32;

    public int Width { get; }
    public int Top { get; private set; }
    public int Bottom { get; private set; }
    public int Height => Bottom - Top;

    private readonly int[] _stripX;            // csíkhatárok (Strips + 1 elem)
    private readonly Dictionary<int, Bitmap> _chunks = new();
    private readonly Dictionary<int, Bitmap> _thumbs = new();
    private readonly HashSet<int> _dirtyThumbs = new();
    private readonly Dictionary<int, (long[] sig, bool[] info)> _rows = new();   // dokumentum-sor → csíkonkénti lenyomat
    private readonly Dictionary<long, List<int>> _index = new();                 // kulcs → dokumentum-sorok (a lenyomat teteje)
    private readonly HashSet<int> _indexed = new();                              // mely sortetők vannak már indexelve

    public int LastVotes { get; private set; }
    public int LastInformative { get; private set; }

    public StitchCanvas(int width, int sigX0, int sigX1)
    {
        Width = width;
        int x0 = Math.Clamp(sigX0, 0, width - 1), x1 = Math.Clamp(sigX1, x0 + 1, width);
        _stripX = new int[Strips + 1];
        for (int i = 0; i <= Strips; i++) _stripX[i] = x0 + (x1 - x0) * i / Strips;
    }

    // ================= Lenyomatok =================

    public FrameSig RowSignatures(Bitmap frame)
    {
        int h = frame.Height;
        var fs = new FrameSig(Strips, h);
        int x0 = _stripX[0], x1 = Math.Min(_stripX[Strips], frame.Width);
        if (x1 <= x0) return fs;

        var data = frame.LockBits(new Rectangle(x0, 0, x1 - x0, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = (x1 - x0) * 4;
            var buf = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, buf, 0, rowBytes);
                for (int s = 0; s < Strips; s++)
                {
                    int a = (_stripX[s] - x0) * 4, b = Math.Min((_stripX[s + 1] - x0) * 4, rowBytes);
                    long h1 = 1469598103934665603L;
                    int min = 255, max = 0;
                    for (int i = a; i < b; i += 8)   // minden második pixel
                    {
                        int v = (buf[i] + buf[i + 1] * 5 + buf[i + 2] * 2) >> 3;
                        h1 = (h1 ^ v) * 1099511628211L;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    fs.Row[s, y] = h1;
                    fs.Info[s, y] = max - min >= 10;
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }
        return fs;
    }

    /// <summary>8 sor együttes kulcsa egy csíkban; null, ha nem beszédes (egyszínű vagy egyforma sorok).</summary>
    private static long? ShingleKey(int strip, Func<int, long> sig, Func<int, bool> info, int r)
    {
        long k = unchecked((strip + 1) * (long)0x9E3779B97F4A7C15UL);
        bool any = false, varied = false;
        long first = sig(r);
        for (int i = 0; i < Shingle; i++)
        {
            long v = sig(r + i);
            k = (k ^ v) * 1099511628211L + i;
            any |= info(r + i);
            varied |= v != first;
        }
        return any && varied ? k : null;
    }

    // ================= Illesztés =================

    /// <summary>
    /// Hol van a képkocka a rögzített képen belül? Görgetési pozíció (dokumentum-sor = képernyő-sor + pozíció)
    /// vagy null, ha nincs egyértelmű egyezés.
    /// </summary>
    public int? Match(FrameSig fs, int frameTop, int predicted, int range)
    {
        LastVotes = LastInformative = 0;
        if (Height == 0) return null;
        var votes = new Dictionary<int, int>();
        int informative = 0, h = fs.Height;

        for (int s = 0; s < Strips; s++)
        {
            long? prev = null;
            for (int r = 0; r + Shingle <= h; r++)
            {
                int ss = s;
                var key = ShingleKey(s, i => fs.Row[ss, i], i => fs.Info[ss, i], r);
                if (key == null || key == prev) { prev = key; continue; }
                prev = key;
                informative++;
                if (!_index.TryGetValue(key.Value, out var rows)) continue;
                foreach (int docY in rows)
                {
                    int pos = docY - (frameTop + r);
                    if (Math.Abs(pos - predicted) > range) continue;
                    votes[pos] = votes.GetValueOrDefault(pos) + 1;
                }
            }
        }

        LastInformative = informative;
        if (votes.Count == 0) return null;

        int best = 0, bestPos = predicted, second = 0;
        foreach (var (pos, v) in votes)
        {
            if (v > best || (v == best && Math.Abs(pos - predicted) < Math.Abs(bestPos - predicted)))
            {
                if (pos != bestPos) second = Math.Max(second, best);
                best = v;
                bestPos = pos;
            }
            else second = Math.Max(second, v);
        }
        LastVotes = best;

        // Egyértelmű győztes: elég szavazat, és jóval több, mint a második helyezetté
        bool enough = best >= Math.Max(6, informative / 12);
        bool clear = best >= second * 2;
        return enough && clear ? bestPos : null;
    }

    // ================= Tárolás =================

    /// <summary>A képkocka adott dokumentum-sorait tárolja (csak az újakat), folytonosan alul vagy felül.</summary>
    public void PutRows(Bitmap frame, int frameTop, int scroll, int docFrom, int docTo, FrameSig fs)
    {
        if (docTo <= docFrom) return;
        if (Height == 0) Top = Bottom = docFrom;

        if (docTo > Bottom && docFrom <= Bottom)
        {
            CopyRows(frame, frameTop, scroll, Bottom, docTo, fs);
            Bottom = docTo;
        }
        if (docFrom < Top && docTo >= Top)
        {
            CopyRows(frame, frameTop, scroll, docFrom, Top, fs);
            Top = docFrom;
        }
        IndexNewShingles();
    }

    private void CopyRows(Bitmap frame, int frameTop, int scroll, int docFrom, int docTo, FrameSig fs)
    {
        int y = docFrom;
        while (y < docTo)
        {
            int ci = FloorDiv(y, ChunkH);
            int chunkTop = ci * ChunkH;
            int segEnd = Math.Min(docTo, chunkTop + ChunkH);
            if (!_chunks.TryGetValue(ci, out var chunk))
            {
                chunk = new Bitmap(Width, ChunkH, PixelFormat.Format24bppRgb);
                _chunks[ci] = chunk;
            }
            using (var g = Graphics.FromImage(chunk))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(frame, new Rectangle(0, y - chunkTop, Width, segEnd - y),
                    new Rectangle(0, y - scroll - frameTop, Width, segEnd - y), GraphicsUnit.Pixel);
            }
            _dirtyThumbs.Add(ci);

            for (int d = y; d < segEnd; d++)
            {
                int r = d - scroll - frameTop;
                if (r < 0 || r >= fs.Height) continue;
                var sig = new long[Strips];
                var info = new bool[Strips];
                for (int s = 0; s < Strips; s++) { sig[s] = fs.Row[s, r]; info[s] = fs.Info[s, r]; }
                _rows[d] = (sig, info);
            }
            y = segEnd;
        }
    }

    /// <summary>Azokat a 8 soros lenyomatokat indexeli, amelyek minden sora már megvan.</summary>
    private void IndexNewShingles()
    {
        for (int d = Top; d + Shingle <= Bottom; d++)
        {
            if (_indexed.Contains(d)) continue;
            _indexed.Add(d);
            for (int s = 0; s < Strips; s++)
            {
                int ss = s;
                var key = ShingleKey(s, i => _rows[i].sig[ss], i => _rows[i].info[ss], d);
                if (key == null) continue;
                if (!_index.TryGetValue(key.Value, out var list)) _index[key.Value] = list = new List<int>();
                if (list.Count < MaxIndexPerKey) list.Add(d);
            }
        }
    }

    // ================= Kiolvasás =================

    public Bitmap Render(int docTop, int docBottom, int x, int w)
    {
        docTop = Math.Max(docTop, Top);
        docBottom = Math.Min(docBottom, Bottom);
        int h = Math.Max(1, docBottom - docTop);
        var bmp = new Bitmap(Math.Max(1, w), h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        for (int y = docTop; y < docBottom;)
        {
            int ci = FloorDiv(y, ChunkH);
            int chunkTop = ci * ChunkH;
            int segEnd = Math.Min(docBottom, chunkTop + ChunkH);
            if (_chunks.TryGetValue(ci, out var chunk))
                g.DrawImage(chunk, new Rectangle(0, y - docTop, w, segEnd - y),
                    new Rectangle(x, y - chunkTop, w, segEnd - y), GraphicsUnit.Pixel);
            y = segEnd;
        }
        return bmp;
    }

    public void DrawPreview(Graphics g, RectangleF dest, int docTop, int docBottom, int x, int w)
    {
        docTop = Math.Max(docTop, Top);
        docBottom = Math.Min(docBottom, Bottom);
        if (docBottom <= docTop || w <= 0) return;
        float scale = dest.Height / (docBottom - docTop);

        for (int y = docTop; y < docBottom;)
        {
            int ci = FloorDiv(y, ChunkH);
            int chunkTop = ci * ChunkH;
            int segEnd = Math.Min(docBottom, chunkTop + ChunkH);
            var thumb = Thumb(ci);
            if (thumb != null)
            {
                var src = new RectangleF((float)x / ThumbScale, (float)(y - chunkTop) / ThumbScale,
                    (float)w / ThumbScale, (float)(segEnd - y) / ThumbScale);
                var dst = new RectangleF(dest.X, dest.Y + (y - docTop) * scale, dest.Width, (segEnd - y) * scale + 0.5f);
                g.DrawImage(thumb, dst, src, GraphicsUnit.Pixel);
            }
            y = segEnd;
        }
    }

    private Bitmap? Thumb(int ci)
    {
        if (!_chunks.TryGetValue(ci, out var chunk)) return null;
        if (_thumbs.TryGetValue(ci, out var t) && !_dirtyThumbs.Contains(ci)) return t;
        t?.Dispose();
        t = new Bitmap(Math.Max(1, Width / ThumbScale), ChunkH / ThumbScale, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(t))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(chunk, new Rectangle(0, 0, t.Width, t.Height));
        }
        _thumbs[ci] = t;
        _dirtyThumbs.Remove(ci);
        return t;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    public void Dispose()
    {
        foreach (var c in _chunks.Values) c.Dispose();
        foreach (var t in _thumbs.Values) t.Dispose();
        _chunks.Clear();
        _thumbs.Clear();
    }
}
