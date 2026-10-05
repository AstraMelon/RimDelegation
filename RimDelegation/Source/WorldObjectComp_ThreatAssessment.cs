using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using RimDelegation.Combat;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「威胁评估」入口 —— 车队侧。
    ///
    /// 车队停在**有威胁的地点**上时，车队 gizmo 栏出现一个按钮：
    /// 它用真实 pawn 折算战场、复现站点守军的确定性编制、跑战斗算法，
    /// 给出成功率与伤亡区间 —— 全程不进地图。
    ///
    /// 这是 §18/§19 里"战斗委派"的 **Forecast 阶段**；
    /// 后续的 Resolving（真的打）与阶段机接在同一个 <see cref="CombatScene"/> 上。
    ///
    /// ⚠️ 判定与跳转全部委托给 <see cref="ThreatAssessmentEntry"/>：
    ///    委派对话框里也有一条同样的入口，两处必须同口径。
    /// </summary>
    public class WorldObjectComp_ThreatAssessment : WorldObjectComp
    {
        private Site Site => parent as Site;

        /// <summary>有威胁点的部件才算威胁（PreciousLump / ItemStash 主件的 wantsThreatPoints 是 false）。</summary>
        public bool HasThreat => ThreatAssessmentEntry.HasThreat(Site);

        public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
        {
            if (!HasThreat) yield break;
            if (caravan == null || Site == null) yield break;
            if (caravan.Tile != Site.Tile) yield break;

            yield return new Command_Action
            {
                // ⚠️ 必须给图标：`Command.DrawIcon` 在 `icon == null` 时画 `BaseContent.BadTex`（洋红叉）
                icon = TexCommand.SquadAttack,
                defaultLabel = "威胁评估",
                defaultDesc = (ThreatAssessmentEntry.SummaryLine(Site) ?? "") + "\n\n" +
                              "用真实装备与技能折算战场，推算这里的守军编制并跑战斗算法。\n\n" +
                              "不会进入地图，也不会真的开打 —— 只给成功率与伤亡区间。",
                action = delegate { OpenAssessment(caravan); },
            };
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            // 车队还没到也能先看看（用当前车队 + 地点威胁推演，但标注"尚未抵达"）
            if (!HasThreat || caravan == null || Site == null) yield break;
            if (caravan.Tile == Site.Tile) yield break;

            yield return new FloatMenuOption(
                "威胁评估：" + Site.LabelCap + "（远行队尚未抵达）",
                delegate { OpenAssessment(caravan); });
        }

        private void OpenAssessment(Caravan caravan)
        {
            ThreatAssessmentEntry.Open(caravan, Site);
        }
    }
}
