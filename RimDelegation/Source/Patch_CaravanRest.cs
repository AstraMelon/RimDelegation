using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 让"正在委派作业的人"在工时段不再被车队自动补休息。
    ///
    /// 原版机制（已反编译确认）：
    ///   Need_Rest.TickResting(e) 只做一件事：lastRestTick = TicksGame; lastRestEffectiveness = e;
    ///   Need_Rest.NeedInterval()（每 150 ticks）：
    ///       if (Resting) CurLevel += 0.005714286f * e * RestRateMultiplier;            // 涨
    ///       else         CurLevel -= RestFallPerTick * 150f * RestFallRateFactor;      // 跌
    ///   而 Caravan_NeedsTracker.TrySatisfyRestNeed 在车队【没在移动】时每 tick 都戳一次 TickResting
    ///   ⇒ 停车作业期间 Resting 恒为真 ⇒ 休息条只涨不跌（这就是"人物一直在休息"的根因）。
    ///
    /// 修法：工时段直接跳过 TrySatisfyRestNeed，让 Resting 自然转假 → 休息条开始正常下降；
    ///       收工时段照常调用 → 夜里把休息睡回来。
    ///       低于 ExhaustionFloor 时不再拦，允许累垮后自己睡过去（避免真把人熬死）。
    /// </summary>
    [HarmonyPatch(typeof(Caravan_NeedsTracker), "TrySatisfyRestNeed")]
    public static class Patch_CaravanNeedsTracker_Rest
    {
        private const float ExhaustionFloor = 0.1f;

        public static bool Prefix(Pawn pawn, Need_Rest rest, int delta)
        {
            if (pawn == null || rest == null || pawn.Dead || pawn.Downed)
            {
                return true;
            }
            WorldObjectComp_Delegations comp = DelegationRegistry.WorkingDelegationFor(pawn);
            if (comp == null)
            {
                return true;
            }
            Delegation d = comp.active;
            Site site = comp.Site;
            if (d?.mode == null || site == null)
            {
                return true;
            }
            if (!d.IsWorkTime(site, GenTicks.TicksAbs))
            {
                return true; // 收工时段：照常休息
            }
            if (rest.CurLevel <= ExhaustionFloor)
            {
                return true; // 快撑不住了，放他们睡
            }
            return false;    // 工时段：不补休息
        }
    }
}
