using RhythmAutoplay.Core;
using RhythmAutoplay.App;

// RhythmAutoplay 本体
// 引数なし: プロファイルと予測コアの自己確認を表示する。
// 引数 "play": 実プレイ経路（キャプチャ→検出→予測→入力）を開始する。
//   追加引数: --dry-run（入力せず検出と発火予定だけ表示）
//             --offset-ms N（発火時刻の全体オフセット）
//             --seconds N（自動終了までの秒数。検証用）

if (args.Length > 0 && args[0].Equals("play", StringComparison.OrdinalIgnoreCase))
{
    bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    double offsetMs = 0;
    int seconds = 0;
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--offset-ms" && double.TryParse(args[i + 1], out double v))
        {
            offsetMs = v;
        }
        if (args[i] == "--seconds" && int.TryParse(args[i + 1], out int s))
        {
            seconds = s;
        }
    }
    var playSession = new PlaySession(
        GameProfile.HypnosyncDefault(),
        new PlayOptions { DryRun = dryRun, OffsetMilliseconds = offsetMs, DurationSeconds = seconds });
    return playSession.Run();
}

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

Console.WriteLine("これは v0.1 の土台確認版です。実プレイ経路（キャプチャ/検出/入力）は v0.2 で入ります。");
if (!Console.IsInputRedirected)
{
    Console.WriteLine("何かキーを押すと終了します。");
    Console.ReadKey(intercept: true);
}
return 0;
