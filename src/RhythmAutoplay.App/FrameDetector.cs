using RhythmAutoplay.Core;

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

    private void DetectArrows(CapturedFrame frame, int pyTop, int pyJudge, List<NoteDetection> results)
    {
        LaneGeometry geo = _profile.Geometry;
        int w = frame.Width, h = frame.Height;

        var candidateRows = new List<(int Py, int X0, int X1)>();
        int scanFrom = pyTop + (int)((pyJudge - pyTop) * 0.3);
        for (int py = scanFrom; py <= pyJudge; py += 4)
        {
            double y = py / (double)h;
            double half = geo.HalfWidthAt(y);
            int xFrom = Clamp((int)((geo.CenterX - half) * w), 0, w - 1);
            int xTo = Clamp((int)((geo.CenterX + half) * w), 0, w - 1);

            int bestStart = -1, bestEnd = -1, curStart = -1, curGap = 0;
            for (int px = xFrom; px <= xTo; px += 2)
            {
                if (IsMark(frame, px, py))
                {
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
                    curGap += 2;
                    if (curGap > 4)
                    {
                        curStart = -1;
                        curGap = 0;
                    }
                }
            }

            double laneWidthPx = geo.LaneWidthAt(_profile.LaneCount, y) * w;
            if (bestStart >= 0 && bestEnd - bestStart > laneWidthPx * 1.9)
            {
                candidateRows.Add((py, bestStart, bestEnd));
            }
        }

        // 候補行を塊にまとめる。
        int i = 0;
        while (i < candidateRows.Count)
        {
            int j = i;
            int widest = i;
            while (j + 1 < candidateRows.Count && candidateRows[j + 1].Py - candidateRows[j].Py <= 8)
            {
                j++;
                if (candidateRows[j].X1 - candidateRows[j].X0 > candidateRows[widest].X1 - candidateRows[widest].X0)
                {
                    widest = j;
                }
            }

            int centerPy = (candidateRows[i].Py + candidateRows[j].Py) / 2;
            double centerY = centerPy / (double)h;
            var wide = candidateRows[widest];
            double centerX = ((wide.X0 + wide.X1) / 2.0) / w;

            ArrowDirection? direction = EstimateDirection(frame, candidateRows, i, j, widest);
            int lane = NearestLane(geo, centerX, centerY);
            results.Add(new NoteDetection(lane, NoteKind.Arrow, direction, centerX, centerY, frame.TimeSeconds));
            i = j + 1;
        }
    }

    private ArrowDirection? EstimateDirection(
        CapturedFrame frame, List<(int Py, int X0, int X1)> rows, int from, int to, int widestIndex)
    {
        var mid = rows[widestIndex];
        int heightPx = rows[to].Py - rows[from].Py;
        int delta = Math.Max(4, heightPx / 4);

        (int X0, int X1)? ExtentAt(int targetPy)
        {
            (int, int)? best = null;
            int bestDist = int.MaxValue;
            for (int k = from; k <= to; k++)
            {
                int d = Math.Abs(rows[k].Py - targetPy);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = (rows[k].X0, rows[k].X1);
                }
            }
            return best;
        }

        var above = ExtentAt(mid.Py - delta);
        var below = ExtentAt(mid.Py + delta);
        if (above is null || below is null)
        {
            return null;
        }
        int protrudeRight = mid.X1 - Math.Max(above.Value.X1, below.Value.X1);
        int protrudeLeft = Math.Min(above.Value.X0, below.Value.X0) - mid.X0;
        if (protrudeRight >= 3 && protrudeRight > protrudeLeft)
        {
            return ArrowDirection.Right;
        }
        if (protrudeLeft >= 3 && protrudeLeft > protrudeRight)
        {
            return ArrowDirection.Left;
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
        // 矢印は白〜淡色で描かれることがある。明るい低彩度も印として拾う。
        var (_, s, v) = ColorClassifier.RgbToHsv(r, g, b);
        return v > 0.8 && s < 0.35;
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
