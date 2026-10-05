using System.Collections.Generic;
using Verse;

namespace RimDelegation
{
    /// <summary>委派的结束条件。</summary>
    public enum DelegationEndCondition
    {
        /// <summary>把目标整点取尽为止（采矿"采空" / 物资点"搬空" / 营救"救出"，文案由 worker 给）。</summary>
        UntilDepleted,
        /// <summary>干满 N 天就收工。</summary>
        Days,
        /// <summary>产出满 N 个单位就收工（单位由 worker 的 OutputUnitName 给）。</summary>
        Quota
    }

    /// <summary>
    /// 一次委派的"下单内容"：计划阶段在对话框里选好，随 CaravanArrivalAction 一起 Scribe，
    /// 抵达时原样交给地点组件。轴变多时只改这里，不动各处签名。
    /// </summary>
    public class DelegationRequest : IExposable
    {
        public DelegationModeDef mode;
        public List<Pawn> pawns = new List<Pawn>();

        /// <summary>作战姿态（强攻 / 潜入）；null = 这条委派没有姿态轴。</summary>
        public DelegationApproachDef approach;

        public DelegationEndCondition endCondition = DelegationEndCondition.UntilDepleted;
        public int daysLimit = 5;
        public int quotaUnits = 400;
        public bool abortWhenOutOfFood = true;

        public DelegationRequest()
        {
        }

        public DelegationRequest(DelegationModeDef mode, List<Pawn> pawns)
        {
            this.mode = mode;
            this.pawns = pawns ?? new List<Pawn>();
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref mode, "mode");
            Scribe_Defs.Look(ref approach, "approach");
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
            Scribe_Values.Look(ref endCondition, "endCondition", DelegationEndCondition.UntilDepleted);
            Scribe_Values.Look(ref daysLimit, "daysLimit", 5);
            Scribe_Values.Look(ref quotaUnits, "quotaUnits", 400);
            Scribe_Values.Look(ref abortWhenOutOfFood, "abortWhenOutOfFood", true);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && pawns == null)
            {
                pawns = new List<Pawn>();
            }
        }

        /// <summary>
        /// 给 UI 用的一行摘要。
        ///
        /// `worker` 可空：空的时候退回采矿口径（只为日志等"没有 worker 上下文"的场合兜底，
        /// 界面上一律把 worker 传进来 —— 传输不到就会出现"搜刮物资点时写着采空为止"）。
        /// </summary>
        public string EndConditionLabel(DelegationWorker worker = null)
        {
            return DelegationUIUtility.EndConditionLabel(endCondition, daysLimit, quotaUnits, worker);
        }
    }
}
