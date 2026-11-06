namespace RhythmAutoplay.Core;

/// <summary>
/// ノーツの位置サンプル列から判定ライン到達時刻 t_hit を推定する。
/// 反応型ではなく予測型にすることが本プログラムの中核（docs/design.md §0, §5）。
/// </summary>
public static class HitTimePredictor
{
    /// <summary>予測に最低限必要なサンプル数。</summary>
    public const int MinSamples = 3;

    /// <summary>
    /// 位置 y が targetY に到達する時刻を、直近サンプルの最小二乗直線で推定する。
    /// </summary>
    /// <param name="samples">(時刻[秒], 位置) の列。時刻は単調増加であること。</param>
    /// <param name="targetY">判定ラインの位置（samples と同じ座標系）。</param>
    /// <param name="hitTimeSeconds">推定到達時刻[秒]。</param>
    /// <param name="residualRms">直線フィットの残差 RMS（位置の単位）。小さいほど信頼できる。</param>
    /// <returns>推定できれば true。サンプル不足・速度ゼロ・後方への移動なら false。</returns>
    public static bool TryPredictHitTime(
        IReadOnlyList<(double Time, double Y)> samples,
        double targetY,
        out double hitTimeSeconds,
        out double residualRms)
    {
        hitTimeSeconds = 0;
        residualRms = double.PositiveInfinity;
        if (samples.Count < MinSamples)
        {
            return false;
        }

        int n = samples.Count;
        double sumT = 0, sumY = 0;
        for (int i = 0; i < n; i++)
        {
            sumT += samples[i].Time;
            sumY += samples[i].Y;
        }
        double meanT = sumT / n;
        double meanY = sumY / n;

        double sTT = 0, sTY = 0;
        for (int i = 0; i < n; i++)
        {
            double dt = samples[i].Time - meanT;
            sTT += dt * dt;
            sTY += dt * (samples[i].Y - meanY);
        }
        if (sTT <= 0)
        {
            return false;
        }

        double velocity = sTY / sTT; // units per second
        if (Math.Abs(velocity) < 1e-9)
        {
            return false;
        }

        double intercept = meanY - velocity * meanT;
        double tHit = (targetY - intercept) / velocity;
        // 到達時刻が最後のサンプルより前なら、すでに通過している（= 予測として無効）。
        if (tHit < samples[n - 1].Time)
        {
            return false;
        }

        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double fitted = intercept + velocity * samples[i].Time;
            double d = samples[i].Y - fitted;
            sumSq += d * d;
        }

        hitTimeSeconds = tHit;
        residualRms = Math.Sqrt(sumSq / n);
        return true;
    }

    /// <summary>
    /// 透視投影用の予測。画面上ではノーツは手前ほど加速して見えるため、
    /// 直線フィットは到達時刻を遅く見積もりすぎる（v0.2.0 の実機で確認した主因）。
    /// 奥行きに反比例する座標 w = 1 / (y - vanishingY) へ変換すると、
    /// 等速で近づくノーツは w が時間に対して直線になる。そこで直線フィットし、
    /// 残差は元の y 座標系に戻して返す（呼び出し側の閾値を共通化するため）。
    /// </summary>
    public static bool TryPredictHitTimeProjective(
        IReadOnlyList<(double Time, double Y)> samples,
        double targetY,
        double vanishingY,
        out double hitTimeSeconds,
        out double residualRms)
    {
        hitTimeSeconds = 0;
        residualRms = double.PositiveInfinity;
        if (samples.Count < MinSamples)
        {
            return false;
        }
        double targetGap = targetY - vanishingY;
        if (targetGap <= 1e-6)
        {
            return false;
        }

        var transformed = new List<(double Time, double Y)>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            double gap = samples[i].Y - vanishingY;
            if (gap <= 1e-6)
            {
                return false;
            }
            transformed.Add((samples[i].Time, 1.0 / gap));
        }

        if (!TryPredictHitTime(transformed, 1.0 / targetGap, out hitTimeSeconds, out _))
        {
            return false;
        }

        // 残差を y 座標系で測り直す: フィットした直線 w(t) を逆変換して比べる。
        int n = transformed.Count;
        double sumT = 0, sumW = 0;
        for (int i = 0; i < n; i++)
        {
            sumT += transformed[i].Time;
            sumW += transformed[i].Y;
        }
        double meanT = sumT / n, meanW = sumW / n;
        double sTT = 0, sTW = 0;
        for (int i = 0; i < n; i++)
        {
            double dt = transformed[i].Time - meanT;
            sTT += dt * dt;
            sTW += dt * (transformed[i].Y - meanW);
        }
        if (sTT <= 0)
        {
            return false;
        }
        double velocity = sTW / sTT;
        double intercept = meanW - velocity * meanT;
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double wFit = intercept + velocity * transformed[i].Time;
            if (Math.Abs(wFit) < 1e-9)
            {
                return false;
            }
            double yFit = vanishingY + 1.0 / wFit;
            double d = samples[i].Y - yFit;
            sumSq += d * d;
        }
        residualRms = Math.Sqrt(sumSq / n);
        return true;
    }
}
