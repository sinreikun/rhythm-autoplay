namespace RhythmAutoplay.Core;

/// <summary>ピクセル色の分類結果。ノーツ種別への対応は検出器側で決める。</summary>
public enum PixelColor
{
    None,
    Red,
    Blue,
    Purple,
    Yellow,
}

/// <summary>
/// RGB → HSV 変換と、ノーツ色の分類。
/// 閾値は HYPNOSYNC のノーツ色を想定した初期値で、実機の dry-run ログで較正する。
/// </summary>
public static class ColorClassifier
{
    public static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        double max = Math.Max(rn, Math.Max(gn, bn));
        double min = Math.Min(rn, Math.Min(gn, bn));
        double delta = max - min;

        double h;
        if (delta < 1e-9)
        {
            h = 0;
        }
        else if (max == rn)
        {
            h = 60 * (((gn - bn) / delta) % 6);
        }
        else if (max == gn)
        {
            h = 60 * ((bn - rn) / delta + 2);
        }
        else
        {
            h = 60 * ((rn - gn) / delta + 4);
        }
        if (h < 0)
        {
            h += 360;
        }

        double s = max <= 0 ? 0 : delta / max;
        return (h / 360.0, s, max);
    }

    public static PixelColor Classify(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        // 暗い色・無彩色は背景やレーン面として捨てる。
        if (v < 0.25 || s < 0.35)
        {
            return PixelColor.None;
        }
        if (h < 0.045 || h >= 0.925)
        {
            return PixelColor.Red; // 赤〜ピンク（HYPNOSYNC の下段色を含む）
        }
        if (h >= 0.10 && h < 0.20)
        {
            return PixelColor.Yellow;
        }
        if (h >= 0.55 && h < 0.70)
        {
            return PixelColor.Blue;
        }
        if (h >= 0.70 && h < 0.925)
        {
            return PixelColor.Purple; // 紫〜マゼンタ
        }
        return PixelColor.None;
    }
}
