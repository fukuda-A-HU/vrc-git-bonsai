using UdonSharp;
using UnityEngine;

namespace BonsaiGit
{
    /// <summary>
    /// 枝1本ぶんの当たり判定。プロシージャルメッシュは盆栽全体で1つしか無いため、
    /// 枝ごとにUseできるようにするには専用のGameObject（SphereCollider付き）が別途必要になる。
    /// Udonでの実行時 Instantiate/Destroy は避けたいので、シーン側に16個を常設しておき、
    /// BonsaiController.StartBuild() が位置だけを枝先へ動かして使わない分を非アクティブにする。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BonsaiBranchTarget : UdonSharpBehaviour
    {
        public BonsaiController controller;

        // BonsaiController 側で Build 結果に応じて書き換える（branchTipPositions のインデックスと対応）。
        [HideInInspector] public int branchIndex;

        private void Start()
        {
            // UdonSharpBehaviour.InteractionText は VRChat のインタラクトプロンプト文言。
            // シーン側（Inspector）でも上書きできるが、既定値としてここで設定しておく。
            InteractionText = "ブランチを見る";
        }

        public override void Interact()
        {
            if (controller == null)
            {
                Debug.LogWarning("[Bonsai] branch target has no controller assigned");
                return;
            }

            controller.SelectBranch(branchIndex);
        }
    }
}
