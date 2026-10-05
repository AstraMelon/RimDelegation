using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「条件原语」（S23）：随机事件的**触发前提**与**概率倍率**的最小可组合单位。
    ///
    /// 为什么要有它：在此之前，"这条事件什么时候能触发"写在 C# 的 <c>CanFire</c> 里
    /// （"塌方要有 ≥5 单位已采矿石""闹别扭要 4 人以上""工伤要有人站着"），
    /// 想加一条同类事件就必须再写一个类。拆成原语后，这些判据变成 XML 里的
    /// <c>&lt;li Class="RimDelegation.DelegationConditionDef_…"&gt;</c>。
    ///
    /// 与效果原语同款：**普通类、不是 Def**，走 `<li Class="…">` 内联多态
    /// （理由见 <see cref="DelegationEffectDef" /> 的注释）。
    ///
    /// 三个挂点（见 <see cref="DelegationEventDef" />）：
    ///   · <c>conditions</c>        —— 全部满足才允许触发；
    ///   · <c>blockers</c>          —— 任一满足即禁止触发（写否定条件用，比"全满足"的否定更好读）；
    ///   · <c>chanceMultipliers</c> —— 相乘成动态概率倍率（工伤的疲劳曲线就在这里）。
    /// </summary>
    public abstract class DelegationConditionDef
    {
        /// <summary>此刻这条前提成立吗。</summary>
        public virtual bool IsMet(Delegation d, Site site)
        {
            return true;
        }

        /// <summary>作为概率倍率时的系数（1 = 不影响）。</summary>
        public virtual float Factor(Delegation d)
        {
            return 1f;
        }
    }

    /// <summary>已采出、尚未交付的矿石至少这么多（塌方的前提前身）。</summary>
    public class DelegationConditionDef_OreUnitsAtLeast : DelegationConditionDef
    {
        public float minUnits = 5f;

        public override bool IsMet(Delegation d, Site site)
        {
            return d != null && d.oreUnits >= minUnits;
        }
    }

    /// <summary>活着的参与者**严格多于**这个数（原"闹别扭要 4 人以上"= min 3）。</summary>
    public class DelegationConditionDef_ParticipantsAliveGreaterThan : DelegationConditionDef
    {
        public int min = 3;

        public override bool IsMet(Delegation d, Site site)
        {
            if (d?.participants == null) return false;
            int n = 0;
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                if (p != null && !p.Dead) n++;
            }
            return n > min;
        }
    }

    /// <summary>至少有一个还能动的人（没死、没倒）。</summary>
    public class DelegationConditionDef_AnyAbleParticipant : DelegationConditionDef
    {
        public override bool IsMet(Delegation d, Site site)
        {
            if (d?.participants == null) return false;
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                if (p != null && !p.Dead && !p.Downed) return true;
            }
            return false;
        }
    }

    /// <summary>事件点的存量还没取尽。</summary>
    public class DelegationConditionDef_DepositNotDepleted : DelegationConditionDef
    {
        public override bool IsMet(Delegation d, Site site)
        {
            return d?.deposit != null && !d.deposit.IsDepleted;
        }
    }

    /// <summary>
    /// 这门活的目标规模**可以**被凭空放大（结构不变量，见 <see cref="DelegationWorker.AllowsScaleIncrease" />）。
    /// 漏配时兜住"富矿脉把物资藏匿点推进死循环"那一类事故。
    /// </summary>
    public class DelegationConditionDef_ScaleIncreaseAllowed : DelegationConditionDef
    {
        public override bool IsMet(Delegation d, Site site)
        {
            return d?.Worker?.AllowsScaleIncrease ?? false;
        }
    }

    /// <summary>该地点真有守军（判据 `SitePartParams.threatPoints`，读存档、不需要生成地图）。</summary>
    public class DelegationConditionDef_HasThreat : DelegationConditionDef
    {
        public override bool IsMet(Delegation d, Site site)
        {
            return ThreatAssessmentEntry.HasThreat(site);
        }
    }

    /// <summary>该地点没有守军。</summary>
    public class DelegationConditionDef_NoThreat : DelegationConditionDef
    {
        public override bool IsMet(Delegation d, Site site)
        {
            return !ThreatAssessmentEntry.HasThreat(site);
        }
    }

    /// <summary>这条事件只对列出的委派成立（不写 = 不限）。</summary>
    public class DelegationConditionDef_DefIs : DelegationConditionDef
    {
        public List<DelegationDef> defs = new List<DelegationDef>();

        public override bool IsMet(Delegation d, Site site)
        {
            if (defs.NullOrEmpty()) return true;
            return d?.def != null && defs.Contains(d.def);
        }
    }

    /// <summary>
    /// 疲劳概率倍率（原 <c>DelegationEventDef_PawnAccident.ChanceMultiplier</c>，§19.25）：
    /// 全队平均休息低于 <see cref="fatigueOnsetRest" /> 起开始抬升，休息见底时达到 <see cref="maxMultiplier" />。
    ///
    /// ⚠️ 倍率是**每小时掷骰那一刻**算的，所以日周期会自然体现出来。
    /// 别用"平均休息对应的倍率"来估：它是分段线性（凸）的，按小时算再平均要大得多
    /// （加班模式：1.36 vs 按平均休息算的 1.00，实测见 §19.25 的表）。
    /// </summary>
    public class DelegationConditionDef_FatigueMultiplier : DelegationConditionDef
    {
        public float fatigueOnsetRest = 0.7f;
        public float maxMultiplier = 4f;

        public override float Factor(Delegation d)
        {
            if (d == null) return 1f;
            return 1f + (maxMultiplier - 1f) * DelegationUtility.FatigueFactor(d, fatigueOnsetRest);
        }
    }
}
