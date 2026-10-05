using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「现场物资」台账的一行：**开工那一刻**现场有多少这类东西（S9）。
    ///
    /// 为什么必须存这一份，而不能像以前那样直接画"现场还剩什么"：
    /// 玩家要的文案是「已获取 3/12 件」—— 分母是"这次委派开工时清点到的总量"。
    /// 而 <c>DelegationWorker.ProgressItems</c> 读的是**现场实时内容**（搬走一件就少一件），
    /// 所以分母必须在开工时抄一份留底，否则画出来只能是"剩余 9 件"，说不出"已经拿了 3 件"。
    ///
    /// 为什么按 ThingDef 归并：与 <c>ProgressItems</c> 同一口径（藏匿点是 7×7 密室，
    /// 同类物资常常几十堆，逐堆记会刷出几十行同名条目）。
    ///
    /// 为什么写进存档（Scribe）：委派可以中途存读档，台账丢了就会出现
    /// "读档后 已获取 变成 0/剩余"这种前后不一致 —— 那是玩家最容易察觉的一类 bug。
    /// 旧存档里在途的委派没有这份台账（字段为 null），UI 会自动退回"只显示剩余"的写法。
    /// </summary>
    public class DelegationItemLedgerEntry : IExposable
    {
        /// <summary>物资种类（归并键）。</summary>
        public ThingDef thingDef;

        /// <summary>这类物资的数量单位（"件" / "单位"）；空 = 这类没有件数概念。</summary>
        public string unitLabel;

        /// <summary>开工时清点到的总量（分母）。</summary>
        public int expectedCount;

        /// <summary>开工时的质量合计（kg，0 = 未知）。</summary>
        public float expectedMass;

        /// <summary>开工时的市价合计（银，&lt; 0 = 未知）。</summary>
        public float expectedValue = -1f;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref thingDef, "thingDef");
            Scribe_Values.Look(ref unitLabel, "unitLabel");
            Scribe_Values.Look(ref expectedCount, "expectedCount", 0);
            Scribe_Values.Look(ref expectedMass, "expectedMass", 0f);
            Scribe_Values.Look(ref expectedValue, "expectedValue", -1f);
        }
    }
}
