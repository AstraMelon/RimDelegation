using Verse;

namespace RimDelegation
{
    /// <summary>
    /// **抵达前**的目标预览：只给"可能的范围"，不给精确值。
    ///
    /// 为什么需要它：存量（DelegationDeposit）是在抵达后才掷定的，
    /// 所以计划阶段只能从 GenStepDef 的 totalValueRange 反推一个区间；
    /// 到了地方再弹对话框时用的是已经掷定的精确存量（见 Dialog_ChooseDelegation 的 exact 分支）。
    /// </summary>
    public class DelegationPreview
    {
        /// <summary>目标物（采矿 = Mineable* 的 ThingDef）。</summary>
        public ThingDef resourceDef;

        /// <summary>可能的最小/最大规模（采矿 = 格数）。</summary>
        public int minUnits;
        public int maxUnits;

        /// <summary>每个单位的产出（采矿 = 每格 EffectiveMineableYield）。</summary>
        public int yieldPerUnit;

        /// <summary>
        /// 每**进度单位**最终装进车队的质量（kg）——对话框据此预告"干完后车队多重"。
        /// 采矿 = `yieldPerUnit × mineableThing.BaseMass`；物资点 = 平均每件质量。
        /// 0 = 未知（此时对话框退回按 `resourceDef.building.mineableThing.BaseMass` 估算）。
        /// </summary>
        public float massPerUnit;

        public int MinYieldUnits => minUnits * yieldPerUnit;

        public int MaxYieldUnits => maxUnits * yieldPerUnit;

        public float AverageUnits => (minUnits + maxUnits) * 0.5f;

        public int AverageYieldUnits => (MinYieldUnits + MaxYieldUnits) / 2;
    }
}
