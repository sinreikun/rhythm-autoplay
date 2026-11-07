using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RhythmAutoplay.App;

/// <summary>キャプチャした1フレーム（BGRA）。TimeSeconds はセッション時計の時刻。</summary>
public sealed class CapturedFrame
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double TimeSeconds { get; init; }

    // コンテンツ矩形（v0.3.71）: 固定アスペクトのゲームは横長ウィンドウで
    // 左右に黒帯を出す。検出はこの矩形を基準に行う。Width/Height は
    // バッファ全体（画像保存用）、View* が検出用の矩形。
    public int ViewX { get; init; }
    public int ViewY { get; init; }
    public int ViewWidth { get; init; }
    public int ViewHeight { get; init; }

    public (byte R, byte G, byte B) PixelAt(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    /// <summary>コンテンツ矩形内の座標で標本を取る（検出専用）。</summary>
    public (byte R, byte G, byte B) ViewPixelAt(int x, int y)
        => PixelAt(x + ViewX, y + ViewY);
}

/// <summary>
/// 対象ウィンドウのクライアント領域をキャプチャする。既定は画面 DC から
/// の BitBlt（重なった別ウィンドウやオーバレイが映り込む）。UsePrintWindow
/// の場合は PrintWindow で対象ウィンドウだけを描かせるため、オーバレイを
/// 表示したままでも検出が汚染されない（v0.3.63）。PrintWindow が失敗した
/// フレームは画面方式に落とし、一度失敗したらそのセッションでは画面
/// 方式に固定する。
/// セッション全体のマスター時計（Stopwatch）もここが持つ。
/// </summary>
public sealed class WindowCapturer : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _memDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private byte[] _buffer = Array.Empty<byte>();
    private IntPtr _srcDc;
    private IntPtr _srcBitmap;
    private IntPtr _srcOldBitmap;
    private int _srcWidth;
    private int _srcHeight;
    private bool _printWindowFailed;

    public WindowCapturer(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>PrintWindow 方式で撮るか（セッション開始時に設定する）。</summary>
    public bool UsePrintWindow { get; set; }

    public double Now => _clock.Elapsed.TotalSeconds;

    public static IntPtr FindWindowByProcessName(string processName)
    {
        foreach (Process p in Process.GetProcessesByName(processName))
        {
            if (p.MainWindowHandle != IntPtr.Zero)
            {
                return p.MainWindowHandle;
            }
        }
        return IntPtr.Zero;
    }

    public CapturedFrame Capture()
    {
        if (!GetClientRect(_hwnd, out RECT client))
        {
            throw new InvalidOperationException("クライアント領域の取得に失敗しました。");
        }
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("クライアント領域が空です（最小化中？）。");
        }

        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(_hwnd, ref origin);

        EnsureBitmap(width, height);

        double time = Now;
        bool viaPrintWindow = UsePrintWindow && !_printWindowFailed
            && CaptureViaPrintWindow(width, height, origin);
        if (viaPrintWindow)
        {
            // PrintWindow の中身は「呼び出し中にゲームが描いた現在状態」で、
            // 開始時刻で打刻すると全フレームが描画所要時間（実測 ~26ms）
            // だけ未来の内容を持ち、横断時刻が一律にずれて判定が
            // GREAT/CRINGE に流れた（v0.3.63 実走: GREAT 108 / CRINGE 75）。
            // 画面方式は「既に表示済みのフレームの複写」なので開始打刻で
            // 較正されているが、PrintWindow 方式では完了時刻で打刻する。
            time = Now;
        }
        if (!viaPrintWindow)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                BitBlt(_memDc, 0, 0, width, height, screenDc, origin.X, origin.Y, Srccopy);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        var bmi = new BITMAPINFO
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFO>(),
            biWidth = width,
            biHeight = -height, // 負 = 上から下への並び
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        int lines = GetDIBits(_memDc, _bitmap, 0, (uint)height, _buffer, ref bmi, 0);
        if (lines == 0)
        {
            throw new InvalidOperationException("GetDIBits に失敗しました。");
        }
        (int vx, int vy, int vw, int vh) = ContentView(width, height);
        return new CapturedFrame
        {
            Bgra = _buffer, Width = width, Height = height, TimeSeconds = time,
            ViewX = vx, ViewY = vy, ViewWidth = vw, ViewHeight = vh,
        };
    }

    // コンテンツ矩形の検出（v0.3.71）。黒帯（各チャンネル <= 6）でない
    // 画素の外接矩形を端から探す。有効な候補が 2 フレーム連続で一致
    // したら確定し、以後は再計算しない（サイズ変更でリセット）。
    // フェード等の真っ黒フレームでは候補が崩れるため確定しない。
    private (int, int, int, int) _view;
    private (int, int, int, int) _viewCandidate;
    private int _viewStable;
    private bool _viewLocked;
    private int _viewForWidth, _viewForHeight;

    private (int X, int Y, int W, int H) ContentView(int width, int height)
    {
        if (_viewLocked && _viewForWidth == width && _viewForHeight == height)
        {
            return _view;
        }
        (int, int, int, int) full = (0, 0, width, height);
        var cand = ScanContentRect(width, height);
        if (cand == full)
        {
            // 黒帯なし（または検出不能）。そのまま全画面を確定とする。
            _view = full;
            _viewLocked = true;
            _viewForWidth = width;
            _viewForHeight = height;
            return _view;
        }
        if (cand.W < 100 || cand.H < 100)
        {
            return _viewLocked ? _view : full; // 崩れた候補は採用しない
        }
        if (Math.Abs(cand.Item1 - _viewCandidate.Item1) <= 3
            && Math.Abs(cand.Item2 - _viewCandidate.Item2) <= 3
            && Math.Abs(cand.Item3 - _viewCandidate.Item3) <= 3
            && Math.Abs(cand.Item4 - _viewCandidate.Item4) <= 3)
        {
            _viewStable++;
        }
        else
        {
            _viewCandidate = cand;
            _viewStable = 0;
        }
        if (_viewStable >= 1)
        {
            _view = cand;
            _viewLocked = true;
            _viewForWidth = width;
            _viewForHeight = height;
            return _view;
        }
        return _viewLocked ? _view : cand;
    }

    private (int X, int Y, int W, int H) ScanContentRect(int width, int height)
    {
        bool IsBar(int x, int y)
        {
            int i = (y * width + x) * 4;
            return _buffer[i] <= 6 && _buffer[i + 1] <= 6 && _buffer[i + 2] <= 6;
        }
        int left = width, right = -1, top = height, bottom = -1;
        for (int x = 0; x < width; x++)
        {
            bool any = false;
            for (int y = 0; y < height; y += 4)
            {
                if (!IsBar(x, y)) { any = true; break; }
            }
            if (any) { left = x; break; }
        }
        if (left >= width)
        {
            return (0, 0, 0, 0); // 全面黒
        }
        for (int x = width - 1; x > left; x--)
        {
            bool any = false;
            for (int y = 0; y < height; y += 4)
            {
                if (!IsBar(x, y)) { any = true; break; }
            }
            if (any) { right = x; break; }
        }
        for (int y = 0; y < height; y++)
        {
            bool any = false;
            for (int x = left; x <= right; x += 4)
            {
                if (!IsBar(x, y)) { any = true; break; }
            }
            if (any) { top = y; break; }
        }
        for (int y = height - 1; y > top; y--)
        {
            bool any = false;
            for (int x = left; x <= right; x += 4)
            {
                if (!IsBar(x, y)) { any = true; break; }
            }
            if (any) { bottom = y; break; }
        }
        if (right < left || bottom < top)
        {
            return (0, 0, 0, 0);
        }
        return (left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// PrintWindow でウィンドウ全体を予備ビットマップに描かせ、
    /// クライアント領域だけを本体ビットマップへ切り出す。失敗したら
    /// false を返し、このセッションでは画面方式に固定する。
    /// </summary>
    private bool CaptureViaPrintWindow(int clientWidth, int clientHeight, POINT clientOrigin)
    {
        if (!GetWindowRect(_hwnd, out RECT win))
        {
            _printWindowFailed = true;
            return false;
        }
        int winWidth = win.Right - win.Left;
        int winHeight = win.Bottom - win.Top;
        if (winWidth <= 0 || winHeight <= 0)
        {
            _printWindowFailed = true;
            return false;
        }
        EnsureSrcBitmap(winWidth, winHeight);
        if (!PrintWindow(_hwnd, _srcDc, 2)) // PW_RENDERFULLCONTENT
        {
            _printWindowFailed = true;
            return false;
        }
        int offX = clientOrigin.X - win.Left;
        int offY = clientOrigin.Y - win.Top;
        BitBlt(_memDc, 0, 0, clientWidth, clientHeight, _srcDc, offX, offY, Srccopy);
        return true;
    }

    private void EnsureSrcBitmap(int width, int height)
    {
        if (_srcBitmap != IntPtr.Zero && width == _srcWidth && height == _srcHeight)
        {
            return;
        }
        DisposeSrcBitmap();
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            _srcDc = CreateCompatibleDC(screenDc);
            _srcBitmap = CreateCompatibleBitmap(screenDc, width, height);
            _srcOldBitmap = SelectObject(_srcDc, _srcBitmap);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        _srcWidth = width;
        _srcHeight = height;
    }

    private void DisposeSrcBitmap()
    {
        if (_srcBitmap != IntPtr.Zero)
        {
            SelectObject(_srcDc, _srcOldBitmap);
            DeleteObject(_srcBitmap);
            _srcBitmap = IntPtr.Zero;
        }
        if (_srcDc != IntPtr.Zero)
        {
            DeleteDC(_srcDc);
            _srcDc = IntPtr.Zero;
        }
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != IntPtr.Zero && width == _bitmapWidth && height == _bitmapHeight)
        {
            return;
        }
        DisposeBitmap();
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screenDc);
            _bitmap = CreateCompatibleBitmap(screenDc, width, height);
            _oldBitmap = SelectObject(_memDc, _bitmap);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        _bitmapWidth = width;
        _bitmapHeight = height;
        _buffer = new byte[width * height * 4];
    }

    private void DisposeBitmap()
    {
        if (_bitmap != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }
        if (_memDc != IntPtr.Zero)
        {
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        DisposeBitmap();
        DisposeSrcBitmap();
    }

    private const int Srccopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // RGBQUAD はパレット用。32bpp では不使用なので省略する。
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hDC, IntPtr hbmp, uint start, uint lines, byte[] pixels, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
}
