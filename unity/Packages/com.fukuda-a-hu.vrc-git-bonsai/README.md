# VRC Git Bonsai (com.fukuda-a-hu.vrc-git-bonsai)

GitHub リポジトリの進捗を「盆栽」として可視化する VRChat ワールド用アセットの VPM パッケージです。
詳しい仕組みやデータスキーマはリポジトリルートの README を参照してください。

## 構成上の注意（重要）

UdonSharp コンパイラは **Packages/ 配下の U# スクリプトを認識しません**
（`does not belong to a U# assembly` エラーになることを Unity 2022.3.22f1 + VRChat SDK 同梱の
UdonSharp で実機確認済み）。そのため U# スクリプト本体（`BonsaiJsonParser.cs` /
`BonsaiTreeBuilder.cs` / `BonsaiController.cs` とそれぞれの `UdonSharpProgramAsset`）は、
このパッケージの外、利用者プロジェクトの `Assets/BonsaiGit/Scripts/` に別途コピーする必要が
あります。導入手順はリポジトリルートの README を参照してください。

このパッケージ自身が持つのは以下のみです。

- `Runtime/Shaders/BonsaiVertexColor.shader` — 盆栽メッシュ用の頂点カラーシェーダ
- `Runtime/Materials/Bonsai.mat` — 上記シェーダを使うマテリアル
- `Runtime/TestData/dummy-bonsai.json` — オフライン確認用のダミーデータ
- `Editor/BonsaiSceneSetup.cs` — `Bonsai/Setup PoC Scene` メニューでシーンを組み立てるエディタ拡張

## セットアップ

1. このパッケージを `Packages/com.fukuda-a-hu.vrc-git-bonsai/` に展開する
2. U# スクリプト一式を `Assets/BonsaiGit/Scripts/` にコピーする
3. Unity メニューの `Bonsai/Setup PoC Scene` を実行する

## ライセンス

MIT License. `LICENSE` を参照してください。
