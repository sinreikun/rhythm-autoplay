using RhythmAutoplay.Core;

namespace RhythmAutoplay.App;

public sealed class SnapshotOptions
{
    public int Seconds { get; init; } = 8;
    public int IntervalMilliseconds { get; init; } = 500;
    public string? OutDirectory { get; init; }
}

/// <summary>
/// 較正用の証拠取りモード。一定間隔でフレームを保存し（半分の解像度の BMP）、
/// そのフレームに対する検出結果（レーン/種別/位置）をテキストに残す。
/// 「検出器が何を見ているか」を実フレームと突き合わせて検証するために使う。
/// </summary>
public sealed class SnapshotSession
{
    private readonly GameProfile _profile;
    private readonly SnapshotOptions _options;
    private readonly Action<string>? _log;
    private volatile bool _cancelled;

    public SnapshotSession(GameProfile profile, SnapshotOptions options, Action<string>? log = null)
    {
        _profile = profile;
        _options = options;
        _log = log;
    }

    private void Log(string message)
    {
        Console.WriteLine(message);
        _log?.Invoke(message);
    }

    public int Run()
    {
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero && !_cancelled; i++)
        {
            hwnd = WindowCapturer.FindWindowByProcessName(_profile.WindowProcessName);
            if (hwnd == IntPtr.Zero)
            {
                Thread.Sleep(500);
            }
        }
        if (hwnd == IntPtr.Zero)
        {
            Log("ウィンドウが見つかりませんでした。");
            return 1;
        }

        string outDir = _options.OutDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RhythmAutoplay", "snapshots", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);

        using var capturer = new WindowCapturer(hwnd);
        var detector = new FrameDetector(_profile);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cancelled = true;
        };

        double startedAt = capturer.Now;
        double nextAt = startedAt;
        int index = 0;
        int totalDetections = 0;

        // 検出結果はフレームごとに追記する（v0.3.32）。以前は終了時に一括で
        // 書いていたため、drive 側の本体キル（同名プロセス一括停止）に巻き
        // 込まれて 200 秒分の検出記録が丸ごと消えた（2026-11-07 の診断ラン）。
        using var writer = new StreamWriter(Path.Combine(outDir, "detections.txt"), append: false) { AutoFlush = true };

        while (!_cancelled && capturer.Now - startedAt < _options.Seconds)
        {
            double now = capturer.Now;
            if (now < nextAt)
            {
                Thread.Sleep(10);
                continue;
            }
            nextAt = now + _options.IntervalMilliseconds / 1000.0;

            CapturedFrame frame;
            try
            {
                frame = capturer.Capture();
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            List<NoteDetection> detections = detector.Detect(frame);
            totalDetections += detections.Count;
            SaveBmpHalf(frame, Path.Combine(outDir, $"frame_{index:D3}.bmp"));
            writer.WriteLine($"# frame={index} t={frame.TimeSeconds:F3} size={frame.Width}x{frame.Height} detections={detections.Count}");
            foreach (NoteDetection d in detections)
            {
                writer.WriteLine($"frame={index} lane={d.Lane} kind={d.Kind} dir={d.Direction?.ToString() ?? "-"} x={d.X:F3} y={d.Y:F3}");
            }
            index++;
        }

        Log($"スナップショットを保存しました: {outDir}（{index} フレーム, 検出 {totalDetections} 件）");
        return 0;
    }

    private static void SaveBmpHalf(CapturedFrame frame, string path)
    {
        int w2 = Math.Max(1, frame.Width / 2);
        int h2 = Math.Max(1, frame.Height / 2);
        byte[] data = new byte[w2 * h2 * 4];
        for (int y = 0; y < h2; y++)
        {
            for (int x = 0; x < w2; x++)
            {
                int si = ((y * 2) * frame.Width + x * 2) * 4;
                int di = (y * w2 + x) * 4;
                data[di] = frame.Bgra[si];
                data[di + 1] = frame.Bgra[si + 1];
                data[di + 2] = frame.Bgra[si + 2];
                data[di + 3] = frame.Bgra[si + 3];
            }
        }

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0x4D42); // 'BM'
        bw.Write(54 + data.Length);
        bw.Write(0);
        bw.Write(54);
        bw.Write(40);
        bw.Write(w2);
        bw.Write(-h2); // 上から下
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(data.Length);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(data);
    }
}
