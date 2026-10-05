using System;
using System.Collections.Generic;

namespace RimDelegation.Combat
{
    /// <summary>撤退策略 —— 对应 DESIGN.md §19.9 的 RetreatPolicyDef。</summary>
    public enum RetreatPolicyKind
    {
        /// <summary>打到全灭。</summary>
        Never = 0,
        /// <summary>任一单位倒地。</summary>
        OnAnyDown = 1,
        /// <summary>任一单位阵亡。</summary>
        OnAnyDeath = 2,
        /// <summary>伤亡比例超过阈值（默认）。</summary>
        CasualtyFraction = 3,
    }

    /// <summary>目标选择策略 —— 对应 §19.5.1 的索敌规格。</summary>
    public enum TargetPriority
    {
        /// <summary>随机（全候选同分 ⇒ 蓄水池抽样给出等概率）。</summary>
        Random = 0,
        /// <summary>威胁最大优先（泛用输出最高）。</summary>
        Strongest = 1,
        /// <summary>最脆优先（剩余耐久比例最低）。</summary>
        Weakest = 2,
    }

    /// <summary>交战距离收缩到哪为止 —— 见 DESIGN.md §19.20.3。</summary>
    public enum ClosingPolicy
    {
        /// <summary>
        /// 收缩到**最短射程者**进入射程为止（默认）。近战会一路冲上去，
        /// 交火距离因此被拉到近身 —— 全队命中率随之提高。
        /// </summary>
        UntilShortestEngaged = 0,

        /// <summary>
        /// 只收缩到**远程单位**进入射程为止。近战单位因此永远够不着 ——
        /// 用于模拟"近战在开阔地冲不到人"的场合。
        /// </summary>
        UntilRangedEngaged = 1,

        /// <summary>不移动，全程停在初始距离。</summary>
        Never = 2,
    }

    /// <summary>
    /// 纵深策略 —— 决定"什么时候把距离拉回自己的有效射程"（§19.21.4）。
    /// </summary>
    public enum StandoffPolicy
    {
        /// <summary>
        /// **只有自己能压制对手射程时才后撤**（默认）。
        ///
        /// 判据：自己的有效射程 &gt; 敌方最短有效射程。只有这样后撤才能真的让对手够不着；
        /// 反之（被对手压制）后撤只是把自己推到命中率最低的位置 —— 实测会把胜率
        /// 从 8% 压到 0%（§19.21.4）。
        /// </summary>
        OnlyIfOutranging = 0,

        /// <summary>永远把自己维持在最大有效射程上（"风筝"战术）。</summary>
        ToMaxRange = 1,

        /// <summary>不前压也不后撤（等价于 <c>MaxStandoff = 0</c>）。</summary>
        Never = 2,
    }

    /// <summary>天气表的一项。命中率乘子来自 vanilla <c>WeatherDef.accuracyMultiplier</c>。</summary>
    public sealed class WeatherSample
    {
        public string Name;
        public float AccuracyMultiplier = 1f;
        public float Weight = 1f;

        public WeatherSample() { }

        public WeatherSample(string name, float accuracyMultiplier, float weight)
        {
            Name = name;
            AccuracyMultiplier = accuracyMultiplier;
            Weight = weight;
        }

        public override string ToString()
        {
            return string.Format("{0}×{1:0.##}@{2:0.#}", Name, AccuracyMultiplier, Weight);
        }
    }

    public sealed class RetreatPolicy
    {
        public RetreatPolicyKind Kind = RetreatPolicyKind.CasualtyFraction;

        /// <summary>仅 <see cref="RetreatPolicyKind.CasualtyFraction"/> 使用。</summary>
        public float CasualtyFraction = 0.34f;

        /// <summary>
        /// 默认 = <see cref="RetreatPolicyKind.CasualtyFraction"/>（伤亡约三分之一）。
        /// 这个默认值是原型跑出来的结论，前后改过两次，见 §19.17.3① 与 §19.9。
        /// </summary>
        public static RetreatPolicy Default() => new RetreatPolicy();

        public override string ToString()
        {
            return Kind == RetreatPolicyKind.CasualtyFraction
                ? string.Format("伤亡≥{0:P0}", CasualtyFraction)
                : Kind.ToString();
        }
    }

    /// <summary>
    /// 一场抽象战斗的全部输入。**纯数值**，无游戏类型依赖。
    ///
    /// 空间模型见 §19.20：战场被压成**一维距离**，双方从
    /// <see cref="StartDistance"/> 相互接近，直到进入各自有效射程才开始交火。
    /// </summary>
    public sealed class CombatScene
    {
        // ── 策略 ────────────────────────────────────────────────────────

        public TargetPriority OurPriority = TargetPriority.Strongest;
        public TargetPriority EnemyPriority = TargetPriority.Weakest;
        public RetreatPolicy Retreat = RetreatPolicy.Default();

        // ── 时间 ────────────────────────────────────────────────────────

        /// <summary>一个回合的时长（ticks）。250 ticks = 6 秒。</summary>
        public int TicksPerRound = 250;

        /// <summary>回合上限。240 回合 = 60000 ticks = 游戏内 1 天。</summary>
        public int MaxRounds = 240;

        // ── 空间（§19.20）──────────────────────────────────────────────

        /// <summary>
        /// 初始交战距离（格）。默认 40 —— **大于常见武器射程（约 25）**，
        /// 因此开局双方都够不着，必须先接近。
        /// </summary>
        public float StartDistance = 40f;

        /// <summary>近战的有效射程（格）。</summary>
        public float MeleeRange = 1.5f;

        /// <summary>命中率两点插值的近端距离：≤此距离用 AccuracyNear。</summary>
        public float NearBand = 8f;

        /// <summary>没有地形优势的单位，射程乘以此系数（近战除外）。0.75 ≈ 射程打七五折。</summary>
        public float NoAdvantageRangeFactor = 0.75f;

        public ClosingPolicy Closing = ClosingPolicy.UntilShortestEngaged;

        /// <summary>
        /// 后排最多能站多远（格，相对己方前线）。0 = 不允许纵深（退化为单标量模型）。
        ///
        /// 存在的理由：纵深让长射程单位保住自己的交战距离（§19.21），
        /// 但若无上限，狙击手可以永远站在敌人射程之外 —— 那是失真，不是战术。
        /// </summary>
        public float MaxStandoff = 12f;

        /// <summary>后撤速度系数（前进是全速）。后撤时是倒着走，比前进慢。</summary>
        public float FallbackFactor = 0.5f;

        /// <summary>纵深策略：什么时候后撤（§19.21.4）。</summary>
        public StandoffPolicy Standoff = StandoffPolicy.OnlyIfOutranging;

        /// <summary>
        /// 记录哪些类别的日志。预告的蒙特卡洛会把它设为 <see cref="CombatLogFilter.None"/>
        /// —— 跑 200 次还逐条拼字符串是纯浪费（§19.22）。
        /// </summary>
        public CombatLogFilter LogFilter = CombatLogFilter.Default;

        // ── 状态与伤害 ──────────────────────────────────────────────────

        /// <summary>倒地被判定的耐久比例阈值。</summary>
        public float DownHealthFraction = 0.25f;

        /// <summary>撤退"脱离接触"轮的输出折扣。</summary>
        public float DisengageFactor = 0.5f;

        /// <summary>
        /// **场景级**掩体通过率 —— 替代 vanilla 的 <c>PassCoverChance</c>。
        /// 双方对称，所以它主要影响**时间尺度**（拉长战斗）而非胜负；见 §19.16.8。
        ///
        /// 注意它和 <see cref="NoAdvantageRangeFactor"/>（地形优势）不是一回事：
        ///   • 本字段 = 整片战场都更难打中（天气/植被/烟尘），**对称**
        ///   • 地形优势 = 一方射程更长，**非对称**，能抢到先手
        /// </summary>
        public float CoverFactor = 1f;

        /// <summary>
        /// 空间优势损失的统一修正（§18.3 分流表的 abstractPenalty）。
        /// 作用在**敌方**输出上：&gt;1 表示敌方占地形便宜（哨所工事），
        /// &lt;1 表示敌方吃亏（休眠机械族被偷袭）。1 表示无修正。
        /// </summary>
        public float EnemyOutputFactor = 1f;

        // ── 天气（§19.20.4）────────────────────────────────────────────

        /// <summary>
        /// 天气表。**空表 = 恒为 1.0（无天气影响）**。
        ///
        /// 每场模拟开始时按权重**采样一次**，整场沿用 —— 而不是取期望值。
        /// 这样蒙特卡洛的分布里就自然包含了"今天起雾了"这种坏运气，
        /// P90 才有意义。数值取自 vanilla <c>WeatherDef.accuracyMultiplier</c>。
        /// </summary>
        public readonly List<WeatherSample> WeatherTable = new List<WeatherSample>();

        public readonly List<CombatUnitSnapshot> Units = new List<CombatUnitSnapshot>();

        public CombatScene Add(CombatUnitSnapshot unit)
        {
            if (unit != null) Units.Add(unit);
            return this;
        }

        public int CountMine()
        {
            int n = 0;
            for (int i = 0; i < Units.Count; i++) if (Units[i].IsMine) n++;
            return n;
        }

        public int CountEnemies()
        {
            int n = 0;
            for (int i = 0; i < Units.Count; i++) if (!Units[i].IsMine) n++;
            return n;
        }

        // ── 空间派生量（核心与 UI 共用，保证"展示的就是算的"）──────────

        /// <summary>有效射程：近战 = MeleeRange；无地形优势者乘 NoAdvantageRangeFactor。</summary>
        public float EffectiveRange(CombatUnitSnapshot u)
        {
            if (u == null) return 0f;
            if (u.IsMelee) return Math.Max(0f, MeleeRange);

            float r = u.Range;
            if (!u.HasTerrainAdvantage) r *= NoAdvantageRangeFactor;
            return Math.Max(0f, r);
        }

        /// <summary>给定距离下的命中率。envMultiplier = 掩体通过率 × 天气乘子（两者都是双方对称的）。</summary>
        public float HitChanceAt(CombatUnitSnapshot u, float distance, float envMultiplier)
        {
            if (u == null) return 0f;
            float eff = EffectiveRange(u);
            float t = eff <= NearBand ? 0f : CombatMath.Clamp01((distance - NearBand) / (eff - NearBand));
            float acc = u.AccuracyNear + (u.AccuracyFar - u.AccuracyNear) * t;
            return CombatMath.Clamp01(acc * envMultiplier);
        }

        /// <summary>本场的环境命中率乘子（掩体 × 天气）。实际模拟里天气是逐场采样的。</summary>
        public float EnvironmentMultiplier(float weatherMultiplier)
        {
            return Math.Max(0f, CoverFactor) * Math.Max(0f, weatherMultiplier);
        }

        /// <summary>天气表的期望乘子（用于展示；实际模拟是逐场采样）。</summary>
        public float ExpectedWeatherMultiplier()
        {
            if (WeatherTable.Count == 0) return 1f;
            float total = 0f, sum = 0f;
            for (int i = 0; i < WeatherTable.Count; i++)
            {
                float w = Math.Max(0f, WeatherTable[i].Weight);
                total += w;
                sum += w * WeatherTable[i].AccuracyMultiplier;
            }
            return total <= 0f ? 1f : sum / total;
        }

        /// <summary>交战距离收敛到的平衡点（§19.20.3）。</summary>
        public float EquilibriumDistance()
        {
            float best = float.MaxValue;
            bool any = false;

            for (int i = 0; i < Units.Count; i++)
            {
                CombatUnitSnapshot u = Units[i];
                if (u == null) continue;
                if (Closing == ClosingPolicy.UntilRangedEngaged && u.IsMelee) continue;
                float r = EffectiveRange(u);
                if (r < best) best = r;
                any = true;
            }

            // 全是近战（或全被策略排除）时退回按全体算，避免平衡点无意义
            if (!any)
            {
                for (int i = 0; i < Units.Count; i++)
                {
                    if (Units[i] == null) continue;
                    float r = EffectiveRange(Units[i]);
                    if (r < best) best = r;
                    any = true;
                }
            }
            return any ? best : 0f;
        }

        /// <summary>某方在给定距离下的推进速度（已进入射程的单位会停下开火）。</summary>
        public float SideAdvanceSpeed(bool mine, float distance)
        {
            float slowest = float.MaxValue;
            bool any = false;
            for (int i = 0; i < Units.Count; i++)
            {
                CombatUnitSnapshot u = Units[i];
                if (u == null || u.IsMine != mine) continue;
                if (EffectiveRange(u) >= distance) continue;      // 已在射程内，不推进
                if (u.MoveSpeed < slowest) slowest = u.MoveSpeed;
                any = true;
            }
            return any ? Math.Max(0f, slowest) : 0f;
        }

        /// <summary>
        /// 一对单位之间的攻击距离 = 战线间距 + 双方各自的纵深。
        ///
        /// **近战攻击者忽略目标纵深** —— 它会绕过前线直取后排。
        /// 本模型不模拟"拦截"（前线挡住冲过去的人），所以近战一旦贴上来就够得着所有人。
        /// 记为 `[未决]`，见 §19.21.6。
        /// </summary>
        public static float AttackDistance(float gap, CombatUnitSnapshot attacker, float attackerDepth, float targetDepth)
        {
            if (attacker != null && attacker.IsMelee) return gap + attackerDepth;
            return gap + attackerDepth + targetDepth;
        }

        /// <summary>
        /// 某单位在给定战线间距下的期望纵深。
        /// <paramref name="enemyMinRange"/> = 敌方最短有效射程（用于 <see cref="StandoffPolicy.OnlyIfOutranging"/>）。
        /// </summary>
        public float PreferredStandoff(CombatUnitSnapshot u, float gap, float enemyMinRange)
        {
            if (u == null) return 0f;
            if (Standoff == StandoffPolicy.Never) return 0f;

            float r = EffectiveRange(u);
            if (Standoff == StandoffPolicy.OnlyIfOutranging)
            {
                // 两个条件缺一不可：① 自己射程更长；② **后撤后确实能撤出对手射程**。
                // 只满足 ① 而后撤距离不够时，后撤纯粹是把自己推到命中率最低的位置。
                if (r <= enemyMinRange) return 0f;
                if (gap + Math.Max(0f, MaxStandoff) <= enemyMinRange) return 0f;
            }

            return CombatMath.Clamp(r - gap, 0f, Math.Max(0f, MaxStandoff));
        }

        /// <summary>敌方最短有效射程（用全体单位估算，供预览展示用）。</summary>
        public float MinEffectiveRange(bool mine)
        {
            float best = float.MaxValue;
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i] == null || Units[i].IsMine != mine) continue;
                float r = EffectiveRange(Units[i]);
                if (r < best) best = r;
            }
            return best == float.MaxValue ? 0f : best;
        }

        /// <summary>在平衡距离上估计每回合输出（用于预告面板展示）。</summary>
        public float ExpectedDamagePerRound(bool mine)
        {
            float gap = Math.Min(StartDistance, EquilibriumDistance());
            float enemyMin = MinEffectiveRange(!mine);
            float env = EnvironmentMultiplier(ExpectedWeatherMultiplier());
            float sum = 0f;
            for (int i = 0; i < Units.Count; i++)
            {
                CombatUnitSnapshot u = Units[i];
                if (u == null || u.IsMine != mine) continue;

                float depth = PreferredStandoff(u, gap, enemyMin);
                float dist = AttackDistance(gap, u, depth, 0f);   // 对敌方前线
                if (dist > EffectiveRange(u)) continue;           // 全程够不着
                float mult = u.IsMine ? 1f : EnemyOutputFactor;
                sum += HitChanceAt(u, dist, env) * u.ShotsPerRound * u.DamagePerShot * mult;
            }
            return sum;
        }

        public float ExpectedOurDamagePerRound() => ExpectedDamagePerRound(true);
        public float ExpectedEnemyDamagePerRound() => ExpectedDamagePerRound(false);

        public CombatScene Clone()
        {
            CombatScene c = new CombatScene
            {
                OurPriority = OurPriority,
                EnemyPriority = EnemyPriority,
                Retreat = new RetreatPolicy { Kind = Retreat.Kind, CasualtyFraction = Retreat.CasualtyFraction },
                TicksPerRound = TicksPerRound,
                MaxRounds = MaxRounds,
                StartDistance = StartDistance,
                MeleeRange = MeleeRange,
                NearBand = NearBand,
                NoAdvantageRangeFactor = NoAdvantageRangeFactor,
                Closing = Closing,
                MaxStandoff = MaxStandoff,
                FallbackFactor = FallbackFactor,
                Standoff = Standoff,
                LogFilter = LogFilter,
                CoverFactor = CoverFactor,
                DownHealthFraction = DownHealthFraction,
                DisengageFactor = DisengageFactor,
                EnemyOutputFactor = EnemyOutputFactor,
            };
            for (int i = 0; i < WeatherTable.Count; i++)
            {
                WeatherSample w = WeatherTable[i];
                c.WeatherTable.Add(new WeatherSample(w.Name, w.AccuracyMultiplier, w.Weight));
            }
            for (int i = 0; i < Units.Count; i++) c.Units.Add(Units[i].Clone());
            return c;
        }

        public void Validate()
        {
            if (CountMine() == 0) throw new InvalidOperationException("CombatScene 没有我方单位");
            if (CountEnemies() == 0) throw new InvalidOperationException("CombatScene 没有敌方单位");
            if (TicksPerRound <= 0) throw new InvalidOperationException("TicksPerRound 必须为正");
            if (MaxRounds <= 0) throw new InvalidOperationException("MaxRounds 必须为正");
            if (StartDistance < 0f) throw new InvalidOperationException("StartDistance 不能为负");
        }
    }
}
