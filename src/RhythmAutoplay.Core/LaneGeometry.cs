namespace RhythmAutoplay.Core;

/// <summary>
/// 透視投影レーンの形状。レーン中心線は画面上では直線なので、
/// レーン全体の半幅は Y に対して線形に補間できる。
/// 座標はウィンドウ（クライアント領域）内の正規化座標 (0..1)。
/// </summary>
public sealed record LaneGeometry(
    double CenterX,
    double TopY,
    double TopHalfWidth,
    double BottomY,
    double BottomHalfWidth,
    double VanishingY = 0.10)
{
    /// <summary>汎用の初期値。ゲームごとの実測で上書きする前提の仮置き。</summary>
    public static LaneGeometry Default => new(
        CenterX: 0.5, TopY: 0.16, TopHalfWidth: 0.06, BottomY: 0.94, BottomHalfWidth: 0.21, VanishingY: 0.10);

    /// <summary>HYPNOSYNC の初期推定（ストアスクリーンショットからの目視見積もり。実機較正で確定させる）。</summary>
    public static LaneGeometry HypnosyncEstimate => new(
        CenterX: 0.5, TopY: 0.16, TopHalfWidth: 0.06, BottomY: 0.94, BottomHalfWidth: 0.21, VanishingY: 0.12);

    public double HalfWidthAt(double y)
    {
        double span = BottomY - TopY;
        double t = span <= 0 ? 0 : (y - TopY) / span;
        return TopHalfWidth + (BottomHalfWidth - TopHalfWidth) * t;
    }

    public double LaneWidthAt(int laneCount, double y) => 2 * HalfWidthAt(y) / laneCount;

    public double LaneCenterX(int lane, int laneCount, double y)
    {
        double half = HalfWidthAt(y);
        double laneWidth = 2 * half / laneCount;
        return CenterX - half + laneWidth * (lane + 0.5);
    }
}
