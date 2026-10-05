using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「锁定车队」的拦截点（S16）。
    ///
    /// 拦在 `Caravan_PathFollower.StartPath` —— **右键改道最终都会走这里**
    /// （世界地图的"前往"、路线规划器、别的 mod 指令都一样）。比去过滤 float menu 稳得多：
    /// 那里的 `FloatMenuOption.action` 是闭包，认不出"这一项会不会让车队移动"，
    /// 只能靠猜标签文字（多语言下必崩）。
    ///
    /// 判据全在 `RimDelegationCaravanState.ShouldBlockMove`：
    /// 锁定的 + **有在途委派**的 + 目的地不是它委派地点的，才拦。
    /// 「前往中」的计划不拦 —— 否则玩家点「取消计划」时车队自己都走不动。
    ///
    /// ⚠️ `Caravan_PathFollower.caravan` 是 **private** 字段 ⇒ 用 Harmony 的 `___caravan` 注入。
    /// </summary>
    [HarmonyPatch(typeof(Caravan_PathFollower), nameof(Caravan_PathFollower.StartPath))]
    public static class Patch_CaravanMoveLock
    {
        [HarmonyPrefix]
        public static bool Prefix(Caravan ___caravan, PlanetTile destTile)
        {
            if (!RimDelegationCaravanState.ShouldBlockMove(___caravan, destTile))
            {
                return true;
            }
            Messages.Message(
                ___caravan.LabelCap + " 已锁定：不会因为右键改道而离开委派地点（在车队按钮栏点「已锁定」可解除）。",
                MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}
