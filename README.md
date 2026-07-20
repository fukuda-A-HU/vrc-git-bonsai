# vrc-git-bonsai

GitHub リポジトリの進捗を「盆栽」として可視化する VRChat ワールド用アセットの PoC です。
デフォルトブランチのコミット履歴を幹（trunk）に、各ブランチを枝に見立て、コミット数・経過日数・
分岐位置などをもとに枝ぶりを決定します。

## データパイプラインの仕組み

1. `scripts/generate-bonsai-json.mjs` が対象リポジトリの git ログを解析し、幹とブランチの情報を
   軽量な JSON (`out/bonsai.json`) に変換します。Node.js 標準モジュール（`child_process` /
   `fs` / `path`）のみを使い、外部パッケージには依存しません。
2. GitHub Actions ワークフロー `.github/workflows/publish-bonsai-json.yml` が
   - 6 時間ごとの `schedule`
   - `workflow_dispatch`（手動実行）
   - `master` への `push`

   をトリガーに JSON を再生成し、`jq` でスキーマを検証したうえで GitHub Pages にデプロイします。
3. VRChat ワールド（Udon）側は配信された JSON を `VRCJson` で取得し、盆栽の描画に使います。

### 配信 URL

```
https://fukuda-a-hu.github.io/vrc-git-bonsai/bonsai.json
```

## JSON スキーマ

`VRCJson` の制約（文字列フィールドを含められない）に合わせ、値はすべて数値のみで構成されます。

```json
{
  "v": 1,
  "gen": 1700000000,
  "trunk": { "commits": 42, "recent30": 5, "len": 0.72 },
  "branches": [
    { "h": 0.85, "len": 0.42, "ahead": 3, "behind": 5, "age": 0.03, "seed": 217 }
  ]
}
```

| フィールド | 意味 |
| --- | --- |
| `v` | スキーマバージョン（固定値 `1`） |
| `gen` | 生成時刻（unix 秒） |
| `trunk.commits` | デフォルトブランチの first-parent チェーン長 |
| `trunk.recent30` | 直近30日の first-parent コミット数 |
| `trunk.len` | 幹の見た目の長さ（0〜1に正規化） |
| `branches[].h` | 幹上の分岐位置（0=根本 / 1=幹の先端） |
| `branches[].len` | 枝の長さ（0〜1、ahead コミット数の対数スケール） |
| `branches[].ahead` | デフォルトブランチとの merge-base からの先行コミット数 |
| `branches[].behind` | デフォルトブランチに対する遅れコミット数 |
| `branches[].age` | ブランチ先端の経過日数（0〜1、90日でクランプ） |
| `branches[].seed` | ブランチ名から算出した疑似乱数シード（0〜359、枝の向き等の見た目バリエーション用） |

`ahead == 0` のブランチ（デフォルトブランチに完全に追従しているだけのブランチ）は除外され、
最大 16 本までブランチ先端の新しい順に採用されます。

## ローカル実行方法

```bash
# カレントディレクトリの git リポジトリを対象に生成
node scripts/generate-bonsai-json.mjs

# 別リポジトリを対象にする場合
BONSAI_REPO_DIR=/path/to/other/repo node scripts/generate-bonsai-json.mjs
```

実行時のカレントディレクトリ配下の `out/bonsai.json` に出力されます（`BONSAI_OUT_DIR` で変更可能）。`origin` への `git fetch` に失敗した場合は警告を出しつつ
ローカルの refs のみで処理を続行します（オフライン・単体テスト向け）。

## 注意事項

- `schedule` トリガーによる GitHub Actions の定期実行は、**リポジトリに 60 日間まったく
  アクティビティ（push 等）が無いと GitHub 側の仕様により自動的に無効化されます**。
  再度動かすには `workflow_dispatch` で手動実行するか、何らかの push を行ってください。

## ワールドへの導入

このリポジトリの実装は VPM パッケージ `com.fukuda-a-hu.vrc-git-bonsai`（`unity/Packages/`
配下）として配布しています。第三者のワールドプロジェクトに組み込む手順は次のとおりです。

### 1. パッケージの導入

1. [Releases](../../releases) から最新の `com.fukuda-a-hu.vrc-git-bonsai-<version>.zip` を
   ダウンロードする
2. 自分の VRChat ワールドプロジェクトの `Packages/com.fukuda-a-hu.vrc-git-bonsai/` に展開する
   （zip ルート直下に `package.json` が来る構造なので、そのままこのフォルダ名で展開すればよい）
3. Unity メニューの `Bonsai/Setup PoC Scene` を実行すると、盆栽オブジェクトを含むシーンが
   組み立てられる（シーンの保存先は利用者プロジェクト内の `Assets/BonsaiGit/Scenes/` になる）

U# スクリプト（`BonsaiJsonParser.cs` / `BonsaiTreeBuilder.cs` / `BonsaiController.cs`）を含め
実装一式がパッケージ内で完結するため、`Assets/` 側への追加コピーは不要です（詳細は下記
「制約」参照）。

### 2. 自分のリポジトリを盆栽化する

配信元をこのリポジトリ（`fukuda-a-hu/vrc-git-bonsai`）から自分のリポジトリに差し替える手順です。

1. `scripts/generate-bonsai-json.mjs` と `.github/workflows/publish-bonsai-json.yml` を
   自分のリポジトリにコピーする
2. 自分のリポジトリの Settings > Pages で **Build and deployment > Source** を
   `GitHub Actions`（`build_type=workflow`）に設定する
3. コピーしたワークフローを一度 `workflow_dispatch` などで実行し、
   `https://<自分のGitHubユーザー名>.github.io/<リポジトリ名>/bonsai.json` が配信されることを確認する
4. ワールド側（`Packages/com.fukuda-a-hu.vrc-git-bonsai/Editor/BonsaiSceneSetup.cs` の
   `BonsaiJsonUrl` 定数、または `Bonsai/Setup PoC Scene` で生成された `BonsaiController` の
   `Json Url` フィールド）を、自分の配信 URL に差し替える

### 制約

- 対象リポジトリは **public** である必要があります（GitHub Pages の無料枠は public リポジトリ
  のみが対象、かつ VRChat の String Loading は認証なしで取得できる URL しか使えないため）。
- 配信先ドメインは `*.github.io` である必要があります。VRChat の String Loading /
  Image Loading が許可する信頼済みドメインの一つのため、追加のホワイトリスト申請なしに
  ワールドから読み込めます。
- UdonSharp コンパイラは既定では **Packages/ 配下の U# スクリプトを認識しません**
  （`does not belong to a U# assembly` エラーになることを Unity 2022.3.22f1 + VRChat SDK 同梱の
  UdonSharp で確認済み）。本パッケージでは対象アセンブリに asmdef（`BonsaiGit.Runtime.asmdef`）
  と `UdonSharpAssemblyDefinition`（`BonsaiGit.Runtime.UdonSharpAsmDef.asset`、Unity の
  `Assets/Create/U# Assembly Definition` で作れるアセット）を対にして用意することで
  認識されることを実機確認し、U# スクリプト本体もパッケージ内（`Runtime/Scripts/`）に
  同梱しています。
