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

    public List<NoteDetection> Detect(CapturedFrame frame)
    {
        var results = new List<NoteDetection>();
        LaneGeometry geo = _profile.Geometry;
        int w = frame.Width, h = frame.Height;
        int pyTop = (int)(geo.TopY * h);
        int pyJudge = (int)(_profile.JudgmentLineY * h);

        for (int lane = 0; lane < _profile.LaneCount; lane++)
        {
            DetectLane(frame, lane, pyTop, pyJudge, results);
        }
        DetectArrows(frame, pyTop, pyJudge, results);
        return results;
    }

    private void DetectLane(CapturedFrame frame, int lane, int pyTop, int pyJudge, List<NoteDetection> results)
    {
        LaneGeometry geo = _profile.Geometry;
        int w = frame.Width, h = frame.Height;

        PixelColor runColor = PixelColor.None;
        int runStart = 0, runLast = 0, gap = 0;

        void Flush()
        {
            if (runColor == PixelColor.None || runLast - runStart < MinBlobPx)
            {
                return;
            }
            // ヒット時にレーン全体が光る演出は、縦に長い run として現れる。
            // ノーツは薄いバー（実測で判定ライン付近でも 16px 程度）なので、
            // 画面高の 5% を超える run はノーツではないとして捨てる。
            int maxBlobPx = Math.Max(28, (int)(frame.Height * 0.05));
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
            results.Add(new NoteDetection(
                lane, ToKind(runColor), null, centerX, centerY, frame.TimeSeconds));
        }

        for (int py = pyTop; py <= pyJudge; py += 2)
        {
            double y = py / (double)h;
            double cx = geo.LaneCenterX(lane, _profile.LaneCount, y);
            int px = Clamp((int)(cx * w), 0, w - 1);
            var (r, g, b) = frame.PixelAt(px, py);
            PixelColor color = ColorClassifier.Classify(r, g, b);

            if (color != PixelColor.None && color == runColor)
            {
                runLast = py;
                gap = 0;
            }
            else if (color != PixelColor.None)
            {
                Flush();
                runColor = color;
                runStart = py;
                runLast = py;
                gap = 0;
            }
            else
            {
                gap += 2;
                if (gap > 2)
                {
                    Flush();
                    runColor = PixelColor.None;
                    gap = 0;
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

    private void DetectArrows(CapturedFrame frame, int pyTop, int pyJudge, List<NoteDetection> results)
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
            double rxSum = 0;
            int markCount = 0, centerCount = 0;
            for (int px = xFrom; px <= xTo; px += 2)
            {
                if (IsMark(frame, px, py))
                {
                    double rx = (px - leftPx) / widthPx;
                    rxSum += rx;
                    markCount++;
                    double laneFrac = (rx * _profile.LaneCount) % 1.0;
                    if (Math.Abs(laneFrac - 0.5) < 0.25)
                    {
                        centerCount++;
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
                candidateRows.Add(new ArrowRow(
                    py, bestStart, bestEnd, rxSum / markCount, centerCount / (double)markCount));
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
        // 矢印は白〜淡色で描かれる。HYPNOSYNC の白シェブロンは実測で純白
        // (255,255,255)〜灰色 (v≈0.49, s=0)。レーン面 (v≈0.16) と区別できる
        // v>0.45 の低彩度を印として拾う。
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
