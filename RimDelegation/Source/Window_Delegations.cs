using System;
using System.Collections.Generic;
using RimDelegationRadiusUI;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「委派」主控台：屏幕底部那个按钮打开的**浮动、可拖拽、可缩放**窗口（S8）。
    ///
    /// 为什么要有它（而不是只靠远行队页签）：页签只在"选中某支正在委派的远行队"时存在，
    /// 玩家想"一眼看全部在途委派"做不到。这里左边列出**所有**在途委派，右边是选中那条的详情。
    /// 布局参考 Radius UI - Quest Menu（左列表 + 右详情 + 顶部标题栏），
    /// 尺寸/位置记忆参考它的 `QuestMenuSettings`（`geomVersion` + NaN 清洗）——
    /// 引擎依据见 DESIGN §19.29。
    ///
    /// 与页签的分工（用户拍板 3A：保留页签 + 新增底部入口）：
    ///   · 页签：选中车队后的"随手看"，空间受限，靠折叠/截断兜底；
    ///   · 本窗口：全局主控台，空间由玩家自己拖，**详情区可滚动**。
    ///
    /// ⚠️ 详情区为什么会话滚动就能用 `Widgets.BeginScrollView`：因为内容总高是
    /// **纯函数算出来的**（<see cref="DetailPass" /> 先空跑一遍），不是靠
    /// `Event.current.type == Layout` 趟测 —— 后者才是"整页只剩标题"的真正原因
    /// （§19.28 的更正，证据：RadiusUI 的 `FlatScroll.Begin` 内部就是调这个原版 API）。
    /// </summary>
    public class Window_Delegations : Window
    {
        // 默认尺寸：S8-c 起是 1180×700（用户拍板，照 Radius UI - Quest Menu）；
        // **S22 改成 1490×760** —— 皮肤侧主控台多了第四栏「流程」，宽度账见
        // `DelegationConsoleSkin` 文件头的 FlowMaxW 注释与 Doc/流程栏与技能挂钩-方案评估.md §1：
        // 内容宽 C = winW − 60，四栏要「左 340 + 流程 300 + 主列 468 + 右 320 + 栏间 2 = 1430」。
        // 屏幕不够时窗口会被下面的 clamp 收窄，皮肤侧会自动回落到三栏（不会出现挤扁的栏）。
        private const float DefaultW = 1490f;
        private const float DefaultH = 760f;
        private const float MinW = 760f;
        private const float MinH = 420f;
        private const float TitleH = 34f;

        /// <summary>
        /// 左栏每条委派的行高。
        /// ⚠️ 三行内容（车队@地点 / 活动·状态·% / 进度条）必须按**真实行高 22** 排版，
        ///    之前写 56 高 + 18px 文字矩形 ⇒ 第二行被裁掉下半截（S8 实机反馈）。
        /// </summary>
        private const float ListRowH = 66f;
        private const float ListPadW = 8f;
        private const float LineH = 22f;
        private const float Gap = 6f;
        private const float ButtonH = 32f;

        private static readonly Color DangerTint = new Color(1f, 0.55f, 0.5f);

        /// <summary>窗口实例（`onlyOneOfTypeAllowed` 之外自己记一份，Toggle 要按引用关）。</summary>
        private static Window_Delegations instance;

        private WorldObjectComp_Delegations selected;

        /// <summary>S18：左栏「待下达」里选中的那张草稿（与 <see cref="selected" />、<see cref="selectedRecord" /> 三者互斥）。</summary>
        private DelegationDraft selectedDraft;

        /// <summary>S15 第二期：左栏「历史」里选中的那条记录（与 <see cref="selected" /> 互斥）。</summary>
        private DelegationRecord selectedRecord;

        /// <summary>
        /// 入口指定的焦点（S18）：`EnsureOpen(draft)` / `EnsureOpen(comp)` 把它写在这里，
        /// 下一次 <see cref="EnsureSelection" /> 优先服从它 —— 否则"点 Site 进主控台"会被
        /// "自动选第一个在途委派"的兜底逻辑抢走（那个兜底本来就是为了"打开就有东西看"）。
        /// </summary>
        private DelegationDraft pendingDraft;
        private WorldObjectComp_Delegations pendingComp;

        /// <summary>草稿表为空的场合复用一个空列表，别每帧 new。</summary>
        private static readonly List<DelegationDraft> NoDrafts = new List<DelegationDraft>();

        /// <summary>历史详情自己的滚动位置（与在途详情分开，来回切时不互相跳）。</summary>
        private Vector2 recordScroll = Vector2.zero;
        private Vector2 listScroll;
        private Vector2 detailScroll;

        /// <summary>参与者列表的排序（S9：用户要求参与者支持排序）。</summary>
        private PawnSortMode pawnSort = PawnSortMode.SkillDesc;

        /// <summary>现场物资列表的排序（S9：用户要求现场物资支持排序）。</summary>
        private ItemSortMode itemSort = ItemSortMode.ValueDesc;

        /// <summary>参与者行的显示用副本（复用，避免每帧每趟都 new 一个列表）。</summary>
        private readonly List<Pawn> displayPawns = new List<Pawn>();

        /// <summary>「队里还能参加、但没参加」的人（S13 参与者列表的后半段，同样复用）。</summary>
        private readonly List<Pawn> otherPawns = new List<Pawn>();

        // ================================================================ 开关

        public static void Toggle()
        {
            if (instance != null && Find.WindowStack.IsOpen(instance))
            {
                instance.Close(true);
                return;
            }
            EnsureOpen();
        }

        public static void EnsureOpen()
        {
            EnsureOpen(null, null);
        }

        /// <summary>
        /// 打开主控台并把焦点放到指定对象上（S18）。
        ///
        /// 三种调用姿势：
        ///   · <c>EnsureOpen()</c>                            —— 底部按钮：保持/自动选择；
        ///   · <c>EnsureOpen(draft)</c>                       —— 右键地点 / 就地 gizmo / 抵达后决定；
        ///   · <c>EnsureOpen(null, comp)</c>                  —— 远行队页签（引导页）跳到"这支车队那条委派"。
        /// </summary>
        public static void EnsureOpen(DelegationDraft focusDraft,
            WorldObjectComp_Delegations focusComp = null)
        {
            if (instance == null || !Find.WindowStack.IsOpen(instance))
            {
                instance = new Window_Delegations();
                Find.WindowStack.Add(instance);
            }
            if (focusDraft != null)
            {
                instance.pendingDraft = focusDraft;
                instance.pendingComp = null;
                // S18：草稿表单是照"对话框 780 高"排的（顶部描述 + 预期获得 + 结束条件 + 人员列表 +
                // 五行汇总 + 按钮行）。窗口被玩家拖矮过之后直接塞进去会互相压住 ——
                // 所以打开草稿时先把它撑到够高（不超屏幕），玩家仍可以自己再拖。
                const float DraftFriendlyH = 700f;
                float maxH = Mathf.Max(MinH, UI.screenHeight - 40f);
                if (instance.windowRect.height < Mathf.Min(DraftFriendlyH, maxH))
                {
                    instance.windowRect.height = Mathf.Min(DraftFriendlyH, maxH);
                }
            }
            else if (focusComp != null)
            {
                instance.pendingComp = focusComp;
                instance.pendingDraft = null;
            }
        }

        public static void CloseInstance()
        {
            if (instance != null && Find.WindowStack.IsOpen(instance))
            {
                instance.Close(true);
            }
        }

        /// <summary>
        /// 皮肤侧用（S18）：把入口指定的**草稿焦点**取走（取走即清空）。
        ///
        /// 为什么必须由皮肤自己来拿：RadiusUI 皮肤是**整个拦掉** `DoWindowContents` 的
        /// （前缀补丁返回 false），于是本类的 <see cref="EnsureSelection" /> 根本不会跑 ——
        /// 入口写下的 <c>pendingDraft</c> 就没人消费了。原版与皮肤各消费一次，
        /// 两边互斥（皮肤接管时原版这一帧不执行），谁也拿不到重复的焦点。
        /// </summary>
        public DelegationDraft ConsumePendingDraft()
        {
            DelegationDraft d = pendingDraft;
            pendingDraft = null;
            return d;
        }

        /// <summary>皮肤侧用（S18）：取走入口指定的在途 / 前往中焦点（取走即清空）。</summary>
        public WorldObjectComp_Delegations ConsumePendingComp()
        {
            WorldObjectComp_Delegations c = pendingComp;
            pendingComp = null;
            return c;
        }

        public Window_Delegations()
        {
            // 这一组是"浮动工作面板"该有的样子（照抄 Radius UI - Quest Menu 的构造）：
            // 可拖拽、可缩放、不吞窗口外的输入、不暂停游戏、不阻止拖动地图、走 GameUI 层。
            draggable = true;
            resizeable = true;
            doWindowBackground = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = false;
            layer = WindowLayer.GameUI;
            soundAppear = SoundDefOf.TabOpen;
            soundClose = SoundDefOf.TabClose;
        }

        public override Vector2 InitialSize
        {
            get
            {
                RimDelegationSettings s = RimDelegationMod.Settings;
                float w = (s != null && s.winW > 0f) ? s.winW : DefaultW;
                float h = (s != null && s.winH > 0f) ? s.winH : DefaultH;
                return new Vector2(Mathf.Clamp(w, MinW, UI.screenWidth - 16f),
                    Mathf.Clamp(h, MinH, UI.screenHeight - 40f));
            }
        }

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            RimDelegationSettings s = RimDelegationMod.Settings;
            if (s != null && s.winX >= 0f && s.winY >= 0f)
            {
                windowRect.x = Mathf.Clamp(s.winX, 0f, Mathf.Max(0f, UI.screenWidth - windowRect.width));
                windowRect.y = Mathf.Clamp(s.winY, 0f, Mathf.Max(0f, UI.screenHeight - windowRect.height));
            }
        }

        public override void PreClose()
        {
            base.PreClose();
            RimDelegationSettings s = RimDelegationMod.Settings;
            if (s != null)
            {
                s.winW = Mathf.Round(windowRect.width);
                s.winH = Mathf.Round(windowRect.height);
                s.winX = Mathf.Round(windowRect.x);
                s.winY = Mathf.Round(windowRect.y);
            }
            if (RimDelegationMod.Instance != null)
            {
                RimDelegationMod.Instance.WriteSettings();
            }
            if (instance == this)
            {
                instance = null;
            }
        }

        // ================================================================ 主绘制

        public override void DoWindowContents(Rect inRect)
        {
            // 玩家可以把窗口拖到任意大小：这里再兜一道下限（Radius 的 Quest Menu 同款），
            // 否则缩到很小之后下面的布局会算出一堆负宽度。
            if (windowRect.width < MinW)
            {
                windowRect.width = MinW;
            }
            if (windowRect.height < MinH)
            {
                windowRect.height = MinH;
            }

            // ── RIM-3（2026-10-05）：皮肤已并入本体，主控台默认走 Radius UI 画法 ──────────────
            // 这里是**直接调用**，不再用 Harmony 前缀补丁（合并前的 SkinPatch.cs 已整个删除）：
            // 同一个程序集里没必要为一处绘制入口付"反射找方法 + 打补丁"的代价，
            // 而且编译期就能保证 DelegationConsoleSkin 存在。
            // 皮肤不接管（反射拿不到 Verse.Window.windowDrawing）或抛异常时，落到下面这段原版画法 ——
            // 那是"界面绝不空窗"的最后一道，不是给玩家切着玩的两套皮（回退开关已按用户拍板移除）。
            if (DelegateToSkin(inRect))
            {
                return;
            }

            List<WorldObjectComp_Delegations> all = DelegationRegistry.AllActive();
            List<WorldObjectComp_Delegations> planned = DelegationRegistry.AllPlanned();
            // S15 第二期：历史（世界级组件 —— 跨委派、跨地点留存，采空销毁的地点也在里面）
            List<DelegationRecord> history = RimDelegationHistory.Get(false)?.records;
            // S18：待下达的草稿（内存态，不进存档）。顺手把失效的（地点没了 / 已经开工了）清掉。
            List<DelegationDraft> drafts = DraftList();
            EnsureSelection(all, planned, history, drafts);

            // S18：只要还有"待下达"的表单，主控台就把自己变成**暂停型** —— 那是"必须现在决定"
            // 的场合（引擎里 `WindowStack.WindowsForcePause` 是逐帧对全部窗口求和的，
            // 所以 `forcePause` 可以按状态来回切）。
            forcePause = drafts.Count > 0;

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            Rect titleRect = new Rect(inRect.x, inRect.y, inRect.width, TitleH);
            Widgets.Label(titleRect, string.Format("RimDelegationConsoleTitle".Translate(), all.Count));
            TooltipHandler.TipRegion(titleRect, "RimDelegationConsoleTitleTip".Translate());

            Rect body = new Rect(inRect.x, titleRect.yMax, inRect.width, inRect.height - TitleH);
            // S16：**删掉"整屏空态早退"** —— 用户要求"没有项目的时候也要显示"：
            // 左栏永远画（含常驻的「历史」组），右栏在没选中时给引导文案（见 DrawDetail）。

            float listW = Mathf.Min(360f, body.width * 0.36f);
            Rect listRect = new Rect(body.x, body.y, listW, body.height);
            Rect detailRect = new Rect(listRect.xMax + 1f, body.y, body.width - listW - 1f, body.height);
            Widgets.DrawBoxSolid(listRect, new Color(0f, 0f, 0f, 0.18f));
            Widgets.DrawBoxSolid(new Rect(listRect.xMax, body.y, 1f, body.height), new Color(1f, 1f, 1f, 0.08f));

            DrawList(listRect, all, planned, history, drafts);
            DrawDetail(detailRect);
        }

        /// <summary>
        /// 把这一帧交给 Radius UI 皮肤画。返回 true = 皮肤画完，调用方直接 return。
        ///
        /// 为什么还要在最外层套一层 try/catch（皮肤内部已经逐块兜了异常）：
        /// 皮肤是 4000+ 行绘制代码，万一在**最外层**抛出来（例如 GUI 组失衡、贴图加载失败），
        /// 不能让整个窗口消失 —— 落回原版画法，玩家至少还能看、还能操作委派。
        /// </summary>
        private bool DelegateToSkin(Rect inRect)
        {
            try
            {
                return DelegationConsoleSkin.Draw(this, inRect);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[RimDelegation] 皮肤：主控台绘制在最外层抛异常，本帧回落到原版画法。\n" + e, 0x5E0F5);
                return false;
            }
        }

        /// <summary>
        /// 当前这一帧的草稿表（只读用）。顺手清失效项 —— 主控台是唯一展示草稿的地方，
        /// 所以"过期草稿"这件事在这一处收拾就够了，不必再加一个每 tick 的钩子。
        /// </summary>
        private static List<DelegationDraft> DraftList()
        {
            RimDelegationDrafts store = RimDelegationDrafts.Get(false);
            if (store == null)
            {
                return NoDrafts;
            }
            store.Prune();
            return store.drafts ?? NoDrafts;
        }

        /// <summary>
        /// 「前往中」计划的详情（S12）：只读摘要 + 「取消计划」/「选中该远行队」。
        ///
        /// 这里刻意没有暂停/加班/改模式这些动作 —— 计划还没有 Delegation 实例，
        /// 那些状态在抵达开工后才有意义（要改计划就取消它、重新下达）。
        /// 「取消计划」的语义按用户拍板：**取消抵达动作，但让远行队继续走到那里**。
        /// </summary>
        private void DrawPlanDetail(Rect inner, WorldObjectComp_Delegations comp)
        {
            float y = inner.y;
            float w = inner.width;
            float headerH = LineH;

            // ---- ① 作战任务（S24，恒常）
            DelegationUIUtility.DrawSectionHeader(new Rect(inner.x, y, w, headerH),
                DelegationUIUtility.SectionCombat, true);
            y += headerH;
            DelegationThreatSummary ts = comp.threatSummary;
            if (ts != null)
            {
                // 计划期只给**事实**（守军/编队/能否评估），不推算成算 —— 计划还没开工，
                // 参与者也还没定下来（"延后决定"）。要推算就等开工后在主列点「重新推算」。
                ts.Ensure(comp.plannedCaravan, comp.Site,
                    comp.plannedRequest?.pawns, comp.plannedRequest?.approach, false);
                List<string> combat = ts.Lines();
                for (int i = 0; i < combat.Count; i++)
                {
                    float h = DelegationUIUtility.MeasuredHeight(combat[i], w, LineH);
                    Widgets.Label(new Rect(inner.x, y, w, h), combat[i]);
                    y += h;
                }
            }
            y += 4f;

            // ---- ② 收集任务（计划摘要：目标 / 模式 / 结束条件）
            DelegationUIUtility.DrawSectionHeader(new Rect(inner.x, y, w, headerH),
                DelegationUIUtility.SectionCollect, true);
            y += headerH;
            List<string> lines = DelegationUIUtility.PlanLines(comp);
            if (!lines.NullOrEmpty())
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    float h = DelegationUIUtility.MeasuredHeight(lines[i], w, LineH);
                    Widgets.Label(new Rect(inner.x, y, w, h), lines[i]);
                    y += h;
                }
            }
            y += 4f;

            // ---- ③ 远行队信息（补给；人员已由 PlanLines 的"参与人员"一行覆盖）
            DelegationUIUtility.DrawSectionHeader(new Rect(inner.x, y, w, headerH),
                DelegationUIUtility.SectionCaravan, true);
            y += headerH;
            string meal = DelegationUIUtility.MealLine(comp.plannedCaravan);
            if (!meal.NullOrEmpty())
            {
                float h = DelegationUIUtility.MeasuredHeight(meal, w, LineH);
                Widgets.Label(new Rect(inner.x, y, w, h), meal);
                y += h;
            }

            // 三颗按钮按可用宽度排成 1~2 行（最窄的右栏 ~370px，放不下三颗 152 + 间距）
            const float BW = 152f;
            const float BGap = 8f;
            bool hasResume = CanResumeDecision(comp);
            int count = hasResume ? 3 : 2;
            bool twoRows = count * (BW + BGap) - BGap > inner.width;
            int perRow = twoRows ? 2 : count;
            float baseY = inner.yMax - (twoRows ? ButtonH * 2f + 4f : ButtonH);
            int idx = 0;
            void Slot(out Rect rect)
            {
                int row = idx / perRow;
                int col = idx % perRow;
                rect = new Rect(inner.x + col * (BW + BGap), baseY + row * (ButtonH + 4f), BW, ButtonH);
                idx++;
            }

            Slot(out Rect cancelRect);
            Color old = GUI.color;
            GUI.color = DangerTint;
            if (Widgets.ButtonText(cancelRect, "取消计划"))
            {
                comp.CancelPlan();
            }
            GUI.color = old;
            TooltipHandler.TipRegion(cancelRect,
                "取消抵达动作：远行队仍会走到该地点，但抵达后不会开工。");

            // S18：人已经站在地点上、却还没开工的那条计划，多给一颗"把那次选择捡回来"
            if (hasResume)
            {
                Slot(out Rect resumeRect);
                if (Widgets.ButtonText(resumeRect, DelegationDraftUI.ResumeLabel))
                {
                    ResumeDecision(comp);
                }
                TooltipHandler.TipRegion(resumeRect,
                    "远行队已经到了，但那次选择还没做完（当初选了「延后决定」，或抵达时把窗口关了）。\n点这里把待下达的表单调出来。");
            }

            Slot(out Rect selectRect);
            if (Widgets.ButtonText(selectRect, "选中该远行队"))
            {
                if (comp.plannedCaravan != null && !comp.plannedCaravan.Destroyed)
                {
                    Find.WorldSelector.Select(comp.plannedCaravan, true);
                }
            }
        }

        /// <summary>
        /// 「前往中」这条能不能"继续决定"（S18）：人已经站在地点格上、委派还没开工、地点还有得取。
        ///
        /// 这就是原来那个洞：抵达时的选择被玩家关掉（或者当初选的是「延后决定」）之后，
        /// 抵达动作已经被原版 `Caravan_PathFollower.StopDead()` 连同 `arrivalAction` 一起清掉了，
        /// 界面上**再也没有任何入口**能把那次选择做完 —— 只能在地点上重新点一次"就地委派"
        /// （那是另一条路径，会重新建一张表单，玩家的原选择全丢）。
        ///
        /// public：RadiusUI 皮肤的「前往中」主列也要长这颗按钮（双端准则）。
        /// </summary>
        public static bool CanResumeDecision(WorldObjectComp_Delegations comp)
        {
            if (comp == null || comp.active != null || comp.Depleted || comp.plannedDef == null)
            {
                return false;
            }
            Caravan caravan = comp.plannedCaravan;
            Site site = comp.Site;
            return caravan != null && !caravan.Destroyed && site != null && !site.Destroyed
                   && caravan.Tile == site.Tile;
        }

        /// <summary>
        /// 把那次没做完的选择调回来：按原计划的 (车队, 地点, 委派) 重建一张「待下达」的表单。
        ///
        /// 两处照抄抵达路径：① 先把存量掷定（`EnsureDeposit`），表单里才会显示精确规模而不是区间；
        /// ② 确认后直接 `comp.StartDelegation`（原版抵达时也是走这一句）。
        /// 带上 `plannedRequest` 当预置值，玩家上次选过的模式/姿态/结束条件不会白丢。
        /// </summary>
        public static void ResumeDecision(WorldObjectComp_Delegations comp)
        {
            Caravan caravan = comp.plannedCaravan;
            Site site = comp.Site;
            DelegationDef def = comp.plannedDef;
            if (caravan == null || site == null || def == null)
            {
                return;
            }
            comp.EnsureDeposit(def);
            DelegationDraft.BeginOrDialog(caravan, site, def,
                request => comp.StartDelegation(caravan, def, request),
                preRequest: comp.plannedRequest);
        }

        private void EnsureSelection(List<WorldObjectComp_Delegations> all,
            List<WorldObjectComp_Delegations> planned, List<DelegationRecord> history,
            List<DelegationDraft> drafts)
        {
            // ---- ① 入口指定的焦点（S18）：先服从它，再谈别的
            if (pendingDraft != null)
            {
                DelegationDraft want = pendingDraft;
                pendingDraft = null;
                pendingComp = null;
                if (drafts.Contains(want))
                {
                    SelectDraft(want);
                    return;
                }
            }
            if (pendingComp != null)
            {
                WorldObjectComp_Delegations want = pendingComp;
                pendingComp = null;
                bool stillThere = (want.active != null && all.Contains(want))
                                  || (want.active == null && planned.Contains(want));
                if (stillThere)
                {
                    selected = want;
                    selectedDraft = null;
                    selectedRecord = null;
                    detailScroll = Vector2.zero;
                    return;
                }
            }

            // ---- ② 现有选择还成立吗
            // S15 第二期：选中的是历史记录时保持不变（除非它被 FIFO 挤掉了）
            if (selectedRecord != null)
            {
                if (history != null && history.Contains(selectedRecord))
                {
                    return;
                }
                selectedRecord = null;
            }
            // S18：选中的草稿被确认 / 取消 / 失效了
            if (selectedDraft != null)
            {
                if (drafts.Contains(selectedDraft) && !selectedDraft.IsStale())
                {
                    return;
                }
                selectedDraft = null;
            }
            if (selected != null)
            {
                bool stillThere = (selected.active != null && all.Contains(selected))
                                  || (selected.active == null && planned.Contains(selected));
                if (stillThere)
                {
                    return;
                }
            }

            // ---- ③ 自动挑一个"最需要玩家看一眼"的
            // 优先级：待下达（等人拍板） > 进行中 > 前往中。
            // 草稿排在最前是因为它**停在那里不动**：不确认就永远不下达，而进行中的任务自己会走。
            if (drafts.Count > 0)
            {
                SelectDraft(drafts[0]);
                return;
            }
            if (all.Count > 0)
            {
                selected = all[0];
            }
            else
            {
                selected = planned.Count > 0 ? planned[0] : null;
            }
        }

        private void SelectDraft(DelegationDraft draft)
        {
            selectedDraft = draft;
            selected = null;
            selectedRecord = null;
        }

        // ================================================================ 左栏：在途委派 / 在途计划

        private void DrawList(Rect r, List<WorldObjectComp_Delegations> all,
            List<WorldObjectComp_Delegations> planned, List<DelegationRecord> history,
            List<DelegationDraft> drafts)
        {
            Rect inner = r.ContractedBy(ListPadW);
            // S16：历史组**常驻**（哪怕 0 条）—— 只在有记录时才画的话，玩家会以为"这个功能不存在"
            //（用户原话：「没有处在委派任务的远行队的时候，看不到历史记录」）。
            int histCount = history?.Count ?? 0;
            int histRows = Mathf.Min(histCount, 20);
            // 标题 1 行 + 记录行；0 条时还要一行说明
            int histLines = 1 + (histRows > 0 ? histRows : 1);
            // S18：「待下达」那一组**只在有草稿时画**。理由与历史恰好相反 ——
            // 草稿一定是"玩家刚刚点出来的"，而且打开时会被自动聚焦；空着占位只会挤掉别人的位置。
            int draftLines = drafts.Count > 0 ? drafts.Count + 1 : 0;
            int total = draftLines + all.Count + (planned.Count > 0 ? planned.Count + 1 : 0) + histLines;
            Rect view = new Rect(0f, 0f, inner.width - 16f, total * ListRowH + 4f);
            // viewRect 高度是**直接算出来的**（行数 × 行高），不依赖 Layout 趟测高 ⇒ 安全
            Widgets.BeginScrollView(inner, ref listScroll, view);
            float y = 0f;
            // S18：生命线的第一段 —— 还没交给任何车队的表单
            if (drafts.Count > 0)
            {
                Widgets.Label(new Rect(0f, y, view.width, ListRowH * 0.6f),
                    string.Format("RimDelegationConsoleDrafts".Translate(), drafts.Count));
                y += ListRowH * 0.6f;
                for (int i = 0; i < drafts.Count; i++)
                {
                    DrawDraftRow(new Rect(0f, y, view.width, ListRowH), drafts[i]);
                    y += ListRowH;
                }
            }
            for (int i = 0; i < all.Count; i++)
            {
                DrawListRow(new Rect(0f, y, view.width, ListRowH), all[i]);
                y += ListRowH;
            }
            // S12：远行队还在路上的那一组（只读行 —— 计划还没有 Delegation 实例，
            //      点一行就把镜头挪到那支远行队，详情区显示计划摘要）
            if (planned.Count > 0)
            {
                Widgets.Label(new Rect(0f, y, view.width, ListRowH * 0.6f),
                    string.Format("前往中（{0}）", planned.Count));
                y += ListRowH * 0.6f;
                for (int i = 0; i < planned.Count; i++)
                {
                    DrawPlanRow(new Rect(0f, y, view.width, ListRowH), planned[i]);
                    y += ListRowH;
                }
            }
            // S16：历史那一组**常驻** —— 点一行，右栏就显示它的报告（与签核窗口同一份画法）。
            // 0 条时给一句说明，让玩家知道"不是没有这个功能，是还没有结束过委派"。
            Widgets.Label(new Rect(0f, y, view.width, ListRowH * 0.6f),
                string.Format("历史（{0}）", histCount));
            y += ListRowH * 0.6f;
            if (histRows == 0)
            {
                Color oldHist = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                Widgets.Label(new Rect(0f, y, view.width, ListRowH * 0.6f),
                    history == null ? "（还没有已结束的委派）" : "（还没有已结束的委派）");
                GUI.color = oldHist;
            }
            else
            {
                for (int i = 0; i < histRows; i++)
                {
                    DrawHistoryRow(new Rect(0f, y, view.width, ListRowH), history[i]);
                    y += ListRowH;
                }
            }
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 「待下达」一行（S18）：车队 → 地点 / 委派名 · 未下达。
        ///
        /// 这一行刻意不用进度条（草稿没有进度），改用暖色副标题 —— 与"进行中"的绿灰、
        /// "前往中"的灰在余光里就能分开。
        /// </summary>
        private void DrawDraftRow(Rect row, DelegationDraft draft)
        {
            if (draft == null)
            {
                return;
            }
            if (draft == selectedDraft)
            {
                Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.10f));
            }
            else if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            string caravanName = draft.caravan != null ? draft.caravan.Name : "?";
            string siteLabel = draft.site != null ? draft.site.Label : "?";
            Widgets.Label(new Rect(row.x + 8f, row.y + 4f, row.width - 16f, 22f),
                caravanName + "  →  " + siteLabel);

            Color old = GUI.color;
            GUI.color = new Color(1f, 0.85f, 0.55f);
            Widgets.Label(new Rect(row.x + 8f, row.y + 28f, row.width - 16f, 22f),
                (draft.def?.label ?? "委派") + " · 未下达" + (draft.onTile ? " · 就地开工" : ""));
            GUI.color = old;

            TooltipHandler.TipRegion(row, draft.HeaderLine() + "\n（点击选中这张表单；右栏底部可确认下达或取消）");
            if (Widgets.ButtonInvisible(row))
            {
                SelectDraft(draft);
                detailScroll = Vector2.zero;
            }
        }

        /// <summary>历史一行：标题（收工 / 中断）+ 结束时刻 · 时长 · 产出 · 事件条数。</summary>
        private void DrawHistoryRow(Rect row, DelegationRecord rec)
        {
            if (rec == null)
            {
                return;
            }
            if (rec == selectedRecord)
            {
                Widgets.DrawHighlightSelected(row);
            }
            else if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }
            Color old = GUI.color;
            if (rec.aborted)
            {
                GUI.color = new Color(1f, 0.76f, 0.72f);
            }
            Widgets.Label(new Rect(row.x + 4f, row.y + 4f, row.width - 8f, 22f), rec.Title);
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(row.x + 4f, row.y + 28f, row.width - 8f, 22f), rec.SubLine);
            GUI.color = old;
            TooltipHandler.TipRegion(row, rec.Title + "\n" + rec.SubLine + "\n（点击查看完整报告）");
            if (Widgets.ButtonInvisible(row))
            {
                selectedRecord = rec;
            }
        }

        private void DrawPlanRow(Rect row, WorldObjectComp_Delegations comp)
        {
            Caravan caravan = comp?.plannedCaravan;
            Site site = comp?.Site;
            if (caravan == null)
            {
                return;
            }
            bool isSel = comp == selected;
            if (isSel)
            {
                Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.10f));
            }
            else if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            Widgets.Label(new Rect(row.x + 8f, row.y + 4f, row.width - 16f, 22f),
                caravan.Name + "  →  " + (site?.Label ?? "?"));
            Color old = GUI.color;
            GUI.color = new Color(0.82f, 0.82f, 0.82f);
            Widgets.Label(new Rect(row.x + 8f, row.y + 28f, row.width - 16f, 22f),
                (comp.plannedDef?.label ?? "委派") + " · " + DelegationUIUtility.PlanStatusWord(comp));
            GUI.color = old;
            List<string> planLines = DelegationUIUtility.PlanLines(comp);
            if (!planLines.NullOrEmpty())
            {
                TooltipHandler.TipRegion(row, string.Join("\n", planLines.ToArray()));
            }

            if (Widgets.ButtonInvisible(row))
            {
                selected = comp;
                // S15 第二期：点在途行 = 取消历史选中（两个详情互斥）
                selectedRecord = null;
                detailScroll = Vector2.zero;
            }
        }

        private void DrawListRow(Rect row, WorldObjectComp_Delegations comp)
        {
            Delegation d = comp?.active;
            Site site = comp?.Site;
            if (d == null)
            {
                return;
            }
            bool isSel = comp == selected;
            if (isSel)
            {
                Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.10f));
            }
            else if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            string caravanName = d.caravan != null ? d.caravan.Name : "?";
            string siteLabel = site != null ? site.Label : "?";
            // 三行都按真实行高 22 排（见 ListRowH 的说明）
            Widgets.Label(new Rect(row.x + 8f, row.y + 4f, row.width - 16f, 22f), caravanName + "  @  " + siteLabel);

            string activity = d.Worker != null ? d.Worker.ActivityName : "?";
            Color old = GUI.color;
            GUI.color = new Color(0.82f, 0.82f, 0.82f);
            Widgets.Label(new Rect(row.x + 8f, row.y + 28f, row.width - 16f, 22f),
                activity + " · " + DelegationUIUtility.StatusWord(d, site) + " · " + d.Progress.ToStringPercent());
            GUI.color = old;

            Rect bar = new Rect(row.x + 8f, row.y + 54f, row.width - 16f, 8f);
            Widgets.FillableBar(bar, Mathf.Clamp01(d.Progress));

            if (Widgets.ButtonInvisible(row))
            {
                selected = comp;
                // S15 第二期：点在途行 = 取消历史选中（两个详情互斥）
                selectedRecord = null;
                detailScroll = Vector2.zero;
            }
        }

        // ================================================================ 右栏：详情

        private void DrawDetail(Rect r)
        {
            Rect inner = r.ContractedBy(10f);
            // S18：选中的是「待下达」的表单 → 右栏就是那张表单本身（与对话框**同一份画法**）
            if (selectedDraft != null)
            {
                DrawDraftDetail(inner, selectedDraft);
                return;
            }
            // S15 第二期：选中历史记录 → 右栏显示那份报告（与结束时的签核窗口**同一个画法**）
            if (selectedRecord != null)
            {
                DelegationReportUI.Draw(inner, selectedRecord.ToReportData(), ref recordScroll);
                return;
            }
            Delegation d = selected?.active;
            Site site = selected?.Site;
            if (d == null || site == null)
            {
                // S12：选中的是「前往中」的计划（还没有 Delegation 实例）—— 显示计划摘要 + 两个动作
                if (selected != null && selected.HasPlan)
                {
                    DrawPlanDetail(inner, selected);
                    return;
                }
                Widgets.Label(new Rect(inner.x, inner.y, inner.width, LineH), "RimDelegationConsoleNoSelection".Translate());
                // S16：没有任何在途项目时，右栏补上引导（左栏永远有「历史」组可看）
                if (selected == null)
                {
                    Widgets.Label(new Rect(inner.x, inner.y + LineH + 6f, inner.width, 30f),
                        "RimDelegationConsoleEmpty".Translate());
                    Widgets.Label(new Rect(inner.x, inner.y + LineH + 36f, inner.width, 56f),
                        "RimDelegationConsoleEmptyHint".Translate());
                    // S17：指一句"历史在哪"（与 Radius 皮肤版同一句，双端准则）
                    Widgets.Label(new Rect(inner.x, inner.y + LineH + 96f, inner.width, 30f),
                        "已结束的委派会留在左栏「历史」里，随时可以回看结案报告。");
                }
                return;
            }

            // S11：模式行与结束条件行**本身就是入口**，所以操作区只剩"动状态"这一排。
            // 宽够就一行（暂停/加班/结束加班 + 查看页签 + 中止），窄了就两行。
            int buttonRows = inner.width >= 700f ? 1 : 2;
            float buttonsH = ButtonH * buttonRows + (buttonRows - 1) * 4f;
            Rect buttonArea = new Rect(inner.x, inner.yMax - buttonsH, inner.width, buttonsH);
            Rect scrollArea = new Rect(inner.x, inner.y, inner.width, Mathf.Max(40f, inner.height - buttonsH - Gap));

            Caravan caravan = d.caravan;
            DelegationWorker worker = d.Worker;
            string unit = worker?.UnitName ?? "格";
            string outputUnit = worker?.OutputUnitName ?? "单位";

            // 两趟：先空跑量出内容总高（纯函数，与绘制同源），再真画。
            // 这就是"内容总高可算 ⇒ 原版滚动可用"的落地方式（§19.28 更正）。
            // ⚠️ 两趟的宽度必须**完全一致**（要减掉滚动条占的 16px），否则自动换行的行
            //    会量出两个高度，最后几行就会被裁掉。
            float viewW = scrollArea.width - 16f;
            float contentH = DetailPass(null, 0f, viewW, d, selected, site, caravan, worker, unit, outputUnit);
            Rect view = new Rect(0f, 0f, viewW, Mathf.Max(contentH, scrollArea.height));
            Widgets.BeginScrollView(scrollArea, ref detailScroll, view);
            DetailPass(view, 0f, viewW, d, selected, site, caravan, worker, unit, outputUnit);
            Widgets.EndScrollView();

            DrawActionButtons(buttonArea, d, selected, buttonRows);
        }

        /// <summary>
        /// 右栏的「待下达」页（S18）：整张表单 + 底栏三个出口。
        ///
        /// 与对话框的区别只有两点：① 顶部多一行"车队 → 地点"（草稿脱离了"点进来的那个上下文"，
        /// 必须自报家门）；② 按钮画在右栏底部，而不是窗口底部。
        /// 正文一行都不另写 —— 全走 `DelegationDraftUI.Draw`（所以窄列适配也只有一处）。
        /// </summary>
        private void DrawDraftDetail(Rect inner, DelegationDraft draft)
        {
            if (draft.def == null || draft.site == null)
            {
                return;
            }
            float infoSize = Widgets.InfoCardButtonSize;
            Widgets.Label(new Rect(inner.x, inner.y, Mathf.Max(80f, inner.width - infoSize - 6f), 26f),
                draft.HeaderLine());
            if (!draft.site.Destroyed)
            {
                Widgets.InfoCardButton(inner.x + inner.width - infoSize, inner.y + 1f, draft.site);
            }

            Rect body = new Rect(inner.x, inner.y + 28f, inner.width, Mathf.Max(140f, inner.height - 28f));
            // 出口按钮：宽够一行（确认 | 延后 | 取消），窄了两行。
            // 最窄的右栏约 370px，而三颗按钮 + 间距要 406px —— 所以"窄了换行"是必须的，不是可选优化。
            int rows = body.width >= 430f ? 1 : 2;
            float buttonsH = ButtonH * rows + (rows - 1) * 4f;
            DelegationDraftUI.Layout(body, buttonsH, 4f, out Rect content, out Rect buttons);
            DelegationDraftUI.Draw(draft, content);
            DrawDraftButtons(buttons, draft, rows);
        }

        /// <summary>
        /// 草稿的三个出口。语义与对话框一字不差（确认 / 延后 / 取消），只是排布按右栏宽度自适应。
        /// 「延后决定」在"人已经站在目标格上"时不提供（那时它等于取消）。
        /// </summary>
        private void DrawDraftButtons(Rect r, DelegationDraft draft, int rows)
        {
            const float ConfirmWidth = 150f;
            const float DeferWidth = 140f;
            const float CancelWidth = 100f;
            const float Gap = 8f;
            bool hasDefer = draft.onDefer != null && !draft.onTile;

            float confirmX = r.x;
            float confirmY = r.y;
            float deferX = r.x + ConfirmWidth + Gap;
            float deferY = r.y;
            float cancelX;
            float cancelY;
            if (rows >= 2)
            {
                deferX = r.x;
                deferY = r.y + ButtonH + 4f;
                cancelX = r.x + (hasDefer ? DeferWidth + Gap : 0f);
                cancelY = deferY;
            }
            else
            {
                cancelX = r.xMax - CancelWidth;
                cancelY = r.y;
            }

            if (Widgets.ButtonText(new Rect(confirmX, confirmY, ConfirmWidth, ButtonH), DelegationDraftUI.ConfirmLabel))
            {
                draft.Confirm();
            }
            if (hasDefer && Widgets.ButtonText(new Rect(deferX, deferY, DeferWidth, ButtonH), DelegationDraftUI.DeferLabel))
            {
                draft.Defer();
            }
            if (Widgets.ButtonText(new Rect(cancelX, cancelY, CancelWidth, ButtonH), DelegationDraftUI.CancelLabel))
            {
                draft.Discard();
            }
        }

        /// <summary>
        /// 详情区的**唯一一份**布局：<paramref name="view" /> 为 null 时只累加高度（量高趟）。
        ///
        /// 为什么用这种"两趟同一份代码"的写法：量高与绘制分成两个函数，早晚会有一处改漏
        /// （S8 的页签就是被"算高度的条件"和"真画的条件"分叉坑过）。这里连行间距都只写一次。
        /// 顺带：量高趟不碰任何 GUI，所以它同时也是"per-frame 只读"的。
        ///
        /// ⚠️ S9 起它是**实例方法**（不再是 static）：区里多了"排序"这种会改本窗口状态的按钮，
        ///    静态方法碰不到 <c>itemSort / pawnSort</c>。
        /// </summary>
        private float DetailPass(Rect? view, float y0, float w, Delegation d,
            WorldObjectComp_Delegations comp, Site site, Caravan caravan, DelegationWorker worker,
            string unit, string outputUnit)
        {
            bool draw = view.HasValue;
            float x = draw ? view.Value.x : 0f;
            float y = draw ? view.Value.y : y0;

            void Line(string text, float height = LineH)
            {
                if (draw && !text.NullOrEmpty())
                {
                    Widgets.Label(new Rect(x, y, w, height), text);
                }
                y += height;
            }

            // ---- 标题 + 地点信息卡
            float infoSize = Widgets.InfoCardButtonSize;
            if (draw)
            {
                Widgets.Label(new Rect(x, y, w - infoSize - 6f, 26f),
                    string.Format("RimDelegationTabTitle".Translate(), d.def?.label ?? "?", site.Label));
                Widgets.InfoCardButton(x + w - infoSize, y + 1f, site);
            }
            y += 28f;

            // ---- ① 作战任务（S24 恒常；S25 加：情报门控 / 编队图标 / 参战人员 / 待命两按钮）
            bool showCombat = DelegationUIUtility.DrawSectionHeader(
                new Rect(x, y, w, DelegationUIUtility.SectionHeaderH),
                DelegationUIUtility.SectionCombat, draw, DelegationUIUtility.SectionId.Combat);
            y += DelegationUIUtility.SectionHeaderH;
            if (showCombat)
            {
                DelegationThreatSummary ts = d.threatSummary;
                ts.flowLine = DelegationUIUtility.CombatPhaseLine(d);
                ts.revealed = DelegationUIUtility.ThreatRevealed(d);
                // 侦察一完成就**自动**算一次成算（用户 S25：「完成侦察任务后，先自动进行战斗评估」）；
                // 之后只有「重新推算」会再算。未揭露时传 false —— 没必要白跑 200 次蒙特卡洛。
                ts.Ensure(caravan, site, d.participants, d.approach, ts.revealed && ts.hasThreat, d.noCombatPawns);

                List<string> combatLines = ts.Lines();
                for (int i = 0; i < combatLines.Count; i++)
                {
                    // 两趟走同一个纯函数量高（同宽同结果），否则内容总高偏小、末尾被裁
                    float ch = DelegationUIUtility.MeasuredHeight(combatLines[i], w, LineH);
                    if (draw)
                    {
                        Widgets.Label(new Rect(x, y, w, ch), combatLines[i]);
                    }
                    y += ch;
                }

                // 编队（S25：带图标；S26：**未揭露就一行都不画** —— 之前这里只信注释、没真门控，
                // 于是"情报未明"那行下面照样列着海盗 ×7，用户第 4 条报的就是这个）
                if (ts.revealed)
                {
                    for (int i = 0; i < ts.roster.Count; i++)
                    {
                        DelegationThreatSummary.RosterEntry entry = ts.roster[i];
                        if (draw)
                        {
                            if (entry.icon != null)
                            {
                                Widgets.ThingIcon(new Rect(x + 4f, y + 1f, 18f, 18f), entry.icon,
                                    null, null, 1f, null, null, 1f);
                            }
                            Widgets.Label(new Rect(x + 26f, y, Mathf.Max(60f, w - 26f), LineH), entry.Short);
                        }
                        y += LineH;
                    }
                }

                // 参战人员（用户 S25：「作战任务里面单独添加参与人员（或动物）」；
                // S27：每行前面加一个**参战勾选框**，用户口径「作战任务里面添加一个勾选框，
                // 决定人物或动物是参加还是不参加作战」——与"参不参加委派"是两条轴）
                if (!ts.ourPawns.NullOrEmpty())
                {
                    string head = string.Format("参战人员（{0} 人参战 / 共 {1} 人）", ts.ourUnits, ts.ourPawns.Count);
                    float ph = DelegationUIUtility.MeasuredHeight(head, w, LineH);
                    if (draw)
                    {
                        Widgets.Label(new Rect(x, y, w, ph), head);
                    }
                    y += ph;
                    for (int i = 0; i < ts.ourPawns.Count; i++)
                    {
                        Pawn p = ts.ourPawns[i];
                        float rowH = DelegationUIUtility.RowHeight;
                        if (draw)
                        {
                            bool fight = d.FightsInCombat(p);
                            Rect pr = new Rect(x, y, w, rowH);
                            Rect toggle = new Rect(pr.x, pr.y + (rowH - 18f) * 0.5f, 18f, 18f);
                            Rect line = new Rect(pr.x + 24f, pr.y, pr.width - 24f, rowH);
                            Color oldRow = GUI.color;
                            if (!fight) GUI.color = new Color(1f, 1f, 1f, 0.62f);   // 不参战的整行压暗
                            DelegationUIUtility.DrawPawnLine(line, p,
                                DelegationUIUtility.PawnLine(p, worker, d.def));
                            GUI.color = oldRow;

                            Rect click = DelegationUIUtility.LabelRectFor(line);
                            TooltipHandler.TipRegion(click, fight
                                ? "点击：这一趟不参加作战（仍然参加委派里的作业）"
                                : "点击：让他/它参加作战");
                            bool clickLabel = Widgets.ButtonInvisible(click);
                            if (DelegationUIUtility.DrawParticipantToggle(toggle, fight, p, true) || clickLabel)
                            {
                                comp.ToggleCombatParticipant(d, p);
                            }
                        }
                        y += rowH;
                    }
                }

                // 待命：两颗决策按钮（用户 S25：「作战任务UI里面添加 进行交战 和 撤退选项」）
                if (d.AwaitingOrder)
                {
                    string signal = DelegationFlow.For(d.def).ActiveSignal(d.flow);
                    if (draw)
                    {
                        float halfW = (w - 6f) * 0.5f;
                        Color oldBtn = GUI.color;
                        // S27：一个参战的人都没有时禁用（点下去只会是"没人打 ⇒ 判负"）
                        bool canFight = ts.ourUnits > 0;
                        if (!canFight) GUI.color = new Color(1f, 1f, 1f, 0.5f);
                        if (Widgets.ButtonText(new Rect(x, y, halfW, LineH), "进行交战", true, true, canFight))
                        {
                            comp.GivePhaseSignal(d, signal);
                        }
                        GUI.color = oldBtn;
                        if (!canFight && draw)
                        {
                            TooltipHandler.TipRegion(new Rect(x, y, halfW, LineH),
                                "没有人参加作战 —— 先在下面的「参战人员」里勾上至少一个");
                        }
                        GUI.color = new Color(1f, 0.62f, 0.55f);   // 不可逆的破坏性动作 ⇒ 红（S7-d 口径）
                        if (Widgets.ButtonText(new Rect(x + halfW + 6f, y, halfW, LineH), "撤退"))
                        {
                            comp.OrderRetreat(d);
                        }
                        GUI.color = oldBtn;
                    }
                    y += LineH + 2f;
                }

                if (ts.revealed && ts.canAssess)
                {
                    if (draw)
                    {
                        float halfW = (w - 6f) * 0.5f;
                        if (Widgets.ButtonText(new Rect(x, y, halfW, LineH), "重新推算"))
                        {
                            ts.Reroll(caravan, site, d.participants, d.approach, d.noCombatPawns);
                        }
                        if (Widgets.ButtonText(new Rect(x + halfW + 6f, y, halfW, LineH), "查看评估"))
                        {
                            // RIM-27：折扣走唯一判据（ApproachFirstStrikePenalty 已归一），
                            // 并把「不参战」名单一起传进去（此前对话框永远看不到它）。
                            ThreatAssessmentEntry.Open(caravan, site,
                                d.Worker?.ApproachFirstStrikePenalty(d.approach) ?? 0f, d.noCombatPawns);
                        }
                    }
                    y += LineH + 2f;
                }
                y += 4f;
            }   // showCombat

            // ---- ② 收集任务（开采 / 搜刮）：模式与结束条件是它的入口，随后是进度与现场物资
            bool showCollect = DelegationUIUtility.DrawSectionHeader(
                new Rect(x, y, w, DelegationUIUtility.SectionHeaderH),
                DelegationUIUtility.SectionCollect, draw, DelegationUIUtility.SectionId.Collect);
            y += DelegationUIUtility.SectionHeaderH;
            if (showCollect)
            {

            // ---- 模式 / 结束条件 / 状态
            //      S11：这两行**本身就是入口**（与原版页签同一个约定：能点的地方写一句提示）。
            //      于是操作区不再需要「切换委派模式」「结束条件」两颗按钮，
            //      底部只剩"动状态 / 破坏性"的那一排 —— 与皮肤底栏一致。
            float mood = DelegationUtility.DailyMoodOffset(d);
            if (draw && Widgets.ButtonText(new Rect(x, y, w, LineH),
                    // RIM-5 追加：模式行只报模式（作息 × 作业强度），满意度**单独写在同一个键的第二个槽**里
                    // —— 原版这一端只有一行位置，所以用紧凑写法（完整口径见皮肤概览栏那一条的悬浮情报）。
                    string.Format("RimDelegationTabMode".Translate(), d.ModeLine(),
                        DelegationUIUtility.SatisfactionShort(d.satisfaction, mood, d.SatisfactionRateFactor))
                    + " · " + DelegationUIUtility.StatusWord(d, site) + "　（点击切换模式）"))
            {
                comp.OpenModeMenu(d);
            }
            y += LineH;

            if (draw && Widgets.ButtonText(new Rect(x, y, w, LineH),
                    string.Format("RimDelegationTabEndCondition".Translate(),
                        WorldObjectComp_Delegations.EndConditionLabelOf(d),
                        d.abortOnOutOfFood
                            ? "RimDelegationFoodAbort".Translate().ToString()
                            : "RimDelegationFoodPressOn".Translate(worker?.ActivityName ?? "开采").ToString())
                    + "　（点击修改）"))
            {
                comp.OpenEndConditionEditor(d);
            }
            y += LineH;

            // ---- 总进度（条 + 尾注），明细仍挂 tooltip
            //      S9/S10：绝对时刻**另起一行**。以前并进尾注，主列只有 ~470px ⇒
            //      「0.7/11 格 · 剩余 0.74 天 · 预期于 …完成」一定会被裁掉（用户截图标红的那处）。
            string tail = string.Format("{0} · {1:0.#}/{2} {3}", d.Progress.ToStringPercent(),
                d.cellsMined, d.totalCells, unit);
            float days = d.EstimatedDaysLeft(site.Tile);
            if (days >= 0f)
            {
                tail += string.Format(" · 剩余 {0:0.##} 天", days);
            }
            if (draw)
            {
                Text.Font = GameFont.Small;
                float tailW = Text.CalcSize(tail).x + 6f;
                const float LabelW = 56f;
                Widgets.Label(new Rect(x, y, LabelW, LineH), "RimDelegationTabTotalProgress".Translate());
                Rect bar = new Rect(x + LabelW, y + 1f, Mathf.Max(80f, w - LabelW - tailW), 20f);
                Widgets.FillableBar(bar, Mathf.Clamp01(d.Progress));
                Widgets.Label(new Rect(bar.xMax + 6f, y, Mathf.Max(60f, tailW), LineH), tail);
                string tip = DelegationUIUtility.ProgressDetailTip(d, worker, unit, outputUnit);
                if (!tip.NullOrEmpty())
                {
                    TooltipHandler.TipRegion(new Rect(x, y, w, 24f), tip);
                }
            }
            y += 24f;

            string finishLine = DelegationUIUtility.FinishDateLine(d, site);
            if (!finishLine.NullOrEmpty())
            {
                Line("　" + finishLine);
            }

            Line(string.Format("RimDelegationTabHours".Translate(), d.ticksWorked / 2500f, d.ticksResting / 2500f));

            // ---- 现场物资（这里空间够，画全；页签那边才需要折叠/截断）
            //      S9：每行「已获取 X/Y 单位」+ 行的市价 + 表头市价合计 + 排序按钮
            List<DelegationPreviewItem> items = DelegationUIUtility.ProgressItemRows(d, site, itemSort);
            bool itemsHidden = DelegationUIUtility.ItemsHidden(d);
            if (itemsHidden)
            {
                // S22（用户要求）：还没透露时保留块头「现场物资（? 类）」，下面留空 —— 与皮肤侧同一条判据
                if (draw)
                {
                    Widgets.Label(new Rect(x, y, w, LineH),
                        DelegationUIUtility.ProgressItemsHeader(d, null, unit));
                }
                y += LineH + Gap;
            }
            else if (!items.NullOrEmpty())
            {
                // S10：短标签（「排序：市价（高→低）」在 138px 的按钮里会被自己裁掉）
                const float ItemSortW = 92f;
                if (draw)
                {
                    Widgets.Label(new Rect(x, y, Mathf.Max(80f, w - ItemSortW - 8f), LineH),
                        DelegationUIUtility.ProgressItemsHeader(d, items, unit));
                    if (Widgets.ButtonText(new Rect(x + w - ItemSortW, y, ItemSortW, LineH),
                            "排序：" + DelegationUIUtility.ItemSortShort(itemSort)))
                    {
                        itemSort = DelegationUIUtility.NextItemSort(itemSort);
                    }
                    TooltipHandler.TipRegion(new Rect(x + w - ItemSortW, y, ItemSortW, LineH),
                        "现场物资排序：市价（高→低）→ 数量（多→少）→ 名称");
                }
                y += LineH;
                for (int i = 0; i < items.Count; i++)
                {
                    if (draw)
                    {
                        // S13：行尾加 i（查看信息卡）
                        DelegationUIUtility.DrawItemRow(new Rect(x, y, w, 26f), items[i], 22f, 18f, true);
                    }
                    y += 26f;
                }
                y += Gap;
            }

            // ---- 战场清点（S31）：尸骸 / 本趟缴获 / 收押的俘虏
            //      用户拍板 3B：**尸骸也要在物资表里有一行** —— 尸体虽然就地处理不带回家，
            //      但"这趟宰了几具、拿回了什么、押了几个人"必须看得见。
            {
                List<DelegationPreviewItem> cleanup = DelegationUIUtility.CleanupRows(d);
                string cleanupHead = DelegationUIUtility.CleanupHeader(d, cleanup);
                if (!cleanupHead.NullOrEmpty())
                {
                    if (draw)
                    {
                        Widgets.Label(new Rect(x, y, w, LineH), cleanupHead);
                    }
                    y += LineH;
                    for (int i = 0; i < cleanup.Count; i++)
                    {
                        if (draw)
                        {
                            DelegationUIUtility.DrawItemRow(new Rect(x, y, w, 26f), cleanup[i], 22f, 18f, true);
                        }
                        y += 26f;
                    }
                    y += Gap;
                }
            }

            // ---- 参与者（全列表：这里放得下，页签那边才有"查看全部"）
            // S27：用户要求把「参与人员」放进**收集任务**（「因为显示的是采矿作业相关的」）——
            // 这一列本来就是"谁去干这趟活"、与作业速率同源；作战那一边另有自己的参战勾选框。
            //      S9/S10：排序 / 添加人员 / 查看全部 三个入口；**名单直接在这张列表里改**
            //      （点某一行 → 移出 / 打开信息卡），所以不再需要单独的「编辑参与者」窗口按钮。
            {
                const float SortW = 92f;
                const float AddW = 92f;
                const float ViewW = 104f;
                if (draw)
                {
                    Widgets.Label(new Rect(x, y, Mathf.Max(80f, w - SortW - AddW - ViewW - 24f), LineH),
                        string.Format("RimDelegationTabParticipants".Translate(), d.participants.Count));
                    if (Widgets.ButtonText(new Rect(x + w - SortW - AddW - ViewW - 18f, y, SortW, LineH),
                            "排序：" + DelegationUIUtility.PawnSortShort(pawnSort)))
                    {
                        pawnSort = DelegationUIUtility.NextSort(pawnSort);
                    }
                    if (Widgets.ButtonText(new Rect(x + w - AddW - ViewW - 10f, y, AddW, LineH), "添加人员"))
                    {
                        comp.OpenAddParticipantMenu(d);
                    }
                    if (Widgets.ButtonText(new Rect(x + w - ViewW, y, ViewW, LineH),
                            string.Format("RimDelegationTabViewAll".Translate(), d.participants.Count)))
                    {
                        DelegationUIUtility.OpenParticipantsMenu(d, worker, d.def, pawnSort);
                    }
                }
                y += LineH + 2f;

                // S13：这一列现在是"当前参与者（✓）+ 队里还能参加的人（×）"，
                // 每行最前面那个开关点一下即切换参不参加（口径全在 comp.ToggleParticipant）。
                DelegationUIUtility.BuildParticipantRoster(d, pawnSort, displayPawns, otherPawns);
                int need = Mathf.Max(1, d.def?.minPawns ?? 1);
                bool canRemove = d.participants.Count > need;

                float rowH = DelegationUIUtility.RowHeight;
                int shown = 0;
                shown += DrawRosterRows(x, w, draw, ref y, d, worker, displayPawns, true, canRemove, rowH);
                shown += DrawRosterRows(x, w, draw, ref y, d, worker, otherPawns, false, true, rowH);
                if (shown == 0)
                {
                    Line("—");
                }
                y += Gap;
            }

            }   // showCollect

            // ---- ③ 远行队信息（S24；S27 起参与者归「收集任务」）
            bool showCaravan = DelegationUIUtility.DrawSectionHeader(
                new Rect(x, y, w, DelegationUIUtility.SectionHeaderH),
                DelegationUIUtility.SectionCaravan, draw, DelegationUIUtility.SectionId.Caravan);
            y += DelegationUIUtility.SectionHeaderH;
            if (showCaravan)
            {

            // ---- 加班 / 疲劳 / 补给（文案全部来自公开件，与页签同源）
            string overtime = DelegationUIUtility.OvertimeLine(d);
            if (!overtime.NullOrEmpty())
            {
                // ⚠️ 两趟都用同一个 MeasuredHeight（纯函数、同宽度 ⇒ 同结果）。
                //    量高趟写 LineH、绘制趟写真实高度的话，内容总高会偏小，最后几行被裁掉。
                float oh = DelegationUIUtility.MeasuredHeight(overtime, w, LineH);
                if (draw)
                {
                    Color oldColor = GUI.color;
                    GUI.color = new Color(1f, 0.8f, 0.45f);
                    Widgets.Label(new Rect(x, y, w, oh), overtime);
                    GUI.color = oldColor;
                }
                y += oh;
            }
            // 休息中：距离开工还有多久（S14）—— 与上面同规矩：两趟走同一个 MeasuredHeight
            string workStart = DelegationUIUtility.WorkStartLine(d, site);
            if (!workStart.NullOrEmpty())
            {
                float wh = DelegationUIUtility.MeasuredHeight(workStart, w, LineH);
                if (draw)
                {
                    Color oldWork = GUI.color;
                    GUI.color = new Color(0.82f, 0.9f, 1f);
                    Widgets.Label(new Rect(x, y, w, wh), workStart);
                    GUI.color = oldWork;
                }
                y += wh;
            }
            Line(DelegationUIUtility.FatigueRiskLine(d));
            Line(DelegationUIUtility.MealLine(caravan));

            // ---- 远行队补给品（S13）：像现场物资那样的列表（图标 / 名称 / 数量 / 总营养 / 重量 / 市价）
            List<DelegationPreviewItem> foodItems = DelegationUIUtility.CaravanFoodItems(caravan);
            if (!foodItems.NullOrEmpty())
            {
                Line(DelegationUIUtility.FoodItemsHeader(foodItems));
                for (int i = 0; i < foodItems.Count; i++)
                {
                    if (draw)
                    {
                        DelegationUIUtility.DrawItemRow(new Rect(x, y, w, 26f), foodItems[i], 22f, 18f, true);
                    }
                    y += 26f;
                }
                y += Gap;
            }
            else if (caravan != null)
            {
                Line("远行队补给品");
                Line("　（远行队里没有可吃的补给品）");
            }

            // ---- 暂停 / 停摆 / 事件 / 计时暂停（与页签同一批条件）
            if (d.paused)
            {
                Line("RimDelegationTabPaused".Translate());
            }
            else if (d.IsStalled(GenTicks.TicksAbs))
            {
                Line(string.Format("RimDelegationTabStalled".Translate(),
                    (d.stalledUntilTickAbs - GenTicks.TicksAbs) / 2500f));
            }
            if (d.eventsFired > 0)
            {
                Line(string.Format("RimDelegationTabEvents".Translate(), d.eventsFired));
            }
            if (RimDelegationMod.Settings != null && !RimDelegationMod.Settings.randomEventsEnabled)
            {
                Line("RimDelegationTabEventsOff".Translate());
            }
            if (comp.timeoutPaused)
            {
                Line(string.Format("RimDelegationTabTimeoutPaused".Translate(), comp.pausedTimeoutRemaining / 60000f));
            }

            }   // showCaravan

            // ---- 流程（**不归三段**：用户 S24 拍板"三段不含流程"）
            //      皮肤侧它有独立的第四栏（S22）；原版没有那一栏，所以放在主列**末尾**当一个独立块
            //      —— 重排 ≠ 删信息，且它本来就不是"作战/收集/远行队"里的事。
            // S14：流程块 = 统一阶段序列（已完成累积 + 当前段三行）。主控台空间比页签大，
            // 已完成最多逐条列 6 段，再多就压成一行汇总。
            List<DelegationStageRow> stageRows = DelegationUIUtility.StageRows(d, site, 6,
                DelegationUIUtility.FlowHeader + "：");
            if (!stageRows.NullOrEmpty())
            {
                // S28：流程块套一张**底框**（用户：「底框直接要有空隙」）—— 内容整体缩进 8px，
                // 所以高度必须用"缩进后的宽度"量（换行数会变），两趟都走这一个式子。
                float pad = DelegationUIUtility.FlowBoxPad;
                float innerW = Mathf.Max(80f, w - pad * 2f);
                float innerH = DelegationUIUtility.StageRowsHeight(stageRows, innerW);
                if (draw)
                {
                    DelegationUIUtility.DrawFlowBox(new Rect(x, y, w, innerH + pad * 2f));
                    DelegationUIUtility.DrawStageRows(new Rect(x + pad, y + pad, innerW, 0f),
                        y + pad, innerW, stageRows);
                }
                // ⚠️ 量高趟与绘制趟必须走同一个纯函数（同宽同结果），否则总高偏小、末尾被裁
                y += innerH + pad * 2f;
            }

            return y;
        }

        /// <summary>
        /// 参与者名单的一批行（S13）：最前面是 V/X 开关，后面是原版那一行人物信息。
        /// <paramref name="participating" /> = true 是"当前参加的人"，false 是"队里还能参加的人"（整行压暗）。
        /// 返回画了几行。与皮肤里的 <c>DrawRosterRows</c> 同构 —— 两端共用
        /// <see cref="DelegationUIUtility.DrawParticipantToggle" /> 与 <c>comp.ToggleParticipant</c>，
        /// 所以开关的长相与判定只有一份。
        /// </summary>
        private int DrawRosterRows(float x, float w, bool draw, ref float y, Delegation d,
            DelegationWorker worker, List<Pawn> list, bool participating, bool canToggle, float rowH)
        {
            if (list.NullOrEmpty())
            {
                return 0;
            }
            int drawn = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == null)
                {
                    continue;
                }
                if (draw)
                {
                    Rect row = new Rect(x, y, w, rowH);
                    if (Mouse.IsOver(row))
                    {
                        Widgets.DrawHighlight(row);
                    }
                    Rect toggle = new Rect(row.x, row.y + (row.height - 18f) * 0.5f, 18f, 18f);
                    Rect line = new Rect(row.x + 24f, row.y, row.width - 24f, row.height);

                    Color old = GUI.color;
                    if (!participating)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.62f);
                    }
                    DelegationUIUtility.DrawPawnLine(line, p, DelegationUIUtility.PawnLine(p, worker, d.def));
                    GUI.color = old;

                    Rect click = DelegationUIUtility.LabelRectFor(line);
                    TooltipHandler.TipRegion(click, "点击管理这名参与者（移出 / 打开信息卡）");
                    if (Widgets.ButtonInvisible(click))
                    {
                        selected.OpenParticipantRowMenu(d, p);
                    }
                    if (DelegationUIUtility.DrawParticipantToggle(toggle, participating, p, canToggle))
                    {
                        selected.ToggleParticipant(d, p);
                    }
                }
                y += rowH;
                drawn++;
            }
            return drawn;
        }

        /// <summary>
        /// 操作区（S9→S11）。
        ///
        /// S11 起这里**只剩"动状态 / 破坏性"的动作**：
        ///   上一行（或同一行左侧）= 暂停 / 紧急加班 / 结束加班；
        ///   右端 = 查看页签 + 中止委派（红）。
        /// 「切换委派模式」「结束条件」已经搬进正文那两行（行本身可点），
        /// 「编辑参与者」搬进参与者列表（点人名），所以底部这一排就是"立刻改变现场状态"的地方 ——
        /// 与皮肤窗口的底栏同一个心智（用户："暂停委派和紧急加班调整到最下面"）。
        /// 详情栏窄于 700px 时用两行（第二行放查看页签与中止），避免按钮叠字。
        /// </summary>
        private void DrawActionButtons(Rect r, Delegation d, WorldObjectComp_Delegations comp, int rows)
        {
            float row2Y = r.y + ButtonH + 4f;

            // ---- 状态与破坏性动作
            float bx2 = r.x;
            if (Widgets.ButtonText(new Rect(bx2, r.y, 140f, ButtonH),
                    d.paused ? "RimDelegationTabResume".Translate() : "RimDelegationTabPause".Translate()))
            {
                comp.TogglePause(d);
            }
            bx2 += 146f;

            if (d.def != null && d.def.AllowsEmergencyOvertime)
            {
                string overtimeLabel = d.EmergencyOvertimeActive
                    ? string.Format("RimDelegationTabOvertimeActive".Translate(), d.overtimeTicksRemaining / 2500f)
                    : string.Format("RimDelegationTabOvertime".Translate(), d.def.emergencyOvertimeHoursPerUse);
                Color oldColor = GUI.color;
                GUI.color = DangerTint;
                if (Widgets.ButtonText(new Rect(bx2, r.y, 152f, ButtonH), overtimeLabel))
                {
                    comp.StartEmergencyOvertime(d);
                }
                GUI.color = oldColor;
                bx2 += 158f;

                if (d.EmergencyOvertimeActive
                    && Widgets.ButtonText(new Rect(bx2, r.y, 110f, ButtonH), "RimDelegationTabOvertimeCancel".Translate()))
                {
                    comp.CancelEmergencyOvertime(d);
                }
            }

            // 宽够：选中车队挤在同一行的右端（中止再往右）；窄了：两者都下移第二行
            float jumpY = rows >= 2 ? row2Y : r.y;
            float jumpX = rows >= 2 ? r.x : r.xMax - 276f;
            if (Widgets.ButtonText(new Rect(jumpX, jumpY, 130f, ButtonH), "RimDelegationConsoleSelectCaravan".Translate()))
            {
                SelectCaravan(d);
            }

            Color abortColor = GUI.color;
            GUI.color = DangerTint;
            if (Widgets.ButtonText(new Rect(r.xMax - 140f, jumpY, 140f, ButtonH), "RimDelegationTabAbort".Translate()))
            {
                comp.Abort(d, "RimDelegationAbortByPlayer".Translate());
            }
            GUI.color = abortColor;
        }

        /// <summary>
        /// 「选中该远行队」：把地图上的选择切到这支车队。
        ///
        /// S18 改名与改行为：页签已经退化成"打开主控台"的引导页，再叫「查看远行队页签」就是误导；
        /// 而且这个动作现在是"把选择挪到那支队伍上"，**不再顺手关窗** ——
        /// 主控台是可拖拽的浮动窗口，玩家往往想"看一眼车队在哪、再回来接着操作"。
        /// </summary>
        private void SelectCaravan(Delegation d)
        {
            if (d?.caravan != null && !d.caravan.Destroyed)
            {
                Find.WorldSelector.Select(d.caravan, true);
            }
        }
    }
}
