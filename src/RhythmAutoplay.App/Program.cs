using RhythmAutoplay.Core;

// RhythmAutoplay 本体
// v0.1.0 は土台版: プロファイルと予測コアの自己確認まで。
// キャプチャ→検出→入力の実プレイ経路は v0.2 で HYPNOSYNC 実機検証と一緒に入る（docs/design.md §12, §14）。

GameProfile profile = GameProfile.HypnosyncDefault();

Console.WriteLine("RhythmAutoplay (body)");
Console.WriteLine($"対象プロファイル: {profile.GameId} / 対象プロセス: {profile.WindowProcessName}.exe");
Console.WriteLine($"レーン数: {profile.LaneCount} / 判定ライン Y: {profile.JudgmentLineY}");
Console.WriteLine("キー配置（ゲーム内設定をこれに合わせてください）:");
foreach (LaneBinding lane in profile.Lanes)
{
    Console.WriteLine($"  レーン{lane.Lane + 1}: 赤={lane.RedKey} 青={lane.BlueKey} 紫=赤+青 黄=どちらでも可");
}
foreach (KeyValuePair<ArrowDirection, string> arrow in profile.ArrowKeys)
{
    Console.WriteLine($"  矢印 {arrow.Key}: {arrow.Value}");
}

// 予測コアの自己確認: 等速で判定ラインへ近づく合成サンプルで t_hit を推定する。
var samples = new List<(double Time, double Y)>();
for (int i = 0; i < 6; i++)
{
    double t = i * 0.05;
    samples.Add((t, 0.20 + 0.90 * t));
}
if (HitTimePredictor.TryPredictHitTime(samples, profile.JudgmentLineY, out double hitTime, out double residual))
{
    Console.WriteLine($"予測コア自己確認: OK（t_hit={hitTime:F3}s, 残差={residual:F4}）");
}
else
{
    Console.WriteLine("予測コア自己確認: NG（推定に失敗しました）");
    return 1;
}

Console.WriteLine("実プレイ経路（キャプチャ/検出/入力）は v0.2 で入ります。");
if (args.Contains("--hold") && !Console.IsInputRedirected)
{
    Console.WriteLine("何かキーを押すと終了します。");
    Console.ReadKey(intercept: true);
}
return 0;
