using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

// RhythmAutoplay ランチャー（配布物はこの exe のみ）
// 起動のたびに GitHub Releases の最新版を確認し、本体を自動更新してから起動する。
// 設計: docs/design.md §15

const string RepoOwner = "sinreikun";
const string RepoName = "rhythm-autoplay";
const string BodyAssetName = "RhythmAutoplay-Body.zip";
const string BodyShaAssetName = "RhythmAutoplay-Body.sha256";
const string BodyExeName = "RhythmAutoplayBody.exe";

string appDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "RhythmAutoplay");
string versionsDir = Path.Combine(appDir, "versions");
string versionFile = Path.Combine(appDir, "installed-version.txt");
Directory.CreateDirectory(versionsDir);

string? installedVersion = File.Exists(versionFile)
    ? File.ReadAllText(versionFile).Trim()
    : null;

string? targetVersion = installedVersion;
string? bodyDir = installedVersion is null ? null : Path.Combine(versionsDir, installedVersion);
bool haveUsableBody = bodyDir is not null && File.Exists(Path.Combine(bodyDir, BodyExeName));

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("RhythmAutoplay-Launcher");

    string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    string releaseJson = await http.GetStringAsync(apiUrl);
    string? latestTag = ExtractJsonString(releaseJson, "tag_name");

    if (latestTag is not null && (latestTag != installedVersion || !haveUsableBody))
    {
        Console.WriteLine($"新しい本体があります: {latestTag}（現在: {installedVersion ?? "未導入"}）");
        string? zipUrl = FindAssetUrl(releaseJson, BodyAssetName);
        string? shaUrl = FindAssetUrl(releaseJson, BodyShaAssetName);
        if (zipUrl is null)
        {
            throw new InvalidOperationException("Release に本体 zip が見つかりませんでした。");
        }

        byte[] zipBytes = await http.GetByteArrayAsync(zipUrl);
        if (shaUrl is not null)
        {
            string expected = (await http.GetStringAsync(shaUrl)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            string actual = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("本体 zip の SHA-256 が一致しませんでした。更新を中止します。");
            }
        }

        string newDir = Path.Combine(versionsDir, latestTag);
        Directory.CreateDirectory(newDir);
        string tempZip = Path.Combine(appDir, "download.tmp.zip");
        await File.WriteAllBytesAsync(tempZip, zipBytes);
        ZipFile.ExtractToDirectory(tempZip, newDir, overwriteFiles: true);
        File.Delete(tempZip);

        if (!File.Exists(Path.Combine(newDir, BodyExeName)))
        {
            throw new InvalidOperationException("展開した本体に実行ファイルがありませんでした。");
        }

        await File.WriteAllTextAsync(versionFile, latestTag);
        targetVersion = latestTag;
        bodyDir = newDir;
        haveUsableBody = true;
        Console.WriteLine($"本体を {latestTag} に更新しました。");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"更新の確認に失敗しました: {ex.Message}");
    if (haveUsableBody)
    {
        Console.WriteLine("導入済みの本体で起動します。");
    }
}

if (!haveUsableBody || bodyDir is null)
{
    Console.WriteLine("起動できる本体がありません。ネットワーク接続を確認して、もう一度お試しください。");
    WaitForKeyIfInteractive();
    return 1;
}

var startInfo = new ProcessStartInfo
{
    FileName = Path.Combine(bodyDir, BodyExeName),
    WorkingDirectory = bodyDir,
    UseShellExecute = true,
};
foreach (string arg in args)
{
    startInfo.ArgumentList.Add(arg);
}

Console.WriteLine($"RhythmAutoplay {targetVersion} を起動します。");
Process.Start(startInfo);
return 0;

static void WaitForKeyIfInteractive()
{
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("何かキーを押すと終了します。");
        Console.ReadKey(intercept: true);
    }
}

// AOT ではリフレクション型の JSON シリアライザを使わない。
// 必要な2項目だけを単純な文字列走査で取り出す。
static string? ExtractJsonString(string json, string key)
{
    string marker = $"\"{key}\": \"";
    int start = json.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0)
    {
        return null;
    }
    start += marker.Length;
    int end = json.IndexOf('"', start);
    return end < 0 ? null : json[start..end];
}

static string? FindAssetUrl(string releaseJson, string assetName)
{
    string nameMarker = $"\"name\": \"{assetName}\"";
    int nameIndex = releaseJson.IndexOf(nameMarker, StringComparison.Ordinal);
    if (nameIndex < 0)
    {
        return null;
    }
    string urlMarker = "\"browser_download_url\": \"";
    int start = releaseJson.IndexOf(urlMarker, nameIndex, StringComparison.Ordinal);
    if (start < 0)
    {
        return null;
    }
    start += urlMarker.Length;
    int end = releaseJson.IndexOf('"', start);
    return end < 0 ? null : releaseJson[start..end];
}
