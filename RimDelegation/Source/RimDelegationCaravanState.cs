using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 车队**锁定状态**（S16 用户要求）：
    /// 「添加一个锁定按键，和定居 拆分这些按钮一起。当锁定的时候，右击其他的地图块不会进行移动，
    ///   以免误操作取消委派。默认是锁定。」
    ///
    /// 语义：**默认锁定** ⇒ 这里存的是"被显式**解锁**过的车队 ID"名单；
    /// 新车队 / 老存档 / 名单里没有的车队一律视为锁定。
    ///
    /// 为什么挂世界级存档组件：锁定是 per-caravan 状态，而 `Caravan` 是原版类、加不了字段；
    /// 用 `GameComponent` 才能既不 patch 又随存档走（与 `RimDelegationHistory` 同一套懒补建写法）。
    /// </summary>
    public class RimDelegationCaravanState : GameComponent
    {
        /// <summary>被显式**解锁**的车队 ID（名单之外的 = 锁定）。</summary>
        public List<int> unlockedCaravanIds = new List<int>();

        public RimDelegationCaravanState()
        {
        }

        public static RimDelegationCaravanState Get(bool createIfMissing = true)
        {
            if (Current.Game == null)
            {
                return null;
            }
            List<GameComponent> comps = Current.Game.components;
            if (comps != null)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    if (comps[i] is RimDelegationCaravanState found)
                    {
                        return found;
                    }
                }
            }
            if (!createIfMissing)
            {
                return null;
            }
            RimDelegationCaravanState created = new RimDelegationCaravanState();
            Current.Game.components.Add(created);
            DelegationUtility.LogVerbose("补建车队锁定状态组件（RimDelegationCaravanState）");
            return created;
        }

        /// <summary>该车队是否处于**锁定**（默认 true —— 没有记录就是锁着）。</summary>
        public static bool IsLocked(Caravan caravan)
        {
            if (caravan == null)
            {
                return false;
            }
            RimDelegationCaravanState st = Get(false);
            return st == null || !st.unlockedCaravanIds.Contains(caravan.ID);
        }

        /// <summary>锁定 ⇄ 解锁。</summary>
        public static void Toggle(Caravan caravan)
        {
            if (caravan == null)
            {
                return;
            }
            RimDelegationCaravanState st = Get();
            if (st == null)
            {
                return;
            }
            if (st.unlockedCaravanIds.Contains(caravan.ID))
            {
                st.unlockedCaravanIds.Remove(caravan.ID);
            }
            else
            {
                st.unlockedCaravanIds.Add(caravan.ID);
            }
        }

        /// <summary>
        /// 这次"设路径"该不该被拦。三个条件同时成立才拦：
        ///   ① 车队是锁定的；② 该车队**有在途委派**（只有这时"误操作"才有代价）；
        ///   ③ 目的地不是它委派的那个地点（放行"走到事件点"和原地重算路径）。
        ///
        /// 「前往中」的计划（`HasPlan`、还没开工）**不拦** —— 否则玩家点「取消计划」时
        /// 车队自己都走不动（那条路径是 CancelPlan 发起的）。
        /// </summary>
        public static bool ShouldBlockMove(Caravan caravan, PlanetTile destTile)
        {
            if (caravan == null || !IsLocked(caravan))
            {
                return false;
            }
            WorldObjectComp_Delegations comp = DelegationRegistry.For(caravan);
            Delegation d = comp?.active;
            if (d == null)
            {
                return false;
            }
            Site site = comp.Site;
            return site != null && destTile != site.Tile;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref unlockedCaravanIds, "unlockedCaravanIds", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && unlockedCaravanIds == null)
            {
                unlockedCaravanIds = new List<int>();
            }
        }
    }
}
