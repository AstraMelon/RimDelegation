using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派对话框里「预期获得」列表的一行（S6）。
    ///
    /// 为什么把措辞留给 worker、UI 只负责画：三类目标的"量纲"完全不同 ——
    ///   物资藏匿点 = 一组精确的 Thing（图标 + 件数 + kg + 市价）；
    ///   矿点       = 一种产物 + **区间**（格数是抵达后才掷定的，见 DelegationPreview）；
    ///   营救       = 一个人（没有件数，只有"1 人"与健康度）。
    /// 如果让 UI 自己拼这些字，它就必须知道这三套玩法 —— 那正是本项目一直在拆的耦合。
    /// </summary>
    public class DelegationPreviewItem
    {
        /// <summary>图标来源（ThingDef）。为 null 时看 <see cref="pawn" />，两者都空则不画图标。</summary>
        public ThingDef thingDef;

        /// <summary>图标来源（人）。人和物件的图标走的是两条路：人是头像，物件是 uiIcon。</summary>
        public Pawn pawn;

        /// <summary>主标题（"钢铁" / "Chisa"）。</summary>
        public string label;

        /// <summary>副标题（"×140 件 · 320 kg · 约 420 银"）。</summary>
        public string detail;

        /// <summary>
        /// 由 UI 填的**前缀**（画在 <see cref="detail" /> 左边）。
        ///
        /// 用途（S9）：作业期的「现场物资」要显示"已获取 3/12 件"，
        /// 而"预期总数"只有 <see cref="Delegation.itemLedger" /> 那一份开工时的台账里才有。
        /// 前缀由 <c>DelegationUIUtility.ProgressItemRows</c> 统一算好填进来 ——
        /// 这样 worker 只管说"现场还剩什么"，不必知道台账怎么读（量纲/文案仍只有一份）。
        /// </summary>
        public string detailPrefix;

        /// <summary>市价合计（银）；负 = 未知（不显示）。</summary>
        public float value = -1f;

        // ── 数量（S9：让 UI 能画"已获取 X/Y"）──────────────────────────────
        //
        // 为什么要在这一层带数量：`detail` 是给玩家看的一整句话，UI 从里面**抠不出数字**。
        // 「现场物资」要显示"已获取 X/Y 单位"就必须有一个机器可读的数值，
        // 而"现场还剩多少"只有 worker 知道 ⇒ 由 worker 填这两格，UI 只做减法。

        /// <summary>现场还剩多少个 <see cref="unitLabel" />（&lt;= 0 且 uncertain 时 UI 不显示数量）。</summary>
        public int count;

        /// <summary>这一行的数量单位（"件" / "单位" / "人"）。空 = 这一行没有"件数"概念。</summary>
        public string unitLabel;

        /// <summary>这一行现场剩余的质量（kg；&lt;= 0 时不显示）。</summary>
        public float mass;

        /// <summary>这一行是不是"还没定"的占位（UI 会画成灰字）。</summary>
        public bool uncertain;
    }
}
