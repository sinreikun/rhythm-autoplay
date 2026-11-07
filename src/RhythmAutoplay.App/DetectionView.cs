using System.Drawing;
using RhythmAutoplay.Core;

namespace RhythmAutoplay.App;

/// <summary>
/// 検出状態の1フレーム分のスナップショット。GUI のライブオーバレイと、
/// play の --watch（逃しやすい状況のトリガ保存）の両方がこれを描く。
/// 「検出器が何を見ているか」をそのまま目で確認するための共通表現。
/// </summary>
public sealed record DetectionView(
    double TimeSeconds,
    int FrameWidth,
    int FrameHeight,
    IReadOnlyList<NoteDetection> Detections,
    IReadOnlyList<(int TopPy, int BottomPy)> Bands,
    IReadOnlyList<(int Lane, NoteKind Kind, double THit)> Upcoming);

/// <summary>DetectionView を Graphics 上に描く共通描画。</summary>
public static class DetectionPainter
{
    public static Color KindColor(NoteKind kind) => kind switch
    {
        NoteKind.Red => Color.FromArgb(255, 90, 130),
        NoteKind.Blue => Color.FromArgb(70, 200, 255),
        NoteKind.Purple => Color.FromArgb(205, 120, 255),
        NoteKind.Yellow => Color.FromArgb(255, 205, 60),
        _ => Color.White,
    };

    /// <summary>
    /// 描画内容: 矢印バンド（半透明の白帯）、固定ライン3本、在席セル
    /// （ライン×レーンの丸、種別色）、塊検出（×印、矢印は方向文字）、
    /// 判定ライン際の予約マーカー（レーン位置に到達までの ms を表示）。
    /// caption があれば上部に帯を出して書く（トリガ保存用）。
    /// </summary>
    public static void Draw(
        Graphics g, DetectionView view, GameProfile profile,
        int widthPx, int heightPx, string? caption, bool overlayMode = false)
    {
        if (view.FrameWidth <= 0 || view.FrameHeight <= 0)
        {
            return;
        }
        double sx = widthPx / (double)view.FrameWidth;
        double sy = heightPx / (double)view.FrameHeight;
        LaneGeometry geo = profile.Geometry;

        // 矢印バンド: ハイウェイ全幅を薄い白で覆う。ただしオーバレイ
        // （TransparencyKey のマゼンタ地）に半透明色を塗るとキー色と
        // 混ざって不透明なマゼンタ面になり、ゲームを覆ってしまう
        // （v0.3.60 実機報告）。オーバレイでは輪郭だけを不透明で描く。
        using (var bandBrush = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
        using (var bandPen = new Pen(Color.White, 1.5f))
        {
            foreach ((int top, int bottom) in view.Bands)
            {
                double yTop = top / (double)view.FrameHeight;
                double yBot = bottom / (double)view.FrameHeight;
                double halfT = geo.HalfWidthAt(yTop);
                double halfB = geo.HalfWidthAt(yBot);
                float x0 = (float)((geo.CenterX - Math.Max(halfT, halfB)) * widthPx);
                float x1 = (float)((geo.CenterX + Math.Max(halfT, halfB)) * widthPx);
                if (overlayMode)
                {
                    g.DrawRectangle(bandPen, x0, (float)(top * sy), x1 - x0, (float)((bottom - top) * sy));
                }
                else
                {
                    g.FillRectangle(bandBrush, x0, (float)(top * sy), x1 - x0, (float)((bottom - top) * sy));
                }
            }
        }

        // 固定ライン3本（在席走査のライン）。
        using (var linePen = new Pen(Color.FromArgb(overlayMode ? 255 : 200, 255, 255, 255), 1.5f))
        {
            foreach (double lineY in LineCrossingTracker.LineYs)
            {
                double half = geo.HalfWidthAt(lineY);
                g.DrawLine(linePen,
                    (float)((geo.CenterX - half) * widthPx), (float)(lineY * heightPx),
                    (float)((geo.CenterX + half) * widthPx), (float)(lineY * heightPx));
            }
        }

        using var font = new Font("Consolas", Math.Max(10f, heightPx * 0.016f), FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        // オーバレイでは影も半透明色になるため塗らない（同上）。
        using var shadowBrush = new SolidBrush(overlayMode ? Color.Transparent : Color.FromArgb(160, 0, 0, 0));

        foreach (NoteDetection d in view.Detections)
        {
            Color c = KindColor(d.Kind);
            using var pen = new Pen(c, 2.5f);
            using var brush = new SolidBrush(c);
            if (d.IsLinePresence)
            {
                // 在席セル: ラインとレーン中心の交点に塗り丸。
                float cx = (float)(geo.LaneCenterX(d.Lane, profile.LaneCount, d.Y) * widthPx);
                float cy = (float)(d.Y * heightPx);
                float r = Math.Max(6f, heightPx * 0.011f);
                g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
                using var ring = new Pen(Color.White, 1.5f);
                g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
            }
            else if (d.Kind == NoteKind.Arrow)
            {
                float cx = (float)(d.X * widthPx);
                float cy = (float)(d.Y * heightPx);
                float r = Math.Max(9f, heightPx * 0.016f);
                g.DrawRectangle(pen, cx - r, cy - r * 0.6f, r * 2, r * 1.2f);
                string dir = d.Direction?.ToString() ?? "?";
                g.DrawString(dir, font, shadowBrush, cx - r + 1, cy - r * 0.6f + 1);
                g.DrawString(dir, font, textBrush, cx - r, cy - r * 0.6f);
            }
            else
            {
                float cx = (float)(d.X * widthPx);
                float cy = (float)(d.Y * heightPx);
                float r = Math.Max(5f, heightPx * 0.009f);
                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
            }
        }

        // 予約マーカー: 判定ライン上のレーン位置に、下向き三角と残り ms。
        double jy = profile.JudgmentLineY;
        foreach ((int lane, NoteKind kind, double tHit) in view.Upcoming)
        {
            double dt = tHit - view.TimeSeconds;
            if (dt < -0.05 || dt > 1.2)
            {
                continue;
            }
            Color c = KindColor(kind);
            using var brush = new SolidBrush(c);
            float cx = (float)(geo.LaneCenterX(lane, profile.LaneCount, jy) * widthPx);
            float cy = (float)(jy * heightPx);
            float r = Math.Max(7f, heightPx * 0.013f);
            var tri = new[]
            {
                new PointF(cx - r, cy - r * 2.2f),
                new PointF(cx + r, cy - r * 2.2f),
                new PointF(cx, cy - r * 0.7f),
            };
            g.FillPolygon(brush, tri);
            string label = $"{dt * 1000:F0}";
            var size = g.MeasureString(label, font);
            g.DrawString(label, font, shadowBrush, cx - size.Width / 2 + 1, cy - r * 2.2f - size.Height + 1);
            g.DrawString(label, font, textBrush, cx - size.Width / 2, cy - r * 2.2f - size.Height);
        }

        if (caption != null)
        {
            using var bar = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
            g.FillRectangle(bar, 0, 0, widthPx, heightPx * 0.045f);
            using var capFont = new Font("Consolas", Math.Max(12f, heightPx * 0.02f), FontStyle.Bold);
            g.DrawString(caption, capFont, textBrush, 8, heightPx * 0.008f);
        }
    }
}
