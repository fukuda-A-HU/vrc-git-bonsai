using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;

namespace BonsaiGit
{
    /// <summary>
    /// bonsai.json をパースして、幹・枝の数値を固定長配列に展開する。
    /// VRCJson の数値は常に Double で返るため、必ず TokenType を指定して受け取り float/int にキャストする。
    /// スキーマ v2 で追加された name/head（ブランチ名・最新コミット情報）にも対応するが、
    /// v1 の JSON（これらのキーが無い）でもパースを失敗させず、空文字/0で埋めて続行する。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BonsaiJsonParser : UdonSharpBehaviour
    {
        // 配信側で16本にcapされている想定だが、Udon側でも防御的に上限を切る。
        public const int MaxBranches = 16;

        [HideInInspector] public int trunkCommits;
        [HideInInspector] public int trunkRecent30;
        [HideInInspector] public float trunkLen;
        [HideInInspector] public string trunkName;
        [HideInInspector] public string trunkHeadSha;
        [HideInInspector] public string trunkHeadMsg;
        [HideInInspector] public string trunkHeadAuthor;
        [HideInInspector] public int trunkHeadAt;
        [HideInInspector] public int genAt; // ルートの "gen"（JSON生成時刻、unix秒）

        // Parse() が公開フィールドへの反映まで完了したかどうか。
        // 後から join したプレイヤーは、自分のパースが終わる前に他プレイヤーの選択状態（同期）を
        // 受信し得るため、BonsaiController 側でこのフラグを見てから木札を更新する。
        [HideInInspector] public bool parsed;

        [HideInInspector] public int branchCount;
        [HideInInspector] public float[] branchH = new float[MaxBranches];
        [HideInInspector] public float[] branchLen = new float[MaxBranches];
        [HideInInspector] public float[] branchAge = new float[MaxBranches];
        [HideInInspector] public int[] branchAhead = new int[MaxBranches];
        [HideInInspector] public int[] branchBehind = new int[MaxBranches];
        [HideInInspector] public int[] branchSeed = new int[MaxBranches];
        [HideInInspector] public string[] branchName = new string[MaxBranches];
        [HideInInspector] public string[] branchHeadSha = new string[MaxBranches];
        [HideInInspector] public string[] branchHeadMsg = new string[MaxBranches];
        [HideInInspector] public string[] branchHeadAuthor = new string[MaxBranches];
        [HideInInspector] public int[] branchHeadAt = new int[MaxBranches];

        /// <summary>
        /// JSON文字列をパースする。成功時 true、失敗時は状態を初期化して false を返す。
        /// </summary>
        public bool Parse(string json)
        {
            parsed = false;
            trunkCommits = 0;
            trunkRecent30 = 0;
            trunkLen = 0f;
            trunkName = "";
            trunkHeadSha = "";
            trunkHeadMsg = "";
            trunkHeadAuthor = "";
            trunkHeadAt = 0;
            genAt = 0;
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

            // "gen" は v1/v2 とも必須だが、無くても致命的ではないので任意扱いにする。
            int newGenAt = GetOptionalInt(root, "gen");

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

            // v2 で追加されたフィールド。v1 の JSON には存在しないため、無くても失敗させない。
            string newTrunkName = GetOptionalString(trunk, "name");
            string newTrunkHeadSha = "";
            string newTrunkHeadMsg = "";
            string newTrunkHeadAuthor = "";
            int newTrunkHeadAt = 0;
            DataToken trunkHeadToken;
            if (trunk.TryGetValue("head", TokenType.DataDictionary, out trunkHeadToken))
            {
                DataDictionary trunkHead = trunkHeadToken.DataDictionary;
                newTrunkHeadSha = GetOptionalString(trunkHead, "sha");
                newTrunkHeadMsg = GetOptionalString(trunkHead, "msg");
                newTrunkHeadAuthor = GetOptionalString(trunkHead, "author");
                newTrunkHeadAt = GetOptionalInt(trunkHead, "at");
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
            string[] newName = new string[MaxBranches];
            string[] newHeadSha = new string[MaxBranches];
            string[] newHeadMsg = new string[MaxBranches];
            string[] newHeadAuthor = new string[MaxBranches];
            int[] newHeadAt = new int[MaxBranches];

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

                // v2 の name/head。v1 では欠けているので、無ければ空文字/0のままにする。
                newName[i] = GetOptionalString(entry, "name");
                DataToken headToken;
                if (entry.TryGetValue("head", TokenType.DataDictionary, out headToken))
                {
                    DataDictionary head = headToken.DataDictionary;
                    newHeadSha[i] = GetOptionalString(head, "sha");
                    newHeadMsg[i] = GetOptionalString(head, "msg");
                    newHeadAuthor[i] = GetOptionalString(head, "author");
                    newHeadAt[i] = GetOptionalInt(head, "at");
                }
                else
                {
                    newHeadSha[i] = "";
                    newHeadMsg[i] = "";
                    newHeadAuthor[i] = "";
                    newHeadAt[i] = 0;
                }
            }

            // ここまで全チェックを通過して初めて公開フィールドへ反映する（部分適用を避ける）。
            trunkCommits = (int)commitsToken.Double;
            trunkRecent30 = (int)recent30Token.Double;
            trunkLen = (float)trunkLenToken.Double;
            trunkName = newTrunkName;
            trunkHeadSha = newTrunkHeadSha;
            trunkHeadMsg = newTrunkHeadMsg;
            trunkHeadAuthor = newTrunkHeadAuthor;
            trunkHeadAt = newTrunkHeadAt;
            genAt = newGenAt;
            branchH = newH;
            branchLen = newLen;
            branchAge = newAge;
            branchAhead = newAhead;
            branchBehind = newBehind;
            branchSeed = newSeed;
            branchName = newName;
            branchHeadSha = newHeadSha;
            branchHeadMsg = newHeadMsg;
            branchHeadAuthor = newHeadAuthor;
            branchHeadAt = newHeadAt;
            branchCount = count;

            // 公開フィールドへの反映が完了した後で初めて true にする。
            parsed = true;

            Debug.Log("[Bonsai] parsed branches=" + branchCount);
            return true;
        }

        // 型が違う/キーが無い場合は "" を返す（v1 JSON との後方互換のための任意フィールド用）。
        private string GetOptionalString(DataDictionary dict, string key)
        {
            DataToken tok;
            if (dict.TryGetValue(key, TokenType.String, out tok))
                return tok.String;
            return "";
        }

        // 型が違う/キーが無い場合は 0 を返す（v1 JSON との後方互換のための任意フィールド用）。
        private int GetOptionalInt(DataDictionary dict, string key)
        {
            DataToken tok;
            if (dict.TryGetValue(key, TokenType.Double, out tok))
                return (int)tok.Double;
            return 0;
        }
    }
}
