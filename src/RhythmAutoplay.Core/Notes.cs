namespace RhythmAutoplay.Core;

/// <summary>ノーツの種類。HYPNOSYNC では色がそのまま種類を表す。</summary>
public enum NoteKind
{
    /// <summary>赤ノーツ: そのレーンの赤キーを押す。</summary>
    Red,
    /// <summary>青ノーツ: そのレーンの青キーを押す。</summary>
    Blue,
    /// <summary>紫ノーツ: 赤+青の同時押し。</summary>
    Purple,
    /// <summary>黄ノーツ: 赤・青どちらでも可。</summary>
    Yellow,
    /// <summary>横長ノーツ: 矢印方向の入力（レバー/方向キー）。</summary>
    Arrow,

    // 色指定プロファイル（レーンの Bindings）専用の種別。番号は既存の
    // ログ互換のため末尾に足す（Binding0 + n = そのレーンの n 番目の
    // 色割り当て）。色ファミリー方式の検出器では生成されない。
    /// <summary>色割り当て 0 番に一致したノーツ。</summary>
    Binding0,
    /// <summary>色割り当て 1 番に一致したノーツ。</summary>
    Binding1,
    /// <summary>色割り当て 2 番に一致したノーツ。</summary>
    Binding2,
    /// <summary>色割り当て 3 番に一致したノーツ。</summary>
    Binding3,
}

/// <summary>矢印ノーツの方向。</summary>
public enum ArrowDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>検出されたノーツ1件。位置は ROI 内の正規化座標 (0..1)。</summary>
public sealed record NoteDetection(
    int Lane,
    NoteKind Kind,
    ArrowDirection? Direction,
    double X,
    double Y,
    double TimeSeconds,
    bool IsLinePresence = false,
    int? PresenceRow = null);

/// <summary>スケジューラへ渡す入力イベント（キー down/up の列）。</summary>
/// <param name="Tag">
/// 予約の事後修正（v0.3.37）で同一予約のイベントを束ねる識別子。
/// 0 は無タグ（修正対象外）。発火済みでないイベントだけが時刻をずらせる。
/// </param>
public sealed record ScheduledInput(
    double TimeSeconds,
    string Key,
    bool IsDown,
    long Tag = 0);
