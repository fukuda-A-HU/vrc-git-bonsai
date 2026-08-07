using UdonSharp;
using UnityEngine;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace BonsaiGit
{
    /// <summary>
    /// bonsai.json のダウンロード（またはダミーデータ）→パース→メッシュ生成→成長アニメ、までを統括する。
    /// Udon には try/catch が無いため、失敗系はすべて戻り値チェック＋ダミーへのフォールバックで防御する。
    /// 加えて、枝のUseによる選択状態（_selectedBranch）をプレイヤー間で同期する。
    /// UdonSyncedフィールドを持つため、SyncModeはNoVariableSyncからManualに変更している。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class BonsaiController : UdonSharpBehaviour
    {
        [Header("Data Source")]
        public VRCUrl jsonUrl;
        public TextAsset dummyJson;
        public bool useDummy = true;

        [Header("References")]
        public BonsaiJsonParser parser;
        public BonsaiTreeBuilder builder;

        [Header("木札 / 枝の当たり判定")]
        public BonsaiInfoPanel infoPanel;
        public BonsaiBranchTarget[] branchTargets; // 長さ16。Bonsaiの子として常設し、Build後に位置だけ更新する。
        public Transform trunkTarget; // 幹用の当たり判定（任意。未設定でも動作する）

        private const int MaxRetries = 3;
        private const float GrowDuration = 6f;

        private int _retryCount;
        private bool _meshBuilt;
        private bool _growing;
        private float _growElapsed;

        // -1 = 幹（デフォルトブランチ）を選択中。SelectBranch()経由でのみ更新し、
        // OnDeserialization()で他プレイヤーの選択を反映する。
        [UdonSynced] private int _selectedBranch = -1;

        private void Start()
        {
            // メッシュ生成前は見えないよう縮めておき、成長アニメの開始点にする。
            transform.localScale = Vector3.zero;

            if (parser == null || builder == null)
            {
                Debug.LogError("[Bonsai] parser/builder not assigned");
                return;
            }

            if (useDummy)
            {
                BuildFromDummy(false);
                return;
            }

            if (jsonUrl == null)
            {
                Debug.LogWarning("[Bonsai] jsonUrl not set, falling back to dummy");
                BuildFromDummy(true);
                return;
            }

            Debug.Log("[Bonsai] starting download");
            VRCStringDownloader.LoadUrl(jsonUrl, (IUdonEventReceiver)this);
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            string json = result.Result;
            int length = json != null ? json.Length : 0;
            Debug.Log("[Bonsai] download ok (" + length + " bytes)");

            if (parser.Parse(json))
            {
                StartBuild();
            }
            else
            {
                Debug.LogWarning("[Bonsai] parse failed, falling back to dummy");
                BuildFromDummy(true);
            }
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            Debug.LogWarning("[Bonsai] download failed: " + result.Error);

            _retryCount++;
            if (_retryCount <= MaxRetries)
            {
                Debug.Log("[Bonsai] retry " + _retryCount + "/" + MaxRetries + " in 10s");
                SendCustomEventDelayedSeconds(nameof(RetryDownload), 10f);
            }
            else
            {
                BuildFromDummy(true);
            }
        }

        /// <summary>
        /// SendCustomEventDelayedSeconds から呼ばれるリトライ用エントリポイント。public 必須。
        /// </summary>
        public void RetryDownload()
        {
            VRCStringDownloader.LoadUrl(jsonUrl, (IUdonEventReceiver)this);
        }

        /// <summary>
        /// ダミーデータからビルドする。isFallbackFromError で「意図的な使用」と
        /// 「ダウンロード/パース失敗によるフォールバック」をログ上で区別する。
        /// </summary>
        private void BuildFromDummy(bool isFallbackFromError)
        {
            if (dummyJson == null)
            {
                Debug.LogError("[Bonsai] dummy data unavailable: dummyJson TextAsset not assigned");
                return;
            }

            if (parser.Parse(dummyJson.text))
            {
                if (isFallbackFromError)
                    Debug.Log("[Bonsai] fallback to dummy (after download/parse error)");
                else
                    Debug.Log("[Bonsai] using dummy data (useDummy=true)");
                StartBuild();
            }
            else
            {
                Debug.LogError("[Bonsai] dummy json parse failed");
            }
        }

        private void StartBuild()
        {
            // メッシュ生成は1回だけ。ダウンロード成功後にリトライ経由の呼び出しが重なっても二重生成しない。
            if (_meshBuilt)
                return;
            _meshBuilt = true;

            builder.Build(parser);

            transform.localScale = Vector3.zero;
            _growElapsed = 0f;
            _growing = true;

            // 後から join したプレイヤーは「先に同期で選択状態を受信 → 後からパース完了・StartBuild」
            // という順序になり得る。ここで _selectedBranch を -1 に上書きすると、既に受信済みの
            // 選択（他プレイヤーが選んでいる枝）を握り潰してしまうため、代入はせず反映だけ行う。
            // 初期値（未同期時の -1 = 幹）はフィールド宣言の初期化子で担保する。
            ApplySelection();
        }

        private void Update()
        {
            if (!_growing)
                return;

            _growElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_growElapsed / GrowDuration);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            transform.localScale = Vector3.one * eased;

            if (t >= 1f)
            {
                _growing = false;
                // 成長中は枝先の位置がスケールに合わせて動き続け、当たり判定が不安定になるため、
                // 成長アニメが完了してから branchTargets を配置・有効化する。
                ActivateBranchTargets();
            }
        }

        /// <summary>
        /// branchTargets（Bonsaiの子として常設された当たり判定オブジェクト）を、
        /// 実際に生成された枝の本数ぶんだけ枝先へ配置して有効化し、余りは非アクティブにする。
        /// </summary>
        private void ActivateBranchTargets()
        {
            if (branchTargets == null)
                return;

            int builtCount = builder.builtBranchCount;

            for (int i = 0; i < branchTargets.Length; i++)
            {
                BonsaiBranchTarget target = branchTargets[i];
                if (target == null)
                    continue;

                if (i < builtCount && i < builder.branchTipPositions.Length)
                {
                    // branchTipPositions はBonsaiのローカル座標。branchTargetsはBonsaiの子である前提。
                    target.transform.localPosition = builder.branchTipPositions[i];
                    target.branchIndex = i;
                    target.gameObject.SetActive(true);
                }
                else
                {
                    target.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// 枝がUseされたときに呼ばれる。選択状態を更新し、木札に反映したうえで他プレイヤーにも同期する。
        /// index に -1 を渡すと幹（デフォルトブランチ）選択として扱う。
        /// </summary>
        public void SelectBranch(int index)
        {
            if (parser == null)
            {
                Debug.LogWarning("[Bonsai] SelectBranch: parser not assigned");
                return;
            }
            if (index < -1 || index >= parser.branchCount)
            {
                Debug.LogWarning("[Bonsai] SelectBranch: index out of range " + index);
                return;
            }

            _selectedBranch = index;
            ApplySelection();

            // 同期変数を書き換えたので、自分がオーナーになってから送信する。
            // 既にオーナーの場合は SetOwner が無駄になるので呼ばない。
            if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            RequestSerialization();
        }

        // _selectedBranch の内容を木札（infoPanel）へ反映する。ローカルでの選択・同期受信の両方から呼ぶ。
        private void ApplySelection()
        {
            if (infoPanel == null)
                return;

            // 後から join したプレイヤーは、自分の bonsai.json のダウンロード・パースが終わる前に
            // 既にワールドに居るプレイヤーの選択状態（OnDeserialization）を受信し得る。
            // その時点では parser の string フィールドが未初期化（null）のままで、
            // infoPanel 側で NullReferenceException になり得るため、パース完了までは何もしない。
            if (parser == null || !parser.parsed)
                return;

            if (_selectedBranch < 0)
                infoPanel.ShowTrunk(parser);
            else
                infoPanel.ShowBranch(parser, _selectedBranch);
        }

        // 他プレイヤーがSelectBranch()を呼んだ結果を受信したときに呼ばれる。
        public override void OnDeserialization()
        {
            ApplySelection();
        }
    }
}
