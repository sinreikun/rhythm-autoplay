# RhythmAutoplay

画面キャプチャだけを頼りに音ゲーを自動プレイする、汎用「外部」オートプレイプログラムです。
プロセスメモリや譜面ファイルは読みません。最初の対応タイトルは Steam 版 **HYPNOSYNC**（App ID 2986590）です。

設計の全体像は [docs/design.md](docs/design.md) を参照してください。

## 使い方（利用者向け）

1. 最新 Release から **`RhythmAutoplay.exe`（ランチャー）だけ** をダウンロードして、好きな場所に置きます。
2. あとはその exe を実行するだけです。起動のたびに GitHub の最新版を確認し、本体を自動で取得・更新してから起動します（導入先: `%LOCALAPPDATA%\RhythmAutoplay`）。本体を別途ダウンロードする必要はありません。
3. HYPNOSYNC 側のキー設定（Settings → View Keybinds → Edit Keybinds）を、下の「HYPNOSYNC のキー配置」に合わせてください。

## HYPNOSYNC のキー配置

HYPNOSYNC は4レーンに5色のノーツが流れ、色ごとに対応キーが違う12キー設計です（赤/青はレーンごとに別キー、紫は同時押し、黄はどちらでも可、横長ノーツは方向キー）。

| レーン | 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| 赤キー | D | F | J | K |
| 青キー | E | R | U | I |

方向キー: 左=S / 右=L / 下=V / 上=N

## 開発者向け

- 言語: C# (.NET 8)。ランチャーは Native AOT、本体は self-contained 単一ファイルで公開します。
- 版管理: リポジトリ直下の `version.txt` が唯一の正です。**改修のたびに版を上げて main へ push** すると、GitHub Actions がテスト→ビルド→Release 公開まで自動で行います（同じ版の Release がある場合は公開をスキップします）。
- 構成:
  - `src/RhythmAutoplay.Launcher` — 起動用 exe。最新版確認・本体の取得（SHA-256 照合つき）・起動だけを担います。
  - `src/RhythmAutoplay.App` — 本体。
  - `src/RhythmAutoplay.Core` — プロファイル、到達時刻の予測、入力スケジューラなどの共通コア。
  - `profiles/` — ゲーム別プロファイル（データ）。
  - `tests/` — コアの単体テスト。

## 現在の状態

- v0.1.0: 土台版。ランチャー＋自動更新、HYPNOSYNC プロファイル、予測/スケジューラのコアとテスト、CI による版付き Release 公開まで。
- 次版 v0.2（予定）: キャプチャ→検出→入力の実プレイ経路を、HYPNOSYNC 実機検証と一緒に入れます。SendInput が本作で素直に通るかが最初の確認項目です（docs/design.md §14）。
