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
}
