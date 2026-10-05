using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派模式：**只决定作息**（工时窗口）+ 一个「作业强度」数值。
    ///
    /// RIM-5（2026-10-05）起职责收窄（用户拍板 1A + 2B）：
    ///   · **不再提供效率**：原来的 `workRateMultiplier`（×0.9 / ×1.0 / ×1.1）不再被任何地方读取
    ///     —— 效率改由「满意度」机制给（见 <see cref="DelegationSatisfaction" />）。
    ///     工时窗口**留在模式**：它是"作息定义"而不是增益（干得久的模式自然日产高）。
    ///   · **不再直接挂心情**：原来的 +3 / 0 / −4 / −6 折算成满意度的一个来源
    ///     「作业强度」（<see cref="workIntensity" />），由满意度统一产出心情。
    ///
    /// 判定完全基于原版的车队时钟语义（地方时 0-24），不依赖 Caravan.NightResting，
    /// 因为 NightResting 的 AnyPawnsNeedRest 只检查"有没有 rest 需求"，语义不适合当工时开关。
    /// </summary>
    public class DelegationModeDef : Def
    {
        /// <summary>开工小时（地方时，0-24）。</summary>
        public float startHour = 6f;

        /// <summary>收工小时（地方时，0-24）。</summary>
        public float endHour = 22f;

        /// <summary>
        /// 作业强度（RIM-5，满意度来源之一）：正数 = 轻松、负数 = 熬人。
        /// 四个模式取 +3 / 0 / −4 / −6（**就是原来那条每日心情的数值**，用户拍板 2B "折算成满意度来源"）。
        ///
        /// 子分算法 = `0.5 + workIntensity / intensitySpan`（跨度默认 12，见
        /// <see cref="DelegationSatisfactionDef.intensitySpan" />）⇒ +3 → 0.75、0 → 0.5、−4 → 0.167、−6 → 0。
        /// </summary>
        public float workIntensity;

        /// <summary>
        /// ⚠️ **已废弃（RIM-5）**：不再被任何地方读取，效率改由满意度给。
        /// 字段留着是为了**第三方 XML patch 不报"未知字段"错误**（原版会把未知节点记成 XML error）；
        /// ModBoot 会在启动自检里喊一次"有人在用废弃字段"，避免静默失效。
        /// </summary>
        public float workRateMultiplier = 1f;

        /// <summary>
        /// ⚠️ **已废弃（RIM-5）**：模式不再直接挂心情（折算成了 <see cref="workIntensity" />）。
        /// 与本 mod 的三个 `RimDelegation_Thought_Mode*` / `_DelegationOverwork` 一样，
        /// Def 定义**保留**只为旧存档里那条记忆还能解析；启动自检会对"还在用它"的 Def 喊一声。
        /// </summary>
        public ThoughtDef dailyMoodThought;

        /// <summary>断粮时是否立刻中断（false = 饿着也继续挖）。对话框里可覆盖。</summary>
        public bool abortWhenOutOfFood = true;

        public bool Is24h => startHour <= 0f && endHour >= 24f;

        /// <summary>此刻是否处于工时段（地方时判定）。</summary>
        public bool IsWorkingNow(PlanetTile tile, long ticksAbs)
        {
            if (Is24h)
            {
                return true;
            }
            float hour = GenDate.HourFloat(ticksAbs, Find.WorldGrid.LongLatOf(tile).x);
            if (startHour < endHour)
            {
                return hour >= startHour && hour < endHour;
            }
            // 跨午夜
            return hour >= startHour || hour < endHour;
        }

        /// <summary>一天里工时的占比（用于估算与显示）。</summary>
        public float WorkFractionPerDay
        {
            get
            {
                if (Is24h)
                {
                    return 1f;
                }
                float span = endHour - startHour;
                if (span < 0f)
                {
                    span += 24f;
                }
                return span / 24f;
            }
        }

        /// <summary>
        /// 距离下一次开工还有多少小时（地方时）。**已经在工时内**（或 24h 模式）返回 0。
        ///
        /// 用途（S14 用户要求）：休息时段里委派的进度条不动、`phaseTicks` 也不涨，
        /// 没有这个数玩家只会看到"什么都没发生"。
        /// </summary>
        public float HoursUntilStart(PlanetTile tile, long ticksAbs)
        {
            if (Is24h || IsWorkingNow(tile, ticksAbs))
            {
                return 0f;
            }
            float hour = GenDate.HourFloat(ticksAbs, Find.WorldGrid.LongLatOf(tile).x);
            float delta = startHour - hour;
            if (delta < 0f)
            {
                delta += 24f;   // 今晚收工了 ⇒ 等明天那个点数
            }
            return delta;
        }

        public string HoursLabel => Is24h
            ? "24h"
            : string.Format("{0:0.#}:00 - {1:0.#}:00", startHour, endHour);
    }
}
