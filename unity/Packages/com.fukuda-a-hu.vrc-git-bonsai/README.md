# VRC Git Bonsai (com.fukuda-a-hu.vrc-git-bonsai)

GitHub リポジトリの進捗を「盆栽」として可視化する VRChat ワールド用アセットの VPM パッケージです。
詳しい仕組みやデータスキーマはリポジトリルートの README を参照してください。

## 構成

U# スクリプトを含む実装一式をこのパッケージだけで完結させています。

- `Runtime/Scripts/` — U# スクリプト本体（`BonsaiJsonParser.cs` / `BonsaiTreeBuilder.cs` /
  `BonsaiController.cs`）とそれぞれの `UdonSharpProgramAsset`
- `Runtime/Scripts/BonsaiGit.Runtime.asmdef` + `BonsaiGit.Runtime.UdonSharpAsmDef.asset` —
  UdonSharp コンパイラは既定では Packages/ 配下のスクリプトを認識しないが、対象アセンブリに
  asmdef と `UdonSharpAssemblyDefinition`（U# Assembly Definition）を対にして用意すると
  認識されるようになる（Unity 2022.3.22f1 + VRChat SDK 同梱の UdonSharp で実機確認済み）。
  この2ファイルがその仕組みを担っている
- `Runtime/Shaders/BonsaiVertexColor.shader` — 盆栽メッシュ用の頂点カラーシェーダ
- `Runtime/Materials/Bonsai.mat` — 上記シェーダを使うマテリアル
- `Runtime/TestData/dummy-bonsai.json` — オフライン確認用のダミーデータ
- `Runtime/Models/BonsaiBase.fbx` — 盆栽の土台モデル（木製台座・楕円鉢・苔つき土・岩2個）。
  Blender で作成した FBX で、頂点カラーに陰影を焼き込み済み。マテリアルは付属のものを使わず、
  シーン組み立て時に `Runtime/Materials/Bonsai.mat`（頂点カラーシェーダ）を割り当てる
- `Editor/BonsaiSceneSetup.cs` — `Bonsai/Setup PoC Scene` メニューでシーンを組み立てるエディタ拡張

## セットアップ

1. このパッケージを `Packages/com.fukuda-a-hu.vrc-git-bonsai/` に展開する
2. Unity メニューの `Bonsai/Setup PoC Scene` を実行する

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
