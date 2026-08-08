using UdonSharp;
using UnityEngine;

namespace BonsaiGit
{
    /// <summary>
    /// 木札の表示を担当する。選択中の枝（または未選択時は幹＝デフォルトブランチ）の
    /// ブランチ名・最新コミット情報・数値を、割り当てられた TextMeshPro に反映する。
    ///
    /// フォントは同梱していない。日本語のコミットメッセージ・作者名を表示するには、
    /// Inspector で各 TextMeshPro の Font Asset に日本語対応の SDF フォントアセットを
    /// 割り当てる必要がある（未設定の場合、既定フォントでは日本語が豆腐（□）になる）。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BonsaiInfoPanel : UdonSharpBehaviour
    {
        [Header("表示先（ワールド空間の TextMeshPro を割り当てる）")]
        public TMPro.TextMeshPro headingText;  // 見出し：ブランチ名 / 幹の名前
        public TMPro.TextMeshPro messageText;  // 最新コミットの件名
        public TMPro.TextMeshPro metaText;     // sha・作者・日時
        public TMPro.TextMeshPro statsText;    // ahead/behind または commits/recent30

        /// <summary>
        /// 枝を選択したときに呼ぶ。index は BonsaiJsonParser の branch 配列に対応する。
        /// </summary>
        public void ShowBranch(BonsaiJsonParser data, int index)
        {
            if (data == null)
            {
                Debug.LogWarning("[Bonsai] ShowBranch: data is null");
                return;
            }
            if (index < 0 || index >= data.branchCount)
            {
                Debug.LogWarning("[Bonsai] ShowBranch: index out of range " + index);
                return;
            }

            string name = NullToEmpty(data.branchName[index]);
            if (name.Length == 0)
                name = "枝 #" + index; // v1 データ等、名前が無い場合のフォールバック

            string statsLine = "ahead " + data.branchAhead[index] + " / behind " + data.branchBehind[index];

            Apply(name, data.branchHeadSha[index], data.branchHeadMsg[index], data.branchHeadAuthor[index],
                data.branchHeadAt[index], data.genAt, statsLine);
        }

        /// <summary>
        /// 未選択時、またはユーザーが幹（デフォルトブランチ）を選んだときに呼ぶ。
        /// </summary>
        public void ShowTrunk(BonsaiJsonParser data)
        {
            if (data == null)
            {
                Debug.LogWarning("[Bonsai] ShowTrunk: data is null");
                return;
            }

            string name = NullToEmpty(data.trunkName);
            if (name.Length == 0)
                name = "幹（デフォルトブランチ）";

            string statsLine = "commits " + data.trunkCommits + " / 直近30日 " + data.trunkRecent30;

            Apply(name, data.trunkHeadSha, data.trunkHeadMsg, data.trunkHeadAuthor, data.trunkHeadAt, data.genAt, statsLine);
        }

        private void Apply(string name, string sha, string msg, string author, int atSeconds, int genSeconds, string statsLine)
        {
            // BonsaiJsonParser の string フィールドはパース完了前は null のままになり得るため、
            // ここでも保険として null を空文字に変換してから .Length 等を触る。
            name = NullToEmpty(name);
            sha = NullToEmpty(sha);
            msg = NullToEmpty(msg);
            author = NullToEmpty(author);

            if (headingText != null)
                headingText.text = name;

            if (messageText != null)
                messageText.text = msg.Length > 0 ? msg : "(コミット情報なし・古い形式のデータ)";

            if (metaText != null)
            {
                string shaPart = sha.Length > 0 ? sha : "-------";
                string authorPart = author.Length > 0 ? author : "?";
                string timePart = atSeconds > 0 ? (RelativeTime(atSeconds, genSeconds) + "（" + AbsoluteTime(atSeconds) + "）") : "-";
                metaText.text = shaPart + " ・ " + authorPart + " ・ " + timePart;
            }

            if (statsText != null)
                statsText.text = statsLine;
        }

        // UdonSharp では ?? / ?. が確実に使えるか確証が持てないため、素朴な if で null を空文字にする。
        private string NullToEmpty(string s)
        {
            if (s == null)
                s = "";
            return s;
        }

        // ---------------------------------------------------------------
        // 相対時刻表示（「3時間前」等）
        //
        // 本来は System.DateTime.UtcNow を使って「実際の現在時刻」を基準にしたいところだが、
        // UdonSharp で確実に動作すると確認できたのは
        //   ・DateTime.UtcNow の取得
        //   ・DateTime 同士の引き算（TimeSpan を返す演算子）
        //   ・TimeSpan.TotalSeconds
        // までで（UdonSharp 同梱のテストスクリプト ForLoopTest.cs で実際に使われているのを確認済み）、
        // JSON の unix秒（gen / head.at）と DateTime を橋渡しするための DateTime コンストラクタや
        // Ticks プロパティが UdonSharp の extern 一覧に含まれているかはこの環境（Unity 実機無し）
        // では検証できなかった。存在しないコンストラクタ/プロパティを呼ぶとビルドが壊れるため、
        // ここでは確実に動く整数演算だけで完結する方法を採用し、
        // 「実世界の現在時刻」ではなく「JSON生成時刻 (gen)」を基準に相対時刻を計算する。
        // bonsai.json は数時間おきに再生成される想定（README参照）なので、
        // 「生成時点からの経過時間」で実用上十分な鮮度の表示になる。
        // ---------------------------------------------------------------

        private string RelativeTime(int atSeconds, int nowSeconds)
        {
            int delta = nowSeconds - atSeconds;
            if (delta < 0)
                delta = 0;

            if (delta < 60)
                return "たった今";
            if (delta < 3600)
                return (delta / 60) + "分前";
            if (delta < 86400)
                return (delta / 3600) + "時間前";
            return (delta / 86400) + "日前";
        }

        // "yyyy-MM-dd HH:mm" 形式（UTC）。DateTime/string.Format を使わず、
        // unix秒からの手計算（Howard Hinnant 氏の civil_from_days アルゴリズム）で組み立てる。
        // 加減算・剰余のみの整数演算なので UdonSharp 対応に不安が無い。
        private string AbsoluteTime(int unixSeconds)
        {
            int days = unixSeconds / 86400;
            int secOfDay = unixSeconds % 86400;
            if (secOfDay < 0)
            {
                secOfDay += 86400;
                days -= 1;
            }
            int hour = secOfDay / 3600;
            int minute = (secOfDay % 3600) / 60;

            int z = days + 719468;
            int era = (z >= 0 ? z : z - 146096) / 146097;
            int doe = z - era * 146097;
            int yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
            int y = yoe + era * 400;
            int doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
            int mp = (5 * doy + 2) / 153;
            int day = doy - (153 * mp + 2) / 5 + 1;
            int month = mp + (mp < 10 ? 3 : -9);
            int year = y + (month <= 2 ? 1 : 0);

            return year + "-" + Pad2(month) + "-" + Pad2(day) + " " + Pad2(hour) + ":" + Pad2(minute);
        }

        private string Pad2(int v)
        {
            if (v < 10)
                return "0" + v;
            return v.ToString();
        }
    }
}
