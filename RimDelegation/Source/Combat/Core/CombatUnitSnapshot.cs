using System;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 伤害类别 —— 只用来决定取哪一档护甲 stat。
    /// 对应 vanilla <c>DamageArmorCategoryDefOf.Sharp / Blunt / Heat</c>。
    /// </summary>
    public enum ArmorCategory
    {
        Sharp = 0,
        Blunt = 1,
        Heat = 2,
    }

    /// <summary>
    /// 一次护甲判定的档位 —— 对应 vanilla <c>ArmorUtility.ApplyArmor</c> 的三个分支。
    /// 战斗日志的"甲弹对抗"明细就记这个。
    /// </summary>
    public enum ArmorRollResult
    {
        /// <summary>未结算（例如伤害为 0）。</summary>
        None = 0,
        /// <summary>完全弹开：<c>Rand.Value &lt; num × 0.5</c> ⇒ 伤害 0。</summary>
        Deflected = 1,
        /// <summary>减半：<c>num × 0.5 ≤ Rand.Value &lt; num</c> ⇒ 伤害减半。</summary>
        Halved = 2,
        /// <summary>全额：<c>Rand.Value ≥ num</c>。</summary>
        Full = 3,
    }

    public static class ArmorRollResultText
    {
        public static string Label(this ArmorRollResult r)
        {
            switch (r)
            {
                case ArmorRollResult.Deflected: return "弹开";
                case ArmorRollResult.Halved: return "减半";
                case ArmorRollResult.Full: return "全额";
                default: return "—";
            }
        }
    }

    /// <summary>
    /// 一个参战单位的**数值快照**。
    ///
    /// 这是"游戏适配层"与"计算核心"之间的唯一契约：核心只看这些数字，
    /// 不知道它是殖民者、机械族还是动物，也不知道手里拿的是步枪还是刀。
    ///
    /// 游戏侧的工厂（<c>CombatSnapshotFactory</c>）负责把真 pawn 折算成快照：
    ///   Range            ← VerbProperties.AdjustedRange(verb, attacker)
    ///   MoveSpeed        ← StatDefOf.MoveSpeed（折算成"格/回合"）
    ///   IsMelee          ← verb 是近战（verbProps.IsMeleeAttack）
    ///   AccuracyNear/Far ← 由 vanilla 的四段精度曲线（touch/short/medium/long
    ///                      @ 3/12/25/40）取两点近似；见 DESIGN.md §19.20
    ///   ShotsPerRound    ← AdjustedFullCycleTime() 与 burstShotCount 折算（§19.16.3）
    ///   DamagePerShot    ← ProjectileProperties.GetDamageAmount() 或 AdjustedMeleeDamageAmount()
    ///   ArmorPen         ← AdjustedArmorPenetration()
    ///   ArmorRating      ← StatDefOf.ArmorRating_Sharp/_Blunt/_Heat（按伤害类别）
    /// </summary>
    public sealed class CombatUnitSnapshot
    {
        public string Name = "?";

        /// <summary>
        /// 画单位图标用的 Def **名字**（S25 引入，RIM-35 由 `ThingDef` 改为 `string`）：编队列表靠它显示"这是人 / 动物 / 机械 / 炮塔"。
        ///
        /// 为什么只留 Def 名字不留 Pawn：敌方 pawn 生成完**立刻被销毁**（见 `ThreatRosterFactory`），
        /// 存 Pawn 引用就是存一个死对象（还会把 pawn 写进存档）；`ThingDef.uiIcon` 足够表达类别。
        ///
        /// 为什么是 `string` 而不是 `ThingDef`：本文件与 `Source/Combat/Core` 下其余文件同属**纯计算核心**，
        /// 被 `Prototype` / `CombatLab` 两个离线工程用 `..\Source\Combat\Core\*.cs` 通配编进来，而那两个工程
        /// **刻意不引用 RimWorld / Unity**。核心一旦出现任何 `Verse` 类型，两个工程必然 CS0246（RIM-35 的现象）。
        /// 游戏侧适配层（`CombatSnapshotFactory`）只写 defName，取值侧（`DelegationThreatSummary`）用
        /// `DefDatabase&lt;ThingDef&gt;.GetNamedSilentFail` 还原 —— 核心因此**零游戏依赖**。
        /// </summary>
        public string IconDefName;

        /// <summary>true = 我方（车队成员），false = 敌方。</summary>
        public bool IsMine;

        // ── 生存 ────────────────────────────────────────────────────────

        /// <summary>参战时的耐久池。我方取真 pawn 的等价总量，敌方同理。</summary>
        public float MaxHealth = 100f;

        // ── 空间（§19.20）──────────────────────────────────────────────

        /// <summary>武器射程（格）。近战单位忽略此值（用 <c>scene.MeleeRange</c>）。</summary>
        public float Range = 25f;

        /// <summary>每回合可接近的格数。</summary>
        public float MoveSpeed = 4f;

        /// <summary>近战单位：有效射程恒为 <c>scene.MeleeRange</c>，且**不受地形射程惩罚**。</summary>
        public bool IsMelee;

        /// <summary>地形优势。没有优势的一方射程要乘 <c>scene.NoAdvantageRangeFactor</c>（近战除外）。</summary>
        public bool HasTerrainAdvantage;

        // ── 命中（按距离两点插值）──────────────────────────────────────

        /// <summary>距离 ≤ <c>scene.NearBand</c> 时的命中率。</summary>
        public float AccuracyNear = 0.75f;

        /// <summary>距离 ≥ 有效射程时的命中率。</summary>
        public float AccuracyFar = 0.4f;

        // ── 输出 ────────────────────────────────────────────────────────

        /// <summary>每个 250-tick 回合能打出几次（含连发折算）。</summary>
        public float ShotsPerRound = 1f;

        /// <summary>单次命中的基础伤害。</summary>
        public float DamagePerShot = 10f;

        /// <summary>破甲值，对应 vanilla 的 armorPenetration。</summary>
        public float ArmorPen;

        /// <summary>本单位承受该伤害类别时的护甲评级。</summary>
        public float ArmorRating;

        /// <summary>索敌用的"泛用输出"权重（命中 × 射速 × 伤害）。</summary>
        public float ThreatWeight;

        public ArmorCategory Category = ArmorCategory.Sharp;

        public CombatUnitSnapshot Clone()
        {
            return (CombatUnitSnapshot)MemberwiseClone();
        }

        public override string ToString()
        {
            return string.Format(
                "{0}{1} hp={2:0.#} range={3:0.#} move={4:0.#} acc={5:P0}→{6:P0} shots/r={7:0.##} dmg={8:0.#} ap={9:0.##} armor={10:0.##}{11}{12}",
                IsMine ? "我:" : "敌:", Name, MaxHealth, Range, MoveSpeed, AccuracyNear, AccuracyFar,
                ShotsPerRound, DamagePerShot, ArmorPen, ArmorRating,
                IsMelee ? " 近战" : "", HasTerrainAdvantage ? " 地形优势" : "");
        }
    }
}
