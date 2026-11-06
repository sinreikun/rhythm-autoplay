using System.Runtime.InteropServices;
using RhythmAutoplay.Core;
using ArrowDirection = RhythmAutoplay.Core.ArrowDirection;

namespace RhythmAutoplay.App;

public sealed class PlayOptions
{
    /// <summary>入力を送らず、検出と発火予定の表示だけにする（較正・検証用）。</summary>
    public bool DryRun { get; init; }
    /// <summary>全ヒットの発火時刻に加えるオフセット [ms]。正で遅く、負で早く。</summary>
    public double OffsetMilliseconds { get; init; }
    /// <summary>自動終了までの秒数。0 = 無制限（F9 / Ctrl+C で終了）。</summary>
    public int DurationSeconds { get; init; }
    /// <summary>予約・発火イベントを時刻つきで書き出す診断ログの保存先。null で無効。</summary>
    public string? EventLogDirectory { get; init; }
}

/// <summary>プレイ中の統計。GUI がポーリングして表示する。</summary>
public sealed class PlayStats
{
    public long Frames;
    public int Scheduled;
    public int Fired;
}

/// <summary>
/// 実プレイ経路: キャプチャ → 検出 → 追跡/到達予測 → スケジュール → SendInput。
/// 終了は F9 キー（ゲーム側のキーと衝突しない）か Ctrl+C、または RequestStop。
/// </summary>
public sealed class PlaySession
{
    private const int VkF9 = 0x78;

    private readonly GameProfile _profile;
    private readonly PlayOptions _options;
    private readonly Action<string>? _log;
    private volatile bool _cancelled;

    public PlaySession(GameProfile profile, PlayOptions options, Action<string>? log = null)
    {
        _profile = profile;
        _options = options;
        _log = log;
    }

    public PlayStats Stats { get; } = new();

    public void RequestStop() => _cancelled = true;

    private void Log(string message)
    {
        Console.WriteLine(message);
        _log?.Invoke(message);
    }

    public int Run()
    {
        Log($"対象プロセス {_profile.WindowProcessName}.exe のウィンドウを探しています…");
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
            Log("ウィンドウが見つかりませんでした。ゲームを起動してからお試しください。");
            return 1;
        }

        using var capturer = new WindowCapturer(hwnd);
        var detector = new FrameDetector(_profile);
        var trackers = new LaneNoteTracker[_profile.LaneCount];
        // 出現ゲートは判定ライン際の静止物（BREAK バー等）だけを弾く緩い
        // 境界に留める。UI と本物の見分けは追跡器側の「動きの確認」が担う:
        // 高速譜面（TRANCE）では本物のノーツの初検出が Y≈0.78〜0.80 まで
        // 下がり、位置ゲートを厳しくすると実ノーツを丸ごと落とす
        // （v0.3.5/0.3.6 の実測: レーンノーツの予約がほぼ全滅した）。
        double spawnGateY = _profile.Geometry.TopY
            + 0.84 * (_profile.JudgmentLineY - _profile.Geometry.TopY);
        for (int lane = 0; lane < trackers.Length; lane++)
        {
            trackers[lane] = new LaneNoteTracker(
                _profile.JudgmentLineY,
                vanishingY: _profile.Geometry.VanishingY,
                spawnGateY: spawnGateY,
                scheduleHorizonSeconds: 1.2,
                commitWindowSeconds: 0.15);
        }
        var scheduler = new InputScheduler();
        double offsetSeconds = _options.OffsetMilliseconds / 1000.0;

        // 診断ログ: 予約 (S) と発火 (F) をセッション時計の時刻つきで残す。
        // 発火行の schedErr は「発火時刻 − 予定時刻」でスケジューラの遅れを示す。
        StreamWriter? eventLog = null;
        if (_options.EventLogDirectory != null)
        {
            Directory.CreateDirectory(_options.EventLogDirectory);
            string path = Path.Combine(_options.EventLogDirectory,
                $"play-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            eventLog = new StreamWriter(path) { AutoFlush = true };
            Log($"セッションログ: {path}");
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cancelled = true;
        };

        Log(_options.DryRun
            ? "DRY-RUN で開始します（入力は送りません）。F9 で終了します。"
            : "実プレイを開始します。F9 で終了します。");
        if (_options.OffsetMilliseconds != 0)
        {
            Log($"オフセット: {_options.OffsetMilliseconds:+0;-0} ms");
        }

        long frames = 0;
        int scheduled = 0, fired = 0;
        double startedAt = capturer.Now;
        var keysDown = new HashSet<string>();

        // 期限の来た入力を先に発火してからキャプチャ・検出に入る
        // （検出処理のぶん発火が遅れるのを避ける）。
        void FireDue()
        {
            foreach (ScheduledInput due in scheduler.CollectDue(capturer.Now))
            {
                if (_options.DryRun)
                {
                    continue;
                }
                if (due.IsDown)
                {
                    // 同キーの連打: 前の押下がまだ保持中なら、先に離してから
                    // 押し直す。離さずに down を重ねるとゲーム側で新しい
                    // 押下として数えられず、連続ノーツを取りこぼす。
                    if (keysDown.Contains(due.Key))
                    {
                        InputSender.Send(due.Key, false);
                    }
                    InputSender.Send(due.Key, true);
                    keysDown.Add(due.Key);
                    fired++;
                    eventLog?.WriteLine(
                        $"F now={capturer.Now:F3} key={due.Key} due={due.TimeSeconds:F3} schedErr={(capturer.Now - due.TimeSeconds) * 1000:F0}ms");
                }
                else if (keysDown.Remove(due.Key))
                {
                    InputSender.Send(due.Key, false);
                }
            }
        }

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

            FireDue();

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
                foreach (ScheduledNote sn in ready)
                {
                    var note = new NoteDetection(group.Key, sn.Kind, sn.Direction, 0, _profile.JudgmentLineY, now);
                    foreach (ScheduledInput ev in KeyPlanBuilder.BuildTap(_profile, note, sn.THit + offsetSeconds))
                    {
                        scheduler.Enqueue(ev);
                    }
                    scheduled++;
                    eventLog?.WriteLine(
                        $"S now={now:F3} lane={group.Key} kind={sn.Kind} dir={sn.Direction} tHit={sn.THit:F3} res={sn.Residual:F4} n={sn.SampleCount} fY={sn.FirstY:F3} ft={sn.FirstTime:F3}");
                    if (_options.DryRun)
                    {
                        Log($"[dry] lane={group.Key + 1} {sn.Kind} dir={sn.Direction?.ToString() ?? "-"} t_hit まで {sn.THit - now:F3}s");
                    }
                }
                foreach (ExpiredTrack ex in trackers[group.Key].DrainExpired())
                {
                    eventLog?.WriteLine(
                        $"X lane={group.Key} kind={ex.Kind} sched={(ex.Scheduled ? 1 : 0)} n={ex.TotalSamples} fY={ex.FirstY:F3} lY={ex.LastY:F3} ft={ex.FirstTime:F3} lt={ex.LastSeenTime:F3} bres={(double.IsInfinity(ex.BestResidual) ? -1 : ex.BestResidual):F4}");
                }
            }

            FireDue();

            Stats.Frames = frames;
            Stats.Scheduled = scheduled;
            Stats.Fired = fired;
            Thread.Sleep(1);
        }

        Log($"終了しました。フレーム数={frames}, スケジュールしたノーツ={scheduled}, 発火したキー(down)={fired}");
        eventLog?.Dispose();
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
