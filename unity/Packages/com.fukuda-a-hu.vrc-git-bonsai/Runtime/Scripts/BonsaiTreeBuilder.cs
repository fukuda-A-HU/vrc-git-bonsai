using UdonSharp;
using UnityEngine;

namespace BonsaiGit
{
    /// <summary>
    /// BonsaiJsonParser がパースした幹・枝の数値から、和風・雲型段葉（クラウドパッド）のプロシージャルメッシュを組み立てる。
    /// 幹: 9リング×6角柱（54頂点+先端キャップ1点）。枝: 1本あたり4リング×4角柱（16頂点+先端キャップ1点）で、ほぼ水平に張り出す。
    /// 葉は交差クワッドではなく、8分割の扁平ディスク「雲」（上下ファン=16三角形/18頂点）を全枝の先端と幹頂に1枚ずつ乗せる。
    /// 鉢・土・台座は別オブジェクト（Blender製の土台モデル）が担当するため、このクラスでは一切生成しない。
    /// シェーダーは法線・ライトを一切使わない完全な Unlit のため、簡易ランバートの陰影をあらかじめ頂点カラーへ焼き込む
    /// （土台モデルの Blender スクリプトと同じライト方向・同じ係数を使い、見た目を揃える）。
    /// 頂点・法線・色は配列に貯めて最後に一括で Mesh に代入する（SetVertices 等は使わない）。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BonsaiTreeBuilder : UdonSharpBehaviour
    {
        private const int TrunkRings = 9;
        private const int TrunkSides = 6;
        private const int BranchRings = 4;
        private const int BranchSides = 4;
        private const int MaxBranchesForMesh = 16;

        // 雲（葉パッド）1枚 = 上下ファン。中心2点（上/下）+ リム8点を上下で複製（陰影を面ごとに変えるため）。
        private const int PadSegments = 8;
        private const int PadVertCount = 2 + PadSegments * 2; // 上下中心2 + 上リム8 + 下リム8 = 18
        private const int PadTriCount = PadSegments * 2;      // 上ファン8 + 下ファン8 = 16

        private const float TrunkBend = 2.1f; // 模様木の曲げ強度

        // Build() 完了後に有効。ローカル座標系での各枝の先端位置（雲の中心あたり）。
        // 枝の Use 選択など、他スクリプトから参照される公開フィールド。
        [HideInInspector] public Vector3[] branchTipPositions = new Vector3[MaxBranchesForMesh];
        // 実際にメッシュを生成した枝の本数（0..MaxBranchesForMesh）。
        [HideInInspector] public int builtBranchCount;

        // 色は sRGB 16進を土台モデルと同じ変換式でリニア化してから使う（Build() 内で計算してフィールドへ格納する）。
        // UdonSharp はフィールド初期化子からのメソッド呼び出しを避けたいので、値は Build() の冒頭で埋める。
        private Color _trunkColorRoot;
        private Color _trunkColorTip;
        private Color _branchColorYoung;
        private Color _branchColorOld;
        private Color _leafColorNew;
        private Color _leafColorOld;
        private Color _crownColorLow;
        private Color _crownColorHigh;

        // 土台モデル（Blender）の LIGHT_DIR = (0.35, -0.30, 0.89) を Z-up→Y-up 変換した値。
        private Vector3 _lightDir;

        // 幹の形状パラメータ。枝の生え際計算でも参照するので Build() 実行中はフィールドに保持する。
        private float _trunkHeight;
        private float _trunkBaseRadius;
        private float _trunkTipRadius;
        private float _trunkBendPhase;

        // 頂点ループ内で Mathf.Sin / Mathf.Cos を呼ばないよう、リング辺数ぶんだけ事前計算しておくテーブル。
        private float[] _trunkCos = new float[TrunkSides];
        private float[] _trunkSin = new float[TrunkSides];
        private float[] _branchCos = new float[BranchSides];
        private float[] _branchSin = new float[BranchSides];
        private float[] _padCos = new float[PadSegments];
        private float[] _padSin = new float[PadSegments];

        // Build() 実行中だけ使う一時バッファ。
        private Vector3[] _vertices;
        private Vector3[] _normals;
        private Color[] _colors;
        private Vector2[] _uv;
        private int[] _triangles;
        private int _vertCursor;
        private int _triCursor;
        private int _padCount;
        private Vector3 _boundsMin;
        private Vector3 _boundsMax;

        /// <summary>
        /// パース済みデータから盆栽メッシュを生成し、自身の MeshFilter に割り当てる。
        /// </summary>
        public void Build(BonsaiJsonParser data)
        {
            if (data == null)
            {
                Debug.LogWarning("[Bonsai] mesh build skipped: parser is null");
                return;
            }

            float startTime = Time.realtimeSinceStartup;

            int branchCount = data.branchCount;
            if (branchCount > MaxBranchesForMesh)
                branchCount = MaxBranchesForMesh;
            if (branchCount < 0)
                branchCount = 0;

            int trunkVertCount = TrunkRings * TrunkSides + 1;
            int trunkTriCount = (TrunkRings - 1) * TrunkSides * 2 + TrunkSides;
            int branchVertCount = BranchRings * BranchSides + 1;
            int branchTriCount = (BranchRings - 1) * BranchSides * 2 + BranchSides;

            // 雲（葉パッド）は age による間引きをせず、全ての枝の先端 + 幹頂に1枚ずつ乗せる。
            int padCount = branchCount + 1;

            int totalVerts = trunkVertCount + branchCount * branchVertCount + padCount * PadVertCount;
            int totalTris = trunkTriCount + branchCount * branchTriCount + padCount * PadTriCount;

            _vertices = new Vector3[totalVerts];
            _normals = new Vector3[totalVerts];
            _colors = new Color[totalVerts];
            _uv = new Vector2[totalVerts];
            _triangles = new int[totalTris * 3];
            _vertCursor = 0;
            _triCursor = 0;
            _padCount = 0;

            for (int j = 0; j < TrunkSides; j++)
            {
                float theta = j * Mathf.PI * 2f / TrunkSides;
                _trunkCos[j] = Mathf.Cos(theta);
                _trunkSin[j] = Mathf.Sin(theta);
            }
            for (int j = 0; j < BranchSides; j++)
            {
                float theta = j * Mathf.PI * 2f / BranchSides;
                _branchCos[j] = Mathf.Cos(theta);
                _branchSin[j] = Mathf.Sin(theta);
            }
            for (int j = 0; j < PadSegments; j++)
            {
                float theta = j * Mathf.PI * 2f / PadSegments;
                _padCos[j] = Mathf.Cos(theta);
                _padSin[j] = Mathf.Sin(theta);
            }

            // 盆栽らしい低めのプロポーションにする（現行より高さを抑える）。
            _trunkHeight = (0.6f + data.trunkLen * 0.9f) * 0.9f;
            _trunkBaseRadius = 0.038f + 0.018f * Mathf.Log10(1f + data.trunkCommits);
            _trunkTipRadius = _trunkBaseRadius * 0.24f;
            // 曲がりは乱数を使わず commits を種にした決定的な位相にする。
            _trunkBendPhase = (data.trunkCommits % 360) * Mathf.Deg2Rad;

            // sRGB → リニア変換した基準色（土台モデルの Blender スクリプトと同じ srgb() の式）。
            _trunkColorRoot = HexToLinearColor(51f, 40f, 31f);     // 0x33281F
            _trunkColorTip = HexToLinearColor(87f, 70f, 54f);      // 0x574636
            _branchColorYoung = HexToLinearColor(69f, 55f, 41f);   // 0x453729
            _branchColorOld = HexToLinearColor(107f, 97f, 81f);    // 0x6B6151
            _leafColorNew = HexToLinearColor(55f, 107f, 38f);      // 0x376B26
            _leafColorOld = HexToLinearColor(31f, 74f, 28f);       // 0x1F4A1C
            _crownColorLow = HexToLinearColor(71f, 126f, 46f);     // 0x477E2E
            _crownColorHigh = HexToLinearColor(36f, 82f, 29f);     // 0x24521D

            // 土台モデル（Blender, Z-up）の LIGHT_DIR = (0.35, -0.30, 0.89) を Y-up に変換した値。
            _lightDir = new Vector3(0.35f, 0.89f, 0.30f).normalized;

            _boundsMin = new Vector3(-_trunkBaseRadius, 0f, -_trunkBaseRadius);
            _boundsMax = new Vector3(_trunkBaseRadius, _trunkHeight, _trunkBaseRadius);

            BuildTrunk();

            for (int b = 0; b < branchCount; b++)
                BuildBranch(data, b);
            builtBranchCount = branchCount;

            BuildCrown(data);

            Mesh mesh = new Mesh();
            mesh.vertices = _vertices;
            mesh.triangles = _triangles;
            mesh.normals = _normals;
            mesh.uv = _uv;
            mesh.colors = _colors;

            Vector3 center = (_boundsMin + _boundsMax) * 0.5f;
            Vector3 size = _boundsMax - _boundsMin;
            mesh.bounds = new Bounds(center, size);

            MeshFilter filter = GetComponent<MeshFilter>();
            if (filter != null)
                filter.sharedMesh = mesh;

            int builtVerts = _vertCursor;
            int builtTris = _triCursor / 3;
            float elapsedMs = (Time.realtimeSinceStartup - startTime) * 1000f;

            // 一時バッファは解放して常駐メモリを節約する。
            _vertices = null;
            _normals = null;
            _colors = null;
            _uv = null;
            _triangles = null;

            Debug.Log("[Bonsai] mesh built verts=" + builtVerts + " tris=" + builtTris + " ms=" + elapsedMs.ToString("F2") + " pads=" + _padCount);
        }

        // sRGB (0-255) → シーンリニアの Color に変換する。土台モデルの Blender スクリプトの srgb() と同じ式。
        private Color HexToLinearColor(float r255, float g255, float b255)
        {
            return new Color(SrgbToLinear(r255 / 255f), SrgbToLinear(g255 / 255f), SrgbToLinear(b255 / 255f), 1f);
        }

        private float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        // シェーダーが Unlit で法線を使わないため、簡易ランバートをあらかじめ頂点カラーに焼き込む。
        private Color ShadeVertex(Color linearBaseColor, Vector3 normal)
        {
            float k = 0.48f + 0.52f * Mathf.Max(Vector3.Dot(normal, _lightDir), 0f);
            return new Color(linearBaseColor.r * k, linearBaseColor.g * k, linearBaseColor.b * k, 1f);
        }

        // 頂点1つを配列に書き込み、カーソルを進める共通処理。トランク/枝/雲すべてでこの手順を踏むため、
        // ここに集約することでインデックスのズレ（頂点数と実書き込み数の不一致）を起きにくくする。
        private void WriteVertex(Vector3 pos, Vector3 normal, Color baseColor)
        {
            _vertices[_vertCursor] = pos;
            _normals[_vertCursor] = normal;
            _colors[_vertCursor] = ShadeVertex(baseColor, normal);
            _uv[_vertCursor] = Vector2.zero;
            ExpandBounds(pos);
            _vertCursor++;
        }

        // 幹の中心線（曲がりオフセット込み）。t は根本 0 〜 先端 1。
        private Vector3 TrunkCenterAt(float t)
        {
            float y = t * _trunkHeight;
            float x = Mathf.Sin(t * Mathf.PI * 1.5f + _trunkBendPhase) * 0.10f * TrunkBend * t;
            float z = Mathf.Cos(t * Mathf.PI * 1.1f + _trunkBendPhase * 0.7f) * 0.06f * TrunkBend * t;
            return new Vector3(x, y, z);
        }

        // 根元付近を太めに残すため、t^0.75 で補間係数を進める（Pow<1 は先端寄りの区間で急に細くなる）。
        private float TrunkRadiusAt(float t)
        {
            return Mathf.Lerp(_trunkBaseRadius, _trunkTipRadius, Mathf.Pow(t, 0.75f));
        }

        private void BuildTrunk()
        {
            int ringStart = _vertCursor;

            for (int i = 0; i < TrunkRings; i++)
            {
                float t = (float)i / (TrunkRings - 1);
                Vector3 center = TrunkCenterAt(t);
                float radius = TrunkRadiusAt(t);
                if (i == 0)
                    radius *= 1.5f; // 最下リングだけ太くして根張りを表現する

                Color baseColor = Color.Lerp(_trunkColorRoot, _trunkColorTip, t * 0.7f);

                for (int j = 0; j < TrunkSides; j++)
                {
                    Vector3 normal = new Vector3(_trunkCos[j], 0f, _trunkSin[j]);
                    Vector3 pos = center + normal * radius;
                    WriteVertex(pos, normal, baseColor);
                }
            }

            for (int i = 0; i < TrunkRings - 1; i++)
            {
                int bottomBase = ringStart + i * TrunkSides;
                int topBase = ringStart + (i + 1) * TrunkSides;
                for (int j = 0; j < TrunkSides; j++)
                {
                    int jn = (j + 1) % TrunkSides;
                    int b0 = bottomBase + j;
                    int b1 = bottomBase + jn;
                    int t0 = topBase + j;
                    int t1 = topBase + jn;

                    _triangles[_triCursor++] = b0;
                    _triangles[_triCursor++] = t0;
                    _triangles[_triCursor++] = b1;

                    _triangles[_triCursor++] = b1;
                    _triangles[_triCursor++] = t0;
                    _triangles[_triCursor++] = t1;
                }
            }

            int tipIndex = _vertCursor;
            Vector3 tipPos = TrunkCenterAt(1f);
            Color tipColor = Color.Lerp(_trunkColorRoot, _trunkColorTip, 0.7f);
            WriteVertex(tipPos, Vector3.up, tipColor);

            int lastRingBase = ringStart + (TrunkRings - 1) * TrunkSides;
            for (int j = 0; j < TrunkSides; j++)
            {
                int jn = (j + 1) % TrunkSides;
                _triangles[_triCursor++] = lastRingBase + j;
                _triangles[_triCursor++] = tipIndex;
                _triangles[_triCursor++] = lastRingBase + jn;
            }
        }

        private void BuildBranch(BonsaiJsonParser data, int branchIndex)
        {
            float h = Mathf.Clamp01(data.branchH[branchIndex]);
            float lenNorm = data.branchLen[branchIndex];
            float age = Mathf.Clamp01(data.branchAge[branchIndex]);
            int seedDeg = data.branchSeed[branchIndex];

            float length = 0.17f + lenNorm * 0.32f;
            float azimuthRad = seedDeg * Mathf.Deg2Rad;
            // 盆栽の段葉らしく、ほぼ水平に張り出させる（年数が経つほどさらに水平寄りになる）。
            float elevationRad = (10f - age * 8f) * Mathf.Deg2Rad;

            Vector3 horizontal = new Vector3(Mathf.Sin(azimuthRad), 0f, Mathf.Cos(azimuthRad));
            Vector3 growthAxis = (horizontal * Mathf.Cos(elevationRad) + Vector3.up * Mathf.Sin(elevationRad)).normalized;

            // growthAxis に垂直な (radial0, tangentAxis) を作る。growthAxis がほぼ真上を向く場合は
            // Vector3.up を基準に取れないので Vector3.forward にフォールバックする。
            Vector3 helper = Mathf.Abs(Vector3.Dot(growthAxis, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            Vector3 radial0 = Vector3.Normalize(Vector3.Cross(helper, growthAxis));
            Vector3 tangentAxis = Vector3.Cross(radial0, growthAxis);

            Vector3 origin = TrunkCenterAt(h);
            float r0 = Mathf.Lerp(_trunkBaseRadius, _trunkTipRadius, h) * 0.5f;

            Color branchColor = Color.Lerp(_branchColorYoung, _branchColorOld, age);

            int ringStart = _vertCursor;

            for (int i = 0; i < BranchRings; i++)
            {
                float t = (float)i / (BranchRings - 1);
                // 先端ほど持ち上がるように、直線の growthAxis 移動に上向きの二次カーブを足す。
                Vector3 center = origin + growthAxis * (length * t) + Vector3.up * (0.30f * t * t * length);

                float radius;
                if (i == 0) radius = r0;
                else if (i == 1) radius = r0 * 0.7f;
                else if (i == 2) radius = r0 * 0.45f;
                else radius = r0 * 0.25f;

                for (int j = 0; j < BranchSides; j++)
                {
                    Vector3 normal = radial0 * _branchCos[j] + tangentAxis * _branchSin[j];
                    Vector3 pos = center + normal * radius;
                    WriteVertex(pos, normal, branchColor);
                }
            }

            for (int i = 0; i < BranchRings - 1; i++)
            {
                int bottomBase = ringStart + i * BranchSides;
                int topBase = ringStart + (i + 1) * BranchSides;
                for (int j = 0; j < BranchSides; j++)
                {
                    int jn = (j + 1) % BranchSides;
                    int b0 = bottomBase + j;
                    int b1 = bottomBase + jn;
                    int t0 = topBase + j;
                    int t1 = topBase + jn;

                    _triangles[_triCursor++] = b0;
                    _triangles[_triCursor++] = t0;
                    _triangles[_triCursor++] = b1;

                    _triangles[_triCursor++] = b1;
                    _triangles[_triCursor++] = t0;
                    _triangles[_triCursor++] = t1;
                }
            }

            int tipIndex = _vertCursor;
            Vector3 tipPos = origin + growthAxis * length + Vector3.up * (0.30f * length);
            WriteVertex(tipPos, growthAxis, branchColor);

            int lastRingBase = ringStart + (BranchRings - 1) * BranchSides;
            for (int j = 0; j < BranchSides; j++)
            {
                int jn = (j + 1) % BranchSides;
                _triangles[_triCursor++] = lastRingBase + j;
                _triangles[_triCursor++] = tipIndex;
                _triangles[_triCursor++] = lastRingBase + jn;
            }

            // 枝先の少し上に雲（段葉）を1枚載せる。盆栽は全ての枝に葉が付いているのが自然なので、
            // 現行のような age による間引きはしない。
            Vector3 padCenter = tipPos + Vector3.up * 0.018f;
            branchTipPositions[branchIndex] = padCenter;

            float rx = Mathf.Lerp(0.15f, 0.24f, lenNorm);
            float rz = rx * 0.85f;
            Color leafColor = Color.Lerp(_leafColorNew, _leafColorOld, age);
            AddCloudPad(padCenter, rx, rz, 0.034f, seedDeg, leafColor);
        }

        // 幹頂部にも大きめの雲を1枚だけ載せる。trunkRecent30 が多いほど色が濃い緑になる。
        private void BuildCrown(BonsaiJsonParser data)
        {
            Vector3 tipPos = TrunkCenterAt(1f);
            float recentT = Mathf.Clamp01(data.trunkRecent30 / 20f);
            Color crownColor = Color.Lerp(_crownColorLow, _crownColorHigh, recentT);
            // 乱数を使わず commits を種にした決定的な輪郭ゆらぎにする。
            int seedDeg = data.trunkCommits % 360;
            AddCloudPad(tipPos, 0.24f, 0.20f, 0.040f, seedDeg, crownColor);
        }

        // 「雲」= 段葉パッド。扁平なレンズ状ディスクを、中心の上下頂点＋周囲8頂点のファンで構成する。
        // 上面ファンと下面ファンでリム頂点を複製しているのは、面ごとに法線・陰影（下面はさらに暗い）を
        // 変えたいため。輪郭は乱数ではなく seedDeg の三角関数でわずかに揺らす。
        private void AddCloudPad(Vector3 center, float rx, float rz, float thick, int seedDeg, Color baseColor)
        {
            Color bottomBaseColor = new Color(baseColor.r * 0.5f, baseColor.g * 0.5f, baseColor.b * 0.5f, 1f);

            int topCenterIndex = _vertCursor;
            WriteVertex(center + Vector3.up * thick, Vector3.up, baseColor);

            int bottomCenterIndex = _vertCursor;
            WriteVertex(center - Vector3.up * (thick * 0.8f), Vector3.down, bottomBaseColor);

            int topRimBase = _vertCursor;
            for (int j = 0; j < PadSegments; j++)
            {
                float wob = 1f + 0.16f * Mathf.Sin(seedDeg * 0.7f + j * 2.3f);
                Vector3 rimPos = center + new Vector3(rx * wob * _padCos[j], 0f, rz * wob * _padSin[j]);
                // 厳密な面法線ではなく、レンズ状の断面を近似した外向き+やや上向きの簡易法線。
                Vector3 rimNormal = new Vector3(_padCos[j], 0.5f, _padSin[j]).normalized;
                WriteVertex(rimPos, rimNormal, baseColor);
            }

            int bottomRimBase = _vertCursor;
            for (int j = 0; j < PadSegments; j++)
            {
                float wob = 1f + 0.16f * Mathf.Sin(seedDeg * 0.7f + j * 2.3f);
                Vector3 rimPos = center + new Vector3(rx * wob * _padCos[j], 0f, rz * wob * _padSin[j]);
                Vector3 rimNormal = new Vector3(_padCos[j], -0.5f, _padSin[j]).normalized;
                WriteVertex(rimPos, rimNormal, bottomBaseColor);
            }

            for (int j = 0; j < PadSegments; j++)
            {
                int jn = (j + 1) % PadSegments;

                _triangles[_triCursor++] = topRimBase + j;
                _triangles[_triCursor++] = topCenterIndex;
                _triangles[_triCursor++] = topRimBase + jn;

                _triangles[_triCursor++] = bottomRimBase + jn;
                _triangles[_triCursor++] = bottomCenterIndex;
                _triangles[_triCursor++] = bottomRimBase + j;
            }

            _padCount++;
        }

        private void ExpandBounds(Vector3 p)
        {
            _boundsMin = new Vector3(Mathf.Min(_boundsMin.x, p.x), Mathf.Min(_boundsMin.y, p.y), Mathf.Min(_boundsMin.z, p.z));
            _boundsMax = new Vector3(Mathf.Max(_boundsMax.x, p.x), Mathf.Max(_boundsMax.y, p.y), Mathf.Max(_boundsMax.z, p.z));
        }
    }
}
