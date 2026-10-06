using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派的**作战姿态** —— 与 <see cref="DelegationModeDef"/>（工时窗口 × 作业强度）正交的一条轴。
    ///
    /// 为什么不能塞进 `DelegationModeDef`：模式回答的是"**干多久**"（08:00–16:00、作业强度 +3），
    /// 姿态回答的是"**怎么进去**"（顶着炮塔强攻、还是摸进去）。两者可以任意组合，
    /// 塞在一起会让模式表变成笛卡尔积。
    ///
    /// 第一条用途是囚犯营救（DESIGN 参考「牢房未探明」这个叙事钩子）：
    ///   · `RimDelegation_Approach_Assault` 强攻 —— 跑战斗结算
    ///   · `RimDelegation_Approach_Stealth` 潜入 —— 掷暴露；失败则**转入强攻且守军先手一轮**
    /// </summary>
    public class DelegationApproachDef : Def
    {
        /// <summary>true = 走潜入判定（掷暴露），false = 直接按战斗结算。</summary>
        public bool stealth;

        /// <summary>
        /// 参与"专业度"的技能 —— 取参与者在这些技能里的**最高等级**。
        /// 空 = 专业度固定为 0（纯按人数与守军数量算）。
        /// </summary>
        public List<SkillDef> skillDefs = new List<SkillDef>();

        /// <summary>基础暴露概率（0..1）。</summary>
        public float baseExposure = 0.15f;

        /// <summary>每个可评估守军单位增加的暴露概率。</summary>
        public float exposurePerGuard = 0.05f;

        /// <summary>每多一名参与者降低的暴露概率（第一个人不计）。</summary>
        public float exposureReductionPerExtraPawn = 0.08f;

        /// <summary>专业度每 1 级降低的暴露概率（取技能最高等级）。</summary>
        public float exposureReductionPerSkillLevel = 0.025f;

        /// <summary>暴露概率的上下限。</summary>
        public float minExposure = 0.05f;
        public float maxExposure = 0.95f;

        /// <summary>
        /// 潜入失败后，守军**先手一轮** —— 折算成我方开局的耐久折扣。
        /// 这个折扣作用在 <c>CombatScene</c> 的快照上，所以**预告与结算看到的是同一个战场**
        /// （"预告即契约"，DESIGN §19.12）。
        /// </summary>
        public bool guardsGetFirstStrike = true;

        /// <summary>先手一轮的折扣系数（1 = 完整体现敌方一回合输出）。</summary>
        public float firstStrikeFactor = 1f;

        /// <summary>
        /// **唯一的**「守军先手一轮」判据（RIM-27 归一，拍板 1A）。判据 = 这次**确实是潜入企图**
        /// （<see cref="stealth" />）且守军有先手（<see cref="guardsGetFirstStrike" />）。
        ///
        /// 语义依据就是 <see cref="guardsGetFirstStrike" /> 自己的注释：「潜入失败后，守军先手一轮」
        /// —— 那是**被发现的代价**，不是"凡是打就有"。
        ///
        /// 为什么要收成一个纯函数：此前同一条规则有 4 份实现，其中 2 份漏了 `stealth` 条件
        /// ⇒ 囚犯营救选「强攻」时玩家会看到**三个不同的答案**（主列成算按有先手算 = 1.0、
        /// 同一面板点开的「查看评估」按 0 算、真结算 0）。玩家照面板决定打不打，正是
        /// "预告即契约"（§19.12）要防的事。
        ///
        /// 现在全仓库只有这一份判据，4 个调用点都走它：
        ///   · <c>DelegationThreatSummary.Refresh</c>（主列成算 / 侧栏摘要）
        ///   · <c>DelegationEffectDef_ResolveCombat.Apply</c>（流程「交战」段真结算）
        ///   · <c>DelegationWorker.ApproachFirstStrikePenalty</c>（对话框底部成算，4 处 UI 都调它）
        ///   · <c>RescueUtility.ResolveClearance</c>（营救清场真结算）
        /// </summary>
        public static float FirstStrikePenalty(DelegationApproachDef approach)
        {
            if (approach == null || !approach.stealth || !approach.guardsGetFirstStrike)
            {
                return 0f;
            }
            return approach.firstStrikeFactor;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (baseExposure < 0f || baseExposure > 1f)
            {
                yield return "baseExposure 应在 0..1";
            }
            if (minExposure > maxExposure)
            {
                yield return "minExposure 不能大于 maxExposure";
            }
        }

        /// <summary>这个姿态是否需要先算出守军编制才能用（强攻要，潜入要算暴露概率也要）。</summary>
        public bool NeedsGuardRoster => true;
    }
}
