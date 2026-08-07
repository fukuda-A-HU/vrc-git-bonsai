using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Components;
using VRC.SDKBase;
using BonsaiGit;

namespace BonsaiGit.Editor
{
    /// <summary>
    /// PoC検証用シーンを1メニューで組み立てる。何度実行してもシーンを作り直すだけなので冪等。
    /// UdonSharpProgramAsset は既存があれば使い回し（GUID を無駄に変えないため）、無ければ生成する。
    /// </summary>
    public static class BonsaiSceneSetup
    {
        private const string BonsaiJsonUrl = "https://fukuda-a-hu.github.io/vrc-git-bonsai/bonsai.json";

        // UdonSharp コンパイラは既定では Packages/ 配下のスクリプトを認識しないが、
        // 対象アセンブリに asmdef + UdonSharpAssemblyDefinition（BonsaiGit.Runtime.asmdef /
        // BonsaiGit.Runtime.UdonSharpAsmDef.asset）を用意すれば認識されることを実機で確認済み。
        // そのため U# スクリプト本体も含め、シーン保存先以外は VPM パッケージ側に集約している。
        private const string AssetsRootFolder = "Assets/BonsaiGit";
        private const string PackageRuntimeFolder = "Packages/com.fukuda-a-hu.vrc-git-bonsai/Runtime";

        private const string ScenePath = AssetsRootFolder + "/Scenes/BonsaiPoC.unity";
        private const string ShaderPath = PackageRuntimeFolder + "/Shaders/BonsaiVertexColor.shader";
        private const string MaterialPath = PackageRuntimeFolder + "/Materials/Bonsai.mat";
        private const string FudaMaterialPath = PackageRuntimeFolder + "/Materials/BonsaiFuda.mat";
        private const string DummyJsonPath = PackageRuntimeFolder + "/TestData/dummy-bonsai.json";
        private const string ModelPath = PackageRuntimeFolder + "/Models/BonsaiBase.fbx";

        private const string TreeAnchorName = "TreeAnchor";

        // 木札（ふだ）板の実寸。台座（BoxCollider center.y=0.19、size 0.95×0.38×0.70）の右脇に、
        // 板の下端がy=0.20・上端がy=0.54になるよう中心位置を決める。
        private const float FudaWidth = 0.26f;
        private const float FudaHeight = 0.34f;
        private const float FudaThickness = 0.02f;
        private static readonly Vector3 FudaPosition = new Vector3(0.62f, 0.20f + FudaHeight * 0.5f, 0f);

        private const string BranchTargetNamePrefix = "BranchTarget_";
        private const float BranchTargetRadius = 0.06f;

        private static readonly string[] ProgramCsPaths =
        {
            PackageRuntimeFolder + "/Scripts/BonsaiJsonParser.cs",
            PackageRuntimeFolder + "/Scripts/BonsaiTreeBuilder.cs",
            PackageRuntimeFolder + "/Scripts/BonsaiController.cs",
            PackageRuntimeFolder + "/Scripts/BonsaiInfoPanel.cs",
            PackageRuntimeFolder + "/Scripts/BonsaiBranchTarget.cs",
        };

        [MenuItem("Bonsai/Setup PoC Scene")]
        public static void SetupScene()
        {
            EnsureProgramAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateLight();
            CreateFloor();
            CreateVrcWorld();
            GameObject baseModel = CreateBase();
            GameObject bonsai = CreateBonsai(baseModel);

            AssetDatabase.SaveAssets();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            Selection.activeGameObject = bonsai;
            Debug.Log("[BonsaiSceneSetup] scene setup complete: " + ScenePath);
        }

        private static void EnsureProgramAssets()
        {
            bool createdAny = false;

            foreach (string csPath in ProgramCsPaths)
            {
                string assetPath = csPath.Substring(0, csPath.Length - 3) + ".asset";

                if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null)
                    continue;

                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(csPath);
                if (script == null)
                {
                    Debug.LogError("[BonsaiSceneSetup] script not found, cannot create program asset: " + csPath);
                    continue;
                }

                UdonSharpProgramAsset programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                programAsset.sourceCsScript = script;
                AssetDatabase.CreateAsset(programAsset, assetPath);
                Debug.Log("[BonsaiSceneSetup] created program asset: " + assetPath);
                createdAny = true;
            }

            if (createdAny)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            // U# → Udon アセンブリへのコンパイルをこの場で確定させる。エラーがあればここで Console に出る。
            UdonSharpCompilerV1.CompileSync();
        }

        private static void CreateLight()
        {
            GameObject lightGo = new GameObject("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static void CreateFloor()
        {
            // ClientSim のリスポーンループを防ぐため、地面となる Collider 付きの Plane を必ず置く。
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.position = Vector3.zero;
        }

        private static void CreateVrcWorld()
        {
            GameObject worldGo = new GameObject("VRCWorld");
            VRCSceneDescriptor descriptor = worldGo.AddComponent<VRCSceneDescriptor>();

            // spawns が空だと ClientSim の EnablePlayerObjects が NullReferenceException になるため必須。
            // 盆栽（原点付近）から2m離れた地点にスポーン地点を用意する。
            GameObject spawn = new GameObject("Spawn");
            spawn.transform.SetParent(worldGo.transform);
            spawn.transform.position = new Vector3(2f, 0f, 0f);
            // スポーン直後に原点の盆栽が視界に入るよう、-X 方向（盆栽側）を向ける。
            spawn.transform.rotation = Quaternion.Euler(0f, -90f, 0f);
            descriptor.spawns = new[] { spawn.transform };
        }

        private static GameObject CreateBase()
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null)
            {
                Debug.LogWarning("[BonsaiSceneSetup] base model not found (" + ModelPath + "). falling back to primitive pot.");
                return CreateFallbackPot();
            }

            GameObject baseModel = (GameObject)PrefabUtility.InstantiatePrefab(model);
            baseModel.name = "BonsaiBase";
            baseModel.transform.position = Vector3.zero;
            // instantiate 直後のルート回転を明示的にゼロへ戻す。FBX インポート時にルートへ軸変換の
            // 回転が乗るケースがあっても、これで BoxCollider のローカル軸＝ワールド軸を保証できる。
            baseModel.transform.rotation = Quaternion.identity;

            Material material = GetOrCreateMaterial();
            foreach (MeshRenderer meshRenderer in baseModel.GetComponentsInChildren<MeshRenderer>(true))
                meshRenderer.sharedMaterial = material;

            BoxCollider collider = baseModel.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 0.19f, 0f);
            collider.size = new Vector3(0.95f, 0.38f, 0.70f);

            return baseModel;
        }

        private static GameObject CreateFallbackPot()
        {
            GameObject pot = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pot.name = "Pot";
            pot.transform.localScale = new Vector3(0.3f, 0.08f, 0.3f);
            pot.transform.position = new Vector3(0f, 0.08f, 0f);
            return pot;
        }

        private static Vector3 FindTreeAnchorPosition(GameObject baseModel)
        {
            Transform anchor = FindChildRecursive(baseModel.transform, TreeAnchorName);
            if (anchor != null)
                return anchor.position;

            // フォールバック（CreateFallbackPot 相当の Cylinder 鉢）：従来ロジックと同じ高さを算出する。
            Debug.LogWarning("[BonsaiSceneSetup] " + TreeAnchorName + " not found under base model, falling back to legacy height.");
            return new Vector3(0f, baseModel.transform.position.y + baseModel.transform.localScale.y, 0f);
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name)
                return parent;

            foreach (Transform child in parent)
            {
                Transform found = FindChildRecursive(child, name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static GameObject CreateBonsai(GameObject baseModel)
        {
            // baseModel（土台モデル or フォールバック鉢）は非等方スケールを持ちうるため、
            // メッシュ座標がそのまま歪まないよう親子付けはしない
            // （成長アニメで localScale を 0→1 に動かすのもこのオブジェクト単体で完結させたい）。
            Vector3 anchorPosition = FindTreeAnchorPosition(baseModel);
            GameObject bonsai = new GameObject("Bonsai");
            bonsai.transform.position = anchorPosition;

            bonsai.AddComponent<MeshFilter>();
            MeshRenderer renderer = bonsai.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GetOrCreateMaterial();

            BonsaiJsonParser parser = bonsai.AddUdonSharpComponent<BonsaiJsonParser>();
            BonsaiTreeBuilder builder = bonsai.AddUdonSharpComponent<BonsaiTreeBuilder>();
            BonsaiController controller = bonsai.AddUdonSharpComponent<BonsaiController>();

            controller.parser = parser;
            controller.builder = builder;
            controller.dummyJson = AssetDatabase.LoadAssetAtPath<TextAsset>(DummyJsonPath);
            controller.useDummy = true; // Pages 未マージでも検証できるようデフォルトはダミー使用
            controller.jsonUrl = new VRCUrl(BonsaiJsonUrl);

            controller.infoPanel = CreateFuda();
            controller.branchTargets = CreateBranchTargets(bonsai, controller);
            // trunkTarget（幹用の当たり判定）は、これを読んでInteractする側のロジックが
            // どこにも存在しない（枝のUseはBonsaiBranchTarget.Interact()経由だが、幹側の
            // 同等コンポーネントは未実装）ため、今回は配線せず未設定のままにする。

            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(parser);
            EditorUtility.SetDirty(builder);

            return bonsai;
        }

        /// <summary>
        /// 枝のUse用当たり判定を16個、Bonsaiの子として常設する。BonsaiController.ActivateBranchTargets()の
        /// コメントにある通り、branchTipPositionsはBonsaiのローカル座標であることが前提のため、
        /// 必ずBonsaiの子として生成する。実際の配置・有効化は成長アニメ完了後にControllerが行うので、
        /// ここでは全個体を無効化した状態で用意するだけでよい。
        /// </summary>
        private static BonsaiBranchTarget[] CreateBranchTargets(GameObject bonsai, BonsaiController controller)
        {
            BonsaiBranchTarget[] targets = new BonsaiBranchTarget[BonsaiJsonParser.MaxBranches];

            for (int i = 0; i < targets.Length; i++)
            {
                GameObject targetGo = new GameObject(BranchTargetNamePrefix + i.ToString("00"));
                targetGo.transform.SetParent(bonsai.transform, false);

                SphereCollider collider = targetGo.AddComponent<SphereCollider>();
                collider.radius = BranchTargetRadius;
                collider.isTrigger = false;

                BonsaiBranchTarget target = targetGo.AddUdonSharpComponent<BonsaiBranchTarget>();
                target.controller = controller;
                EditorUtility.SetDirty(target);

                // 位置(branchIndexに対応するbranchTipPositions)と有効化はBonsaiController側が
                // 成長アニメ完了後（ActivateBranchTargets）に行う。ここでは常に非表示から始める。
                targetGo.SetActive(false);

                targets[i] = target;
            }

            return targets;
        }

        /// <summary>
        /// 木札（ふだ）を生成する。Bonsaiオブジェクトの子にはしない。Bonsaiは成長アニメで
        /// localScaleを0→1へ動かすため、子にしてしまうと木札まで縮んだ状態から生えてくる
        /// ように見えてしまう（木札は最初から実寸で固定表示したい）。
        /// </summary>
        private static BonsaiInfoPanel CreateFuda()
        {
            GameObject fuda = new GameObject("Fuda");
            fuda.transform.position = FudaPosition;
            // スポーン地点（+X側）から盆栽（原点付近）へ歩いてくるプレイヤーから読めるよう
            // 正面をおおむね+X寄りにしつつ、盆栽側（-X）へもやや傾ける。
            // ※ TextMeshProの「読める面」がローカルのどちら向きになるかは実機未検証。
            //   文字が裏返って見える場合はこの回転を180度反転すること。
            fuda.transform.rotation = Quaternion.Euler(0f, 100f, 0f);

            GameObject board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "Board";
            board.transform.SetParent(fuda.transform, false);
            board.transform.localScale = new Vector3(FudaWidth, FudaHeight, FudaThickness);
            board.GetComponent<MeshRenderer>().sharedMaterial = GetOrCreateFudaMaterial();

            BonsaiInfoPanel panel = fuda.AddUdonSharpComponent<BonsaiInfoPanel>();

            // 板の厚み方向（ローカルZ）のすぐ外側にテキストを配置する。
            float textZ = FudaThickness * 0.5f + 0.002f;
            // 座標は板のローカル（実寸 m）、sizeDelta とフォントサイズはテキスト側の 1/100 スケール前の値。
            // つまり sizeDelta 22 = 幅 0.22m、fontSize 1 = 文字高 0.01m に相当する。
            // 板の高さは 0.34m（y は -0.17〜+0.17）なので、4つの枠が重ならず収まるよう縦に積む。
            // フォントサイズは「その枠に想定文字数が入るか」で決めている。特に message は日本語80文字
            // （generate-bonsai-json.mjs の切り詰め上限）が最悪ケースで、fontSize 1.2 なら 1行18文字×5行弱で収まる。
            panel.headingText = CreateFudaText(fuda.transform, "HeadingText", new Vector3(0f, 0.130f, textZ), new Vector2(22f, 5f), 1.5f, 2.5f);
            panel.messageText = CreateFudaText(fuda.transform, "MessageText", new Vector3(0f, 0.045f, textZ), new Vector2(22f, 12f), 0.8f, 1.6f);
            panel.metaText = CreateFudaText(fuda.transform, "MetaText", new Vector3(0f, -0.055f, textZ), new Vector2(22f, 5f), 0.8f, 1.4f);
            panel.statsText = CreateFudaText(fuda.transform, "StatsText", new Vector3(0f, -0.115f, textZ), new Vector2(22f, 5f), 1.0f, 1.8f);

            EditorUtility.SetDirty(panel);
            return panel;
        }

        /// <summary>
        /// 木札用のワールド空間TextMeshProを1つ生成する。
        /// 既定のフォントサイズは0.26×0.34m程度の板には大きすぎるため、オブジェクト自体を
        /// 1/100スケールにしたうえで、sizeDelta・フォントサイズはその「縮小前」の数値で指定する
        /// （VRChatワールドのネームプレート等でもよく使われる手法）。文字列の長さが読めないため、
        /// enableAutoSizingで指定範囲に収まるよう自動調整する。
        /// </summary>
        private static TMPro.TextMeshPro CreateFudaText(Transform parent, string name, Vector3 localPosition, Vector2 sizeDelta, float fontSizeMin, float fontSizeMax)
        {
            GameObject go = new GameObject(name);
            TMPro.TextMeshPro tmp = go.AddComponent<TMPro.TextMeshPro>();

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = Vector3.one * 0.01f;

            tmp.text = "";
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.color = new Color(0.95f, 0.92f, 0.85f); // 焦げ茶の板に映える生成り色
            tmp.enableWordWrapping = true;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = fontSizeMin;
            tmp.fontSizeMax = fontSizeMax;
            tmp.rectTransform.sizeDelta = sizeDelta;

            return tmp;
        }

        private static Material GetOrCreateFudaMaterial()
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(FudaMaterialPath);
            if (existing != null)
                return existing;

            // Bonsai.mat は頂点カラー専用のUnlitシェーダーで単色を持たないため、そのまま使うと
            // 木札が真っ黒になる。木札は単色の暗い木目色で塗るだけでよいので汎用のUnlit/Colorを使う。
            Material mat = new Material(Shader.Find("Unlit/Color"));
            mat.color = new Color(0.32f, 0.20f, 0.11f); // 焦げ茶
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FudaMaterialPath));
            AssetDatabase.CreateAsset(mat, FudaMaterialPath);
            return mat;
        }

        private static Material GetOrCreateMaterial()
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null)
                return existing;

            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
                shader = Shader.Find("BonsaiGit/VertexColor");

            Material mat = new Material(shader);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MaterialPath));
            AssetDatabase.CreateAsset(mat, MaterialPath);
            return mat;
        }
    }
}
