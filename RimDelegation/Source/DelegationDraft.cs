using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一次「还没下达」的委派 —— **草稿**（S18）。
    ///
    /// 这个类是 S18「全入口收敛到主控台」的地基：把原先长在
    /// <see cref="Dialog_ChooseDelegation" /> 里的那 12 个可变字段（模式 / 姿态 / 结束条件 /
    /// 天数 / 配额 / 勾选的人 / 排序 / 补给中止）连同"怎么确认"一起搬出来，
    /// 让**两个壳**共用同一份状态：
    ///   · <see cref="Dialog_ChooseDelegation" />（640×780 模态壳，抵达时必须决定时仍用它）；
    ///   · <see cref="Window_Delegations" /> 的新建页（左栏「待下达」那一组）。
    ///
    /// 为什么字段一律 public：皮肤 mod（RimDelegation - Radius UI）要直接读写它们。
    /// 对话框时代它只能靠反射读 18 个私有字段（S18 已把那个反射桥连同对话框皮肤一起删掉），
    /// 草稿是新类 —— **没有历史包袱，就不该再复制一份反射**。
    ///
    /// 为什么草稿不进存档（用户拍板）：草稿就是"玩家正在填的一张表单"，
    /// 与"关掉对话框就没了"是同一个心智模型；「前往中」「进行中」「历史」本来就有存档。
    /// 存档后草稿清空，玩家重新点一次命令即可（见 <see cref="RimDelegationDrafts" />）。
    /// </summary>
    public class DelegationDraft
    {
        // ================================================================ 身份

        /// <summary>下给谁。</summary>
        public Caravan caravan;

        /// <summary>下到哪。</summary>
        public Site site;

        /// <summary>哪一条委派。</summary>
        public DelegationDef def;

        /// <summary>「确认下达」要做什么 —— 由**入口**决定（就地开工 / 挂到 pather 上 / 直接开工）。</summary>
        public Action<DelegationRequest> onConfirm;

        /// <summary>「延后决定」要做什么；null = 这个场合不提供延后（例如人已经站在点上）。</summary>
        public Action onDefer;

        // ================================================================ 选择状态（皮肤可直接读写）

        /// <summary>候选人（按 <see cref="sortMode" /> 排好序）。</summary>
        public readonly List<Pawn> candidates = new List<Pawn>();

        /// <summary>勾选中的人。</summary>
        public readonly HashSet<Pawn> selected = new HashSet<Pawn>();

        public DelegationModeDef mode;
        public DelegationApproachDef approach;
        public DelegationEndCondition endCondition = DelegationEndCondition.UntilDepleted;
        public int daysLimit = 5;
        public int quotaUnits = 400;
        public bool abortWhenOutOfFood = true;
        public PawnSortMode sortMode = PawnSortMode.SkillDesc;

        /// <summary>人员列表的滚动位置（挂在草稿上，两个壳来回切时不跳）。</summary>
        public Vector2 scroll;

        /// <summary>「预期获得」列表的滚动位置（与人员列表分开，否则拖一个另一个跟着动）。</summary>
        public Vector2 itemScroll;

        // ================================================================ 只读派生（随地点数据刷新）

        /// <summary>抵达前的范围预览。</summary>
        public DelegationPreview preview = new DelegationPreview();

        /// <summary>
        /// S24：「作战任务」段的数据缓存（草稿页与主列同构 ⇒ 也带一份）。
        /// 草稿是临时对象、不进存档，所以这份缓存只活在"这张表单打开着"的这段时间里。
        /// </summary>
        public DelegationThreatSummary threatSummary = new DelegationThreatSummary();

        /// <summary>抵达后地点上已掷定的存量；null = 还没到地方，只显示区间。</summary>
        public DelegationDeposit exactDeposit;

        public int dispMinCells;
        public int dispMaxCells;

        /// <summary>车队此刻是否就站在目标格上（决定「延后决定」有没有意义）。</summary>
        public bool onTile;

        /// <summary>
        /// 本草稿的 worker 实例（懒建、**一份**）。
        /// 所有文案（结束条件 / 量纲 / 补给勾选框）都要问它，一帧里散着 new 好几个只会浪费，
        /// 而且给"同一屏两处文案不一致"留缝。
        /// </summary>
        private DelegationWorker workerCache;

        public DelegationWorker Worker => workerCache ?? (workerCache = def?.CreateWorker());

        // ================================================================ 构造

        public DelegationDraft(Caravan caravan, Site site, DelegationDef def, DelegationRequest preRequest = null)
        {
            this.caravan = caravan;
            this.site = site;
            this.def = def;

            onTile = caravan != null && site != null && caravan.Tile == site.Tile;
            RefreshSiteData();

            DelegationWorker worker = Worker;
            List<Pawn> preferred = preRequest?.pawns;
            if (!preferred.NullOrEmpty())
            {
                for (int i = 0; i < preferred.Count; i++)
                {
                    if (preferred[i] != null && candidates.Contains(preferred[i]))
                    {
                        selected.Add(preferred[i]);
                    }
                }
            }
            if (selected.Count == 0)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    selected.Add(candidates[i]);
                }
            }

            mode = def.ResolveMode(preRequest?.mode);
            approach = def.ResolveApproach(preRequest?.approach);
            endCondition = preRequest?.endCondition ?? def.defaultEndCondition;
            daysLimit = Mathf.Clamp(preRequest?.daysLimit ?? 5, 1, Mathf.Max(1, def.maxDaysLimit));
            int quotaCap = QuotaCap();
            quotaUnits = Mathf.Clamp(preRequest?.quotaUnits ?? Mathf.Max(10, quotaCap / 2), 1, Mathf.Max(1, quotaCap));
            abortWhenOutOfFood = preRequest?.abortWhenOutOfFood ?? (mode?.abortWhenOutOfFood ?? true);
        }

        /// <summary>
        /// 重新读一遍地点/车队的数据。
        ///
        /// 为什么必须有它：草稿是**长命**的（可以开着窗挂很久，甚至复用同一条），
        /// 而存量、预览区间、可参加人员都会变（抵达时存量才掷定、队员可能受伤/离队）。
        /// 构造时与"复用已有草稿"时都走它，避免出现"配额上限按旧存量算"这种错。
        /// </summary>
        public void RefreshSiteData()
        {
            DelegationWorker worker = Worker;
            preview = worker?.MakePreview(site) ?? new DelegationPreview();
            exactDeposit = site != null ? site.GetComponent<WorldObjectComp_Delegations>()?.deposit : null;
            dispMinCells = exactDeposit != null ? exactDeposit.UnitsRemaining : preview.minUnits;
            dispMaxCells = exactDeposit != null ? exactDeposit.UnitsRemaining : preview.maxUnits;
            onTile = caravan != null && site != null && caravan.Tile == site.Tile;

            candidates.Clear();
            candidates.AddRange(DelegationUtility.EligiblePawns(caravan));
            SortCandidates();
            // 已经不在这支队伍里（或不能行动）的人要从勾选里剔掉，否则会下单给一个不在场的人
            selected.RemoveWhere(p => p == null || !candidates.Contains(p));
        }

        // ================================================================ 下达 / 放弃

        /// <summary>
        /// 建一条草稿、挂进当前存档的草稿表、并把主控台打开到它身上。
        ///
        /// 同 (车队, 地点, 委派) 三件套的草稿**只留一份**：玩家来回点同一个地点时，
        /// 期望的是"回到那张没写完的表单"，而不是叠出一张一模一样的空表。
        /// 复用时按最新一次的 <paramref name="onConfirm" /> / <paramref name="onDefer" /> 走
        /// （不同入口的"确认之后做什么"不一样），并刷新地点数据。
        /// </summary>
        public static DelegationDraft Begin(Caravan caravan, Site site, DelegationDef def,
            Action<DelegationRequest> onConfirm, Action onDefer = null, DelegationRequest preRequest = null)
        {
            if (caravan == null || caravan.Destroyed || site == null || site.Destroyed || def == null)
            {
                return null;
            }

            RimDelegationDrafts store = RimDelegationDrafts.Get(true);
            DelegationDraft reuse = store?.Find(caravan, site, def);
            if (reuse != null)
            {
                reuse.onConfirm = onConfirm;
                reuse.onDefer = onDefer;
                reuse.RefreshSiteData();
                Window_Delegations.EnsureOpen(reuse);
                return reuse;
            }

            DelegationDraft draft = new DelegationDraft(caravan, site, def, preRequest)
            {
                onConfirm = onConfirm,
                onDefer = onDefer
            };
            store?.Add(draft);
            Window_Delegations.EnsureOpen(draft);
            return draft;
        }

        /// <summary>
        /// **四条下达路径的唯一落点**（S18）：就地 gizmo / 右键·预先委派 / 右键·就地开工 / 抵达后决定。
        ///
        /// 默认走主控台草稿（<see cref="Begin" />）；Mod 设置里勾了「使用旧版委派对话框」则回到
        /// <see cref="Dialog_ChooseDelegation" /> 那个模态壳。把这个分支收在一处，
        /// 是为了让四个调用点长得一模一样 —— 将来再加第五条路径时不会漏掉逃生门的判断。
        /// </summary>
        public static void BeginOrDialog(Caravan caravan, Site site, DelegationDef def,
            Action<DelegationRequest> onConfirm, Action onDefer = null, DelegationRequest preRequest = null)
        {
            if (RimDelegationMod.Settings != null && RimDelegationMod.Settings.legacyDelegationDialog)
            {
                Find.WindowStack.Add(new Dialog_ChooseDelegation(caravan, site, def, onConfirm, preRequest, onDefer));
                return;
            }
            Begin(caravan, site, def, onConfirm, onDefer, preRequest);
        }

        /// <summary>组装要交给 <c>StartDelegation</c> 的下单内容。</summary>
        public DelegationRequest MakeRequest()
        {
            return new DelegationRequest(mode, ChosenList())
            {
                approach = approach,
                endCondition = endCondition,
                daysLimit = daysLimit,
                quotaUnits = quotaUnits,
                abortWhenOutOfFood = abortWhenOutOfFood
            };
        }

        /// <summary>人够了没有；不够时 <paramref name="reason" /> 给玩家看的原因。</summary>
        public bool CanConfirm(out string reason)
        {
            int need = Mathf.Max(1, def?.minPawns ?? 1);
            List<Pawn> chosen = ChosenList();
            if (chosen.Count < need)
            {
                reason = string.Format("至少需要 {0} 名可行动人员", need);
                return false;
            }
            if (site == null || site.Destroyed)
            {
                reason = "目标地点已经不存在了";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>「确认下达」：先把草稿摘掉，再回调入口给的动作。</summary>
        public void Confirm()
        {
            if (!CanConfirm(out string reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }
            DelegationRequest request = MakeRequest();
            Action<DelegationRequest> callback = onConfirm;
            Discard();
            callback?.Invoke(request);
        }

        /// <summary>「延后决定」：先让车队过去（不携带选择），抵达后再决定。</summary>
        public void Defer()
        {
            Action callback = onDefer;
            Discard();
            callback?.Invoke();
        }

        /// <summary>"这条草稿还成立吗" —— 地点没了、车队没了、或者这条委派已经不适用了。</summary>
        public bool IsStale()
        {
            if (def == null || site == null || site.Destroyed)
            {
                return true;
            }
            if (caravan == null || caravan.Destroyed)
            {
                return true;
            }
            // 地点已经开始/结束过这条委派了 ⇒ 这张表已经过期（例如玩家在别处把它下达了）
            WorldObjectComp_Delegations comp = site.GetComponent<WorldObjectComp_Delegations>();
            if (comp == null)
            {
                return true;
            }
            return comp.active != null || comp.Depleted;
        }

        /// <summary>从草稿表里摘掉自己（确认 / 延后 / 取消 三个出口的唯一落点）。</summary>
        public void Discard()
        {
            RimDelegationDrafts.Get(false)?.Remove(this);
        }

        // ================================================================ 派生值

        /// <summary>配额上限：本趟最多能取出的单位数。</summary>
        public int QuotaCap()
        {
            if (exactDeposit != null)
            {
                return Mathf.Max(1, Mathf.RoundToInt(exactDeposit.UnitsRemaining * exactDeposit.yieldPerUnit));
            }
            return Mathf.Max(1, preview.MaxYieldUnits);
        }

        /// <summary>按候选顺序取出已勾选的人。**每次返回新列表** —— 它会直接进 DelegationRequest。</summary>
        public List<Pawn> ChosenList()
        {
            List<Pawn> list = new List<Pawn>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (selected.Contains(candidates[i]))
                {
                    list.Add(candidates[i]);
                }
            }
            return list;
        }

        public void SortCandidates()
        {
            DelegationUIUtility.SortPawns(candidates, sortMode, def);
        }

        public string EndConditionLabel()
        {
            return DelegationUIUtility.EndConditionLabel(endCondition, daysLimit, quotaUnits, Worker);
        }

        public string ModeLine()
        {
            return DelegationUIUtility.ModeLine(mode);
        }

        /// <summary>
        /// 草稿的负重预测（S19）：主控台原版详情、皮肤右栏"概览"、皮肤草稿页的汇总行三处共用。
        ///
        /// 与在途那条 `DelegationUIUtility.TryMassForecast(d, site)` 的区别：在途有 `Delegation`，
        /// 草稿只有**预览**，所以"每进度单位装车质量"要按 MakePreview 给的 `massPerUnit` 推，
        /// 拿不到就退回采矿的旧算法（每格产出 × 矿物单位质量）。
        /// 返回值 false = 说不出产物的质量 ⇒ 调用方**不要**画那一行（画一行 0 kg 是骗人）。
        /// </summary>
        public bool TryMassForecast(out float now, out float minDelta, out float maxDelta, out float cap)
        {
            now = minDelta = maxDelta = cap = 0f;
            if (caravan == null)
            {
                return false;
            }
            float perUnit = preview?.massPerUnit ?? 0f;
            if (perUnit <= 0f && preview?.resourceDef?.building?.mineableThing != null)
            {
                perUnit = preview.yieldPerUnit * preview.resourceDef.building.mineableThing.BaseMass;
            }
            if (perUnit <= 0f)
            {
                return false;
            }
            now = caravan.MassUsage;
            cap = caravan.MassCapacity;
            minDelta = dispMinCells * perUnit;
            maxDelta = dispMaxCells * perUnit;
            return true;
        }

        public string ApproachLine()
        {
            return DelegationUIUtility.ApproachLabel(approach);
        }

        /// <summary>顶栏那一行：谁 → 哪 → 干什么（主控台用它，因为草稿脱离了"点进来的那个上下文"）。</summary>
        public string HeaderLine()
        {
            string caravanName = caravan != null ? caravan.Name : "?";
            string siteLabel = site != null ? site.Label : "?";
            return string.Format("{0}  →  {1}　·　{2}", caravanName, siteLabel, def?.label ?? "委派");
        }

        /// <summary>左栏那一行的副标题。</summary>
        public string SubLine()
        {
            return (def?.label ?? "委派") + " · 未下达";
        }

        // ================================================================ 三个下拉/菜单（照搬对话框）

        public void OpenEndConditionMenu()
        {
            DelegationWorker worker = Worker;
            List<FloatMenuOption> options = new List<FloatMenuOption>
            {
                new FloatMenuOption(Mark(DelegationEndCondition.UntilDepleted, worker.UntilDepletedLabel),
                    () => endCondition = DelegationEndCondition.UntilDepleted),
                new FloatMenuOption(Mark(DelegationEndCondition.Days, $"按天数（1–{Mathf.Max(1, def.maxDaysLimit)} 天）"),
                    () => endCondition = DelegationEndCondition.Days),
                new FloatMenuOption(Mark(DelegationEndCondition.Quota,
                        $"按产出配额（最多约 {QuotaCap()} {worker.OutputUnitName}）"),
                    () => endCondition = DelegationEndCondition.Quota)
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private string Mark(DelegationEndCondition condition, string label)
        {
            return (endCondition == condition ? "✓ " : "     ") + label;
        }

        public void OpenModeMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            if (def.modes.NullOrEmpty())
            {
                options.Add(new FloatMenuOption("（该委派没有定义模式）", null));
            }
            else
            {
                for (int i = 0; i < def.modes.Count; i++)
                {
                    DelegationModeDef local = def.modes[i];
                    // RIM-5（用户拍板 1A + 2B）：模式只报"作息窗口 + 作业强度"，
                    // 它给的心情与效率都由满意度产出 —— 所以这里直接把预计满意度摊给玩家看。
                    string label = string.Format("{0} · {1} · 作业强度 {2:+0.#;-0.#;0}",
                        local.LabelCap, local.HoursLabel, local.workIntensity);
                    label += "\n" + DelegationUIUtility.SatisfactionLineEstimated(local);
                    if (!local.description.NullOrEmpty())
                    {
                        label += "\n" + local.description;
                    }
                    options.Add(new FloatMenuOption(label, () => mode = local));
                }
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        public void OpenApproachMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            if (def.approaches.NullOrEmpty())
            {
                options.Add(new FloatMenuOption("（该委派没有定义作战姿态）", null));
            }
            else
            {
                for (int i = 0; i < def.approaches.Count; i++)
                {
                    DelegationApproachDef local = def.approaches[i];
                    string label = DelegationUIUtility.ApproachLabel(local);
                    if (!local.description.NullOrEmpty())
                    {
                        label += "\n" + local.description;
                    }
                    options.Add(new FloatMenuOption(label, () => approach = local));
                }
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
