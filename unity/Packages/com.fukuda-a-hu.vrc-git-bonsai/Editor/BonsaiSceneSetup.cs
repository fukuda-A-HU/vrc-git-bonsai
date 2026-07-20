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
        private const string DummyJsonPath = PackageRuntimeFolder + "/TestData/dummy-bonsai.json";

        private static readonly string[] ProgramCsPaths =
        {
            PackageRuntimeFolder + "/Scripts/BonsaiJsonParser.cs",
            PackageRuntimeFolder + "/Scripts/BonsaiTreeBuilder.cs",
            PackageRuntimeFolder + "/Scripts/BonsaiController.cs",
        };

        [MenuItem("Bonsai/Setup PoC Scene")]
        public static void SetupScene()
        {
            EnsureProgramAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateLight();
            CreateFloor();
            CreateVrcWorld();
            GameObject pot = CreatePot();
            GameObject bonsai = CreateBonsai(pot);

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
            descriptor.spawns = new[] { spawn.transform };
        }

        private static GameObject CreatePot()
        {
            GameObject pot = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pot.name = "Pot";
            pot.transform.localScale = new Vector3(0.3f, 0.08f, 0.3f);
            pot.transform.position = new Vector3(0f, 0.08f, 0f);
            return pot;
        }

        private static GameObject CreateBonsai(GameObject pot)
        {
            // Pot は非等方スケールなので、メッシュ座標がそのまま歪まないよう親子付けはしない
            // （成長アニメで localScale を 0→1 に動かすのもこのオブジェクト単体で完結させたい）。
            float potTopY = pot.transform.position.y + pot.transform.localScale.y;
            GameObject bonsai = new GameObject("Bonsai");
            bonsai.transform.position = new Vector3(0f, potTopY, 0f);

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

            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(parser);
            EditorUtility.SetDirty(builder);

            return bonsai;
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
