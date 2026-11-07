using System.Runtime.InteropServices;
using RhythmAutoplay.Core;
using ArrowDirection = RhythmAutoplay.Core.ArrowDirection;
using RhythmAutoplay.App;

// RhythmAutoplay 本体
// 引数なし / "gui": GUI を起動する（通常の使い方）。
// 引数 "play": 実プレイ経路（キャプチャ→検出→予測→入力）をコンソールで開始する。
//   追加引数: --dry-run（入力せず検出と発火予定だけ表示）
//             --offset-ms N（発火時刻の全体オフセット）
//             --seconds N（自動終了までの秒数。検証用）
// 引数 "snapshot": 較正用にフレームと検出結果を保存する。
// 引数 "info": プロファイルと予測コアの自己確認を表示する。

if (args.Length == 0 || args[0].Equals("gui", StringComparison.OrdinalIgnoreCase))
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm());
    return 0;
}

// CLI モードでは親コンソールに接続して出力する（WinExe のため）。
NativeConsole.AttachConsole(-1);

if (args.Length > 0 && args[0].Equals("play", StringComparison.OrdinalIgnoreCase))
{
    bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    // --log-samples は値を持たないフラグ。末尾に置かれても拾えるよう Contains で見る
    // （値つき引数のループは末尾要素を見ないため、v0.3.39 では末尾指定が無効だった）。
    bool logSamplesFlag = args.Contains("--log-samples", StringComparer.OrdinalIgnoreCase);
    bool logPresenceFlag = args.Contains("--log-presence", StringComparer.OrdinalIgnoreCase);
    bool logLineSamplesFlag = args.Contains("--log-line-samples", StringComparer.OrdinalIgnoreCase);
    bool watchFlag = args.Contains("--watch", StringComparer.OrdinalIgnoreCase);
    bool printWindowFlag = args.Contains("--print-window", StringComparer.OrdinalIgnoreCase);
    bool upperLinesFlag = args.Contains("--upper-lines", StringComparer.OrdinalIgnoreCase);
    bool denseLinesFlag = args.Contains("--dense-lines", StringComparer.OrdinalIgnoreCase);
    double offsetMs = 0;
    double strayMs = 0;
    double commitMs = 100;
    double holdMs = 0;
    int seconds = 0;
    string engine = "hybrid";
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--offset-ms" && double.TryParse(args[i + 1], out double v))
        {
            offsetMs = v;
        }
        if (args[i] == "--inject-stray-ms" && double.TryParse(args[i + 1], out double sv))
        {
            strayMs = sv;
        }
        if (args[i] == "--commit-ms" && double.TryParse(args[i + 1], out double cv))
        {
            commitMs = cv;
        }
        if (args[i] == "--hold-ms" && double.TryParse(args[i + 1], out double hv))
        {
            holdMs = hv;
        }
        if (args[i] == "--seconds" && int.TryParse(args[i + 1], out int s))
        {
            seconds = s;
        }
        if (args[i] == "--engine")
        {
            engine = args[i + 1];
        }
    }
    var playSession = new PlaySession(
        GameProfile.HypnosyncDefault(),
        new PlayOptions { DryRun = dryRun, OffsetMilliseconds = offsetMs, InjectStrayMilliseconds = strayMs, CommitWindowMilliseconds = commitMs, HoldMilliseconds = holdMs, LogSamples = logSamplesFlag, LogPresence = logPresenceFlag, LogLineSamples = logLineSamplesFlag, Watch = watchFlag, PrintWindowCapture = printWindowFlag, UpperLines = upperLinesFlag, DenseLines = denseLinesFlag, DurationSeconds = seconds, EventLogDirectory = GuiSettings.EventLogDirectory, Engine = engine });
    return playSession.Run();
}

if (args.Length > 0 && args[0].Equals("snapshot", StringComparison.OrdinalIgnoreCase))
{
    int snapSeconds = 8;
    int intervalMs = 500;
    string? outDir = null;
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--seconds" && int.TryParse(args[i + 1], out int s))
        {
            snapSeconds = s;
        }
        if (args[i] == "--interval-ms" && int.TryParse(args[i + 1], out int iv))
        {
            intervalMs = iv;
        }
        if (args[i] == "--out")
        {
            outDir = args[i + 1];
        }
    }
    var snapshotSession = new SnapshotSession(
        GameProfile.HypnosyncDefault(),
        new SnapshotOptions { Seconds = snapSeconds, IntervalMilliseconds = intervalMs, OutDirectory = outDir });
    return snapshotSession.Run();
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

Console.WriteLine("使い方: 引数なしで GUI、play で実プレイ、snapshot で較正用スナップショットを起動します。");
if (!Console.IsInputRedirected)
{
    Console.WriteLine("何かキーを押すと終了します。");
    Console.ReadKey(intercept: true);
}
return 0;

internal static class NativeConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int processId);
}
