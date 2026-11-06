using System.Runtime.InteropServices;
using RhythmAutoplay.Core;

namespace RhythmAutoplay.App;

public sealed class PlayOptions
{
    /// <summary>入力を送らず、検出と発火予定の表示だけにする（較正・検証用）。</summary>
    public bool DryRun { get; init; }
    /// <summary>全ヒットの発火時刻に加えるオフセット [ms]。正で遅く、負で早く。</summary>
    public double OffsetMilliseconds { get; init; }
    /// <summary>自動終了までの秒数。0 = 無制限（F9 / Ctrl+C で終了）。</summary>
    public int DurationSeconds { get; init; }
}

/// <summary>
/// 実プレイ経路: キャプチャ → 検出 → 追跡/到達予測 → スケジュール → SendInput。
/// 終了は F9 キー（ゲーム側のキーと衝突しない）か Ctrl+C。
/// </summary>
public sealed class PlaySession
{
    private const int VkF9 = 0x78;

    private readonly GameProfile _profile;
    private readonly PlayOptions _options;
    private volatile bool _cancelled;

    public PlaySession(GameProfile profile, PlayOptions options)
    {
        _profile = profile;
        _options = options;
    }

    public int Run()
    {
        Console.WriteLine($"対象プロセス {_profile.WindowProcessName}.exe のウィンドウを探しています…");
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
            Console.WriteLine("ウィンドウが見つかりませんでした。ゲームを起動してからお試しください。");
            return 1;
        }

        using var capturer = new WindowCapturer(hwnd);
        var detector = new FrameDetector(_profile);
        var trackers = new LaneNoteTracker[_profile.LaneCount];
        for (int lane = 0; lane < trackers.Length; lane++)
        {
            trackers[lane] = new LaneNoteTracker(_profile.JudgmentLineY, vanishingY: _profile.Geometry.VanishingY);
        }
        var scheduler = new InputScheduler();
        double offsetSeconds = _options.OffsetMilliseconds / 1000.0;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cancelled = true;
        };

        Console.WriteLine(_options.DryRun
            ? "DRY-RUN で開始します（入力は送りません）。F9 で終了します。"
            : "実プレイを開始します。F9 で終了します。");
        if (_options.OffsetMilliseconds != 0)
        {
            Console.WriteLine($"オフセット: {_options.OffsetMilliseconds:+0;-0} ms");
        }

        long frames = 0;
        int scheduled = 0, fired = 0;
        double startedAt = capturer.Now;

        while (!_cancelled)
        {
            if ((GetAsyncKeyState(VkF9) & 0x8000) != 0)
            {
                break;
            }
            if (_options.DurationSeconds > 0 && capturer.Now - startedAt > _options.DurationSeconds)
            {
                break;
            }

            CapturedFrame frame;
            try
            {
                frame = capturer.Capture();
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(100);
                continue;
            }
            frames++;
            double now = frame.TimeSeconds;

            foreach (IGrouping<int, NoteDetection> group in detector.Detect(frame).GroupBy(d => d.Lane))
            {
                var ready = trackers[group.Key].Observe(now, group.ToList());
                foreach ((NoteKind kind, ArrowDirection? direction, double tHit) in ready)
                {
                    var note = new NoteDetection(group.Key, kind, direction, 0, _profile.JudgmentLineY, now);
                    foreach (ScheduledInput ev in KeyPlanBuilder.BuildTap(_profile, note, tHit + offsetSeconds))
                    {
                        scheduler.Enqueue(ev);
                    }
                    scheduled++;
                    if (_options.DryRun)
                    {
                        Console.WriteLine($"[dry] lane={group.Key + 1} {kind} dir={direction?.ToString() ?? "-"} t_hit まで {tHit - now:F3}s");
                    }
                }
            }

            foreach (ScheduledInput due in scheduler.CollectDue(capturer.Now))
            {
                if (_options.DryRun)
                {
                    continue;
                }
                InputSender.Send(due.Key, due.IsDown);
                if (due.IsDown)
                {
                    fired++;
                }
            }

            Thread.Sleep(1);
        }

        Console.WriteLine($"終了しました。フレーム数={frames}, スケジュールしたノーツ={scheduled}, 発火したキー(down)={fired}");
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
