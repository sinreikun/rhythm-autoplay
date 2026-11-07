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
        // 暗い色・無彩色は背景やレーン面として捨てる。床は実測で決める:
        // HYPNOSYNC のコンボ数字は半透明の暗い字形で、実測 RGB(65,43,54)
        // （v≈0.25, s≈0.35）が旧床の境界上で点滅し赤と誤認された（v0.3.1 実機）。
        // 本物のノーツは s≥0.8, v≥0.66 の鮮やかな色なので、床を v=0.30 / s=0.45
        // まで上げても取りこぼさない。
        if (v < 0.30 || s < 0.45)
        {
            return PixelColor.None;
        }
        return FromHue(h);
    }

    /// <summary>
    /// 緩い分類: 矢印ノーツの白帯の上に半透明で重なった通常ノーツを読むため。
    /// 白に薄まると彩度が落ちて通常の床（s≥0.45）を割るため、明るく
    /// （v&gt;0.55）色味が残る（s&gt;0.15）ピクセルは色相だけで判定する。
    /// 帯の内側という文脈でのみ使う前提で、単独では使わない。
    /// </summary>
    public static PixelColor ClassifyLoose(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        if (v < 0.55 || s < 0.15)
        {
            return PixelColor.None;
        }
        return FromHue(h);
    }

    /// <summary>
    /// 単ライン方式（v0.3.73）の診断用分類: 上部帯のノーツは描画が薄く
    /// （実測で彩度・明度が下部の半分以下、v≈0.2 / s≈0.4）、通常の床
    /// （v≥0.30 / s≥0.45）を割る。床を下げた読みを「影」として PL1
    /// ログに記録し、レーン面（v≈0.16）と分離できることが実ログで
    /// 確認できてから発火側へ昇格させる。当面は記録専用で、
    /// 発火判定には使わない。
    /// </summary>
    public static PixelColor ClassifyDim(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        if (v < 0.18 || s < 0.28)
        {
            return PixelColor.None;
        }
        return FromHue(h);
    }

    private static PixelColor FromHue(double h)
    {
        if (h < 0.045 || h >= 0.90)
        {
            return PixelColor.Red; // 赤〜ピンク（HYPNOSYNC の下段色を含む）
        }
        if (h >= 0.10 && h < 0.20)
        {
            return PixelColor.Yellow;
        }
        if (h >= 0.50 && h < 0.70)
        {
            // HYPNOSYNC の青ノーツはシアン寄り（実測 RGB 43,207,254 → 色相 0.537）。
            // 0.55 始まりでは取りこぼすため 0.50 から拾う（v0.3.0 実機で確認）。
            return PixelColor.Blue;
        }
        if (h >= 0.70 && h < 0.90)
        {
            return PixelColor.Purple; // 紫（青紫〜マゼンタ手前）
        }
        return PixelColor.None;
    }
}
