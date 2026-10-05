using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// RIM-5 满意度的**来源**。四个来源各自可开关、可配权重（设置页 + Def 双落点）。
    ///
    /// 与用户拍板的对应关系：
    ///   · 用户原话的三条来源 = 「最近一段时间的吃喝」「游戏难度」「远行时间」；
    ///   · 第四条 <see cref="WorkIntensity"/> = 「作业强度」，来自用户拍板 **2B**：
    ///     作业模式原来的 +3 / 0 / −4 / −6 心情**不删**，折算成满意度的一个来源。
    /// </summary>
    public enum DelegationSatisfactionSource
    {
        /// <summary>最近一段时间的吃喝（近 N 天每次吃饭的野外伙食心情均值）。</summary>
        Meals = 0,

        /// <summary>游戏难度（原版 `colonistMoodOffset`：和平 +10 … 冷酷 −10）。</summary>
        Difficulty = 1,

        /// <summary>远行时间（远行队**实际开始移动**那一刻起算的在外天数）。</summary>
        TravelTime = 2,

        /// <summary>作业强度（来自作业模式：轻松 +3 / 常规 0 / 加班 −4 / 全天候 −6）。</summary>
        WorkIntensity = 3,
    }

    /// <summary>
    /// 一条来源的配置（Def 与设置页共用一份结构：开关 + 权重）。
    /// 刻意做成顶层类（与原版 Def 集合元素的惯例一致，<see cref="DelegationFoodMoodEntry" /> 同理）。
    /// </summary>
    public class DelegationSatisfactionSourceEntry
    {
        /// <summary>是哪个来源。</summary>
        public DelegationSatisfactionSource source = DelegationSatisfactionSource.Meals;

        /// <summary>默认开关（设置页里玩家还能再关一次；两处都开才生效）。</summary>
        public bool enabled = true;

        /// <summary>权重。**允许负数**（例如想要"难度越高越有成就感"就把难度权重取负）。</summary>
        public float weight = 1f;
    }

    /// <summary>
    /// 满意度的曲线参数与默认权重（RIM-5，用户拍板 8A：数值落点 = 设置页 + Defs 双落点）。
    ///
    /// 分工（**这是本机制的配置契约**）：
    ///   · **Def 管曲线**：吃喝窗口 / 远行宽限 / 强度跨度 / 效率幅度 / 心情幅度 / 每日记忆 Def，
    ///     以及四个来源（作业强度、远行时间…）的**默认**开关与权重；
    ///   · **设置页管玩家实际生效值**：总开关、每来源开关 + 权重、效率幅度、心情幅度；
    ///     点「恢复满意度默认值」即从本 Def 重新取值。
    ///   · 想看/改更细的浮点（设置页是档位按钮，不是滑条）：直接改
    ///     `Config\ModSettings\RimDelegationMod.xml` 里的对应键。
    /// </summary>
    public class DelegationSatisfactionDef : Def
    {
        /// <summary>满意度的每日记忆 Def（**多 stage**，按满意度档位取）。</summary>
        public ThoughtDef thought;

        /// <summary>「最近一段时间的吃喝」的回看窗口（天）。窗口内每次吃饭记一条。</summary>
        public float mealRecentDays = 3f;

        /// <summary>远行时间的**宽限**（天）：出门不超过这个天数时不影响满意度（子分 0.5）。</summary>
        public float travelGraceDays = 1f;

        /// <summary>从宽限结束起再走这么多天，远行时间的子分线性掉到 0。</summary>
        public float travelFullPenaltyDays = 9f;

        /// <summary>作业强度的跨度：子分 = 0.5 + 强度 / 跨度（默认 12 ⇒ ±6 正好到 0 / 1）。</summary>
        public float intensitySpan = 12f;

        /// <summary>效率幅度：满意度从 0.5 偏离到 0 / 1 时，作业速率系数 = 1 ∓ 这个值（默认 0.15）。</summary>
        public float efficiencyRange = 0.15f;

        /// <summary>心情幅度：每日记忆的心情值 × 这个系数（默认 1 = 直接用 Def 里的阶段值）。</summary>
        public float moodScale = 1f;

        /// <summary>四个来源的默认开关与权重（设置页的「恢复默认」从这里读）。</summary>
        public List<DelegationSatisfactionSourceEntry> sources = new List<DelegationSatisfactionSourceEntry>();

        /// <summary>某个来源的默认配置；没有配就返回一条内置默认（开、权重 1）。</summary>
        public DelegationSatisfactionSourceEntry EntryFor(DelegationSatisfactionSource source)
        {
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    DelegationSatisfactionSourceEntry e = sources[i];
                    if (e != null && e.source == source)
                    {
                        return e;
                    }
                }
            }
            return new DelegationSatisfactionSourceEntry { source = source, enabled = true, weight = 1f };
        }
    }

    /// <summary>
    /// 一次吃饭的记录（RIM-5「最近一段时间的吃喝」来源的滚动窗口）。
    ///
    /// 为什么需要它：现有的「野外伙食」只挂一条**当前这顿**的记忆（`Thought_Memory` 会刷新不叠加），
    /// 而满意度要的是"最近一段时间的吃喝"——那是一个**序列**，必须自己记。
    /// </summary>
    public class DelegationMealRecord : IExposable
    {
        /// <summary>吃下去那一刻（`GenTicks.TicksAbs`）。</summary>
        public int tickAbs;

        /// <summary>`DelegationFoodMoodDef.StageFor(食物)` 给出的阶段（决定这顿值多少心情）。</summary>
        public int stage;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tickAbs, "tickAbs", 0);
            Scribe_Values.Look(ref stage, "stage", 0);
        }
    }

    /// <summary>
    /// RIM-5「满意度」的**唯一计算收口**（全部是静态函数，没有任何状态）。
    ///
    /// 一句话：满意度 `S ∈ [0,1]`（0.5 = 中性）由四个来源加权平均得到，
    /// 它同时给出**每日心情**（多 stage 记忆，沿用户拍板 6A 的 memory thought 路线）
    /// 与**作业速率系数**（喂给原来读 `mode.workRateMultiplier` 的那几处乘法）。
    ///
    /// ⚠️ 心情为什么必须是"记忆想法"：远行队里的 pawn 不跑 `Pawn.TickInterval`
    /// （`Caravan_NeedsTracker` 手写了 food/rest/joy 而没有 `Need_Mood` 分支），
    /// 情境想法（`ThoughtWorker`）在途不刷新 —— 只有 memory thought 写得进去、回图后兑现。
    /// 这条与 `<see cref="DelegationUtility.GrantThought"/>` 的既有做法同源。
    /// </summary>
    public static class DelegationSatisfaction
    {
        /// <summary>中性值：所有"不知道 / 无影响"的来源都取它。</summary>
        public const float Neutral = 0.5f;

        /// <summary>每日记忆的 stage 数固定按 Def 里 ThoughtDef 的 stage 数走；这里只兜底一个值给缺 Def 的情况。</summary>
        private const int FallbackStages = 5;

        private static DelegationSatisfactionDef cached;

        /// <summary>满意度 Def（全局只该有一条）。没有就返回 null —— 所有取值函数都会退回中性值。</summary>
        public static DelegationSatisfactionDef Def
        {
            get
            {
                // Def 在开发者模式下可能被热重载成新实例，所以每次都验一下 defName（与 FoodMoodDef 同款）
                if (cached != null && !cached.defName.NullOrEmpty())
                {
                    return cached;
                }
                List<DelegationSatisfactionDef> all = DefDatabase<DelegationSatisfactionDef>.AllDefsListForReading;
                if (all == null || all.Count == 0)
                {
                    return null;
                }
                cached = all[0];
                return cached;
            }
        }

        // ================================================================ 子分（0..1，0.5 = 中性）

        /// <summary>「最近一段时间的吃喝」：近 `mealRecentDays` 天每次吃饭的野外伙食心情均值 → 0..1。</summary>
        public static float MealValue(Delegation d)
        {
            if (d?.recentMeals == null || d.recentMeals.Count == 0)
            {
                return Neutral;
            }
            int nowAbs = GenTicks.TicksAbs;
            int window = (int)(MealRecentDays * Delegation.TicksPerHour * 24f);
            float moodSum = 0f;
            int n = 0;
            for (int i = 0; i < d.recentMeals.Count; i++)
            {
                DelegationMealRecord m = d.recentMeals[i];
                if (m == null || nowAbs - m.tickAbs > window)
                {
                    continue;
                }
                moodSum += MealMoodOf(m.stage);
                n++;
            }
            if (n == 0)
            {
                return Neutral;
            }
            // 野外伙食的阶段值是 −2 / 0 / +2 / +4 ⇒ (−2 → 0)、(0 → 0.333)、(+2 → 0.667)、(+4 → 1)
            return Mathf.Clamp01((moodSum / n + 2f) / 6f);
        }

        /// <summary>「游戏难度」：原版 `colonistMoodOffset`（和平 +10 … 冷酷 −10）⇒ +10 → 1、−10 → 0。</summary>
        public static float DifficultyValue()
        {
            // ⚠️ 实测：`DifficultyDef.colonistMoodOffset` 是 **float**（编译期由 CS0266 确认过），
            //    不是 int —— 别照文档表格里的整数写成 int。
            float offset;
            try
            {
                offset = Find.Storyteller?.difficulty?.colonistMoodOffset ?? 0f;
            }
            catch (Exception)
            {
                // 世界还没建好 / 某些测试场景下取不到难度：按中性处理，绝不抛给调用方
                return Neutral;
            }
            return Mathf.Clamp01((offset + 10f) / 20f);
        }

        /// <summary>「远行时间」：宽限内 = 中性；超出后线性掉到 0。</summary>
        public static float TravelValue(float daysAway)
        {
            DelegationSatisfactionDef def = Def;
            float grace = def?.travelGraceDays ?? 1f;
            float full = def?.travelFullPenaltyDays ?? 9f;
            if (full < 0.01f)
            {
                full = 0.01f;
            }
            float t = Mathf.Clamp01((daysAway - grace) / full);
            return Neutral * (1f - t);
        }

        /// <summary>「作业强度」：0.5 + 强度 / 跨度（默认跨度 12 ⇒ +3 → 0.75、0 → 0.5、−4 → 0.167、−6 → 0）。</summary>
        public static float IntensityValue(DelegationModeDef mode)
        {
            float span = Def?.intensitySpan ?? 12f;
            if (span < 0.01f)
            {
                span = 12f;
            }
            return Mathf.Clamp01(Neutral + (mode?.workIntensity ?? 0f) / span);
        }

        // ================================================================ 汇总

        /// <summary>在途委派的满意度（只读 <see cref="Delegation.satisfaction" /> 之前先调 <see cref="Delegation.RefreshSatisfaction" />）。</summary>
        public static float Value(Delegation d)
        {
            return Compose(MealValue(d), DifficultyValue(), TravelValue(d?.DaysAway ?? 0f), IntensityValue(d?.mode));
        }

        /// <summary>
        /// 估算用的满意度（草稿 / 前往中计划：还没有 <see cref="Delegation" /> 实例）。
        /// 吃喝取不到 ⇒ 中性；远行时间由调用方给出"已经/预计在外几天"。
        /// </summary>
        public static float EstimatedValue(DelegationModeDef mode, float daysAway)
        {
            return Compose(Neutral, DifficultyValue(), TravelValue(daysAway), IntensityValue(mode));
        }

        /// <summary>加权平均（只算"开着的、权重非 0"的来源；权重和接近 0 ⇒ 中性）。</summary>
        public static float Compose(float meals, float difficulty, float travel, float intensity)
        {
            float sum = 0f;
            float wsum = 0f;
            Accumulate(DelegationSatisfactionSource.Meals, meals, ref sum, ref wsum);
            Accumulate(DelegationSatisfactionSource.Difficulty, difficulty, ref sum, ref wsum);
            Accumulate(DelegationSatisfactionSource.TravelTime, travel, ref sum, ref wsum);
            Accumulate(DelegationSatisfactionSource.WorkIntensity, intensity, ref sum, ref wsum);
            if (wsum > -0.0001f && wsum < 0.0001f)
            {
                return Neutral;
            }
            return Mathf.Clamp01(sum / wsum);
        }

        private static void Accumulate(DelegationSatisfactionSource source, float value,
            ref float sum, ref float weightSum)
        {
            if (!SourceEnabled(source))
            {
                return;
            }
            float w = SourceWeight(source);
            if (w > -0.0001f && w < 0.0001f)
            {
                return;
            }
            sum += w * value;
            weightSum += w;
        }

        // ================================================================ 产出：效率系数与心情

        /// <summary>作业速率系数：`1 + (S − 0.5) × 2 × 幅度`（默认幅度 0.15 ⇒ S=0 → ×0.85、S=1 → ×1.15）。</summary>
        public static float RateFactor(float satisfaction)
        {
            if (!Enabled)
            {
                return 1f;   // 总开关关掉 ⇒ 效率完全不受影响（不能只靠"满意度恒为 0.5"间接保证）
            }
            float range = RangeOfEfficiency;
            return 1f + (Mathf.Clamp01(satisfaction) - Neutral) * 2f * range;
        }

        /// <summary>满意度落在第几个 stage（按 ThoughtDef 的 stage 数均分）。</summary>
        public static int Stage(float satisfaction)
        {
            ThoughtDef thought = Def?.thought;
            int stages = thought?.stages?.Count ?? 0;
            if (stages <= 0)
            {
                stages = FallbackStages;
            }
            return Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(satisfaction) * stages), 0, stages - 1);
        }

        /// <summary>这一档的每日心情值（含心情幅度；没有 Def / 总开关关掉时返回 0）。</summary>
        public static float Mood(float satisfaction)
        {
            if (!Enabled)
            {
                return 0f;
            }
            float baseMood = MoodOfStage(Stage(satisfaction));
            return baseMood * MoodScale;
        }

        /// <summary>某一档的原始心情（Def 里写死的阶段值）。</summary>
        public static float MoodOfStage(int stage)
        {
            ThoughtDef thought = Def?.thought;
            if (thought?.stages == null || thought.stages.Count == 0)
            {
                return 0f;
            }
            int idx = Mathf.Clamp(stage, 0, thought.stages.Count - 1);
            return thought.stages[idx].baseMoodEffect;
        }

        /// <summary>满意度这一档的档位词（UI 用；没有 Def 时返回 null，UI 自己兜）。</summary>
        public static string StageLabel(float satisfaction)
        {
            ThoughtDef thought = Def?.thought;
            if (thought?.stages == null || thought.stages.Count == 0)
            {
                return null;
            }
            int idx = Mathf.Clamp(Stage(satisfaction), 0, thought.stages.Count - 1);
            return thought.stages[idx].label;
        }

        /// <summary>一次吃饭的价值（心情值），供「最近吃喝」的均值使用。</summary>
        public static float MealMoodOf(int stage)
        {
            DelegationFoodMoodDef cfg = DelegationUtility.FoodMoodDef();
            if (cfg != null)
            {
                return cfg.MoodOfStage(stage);
            }
            // 兜底映射与 RimDelegation_FoodMood.xml 的四个阶段一致（Def 缺失时也别算成 0）
            switch (stage)
            {
                case 0: return -2f;
                case 2: return 2f;
                case 3: return 4f;
                default: return 0f;
            }
        }

        // ================================================================ 明细（悬浮情报用）

        /// <summary>
        /// 满意度的**一个来源**的明细 —— 给 UI 的悬浮情报用（"显示影响的因素"）。
        /// 每一个字段都是**可显示的当前值**，不含任何计算。
        /// </summary>
        public class SatisfactionFactor
        {
            public DelegationSatisfactionSource source;

            /// <summary>来源名（玩家词，如「最近一段时间的吃喝」）。</summary>
            public string label;

            /// <summary>当前是否生效（设置页与 Def 两处都开才算）。</summary>
            public bool enabled;

            /// <summary>玩家生效权重（**可为负**）。</summary>
            public float weight;

            /// <summary>子分 0..1（0.5 = 中性）。</summary>
            public float value;

            /// <summary>一句话细节：数据从哪来、当前是什么值。</summary>
            public string detail;

            /// <summary>这一行的显示文本（UI 直接画，不要再自己拼）。</summary>
            public string Line()
            {
                string w = weight >= 0f ? weight.ToString("0.##") : weight.ToString("0.##");
                string head = string.Format("· {0} {1}", label, value.ToStringPercent());
                if (!enabled)
                {
                    return head + "（已关闭）";
                }
                return string.Format("{0}（权重 {1}）：{2}", head, w, detail);
            }
        }

        /// <summary>四个来源的名字（玩家词）。与 <see cref="DelegationSatisfactionSource" /> 一一对应。</summary>
        public static string LabelOf(DelegationSatisfactionSource source)
        {
            switch (source)
            {
                case DelegationSatisfactionSource.Difficulty: return "游戏难度";
                case DelegationSatisfactionSource.TravelTime: return "远行时间";
                case DelegationSatisfactionSource.WorkIntensity: return "作业强度";
                default: return "最近一段时间的吃喝";
            }
        }

        /// <summary>在途委派的四来源明细（UI 悬浮情报的**唯一来源**）。</summary>
        public static List<SatisfactionFactor> Factors(Delegation d)
        {
            return new List<SatisfactionFactor>
            {
                MealFactor(d),
                DifficultyFactor(),
                TravelFactor(d?.DaysAway ?? 0f),
                IntensityFactor(d?.mode),
            };
        }

        /// <summary>草稿 / 前往中计划的四来源明细（吃喝与在外天数按调用方给的估计值）。</summary>
        public static List<SatisfactionFactor> FactorsEstimated(DelegationModeDef mode, float daysAway)
        {
            SatisfactionFactor meals = MealFactor(null);
            meals.detail = "草稿阶段还没有吃饭记录 ⇒ 按中性 0.5 计";
            return new List<SatisfactionFactor>
            {
                meals,
                DifficultyFactor(),
                TravelFactor(daysAway),
                IntensityFactor(mode),
            };
        }

        /// <summary>明细的文本行（含汇总那一行），UI 直接逐行画。</summary>
        public static List<string> FactorLines(Delegation d)
        {
            return Lines(Factors(d), d?.satisfaction ?? Neutral);
        }

        public static List<string> FactorLinesEstimated(DelegationModeDef mode, float daysAway)
        {
            float s = EstimatedValue(mode, daysAway);
            return Lines(FactorsEstimated(mode, daysAway), s);
        }

        private static List<string> Lines(List<SatisfactionFactor> factors, float satisfaction)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < factors.Count; i++)
            {
                lines.Add(factors[i].Line());
            }
            lines.Add(string.Format("→ 满意度 {0}（{1}）：每天心情 {2:+0.#;-0.#;0}，作业速率 ×{3:0.000}",
                Mathf.Clamp01(satisfaction).ToStringPercent(),
                StageLabel(satisfaction) ?? "—",
                Mood(satisfaction),
                RateFactor(satisfaction)));
            return lines;
        }

        private static SatisfactionFactor MealFactor(Delegation d)
        {
            DelegationSatisfactionDef def = Def;
            float days = def?.mealRecentDays ?? 3f;
            SatisfactionFactor f = new SatisfactionFactor
            {
                source = DelegationSatisfactionSource.Meals,
                label = LabelOf(DelegationSatisfactionSource.Meals),
                enabled = SourceEnabled(DelegationSatisfactionSource.Meals),
                weight = SourceWeight(DelegationSatisfactionSource.Meals),
                value = MealValue(d),
            };
            int count = 0;
            float moodSum = 0f;
            if (d?.recentMeals != null)
            {
                int nowAbs = GenTicks.TicksAbs;
                int window = (int)(days * Delegation.TicksPerHour * 24f);
                for (int i = 0; i < d.recentMeals.Count; i++)
                {
                    DelegationMealRecord m = d.recentMeals[i];
                    if (m == null || nowAbs - m.tickAbs > window)
                    {
                        continue;
                    }
                    moodSum += MealMoodOf(m.stage);
                    count++;
                }
            }
            f.detail = count == 0
                ? string.Format("近 {0:0.#} 天没有吃饭记录 ⇒ 中性", days)
                : string.Format("近 {0:0.#} 天 {1} 顿，平均 {2:+0.#;-0.#;0}（−2 干粮 … +4 很好）",
                    days, count, moodSum / count);
            return f;
        }

        private static SatisfactionFactor DifficultyFactor()
        {
            SatisfactionFactor f = new SatisfactionFactor
            {
                source = DelegationSatisfactionSource.Difficulty,
                label = LabelOf(DelegationSatisfactionSource.Difficulty),
                enabled = SourceEnabled(DelegationSatisfactionSource.Difficulty),
                weight = SourceWeight(DelegationSatisfactionSource.Difficulty),
                value = DifficultyValue(),
            };
            float offset = 0f;
            string name = null;
            try
            {
                offset = Find.Storyteller?.difficulty?.colonistMoodOffset ?? 0f;
                // ⚠️ RIM-6/RIM-9 会话代修的一处编译阻断（CS1061）：`RimWorld.Difficulty` 上既没有
                //    `label` 也没有 `def` —— 难度名在它**兄弟**字段 `Storyteller.difficultyDef`（`DifficultyDef.label`）上
                //    （反射实测：`Difficulty` 只有一堆系数，`Storyteller` = def / difficultyDef / difficulty）。
                name = Find.Storyteller?.difficultyDef?.label;
            }
            catch (Exception)
            {
                // 取不到就只报子分（与 DifficultyValue 的兜底一致）
            }
            f.detail = string.Format("{0}colonistMoodOffset {1:+0.#;-0.#;0}（和平 +10 … 冷酷 −10）",
                name.NullOrEmpty() ? "" : name + "：", offset);
            return f;
        }

        private static SatisfactionFactor TravelFactor(float daysAway)
        {
            DelegationSatisfactionDef def = Def;
            float grace = def?.travelGraceDays ?? 1f;
            float full = def?.travelFullPenaltyDays ?? 9f;
            SatisfactionFactor f = new SatisfactionFactor
            {
                source = DelegationSatisfactionSource.TravelTime,
                label = LabelOf(DelegationSatisfactionSource.TravelTime),
                enabled = SourceEnabled(DelegationSatisfactionSource.TravelTime),
                weight = SourceWeight(DelegationSatisfactionSource.TravelTime),
                value = TravelValue(daysAway),
            };
            f.detail = daysAway <= grace
                ? string.Format("在外 {0:0.#} 天（{1:0.#} 天宽限内 ⇒ 中性）", daysAway, grace)
                : string.Format("在外 {0:0.#} 天：超过宽限 {1:0.#} 天后，再用 {2:0.#} 天线性掉到 0",
                    daysAway, grace, full);
            return f;
        }

        private static SatisfactionFactor IntensityFactor(DelegationModeDef mode)
        {
            SatisfactionFactor f = new SatisfactionFactor
            {
                source = DelegationSatisfactionSource.WorkIntensity,
                label = LabelOf(DelegationSatisfactionSource.WorkIntensity),
                enabled = SourceEnabled(DelegationSatisfactionSource.WorkIntensity),
                weight = SourceWeight(DelegationSatisfactionSource.WorkIntensity),
                value = IntensityValue(mode),
            };
            f.detail = mode == null
                ? "还没选模式（按 0 计）"
                : string.Format("{0}：作业强度 {1:+0.#;-0.#;0}", mode.LabelCap, mode.workIntensity);
            return f;
        }

        // ================================================================ 设置 / Def 取值

        private static RimDelegationSettings S => RimDelegationMod.Settings;

        /// <summary>吃喝窗口（天）：Def 优先。</summary>
        public static float MealRecentDays => Def?.mealRecentDays ?? 3f;

        /// <summary>效率幅度：玩家在设置页的值优先，其次 Def。</summary>
        public static float RangeOfEfficiency
        {
            get
            {
                RimDelegationSettings s = S;
                if (s != null && s.satisfactionEnabled)
                {
                    return Mathf.Clamp(s.satisfactionEfficiencyRange, 0f, 0.6f);
                }
                return Def?.efficiencyRange ?? 0.15f;
            }
        }

        /// <summary>心情幅度：玩家在设置页的值优先，其次 Def。</summary>
        public static float MoodScale
        {
            get
            {
                RimDelegationSettings s = S;
                if (s != null && s.satisfactionEnabled)
                {
                    return Mathf.Clamp(s.satisfactionMoodScale, 0f, 5f);
                }
                return Def?.moodScale ?? 1f;
            }
        }

        /// <summary>整个机制的总开关（关掉 ⇒ 满意度恒为中性、效率系数 1、不挂记忆）。</summary>
        public static bool Enabled => S?.satisfactionEnabled ?? true;

        /// <summary>这个来源是否生效：**设置页与 Def 两处都开**才算开。</summary>
        public static bool SourceEnabled(DelegationSatisfactionSource source)
        {
            if (!Enabled)
            {
                return false;
            }
            RimDelegationSettings s = S;
            if (s != null && !s.SourceEnabled(source))
            {
                return false;
            }
            return Def?.EntryFor(source).enabled ?? true;
        }

        /// <summary>这个来源的权重：**设置页优先**（玩家实际生效值），其次 Def 默认。</summary>
        public static float SourceWeight(DelegationSatisfactionSource source)
        {
            RimDelegationSettings s = S;
            if (s != null)
            {
                return s.SourceWeight(source);
            }
            return Def?.EntryFor(source).weight ?? 1f;
        }
    }
}
