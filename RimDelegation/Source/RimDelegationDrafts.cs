using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 当前存档的**草稿表**（S18）—— 玩家已经点开、但还没「确认下达」的那几张表单。
    ///
    /// 为什么是 <see cref="GameComponent" />：`Game.FillComponents()` 会用
    /// `InstantiableDescendantsAndSelf()` 反射自动收集所有子类，本项目零 Harmony 的约束下
    /// 这是唯一省事的挂点（与 `RimDelegationHistory` / `RimDelegationCaravanState` 同一套写法）。
    ///
    /// 为什么**刻意不 Scribe**（用户拍板「草稿不进存档」）：
    ///   · 草稿就是"玩家正在填的一张表单"，与"关掉对话框就没了"是同一个心智；
    ///   · 组件在**读档时由原版新建**（`LookMode.Deep` 走无参构造），不写 ExposeData
    ///     就等于"读档即清空"，而且不会留下指向上一局 Pawn/Site 的悬空引用。
    /// 「前往中 / 进行中 / 历史」本来就有各自的存档，不受影响。
    /// </summary>
    public class RimDelegationDrafts : GameComponent
    {
        public List<DelegationDraft> drafts = new List<DelegationDraft>();

        /// <summary>⚠️ `GameComponent` 的构造函数是 protected 无参（`Activator.CreateInstance` 造实例），
        /// 所以**不能**写 `: base(game)`。见 `RimDelegationHistory` 的同类说明。</summary>
        public RimDelegationDrafts()
        {
        }

        public static RimDelegationDrafts Get(bool createIfMissing = true)
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
                    if (comps[i] is RimDelegationDrafts found)
                    {
                        return found;
                    }
                }
            }
            if (!createIfMissing)
            {
                return null;
            }
            RimDelegationDrafts created = new RimDelegationDrafts();
            Current.Game.components.Add(created);
            DelegationUtility.LogVerbose("补建委派草稿表（RimDelegationDrafts）");
            return created;
        }

        /// <summary>当前有几张待下达的草稿（主控台底栏计数与"要不要暂停游戏"都用它）。</summary>
        public static int Count()
        {
            RimDelegationDrafts store = Get(false);
            return store?.drafts?.Count ?? 0;
        }

        /// <summary>
        /// 找一张同 (车队, 地点, 委派) 的草稿 —— 顺手把**已经失效**的清掉
        /// （地点被采空销毁、车队没了、这条委派已经在别处开工了）。
        /// </summary>
        public DelegationDraft Find(Caravan caravan, Site site, DelegationDef def)
        {
            if (drafts == null || drafts.Count == 0)
            {
                return null;
            }
            DelegationDraft found = null;
            for (int i = drafts.Count - 1; i >= 0; i--)
            {
                DelegationDraft d = drafts[i];
                if (d == null || d.IsStale())
                {
                    drafts.RemoveAt(i);
                    continue;
                }
                if (found == null && d.caravan == caravan && d.site == site && d.def == def)
                {
                    found = d;
                }
            }
            return found;
        }

        public void Add(DelegationDraft draft)
        {
            if (draft == null)
            {
                return;
            }
            if (drafts == null)
            {
                drafts = new List<DelegationDraft>();
            }
            if (!drafts.Contains(draft))
            {
                drafts.Add(draft);
            }
        }

        public void Remove(DelegationDraft draft)
        {
            drafts?.Remove(draft);
        }

        /// <summary>把失效的草稿清一遍（主控台每次绘制时调一次，条数很少）。</summary>
        public void Prune()
        {
            if (drafts == null || drafts.Count == 0)
            {
                return;
            }
            for (int i = drafts.Count - 1; i >= 0; i--)
            {
                if (drafts[i] == null || drafts[i].IsStale())
                {
                    drafts.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// ⚠️ **刻意什么都不写**：草稿不进存档。
        /// 留着这个空实现是为了把"为什么没有 Scribe"钉在代码里 —— 它看起来像漏写，
        /// 而实际上"读档后草稿清空"正是用户拍板的语义。
        /// </summary>
        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                drafts = new List<DelegationDraft>();
            }
        }
    }
}
