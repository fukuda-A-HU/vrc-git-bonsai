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
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BonsaiController : UdonSharpBehaviour
    {
        [Header("Data Source")]
        public VRCUrl jsonUrl;
        public TextAsset dummyJson;
        public bool useDummy = true;

        [Header("References")]
        public BonsaiJsonParser parser;
        public BonsaiTreeBuilder builder;

        private const int MaxRetries = 3;
        private const float GrowDuration = 6f;

        private int _retryCount;
        private bool _meshBuilt;
        private bool _growing;
        private float _growElapsed;

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
                _growing = false;
        }
    }
}
