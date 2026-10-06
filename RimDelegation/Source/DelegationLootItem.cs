using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一件（或一叠）**缴获**：打完仗从倒下的守军身上清点出来的东西（S30）。
    ///
    /// 用户口径（2026-09-28）：「作战任务的主要是缴获敌人装备，哨所/工作站点/站点的应该放在收集任务里面」。
    /// 所以本类只服务**作战侧**：来源是敌人的 `equipment` / `apparel` / `inventory`。
    ///
    /// 为什么只存"重建一件实物所需的最小信息"而不存 `Thing`：
    ///   1. 那些 pawn 是**只为取数值**临时生成的，抄完数值立刻 `Destroy()`（见 `ThreatRosterFactory`）
    ///      —— 存 Thing 等于存一个死对象，而且会把它们写进存档；
    ///   2. 实物要等「搜集战利品」段（可能在读档之后才走到）才真正造出来 ⇒ 必须能存档。
    /// 代价说清楚：**耐久（HP）不保留**（只保留 Def / 材质 / 品质 / 数量）——
    /// 一把打旧了的枪缴获回来是崭新的。这是刻意的近似，不是漏了。
    /// </summary>
    public class DelegationLootItem : IExposable
    {
        /// <summary>物品 Def（枪、甲、药、银…）。</summary>
        public ThingDef def;

        /// <summary>材质（钢铁/零部件/皮革…）。null = 该物品没有材质。</summary>
        public ThingDef stuff;

        /// <summary>数量（只有可堆叠物才会 > 1；枪甲各自单独一条）。</summary>
        public int count = 1;

        /// <summary>品质；**-1 = 这东西没有品质**（用 int 存，因为 Scribe_Values 只认值类型）。</summary>
        public int quality = -1;

        /// <summary>
        /// 这是**第几个敌人**身上的东西（按 `ThreatRosterFactory` 的编队顺序，从 0 起）。
        ///
        /// 为什么要它：缴获只算**打掉的**敌人（阵亡 + 倒地），而"谁被打掉了"记在战斗结果的
        /// `UnitReport` 里 —— 那张表与 `CombatScene` 的单位**同序**，场景里敌方又严格按编队顺序加入，
        /// 所以用下标就能对齐。不按名字匹配：同名守军（三个"海盗"）会配错。
        /// </summary>
        public int enemyIndex;

        /// <summary>
        /// RIM-32(1A)：**生物编码的原主名**（null / 空 = 这件东西没被编码）。
        ///
        /// 只存名字**不存 pawn 引用**：原主是临时生成的守军，抄完数值就 `Destroy` 了，
        /// 存引用等于存一个死对象（`CompBiocodable` 自己也只是为了显示"Coded for X"）。
        /// 还原时把 comp 置成"已编码 + 原主名 + 无 pawn 引用" ⇒ 谁都装备不了（1A 要的"原主锁死"）。
        /// </summary>
        public string codedPawnLabel;

        /// <summary>
        /// RIM-32(3A)：**武器特性的 defName 列表**（空 / null = 没有特性）。
        ///
        /// 为什么存 defName 而不是 `WeaponTraitDef`：这是记账（要进存档的配方），
        /// 存 Def 实例会让 Scribe 变成"引用 Def"（本项目惯例是值化）。
        /// 还原时要**先清空**再逐条加：`ThingMaker.MakeThing` 对独特武器会自己随机生成一套
        /// （反编译 `CompUniqueWeapon.PostPostMake → InitializeTraits`），不清就会多出它本来没有的特性。
        /// </summary>
        public List<string> weaponTraits;

        /// <summary>单件质量（kg）。只用于"装不装得下"的估算。</summary>
        public float UnitMass => def == null ? 0f : def.GetStatValueAbstract(StatDefOf.Mass, stuff);

        /// <summary>
        /// RIM-31：这一条的**真实单件市价**（银）—— 造出实物之后取的 `Thing.MarketValue`，
        /// 含品质 / 材质 / 武器特性。`0` = 还没造过实物（旧存档的行、或只是"待搬"的账），
        /// 此时按 <see cref="SortValue" /> 退回粗估。
        /// </summary>
        public float unitMarketValue;

        /// <summary>
        /// 这一条的估值（银）——**排序与 UI 表格的唯一来源**（RIM-31 起）。
        /// 优先用真实市价（造实物时写进 <see cref="unitMarketValue" />），没有时退回
        /// `BaseMarketValue × count` 那份粗估（旧存档）。旧写法两处各有一套口径：
        /// 排序与表格用粗估、段说明行用真实市价 ⇒ 一把传奇枪在两个地方差约 5 倍。
        /// </summary>
        public float SortValue => def == null ? 0f
            : (unitMarketValue > 0f ? unitMarketValue : def.BaseMarketValue) * count;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref stuff, "stuff");
            Scribe_Values.Look(ref count, "count", 1);
            Scribe_Values.Look(ref quality, "quality", -1);
            Scribe_Values.Look(ref enemyIndex, "enemyIndex", 0);
            Scribe_Values.Look(ref unitMarketValue, "unitMarketValue", 0f);
            Scribe_Values.Look(ref codedPawnLabel, "codedPawnLabel");
            Scribe_Collections.Look(ref weaponTraits, "weaponTraits", LookMode.Value);
        }
    }
}
