using RhythmAutoplay.Core;
using ArrowDirection = RhythmAutoplay.Core.ArrowDirection;

namespace RhythmAutoplay.App;

/// <summary>
/// フレームからノーツを検出する（A 型: レーン落下。透視はレーン中心線の
/// 直線性で吸収する）。処理は2パス:
/// 1) 各レーン中心線を縦に走査し、色 run をノーツ塊として拾う。
/// 2) 横長ノーツ（矢印）はレーン幅を超える横 run として別パスで拾い、
///    中央行の突出で方向を推定する（実機較正の対象）。
/// </summary>
/// <summary>単ライン走査のレーン別状態（PL1 診断用）。マスクのビット位置は色種番号（NoteKind）。</summary>
public readonly record struct Line1LaneState(
    int StrictMask, int DimMask, int WhiteCount, bool InWave, int PurpleCount);

public sealed class FrameDetector
{
    private const int MinBlobPx = 7;
    private const double WideFactor = 1.7;

    // 画素絶対の定数はすべて基準解像度 1280x720 で較正されている。
    // v0.3.69 でフレーム寸法比の読み替え（プリセットの画面追従）を入れたが、
    // FS 実走で矢印検出が半減し（306 -> 158）、黄の在席も膨らんだ。
    // 対照実験（v0.3.68 バイナリ、同じ FS・同日）で矢印 325 と無事だった
    // ため、距離量のスケールは字形・標本格子に紐づく絶対量と結論し、
    // v0.3.72 で全廃した。画面追従は分数ジオメトリとコンテンツ矩形
    // （v0.3.71）が担う。将来プロファイル側に基準解像度を持たせる場合も、
    // この実測を踏まえること。
    private const double BaseFrameWidth = 1280.0;
    private const double BaseFrameHeight = 720.0;
    private double _sx = 1.0; // 未使用（将来のプロファイル基準解像度用に残置）
    private double _sy = 1.0; // 未使用（同上）

    /// <summary>縦の画素量。解像度では読み替えない（v0.3.72、上記参照）。</summary>
    private int V(double pxAtBase) => Math.Max(1, (int)Math.Round(pxAtBase));

    private readonly GameProfile _profile;

    public FrameDetector(GameProfile profile) => _profile = profile;

    /// <summary>固定ライン横断方式（engine=line）で使うライン在席走査の有効化。</summary>
    public bool LineMode { get; set; }

    /// <summary>単ライン機械式（engine=line1）で使う単ライン走査の有効化。</summary>
    public bool SingleLineMode { get; set; }

    /// <summary>直近の Detect の単ライン走査のレーン別状態（PL1 診断用）。</summary>
    public IReadOnlyList<Line1LaneState> LastLine1States { get; private set; }
        = Array.Empty<Line1LaneState>();

    /// <summary>傾き参照ライン（UpperLineY）のレーン別状態（PL2 診断用、v0.3.77）。</summary>
    public IReadOnlyList<Line1LaneState> LastLine2States { get; private set; }
        = Array.Empty<Line1LaneState>();

    /// <summary>PLR 較正用: 発火ライン走査の標本 HSV を記録するか（v0.3.80）。</summary>
    public bool CaptureLineSamples { get; set; }

    /// <summary>直近の Detect の発火ライン標本 HSV [レーン, 標本 9, H/S/V]。
    /// 標本の順序は行（-2px, 0, +2px）×列（中央, -30%, +30%）。</summary>
    public double[,,] LastLineSamples { get; private set; } = new double[0, 0, 0];

    /// <summary>直近の Detect の発火ライン標本が波の帯内だったか（レーン別）。</summary>
    public bool[] LastLineSampleWave { get; private set; } = Array.Empty<bool>();

    /// <summary>直近の Detect で見つけた矢印バンドの行範囲（フレーム px）。
    /// オーバレイ／watch 描画用（v0.3.59）。Detect のたびに差し替わる。</summary>
    public IReadOnlyList<(int TopPy, int BottomPy)> LastBands { get; private set; }
        = Array.Empty<(int, int)>();

    public List<NoteDetection> Detect(CapturedFrame frame)
    {
        var results = new List<NoteDetection>();
        LaneGeometry geo = _profile.Geometry;
        int w = frame.ViewWidth, h = frame.ViewHeight;
        _sx = w / BaseFrameWidth;
        _sy = h / BaseFrameHeight;
        int pyTop = (int)(geo.TopY * h);
        int pyJudge = (int)(_profile.JudgmentLineY * h);

        // 先に矢印パスで帯の Y 範囲を知る。通常ノーツが矢印と同時に来ると
        // 白帯の上に半透明で重なって見落としやすいため（マスター実機確認
        // 2026-11-06）、レーンパスでは帯の内側だけ緩い分類と橋渡しを使う。
        var bands = new List<(int TopPy, int BottomPy)>();
        DetectArrows(frame, pyTop, pyJudge, results, bands);
        // 描画側はフレーム全体の座標で受けるため、帯だけ全体座標へ戻す。
        LastBands = bands
            .Select(b => (b.TopPy + frame.ViewY, b.BottomPy + frame.ViewY))
            .ToList();
        for (int lane = 0; lane < _profile.LaneCount; lane++)
        {
            DetectLane(frame, lane, pyTop, pyJudge, bands, results);
        }
        if (SingleLineMode)
        {
            ScanSingleLine(frame, bands, results);
        }
        else if (LineMode)
        {
            ScanLines(frame, bands, results);
        }
        return results;
    }

    /// <summary>
    /// 単ライン走査（v0.3.73、engine=line1）。判定文字とエフェクトの届かない
    /// 高さの 1 本のラインについて、各レーンの「色相ファミリー / 白の重なり /
    /// 波の重なり」を別々に読み、組み合わせ（パターン）として確定した原子色
    /// だけを在席として出す。白が被ると彩度が落ちて通常分類から外れるため、
    /// 色の証拠は (a) 通常分類、(b) 波の帯の内側だけ色相のみの緩い分類、の
    /// 順で集める。パターンの違いは数え方のためだけにあり、押すキーは
    /// 原子色に共通化される（赤系は全部そのレーンの赤キー）。
    /// 薄い読み（ClassifyDim）は発火には使わず、影の証拠として
    /// LastLine1States に記録する（PL1 診断）。紫相の証拠は「未解決の
    /// 重なり」として数だけ記録し、発火させない。
    /// </summary>
    private void ScanSingleLine(
        CapturedFrame frame, List<(int TopPy, int BottomPy)> bands,
        List<NoteDetection> results)
    {
        LaneGeometry geo = _profile.Geometry;
        int w = frame.ViewWidth, h = frame.ViewHeight;
        // 発火ライン (LineY) と傾き参照ライン (UpperLineY) の 2 本を走査する
        // (v0.3.77)。走査と確定の規則は両ラインで同一で、参照ラインの
        // 在席は「傾きを測るための横断」としてだけ使われる。
        foreach (double lineY in new[] { SingleLineTracker.UpperLineY, SingleLineTracker.LineY })
        {
        int pyC = (int)(lineY * h);
        var states = new Line1LaneState[_profile.LaneCount];
        double[,,] samples = CaptureLineSamples && lineY == SingleLineTracker.LineY
            ? new double[_profile.LaneCount, 9, 3]
            : null;
        bool[] sampleWave = samples != null ? new bool[_profile.LaneCount] : null;
        for (int lane = 0; lane < _profile.LaneCount; lane++)
        {
            double cx = geo.LaneCenterX(lane, _profile.LaneCount, lineY);
            double halfSpread = geo.LaneWidthAt(_profile.LaneCount, lineY) * w * 0.30;
            var strictCounts = new int[6];
            var dimCounts = new int[6];
            int whiteCount = 0;
            bool inWave = false;
            int step = V(2);
            int rowIdx = 0;
            for (int dpy = -step; dpy <= step; dpy += step, rowIdx++)
            {
                int py = Math.Clamp(pyC + dpy, 0, h - 1);
                bool inBand = false;
                foreach ((int top, int bottom) in bands)
                {
                    if (py >= top && py <= bottom)
                    {
                        inBand = true;
                        break;
                    }
                }
                if (inBand)
                {
                    inWave = true;
                }
                int colIdx = 0;
                foreach (double off in new[] { 0.0, -halfSpread, halfSpread })
                {
                    int px = Math.Clamp((int)(cx * w + off), 0, w - 1);
                    var (r, g, b) = frame.ViewPixelAt(px, py);
                    if (samples != null)
                    {
                        var (hh, ssv, vvv) = ColorClassifier.RgbToHsv(r, g, b);
                        int si = rowIdx * 3 + colIdx;
                        samples[lane, si, 0] = hh;
                        samples[lane, si, 1] = ssv;
                        samples[lane, si, 2] = vvv;
                    }
                    colIdx++;
                    PixelColor c = ColorClassifier.Classify(r, g, b);
                    if (c == PixelColor.None && inBand)
                    {
                        // 波の白の上に半透明で重なったノーツは、色相だけの
                        // 緩い分類で拾う（波赤・波青などのパターン）。
                        c = ColorClassifier.ClassifyLoose(r, g, b);
                    }
                    if (c != PixelColor.None)
                    {
                        strictCounts[(int)c]++;
                    }
                    else
                    {
                        PixelColor cd = ColorClassifier.ClassifyDim(r, g, b);
                        if (cd != PixelColor.None)
                        {
                            dimCounts[(int)cd]++;
                        }
                    }
                    if (IsWhiteMark(frame, px, py))
                    {
                        whiteCount++;
                    }
                }
            }
            int strictMask = 0, dimMask = 0;
            foreach (PixelColor pc in new[] { PixelColor.Red, PixelColor.Blue, PixelColor.Yellow })
            {
                if (strictCounts[(int)pc] >= 2)
                {
                    strictMask |= 1 << (int)ToKind(pc);
                }
                if (dimCounts[(int)pc] >= 2)
                {
                    dimMask |= 1 << (int)ToKind(pc);
                }
            }
            // パターンの確定: 証拠の最も多い色ファミリーを、そのレーンの
            // 原子色として 1 件だけ在席にする（勝者総取り）。v0.3.74 で
            // 「証拠のある色を全部独立に出す」方式を試したが、合意集合
            // との照合でゴミ発火が約 300 件に爆発し、カバー率も落ちた
            // （在席が他色に切られず伸びて隣のノーツと融合する／波の
            // 緩い読みが誤った時刻に在席を立てる）。勝者総取りには
            // エピソードを区切る働きがあり、v0.3.73 の実測が最良だった
            // ため差し戻した。白・波の重なりは確定を変えない（キーは
            // 原子色に共通化されるため）。
            PixelColor best = PixelColor.None;
            int bestCount = 1; // 在席には同色 2 標本以上の証拠を要求する
            foreach (PixelColor pc in new[] { PixelColor.Red, PixelColor.Blue, PixelColor.Yellow })
            {
                if (strictCounts[(int)pc] > bestCount)
                {
                    bestCount = strictCounts[(int)pc];
                    best = pc;
                }
            }
            if (best != PixelColor.None)
            {
                results.Add(new NoteDetection(
                    lane, ToKind(best), null, cx, lineY, frame.TimeSeconds,
                    IsLinePresence: true));
            }
            states[lane] = new Line1LaneState(
                strictMask, dimMask, whiteCount, inWave,
                strictCounts[(int)PixelColor.Purple]);
            if (sampleWave != null)
            {
                sampleWave[lane] = inWave;
            }
        }
        if (lineY == SingleLineTracker.LineY)
        {
            LastLine1States = states;
            if (samples != null)
            {
                LastLineSamples = samples;
                LastLineSampleWave = sampleWave;
            }
        }
        else
        {
            LastLine2States = states;
        }
        }
    }

    /// <summary>
    /// 固定ライン上の在席走査（v0.3.42）。各ライン・各レーンについて、
    /// ライン行（±2px）とレーン 3 列のどこかにノーツ色があれば、その
    /// ラインにその色が在席するという検出を 1 件出す。在席の立ち上がりを
    /// 横断イベントとして読むのは LineCrossingTracker の仕事で、ここは
    /// 「その行に色があるか」だけを毎フレーム申告する。塊の成立条件を
    /// 課さないため、薄い・小さいノーツでもライン上では拾える。
    /// </summary>
    private void ScanLines(
        CapturedFrame frame, List<(int TopPy, int BottomPy)> bands,
        List<NoteDetection> results)
    {
        LaneGeometry geo = _profile.Geometry;
        int w = frame.ViewWidth, h = frame.ViewHeight;
        foreach (double lineY in LineCrossingTracker.LineYs)
        {
            int pyC = (int)(lineY * h);
            for (int lane = 0; lane < _profile.LaneCount; lane++)
            {
                double cx = geo.LaneCenterX(lane, _profile.LaneCount, lineY);
                double halfSpread = geo.LaneWidthAt(_profile.LaneCount, lineY) * w * 0.30;
                // 在席には同色 2 標本以上の証拠を要求する（v0.3.43）。
                // 1 標本でも在席にすると単画素のちらつきが横断イベントに
                // なり、1 横断だけの鎖が大量に生まれた（v0.3.42 実測）。
                var counts = new int[6];
                int step = V(2);
                for (int dpy = -step; dpy <= step; dpy += step)
                {
                    int py = Math.Clamp(pyC + dpy, 0, h - 1);
                    // 矢印バンドの内側では、通常分類で取れない場合に限り
                    // 色相だけの緩い分類で拾う（v0.3.46）。白帯の上に
                    // 半透明で重なったノーツは彩度・明度が落ちて通常分類を
                    // 落ち、横断の鎖が切れていた（Floating の CRINGE 44 の
                    // 主因候補、マスター実機観察 2026-11-07）。レーンパスの
                    // ClassifyRow と同じ規則にそろえる。
                    // v0.3.58 で足したバンド上下マージン（±0.04*h）は
                    // v0.3.60 で撤回。実測で Floating CRINGE 25→28、
                    // GOODBOUNCE 107→119 と改善が無く、帯の縁にいる
                    // ノーツが主因ではないことが分かったため。盲目分は
                    // 白板の上での全面遮蔽か、フレーム間の短時間在席が
                    // 本命として残る。
                    bool inBand = false;
                    foreach ((int top, int bottom) in bands)
                    {
                        if (py >= top && py <= bottom)
                        {
                            inBand = true;
                            break;
                        }
                    }
                    foreach (double off in new[] { 0.0, -halfSpread, halfSpread })
                    {
                        int px = Math.Clamp((int)(cx * w + off), 0, w - 1);
                        var (r, g, b) = frame.ViewPixelAt(px, py);
                        PixelColor c = ColorClassifier.Classify(r, g, b);
                        if (c == PixelColor.None && inBand)
                        {
                            c = ColorClassifier.ClassifyLoose(r, g, b);
                        }
                        if (c != PixelColor.None)
                        {
                            counts[(int)c]++;
                        }
                    }
                }
                PixelColor found = PixelColor.None;
                for (int k = 1; k < counts.Length; k++)
                {
                    if (counts[k] >= 2)
                    {
                        found = (PixelColor)k;
                        break;
                    }
                }
                if (found != PixelColor.None)
                {
                    results.Add(new NoteDetection(
                        lane, ToKind(found), null, cx, lineY, frame.TimeSeconds,
                        IsLinePresence: true));
                }
            }
        }
    }

    private void DetectLane(
        CapturedFrame frame, int lane, int pyTop, int pyJudge,
        List<(int TopPy, int BottomPy)> bands, List<NoteDetection> results)
    {
        LaneGeometry geo = _profile.Geometry;
        int w = frame.ViewWidth, h = frame.ViewHeight;

        bool InBand(int py)
        {
            int margin = V(4);
            foreach ((int top, int bottom) in bands)
            {
                if (py >= top - margin && py <= bottom + margin)
                {
                    return true;
                }
            }
            return false;
        }

        PixelColor runColor = PixelColor.None;
        int runStart = 0, runLast = 0, gap = 0, coloredPx = 0;
        bool bridged = false;

        void Flush()
        {
            // 塊の成立条件: 色のついた標本が最低 3 つあり、かつ縦の広がりが
            // MinBlobPx 以上（または色標本だけで MinBlobPx 分ある）こと。
            // 白帯の橋渡しだけでできた塊（coloredPx=0）は弾く。標本数だけで
            // 判定すると薄いノーツ（広がり 8〜11px = 標本 4〜6）を落とすため、
            // 広がりとの併用にする（v0.3.7、TRANCE 譜面の実測から）。
            // 静かな上部帯（下端 y<0.72）では最小サイズを緩める（v0.3.41）。
            // 実測ではトラックの初検出 Y の中央値が 0.79 で、大半のノーツが
            // 判定文字・レーン発光の飛び交う帯域の内側で生まれていた。
            // 出現直後のノーツは高さ 4〜6px しかなく、通常の MinBlobPx=7
            // では構造的に弾かれる。上部帯には判定文字もレーン発光も無い
            // ため、小さい塊を拾ってもゴミは増えにくい（残りは追跡側の
            // 出現・移動・残差ゲートが捌く）。
            bool upperZone = runLast / (double)h < 0.72;
            int minColored = upperZone ? 2 : 3;
            int minBlobPx = upperZone ? V(4) : V(MinBlobPx);
            if (runColor == PixelColor.None || coloredPx < minColored
                || (runLast - runStart < minBlobPx && coloredPx < minBlobPx))
            {
                return;
            }
            // ヒット時にレーン全体が光る演出は、縦に長い run として現れる。
            // ノーツは薄いバー（実測で判定ライン付近でも 16px 程度）で、
            // 接近した同色2連が1つの run に融合しても高さ 55px 程度に収まる。
            // 発光は 100px 以上になるため、上限は画面高の 8.5% に置く。
            // 矢印の帯を橋渡しした塊は帯の高さぶん長くなるため上限を緩める。
            int maxBlobPx = bridged
                ? Math.Max(V(40), (int)(h * 0.12))
                : Math.Max(V(40), (int)(h * 0.085));
            if (runLast - runStart > maxBlobPx)
            {
                return;
            }
            int centerPy = (runStart + runLast) / 2;
            double centerY = centerPy / (double)h;
            double centerX = geo.LaneCenterX(lane, _profile.LaneCount, centerY);
            int centerPx = Clamp((int)(centerX * w), 0, w - 1);
            int widthPx = MeasureWidth(frame, centerPx, centerPy, runColor);
            double laneWidthPx = geo.LaneWidthAt(_profile.LaneCount, centerY) * w;
            if (widthPx > laneWidthPx * WideFactor)
            {
                return; // 横長ノーツの一部。矢印パスに任せる。
            }
            // 追跡に使う Y は塊の中心ではなく下端（先端）にする。厚みのある
            // バーの画面中心は射影の運動法則から系統的にずれるため、高速譜面
            // では到達予測の残差が閾値を超えて予約が全滅した（v0.3.7 実測:
            // 遅い序盤・終盤だけ予約され、矢印＝下線という点特徴は全編通った）。
            // 先端は点特徴として法則に従う。
            double bottomY = runLast / (double)h;
            // 塊の行範囲が矢印の帯と重なるか（v0.3.81: line1 の波救済が
            // 「波に覆われていた塊か」を判定するために使う）。
            bool blobInBand = false;
            foreach ((int top, int bottom) in bands)
            {
                if (runLast >= top - V(4) && runStart <= bottom + V(4))
                {
                    blobInBand = true;
                    break;
                }
            }
            results.Add(new NoteDetection(
                lane, ToKind(runColor), null, centerX, bottomY, frame.TimeSeconds,
                InBand: blobInBand));
        }

        // 1 行の色を 3 列（中心と左右 ±30% レーン幅）から決める（v0.3.36）。
        // 従来は中心線 1 列だけで、判定テキスト（BASED! 等は中央に重なる）が
        // 同レーンの後続ノーツを数フレーム連続で覆うと、その間ノーツが
        // 丸ごと見えなかった。横にずれた列ならテキストの外に出るため、
        // どれか 1 列でも色が取れれば行はその色とする。現在の run と同じ
        // 色を優先し、run の連続性を保つ。
        PixelColor ClassifyRow(int py)
        {
            double y = py / (double)h;
            double cx = geo.LaneCenterX(lane, _profile.LaneCount, y);
            double halfSpread = geo.LaneWidthAt(_profile.LaneCount, y) * w * 0.30;
            bool inBand = bands.Count > 0 && InBand(py);
            PixelColor found = PixelColor.None;
            foreach (double off in new[] { 0.0, -halfSpread, halfSpread })
            {
                int px = Clamp((int)(cx * w + off), 0, w - 1);
                var (r, g, b) = frame.ViewPixelAt(px, py);
                PixelColor c = ColorClassifier.Classify(r, g, b);
                if (c == PixelColor.None && inBand)
                {
                    // 白帯の上に薄まったノーツ色は、色相だけの緩い分類で拾う。
                    c = ColorClassifier.ClassifyLoose(r, g, b);
                }
                if (c == PixelColor.None)
                {
                    continue;
                }
                if (c == runColor)
                {
                    return c;
                }
                if (found == PixelColor.None)
                {
                    found = c;
                }
            }
            return found;
        }

        for (int py = pyTop; py <= pyJudge; py += 2)
        {
            PixelColor color = ClassifyRow(py);
            bool inBand = bands.Count > 0 && InBand(py);

            if (color != PixelColor.None && color == runColor)
            {
                runLast = py;
                gap = 0;
                coloredPx++;
            }
            else if (color != PixelColor.None)
            {
                Flush();
                runColor = color;
                runStart = py;
                runLast = py;
                gap = 0;
                coloredPx = 1;
                bridged = false;
            }
            else if (runColor != PixelColor.None && inBand)
            {
                // 帯の白で run が切れないよう橋渡しする（帯の内側だけ）。
                runLast = py;
                gap = 0;
                bridged = true;
            }
            else
            {
                gap += 2;
                if (gap > V(2))
                {
                    Flush();
                    runColor = PixelColor.None;
                    gap = 0;
                    coloredPx = 0;
                    bridged = false;
                }
            }
        }
        Flush();
    }

    private static int MeasureWidth(CapturedFrame frame, int centerPx, int py, PixelColor color)
    {
        int w = frame.ViewWidth;
        int left = centerPx;
        while (left > 0)
        {
            var (r, g, b) = frame.ViewPixelAt(left - 1, py);
            if (ColorClassifier.Classify(r, g, b) != color)
            {
                break;
            }
            left--;
        }
        int right = centerPx;
        while (right < w - 1)
        {
            var (r, g, b) = frame.ViewPixelAt(right + 1, py);
            if (ColorClassifier.Classify(r, g, b) != color)
            {
                break;
            }
            right++;
        }
        return right - left + 1;
    }

    /// <summary>矢印パスの候補行。方向推定に使う統計（印ピクセルの正規化重心 X とレーン中央部の印比率）と白印数を持つ。</summary>
    private readonly record struct ArrowRow(int Py, int X0, int X1, double MeanRx, double CenterMass, int WhiteCount);

    private void DetectArrows(
        CapturedFrame frame, int pyTop, int pyJudge,
        List<NoteDetection> results, List<(int TopPy, int BottomPy)> bands)
    {
        LaneGeometry geo = _profile.Geometry;
        int w = frame.ViewWidth, h = frame.ViewHeight;

        var candidateRows = new List<ArrowRow>();
        int scanFrom = pyTop + (int)((pyJudge - pyTop) * 0.08);
        for (int py = scanFrom; py <= pyJudge; py += 2)
        {
            double y = py / (double)h;
            double half = geo.HalfWidthAt(y);
            double leftPx = (geo.CenterX - half) * w;
            double widthPx = 2 * half * w;
            int xFrom = Clamp((int)leftPx, 0, w - 1);
            int xTo = Clamp((int)(leftPx + widthPx), 0, w - 1);

            int bestStart = -1, bestEnd = -1, curStart = -1, curGap = 0;
            double rxSum = 0, rxSumWhite = 0;
            int markCount = 0, centerCount = 0, whiteCount = 0, centerWhite = 0;
            for (int px = xFrom; px <= xTo; px += 2)
            {
                if (IsMark(frame, px, py))
                {
                    double rx = (px - leftPx) / widthPx;
                    rxSum += rx;
                    markCount++;
                    double laneFrac = (rx * _profile.LaneCount) % 1.0;
                    bool isCenter = Math.Abs(laneFrac - 0.5) < 0.25;
                    if (isCenter)
                    {
                        centerCount++;
                    }
                    if (IsWhiteMark(frame, px, py))
                    {
                        rxSumWhite += rx;
                        whiteCount++;
                        if (isCenter)
                        {
                            centerWhite++;
                        }
                    }
                    if (curStart < 0)
                    {
                        curStart = px;
                    }
                    curGap = 0;
                    if (px - curStart > bestEnd - bestStart)
                    {
                        bestStart = curStart;
                        bestEnd = px;
                    }
                }
                else
                {
                    // 白シェブロンは字形ごとに暗い隙間が空くため、
                    // 隙間 30px（基準解像度）までは同じ連なりとして扱う（v0.3.1）。
                    curGap += 2;
                    if (curGap > V(30))
                    {
                        curStart = -1;
                        curGap = 0;
                    }
                }
            }

            double laneWidthPx = geo.LaneWidthAt(_profile.LaneCount, y) * w;
            // 白印の下限（v0.3.65）: 隣接レーンの同色ノーツ対は、隙間 30px の
            // 橋渡しで 1.5 レーン幅を超える run になり、波でないのに帯化する
            // （マスター実機目視）。本物の波は白シェブロンを必ず含むため、
            // 白印がほぼ無い行は候補行にしない。
            // 白印数の下限は解像度で読み替えない（v0.3.70）: 明部の芯の
            // 標本数は解像度に比例して増えず、線形スケールでは実効的に
            // 厳しくなって本物の波を落とした（v0.3.69 FS 実測）。
            // 距離量のスケールとは別扱いにする。
            if (bestStart >= 0 && bestEnd - bestStart > laneWidthPx * 1.5 && markCount > 0
                && whiteCount >= 3 && whiteCount * 10 >= markCount)
            {
                // 方向推定の統計は白印だけで取る（白印が無い行は全印にフォールバック）。
                double meanRx = whiteCount > 0 ? rxSumWhite / whiteCount : rxSum / markCount;
                double centerMass = whiteCount > 0
                    ? centerWhite / (double)whiteCount
                    : centerCount / (double)markCount;
                candidateRows.Add(new ArrowRow(py, bestStart, bestEnd, meanRx, centerMass, whiteCount));
            }
        }

        // 候補行を塊にまとめる。模様とその下の横線は最大 20px 離れるため、
        // 間隔 20 までは同じノーツとして扱う。
        int i = 0;
        while (i < candidateRows.Count)
        {
            int j = i;
            int widest = i;
            while (j + 1 < candidateRows.Count && candidateRows[j + 1].Py - candidateRows[j].Py <= V(20))
            {
                j++;
                if (candidateRows[j].X1 - candidateRows[j].X0 > candidateRows[widest].X1 - candidateRows[widest].X0)
                {
                    widest = j;
                }
            }

            // 方向ノーツの基準位置は波模様そのものではなく、その下にある横線
            // （マスターの実機確認）。塊の最下行を検出 Y として追跡する。
            double centerY = candidateRows[j].Py / (double)h;
            var wide = candidateRows[widest];
            double centerX = ((wide.X0 + wide.X1) / 2.0) / w;

            // 方向推定は模様部分だけで行う（下の横線は最大の行間ギャップで切り離す）。
            int patternEnd = j;
            if (j - i + 1 >= 4)
            {
                int bestGap = 0, bestSplit = -1;
                for (int k = i; k < j; k++)
                {
                    int gap = candidateRows[k + 1].Py - candidateRows[k].Py;
                    if (gap > bestGap)
                    {
                        bestGap = gap;
                        bestSplit = k;
                    }
                }
                if (bestGap >= V(12) && bestSplit - i + 1 >= 2)
                {
                    patternEnd = bestSplit;
                }
            }

            // 帯全体でも白印が乏しい塊は波ではない（隣接同色対の誤帯）。
            // 検出も帯も出さない: 誤帯は帯内緩和で通常検出まで汚すため。
            int whiteSum = 0;
            for (int k = i; k <= j; k++)
            {
                whiteSum += candidateRows[k].WhiteCount;
            }
            if (whiteSum < 12)
            {
                i = j + 1;
                continue;
            }

            ArrowDirection? direction = EstimateDirection(candidateRows, i, patternEnd);
            int lane = NearestLane(geo, centerX, centerY);
            results.Add(new NoteDetection(lane, NoteKind.Arrow, direction, centerX, centerY, frame.TimeSeconds));
            bands.Add((candidateRows[i].Py, candidateRows[j].Py));
            i = j + 1;
        }
    }

    /// <summary>
    /// シェブロンの方向推定。字形はレーン端まで届くため外形（幅の突出）では
    /// 読めず、印ピクセルの分布で読む（実スクリーンショット4件で検証済み）:
    /// - 左右: 正規化した印重心 X の軌跡が、帯の中央で山になれば右、谷になれば左。
    /// - 上下: レーン中央部の印比率が上部で高ければ上、下部で高ければ下
    ///   （∧ は頂点がレーン中央にあり脚が端に開くため）。
    /// </summary>
    private static ArrowDirection? EstimateDirection(List<ArrowRow> rows, int from, int to)
    {
        int n = to - from + 1;
        if (n < 5)
        {
            return null;
        }
        int third = Math.Max(1, n / 3);

        double MeanRx(int s, int e)
        {
            double a = 0;
            for (int k = s; k <= e; k++)
            {
                a += rows[k].MeanRx;
            }
            return a / (e - s + 1);
        }

        double CenterMass(int s, int e)
        {
            double a = 0;
            for (int k = s; k <= e; k++)
            {
                a += rows[k].CenterMass;
            }
            return a / (e - s + 1);
        }

        double meanTop = MeanRx(from, from + third - 1);
        int midStart = from + n / 2 - third / 2;
        double meanMid = MeanRx(midStart, midStart + third - 1);
        double meanBot = MeanRx(to - third + 1, to);
        double cmTop = CenterMass(from, from + third - 1);
        double cmBot = CenterMass(to - third + 1, to);

        double dx1 = meanMid - meanTop, dx2 = meanBot - meanMid;
        const double th = 0.008;
        if (dx1 > th && dx2 < -th)
        {
            return ArrowDirection.Right;
        }
        if (dx1 < -th && dx2 > th)
        {
            return ArrowDirection.Left;
        }
        double cmTrend = cmTop - cmBot;
        if (cmTrend > 0.06)
        {
            return ArrowDirection.Up;
        }
        if (cmTrend < -0.06)
        {
            return ArrowDirection.Down;
        }
        return null;
    }

    private int NearestLane(LaneGeometry geo, double x, double y)
    {
        int best = 0;
        double bestDist = double.MaxValue;
        for (int lane = 0; lane < _profile.LaneCount; lane++)
        {
            double d = Math.Abs(geo.LaneCenterX(lane, _profile.LaneCount, y) - x);
            if (d < bestDist)
            {
                bestDist = d;
                best = lane;
            }
        }
        return best;
    }

    private static bool IsMark(CapturedFrame frame, int px, int py)
    {
        var (r, g, b) = frame.ViewPixelAt(px, py);
        if (ColorClassifier.Classify(r, g, b) != PixelColor.None)
        {
            return true;
        }
        return IsWhiteMark(frame, px, py);
    }

    /// <summary>
    /// 白〜淡色の印か。HYPNOSYNC の白シェブロンは実測で純白 (255,255,255)〜
    /// 灰色 (v≈0.49, s=0)。レーン面 (v≈0.16) と区別できる v&gt;0.45 の低彩度。
    /// 方向推定の統計はこちらの印だけで取る: 通常ノーツの鮮やかな色まで
    /// 含めると、矢印と同時に来た通常ノーツが重心を歪めるため（v0.3.6）。
    /// </summary>
    private static bool IsWhiteMark(CapturedFrame frame, int px, int py)
    {
        var (r, g, b) = frame.ViewPixelAt(px, py);
        var (_, s, v) = ColorClassifier.RgbToHsv(r, g, b);
        return v > 0.45 && s < 0.35;
    }

    private static NoteKind ToKind(PixelColor color) => color switch
    {
        PixelColor.Red => NoteKind.Red,
        PixelColor.Blue => NoteKind.Blue,
        PixelColor.Purple => NoteKind.Purple,
        PixelColor.Yellow => NoteKind.Yellow,
        _ => NoteKind.Red,
    };

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
