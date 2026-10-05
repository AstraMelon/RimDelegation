using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 随机事件的**分级**（S15）。只有两档 —— 用户明确不要 "Major Crises"：
    ///   · <see cref="Flavor"/> = 纯叙事、零机制影响（只留痕，不提示）；
    ///   · <see cref="Effect"/> = 有实质影响（停摆 / 损失 / 伤害 / 心情 / 改规模）。
    ///
    /// 这个轴决定"要不要弹东西"，与 `LetterDef`（信件长什么样）是两件事，所以单独一格。
    /// </summary>
    public enum DelegationEventSeverity
    {
        Flavor,
        Effect,
    }

    /// <summary>
    /// 一条**已经发生过**的随机事件的留痕（S15）。
    ///
    /// 为什么必须存它：`Apply()` 返回的那句描述原本只进信件正文，事件过去就再也找不回来 ——
    /// 所以"流程块里显示最近发生了什么""结束时写进报告""历史记录"这三件事都没有数据可用。
    /// 这里存下 def + 时刻 + 文本 + 是否已被玩家看过。
    ///
    /// 挂在 <see cref="Delegation" /> 上（`Scribe_Deep` 里的一层），不是 WorldObject comp 的平铺层
    /// ⇒ 标签不需要 `ro` 前缀（与 S9 的 `itemLedger` 同一规矩）。
    /// </summary>
    public class DelegationEventLogEntry : IExposable
    {
        public DelegationEventDef def;

        /// <summary>
        /// S23：段进入钩子产生的留痕用**段名**代替事件名（例：`交战`）。
        /// 非空时优先显示它 —— 否则那条留痕会显示成兜底的"事件"，玩家看不出那是哪一段的结果。
        /// </summary>
        public string phaseLabel;

        /// <summary>
        /// S29：这条留痕出自**作战**那一段吗（`DelegationPhaseDef.InCombatFlow`）。
        ///
        /// 用途只有一个：流程块里那条 `!` 行要落在**对应的分组**下面。用户原话：
        /// 「图一的信息应该在作战任务里面」—— 交战结算原来被排在流程块最末尾，
        /// 视觉上就挂在了「收集任务」组的下面（那会儿正在开采），看着像是开采留下的东西。
        /// </summary>
        public bool combatPhase;

        /// <summary>发生时刻（TicksAbs），报告里用来排序与算间隔。</summary>
        public int firedTickAbs;

        /// <summary>`Apply()` 返回的那句话（损失多少、谁受伤都在里面）。</summary>
        public string detail;

        /// <summary>玩家是否已经"看过"（底部按钮角标用它计数）。</summary>
        public bool seen;

        public DelegationEventSeverity Severity =>
            def?.severity ?? DelegationEventSeverity.Effect;

        public string Label => !phaseLabel.NullOrEmpty()
            ? phaseLabel
            : (def?.LabelCap.ToString() ?? "事件");

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref phaseLabel, "phaseLabel");
            Scribe_Values.Look(ref combatPhase, "combatPhase", false);
            Scribe_Values.Look(ref firedTickAbs, "tick", 0);
            Scribe_Values.Look(ref detail, "detail");
            Scribe_Values.Look(ref seen, "seen", false);
        }
    }
}
