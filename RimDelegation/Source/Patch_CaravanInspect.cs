using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 让远行队的信息栏在委派期间说人话：
    ///   · 把原版的「等待中。」换成「委派开采中。」
    ///   · 把商队级的「休息中。（使用N个睡袋）」换成【每个人】的工作状态：
    ///       Chisa 开采中。
    ///       Denia 休息中。（使用0个睡袋）
    ///
    /// 原版 Caravan.GetInspectString() 的相关片段（已反编译确认）：
    ///     ... pather.Moving ? ArrivalAction.ReportString : (visiting ? CaravanVisiting : "CaravanWaiting")   ← 等待中
    ///     ... if (!pather.MovingNow) { AppendLine(); Append(CaravanBedUtility.AppendUsingBedsLabel("CaravanResting", beds.GetUsedBedCount())); }
    /// 所以按行替换即可，不动其它任何行（旅行中/访问中/超重/补给耗尽等都保留原样）。
    /// </summary>
    [HarmonyPatch(typeof(Caravan), nameof(Caravan.GetInspectString))]
    public static class Patch_Caravan_GetInspectString
    {
        public static void Postfix(Caravan __instance, ref string __result)
        {
            if (__result.NullOrEmpty())
            {
                return;
            }
            WorldObjectComp_Delegations comp = DelegationRegistry.For(__instance);
            Delegation d = comp?.active;
            Site site = comp?.Site;
            if (d == null || site == null)
            {
                return;
            }

            string waiting = "CaravanWaiting".Translate().ToString();
            string resting = "CaravanResting".Translate().ToString();

            // 措辞按 worker 分键（采矿"开采中" / 搜刮"搜刮中" / 营救"营救中"）：
            // 写死一句"委派开采中"会让搜刮物资藏匿点的车队显示成"开采中"。
            string delegatingKey = DelegationWorker.StatusKey("RimDelegationCaravanDelegating",
                d.Worker?.StatusKeySuffix);

            string[] lines = __result.Split('\n');
            List<string> outLines = new List<string>(lines.Length + d.participants.Count + 1);
            bool replaced = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');

                // 「等待中。」→「委派开采中。」+ 逐人工作状态
                if (!replaced && line == waiting)
                {
                    outLines.Add(delegatingKey.Translate().ToString());
                    for (int j = 0; j < d.participants.Count; j++)
                    {
                        Pawn p = d.participants[j];
                        if (p != null)
                        {
                            outLines.Add(PawnWorkLine(p, d, site, __instance));
                        }
                    }
                    replaced = true;
                    continue;
                }

                // 商队级的「休息中。（使用N个睡袋）」由逐人行取代
                if (!resting.NullOrEmpty() && line.StartsWith(resting))
                {
                    continue;
                }

                outLines.Add(line);
            }

            if (replaced)
            {
                __result = string.Join("\n", outLines);
            }
        }

        private static string PawnWorkLine(Pawn p, Delegation d, Site site, Caravan caravan)
        {
            // 判据统一走 IsWorkTime（含紧急加班）：加班时人确实在干，这条行不该写"休息中"
            bool working = !p.Downed && !p.Dead && d.IsWorkTime(site, GenTicks.TicksAbs);
            if (working)
            {
                return DelegationWorker.StatusKey("RimDelegationPawnWorking", d.Worker?.StatusKeySuffix)
                    .Translate(p.LabelShortCap).ToString();
            }
            int beds = (caravan.beds != null && caravan.beds.GetBedUsedBy(p) != null) ? 1 : 0;
            return "RimDelegationPawnResting".Translate(p.LabelShortCap, beds).ToString();
        }
    }
}
