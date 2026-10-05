using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一个事件点的"矿藏/资源存量"。**属于地点，不属于某一次委派**。
    ///
    /// 为什么必须挂在地点上：S1–S4 早期把 rolledValue / totalCells 放在 Delegation 上，
    /// 于是"中止 → 重新委派"会重新掷一次全新的存量，而且已交付的产出不扣减
    /// ⇒ 可以挖一点、中止、重委派来刷资源。存量必须跟着事件点持久化并累计扣减。
    /// </summary>
    public class DelegationDeposit : IExposable
    {
        /// <summary>用哪种委派掷的（决定 worker 语义）。</summary>
        public DelegationDef def;

        /// <summary>目标物（采矿 = Mineable* 的 ThingDef；矿种就是它）。</summary>
        public ThingDef resourceDef;

        /// <summary>worker 语义下的原始掷值（采矿 = 价值，3500–5000）。</summary>
        public float rolledUnits;

        /// <summary>总规模（采矿 = 总格数）。</summary>
        public int totalUnits;

        /// <summary>每个单位的产出（采矿 = 每格的 EffectiveMineableYield）。</summary>
        public int yieldPerUnit;

        /// <summary>累计已采（跨多次委派，保留小数）。</summary>
        public float unitsMined;

        /// <summary>累计已交付到车队/殖民地的产出单位数。</summary>
        public int unitsDelivered;

        /// <summary>被委派过几次（UI 用来提示"这个点已经被采过"）。</summary>
        public int timesDelegated;

        /// <summary>
        /// 这份清单是不是**委派替原版掷的**（物资藏匿点专用）。
        ///
        /// 背景：`SitePart.things` 为空/null 的事件点，原版要等生成地图时才用
        /// `MapGen_DefaultStockpile` 掷清单；委派按同一口径提前掷定并写回了地点
        /// （见 `DelegationWorker_TakeItemStash.TryRollMissingContents`）。
        /// 按本项目的原则，"不可避免的近似必须同时在代码注释与**玩家可见处**写明"——
        /// 这个标记就是那个"玩家可见处"的开关（掷的市价上限取 1800，见 StashMarketValueCap）。
        /// </summary>
        public bool workerRolledContents;

        public int UnitsRemaining => Mathf.Max(0, totalUnits - Mathf.FloorToInt(unitsMined));

        public bool IsDepleted => totalUnits > 0 && unitsMined >= totalUnits - 0.0001f;

        public bool HasBeenWorked => unitsMined > 0.0001f || timesDelegated > 0;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref resourceDef, "resourceDef");
            Scribe_Values.Look(ref rolledUnits, "rolledUnits", 0f);
            Scribe_Values.Look(ref totalUnits, "totalUnits", 0);
            Scribe_Values.Look(ref yieldPerUnit, "yieldPerUnit", 0);
            Scribe_Values.Look(ref unitsMined, "unitsMined", 0f);
            Scribe_Values.Look(ref unitsDelivered, "unitsDelivered", 0);
            Scribe_Values.Look(ref timesDelegated, "timesDelegated", 0);
            Scribe_Values.Look(ref workerRolledContents, "workerRolledContents", false);
        }
    }
}
