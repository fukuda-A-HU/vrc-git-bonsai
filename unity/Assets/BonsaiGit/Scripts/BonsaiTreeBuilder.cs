using UdonSharp;
using UnityEngine;

namespace BonsaiGit
{
    /// <summary>
    /// BonsaiJsonParser がパースした幹・枝の数値から、円柱リング方式でプロシージャルメッシュを組み立てる。
    /// 幹: 10リング×6辺（60頂点+先端キャップ1点）。枝: 1本あたり5リング×4辺（20頂点+先端キャップ1点）。
    /// 頂点・法線・色は配列に貯めて最後に一括で Mesh に代入する（SetVertices 等は使わない）。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BonsaiTreeBuilder : UdonSharpBehaviour
    {
        private const int TrunkRings = 10;
        private const int TrunkSides = 6;
        private const int BranchRings = 5;
        private const int BranchSides = 4;
        private const int MaxBranchesForMesh = 16;
        private const float LeafAgeThreshold = 0.5f;
        private const int LeafQuadVertCount = 4;   // クワッド1枚あたりの頂点数
        private const int LeafQuadTriCount = 2;    // クワッド1枚あたりの三角形数
        private const int LeafClusterQuadCount = 2; // 交差クワッド1房あたりの枚数

        // UdonSharp は static フィールドを未サポートのためインスタンスフィールドにする。
        private Color TrunkColor = new Color(0.42f, 0.28f, 0.16f, 1f);
        private Color BranchColorYoung = new Color(0.35f, 0.5f, 0.2f, 1f);
        private Color BranchColorOld = new Color(0.5f, 0.5f, 0.45f, 1f);
        private Color LeafColorNew = new Color(0.30f, 0.62f, 0.22f, 1f);
        private Color LeafColorOld = new Color(0.62f, 0.60f, 0.28f, 1f);
        private Color CrownColorLow = new Color(0.52f, 0.58f, 0.28f, 1f);
        private Color CrownColorHigh = new Color(0.15f, 0.42f, 0.12f, 1f);

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

        // Build() 実行中だけ使う一時バッファ。枝ごとにメソッドを分けているので、
        // 将来フレーム分割（1本ずつ生成）したくなった場合もこのフィールドを持ち越すだけでよい。
        private Vector3[] _vertices;
        private Vector3[] _normals;
        private Color[] _colors;
        private Vector2[] _uv;
        private int[] _triangles;
        private int _vertCursor;
        private int _triCursor;
        private int _leafQuadCount;
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

            // 葉クワッドの房数を先に数えておく（頂点配列を一括確保するため）。
            // 房1つ = 交差クワッド2枚 = 8頂点・4三角形。
            int leafBranchCount = 0;
            for (int b = 0; b < branchCount; b++)
            {
                if (data.branchAge[b] < LeafAgeThreshold)
                    leafBranchCount++;
            }
            int crownClusterCount = Mathf.Clamp(Mathf.RoundToInt(data.trunkRecent30 / 3f), 1, 5);
            int leafClusterCount = leafBranchCount + crownClusterCount;

            int clusterVertCount = LeafClusterQuadCount * LeafQuadVertCount;
            int clusterTriCount = LeafClusterQuadCount * LeafQuadTriCount;

            int totalVerts = trunkVertCount + branchCount * branchVertCount + leafClusterCount * clusterVertCount;
            int totalTris = trunkTriCount + branchCount * branchTriCount + leafClusterCount * clusterTriCount;

            _vertices = new Vector3[totalVerts];
            _normals = new Vector3[totalVerts];
            _colors = new Color[totalVerts];
            _uv = new Vector2[totalVerts];
            _triangles = new int[totalTris * 3];
            _vertCursor = 0;
            _triCursor = 0;
            _leafQuadCount = 0;

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

            _trunkHeight = 0.6f + data.trunkLen * 0.9f;
            _trunkBaseRadius = 0.04f + 0.02f * Mathf.Log10(1f + data.trunkCommits);
            _trunkTipRadius = _trunkBaseRadius * 0.25f;
            // 曲がりは乱数を使わず commits を種にした決定的な位相にする。
            _trunkBendPhase = (data.trunkCommits % 360) * Mathf.Deg2Rad;

            _boundsMin = new Vector3(-_trunkBaseRadius, 0f, -_trunkBaseRadius);
            _boundsMax = new Vector3(_trunkBaseRadius, _trunkHeight, _trunkBaseRadius);

            BuildTrunk();

            for (int b = 0; b < branchCount; b++)
                BuildBranch(data, b);

            BuildCrown(data, crownClusterCount);

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

            Debug.Log("[Bonsai] mesh built verts=" + builtVerts + " tris=" + builtTris + " ms=" + elapsedMs.ToString("F2") + " leaves=" + _leafQuadCount);
        }

        // 幹の中心線（曲がりオフセット込み）。t は根本 0 〜 先端 1。
        private Vector3 TrunkCenterAt(float t)
        {
            float y = t * _trunkHeight;
            float x = Mathf.Sin(t * Mathf.PI * 1.5f + _trunkBendPhase) * 0.10f * t;
            float z = Mathf.Cos(t * Mathf.PI * 1.1f + _trunkBendPhase * 0.7f) * 0.06f * t;
            return new Vector3(x, y, z);
        }

        private float TrunkRadiusAt(float t)
        {
            return Mathf.Lerp(_trunkBaseRadius, _trunkTipRadius, t);
        }

        private void BuildTrunk()
        {
            int ringStart = _vertCursor;

            for (int i = 0; i < TrunkRings; i++)
            {
                float t = (float)i / (TrunkRings - 1);
                Vector3 center = TrunkCenterAt(t);
                float radius = TrunkRadiusAt(t);

                for (int j = 0; j < TrunkSides; j++)
                {
                    Vector3 normal = new Vector3(_trunkCos[j], 0f, _trunkSin[j]);
                    Vector3 pos = center + normal * radius;

                    _vertices[_vertCursor] = pos;
                    _normals[_vertCursor] = normal;
                    _colors[_vertCursor] = TrunkColor;
                    _uv[_vertCursor] = Vector2.zero;
                    ExpandBounds(pos);
                    _vertCursor++;
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
            _vertices[tipIndex] = tipPos;
            _normals[tipIndex] = Vector3.up;
            _colors[tipIndex] = TrunkColor;
            _uv[tipIndex] = Vector2.zero;
            ExpandBounds(tipPos);
            _vertCursor++;

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

            float length = 0.15f + lenNorm * 0.45f;
            float azimuthRad = seedDeg * Mathf.Deg2Rad;
            float elevationRad = (35f - age * 25f) * Mathf.Deg2Rad;

            Vector3 horizontal = new Vector3(Mathf.Sin(azimuthRad), 0f, Mathf.Cos(azimuthRad));
            Vector3 growthAxis = (horizontal * Mathf.Cos(elevationRad) + Vector3.up * Mathf.Sin(elevationRad)).normalized;

            // growthAxis に垂直な (radial0, tangentAxis) を作る。growthAxis がほぼ真上を向く場合は
            // Vector3.up を基準に取れないので Vector3.forward にフォールバックする。
            Vector3 helper = Mathf.Abs(Vector3.Dot(growthAxis, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            Vector3 radial0 = Vector3.Normalize(Vector3.Cross(helper, growthAxis));
            Vector3 tangentAxis = Vector3.Cross(radial0, growthAxis);

            Vector3 origin = TrunkCenterAt(h);
            float baseRadius = TrunkRadiusAt(h) * 0.5f;
            float tipRadius = baseRadius * 0.25f;

            Color branchColor = Color.Lerp(BranchColorYoung, BranchColorOld, age);

            int ringStart = _vertCursor;

            for (int i = 0; i < BranchRings; i++)
            {
                float t = (float)i / (BranchRings - 1);
                Vector3 center = origin + growthAxis * (length * t);
                float radius = Mathf.Lerp(baseRadius, tipRadius, t);

                for (int j = 0; j < BranchSides; j++)
                {
                    Vector3 normal = radial0 * _branchCos[j] + tangentAxis * _branchSin[j];
                    Vector3 pos = center + normal * radius;

                    _vertices[_vertCursor] = pos;
                    _normals[_vertCursor] = normal;
                    _colors[_vertCursor] = branchColor;
                    _uv[_vertCursor] = Vector2.zero;
                    ExpandBounds(pos);
                    _vertCursor++;
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
            Vector3 tipPos = origin + growthAxis * length;
            _vertices[tipIndex] = tipPos;
            _normals[tipIndex] = growthAxis;
            _colors[tipIndex] = branchColor;
            _uv[tipIndex] = Vector2.zero;
            ExpandBounds(tipPos);
            _vertCursor++;

            int lastRingBase = ringStart + (BranchRings - 1) * BranchSides;
            for (int j = 0; j < BranchSides; j++)
            {
                int jn = (j + 1) % BranchSides;
                _triangles[_triCursor++] = lastRingBase + j;
                _triangles[_triCursor++] = tipIndex;
                _triangles[_triCursor++] = lastRingBase + jn;
            }

            // 若い枝（age<0.5）の先端に交差クワッドの葉房を1つ追加する。
            if (age < LeafAgeThreshold)
            {
                float leafSize = Mathf.Lerp(0.05f, 0.09f, lenNorm);
                Color leafColor = Color.Lerp(LeafColorNew, LeafColorOld, Mathf.Clamp01(age / LeafAgeThreshold));
                AddLeafCluster(tipPos, growthAxis, leafSize, seedDeg, leafColor);
            }
        }

        // 幹頂部の樹冠。trunkRecent30 が多いほど房数が増え、色も濃い緑になる。
        private void BuildCrown(BonsaiJsonParser data, int clusterCount)
        {
            Vector3 tipPos = TrunkCenterAt(1f);
            float recentT = Mathf.Clamp01(data.trunkRecent30 / 20f);
            Color crownColor = Color.Lerp(CrownColorLow, CrownColorHigh, recentT);
            float size = Mathf.Lerp(0.06f, 0.09f, recentT);

            for (int i = 0; i < clusterCount; i++)
            {
                // 乱数を使わず commits と房番号から決定的に配置角度をずらす。
                int angleDeg = (data.trunkCommits * 3 + i * 73) % 360;
                float radius = _trunkTipRadius * 1.2f + i * 0.012f;
                float rad = angleDeg * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(rad) * radius, i * 0.02f, Mathf.Sin(rad) * radius);
                Vector3 center = tipPos + offset;
                AddLeafCluster(center, Vector3.up, size, angleDeg + i * 17, crownColor);
            }
        }

        // attachPoint を起点に、axis 方向へ伸びる交差クワッド（2枚・8頂点・4三角形）の葉房を1つ追加する。
        // 向きは axis 周りに seedDeg から決定的に回転させ、房ごとに傾きをずらす。
        private void AddLeafCluster(Vector3 attachPoint, Vector3 axis, float size, int seedDeg, Color color)
        {
            Vector3 helper = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            Vector3 side0 = Vector3.Normalize(Vector3.Cross(helper, axis));

            // side0 を axis 周りに seedDeg 回転させる（Rodrigues の回転公式。side0 は axis に垂直なので簡略化できる）。
            float rollRad = (seedDeg % 360) * Mathf.Deg2Rad;
            Vector3 rolled = side0 * Mathf.Cos(rollRad) + Vector3.Cross(axis, side0) * Mathf.Sin(rollRad);
            Vector3 side1 = Vector3.Cross(axis, rolled);

            Vector3 center = attachPoint - axis * (size * 0.25f);
            AddLeafQuad(center, rolled, axis, size, color);
            AddLeafQuad(center, side1, axis, size, color);
        }

        // center から sideDir/axis 方向に広がる1枚のクワッド（4頂点・2三角形）を追加する。
        private void AddLeafQuad(Vector3 center, Vector3 sideDir, Vector3 axis, float size, Color color)
        {
            float half = size * 0.5f;
            Vector3 v0 = center - sideDir * half;
            Vector3 v1 = center + sideDir * half;
            Vector3 v2 = v1 + axis * size;
            Vector3 v3 = v0 + axis * size;
            Vector3 faceNormal = Vector3.Cross(sideDir, axis).normalized;

            int i0 = _vertCursor;
            _vertices[i0] = v0;
            _normals[i0] = faceNormal;
            _colors[i0] = color;
            _uv[i0] = Vector2.zero;
            ExpandBounds(v0);
            _vertCursor++;

            int i1 = _vertCursor;
            _vertices[i1] = v1;
            _normals[i1] = faceNormal;
            _colors[i1] = color;
            _uv[i1] = Vector2.zero;
            ExpandBounds(v1);
            _vertCursor++;

            int i2 = _vertCursor;
            _vertices[i2] = v2;
            _normals[i2] = faceNormal;
            _colors[i2] = color;
            _uv[i2] = Vector2.zero;
            ExpandBounds(v2);
            _vertCursor++;

            int i3 = _vertCursor;
            _vertices[i3] = v3;
            _normals[i3] = faceNormal;
            _colors[i3] = color;
            _uv[i3] = Vector2.zero;
            ExpandBounds(v3);
            _vertCursor++;

            _triangles[_triCursor++] = i0;
            _triangles[_triCursor++] = i2;
            _triangles[_triCursor++] = i1;

            _triangles[_triCursor++] = i0;
            _triangles[_triCursor++] = i3;
            _triangles[_triCursor++] = i2;

            _leafQuadCount++;
        }

        private void ExpandBounds(Vector3 p)
        {
            _boundsMin = new Vector3(Mathf.Min(_boundsMin.x, p.x), Mathf.Min(_boundsMin.y, p.y), Mathf.Min(_boundsMin.z, p.z));
            _boundsMax = new Vector3(Mathf.Max(_boundsMax.x, p.x), Mathf.Max(_boundsMax.y, p.y), Mathf.Max(_boundsMax.z, p.z));
        }
    }
}
