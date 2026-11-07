using System.Drawing;
using System.Drawing.Imaging;
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
    /// <summary>
    /// 診断用: 0 より大きい場合、各ノーツ（矢印を除く）の発火のこの ms 数だけ前に、
    /// 隣レーンの間違ったキーを 1 回落とす。「判定ウィンドウ内の誤キーがノーツを
    /// 消費するか」を実測するための実験機能で、通常は 0 (無効)。
    /// </summary>
    public double InjectStrayMilliseconds { get; init; }
    /// <summary>
    /// 遅延確定の窓 [ms]: 予測ヒットまでこの時間を切るまで予約を確定しない。
    /// 小さいほど標本が溜まって予測が締まるが、確定が遅れる。既定 100
    /// （Floating 実測スイープ: 150→CRINGE 125〜145 / 100→111〜140 で
    /// グレード最良 / 70→174。飢餓救済ありでも 100 が最適だった）。
    /// </summary>
    public double CommitWindowMilliseconds { get; init; } = 100;
    /// <summary>
    /// タップの保持時間上書き [ms]（v0.3.35、実験用）: 0 より大きい場合に
    /// プロファイルの TapHoldMilliseconds を差し替える。ゲーム側の入力
    /// ポーリング（曲中は実測 ~30fps 級）が 40ms のタップを取りこぼす
    /// 仮説の検証用。通常は 0（プロファイル値 40ms）。
    /// </summary>
    public double HoldMilliseconds { get; init; }
    /// <summary>
    /// 診断用（v0.3.39）: 全軌跡の全標本を P 行としてイベントログに残す。
    /// フィット窓（直近12標本 vs 全標本など）の優劣をオフラインで事後評価するため。
    /// </summary>
    public bool LogSamples { get; init; }
    /// <summary>
    /// 診断用（v0.3.56）: ライン在席のビットマスク（ライン×色種）の変化を
    /// PL 行としてイベントログに残す。在席そのものは従来ログに残らず、
    /// 取りこぼしの内訳（検出盲 / 鎖の不成立 / 救済ゲート不通過）を
    /// 事後に切り分けられなかったための計装。挙動は変えない。
    /// </summary>
    public bool LogPresence { get; init; }
    /// <summary>
    /// 検出オーバレイ保存（v0.3.59）: 逃しやすい状況（矢印バンド重なり /
    /// 高密度 / 未予約のまま失効 / 最終ライン救済）が起きた瞬間のフレームに
    /// 検出状態を描き込んで PNG 保存する。保存先は
    /// %LOCALAPPDATA%\RhythmAutoplay\watch\&lt;日時&gt;\。挙動は変えない。
    /// </summary>
    public bool Watch { get; init; }
    /// <summary>
    /// ウィンドウ単体キャプチャ（v0.3.63）: 画面 BitBlt の代わりに
    /// PrintWindow で対象ウィンドウだけを描かせる。重なったオーバレイや
    /// 別ウィンドウが映り込まないため、検出オーバレイを表示したまま
    /// プレイできる（GUI はオーバレイ表示 ON のセッションで自動的に
    /// この方式になる）。代償として 1 フレーム ~27ms と画面方式
    /// （~17ms）より重い。画面方式はオーバレイ OFF の通常プレイ用。
    /// </summary>
    public bool PrintWindowCapture { get; init; }
    /// <summary>
    /// 検出範囲と発火ラインの分離（v0.3.66、実験）: 走査ラインを判定文字帯
    /// より上（0.63/0.665/0.70）に置き、横断観測を演出の無い帯域で完結させる。
    /// 発火は本物の判定ラインへの到達推定のまま。ライン間隔が広がる分、
    /// 鎖の寿命と推定ばらつき許容も広げる。
    /// </summary>
    public bool UpperLines { get; init; }
    /// <summary>従来帯に走査ラインを 5 本置く実験モード（v0.3.68）。</summary>
    public bool DenseLines { get; init; }
    /// <summary>自動終了までの秒数。0 = 無制限（F9 / Ctrl+C で終了）。</summary>
    public int DurationSeconds { get; init; }
    /// <summary>予約・発火イベントを時刻つきで書き出す診断ログの保存先。null で無効。</summary>
    public string? EventLogDirectory { get; init; }
    /// <summary>
    /// 追跡エンジン: "hybrid"（固定ライン横断+塊の穴埋め、v0.3.49〜）、
    /// "line"（固定ライン横断、v0.3.42）、"line1"（単ライン機械式、
    /// v0.3.73）、"track"（全飛行の軌跡追跡、
    /// v0.3.11 系）、"band"（帯域トリガー、v0.3.14）。
    /// 既定は hybrid（v0.3.52）。v0.3.51 までは既定が "track" で、GUI に
    /// エンジン選択肢が無いため、検証で使った hybrid が GUI 起動では
    /// 選ばれない不具合があった（マスターの実プレイで発覚）。
    /// </summary>
    public string Engine { get; init; } = "hybrid";
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
    private volatile bool _fireStop;

    public PlaySession(GameProfile profile, PlayOptions options, Action<string>? log = null)
    {
        _options = options;
        _profile = options.HoldMilliseconds > 0
            ? profile with { TapHoldMilliseconds = options.HoldMilliseconds }
            : profile;
        _log = log;
    }

    public PlayStats Stats { get; } = new();

    /// <summary>最新の検出状態（GUI のライブオーバレイ用）。毎フレーム差し替わる。</summary>
    public DetectionView? LatestView { get; private set; }

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

        using var capturer = new WindowCapturer(hwnd) { UsePrintWindow = _options.PrintWindowCapture };
        var detector = new FrameDetector(_profile);
        var trackers = new ILaneTracker[_profile.LaneCount];
        // 出現ゲートは判定ライン際の静止物（BREAK バー等）だけを弾く緩い
        // 境界に留める。UI と本物の見分けは追跡器側の「動きの確認」が担う:
        // 高速譜面（TRANCE）では本物のノーツの初検出が Y≈0.78〜0.80 まで
        // 下がり、位置ゲートを厳しくすると実ノーツを丸ごと落とす
        // （v0.3.5/0.3.6 の実測: レーンノーツの予約がほぼ全滅した）。
        double spawnGateY = _profile.Geometry.TopY
            + 0.84 * (_profile.JudgmentLineY - _profile.Geometry.TopY);
        bool useBand = string.Equals(_options.Engine, "band", StringComparison.OrdinalIgnoreCase);
        bool useLine = string.Equals(_options.Engine, "line", StringComparison.OrdinalIgnoreCase);
        bool useHybrid = string.Equals(_options.Engine, "hybrid", StringComparison.OrdinalIgnoreCase);
        bool useLine1 = string.Equals(_options.Engine, "line1", StringComparison.OrdinalIgnoreCase);
        detector.LineMode = useLine || useHybrid;
        detector.SingleLineMode = useLine1;
        if (_options.DenseLines)
        {
            LineCrossingTracker.LineYs = LineCrossingTracker.DenseLineYs;
            LineCrossingTracker.ChainTimeoutSeconds = 0.45;
            Log("高密度ライン モード: 走査ライン 0.775/0.81/0.845/0.88/0.905（5 本）");
        }
        if (_options.UpperLines)
        {
            LineCrossingTracker.LineYs = LineCrossingTracker.UpperLineYs;
            LineCrossingTracker.ChainTimeoutSeconds = 0.55;
            LineCrossingTracker.MaxEstimateDeviationSeconds = 0.030;
            LineCrossingTracker.UseSlopeFit = true;
            LineCrossingTracker.HitCorrectionSeconds = 0.003;
            Log("上部ライン モード: 走査ライン 0.63/0.665/0.70（検出範囲と発火ラインの分離）+ 傾きフィット");
        }
        for (int lane = 0; lane < trackers.Length; lane++)
        {
            trackers[lane] = useBand
                ? new BandNoteTracker(
                    _profile.JudgmentLineY,
                    vanishingY: _profile.Geometry.VanishingY)
                : useHybrid
                ? new HybridGapFillTracker(
                    _profile.JudgmentLineY,
                    vanishingY: _profile.Geometry.VanishingY,
                    spawnGateY: spawnGateY,
                    commitWindowSeconds: _options.CommitWindowMilliseconds / 1000.0)
                : useLine1
                ? new SingleLineTracker(
                    _profile.JudgmentLineY,
                    vanishingY: _profile.Geometry.VanishingY)
                : useLine
                ? new LineCrossingTracker(
                    _profile.JudgmentLineY,
                    vanishingY: _profile.Geometry.VanishingY,
                    spawnGateY: spawnGateY,
                    commitWindowSeconds: _options.CommitWindowMilliseconds / 1000.0)
                : new LaneNoteTracker(
                    _profile.JudgmentLineY,
                    vanishingY: _profile.Geometry.VanishingY,
                    spawnGateY: spawnGateY,
                    scheduleHorizonSeconds: 1.2,
                    commitWindowSeconds: _options.CommitWindowMilliseconds / 1000.0);
            trackers[lane].CollectSamples = _options.LogSamples;
        }
        Log($"追跡エンジン: {(useBand ? "band（帯域トリガー）" : useHybrid ? "hybrid（ライン主軸+塊の穴埋め）" : useLine1 ? "line1（単ライン機械式）" : useLine ? "line（固定ライン横断）" : "track（軌跡追跡）")}");
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

        Log($"設定: offset={_options.OffsetMilliseconds}ms commit={_options.CommitWindowMilliseconds}ms stray={_options.InjectStrayMilliseconds}ms engine={_options.Engine} hold={_profile.TapHoldMilliseconds}ms");
        Log(_options.DryRun
            ? "DRY-RUN で開始します（入力は送りません）。F9 で終了します。"
            : "実プレイを開始します。F9 で終了します。");
        if (_options.OffsetMilliseconds != 0)
        {
            Log($"オフセット: {_options.OffsetMilliseconds:+0;-0} ms");
        }

        long frames = 0;
        int scheduled = 0, fired = 0;
        // 段階別コスト計測（v0.3.33、診断用）: ループが約31fpsに張り付く
        // 内訳（キャプチャ / 検出 / 追跡+予約）を実測してから最適化する。
        double capMs = 0, detMs = 0, trkMs = 0;
        double startedAt = capturer.Now;
        var keysDown = new HashSet<string>();

        // v0.3.17: 発火は専用スレッドが担当する。従来はキャプチャループ内
        // でポーリングしており、発火遅れ (schedErr) は平均 +9ms / 標準偏差
        // 5ms / 最大 +39ms まで広がっていた（v0.3.16 の play ログ実測）。
        // v0.3.18: ロック分離・送信のロック外し・F 行のバッファ化で平均
        // +5.4ms まで改善したが、6〜10ms の山が残った。待ちループが
        // 4ms 上限のポーリングで、その内側で毎周ログ flush が走って
        // 周期を伸ばしていたのが原因。
        // v0.3.19: イベント駆動にする。メインスレッドが予約を入れるたび
        // fireWake を立て、発火スレッドは次の発火時刻までイベントで
        // 眠り、最後の 3ms だけスピンで詰める。ログ flush は待ち時間が
        // 本当に長いとき（>25ms）か、バッファが溜まったときだけ行う。
        // v0.3.20: それでも schedErr の平均 +5ms と 6〜10ms の山が残った
        // （バッチ内直列は否定済み: 同時発火グループ内の位置で平均は
        // 変わらない）。OS のイベント待ちからの復帰遅れが疑わしいため、
        // スピン区間を最後の 12ms まで広げ、イベント待ちは due の
        // 10ms 手前で切り上げた。schedErr は平均 +0.9ms まで潰れたが、
        // Floating の CRINGE が 2 ランとも悪化した (130/125 → 154/147)。
        // 長いスピンが密度の高い場面でキャプチャ側を圧迫したと見て、
        // v0.3.21 でスピン区間を 3ms に戻す。発火遅れの数値ではなく
        // 判定結果で選ぶ。
        var schedGate = new object();
        var logGate = new object();
        var fireWake = new ManualResetEventSlim(false);

        void LogEvent(string line)
        {
            lock (logGate)
            {
                eventLog?.WriteLine(line);
            }
        }

        // 発火スレッド専用。due イベントを取り出して送信し、F 行は
        // バッファへ積む（書き出しは FlushLogBuffer が行う）。
        void FireDue(List<string> logBuffer)
        {
            IReadOnlyList<ScheduledInput> dueEvents;
            lock (schedGate)
            {
                dueEvents = scheduler.CollectDue(capturer.Now);
            }
            foreach (ScheduledInput due in dueEvents)
            {
                double firedAt = capturer.Now;
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
                    logBuffer.Add(
                        $"F now={firedAt:F3} key={due.Key} due={due.TimeSeconds:F3} schedErr={(firedAt - due.TimeSeconds) * 1000:F0}ms");
                }
                else if (keysDown.Remove(due.Key))
                {
                    InputSender.Send(due.Key, false);
                }
            }
        }

        void FlushLogBuffer(List<string> logBuffer)
        {
            if (logBuffer.Count > 0 && eventLog is not null)
            {
                lock (logGate)
                {
                    eventLog.WriteLine(string.Join(Environment.NewLine, logBuffer));
                }
            }
            logBuffer.Clear();
        }

        _fireStop = false;
        timeBeginPeriod(1);
        var fireThread = new Thread(() =>
        {
            var logBuffer = new List<string>();
            try
            {
                while (!_cancelled && !_fireStop)
                {
                    FireDue(logBuffer);
                    double realWaitMs;
                    lock (schedGate)
                    {
                        double? next = scheduler.NextTimeSeconds;
                        realWaitMs = next is null
                            ? 50.0
                            : Math.Max(0.0, (next.Value - capturer.Now) * 1000.0);
                    }
                    // ログ flush は待ち時間が本当に長いときだけ。周期の
                    // 内側で毎回流すと発火チェックそのものが遅れる。
                    if (realWaitMs > 25.0 || logBuffer.Count >= 64)
                    {
                        FlushLogBuffer(logBuffer);
                    }
                    if (realWaitMs > 3.0)
                    {
                        // 新しい予約が入ったらメインスレッドが起こす。
                        // 寝過ごしても次のループでキューを見直す。
                        fireWake.Wait((int)(realWaitMs - 2.0));
                        fireWake.Reset();
                    }
                    else if (realWaitMs > 0.0)
                    {
                        double deadline = capturer.Now + realWaitMs / 1000.0;
                        while (!_cancelled && !_fireStop && capturer.Now < deadline)
                        {
                            Thread.SpinWait(256);
                        }
                    }
                }
            }
            finally
            {
                FlushLogBuffer(logBuffer);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "RhythmAutoplay.Fire",
        };
        fireThread.Start();

        // v0.3.16: セッション側の二重発火抑制。従来方式では同じ物理ノーツが
        // 2 つの断片から二重に予約される実測がある（同一レーン・同種で
        // 到達時刻差の中央値 11〜20ms、曲により 69〜131 組）。帯域方式で
        // 入れた抑制を、キー集合が同じで到達時刻が 35ms 以内の予約にも
        // 適用する。実在する同キー連続ノーツは音楽的に 35ms 未満では
        // 並ばない前提。
        var recentSchedules = new List<(string Signature, double THit, double At)>();
        // ライン在席ログ（v0.3.56、診断用）: レーンごとの前フレームの
        // 在席マスク。ビット位置は ライン番号×5 + 色種番号。
        var prevPresenceMask = new int[_profile.LaneCount];
        // 単ライン状態ログ（v0.3.73、診断用）: ビット配置は PL1 の
        // 書き出し箇所に記載（v0.3.74 で重なりを修正済み）。
        var prevLine1Mask = new int[_profile.LaneCount];
        // 予約の事後修正（v0.3.37）: 受け付けた予約にタグを付け、追跡器から
        // 修正が来たらスケジューラ内の未発火イベントをずらす。対応表は
        // (レーン, 署名, 現在の到達時刻) → タグ。修正のたびに時刻を更新する。
        long scheduleTag = 0;
        var liveSchedules = new List<(int Lane, string Signature, double THit, long Tag)>();

        // 検出オーバレイ（v0.3.59）: viewUpcoming はライブオーバレイの
        // 予約マーカー用。--watch では直近フレームのリングを持ち、逃し
        // やすい状況のトリガで検出状態を描いた PNG を保存する。保存の
        // 失敗で演奏を止めないよう、保存側の例外は全て握りつぶす。
        var viewUpcoming = new List<(int Lane, NoteKind Kind, double THit)>();
        string? watchDir = null;
        StreamWriter? watchIndex = null;
        var watchRing = new Queue<(double T, byte[] Bgra, int W, int H, DetectionView View)>();
        int watchSaved = 0;
        double lastWaveSave = -10, lastDenseSave = -10, lastExpireSave = -10;
        if (_options.Watch)
        {
            watchDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RhythmAutoplay", "watch", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(watchDir);
            watchIndex = new StreamWriter(Path.Combine(watchDir, "index.txt")) { AutoFlush = true };
            Log($"検出オーバレイ保存: {watchDir}");
        }

        void SaveWatchFrame(
            (double T, byte[] Bgra, int W, int H, DetectionView View) entry,
            string trigger, string detail)
        {
            if (watchDir == null || watchSaved >= 80)
            {
                return;
            }
            try
            {
                watchSaved++;
                string name = $"watch_{watchSaved:D3}_{trigger}.png";
                using var bmp = new Bitmap(entry.W, entry.H, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, entry.W, entry.H);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    if (data.Stride == entry.W * 4)
                    {
                        Marshal.Copy(entry.Bgra, 0, data.Scan0,
                            Math.Min(entry.Bgra.Length, data.Stride * entry.H));
                    }
                    else
                    {
                        for (int y = 0; y < entry.H; y++)
                        {
                            Marshal.Copy(entry.Bgra, y * entry.W * 4,
                                data.Scan0 + y * data.Stride, entry.W * 4);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                using (var g = Graphics.FromImage(bmp))
                {
                    DetectionPainter.Draw(g, entry.View, _profile, entry.W, entry.H,
                        $"t={entry.T:F3} trigger={trigger} {detail}");
                }
                bmp.Save(Path.Combine(watchDir, name), ImageFormat.Png);
                watchIndex?.WriteLine($"{name} t={entry.T:F3} trigger={trigger} {detail}");
            }
            catch
            {
                // 診断保存の失敗で演奏を止めない。
            }
        }

        (double T, byte[] Bgra, int W, int H, DetectionView View)? RingFrameNear(double t)
        {
            (double T, byte[] Bgra, int W, int H, DetectionView View)? best = null;
            double bestDiff = double.MaxValue;
            foreach (var e in watchRing)
            {
                double diff = Math.Abs(e.T - t);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = e;
                }
            }
            return best;
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

            CapturedFrame frame;
            double capStart = capturer.Now;
            try
            {
                frame = capturer.Capture();
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(100);
                continue;
            }
            double capEnd = capturer.Now;
            capMs += (capEnd - capStart) * 1000.0;
            frames++;
            double now = frame.TimeSeconds;

            List<NoteDetection> detections = detector.Detect(frame);
            detMs += (capturer.Now - capEnd) * 1000.0;
            if (_options.LogPresence)
            {
                var masks = new int[_profile.LaneCount];
                foreach (NoteDetection d in detections)
                {
                    if (!d.IsLinePresence || d.Kind == NoteKind.Arrow)
                    {
                        continue;
                    }
                    int li = -1;
                    for (int i = 0; i < LineCrossingTracker.LineYs.Length; i++)
                    {
                        if (Math.Abs(d.Y - LineCrossingTracker.LineYs[i]) < 0.01)
                        {
                            li = i;
                            break;
                        }
                    }
                    if (li >= 0 && d.Lane >= 0 && d.Lane < masks.Length)
                    {
                        masks[d.Lane] |= 1 << (li * 5 + (int)d.Kind);
                    }
                }
                for (int lane = 0; lane < masks.Length; lane++)
                {
                    if (masks[lane] != prevPresenceMask[lane])
                    {
                        LogEvent($"PL t={now:F3} lane={lane} mask={masks[lane]:X4}");
                        prevPresenceMask[lane] = masks[lane];
                    }
                }
            }
            if (_options.LogPresence && useLine1)
            {
                IReadOnlyList<Line1LaneState> states = detector.LastLine1States;
                for (int lane = 0; lane < states.Count && lane < prevLine1Mask.Length; lane++)
                {
                    Line1LaneState st = states[lane];
                    // ビット配置（v0.3.74 で重なりを修正）: 0-3 = 通常読み、
                    // 4-7 = 薄い読み、8 = 白の重なり、9 = 波の重なり、
                    // 10 = 紫相の証拠。初版は薄い読みを <<3 に置き、通常
                    // 読みの黄（ビット 3）と白（ビット 6）が衝突していた。
                    int m = st.StrictMask | (st.DimMask << 4)
                        | (st.WhiteCount >= 2 ? 1 << 8 : 0)
                        | (st.InWave ? 1 << 9 : 0)
                        | (st.PurpleCount >= 2 ? 1 << 10 : 0);
                    if (m != prevLine1Mask[lane])
                    {
                        LogEvent($"PL1 t={now:F3} lane={lane} m={m:X4}");
                        prevLine1Mask[lane] = m;
                    }
                }
            }
            viewUpcoming.RemoveAll(u => now > u.THit + 0.3);
            var view = new DetectionView(
                now, frame.Width, frame.Height, detections, detector.LastBands,
                viewUpcoming.ToList());
            LatestView = view;
            if (_options.Watch && now - startedAt >= 5.0)
            {
                // 開始 5 秒は曲のイントロ演出（ARE YOU…）がバンドや在席に
                // 混ざるため保存対象から外す（v0.3.60、初回実走の実測）。
                var entry = (now, (byte[])frame.Bgra.Clone(), frame.Width, frame.Height, view);
                watchRing.Enqueue(entry);
                while (watchRing.Count > 10)
                {
                    watchRing.Dequeue();
                }
                int presenceCount = detections.Count(d => d.IsLinePresence);
                if (detector.LastBands.Count > 0 && presenceCount > 0 && now - lastWaveSave >= 2.5)
                {
                    lastWaveSave = now;
                    SaveWatchFrame(entry, "wave",
                        $"bands={detector.LastBands.Count} pres={presenceCount}");
                }
                else if (presenceCount >= 8 && now - lastDenseSave >= 2.5)
                {
                    // 在席セルは最大 12（4 レーン × 3 ライン）。v0.3.59 の
                    // 閾値 12 は満杯条件で 1 回しか発火しなかった。
                    lastDenseSave = now;
                    SaveWatchFrame(entry, "dense", $"pres={presenceCount}");
                }
            }
            double trkStart = capturer.Now;

            foreach (IGrouping<int, NoteDetection> group in detections.GroupBy(d => d.Lane))
            {
                var ready = trackers[group.Key].Observe(now, group.ToList());
                foreach (ScheduledNote sn in ready)
                {
                    var note = new NoteDetection(group.Key, sn.Kind, sn.Direction, 0, _profile.JudgmentLineY, now);
                    var events = KeyPlanBuilder.BuildTap(_profile, note, sn.THit + offsetSeconds).ToList();
                    string signature = string.Join(",", events.Select(e => e.Key).Distinct().OrderBy(k => k));
                    recentSchedules.RemoveAll(r => now - r.At > 1.0);
                    if (recentSchedules.Any(r => r.Signature == signature && Math.Abs(r.THit - sn.THit) <= 0.035))
                    {
                        eventLog?.WriteLine(
                            $"D lane={group.Key} kind={sn.Kind} tHit={sn.THit:F3}");
                        continue;
                    }
                    recentSchedules.Add((signature, sn.THit, now));
                    long tag = ++scheduleTag;
                    liveSchedules.Add((group.Key, signature, sn.THit, tag));
                    var enqueued = events.Select(e => e with { Tag = tag }).ToList();
                    if (_options.InjectStrayMilliseconds > 0 && sn.Kind != NoteKind.Arrow)
                    {
                        // 診断: 本発火の少し前に隣レーンの誤キーを落とす。
                        // 重複抑制の署名には含めない（実験が本線を壊さない）。
                        var strayNote = new NoteDetection(
                            (group.Key + 1) % _profile.Lanes.Count, NoteKind.Red, null, 0, 0, now);
                        double strayAt = sn.THit + offsetSeconds - _options.InjectStrayMilliseconds / 1000.0;
                        enqueued.AddRange(KeyPlanBuilder.BuildTap(_profile, strayNote, strayAt));
                    }
                    lock (schedGate)
                    {
                        foreach (ScheduledInput ev in enqueued)
                        {
                            scheduler.Enqueue(ev);
                        }
                    }
                    fireWake.Set();
                    scheduled++;
                    LogEvent(
                        $"S now={now:F3} lane={group.Key} kind={sn.Kind} dir={sn.Direction} tHit={sn.THit:F3} res={sn.Residual:F4} n={sn.SampleCount} fY={sn.FirstY:F3} ft={sn.FirstTime:F3}");
                    if (_options.DryRun)
                    {
                        Log($"[dry] lane={group.Key + 1} {sn.Kind} dir={sn.Direction?.ToString() ?? "-"} t_hit まで {sn.THit - now:F3}s");
                    }
                    viewUpcoming.Add((group.Key, sn.Kind, sn.THit));
                    if (_options.Watch && sn.SampleCount == 1
                        && Math.Abs(sn.FirstY - LineCrossingTracker.LineYs[2]) < 0.01)
                    {
                        // 最終ライン救済の発火: ノーツが最終ラインを横切る
                        // 瞬間のフレームを残す（救済が拾った証拠の確認用）。
                        var e = RingFrameNear(sn.THit - 0.109);
                        if (e != null)
                        {
                            SaveWatchFrame(e.Value, "rescue",
                                $"lane={group.Key} kind={sn.Kind} tHit={sn.THit:F3}");
                        }
                    }
                }
                if (_options.LogSamples)
                {
                    foreach ((int id, double st, double sy) in trackers[group.Key].DrainSampleLog())
                    {
                        LogEvent($"P lane={group.Key} id={id} t={st:F3} y={sy:F3}");
                    }
                }
                foreach (SuppressedDuplicate sd in trackers[group.Key].DrainSuppressed())
                {
                    LogEvent(
                        $"D lane={group.Key} kind={sd.Kind} tHit={sd.THit:F3}");
                }
                liveSchedules.RemoveAll(e => now > e.THit + 0.5);
                foreach (RevisedNote rv in trackers[group.Key].DrainRevisions())
                {
                    int idx = liveSchedules.FindIndex(
                        e => e.Lane == group.Key && Math.Abs(e.THit - rv.OldHit) < 1e-9);
                    if (idx < 0)
                    {
                        continue;
                    }
                    var entry = liveSchedules[idx];
                    bool shifted;
                    lock (schedGate)
                    {
                        shifted = scheduler.ShiftPending(entry.Tag, rv.NewHit - rv.OldHit);
                    }
                    if (!shifted)
                    {
                        continue; // 発火済み。もう動かせない。
                    }
                    liveSchedules[idx] = (entry.Lane, entry.Signature, rv.NewHit, entry.Tag);
                    int ridx = recentSchedules.FindIndex(
                        r => r.Signature == entry.Signature && Math.Abs(r.THit - rv.OldHit) < 1e-9);
                    if (ridx >= 0)
                    {
                        recentSchedules[ridx] = (entry.Signature, rv.NewHit, recentSchedules[ridx].At);
                    }
                    fireWake.Set();
                    LogEvent(
                        $"R now={now:F3} lane={group.Key} kind={rv.Kind} from={rv.OldHit:F3} to={rv.NewHit:F3} res={rv.Residual:F4} n={rv.SampleCount}");
                }
                foreach (ExpiredTrack ex in trackers[group.Key].DrainExpired())
                {
                    LogEvent(
                        $"X lane={group.Key} kind={ex.Kind} sched={(ex.Scheduled ? 1 : 0)} n={ex.TotalSamples} fY={ex.FirstY:F3} lY={ex.LastY:F3} ft={ex.FirstTime:F3} lt={ex.LastSeenTime:F3} bres={(double.IsInfinity(ex.BestResidual) ? -1 : ex.BestResidual):F4} why={ex.FailReason} dz={ex.DeathbedReason} blr={ex.BestLineResidual:F4}");
                    if (_options.Watch && !ex.Scheduled && ex.TotalSamples >= 2
                        && now - startedAt >= 5.0 && now - lastExpireSave >= 0.8)
                    {
                        // 未予約のまま失効した鎖 = 取りこぼしの瞬間。鎖が
                        // 最後に見えた時刻のフレームを残す。標本 1 個だけの
                        // 断片は反響・ちらつきが大半で、初回実走では保存
                        // 上限 80 枚が 38 秒で尽きたため対象から外す。
                        var e = RingFrameNear(ex.LastSeenTime);
                        if (e != null)
                        {
                            lastExpireSave = now;
                            SaveWatchFrame(e.Value, "expire",
                                $"lane={group.Key} kind={ex.Kind} why={ex.FailReason} n={ex.TotalSamples}");
                        }
                    }
                }
            }

            trkMs += (capturer.Now - trkStart) * 1000.0;
            // 段階別コストは 600 フレームごとにイベントログへも出す（v0.3.34）。
            // 終了時の要約だけだと drive の強制終了で毎回失われる（v0.3.33 の
            // 実測ランで発生）。本体の標準出力もリダイレクト時は終了まで
            // フラッシュされないため、頼れるのはイベントログだけ。
            if (frames % 600 == 0)
            {
                eventLog?.WriteLine($"# timing capture={capMs / frames:F2} detect={detMs / frames:F2} track={trkMs / frames:F2}");
            }

            Stats.Frames = frames;
            Stats.Scheduled = scheduled;
            Stats.Fired = fired;
            Thread.Sleep(1);
        }

        _fireStop = true;
        fireWake.Set();
        fireThread.Join(2000);
        fireWake.Dispose();
        timeEndPeriod(1);
        // 押下中のキーが残っていたら離す（セッション終了時の後始末）。
        if (!_options.DryRun)
        {
            foreach (string key in keysDown)
            {
                InputSender.Send(key, false);
            }
        }
        keysDown.Clear();

        Log($"終了しました。フレーム数={frames}, スケジュールしたノーツ={scheduled}, 発火したキー(down)={fired}");
        if (frames > 0)
        {
            Log($"段階別コスト平均(ms): capture={capMs / frames:F2} detect={detMs / frames:F2} track={trkMs / frames:F2} period={(capturer.Now - startedAt) / frames * 1000.0:F2}");
            eventLog?.WriteLine($"# timing capture={capMs / frames:F2} detect={detMs / frames:F2} track={trkMs / frames:F2}");
        }
        eventLog?.Dispose();
        watchIndex?.Dispose();
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);
}
