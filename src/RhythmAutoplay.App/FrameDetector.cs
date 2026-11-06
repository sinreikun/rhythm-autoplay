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
public sealed class FrameDetector
{
    private const int MinBlobPx = 7;
    private const double WideFactor = 1.7;

    private readonly GameProfile _profile;

    public FrameDetector(GameProfile profile) => _profile = profile;

    /// <summary>固定ライン横断方式（engine=line）で使うライン在席走査の有効化。</summary>
    public bool LineMode { get; set; }

    public List<NoteDetection> Detect(CapturedFrame frame)
    {
        var results = new List<NoteDetection>();
        LaneGeometry geo = _profile.Geometry;
        int w = frame.Width, h = frame.Height;
        int pyTop = (int)(geo.TopY * h);
        int pyJudge = (int)(_profile.JudgmentLineY * h);

        // 先に矢印パスで帯の Y 範囲を知る。通常ノーツが矢印と同時に来ると
        // 白帯の上に半透明で重なって見落としやすいため（マスター実機確認
        // 2026-11-06）、レーンパスでは帯の内側だけ緩い分類と橋渡しを使う。
        var bands = new List<(int TopPy, int BottomPy)>();
        DetectArrows(frame, pyTop, pyJudge, results, bands);
        for (int lane = 0; lane < _profile.LaneCount; lane++)
        {
            DetectLane(frame, lane, pyTop, pyJudge, bands, results);
        }
        if (LineMode)
        {
            ScanLines(frame, bands, results);
        }
        return results;
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
        int w = frame.Width, h = frame.Height;
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
                for (int dpy = -2; dpy <= 2; dpy += 2)
                {
                    int py = Math.Clamp(pyC + dpy, 0, h - 1);
                    // 矢印バンドの内側では、通常分類で取れない場合に限り
                    // 色相だけの緩い分類で拾う（v0.3.46）。白帯の上に
                    // 半透明で重なったノーツは彩度・明度が落ちて通常分類を
                    // 落ち、横断の鎖が切れていた（Floating の CRINGE 44 の
                    // 主因候補、マスター実機観察 2026-11-07）。レーンパスの
                    // ClassifyRow と同じ規則にそろえる。
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
                        var (r, g, b) = frame.PixelAt(px, py);
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
        int w = frame.Width, h = frame.Height;

        bool InBand(int py)
        {
            foreach ((int top, int bottom) in bands)
            {
                if (py >= top - 4 && py <= bottom + 4)
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
            int minBlobPx = upperZone ? 4 : MinBlobPx;
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
                ? Math.Max(40, (int)(frame.Height * 0.12))
                : Math.Max(40, (int)(frame.Height * 0.085));
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
            results.Add(new NoteDetection(
                lane, ToKind(runColor), null, centerX, bottomY, frame.TimeSeconds));
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
                var (r, g, b) = frame.PixelAt(px, py);
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
                if (gap > 2)
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
        int w = frame.Width;
        int left = centerPx;
        while (left > 0)
        {
            var (r, g, b) = frame.PixelAt(left - 1, py);
            if (ColorClassifier.Classify(r, g, b) != color)
            {
                break;
            }
            left--;
        }
        int right = centerPx;
        while (right < w - 1)
        {
            var (r, g, b) = frame.PixelAt(right + 1, py);
            if (ColorClassifier.Classify(r, g, b) != color)
            {
                break;
            }
            right++;
        }
        return right - left + 1;
    }

    /// <summary>矢印パスの候補行。方向推定に使う統計（印ピクセルの正規化重心 X とレーン中央部の印比率）を持つ。</summary>
    private readonly record struct ArrowRow(int Py, int X0, int X1, double MeanRx, double CenterMass);

    private void DetectArrows(
        CapturedFrame frame, int pyTop, int pyJudge,
        List<NoteDetection> results, List<(int TopPy, int BottomPy)> bands)
    {
        LaneGeometry geo = _profile.Geometry;
        int w = frame.Width, h = frame.Height;

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
                    // 隙間 30px までは同じ連なりとして扱う（v0.3.1）。
                    curGap += 2;
                    if (curGap > 30)
                    {
                        curStart = -1;
                        curGap = 0;
                    }
                }
            }

            double laneWidthPx = geo.LaneWidthAt(_profile.LaneCount, y) * w;
            if (bestStart >= 0 && bestEnd - bestStart > laneWidthPx * 1.5 && markCount > 0)
            {
                // 方向推定の統計は白印だけで取る（白印が無い行は全印にフォールバック）。
                double meanRx = whiteCount > 0 ? rxSumWhite / whiteCount : rxSum / markCount;
                double centerMass = whiteCount > 0
                    ? centerWhite / (double)whiteCount
                    : centerCount / (double)markCount;
                candidateRows.Add(new ArrowRow(py, bestStart, bestEnd, meanRx, centerMass));
            }
        }

        // 候補行を塊にまとめる。模様とその下の横線は最大 20px 離れるため、
        // 間隔 20 までは同じノーツとして扱う。
        int i = 0;
        while (i < candidateRows.Count)
        {
            int j = i;
            int widest = i;
            while (j + 1 < candidateRows.Count && candidateRows[j + 1].Py - candidateRows[j].Py <= 20)
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
                if (bestGap >= 12 && bestSplit - i + 1 >= 2)
                {
                    patternEnd = bestSplit;
                }
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
        var (r, g, b) = frame.PixelAt(px, py);
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
        var (r, g, b) = frame.PixelAt(px, py);
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
