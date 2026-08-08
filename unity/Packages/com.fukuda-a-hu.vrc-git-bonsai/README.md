# VRC Git Bonsai (com.fukuda-a-hu.vrc-git-bonsai)

GitHub リポジトリの進捗を「盆栽」として可視化する VRChat ワールド用アセットの VPM パッケージです。
詳しい仕組みやデータスキーマはリポジトリルートの README を参照してください。

## 構成

U# スクリプトを含む実装一式をこのパッケージだけで完結させています。

- `Runtime/Scripts/` — U# スクリプト本体とそれぞれの `UdonSharpProgramAsset`
  - `BonsaiJsonParser.cs` — bonsai.json（v2。v1 との後方互換あり）のパース
  - `BonsaiTreeBuilder.cs` — 幹・枝・葉のプロシージャルメッシュ生成
  - `BonsaiController.cs` — ダウンロード〜生成〜成長アニメの統括、枝の選択状態の同期
  - `BonsaiInfoPanel.cs` — 木札（ふだ）の表示更新
  - `BonsaiBranchTarget.cs` — 枝1本ぶんのUse用当たり判定
- `Runtime/Scripts/BonsaiGit.Runtime.asmdef` + `BonsaiGit.Runtime.UdonSharpAsmDef.asset` —
  UdonSharp コンパイラは既定では Packages/ 配下のスクリプトを認識しないが、対象アセンブリに
  asmdef と `UdonSharpAssemblyDefinition`（U# Assembly Definition）を対にして用意すると
  認識されるようになる（Unity 2022.3.22f1 + VRChat SDK 同梱の UdonSharp で実機確認済み）。
  この2ファイルがその仕組みを担っている
- `Runtime/Shaders/BonsaiVertexColor.shader` — 盆栽メッシュ用の頂点カラーシェーダ
- `Runtime/Materials/Bonsai.mat` — 上記シェーダを使うマテリアル
- `Runtime/Materials/BonsaiFuda.mat` — 木札（板）用のマテリアル（`Unlit/Color`）。
  `Bonsai.mat` は頂点カラー専用で単色を持たないため木札には使えず、別マテリアルにしている
- `Runtime/TestData/dummy-bonsai.json` — オフライン確認用のダミーデータ（v2 形式）
- `Runtime/Models/BonsaiBase.fbx` — 盆栽の土台モデル（木製台座・楕円鉢・苔つき土・岩2個）。
  Blender で作成した FBX で、頂点カラーに陰影を焼き込み済み。マテリアルは付属のものを使わず、
  シーン組み立て時に `Runtime/Materials/Bonsai.mat`（頂点カラーシェーダ）を割り当てる
- `Editor/BonsaiSceneSetup.cs` — `Bonsai/Setup PoC Scene` メニューでシーンを組み立てるエディタ拡張。
  盆栽本体・枝のUse用当たり判定16個・木札（TextMeshPro 4枚）まで含めて自動配線する

## 枝の選択と木札表示

枝をUseするとそのブランチの最新コミット情報が木札に表示されます（詳細はリポジトリルートの
READMEを参照）。木札の表示には TextMeshPro を使っているため、**事前に TMP Essential
Resources のインポートが必要**です（下記セットアップ参照）。そのうえで、**日本語のコミット
メッセージ・作者名を表示するには、木札の各 TextMeshPro（Heading / Message / Meta / Stats）の
Font Asset に日本語対応の SDF フォントアセットを Inspector で割り当ててください**（このパッケージ
には日本語フォントを同梱していません。TMP Essential Resources 導入直後の既定 Latin 専用フォント
のままだと日本語部分が豆腐（□）表示になります）。

## セットアップ

1. このパッケージを `Packages/com.fukuda-a-hu.vrc-git-bonsai/` に展開する
2. まだ導入していなければ、Unity メニューの `Window > TextMeshPro > Import TMP Essential
   Resources` を実行する（未導入のまま次の手順を行うと、TextMeshPro 生成時に Unity が
   「TMP Importer」ダイアログを表示してブロックされます。`Bonsai/Setup PoC Scene` 側でも
   未導入を検出してエラーで中断するようになっています）
3. Unity メニューの `Bonsai/Setup PoC Scene` を実行する

（シーンの保存先だけは利用者プロジェクトを汚さないよう `Assets/BonsaiGit/Scenes/` になります）

## 土台モデルの再生成

`Runtime/Models/BonsaiBase.fbx` はリポジトリ同梱の Blender スクリプト `scripts/blender/make_bonsai_base.py`
から再生成できます。Blender 5.x のヘッドレス実行で、カレントディレクトリ直下の `out/` に
`BonsaiBase.fbx`（と確認用の `.blend` / プレビュー画像）を出力します。

```
blender --background --python scripts/blender/make_bonsai_base.py
```

出力された `out/BonsaiBase.fbx` を `Runtime/Models/BonsaiBase.fbx` に上書きしてください。

## ライセンス

MIT License. `LICENSE` を参照してください。
