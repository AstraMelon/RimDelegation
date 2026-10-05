using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派模式：决定工时窗口与速率系数。
    /// 判定完全基于原版的车队时钟语义（地方时 0-24），不依赖 Caravan.NightResting，
    /// 因为 NightResting 的 AnyPawnsNeedRest 只检查"有没有 rest 需求"，语义不适合当工时开关。
    /// </summary>
    public class DelegationModeDef : Def
    {
        /// <summary>开工小时（地方时，0-24）。</summary>
        public float startHour = 6f;

        /// <summary>收工小时（地方时，0-24）。</summary>
        public float endHour = 22f;

        /// <summary>速率系数（1 = 标准）。</summary>
        public float workRateMultiplier = 1f;

        /// <summary>这个模式每天额外给参与者挂的心情记忆（S4）。</summary>
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
