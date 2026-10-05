using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// "远行队 ↔ 正在进行的委派"的反向索引。
    ///
    /// 两个用途：
    ///   ① Harmony 补丁：判断某个人是不是正在工时段里干活（每 tick × 每人被调用，不能现查）
    ///   ② WITab_Caravan_Delegation：判断选中的远行队有没有委派（每帧被问一次）
    ///
    /// 每 tick 重建一次，**不保存任何跨存档状态**，所以没有"读档后静态注册表残留"的问题。
    /// </summary>
    public static class DelegationRegistry
    {
        private static readonly Dictionary<Pawn, WorldObjectComp_Delegations> workingPawns =
            new Dictionary<Pawn, WorldObjectComp_Delegations>();

        private static readonly Dictionary<Caravan, WorldObjectComp_Delegations> activeCaravans =
            new Dictionary<Caravan, WorldObjectComp_Delegations>();

        /// <summary>
        /// 参与者 → 委派。**不看工时、不看暂停**：吃饭、受伤这类"随时都会发生"的事情要用它。
        /// （`workingPawns` 回答的是另一个问题："此刻他算不算在干活"。）
        /// </summary>
        private static readonly Dictionary<Pawn, WorldObjectComp_Delegations> activePawns =
            new Dictionary<Pawn, WorldObjectComp_Delegations>();

        /// <summary>
        /// 本 tick 的所有"在进行中的委派"宿主（S8「委派」主控台窗口与底部按钮都要它）。
        ///
        /// ⚠️ 返回的是**内部缓存列表**（只读用、别改！）—— 这个查询会被底部按钮条**每帧**调用，
        /// 每次拷一份就是每帧一次分配。列表每 tick 重建，所以同一帧内多次取是同一个实例。
        /// </summary>
        private static readonly List<WorldObjectComp_Delegations> activeComps =
            new List<WorldObjectComp_Delegations>();

        /// <summary>
        /// 本 tick 的所有"在途计划"宿主（S12：远行队还在路上、抵达就会开工）。
        ///
        /// 与 <see cref="activeComps" /> 一样返回**内部缓存列表**（只读！），
        /// 每 tick 与 <see cref="AllActive" /> 同一次遍历里重建 —— 两者互斥：
        /// 一个宿主要么在进行中、要么在前往中（开工时 `ClearPlan` 会把它从这一列挪到那一列）。
        /// </summary>
        private static readonly List<WorldObjectComp_Delegations> plannedComps =
            new List<WorldObjectComp_Delegations>();

        private static int cachedTick = -1;

        /// <summary>该人此刻是否在某个"正处于工时段"的委派里；不是则返回 null。（补丁用）</summary>
        public static WorldObjectComp_Delegations WorkingDelegationFor(Pawn pawn)
        {
            if (pawn == null)
            {
                return null;
            }
            RefreshIfNeeded();
            return workingPawns.TryGetValue(pawn, out WorldObjectComp_Delegations comp) ? comp : null;
        }

        /// <summary>该人是某个正在进行委派的参与者吗；不是则返回 null。（吃饭等事件用，不看工时）</summary>
        public static WorldObjectComp_Delegations AnyActiveFor(Pawn pawn)
        {
            if (pawn == null)
            {
                return null;
            }
            RefreshIfNeeded();
            return activePawns.TryGetValue(pawn, out WorldObjectComp_Delegations comp) ? comp : null;
        }

        /// <summary>该远行队是否正在进行委派；不是则返回 null。（UI 用，不看工时）</summary>
        public static WorldObjectComp_Delegations For(Caravan caravan)
        {
            if (caravan == null)
            {
                return null;
            }
            RefreshIfNeeded();
            return activeCaravans.TryGetValue(caravan, out WorldObjectComp_Delegations comp) ? comp : null;
        }

        /// <summary>所有在途委派的宿主组件（只读！见 <see cref="activeComps" /> 的说明）。</summary>
        public static List<WorldObjectComp_Delegations> AllActive()
        {
            RefreshIfNeeded();
            return activeComps;
        }

        /// <summary>所有「前往中」计划的宿主组件（只读！S12）。</summary>
        public static List<WorldObjectComp_Delegations> AllPlanned()
        {
            RefreshIfNeeded();
            return plannedComps;
        }

        /// <summary>在途委派数量（底部按钮的灰态判据，每帧调用）。</summary>
        public static int ActiveCount()
        {
            RefreshIfNeeded();
            return activeComps.Count;
        }

        /// <summary>
        /// 在途委派里**进度最低**那一条的进度（0..1）—— 底部按钮自带的进度条
        /// （`MainButtonWorker.ButtonBarPercent`）用它：一眼看到"最拖后腿的那条"。
        /// </summary>
        public static float SlowestProgress()
        {
            RefreshIfNeeded();
            float min = 0f;
            bool any = false;
            for (int i = 0; i < activeComps.Count; i++)
            {
                Delegation d = activeComps[i].active;
                if (d == null)
                {
                    continue;
                }
                float p = d.Progress;
                if (!any || p < min)
                {
                    min = p;
                    any = true;
                }
            }
            return any ? min : 0f;
        }

        /// <summary>
        /// 所有在途委派里"还没被玩家看过"的事件条数（S15）—— 底部「委派」按钮的角标用它。
        ///
        /// 事件不再发信之后，这是"玩家不打开面板也能知道出了事"的唯一信号，
        /// 所以它必须便宜（一次 RefreshIfNeeded + 一个循环），也不需要任何新存档。
        /// </summary>
        public static int UnseenEventCount()
        {
            RefreshIfNeeded();
            int n = 0;
            for (int i = 0; i < activeComps.Count; i++)
            {
                Delegation d = activeComps[i].active;
                if (d != null)
                {
                    n += d.UnseenEventCount();
                }
            }
            return n;
        }

        /// <summary>打开主控台时调用：把在途委派的事件留痕全部标成"已看过"（角标清零）。</summary>
        public static void MarkAllEventsSeen()
        {
            RefreshIfNeeded();
            for (int i = 0; i < activeComps.Count; i++)
            {
                Delegation d = activeComps[i].active;
                if (d != null)
                {
                    d.MarkEventsSeen();
                }
            }
        }

        private static void RefreshIfNeeded()
        {
            // 这个索引原本只被补丁和页签调用，现在还会被 `Thing.Ingested` 的后缀调用 ——
            // 那条路径在非游戏状态下也可能被触发（世界生成、开发者调试），必须先挡住。
            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now == cachedTick)
            {
                return;
            }
            cachedTick = now;
            workingPawns.Clear();
            activeCaravans.Clear();
            activePawns.Clear();
            activeComps.Clear();
            plannedComps.Clear();

            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                if (!(all[i] is Site site) || site.Destroyed)
                {
                    continue;
                }
                WorldObjectComp_Delegations comp = site.GetComponent<WorldObjectComp_Delegations>();
                Delegation d = comp?.active;
                // S12：还没开工、但已经有人正在路上的地点 —— 「前往中」那一组用它
                if (comp != null && d == null && comp.HasPlan)
                {
                    plannedComps.Add(comp);
                }
                if (d == null)
                {
                    continue;
                }
                // 主控台窗口要列出**所有**在途委派，所以这一条放在 mode 检查之前
                activeComps.Add(comp);
                if (d.mode == null)
                {
                    continue;
                }

                if (d.caravan != null && !d.caravan.Destroyed)
                {
                    activeCaravans[d.caravan] = comp;
                }

                for (int j = 0; j < d.participants.Count; j++)
                {
                    Pawn p = d.participants[j];
                    if (p != null && !p.Dead)
                    {
                        activePawns[p] = comp;
                    }
                }

                // **暂停 / 停摆期间人是"在休息"，不是"在干活"** —— 必须从 workingPawns 里排除。
                // 否则休息补丁会继续拦着 Caravan_NeedsTracker.TrySatisfyRestNeed，
                // 休息条不会回升，"暂停委派让人休息"这件事就落空了（§19.24）。
                if (d.paused || d.IsStalled(GenTicks.TicksAbs))
                {
                    continue;
                }

                // 只有"算在干活"的时段才需要"禁止自动休息"。
                // 判据统一走 Delegation.IsWorkTime —— 它包含紧急加班（§19.26）：
                // 加班期间人确实在干，所以休息条必须照常下降（那正是加班的生理代价）。
                if (!d.IsWorkTime(site, GenTicks.TicksAbs))
                {
                    continue;
                }
                for (int j = 0; j < d.participants.Count; j++)
                {
                    Pawn p = d.participants[j];
                    if (p != null && !p.Dead)
                    {
                        workingPawns[p] = comp;
                    }
                }
            }
        }
    }
}
