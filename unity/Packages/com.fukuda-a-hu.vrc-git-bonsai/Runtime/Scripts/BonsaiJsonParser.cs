using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;

namespace BonsaiGit
{
    /// <summary>
    /// bonsai.json (配信済みスキーマ v1) をパースして、幹・枝の数値を固定長配列に展開する。
    /// VRCJson の数値は常に Double で返るため、必ず TokenType を指定して受け取り float/int にキャストする。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BonsaiJsonParser : UdonSharpBehaviour
    {
        // 配信側で16本にcapされている想定だが、Udon側でも防御的に上限を切る。
        public const int MaxBranches = 16;

        [HideInInspector] public int trunkCommits;
        [HideInInspector] public int trunkRecent30;
        [HideInInspector] public float trunkLen;

        [HideInInspector] public int branchCount;
        [HideInInspector] public float[] branchH = new float[MaxBranches];
        [HideInInspector] public float[] branchLen = new float[MaxBranches];
        [HideInInspector] public float[] branchAge = new float[MaxBranches];
        [HideInInspector] public int[] branchAhead = new int[MaxBranches];
        [HideInInspector] public int[] branchBehind = new int[MaxBranches];
        [HideInInspector] public int[] branchSeed = new int[MaxBranches];

        /// <summary>
        /// JSON文字列をパースする。成功時 true、失敗時は状態を初期化して false を返す。
        /// </summary>
        public bool Parse(string json)
        {
            trunkCommits = 0;
            trunkRecent30 = 0;
            trunkLen = 0f;
            branchCount = 0;

            if (json == null || json.Length == 0)
            {
                Debug.LogWarning("[Bonsai] parse failed: empty json");
                return false;
            }

            DataToken rootToken;
            if (!VRCJson.TryDeserializeFromJson(json, out rootToken))
            {
                Debug.LogWarning("[Bonsai] parse failed: invalid json");
                return false;
            }

            if (rootToken.TokenType != TokenType.DataDictionary)
            {
                Debug.LogWarning("[Bonsai] parse failed: root is not an object");
                return false;
            }

            DataDictionary root = rootToken.DataDictionary;

            DataToken trunkToken;
            if (!root.TryGetValue("trunk", TokenType.DataDictionary, out trunkToken))
            {
                Debug.LogWarning("[Bonsai] parse failed: missing trunk");
                return false;
            }
            DataDictionary trunk = trunkToken.DataDictionary;

            DataToken commitsToken;
            if (!trunk.TryGetValue("commits", TokenType.Double, out commitsToken))
            {
                Debug.LogWarning("[Bonsai] parse failed: missing trunk.commits");
                return false;
            }

            DataToken recent30Token;
            if (!trunk.TryGetValue("recent30", TokenType.Double, out recent30Token))
            {
                Debug.LogWarning("[Bonsai] parse failed: missing trunk.recent30");
                return false;
            }

            DataToken trunkLenToken;
            if (!trunk.TryGetValue("len", TokenType.Double, out trunkLenToken))
            {
                Debug.LogWarning("[Bonsai] parse failed: missing trunk.len");
                return false;
            }

            DataToken branchesToken;
            if (!root.TryGetValue("branches", TokenType.DataList, out branchesToken))
            {
                Debug.LogWarning("[Bonsai] parse failed: missing branches");
                return false;
            }
            DataList branches = branchesToken.DataList;

            int count = branches.Count;
            if (count > MaxBranches)
                count = MaxBranches;

            float[] newH = new float[MaxBranches];
            float[] newLen = new float[MaxBranches];
            float[] newAge = new float[MaxBranches];
            int[] newAhead = new int[MaxBranches];
            int[] newBehind = new int[MaxBranches];
            int[] newSeed = new int[MaxBranches];

            for (int i = 0; i < count; i++)
            {
                DataToken entryToken;
                if (!branches.TryGetValue(i, TokenType.DataDictionary, out entryToken))
                {
                    Debug.LogWarning("[Bonsai] parse failed: branch entry is not an object");
                    return false;
                }
                DataDictionary entry = entryToken.DataDictionary;

                DataToken hTok;
                if (!entry.TryGetValue("h", TokenType.Double, out hTok))
                {
                    Debug.LogWarning("[Bonsai] parse failed: branch.h");
                    return false;
                }

                DataToken lenTok;
                if (!entry.TryGetValue("len", TokenType.Double, out lenTok))
                {
                    Debug.LogWarning("[Bonsai] parse failed: branch.len");
                    return false;
                }

                DataToken ageTok;
                if (!entry.TryGetValue("age", TokenType.Double, out ageTok))
                {
                    Debug.LogWarning("[Bonsai] parse failed: branch.age");
                    return false;
                }

                DataToken seedTok;
                if (!entry.TryGetValue("seed", TokenType.Double, out seedTok))
                {
                    Debug.LogWarning("[Bonsai] parse failed: branch.seed");
                    return false;
                }

                DataToken aheadTok;
                if (!entry.TryGetValue("ahead", TokenType.Double, out aheadTok))
                {
                    Debug.LogWarning("[Bonsai] parse failed: branch.ahead");
                    return false;
                }

                DataToken behindTok;
                if (!entry.TryGetValue("behind", TokenType.Double, out behindTok))
                {
                    Debug.LogWarning("[Bonsai] parse failed: branch.behind");
                    return false;
                }

                newH[i] = (float)hTok.Double;
                newLen[i] = (float)lenTok.Double;
                newAge[i] = (float)ageTok.Double;
                newAhead[i] = (int)aheadTok.Double;
                newBehind[i] = (int)behindTok.Double;
                newSeed[i] = (int)seedTok.Double;
            }

            // ここまで全チェックを通過して初めて公開フィールドへ反映する（部分適用を避ける）。
            trunkCommits = (int)commitsToken.Double;
            trunkRecent30 = (int)recent30Token.Double;
            trunkLen = (float)trunkLenToken.Double;
            branchH = newH;
            branchLen = newLen;
            branchAge = newAge;
            branchAhead = newAhead;
            branchBehind = newBehind;
            branchSeed = newSeed;
            branchCount = count;

            Debug.Log("[Bonsai] parsed branches=" + branchCount);
            return true;
        }
    }
}
