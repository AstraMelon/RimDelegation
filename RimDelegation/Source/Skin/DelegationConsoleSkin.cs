using System;
using System.Collections.Generic;
using RadiusUI.Framework;
using RimDelegation;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegationRadiusUI
{
    // ════════════════════════════════════════════════════════════════════════════
    // RIM-3（2026-10-05）：本文件原属独立 mod「RimDelegation - Radius UI」，现随皮肤
    // **整体并入 RimDelegation 本体程序集**（RimDelegation.dll）。命名空间保持不变，
    // 以免 4000+ 行里出现大量改名噪声；合并后不再有"皮肤开关 / 回退原版"这回事 ——
    // 主控台只有这一种画法（用户拍板：硬依赖 Radius UI Framework、移除回退开关）。
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>左栏排序方式（主控台专用，不动 RimDelegation 的参与者排序）。</summary>
    internal enum ConsoleSort
    {
        DaysLeft,
        Progress,
        Site
    }

    /// <summary>
    /// 用 Radius UI 重画「委派」主控台窗口（`Window_Delegations.DoWindowContents`），
    /// 版面照 **Radius UI - Quest Menu** 的三区骨架（用户拍板：状态分组 / Pin / 1180×700 / 仅皮肤版）。
    ///
    /// 骨架（尺寸与画法均取自 Quest Menu 的反编译）：
    ///   ① 标题栏 52：30×30 强调色圆角底 + **我们自己的委派图标**、标题（GameFont.Medium bold）、
    ///      计数（Small，TextDim）、右侧三枚 30×30 IconButton（设置 / 切回原版 / 关闭），底边 1px 分隔线；
    ///   ② 左栏 `min(340, w×0.30)`：状态行 → 原版 `QuickSearchWidget` + 排序 → 按状态分组（置顶/作业中/
    ///      休息中/已暂停/停摆），每组标题带计数，行 = 活动图标 + `车队 @ 地点` + `活动 · 状态` + 右端剩余天数；
    ///   ③ 主列：`def.description` + 进度 + 现场物资 + 参与者 + 流程 + 加班/疲劳/伙食（可滚动）；
    ///   ④ 右栏 `min(320, w×0.28)`：`At a glance` 卡（7 行：状态/剩余/模式/结束条件/参与者/负重/补给）+
    ///      `世界位置` 卡（第一轮是示意图，第二轮换 `WorldSnapshot` 缩略图）+ `前往` 卡；
    ///   ⑤ 底部状态栏 30：左=选中摘要，中=快捷键提示，右=`Pin` + `中止委派`（红）。
    ///
    /// 分工不变：**只换绘制**，数据全走 RimDelegation 公开件，动作调 `comp.TogglePause / Abort / …`。
    /// 滚动仍是"内容总高由 Cursor 空跑一趟算出来 + FlatScroll"，不依赖 Layout 趟测高。
    /// </summary>
    internal static class DelegationConsoleSkin
    {
        // ── 尺寸（Quest Menu 的实测值 + 我们内容微调）─────────────────────────
        private const float Pad = 12f;

        private const float GapH = 8f;
        private const float TitleH = 52f;
        private const float BottomH = 30f;
        private const float StatusLineH = 20f;
        private const float SearchH = 26f;
        private const float GroupH = 26f;
        private const float RowH = 56f;
        private const float BtnH = 30f;
        private const float ItemRowH = 26f;

        /// <summary>窗口最小尺寸 —— 与 RimDelegation 的 MinW/MinH 保持一致，皮肤接管时要自己兜（见下）。</summary>
        private const float SkinMinW = 900f;

        private const float SkinMinH = 480f;

        // ── 流程栏（S22 四栏布局）───────────────────────────────────────────
        //
        // 宽度账（全部 [源码] 推导，见 Doc/流程栏与技能挂钩-方案评估.md §1）：
        //   窗口内容宽 C = winW − 36（Verse.Window.Margin = 18）− 24（皮肤 Pad = 12）⇒ C = winW − 60；
        //   主列必须 ≥ MainMinW 才能容下「现场物资行（名称 + 右对齐明细）」与
        //   「参与者表头三个按钮 86+92+100+12 = 290 加标签约 80」这两处 ⇒ 故取 440 作下限。
        //   flowW = clamp(C − 左 − 右 − 2 − 主列下限, 0, FlowMaxW)；
        //   小于 FlowMinW（240 = 标题行会折行的临界）就**整栏不出现**，流程块回落到主列里
        //   （＝S17 及以前的现状），这样窄屏/老存档几何永远不会出现"挤到读不出来"的栏。

        /// <summary>流程栏宽度上限（300 是"舒适"档：标题一行、旁白两行）。</summary>
        private const float FlowMaxW = 300f;

        /// <summary>流程栏可用下限；低于它就不画流程栏（流程回主列）。</summary>
        private const float FlowMinW = 240f;

        /// <summary>主列的宽度下限（现场物资行 + 参与者表头按钮 + 标签）。</summary>
        private const float MainMinW = 440f;

        /// <summary>S22：折叠后一列收成的竖条宽度。</summary>
        private const float StripW = 26f;

        /// <summary>S22：四个区域顶端的「列头」高度 —— 折叠开关就画在这一条里。</summary>
        private const float ColHeadH = 22f;

        /// <summary>S34：列头里图钉按钮的宽度（与 ＋/－ 同宽，紧挨在它**左边**）。</summary>
        private const float PinBtnW = 22f;

        /// <summary>RIM-6：折叠开关（`＋/－`）的方块边长 —— 与 <see cref="PinBtnW" /> 同高，整组更紧凑。</summary>
        private const float ColBtnW = 18f;

        /// <summary>RIM-6：列头两颗按钮之间的间距，也是按钮组距列右边界的余量（旧版是贴边 2px）。</summary>
        private const float BtnGap = 4f;

        // ── 状态（同一时刻只有一个主控台窗口）────────────────────────────────
        private static Window_Delegations owner;

        /// <summary>草稿表为空的场合复用一个空列表（与 `Window_Delegations.NoDrafts` 同一个理由）。</summary>
        private static readonly List<DelegationDraft> noDrafts = new List<DelegationDraft>();

        private static QuickSearchWidget search;
        private static Vector2 listScroll;
        private static Vector2 mainScroll;

        /// <summary>草稿页（待下达）自己的滚动位置（与在途详情分开，来回切时不互相跳）。</summary>
        private static Vector2 draftScroll;

        /// <summary>计划页（前往中）自己的滚动位置（同上）。</summary>
        private static Vector2 planScroll;

        /// <summary>流程栏（S22 四栏）自己的滚动位置 —— 与主列分开，来回切选中时不互相跳。</summary>
        private static Vector2 flowScroll;

        /// <summary>这一帧流程栏到底画没画（主列据此决定要不要自己画流程块 —— 同一信息只许画一处）。</summary>
        private static bool flowShown;

        /// <summary>流程栏当前跟着哪一条委派（换选中目标就把流程栏滚回顶部）。</summary>
        private static Delegation flowOwner;

        private static Vector2 railScroll;
        private static WorldObjectComp_Delegations selected;

        /// <summary>S15 第二期：左栏「历史」里选中的那条记录（与 <see cref="selected" /> 互斥）。</summary>
        private static DelegationRecord selectedRecord;

        /// <summary>
        /// S18：左栏「待下达」里选中的那张草稿（与 <see cref="selected" />、<see cref="selectedRecord" /> 三者互斥）。
        ///
        /// 皮肤**自己维护**选中态（不像原版那样存在窗口字段里），所以草稿也要在这里记一份；
        /// 入口指定的焦点由 `Window_Delegations.ConsumePendingDraft()` 取回来。
        /// </summary>
        private static DelegationDraft selectedDraft;

        /// <summary>历史详情自己的滚动位置（与主列分开，来回切时不互相跳）。</summary>
        private static Vector2 recordScroll = Vector2.zero;
        private static ConsoleSort sort = ConsoleSort.DaysLeft;

        /// <summary>参与者 / 现场物资两个列表的排序（S9：用户要求这两个列表都支持排序）。</summary>
        private static PawnSortMode pawnSort = PawnSortMode.SkillDesc;

        private static ItemSortMode itemSort = ItemSortMode.ValueDesc;

        /// <summary>参与者行的显示用副本（复用，避免每帧每趟都 new 一个列表）。</summary>
        private static readonly List<Pawn> displayPawns = new List<Pawn>();

        /// <summary>「队里还能参加、但没参加」的人（S13 参与者列表的后半段，同样复用）。</summary>
        private static readonly List<Pawn> otherPawns = new List<Pawn>();

        /// <summary>"置顶"的站点 ID 集合（持久化在本地 Mod 配置里，不进存档）。</summary>
        private static readonly HashSet<int> pinned = new HashSet<int>();

        private static bool pinnedLoaded;

        /// <summary>本次绘制里"过掉搜索、排好序"的候选（分组时按状态再分桶）。</summary>
        private static readonly List<WorldObjectComp_Delegations> visible =
            new List<WorldObjectComp_Delegations>();

        private static readonly List<WorldObjectComp_Delegations> pinnedBucket =
            new List<WorldObjectComp_Delegations>();

        private static readonly List<WorldObjectComp_Delegations> workingBucket =
            new List<WorldObjectComp_Delegations>();

        private static readonly List<WorldObjectComp_Delegations> restingBucket =
            new List<WorldObjectComp_Delegations>();

        private static readonly List<WorldObjectComp_Delegations> pausedBucket =
            new List<WorldObjectComp_Delegations>();

        private static readonly List<WorldObjectComp_Delegations> stalledBucket =
            new List<WorldObjectComp_Delegations>();

        private static Texture2D ownIcon;

        private static bool ownIconTried;

        // ================================================================ 入口

        /// <summary>
        /// 主控台皮肤的**唯一入口**（RIM-3 起由 <c>Window_Delegations.DoWindowContents</c> 直接调用，
        /// 不再走 Harmony 前缀补丁）。
        ///
        /// <returns>true = 本帧由皮肤画完，调用方应当直接 return；
        /// false = 皮肤不接管（反射桥不可用），调用方落回原版画法。</returns>
        /// </summary>
        public static bool Draw(Window_Delegations win, Rect inRect)
        {
            if (!ConsoleWindowBridge.Ready)
            {
                // 启动时反射 `Verse.Window.windowDrawing` 就失败了 ⇒ 本会话主控台一直是原版画法。
                // 这条只喊一次，别每帧刷屏（原皮肤 mod 走的是"patching 状态"，同样的语义）。
                Log.WarningOnce("[RimDelegation] 皮肤：主控台不可用，本会话使用原版主控台画法（" +
                                "通常是 Verse.Window.windowDrawing 改了实现）", 0x5E0F0);
                return false;
            }

            // ⚠️ 皮肤接管后 RimDelegation 原版 DoWindowContents 的下半段不执行，它里面那道
            //    "窗口被拖太小就兜回下限"也随之被绕过 —— 这里必须自己补一遍。
            //    注意皮肤的下限（900×480）比原版的（760×420）大：主控台的内容是照三/四栏排的。
            if (win.windowRect.width < SkinMinW)
            {
                win.windowRect.width = SkinMinW;
            }
            if (win.windowRect.height < SkinMinH)
            {
                win.windowRect.height = SkinMinH;
            }

            if (!ReferenceEquals(owner, win))
            {
                owner = win;
                listScroll = Vector2.zero;
                mainScroll = Vector2.zero;
                railScroll = Vector2.zero;
                selected = null;
                if (search != null)
                {
                    search.Reset();
                }
            }
            if (search == null)
            {
                search = new QuickSearchWidget();
            }
            LoadPinnedOnce();

            ConsoleWindowBridge bridge = new ConsoleWindowBridge(win);
            if (!bridge.InstallChrome())
            {
                CardChrome.Card(inRect, false);
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            List<WorldObjectComp_Delegations> all = DelegationRegistry.AllActive();
            List<WorldObjectComp_Delegations> planned = DelegationRegistry.AllPlanned();
            // S18：待下达的草稿（内存态，不进存档）。两件事必须在皮肤里自己补（RIM-3 后依然成立：
            // 皮肤接管即 return，原版 DoWindowContents 的下半段不执行）：
            //   ① 消费入口焦点 —— 原版的 EnsureSelection 不跑；
            //   ② 动态 forcePause —— 原版是靠 `forcePause = drafts.Count > 0` 那一行做的，同样不跑。
            List<DelegationDraft> drafts = DraftList();
            DelegationDraft focusDraft = win.ConsumePendingDraft();
            if (focusDraft != null)
            {
                SelectDraft(focusDraft);
            }
            else
            {
                WorldObjectComp_Delegations focusComp = win.ConsumePendingComp();
                if (focusComp != null)
                {
                    selected = focusComp;
                    selectedDraft = null;
                    selectedRecord = null;
                    mainScroll = Vector2.zero;
                }
            }
            win.forcePause = drafts.Count > 0;
            BuildVisible(all);
            EnsureSelection(planned, drafts);

            Rect body = inRect.ContractedBy(Pad);
            Rect titleRect = new Rect(body.x, body.y, body.width, TitleH);
            DrawTitlebar(titleRect, win, all.Count, planned.Count);

            // S17：**删掉"整屏空态早退"** —— 用户原话：「没有委派的时候应该也要显示对应内容（例如历史记录和其他）」。
            // 旧写法在"既没有在途委派、也没有在途计划"时只画两行居中提示就 `return true`，
            // 于是左栏（含常驻的「历史」组）、主列、右栏与底栏**整块消失**，
            // 玩家在一支在途委派都没有的时候根本看不到自己结束过的委派记录。
            // 现在三栏 + 底栏**永远画**，空态提示改由主列承担（见 DrawNoSelection）。
            Rect below = new Rect(body.x, titleRect.yMax, body.width, Mathf.Max(0f, body.yMax - titleRect.yMax));

            Rect bottomRect = new Rect(below.x, below.yMax - BottomH, below.width, BottomH);
            Rect content = new Rect(below.x, below.y, below.width, Mathf.Max(0f, below.height - BottomH - GapH));

            // S22：四个区域的折叠开关画在各自顶端的「列头」里（统一 22px 高）——
            // 这样不必改四列各自的内部标题行（它们的高度与位置各不一样）。
            // 折叠只是**本地阅读偏好**（`RimDelegationSettings`），不进存档。
            RimDelegationSettings skin = RimDelegationMod.Settings;
            // S34：图钉 = 固定展开 ⇒ 生效的折叠态 = `collapsed* && !pinned*`
            //（点那一栏的「－」会顺带拔钉，见 DrawColumnToggle，所以不会出现"钉住却收着"的状态）。
            bool pinLeft = skin != null && skin.pinnedLeft;
            bool pinFlow = skin != null && skin.pinnedFlow;
            bool pinMain = skin != null && skin.pinnedMain;
            bool pinRail = skin != null && skin.pinnedRail;
            bool colLeft = skin != null && skin.collapsedLeft && !pinLeft;
            bool colFlow = skin != null && skin.collapsedFlow && !pinFlow;
            bool colMain = skin != null && skin.collapsedMain && !pinMain;
            bool colRail = skin != null && skin.collapsedRail && !pinRail;

            Rect headRow = new Rect(content.x, content.y, content.width, ColHeadH);
            Rect body2 = new Rect(content.x, content.y + ColHeadH, content.width,
                Mathf.Max(0f, content.height - ColHeadH));

            // ---- S29：悬浮焦点（用户：「鼠标悬浮在流程，则展开流程、折叠左栏；悬浮在左栏则展开左栏」）
            // S34 起这条规则是**不对称**的（用户原话见下）。只影响**左栏 / 流程栏**这一对；
            // 主列与右栏不参与（它们的折叠是"看大图/操作"的场合）。
            // 判据与两条防抖理由都在 UpdateColumnFocus 里；这里只把结果套到本帧的有效折叠状态上。
            UpdateColumnFocus(skin, colLeft, colFlow);
            if (columnFocus == ColumnFocus.Left)
            {
                // S34（用户原话）：「鼠标悬浮在左栏的时候展开左栏，**但是不折叠流程**」。
                // 与 S29 的差别只在这一条：旧版这里会 `colFlow = true`（悬浮左栏 ⇒ 把流程收掉）。
                // 现在悬浮左栏只把左栏摆成展开态，流程栏维持它自己的状态；两栏宽度真挤不下时，
                // 流程栏照 S22 的规则回落进主列（那不是"被折叠"，是版面取舍）。
                colLeft = false;
            }
            else if (columnFocus == ColumnFocus.Flow && !pinLeft)
            {
                // 焦点在流程 ⇒ 左栏收起（顺带把让出的宽度抬进 flowCap）。
                // 被图钉固定展开的左栏**不吃这一下** —— 这正是图钉的主用途。
                colLeft = true;
            }

            float listBaseW = Mathf.Min(340f, body2.width * 0.30f);
            float listW = colLeft ? StripW : listBaseW;
            float railW = colRail ? StripW : Mathf.Min(320f, body2.width * 0.28f);

            // ── S22 四栏：左栏 / 流程栏 / 主列 / 右栏（宽度账见文件头的 FlowMaxW 注释）──
            // 流程栏只在"选中的是一条**在途委派**"时才有内容（草稿 / 历史 / 前往中计划 / 空态都无流程可讲），
            // 放不下时 flowW = 0 ⇒ 自动退回三栏、主列拿回全部宽度（不是留一条空栏）。
            // 手动折叠（`collapsedFlow`）走**同一条**回落路径：流程块回到主列里。
            //
            // S22 追加（用户要求）：「左栏收起的时候，空间是否可以给到流程」⇒ 左栏折叠时把
            // 它让出的宽度抬进流程栏的上限（`flowCap`）。理由：玩家收起左栏通常正是"只想看这一条的流程"，
            // 那宽度就该给流程；多出来的余量仍旧归主列。右栏折叠不这么做（那是"看大图/操作"的场合）。
            float flowCap = FlowMaxW;
            if (colLeft)
            {
                flowCap += Mathf.Max(0f, listBaseW - StripW);
            }

            Delegation flowDelegation = selected?.active;
            float flowW = 0f;
            if (flowDelegation != null && !colFlow)
            {
                flowW = Mathf.Clamp(body2.width - listW - railW - 2f - MainMinW, 0f, flowCap);
                if (flowW < FlowMinW)
                {
                    flowW = 0f;   // 挤不出可用宽度就不放：流程块回落到主列里（S17 及以前的现状）
                }
            }
            flowShown = flowW > 0f;
            if (!ReferenceEquals(flowOwner, flowDelegation))
            {
                flowOwner = flowDelegation;
                flowScroll = Vector2.zero;   // 换了选中目标：流程栏滚回顶部
            }

            // 左→右依次排：左栏 / 流程栏 / 主列，右栏锚在右边。主列吃掉剩下的全部宽度。
            Rect railRect = new Rect(body2.xMax - railW, body2.y, railW, body2.height);
            Rect listRect = new Rect(body2.x, body2.y, listW, body2.height);
            float x = listRect.xMax + 1f;
            Rect flowRect = flowShown ? new Rect(x, body2.y, flowW, body2.height) : Rect.zero;
            if (flowShown)
            {
                x = flowRect.xMax + 1f;
            }
            float mainW = railRect.x - x - 2f;

            // 主列折叠：把它让出来的宽度给流程栏（没有流程栏就给左栏），避免在中间留一块空白。
            if (colMain)
            {
                float freed = Mathf.Max(0f, mainW - StripW);
                if (flowShown)
                {
                    flowRect = new Rect(flowRect.x, flowRect.y, flowRect.width + freed, flowRect.height);
                }
                else
                {
                    listRect = new Rect(listRect.x, listRect.y, listRect.width + freed, listRect.height);
                }
                x = (flowShown ? flowRect.xMax : listRect.xMax) + 1f;
                mainW = StripW;
            }
            Rect mainRect = new Rect(x, body2.y, Mathf.Max(StripW, mainW), body2.height);

            // ⚠️ S22 修（用户报的 bug：「流程的折叠按钮，按下之后进行了折叠，然后没有展开的按钮了」）：
            //    折叠后 `flowRect` 宽度是 0，而列头的开关原来画在 `flowRect` 里 ⇒ 收起来就再也打不开。
            //    所以手动折叠时**额外留一个 26px 的列头槽位**（就在左栏右边），只有"自动回落"
            //    （窗口太窄，本来就没配到空间）才不给开关 —— 那种情况给了也展不开，等于骗人。
            Rect flowHead = flowShown
                ? flowRect
                : (colFlow && flowDelegation != null
                    ? new Rect(listRect.xMax + 1f, body2.y, StripW, body2.height)
                    : Rect.zero);

            // S29：把本帧**实际**的两栏矩形记下来（含列头那 22px）—— 下一帧的焦点判据用它。
            // 含列头是为了"移上去点列头开关"这一步不会让布局先弹回去（开关从指头下面跑掉）。
            lastListRect = new Rect(listRect.x, headRow.y, listRect.width, headRow.height + listRect.height);
            lastFlowRect = flowShown
                ? new Rect(flowRect.x, headRow.y, flowRect.width, headRow.height + flowRect.height)
                : Rect.zero;

            DrawColumnHeads(headRow, listRect, flowHead, mainRect, railRect);

            if (colLeft)
            {
                CardChrome.Fill(listRect, new Color(0f, 0f, 0f, 0.20f));
            }
            else
            {
                DrawLeft(listRect, planned.Count, drafts.Count);
            }
            CardChrome.Fill(new Rect(listRect.xMax, body2.y, 1f, body2.height), Palette.Border);

            if (flowShown)
            {
                DrawFlowColumn(flowRect, flowDelegation, selected.Site);
                CardChrome.Fill(new Rect(flowRect.xMax, body2.y, 1f, body2.height), Palette.Border);
            }

            if (colMain)
            {
                CardChrome.Fill(mainRect, new Color(0f, 0f, 0f, 0.20f));
            }
            else
            {
                DrawMain(mainRect);
            }

            if (colRail)
            {
                CardChrome.Fill(railRect, new Color(0f, 0f, 0f, 0.20f));
            }
            else
            {
                DrawRail(railRect);
            }

            DrawBottomBar(bottomRect);
            HandleKeys(win);
            return true;
        }

        // ================================================================ ① 标题栏

        private static void DrawTitlebar(Rect r, Window_Delegations win, int total, int planned)
        {
            RadiusIcon questIcon = IconSet.Get("Event/Quest");
            Rect plate = new Rect(r.x + 16f, r.y + 11f, 30f, 30f);
            CardChrome.Rounded(plate, RadiusTheme.Accent, 8f);
            Texture2D own = OwnIcon();
            if (own != null)
            {
                // 用**我们自己的委派图标**（RimDelegation 那张 64×64），而不是框架的通用图标
                GUI.DrawTexture(GenUI.ContractedBy(plate, 6f), own, ScaleMode.ScaleToFit);
            }
            else if (questIcon.Exists)
            {
                questIcon.Draw(GenUI.ContractedBy(plate, 6f), Palette.InkOnAccent);
            }

            float titleX = plate.xMax + 10f;
            string title = "RimDelegationConsoleTitleShort".Translate().ToString();
            RadiusFont.Label(new Rect(titleX, r.y, 220f, r.height), title, GameFont.Medium, true, null,
                TextAnchor.MiddleLeft, false);
            float titleW = RadiusFont.Size(title, GameFont.Medium, true).x;
            string counts = string.Format("RimDelegationConsoleCounts".Translate(), total,
                CountWorking(), CountPaused(), planned);
            RadiusFont.Label(new Rect(titleX + titleW + 16f, r.y, 460f, r.height), counts, GameFont.Small, false,
                Palette.TextDim, TextAnchor.MiddleLeft, false);

            // 右侧两枚：设置 / 关闭（Quest Menu 同款位置与间距）。
            // RIM-3（皮肤并入本体）后**没有**"切回原版"了 —— 用户拍板取消回退开关，
            // 主控台只有 Radius UI 一种画法；那枚 SwapView 按钮连同 `skinConsole` 一起删除。
            float bx = r.xMax - 16f - 30f;
            if (UIKit.IconButton(new Rect(bx, r.y + 11f, 30f, 30f), IconSet.Get("Action/Decline"),
                    "关闭"))
            {
                win.Close(true);
            }
            bx -= 38f;
            if (UIKit.IconButton(new Rect(bx, r.y + 11f, 30f, 30f), IconSet.Get("Action/Settings"),
                    "打开 Mod 设置"))
            {
                RimDelegationMod mod = LoadedModManager.GetMod<RimDelegationMod>();
                if (mod != null)
                {
                    Find.WindowStack.Add(new Dialog_ModSettings(mod));
                }
            }

            CardChrome.Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), Palette.Border);
        }

        private static Texture2D OwnIcon()
        {
            if (!ownIconTried)
            {
                ownIconTried = true;
                try
                {
                    // 图标住在 RimDelegation 自己的 Textures 里；ContentFinder 会跨 mod 找
                    ownIcon = ContentFinder<Texture2D>.Get("UI/Icons/RimDelegation_Delegations", false);
                }
                catch (Exception)
                {
                    ownIcon = null;
                }
            }
            return ownIcon;
        }

        // ================================================================ ② 左栏

        private static void DrawLeft(Rect r, int plannedCount, int draftCount)
        {
            CardChrome.Fill(r, new Color(0f, 0f, 0f, 0.20f));
            Rect inner = r.ContractedBy(10f);
            float y = inner.y;

            // 状态行：`N 个在途 · 已选 i/N`（S12：有"前往中"时补一句，免得玩家以为漏了）
            // S22 修：选中**待下达的表单**时它不在 `visible`（那里只有在途的委派）里 ⇒
            //   原来会显示「待下达 1 · 共 0 项 · 当前第 1 项」这种自相矛盾的一行（用户截图左栏）。
            //   现在按草稿自己的数量与序号报。
            int statusCount = visible.Count;
            int statusIndex = SelectedIndex() + 1;
            if (selectedDraft != null)
            {
                int di = DraftList().IndexOf(selectedDraft);
                statusCount = draftCount;
                statusIndex = (di < 0 ? 0 : di) + 1;
            }
            string status = string.Format("RimDelegationConsoleSelection".Translate(), statusCount, statusIndex);
            if (plannedCount > 0)
            {
                status += string.Format(" · 前往中 {0}", plannedCount);
            }
            // S18：有待下达的表单时把它顶在最前面 —— 那是**在等玩家动手**的一项，
            // 比其他任何统计都更需要被看见（也有意用暖色，与"作业中/休息中"的冷色分开）。
            if (draftCount > 0)
            {
                status = string.Format("待下达 {0} · ", draftCount) + status;
            }
            RadiusFont.LabelAt(new Rect(inner.x, y, inner.width, StatusLineH), status,
                RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);
            y += StatusLineH + 4f;

            // 搜索 + 排序（搜索用**原版** QuickSearchWidget，Quest Menu 也是它）
            //
            // ⚠️ S10 修：宽度原来是 `inner.width - 108` 配一颗 124px 的排序按钮 ——
            //    左栏只有 340px（内部 320），两者加起来 336 会把排序按钮顶出栏外被裁掉
            //    （用户截图："左上角的排序显示有问题"）。现在按按钮实际宽度反推搜索框宽度。
            //    标签也去掉"排序："前缀并用短词：这一栏只够 4 个中文字。
            const float SortW = 96f;
            Rect searchRect = new Rect(inner.x, y, Mathf.Max(80f, inner.width - SortW - 8f), SearchH);
            search.OnGUI(searchRect, null, null);
            Rect sortRect = new Rect(searchRect.xMax + 8f, y, SortW, SearchH);
            if (UIKit.Button(sortRect, SortShortLabel(), ButtonStyle.Solid, true,
                    "列表排序（点一下换一档）：剩余时间 → 进度 → 地点"))
            {
                sort = sort == ConsoleSort.DaysLeft ? ConsoleSort.Progress
                    : sort == ConsoleSort.Progress ? ConsoleSort.Site : ConsoleSort.DaysLeft;
            }
            y += SearchH + GapH;

            // 分组列表
            Rect listArea = new Rect(inner.x, y, inner.width, Mathf.Max(40f, inner.yMax - y));
            DrawGroupedList(listArea);
        }

        private static void DrawGroupedList(Rect r)
        {
            int planRows = DelegationRegistry.AllPlanned().Count;
            // S18：待下达的草稿（只有存在时才占位 —— 它一定是玩家刚刚点出来的，而且会被自动聚焦）
            List<DelegationDraft> drafts = DraftList();
            float draftsH = drafts.Count > 0 ? GroupH + drafts.Count * RowH : 0f;
            // S16：历史组**常驻**（哪怕 0 条）—— 只在有记录时才画的话，玩家会以为"没这个功能"
            //（用户原话：「没有处在委派任务的远行队的时候，看不到历史记录」）。
            List<DelegationRecord> history = RimDelegationHistory.Get(false)?.records;
            int historyCount = history?.Count ?? 0;
            int historyRows = Mathf.Min(historyCount, 20);
            float historyH = GroupH + (historyRows > 0 ? historyRows * RowH : RowH * 0.7f);
            float contentH = draftsH + GroupTotal() + (planRows > 0 ? GroupH + planRows * RowH : 0f) + historyH;

            // S22 修（用户截图：**只有「待下达」的时候**，左上角会冒出一句「…请调整搜索关键词」）：
            //   ① 原来那句"没有匹配"的判据是 `visible==0 && planned==0 && history==0` —— **漏了草稿**，
            //      于是一张待下达的表单（没有任何在途/计划/历史）就会命中；
            //   ② 而且它画在**写死的 y=4**（正是组标题的位置）⇒ 一命中就必然压在「待下达」标题上叠字。
            //   ③ 措辞也不对：没有搜索词时不该说"请调整搜索关键词" —— 那是"筛选之后没结果"的话。
            // 现在：判据把草稿算进去、只在真有搜索词时出现、并且画在内容**之后**（当前 y）。
            bool filtered = !(search?.filter?.Text).NullOrEmpty();
            bool anyContent = visible.Count > 0 || planRows > 0 || historyCount > 0 || drafts.Count > 0;
            bool showNoMatch = filtered && !anyContent;
            if (showNoMatch)
            {
                contentH += 22f;
            }
            Rect view = new Rect(0f, 0f, r.width - 12f, Mathf.Max(contentH, r.height));
            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(r, ref listScroll, view);
                opened = true;
                float y = 0f;
                // S18：生命线的第一段 —— 还没交给任何车队的表单
                if (drafts.Count > 0)
                {
                    RadiusFont.LabelAt(new Rect(0f, y, view.width - 40f, GroupH), "待下达",
                        RadiusFont.Scale.Section, Palette.Flat.Accent, TextAnchor.MiddleLeft, true, false);
                    RadiusFont.LabelAt(new Rect(0f, y, view.width, GroupH), drafts.Count.ToString(),
                        RadiusFont.Scale.Section, Palette.Flat.Accent, TextAnchor.MiddleRight, true, false);
                    y += GroupH;
                    for (int i = 0; i < drafts.Count; i++)
                    {
                        DrawDraftRow(new Rect(0f, y, view.width, RowH), i, drafts[i]);
                        y += RowH;
                    }
                }
                DrawBucket(view.width, ref y, "已固定", pinnedBucket);
                DrawBucket(view.width, ref y, "作业中", workingBucket);
                DrawBucket(view.width, ref y, "休息中", restingBucket);
                DrawBucket(view.width, ref y, "已暂停", pausedBucket);
                DrawBucket(view.width, ref y, "停摆", stalledBucket);
                // S12：远行队还在路上的计划（只读行：点一行选中那支远行队对应的计划）
                List<WorldObjectComp_Delegations> planned = DelegationRegistry.AllPlanned();
                if (planned.Count > 0)
                {
                    RadiusFont.LabelAt(new Rect(0f, y, view.width - 40f, GroupH), "前往中",
                        RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleLeft, true, false);
                    RadiusFont.LabelAt(new Rect(0f, y, view.width, GroupH), planned.Count.ToString(),
                        RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleRight, true, false);
                    y += GroupH;
                    for (int i = 0; i < planned.Count; i++)
                    {
                        DrawPlanRow(new Rect(0f, y, view.width, RowH), i, planned[i]);
                        y += RowH;
                    }
                }
                // S16：历史那一组**常驻**（0 条时给一句说明）—— 点一行，主列显示它的报告
                RadiusFont.LabelAt(new Rect(0f, y, view.width - 40f, GroupH), "历史",
                    RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleLeft, true, false);
                RadiusFont.LabelAt(new Rect(0f, y, view.width, GroupH), historyCount.ToString(),
                    RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleRight, true, false);
                y += GroupH;
                if (historyRows == 0)
                {
                    RadiusFont.LabelAt(new Rect(0f, y, view.width, RowH * 0.7f),
                        history == null ? "（还没有已结束的委派）" : "（还没有已结束的委派）",
                        RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
                    y += RowH * 0.7f;
                }
                else
                {
                    for (int i = 0; i < historyRows; i++)
                    {
                        DrawHistoryRow(new Rect(0f, y, view.width, RowH), history[i]);
                        y += RowH;
                    }
                }
                if (showNoMatch)
                {
                    RadiusFont.LabelAt(new Rect(0f, y + 4f, view.width, 22f), "RimDelegationConsoleNoMatch".Translate(),
                        RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
                }
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        /// <summary>
        /// 「前往中」那一组的一行（S12）：远行队 → 地点 / 委派 · 已决定|延后决定。
        /// 图标沿用 worker 的 gizmo 图标（数据驱动），点一行即选中它、右侧显示计划摘要。
        /// </summary>
        private static void DrawPlanRow(Rect row, int index, WorldObjectComp_Delegations comp)
        {
            Caravan caravan = comp?.plannedCaravan;
            Site site = comp?.Site;
            if (caravan == null)
            {
                return;
            }
            bool isSel = comp == selected;
            UIKit.Flat.TableRow(row, index, Mouse.IsOver(row));
            if (isSel)
            {
                Sdf.RoundedRect(row.ContractedBy(1f), Palette.ActiveWash, 6f);
                Sdf.RoundedRect(new Rect(row.x + 2f, row.y + 6f, 3f, row.height - 12f),
                    Palette.Flat.Accent, 1.5f);
            }

            float bodyH = RadiusFont.LineHAt(RadiusFont.Scale.Body, false);
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            Texture2D icon = null;
            try
            {
                icon = comp.plannedDef?.CreateWorker()?.GetGizmoIcon(site);
            }
            catch (Exception)
            {
                icon = null;
            }
            float textX = row.x + 12f;
            if (icon != null)
            {
                Rect iconRect = new Rect(row.x + 10f, row.y + (row.height - 24f) * 0.5f, 24f, 24f);
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);
                textX = iconRect.xMax + 10f;
            }
            float textW = row.xMax - textX - 12f;
            RadiusFont.LabelAt(new Rect(textX, row.y + 6f, textW, bodyH),
                (caravan.Name ?? "?") + "  →  " + (site?.Label ?? "?"),
                RadiusFont.Scale.Body, isSel ? Palette.Flat.Ink : Palette.Flat.InkMid,
                TextAnchor.MiddleLeft, false, false);
            RadiusFont.LabelAt(new Rect(textX, row.y + 6f + bodyH, Mathf.Max(60f, textW - 90f), metaH),
                comp.plannedDef?.label ?? "委派",
                RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
            RadiusFont.LabelAt(new Rect(textX, row.y + 6f + bodyH, textW, metaH),
                DelegationUIUtility.PlanStatusWord(comp),
                RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleRight, false, false);

            List<string> lines = DelegationUIUtility.PlanLines(comp);
            if (!lines.NullOrEmpty())
            {
                TooltipHandler.TipRegion(row, string.Join("\n", lines.ToArray()));
            }
            if (Widgets.ButtonInvisible(row))
            {
                selected = comp;
                // S15 第二期：点在途行 = 取消历史选中（两个详情互斥）
                selectedRecord = null;
                mainScroll = Vector2.zero;
            }
        }

        /// <summary>
        /// 「待下达」那一组的一行（S18）：远行队 → 地点 / 委派 · 未下达。
        ///
        /// 版式照抄「前往中」那一行（同一个三件套：图标 / 主行 / 副行右对齐状态），
        /// 只把状态与强调色换成 `Palette.Flat.Accent` —— 草稿没有进度条可用，颜色就是它唯一的身份标记。
        /// </summary>
        private static void DrawDraftRow(Rect row, int index, DelegationDraft draft)
        {
            if (draft == null)
            {
                return;
            }
            bool isSel = draft == selectedDraft;
            UIKit.Flat.TableRow(row, index, Mouse.IsOver(row));
            if (isSel)
            {
                Sdf.RoundedRect(row.ContractedBy(1f), Palette.ActiveWash, 6f);
                Sdf.RoundedRect(new Rect(row.x + 2f, row.y + 6f, 3f, row.height - 12f),
                    Palette.Flat.Accent, 1.5f);
            }

            float bodyH = RadiusFont.LineHAt(RadiusFont.Scale.Body, false);
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            Texture2D icon = null;
            try
            {
                icon = draft.def?.CreateWorker()?.GetGizmoIcon(draft.site);
            }
            catch (Exception)
            {
                icon = null;
            }
            float textX = row.x + 12f;
            if (icon != null)
            {
                Rect iconRect = new Rect(row.x + 10f, row.y + (row.height - 24f) * 0.5f, 24f, 24f);
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);
                textX = iconRect.xMax + 10f;
            }
            float textW = row.xMax - textX - 12f;
            RadiusFont.LabelAt(new Rect(textX, row.y + 6f, textW, bodyH),
                (draft.caravan?.Name ?? "?") + "  →  " + (draft.site?.Label ?? "?"),
                RadiusFont.Scale.Body, isSel ? Palette.Flat.Ink : Palette.Flat.InkMid,
                TextAnchor.MiddleLeft, false, false);
            RadiusFont.LabelAt(new Rect(textX, row.y + 6f + bodyH, Mathf.Max(60f, textW - 90f), metaH),
                draft.def?.label ?? "委派",
                RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
            RadiusFont.LabelAt(new Rect(textX, row.y + 6f + bodyH, textW, metaH),
                draft.onTile ? "未下达 · 就地开工" : "未下达",
                RadiusFont.Scale.Meta, Palette.Flat.Accent, TextAnchor.MiddleRight, false, false);

            TooltipHandler.TipRegion(row, draft.HeaderLine() + "\n（点击选中这张表单；底栏可确认下达或取消）");
            if (Widgets.ButtonInvisible(row))
            {
                SelectDraft(draft);
                mainScroll = Vector2.zero;
            }
        }

        private static void DrawBucket(float w, ref float y, string label, List<WorldObjectComp_Delegations> bucket)
        {
            RadiusFont.LabelAt(new Rect(0f, y, w - 40f, GroupH), label,
                RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleLeft, true, false);
            RadiusFont.LabelAt(new Rect(0f, y, w, GroupH), bucket.Count.ToString(),
                RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleRight, true, false);
            y += GroupH;
            for (int i = 0; i < bucket.Count; i++)
            {
                DrawRow(new Rect(0f, y, w, RowH), i, bucket[i]);
                y += RowH;
            }
        }

        private static void DrawRow(Rect row, int index, WorldObjectComp_Delegations comp)
        {
            Delegation d = comp?.active;
            Site site = comp?.Site;
            if (d == null)
            {
                return;
            }
            bool isSel = comp == selected;
            UIKit.Flat.TableRow(row, index, Mouse.IsOver(row));
            if (isSel)
            {
                Sdf.RoundedRect(row.ContractedBy(1f), Palette.ActiveWash, 6f);
                Sdf.RoundedRect(new Rect(row.x + 2f, row.y + 6f, 3f, row.height - 12f),
                    Palette.Flat.Accent, 1.5f);
            }

            float bodyH = RadiusFont.LineHAt(RadiusFont.Scale.Body, false);
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);

            // 活动图标：直接问 worker 要 gizmo 图标（RimDelegation 自己的数据，不猜）
            Texture2D icon = null;
            try
            {
                icon = d.Worker?.GetGizmoIcon(site);
            }
            catch (Exception)
            {
                icon = null;
            }
            float textX = row.x + 12f;
            if (icon != null)
            {
                Rect iconRect = new Rect(row.x + 10f, row.y + (row.height - 24f) * 0.5f, 24f, 24f);
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);
                textX = iconRect.xMax + 10f;
            }
            float textW = row.xMax - textX - 12f;

            string caravanName = d.caravan != null ? d.caravan.Name : "?";
            string siteLabel = site != null ? site.Label : "?";
            RadiusFont.LabelAt(new Rect(textX, row.y + 6f, textW, bodyH),
                caravanName + "  @  " + siteLabel,
                RadiusFont.Scale.Body, isSel ? Palette.Flat.Ink : Palette.Flat.InkMid,
                TextAnchor.MiddleLeft, false, false);

            float metaY = row.y + 6f + bodyH;
            string activity = d.Worker != null ? d.Worker.ActivityName : "?";
            RadiusFont.LabelAt(new Rect(textX, metaY, Mathf.Max(60f, textW - 96f), metaH),
                activity + " · " + DelegationUIUtility.StatusWord(d, site),
                RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);

            // 右端：剩余天数（拿不到就退回百分比）
            float days = site != null ? d.EstimatedDaysLeft(site.Tile) : -1f;
            string right = days >= 0f
                ? string.Format("剩余 {0:0.##} 天", days)
                : d.Progress.ToStringPercent();
            RadiusFont.LabelAt(new Rect(textX, metaY, textW, metaH), right,
                RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleRight, false, false);

            // 进度条（细的，自己铺两条圆角）
            float barY = metaY + metaH + 3f;
            Rect track = new Rect(textX, barY, textW, 5f);
            CardChrome.Rounded(track, Palette.Flat.Lift, 3f);
            float fillW = Mathf.Clamp01(d.Progress) * textW;
            if (fillW > 1f)
            {
                CardChrome.Rounded(new Rect(textX, barY, fillW, 5f), Palette.Flat.Skill, 3f);
            }

            if (Widgets.ButtonInvisible(row))
            {
                selected = comp;
                // S15 第二期：点在途行 = 取消历史选中（两个详情互斥）
                selectedRecord = null;
                mainScroll = Vector2.zero;
            }
        }

        // ================================================================ ③ 主列

        private static void DrawMain(Rect r)
        {
            // S18：选中的是「待下达」的表单 → 主列就是那张表单本身
            if (selectedDraft != null)
            {
                DrawDraftMain(r, selectedDraft);
                return;
            }
            // S15 第二期：选中历史记录 → 主列显示那份报告
            //（数据与签核窗口同一份 `DelegationRecord.ToReportData`，画法用 RadiusFont）
            if (selectedRecord != null)
            {
                RecordMain(r);
                return;
            }
            Delegation d = selected?.active;
            Site site = selected?.Site;
            if (d == null || site == null)
            {
                // S12：选中的是「前往中」的计划 —— 显示计划摘要 + 「取消计划 / 选中该远行队」
                if (selected != null && selected.HasPlan)
                {
                    DrawPlanMain(r, selected);
                    return;
                }
                // S17：什么都没有选中 —— 主列给引导（原来的整屏空态早退已删，空态落在这一块）
                if (selected == null)
                {
                    DrawNoSelection(r);
                    return;
                }
                RadiusFont.LabelAt(new Rect(r.x + 12f, r.y + 8f, r.width - 24f, 22f),
                    "RimDelegationConsoleNoSelection".Translate(),
                    RadiusFont.Scale.Body, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);
                return;
            }

            Rect inner = r.ContractedBy(10f);
            float viewW = inner.width - 16f;
            Cursor measure = new Cursor(false, new Rect(0f, 0f, viewW, 0f), 0f);
            MainPass(measure, d, site, selected);
            Rect view = new Rect(0f, 0f, viewW, Mathf.Max(measure.y, inner.height));

            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(inner, ref mainScroll, view);
                opened = true;
                MainPass(new Cursor(true, view, 0f), d, site, selected);
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        /// <summary>
        /// 主列的「待下达」页（S18/S19）。
        ///
        /// S19 起**表体也是 Radius 画法**（`DraftPass`）—— 之前直接调 RimDelegation 原版的
        /// `DelegationDraftUI.Draw`，用户看到的是"Radius 窗口里贴了一张原版表单"（他会截图指出）。
        /// 现在两个壳各自画：原版主控台走 `DelegationDraftUI`，皮肤走 `DraftPass`，
        /// **数据与校验仍然只有一份**（全在 `DelegationDraft` 的公开字段/方法上）。
        ///
        /// 两趟同一份代码 + `FlatScroll`：与在途主列（`MainPass`）完全同构 ——
        /// 所以草稿表单整列跟着主列一起滚，不需要内嵌滚动视图。
        /// </summary>
        private static void DrawDraftMain(Rect r, DelegationDraft draft)
        {
            if (draft == null || draft.def == null || draft.site == null)
            {
                return;
            }
            Rect inner = r.ContractedBy(10f);
            float titleH = RadiusFont.LineHAt(RadiusFont.Scale.Title, true);
            RadiusFont.LabelAt(new Rect(inner.x, inner.y, Mathf.Max(80f, inner.width - Widgets.InfoCardButtonSize - 6f), titleH),
                draft.HeaderLine(), RadiusFont.Scale.Title, Palette.Flat.Ink, TextAnchor.MiddleLeft, true, false);
            if (!draft.site.Destroyed)
            {
                Widgets.InfoCardButton(inner.x + inner.width - Widgets.InfoCardButtonSize, inner.y + 2f, draft.site);
            }

            Rect body = new Rect(inner.x, inner.y + titleH + 6f, inner.width,
                Mathf.Max(120f, inner.yMax - inner.y - titleH - 6f));
            float viewW = body.width - 16f;
            Cursor measure = new Cursor(false, new Rect(0f, 0f, viewW, 0f), 0f);
            DraftPass(measure, draft);
            Rect view = new Rect(0f, 0f, viewW, Mathf.Max(measure.y, body.height));

            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(body, ref draftScroll, view);
                opened = true;
                DraftPass(new Cursor(true, view, 0f), draft);
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        /// <summary>
        /// 皮肤版草稿表单（S19）：与在途详情**同一套** Radius 语言 ——
        /// 描述 / 可点行（模式 · 姿态 · 结束条件）/ 数值步进 / 勾选 / 两个列表 / 汇总。
        ///
        /// ⚠️ 一行逻辑都不重写：取值全部来自公开的 `DelegationDraft`
        /// （`ModeLine / ApproachLine / EndConditionLabel / ChosenList / QuotaCap / TryMassForecast /
        /// OpenModeMenu / OpenApproachMenu / OpenEndConditionMenu`），
        /// 与 `DelegationDraftUI`（原版那一份）共用同一批方法 —— 新增字段时两处都要看一眼，
        /// 这就是"同一份数据、两套画法"的代价。
        /// </summary>
        private static void DraftPass(Cursor c, DelegationDraft draft)
        {
            DelegationWorker worker = draft.Worker;
            if (worker == null)
            {
                c.Line("这条委派的 worker 类找不到了（Def 被改名或删除？）", RadiusFont.Scale.Body, Palette.Bad);
                return;
            }
            float lhBody = RadiusFont.LineHAt(RadiusFont.Scale.Body, false);
            float lhMeta = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            // 已选人员与"可点行"这两样跨了三段 ⇒ 声明必须提到三段之外
            //（S25 分段后留在某一段里的话，另一段就看不见了 —— 这正是编译器当场报的三个错）
            List<Pawn> chosen = draft.ChosenList();

            // ---- 可点行：与右栏「概览」的可点行同一套版式（主行 + 副行 + 右端"切换"）
            void ClickRow(string icon, string subIcon, string main, string sub, bool clickable, string tip,
                Action onClick)
            {
                float h = lhBody + lhMeta + 6f;
                Rect row = new Rect(c.view.x, c.y, c.w, h);
                bool hover = clickable && Mouse.IsOver(row);
                if (c.draw)
                {
                    if (clickable)
                    {
                        UIKit.Flat.TableRow(row, 0, hover);
                    }
                    float tx = row.x + 6f;
                    RadiusIcon ic = IconSet.Get(icon);
                    if (ic.Exists)
                    {
                        ic.Draw(new Rect(tx, row.y + 4f, 16f, 16f),
                            clickable ? Palette.Flat.Accent : Palette.Flat.InkMid);
                        tx += 22f;
                    }
                    float tw = Mathf.Max(40f, row.xMax - 8f - tx - (clickable ? 34f : 0f));
                    RadiusFont.LabelAt(new Rect(tx, row.y + 2f, tw, lhBody), main,
                        RadiusFont.Scale.Body, Palette.Flat.Ink, TextAnchor.MiddleLeft, false, false);
                    float subX = tx;
                    if (!subIcon.NullOrEmpty())
                    {
                        RadiusIcon sic = IconSet.Get(subIcon);
                        if (sic.Exists)
                        {
                            sic.Draw(new Rect(tx, row.y + 4f + lhBody + (lhMeta - 12f) * 0.5f, 12f, 12f),
                                Palette.Flat.InkLow);
                            subX = tx + 16f;
                        }
                    }
                    RadiusFont.LabelAt(new Rect(subX, row.y + 4f + lhBody, Mathf.Max(30f, tx + tw - subX), lhMeta), sub,
                        RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
                    if (clickable)
                    {
                        RadiusFont.LabelAt(new Rect(row.xMax - 40f, row.y, 32f, h), "切换",
                            RadiusFont.Scale.Meta, hover ? RadiusTheme.Accent : Palette.Flat.InkLow,
                            TextAnchor.MiddleRight, false, false);
                    }
                    if (!tip.NullOrEmpty())
                    {
                        TooltipHandler.TipRegion(row, tip);
                    }
                }
                if (c.draw && clickable && Widgets.ButtonInvisible(row))
                {
                    onClick?.Invoke();
                }
                c.y += h + 2f;
            }

            // ---- ① 作战任务（S24，**恒常**）：与原版草稿页、主列同一份数据与文案
            bool showCombat = SectionCollapsible(c, DelegationUIUtility.SectionCombat,
                DelegationUIUtility.SectionId.Combat);
            if (showCombat)
            {
            DelegationThreatSummary ds = draft.threatSummary;
            // S26：**待下达这一屏不揭露守军情报**（用户：「待下达状态的时候，敌方编队和威胁点数就揭露了
            // -- 不要揭露」）—— 情报要靠派人过去侦察换来。
            ds.revealed = false;
            ds.Ensure(draft.caravan, draft.site, draft.ChosenList(), draft.approach, false);
            List<string> dCombat = ds.Lines();
            for (int i = 0; i < dCombat.Count; i++)
            {
                c.Wrapped(dCombat[i], RadiusFont.Scale.Meta, Palette.Flat.InkMid);
            }
            if (!draft.def.approaches.NullOrEmpty())
            {
                // 姿态是作战概念 ⇒ 归这一段（"模式"是作业作息，留在收集任务里）
                ClickRow("Common/Sword", null, "作战姿态：" + draft.ApproachLine(), "决定这一趟怎么结算战斗", true,
                    "点击切换作战姿态（强攻 / 潜入；潜入会掷暴露）", draft.OpenApproachMenu);
            }
            if (ds.revealed && ds.canAssess)
            {
                const float DBtnW = 104f;
                if (c.draw)
                {
                    if (UIKit.Button(new Rect(c.view.x, c.y, DBtnW, 24f), "重新推算",
                            ButtonStyle.Solid, true, "重新采样一次成算（蒙特卡洛）"))
                    {
                        ds.Reroll(draft.caravan, draft.site, draft.ChosenList(), draft.approach);
                    }
                    if (UIKit.Button(new Rect(c.view.x + DBtnW + 8f, c.y, DBtnW, 24f), "威胁评估",
                            ButtonStyle.Solid, true, "打开完整的威胁评估面板（编队 / 预告 / 单场推演与日志）"))
                    {
                        ThreatAssessmentEntry.Open(draft.caravan, draft.site,
                            draft.Worker?.ApproachFirstStrikePenalty(draft.approach) ?? 0f);
                    }
                }
                c.y += 28f;
            }
            c.Gap();

            }   // showCombat

            c.Gap();   // S29：三段之间留空隙（用户报「作战任务/收集任务/远行队信息 之间没有空隙」）

            // ---- ② 收集任务（描述 / 规模 / 作业模式 / 结束条件 / 进度 / 物资 / 预期获得）
            bool showCollect = SectionCollapsible(c, DelegationUIUtility.SectionCollect,
                DelegationUIUtility.SectionId.Collect);
            if (showCollect)
            {
            // ---- 描述 + 规模说明
            c.Wrapped(draft.def.description, RadiusFont.Scale.Body, Palette.Flat.InkMid);
            string previewText = worker.PreviewLabel(draft.site, draft.preview, draft.exactDeposit);
            if (!previewText.NullOrEmpty())
            {
                c.Gap();
                c.Wrapped(previewText, RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            }
            c.Gap();

            // ---- 可点行见方法开头的 `ClickRow`（S25 提到三段之外）
            // RIM-5：模式行不再报"速率"（模式不提供效率），心情与速率都说满意度那一行。
            // 草稿里还没有 Delegation 实例 ⇒ 用预计口径（吃喝与在外时间未知时取中性）。
            float mood = DelegationUtility.DailyMoodOffset(draft.def, draft.mode);
            ClickRow("Action/Snooze", mood < 0f ? "Stat/TrendDown" : "Stat/Mood", draft.ModeLine(),
                DelegationUIUtility.SatisfactionLineEstimated(draft.mode), true,
                "点击切换委派模式（作息窗口 / 作业强度）——换班本身不扣心情，满意度按新模式重算", draft.OpenModeMenu);

            ClickRow("Action/Check", draft.abortWhenOutOfFood ? "Alert/Warning" : "Stat/Food",
                "结束条件：" + draft.EndConditionLabel(),
                draft.abortWhenOutOfFood ? "补给耗尽时中止" : string.Format("饿着也继续{0}", worker.ActivityName),
                true, "点击切换结束条件（取尽 / 按天数 / 按产出配额）", draft.OpenEndConditionMenu);

            if (draft.endCondition == DelegationEndCondition.Days)
            {
                Stepper(c, "干满天数", ref draft.daysLimit, 1, Mathf.Max(1, draft.def.maxDaysLimit), "天");
            }
            else if (draft.endCondition == DelegationEndCondition.Quota)
            {
                Stepper(c, "产出配额", ref draft.quotaUnits, 1, draft.QuotaCap(), worker.OutputUnitName);
            }
            // S21：用户书面语清单里对"取尽"这一档写着「！删除这个描述！」——
            // 「结束条件：搬空为止」上面那行已经说清了，"无需设定：一直干到…"纯属重复。

            // 勾选：皮肤框架里没有勾选件（S15 那张「结束报告」开关也是这么处理的），
            // 先用原版 `Widgets.CheckboxLabeled`，等框架补上件再换 —— 这里刻意保留原版观感，
            // 因为一个"选不选中"的语义用自绘方块反而更容易被误读。
            {
                const float h = 26f;
                if (c.draw)
                {
                    Widgets.CheckboxLabeled(new Rect(c.view.x, c.y, c.w, h),
                        string.Format("补给耗尽时中止（取消勾选 = 饿着也继续{0}）", worker.ActivityName),
                        ref draft.abortWhenOutOfFood);
                }
                c.y += h;
            }
            c.Gap();
            float perDay = draft.mode == null
                ? 0f
                : worker.EstimateUnitsPerDayFor(chosen, draft.mode, draft.site.Tile, draft.site,
                    DelegationSatisfaction.RateFactor(DelegationSatisfaction.EstimatedValue(draft.mode, 0f)));

            // ---- 总进度（S20：与在途详情**同款的一行**）
            // 草稿没有"已完成的量"，但**分母是有的**（预览区间 dispMin/Max）⇒ `0/40 件` 不是假数字；
            // 真正算不出来的只有"完成时间"，那时才写"无法估算"（用户口径：条目要在，不知道就说不知道）。
            c.Section("RimDelegationTabTotalProgress".Translate());
            string tail = string.Format("0/{0} {1}", draft.dispMaxCells, worker.UnitName);
            tail += perDay > 0f
                ? string.Format(" · 预计完成时间：{0:0.#}–{1:0.#} 天",
                    draft.dispMinCells / perDay, draft.dispMaxCells / perDay)
                : " · 预计完成时间：无法估算";
            c.Progress(tail, 0f, draft.exactDeposit != null
                ? "这一处的底细已经摸清了 ⇒ 上面是按实际规模算的"
                : "这一处的底细要等队伍到了才清楚，所以分母给的是上限");
            c.Line(string.Format("RimDelegationTabHours".Translate(), 0f, 0f) + "（尚未开工）",
                RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            c.Gap();

            // ---- 预期获得（清单读不到时 worker 返回 null ⇒ 整块不画，由下面的说明行解释"未定"）
            // ⚠️ S22：**先取清单再写说明行** —— 说明行的措辞取决于清单到底有没有被门控挡掉。
            List<DelegationPreviewItem> items = null;
            try
            {
                items = worker.PreviewItems(draft.site, draft.preview, draft.exactDeposit);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[RimDelegation] 皮肤：草稿页「预期获得」绘制失败：" + e.Message, 0x5E0F4);
            }

            // ---- 现场物资（同款分区标题）：草稿这一格**未知** ——
            // 清点要人到现场才能做，所以写明"未知"，而不是把整块藏掉（重排 ≠ 删信息）。
            c.Section("现场物资");
            c.Wrapped(DraftItemsUnknownLine(draft),
                RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            c.Gap();

            if (!items.NullOrEmpty())
            {
                c.Section(string.Format("预期获得（{0} 项）", items.Count));
                for (int i = 0; i < items.Count; i++)
                {
                    DrawPreviewItemRow(c, items[i]);
                }
                c.Gap();
            }

            // ---- 参与者（S20：与在途详情**同款** —— 同一个分区标题、同一种 V/X 行、同一批按钮位）
            // 草稿的"参加"就是"这一趟谁去"，语义与在途完全一致；只有按钮不同：
            // 原版的「添加人员 / 查看全部」需要 `Delegation` 实例，草稿还没有，换成 全选 / 全不选。
            {
                const float SortW = 86f;
                const float AllW = 56f;
                const float NoneW = 66f;
                Rect pRight = c.SectionRow(
                    string.Format("RimDelegationTabParticipants".Translate(), draft.selected.Count),
                    SortW + AllW + NoneW + 12f);
                if (c.draw)
                {
                    if (UIKit.Button(new Rect(pRight.x, pRight.y, SortW, pRight.height),
                            "排序：" + DelegationUIUtility.PawnSortShort(draft.sortMode), ButtonStyle.Solid, true,
                            "参与者排序：技能（高→低）→ 名字 → 心情（差→好）"))
                    {
                        draft.sortMode = DelegationUIUtility.NextSort(draft.sortMode);
                        draft.SortCandidates();
                    }
                    bool canAll = draft.selected.Count < draft.candidates.Count;
                    if (UIKit.Button(new Rect(pRight.x + SortW + 6f, pRight.y, AllW, pRight.height), "全选",
                            ButtonStyle.Solid, canAll, canAll ? "勾上队里所有可参加的人" : "已经全勾上了"))
                    {
                        for (int i = 0; i < draft.candidates.Count; i++)
                        {
                            draft.selected.Add(draft.candidates[i]);
                        }
                    }
                    if (UIKit.Button(new Rect(pRight.x + SortW + AllW + 12f, pRight.y, NoneW, pRight.height),
                            "全不选", ButtonStyle.Solid, draft.selected.Count > 0, "清空勾选（一个都不派）"))
                    {
                        draft.selected.Clear();
                    }
                }
                int shown = DrawDraftRosterRows(c, draft, worker, DelegationUIUtility.RowHeight);
                if (shown == 0)
                {
                    c.Line("远行队里没有可参加委派的人员。", RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                }
                c.Gap();
            }

            }   // showCollect

            c.Gap();   // S29：段间空隙

            // ---- ③ 远行队信息（S27 起：远行队行 / 补给品；参与者已归「收集任务」）
            bool showCaravan = SectionCollapsible(c, DelegationUIUtility.SectionCaravan,
                DelegationUIUtility.SectionId.Caravan);
            if (showCaravan)
            {

            // ---- 远行队（S20：与在途详情同一个分区 —— 离开工 / 疲劳 / 补给品行）
            c.Section("远行队");
            c.Wrapped("距离开工：未知（委派尚未下达）", RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            c.Wrapped(DelegationUIUtility.FatigueRiskLineOf(chosen), RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            c.Wrapped(DelegationUIUtility.MealLine(draft.caravan), RadiusFont.Scale.Meta, Palette.Flat.InkLow);

            // ---- 远行队补给品（同款列表；没有就明说 —— 对照在途详情那一块）
            List<DelegationPreviewItem> foodItems = DelegationUIUtility.CaravanFoodItems(draft.caravan);
            if (!foodItems.NullOrEmpty())
            {
                c.Section(DelegationUIUtility.FoodItemsHeader(foodItems));
                for (int i = 0; i < foodItems.Count; i++)
                {
                    DrawPreviewItemRow(c, foodItems[i]);
                }
                c.Gap();
            }
            else if (draft.caravan != null)
            {
                c.Section("远行队补给品");
                c.Line("（远行队里没有可吃的补给品）", RadiusFont.Scale.Meta, Palette.Bad);
                c.Gap();
            }

            // ---- 姿态成算（潜入暴露概率之类）：它是"现在下不下单"的最后一个决策变量
            string forecast = worker.ApproachForecast(draft.site, chosen, draft.approach);
            if (!forecast.NullOrEmpty())
            {
                c.Line(forecast, RadiusFont.Scale.Meta, Palette.Flat.InkMid);
            }
            }   // showCaravan
        }

        /// <summary>数值步进一行（[-] 值 [+]，用于"按天数 / 按产出配额"两档结束条件）。</summary>
        private static void Stepper(Cursor c, string label, ref int value, int min, int max, string unit)
        {
            const float h = 28f;
            const float BtnW = 28f;
            Rect row = new Rect(c.view.x, c.y, c.w, h);
            value = Mathf.Clamp(value, min, Mathf.Max(min, max));
            if (c.draw)
            {
                RadiusFont.LabelAt(new Rect(row.x + 6f, row.y, Mathf.Max(80f, row.width - 180f), h), label,
                    RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);
                Rect minus = new Rect(row.xMax - 168f, row.y + 1f, BtnW, h - 2f);
                Rect plus = new Rect(row.xMax - BtnW - 2f, row.y + 1f, BtnW, h - 2f);
                if (UIKit.Button(minus, "-", ButtonStyle.Solid, value > min, null))
                {
                    value--;
                }
                if (UIKit.Button(plus, "+", ButtonStyle.Solid, value < max, null))
                {
                    value++;
                }
                string text = unit.NullOrEmpty() ? value.ToString() : value + " " + unit;
                RadiusFont.LabelAt(new Rect(minus.xMax + 4f, row.y, plus.x - minus.xMax - 8f, h), text,
                    RadiusFont.Scale.Body, Palette.Flat.Ink, TextAnchor.MiddleCenter, false, false);
            }
            c.y += h + 2f;
        }

        /// <summary>
        /// S22：草稿页「现场物资」那一行的说明。
        ///
        /// 清单被 `DelegationDef.hideItemsUntilPhase` 门控住时**不能**再说"以下是本趟预计可获取的产出物"
        /// —— 那份清单已经不画了（用户口径：「这部分需要完成破门才给玩家看预期获得」）。
        /// 措辞从 Def 里取那一段的 `label` 拼出来，所以改 XML 里的段名这一行会跟着变。
        /// </summary>
        private static string DraftItemsUnknownLine(DelegationDraft draft)
        {
            string gate = draft?.def?.hideItemsUntilPhase;
            if (!gate.NullOrEmpty())
            {
                DelegationPhaseDef phase = DefDatabase<DelegationPhaseDef>.GetNamedSilentFail(gate);
                return phase != null
                    ? string.Format("未知——要等「{0}」之后才能确认现场有什么。", phase.label)
                    : "未知——要等前置流程走完之后才能确认现场有什么。";
            }
            return "未知——需抵达现场后方可确认；以下为本趟预计可获取的产出物。";
        }

        /// <summary>草稿页的「预期获得」一行：图标 + 名称 + 右对齐明细（Radius 画法）。</summary>
        private static void DrawPreviewItemRow(Cursor c, DelegationPreviewItem item)
        {
            if (item == null)
            {
                return;
            }
            const float h = 26f;
            const float Icon = 20f;
            Rect row = new Rect(c.view.x, c.y, c.w, h);
            if (c.draw)
            {
                Rect iconRect = new Rect(row.x + 6f, row.y + (h - Icon) * 0.5f, Icon, Icon);
                if (item.pawn != null)
                {
                    GUI.DrawTexture(iconRect, PortraitsCache.Get(item.pawn, new Vector2(Icon, Icon), Rot4.South));
                }
                else if (item.thingDef != null)
                {
                    Widgets.ThingIcon(iconRect, item.thingDef, null, null, 1f, null, null, 1f);
                }
                string detail = item.detail ?? "";
                float detailW = detail.NullOrEmpty()
                    ? 0f
                    : RadiusFont.WidthAt(detail, RadiusFont.Scale.Meta, false) + 8f;
                float tx = iconRect.xMax + 6f;
                float labelW = Mathf.Max(40f, row.xMax - 8f - tx - detailW);
                RadiusFont.LabelAt(new Rect(tx, row.y, labelW, h), item.label ?? "",
                    RadiusFont.Scale.Meta, item.uncertain ? Palette.Flat.InkLow : Palette.Flat.InkMid,
                    TextAnchor.MiddleLeft, false, false);
                if (!detail.NullOrEmpty())
                {
                    RadiusFont.LabelAt(new Rect(tx + labelW, row.y, Mathf.Max(30f, row.width - labelW), h), detail,
                        RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleRight, false, false);
                }
                TooltipHandler.TipRegion(row, (item.label ?? "") + "\n" + detail);
            }
            c.y += h;
        }

        /// <summary>
        /// 草稿页的参与者行（S20）：与在途详情那一批**同款** —— 最前面 18px 的 V/X 开关
        /// （`DelegationUIUtility.DrawParticipantToggle`，两处共用同一个件），后面是人物行
        /// （`DelegationUIUtility.DrawPawnLine` + `PawnLine`：头像 / 名字 / 负重 / 心情与受伤徽章全同源）。
        ///
        /// 为什么不再自绘"勾选块 + 头像"：用户明确要求「参与者用和休息中/作业中的一样的」，
        /// 而"一样"的唯一判据就是两处调**同一批函数** —— 自己画一套只会"长得像、细节不一样"。
        /// 返回画了几行（调用方用它决定要不要画"—"）。
        /// </summary>
        private static int DrawDraftRosterRows(Cursor c, DelegationDraft draft, DelegationWorker worker, float rowH)
        {
            if (draft.candidates.Count == 0)
            {
                return 0;
            }
            for (int i = 0; i < draft.candidates.Count; i++)
            {
                Pawn p = draft.candidates[i];
                if (p == null)
                {
                    continue;
                }
                bool sel = draft.selected.Contains(p);
                if (c.draw)
                {
                    Rect row = new Rect(c.view.x, c.y, c.w, rowH);
                    if (Mouse.IsOver(row))
                    {
                        Widgets.DrawHighlight(row);
                    }
                    Rect toggle = new Rect(row.x, row.y + (row.height - 18f) * 0.5f, 18f, 18f);
                    Rect line = new Rect(row.x + 24f, row.y, row.width - 24f, row.height);

                    Color old = GUI.color;
                    if (!sel)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.62f);   // 不参加的整行压暗（与在途那边一致）
                    }
                    DelegationUIUtility.DrawPawnLine(line, p, DelegationUIUtility.PawnLine(p, worker, draft.def));
                    GUI.color = old;

                    Rect click = DelegationUIUtility.LabelRectFor(line);
                    TooltipHandler.TipRegion(click, sel
                        ? "点击把这名队员从这次委派里去掉"
                        : "点击把这名队员加进这次委派");
                    if (Widgets.ButtonInvisible(click))
                    {
                        SetDraftPawn(draft, p, !sel);
                    }
                    if (DelegationUIUtility.DrawParticipantToggle(toggle, sel, p, true))
                    {
                        SetDraftPawn(draft, p, !sel);
                    }
                }
                c.y += rowH;
            }
            return draft.candidates.Count;
        }

        private static void SetDraftPawn(DelegationDraft draft, Pawn p, bool participating)
        {
            if (participating)
            {
                draft.selected.Add(p);
            }
            else
            {
                draft.selected.Remove(p);
            }
        }

        /// <summary>
        /// 「前往中」计划的主列（S20）：与在途详情**同一套分块**（总进度 / 现场物资 / 参与者 /
        /// 远行队 / 补给品）—— 计划还不知道的一律写「未知」，**条目一个不少**
        ///（用户口径：「前往中也适用 UI，不知道的信息就填未知」）。
        ///
        /// 与本文件其它主列一样：两趟同一份代码 + `FlatScroll`。数据全部来自公开件
        /// （`plannedDef` / `plannedRequest` / `PlanLines` / `FatigueRiskLineOf` / `CaravanFoodItems`），
        /// 所以"计划态"和"在途态"看到的同一格内容永远同源。
        /// </summary>
        private static void PlanPass(Cursor c, WorldObjectComp_Delegations comp)
        {
            DelegationDef def = comp.plannedDef;
            DelegationRequest req = comp.plannedRequest;
            DelegationWorker worker = def?.CreateWorker();
            Caravan caravan = comp.plannedCaravan;
            Site site = comp.Site;
            bool decided = req != null;

            // 标题 + 地点信息卡（与在途主列同款）
            float infoSize = Widgets.InfoCardButtonSize;
            float titleH = Mathf.Max(24f, RadiusFont.LineHAt(RadiusFont.Scale.Title, true));
            if (c.draw)
            {
                RadiusFont.LabelAt(new Rect(c.view.x, c.y, Mathf.Max(80f, c.w - infoSize - 8f), titleH),
                    string.Format("RimDelegationTabTitle".Translate(), def?.label ?? "?", site?.Label ?? "?"),
                    RadiusFont.Scale.Title, Palette.Flat.Ink, TextAnchor.MiddleLeft, true, false);
                if (site != null && !site.Destroyed)
                {
                    Widgets.InfoCardButton(c.view.x + c.w - infoSize, c.y + 2f, site);
                }
            }
            c.y += titleH + 4f;

            c.Wrapped(def?.description, RadiusFont.Scale.Body, Palette.Flat.InkMid);
            c.Gap();

            // ---- ① 作战任务（S24，恒常）
            //      计划期只给**事实**（守军 / 编队 / 能否评估）：参与者选了"延后决定"时还没定下来，
            //      这时候给成算等于给一个假承诺。要推算就等开工后在主列点「重新推算」。
            bool showCombat = SectionCollapsible(c, DelegationUIUtility.SectionCombat,
                DelegationUIUtility.SectionId.Combat);
            if (showCombat)
            {
            comp.threatSummary.Ensure(caravan, site, req?.pawns, req?.approach, false);
            List<string> planCombat = comp.threatSummary.Lines();
            for (int i = 0; i < planCombat.Count; i++)
            {
                c.Wrapped(planCombat[i], RadiusFont.Scale.Meta, Palette.Flat.InkMid);
            }
            c.Gap();

            }   // showCombat

            c.Gap();   // S29：三段之间留空隙

            // ---- ② 收集任务（计划摘要 / 进度 / 现场物资）
            bool showCollect = SectionCollapsible(c, DelegationUIUtility.SectionCollect,
                DelegationUIUtility.SectionId.Collect);
            if (showCollect)
            {

            // 计划状态那几句（唯一来源：DelegationUIUtility.PlanLines，与原版主控台同一份）
            List<string> plan = DelegationUIUtility.PlanLines(comp);
            if (!plan.NullOrEmpty())
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    c.Wrapped(plan[i], RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                }
            }
            c.Gap();

            // 总进度：还没开工 ⇒ 值写"未知"，但条目要在（与草稿页同一口径）
            c.Section("RimDelegationTabTotalProgress".Translate());
            c.Progress("未知", 0f, "这条委派还没有开工，所以没有进度可算");
            c.Line(string.Format("RimDelegationTabHours".Translate(), 0f, 0f) + "（尚未开工）",
                RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            c.Gap();

            // 现场物资：清点要人到现场
            c.Section("现场物资");
            c.Wrapped("未知——需抵达现场后方可确认。", RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            c.Gap();

            }   // showCollect

            c.Gap();   // S29：段间空隙

            // ---- ③ 远行队信息（S24）：参与者 / 远行队行 / 补给品都归这一段
            bool showCaravan = SectionCollapsible(c, DelegationUIUtility.SectionCaravan,
                DelegationUIUtility.SectionId.Caravan);
            if (showCaravan)
            {

            // 参与者：已决定就照在途同款画（**只读** —— 计划期要改人是"取消这条计划、重新下达"）
            c.Section(string.Format("RimDelegationTabParticipants".Translate(), req?.pawns?.Count ?? 0));
            if (!decided || req.pawns.NullOrEmpty())
            {
                c.Line("未知——需队伍抵达后确认。", RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            }
            else
            {
                for (int i = 0; i < req.pawns.Count; i++)
                {
                    Pawn p = req.pawns[i];
                    if (p == null)
                    {
                        continue;
                    }
                    float rowH = DelegationUIUtility.RowHeight;
                    if (c.draw)
                    {
                        DelegationUIUtility.DrawPawnLine(new Rect(c.view.x, c.y, c.w, rowH), p,
                            DelegationUIUtility.PawnLine(p, worker, def));
                    }
                    c.y += rowH;
                }
            }
            c.Gap();

            // 远行队（与在途/草稿同一个分区）
            c.Section("远行队");
            c.Wrapped("距离开工：未知（委派尚未开工）", RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            if (decided && !req.pawns.NullOrEmpty())
            {
                c.Wrapped(DelegationUIUtility.FatigueRiskLineOf(req.pawns), RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            }
            c.Wrapped(DelegationUIUtility.MealLine(caravan), RadiusFont.Scale.Meta, Palette.Flat.InkLow);

            // 补给品列表（同款；没有就明说）
            List<DelegationPreviewItem> foodItems = DelegationUIUtility.CaravanFoodItems(caravan);
            if (!foodItems.NullOrEmpty())
            {
                c.Section(DelegationUIUtility.FoodItemsHeader(foodItems));
                for (int i = 0; i < foodItems.Count; i++)
                {
                    DrawPreviewItemRow(c, foodItems[i]);
                }
                c.Gap();
            }
            else if (caravan != null)
            {
                c.Section("远行队补给品");
                c.Line("（远行队里没有可吃的补给品）", RadiusFont.Scale.Meta, Palette.Bad);
                c.Gap();
            }
            }   // showCaravan
        }

        /// <summary>
        /// 「前往中」计划的主列视图（S12→S20）：两趟同一份代码 + `FlatScroll` + <see cref="PlanPass" />。
        ///
        /// S19 之前这里只有 `PlanLines` 几行字、外加一对与全局底栏重复的动作按钮（用户截图指出）；
        /// 现在主列只负责"读"，动作全在底栏（`DrawPlanBottomBar`），分块与在途详情对齐。
        /// </summary>
        private static void DrawPlanMain(Rect r, WorldObjectComp_Delegations comp)
        {
            Rect inner = r.ContractedBy(10f);
            float viewW = inner.width - 16f;
            Cursor measure = new Cursor(false, new Rect(0f, 0f, viewW, 0f), 0f);
            PlanPass(measure, comp);
            Rect view = new Rect(0f, 0f, viewW, Mathf.Max(measure.y, inner.height));

            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(inner, ref planScroll, view);
                opened = true;
                PlanPass(new Cursor(true, view, 0f), comp);
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        /// <summary>
        /// 参与者名单的一批行（S13）：每行最前面是 V/X 开关，后面是原版那一行人物信息。
        ///
        /// 两个批次共用这一份：<paramref name="participating" /> = true 是"当前参加的人"（打 ✓），
        /// false 是"队里还能参加的人"（打 ×，整行压暗一点以表示未参加）。
        /// 返回画了几行（调用方用它决定要不要画"—"）。
        /// </summary>
        private static int DrawRosterRows(Cursor c, Delegation d, DelegationWorker worker, List<Pawn> list,
            bool participating, bool canToggle, float rowH)
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
                if (c.draw)
                {
                    Rect row = new Rect(c.view.x, c.y, c.w, rowH);
                    if (Mouse.IsOver(row))
                    {
                        Widgets.DrawHighlight(row);
                    }
                    // 开关占最前面 18px（用户要求"参与者的最前面"），人物行右移 24px
                    Rect toggle = new Rect(row.x, row.y + (row.height - 18f) * 0.5f, 18f, 18f);
                    Rect line = new Rect(row.x + 24f, row.y, row.width - 24f, row.height);

                    Color old = GUI.color;
                    if (!participating)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.62f);   // 没参加的整行压暗
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
                c.y += rowH;
                drawn++;
            }
            return drawn;
        }

        // ================================================================ S29：悬浮焦点（左栏 / 流程栏）

        /// <summary>鼠标焦点此刻落在哪一栏（<see cref="UpdateColumnFocus" /> 每帧算）。</summary>
        private enum ColumnFocus
        {
            None,
            Left,
            Flow,
        }

        private static ColumnFocus columnFocus = ColumnFocus.None;

        /// <summary>上一帧**实际**的左栏 / 流程栏矩形（含列头那一行）。</summary>
        private static Rect lastListRect;

        private static Rect lastFlowRect;

        /// <summary>
        /// S29：每帧更新"焦点在哪一栏"。用户原话：「验证是否可以采取焦点的做法，如果鼠标悬浮在流程，
        /// 则展开流程，折叠左栏；如果鼠标悬浮在左栏，则展开左栏」。
        ///
        /// S34 起两栏**不对称**（用户原话：「请修改成这样：鼠标悬浮在流程的时候展开流程，然后折叠左栏。
        /// 鼠标悬浮在左栏的时候展开左栏，但是不折叠流程」）：焦点在左栏时**只**展开左栏，
        /// 流程栏不再被收掉 —— 这一条由调用点（<see cref="Draw" />）负责，本函数只管"焦点落哪一栏"。
        ///
        /// 判据用**上一帧的实际列矩形**，三个都是防抖动的理由：
        ///   ① 粘滞 —— 若用"两栏都展开"的理想几何，会在交界处自激：展开 → 形状变 → 鼠标出界 →
        ///      收起 → 又落回旧区……一帧一个样；用上一帧实际的矩形，鼠标在展开后的那一栏里就保持焦点；
        ///   ② 流程栏切到焦点态时**右边缘不动**（S22 的 `flowCap` 把左栏让出的宽度抬给流程栏 ⇒
        ///      流程栏的 x 从左栏展开位（`listBaseW + 1`）跳到左栏收起位（`StripW + 1`）、
        ///      宽度加上等量的差值，右边缘恒等于"左栏展开时流程栏的右边缘"）
        ///      ⇒ 鼠标落在流程栏右半时不会因为这一栏变窄而把自己挤出矩形；
        ///   ③ 含列头那 22px —— 否则"移上去点列头开关"的途中布局会先弹回去，开关从指头下面跑掉。
        ///
        /// 只在**两栏都是展开状态**时生效：用户特意收起某一栏之后，鼠标路过不该把它又弹开
        /// （那种情况直接不介入，`collapsed*` 说了算）。传进来的 <paramref name="colLeft" /> /
        /// <paramref name="colFlow" /> 是**已把图钉算进去**的生效折叠态 —— 被钉住的栏按定义是展开的，
        /// 所以钉住左栏不会把整套悬浮焦点关掉（只是左栏不再被收起，见 Draw 里的 `!pinLeft`）。
        /// 鼠标离开两栏 ⇒ 归 None，同样交还给用户设置。
        /// </summary>
        private static void UpdateColumnFocus(RimDelegationSettings skin, bool colLeft, bool colFlow)
        {
            if (skin == null || !skin.hoverFocus || colLeft || colFlow)
            {
                columnFocus = ColumnFocus.None;
                return;
            }
            if (lastFlowRect.width > 1f && Mouse.IsOver(lastFlowRect))
            {
                columnFocus = ColumnFocus.Flow;
                return;
            }
            if (lastListRect.width > 1f && Mouse.IsOver(lastListRect))
            {
                columnFocus = ColumnFocus.Left;
                return;
            }
            columnFocus = ColumnFocus.None;
        }

        // ================================================================ ③-a 列头（折叠开关）

        /// <summary>
        /// S22：四个区域的折叠开关（左栏 / 流程 / 主信息 / 概览）—— 用户问的那句
        /// 「最左边的过滤器，流程，中间的核心信息框，概览这几个显示上是否可以进行折叠？」。
        ///
        /// 为什么统一放在"列头"这一条 22px 里、而不是各列自己的标题行：四列的内部标题行结构完全不同
        /// （左栏是状态行、流程栏是位置行 + Section、主列是地点标题 + i 按钮、右栏是卡片栈），
        /// 逐个改造会四处分叉；统一一条列头则只有一个实现。
        ///
        /// 折叠状态存**本地 Mod 配置**（`RimDelegationSettings.collapsed*`）；点击当帧即写，
        /// 布局在下一帧生效（一帧延迟，肉眼无感）。图钉（S34）同住一份配置。
        /// </summary>
        /// <param name="flowHead">
        /// 流程栏的开关槽位：显示时就是流程栏本身，**手动折叠时是左栏右边那 26px**，
        /// 自动回落（窗口太窄）时是 <see cref="Rect.zero" />（不给开关）。
        /// </param>
        private static void DrawColumnHeads(Rect head, Rect listRect, Rect flowHead, Rect mainRect, Rect railRect)
        {
            RimDelegationSettings skin = RimDelegationMod.Settings;
            if (skin == null)
            {
                return;
            }
            CardChrome.Fill(head, new Color(0f, 0f, 0f, 0.12f));
            DrawColumnToggle(head, listRect, "左栏", ref skin.collapsedLeft, ref skin.pinnedLeft);
            if (flowHead.width > 0f)
            {
                DrawColumnToggle(head, flowHead, DelegationUIUtility.FlowHeader, ref skin.collapsedFlow,
                    ref skin.pinnedFlow);
            }
            DrawColumnToggle(head, mainRect, "主信息", ref skin.collapsedMain, ref skin.pinnedMain);
            DrawColumnToggle(head, railRect, "概览", ref skin.collapsedRail, ref skin.pinnedRail);
        }

        /// <summary>
        /// 一栏的列头控件：折叠开关 + 它**左边**的图钉（S34，用户原话：
        /// 「上面折叠展开的+-号按钮左边添加一个图钉PIN按钮，点击后可以固定展开」）。
        ///
        /// 图钉语义 = 固定展开：钉住后这一栏不吃悬浮焦点的自动收起（见 <see cref="UpdateColumnFocus" />
        /// 与 `Draw` 里的 `!pinLeft`），生效的折叠态变成"永远展开"。
        /// 为了不出现"钉住了却被收起来"的矛盾，点折叠开关 ＝ 收起**并顺带拔钉**。
        ///
        /// 折叠成 26px 竖条时那一格放不下第二颗按钮 ⇒ 只画折叠开关；而"钉住的栏按定义就是展开的"
        /// （钉住时会把 `collapsed` 清掉、且折叠开关会拔钉）⇒ 不存在"钉住了却看不到图钉、拔不掉"的死角。
        ///
        /// ⚠️ RIM-6（2026-10-05，用户报「上面添加了PIN按钮，但是折叠/展开按钮没了」）：
        ///   旧版这里调 `UIKit.Button(rect, "－"/"＋", ButtonStyle.Ghost, …)` —— `Ghost` 档**不铺底**，
        ///   于是那颗按钮的全部视觉都押在**全角 `－`(U+FF0D) / `＋`(U+FF0B)** 这两个字形上；
        ///   实测两栏都只剩图钉、开关那一格是纯背景（IL 里按钮确实在画，见 RIM-6 议题的反汇编表）。
        ///   ⇒ 改法（与图钉同源）：**底色 + 悬停 + 自绘几何字形 + `Widgets.ButtonInvisible`**，
        ///   彻底不依赖字体覆盖；同时把这一组整体按 `BtnGap` 内收，不再让右边缘压在列边界（`col.xMax`）上。
        /// </summary>
        private static void DrawColumnToggle(Rect head, Rect col, string label, ref bool collapsed, ref bool pinned)
        {
            bool strip = collapsed && !pinned;   // 本帧实际显示为"收起"吗（图钉优先于 collapsed）
            bool roomForPin = col.width > StripW + 6f;

            // RIM-6：整组内收 —— 旧版 `col.xMax - 24` + 宽 22 ⇒ 右边缘落在 `col.xMax - 2`，
            // 贴边太紧；现在多留 `BtnGap`，且按钮改成 18×18 的方块，整组更紧凑也更好点。
            float btnX = col.xMax - BtnGap - ColBtnW;
            if (roomForPin)
            {
                Rect pinRect = new Rect(btnX - PinBtnW - BtnGap, head.y + (ColHeadH - ColBtnW) * 0.5f,
                    PinBtnW, ColBtnW);
                if (DrawPinToggle(pinRect, label, pinned))
                {
                    pinned = !pinned;
                    if (pinned)
                    {
                        collapsed = false;   // 钉住 = 固定展开，顺手把它展开
                    }
                }
                btnX -= PinBtnW + BtnGap;
            }

            if (DrawCollapseToggle(new Rect(btnX, head.y + (ColHeadH - ColBtnW) * 0.5f, ColBtnW, ColBtnW),
                    label, strip, pinned))
            {
                if (strip)
                {
                    collapsed = false;
                }
                else
                {
                    collapsed = true;
                    pinned = false;   // 收起说的就是收起：连图钉一起撤掉
                }
            }
            // 折叠成竖条时那块只有 26px，写不下名字 ⇒ 只留按钮（tooltip 里已经说了是哪个区域）
            if (roomForPin)
            {
                // 名字要给"图钉 + 折叠开关"两颗按钮让位
                float reserved = PinBtnW + BtnGap + ColBtnW + BtnGap + 16f;
                RadiusFont.LabelAt(new Rect(col.x + 6f, head.y, Mathf.Max(40f, col.width - reserved), ColHeadH),
                    label, RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
            }
        }

        /// <summary>
        /// 折叠开关（RIM-6 起的画法）：底 + 悬停 + **自绘几何字形** + 收点击。
        ///
        /// 字形为什么自己画、不用 `RadiusIcon`：框架的图标集里**没有加减号**
        /// （`Textures/RadiusUI/Action/` 只有 `DevPlus`/`ChevronDown`/`Strip` 等，已逐张看过），
        /// 而 `＋/－` 这两个字形正是 RIM-6 的嫌疑点 —— 自绘两根 2px 的线最直接也最可控。
        /// </summary>
        private static bool DrawCollapseToggle(Rect r, string label, bool strip, bool pinned)
        {
            bool hover = Mouse.IsOver(r);
            CardChrome.Rounded(r, Palette.Surface2, 6f);
            if (hover)
            {
                CardChrome.Hover(r, 6f);
            }
            RadiusFont.LabelAt(r, strip ? "＋" : "－", RadiusFont.Scale.Section,
                hover ? Palette.Ink : Palette.TextDim, TextAnchor.MiddleCenter, false, false);
            TooltipHandler.TipRegion(r, strip
                ? "展开" + label
                : (pinned ? "收起" + label + "（同时取消固定）" : "收起" + label));
            return Widgets.ButtonInvisible(r, true);
        }

        /// <summary>
        /// 图钉按钮。贴图用 Radius UI Framework 自带的 `RadiusUI/Action/Pin`
        /// （Quest Menu 的"钉住任务"用的是同一张，[反编译] `RadiusUIQuestMenu.QuestDetail.DrawContent`）；
        /// 钉住时用强调色底 + 亮色图标，未钉住时与其它 IconButton 同款暗底。
        /// </summary>
        private static bool DrawPinToggle(Rect r, string label, bool pinned)
        {
            RadiusIcon icon = IconSet.Get("Action/Pin");
            bool hover = Mouse.IsOver(r);
            CardChrome.Rounded(r, pinned ? RadiusTheme.Accent : Palette.Surface2, 6f);
            if (hover)
            {
                CardChrome.Hover(r, 6f);
            }
            if (icon.Exists)
            {
                icon.Draw(GenUI.ContractedBy(r, 3f),
                    pinned ? Palette.InkOnAccent : (hover ? Palette.Ink : Palette.TextDim));
            }
            TooltipHandler.TipRegion(r, pinned
                ? "取消固定" + label + "（恢复随鼠标自动收起）"
                : "固定展开" + label + "（不再随鼠标自动收起）");
            return Widgets.ButtonInvisible(r, true);
        }

        // ================================================================ ③-b 流程栏（S22 四栏）

        /// <summary>
        /// 流程栏 —— S22 四栏布局里的第四栏（左栏 / **流程** / 主列 / 右栏）。
        ///
        /// 为什么单开一栏：流程是"现在在干什么、怎么走到这的"，是玩家看得最勤的一块；
        /// 放在主列里它排在「现场物资 / 参与者」之后，每次都要滚过两屏才看得到。
        /// 内容与主列里那块**同一份实现**（<see cref="FlowPass" />，两处互斥，见 `flowShown`）。
        ///
        /// 两趟跑法（量高 → 画）与主列 / `RailRun` 完全同构：内容总高由 `Cursor` 空跑一趟算出，
        /// 不依赖 Layout 趟测高（§16.8 修正后的口径）。
        /// </summary>
        private static void DrawFlowColumn(Rect r, Delegation d, Site site)
        {
            if (d == null)
            {
                return;
            }

            CardChrome.Fill(r, new Color(0f, 0f, 0f, 0.20f));
            Rect inner = r.ContractedBy(10f);
            float viewW = inner.width - 14f;
            Cursor measure = new Cursor(false, new Rect(0f, 0f, viewW, 0f), 0f);
            FlowPass(measure, d, site);

            // S28：流程块自己带一张卡（用户：「底框直接要有空隙」）⇒ 这一栏不再铺深色底板，
            // 否则就是"卡里套卡"。只有**确实没有任何行**时才退回旧底板（那说明这条没有流程可讲）。
            if (measure.y <= 0.5f)
            {
                CardChrome.Fill(r, new Color(0f, 0f, 0f, 0.20f));
                return;
            }

            Rect view = new Rect(0f, 0f, viewW, Mathf.Max(measure.y, inner.height));

            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(inner, ref flowScroll, view);
                opened = true;
                FlowPass(new Cursor(true, view, 0f), d, site);
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        /// <summary>
        /// 「流程」块的**唯一一份内容实现**（阶段行 → 各 kind 的画法）。流程栏与主列（放不下时）都调它。
        ///
        /// 条直接复用 `Cursor.Progress` —— 它内部就是 `UIKit.Flat.Bar`，与「总进度」那一行同一个件
        /// （用户要求"用同页面有的进度条"）；已完成最多逐条列 6 段，超出由 `Rows(maxDoneRows)` 压一行。
        ///
        /// S28（用户三条）：①「字体字号参考右边的概览，底框直接要有空隙」⇒ 整块套一张**与右栏
        /// 「概览/位置/导航」完全相同的卡**（`CardChrome.Card`），卡头 8px、内容四边缩进 8px，
        /// 正文行 15px（`Scale.Body`，概览正文同档）、副行（旁白/条尾）仍 13px（`Scale.Meta`，与概览的副行同档）；
        /// ② 行内火苗挪到技能名之后；③ 阶段行按「作战任务 / 收集任务」两组排（组标题由 `Rows` 插，见那里）。
        ///
        /// ⚠️ 卡必须"高度已知"才能铺底（S22 的教训：反了内容会被盖住）⇒ 高度在**量高趟**记进
        /// `flowCardH`、绘制趟开头铺底（与三段的 `SectionChrome` 同一套做法，两趟同帧先后关系不变）。
        /// </summary>
        private static void FlowPass(Cursor c, Delegation d, Site site)
        {
            List<DelegationStageRow> stageRows = DelegationUIUtility.StageRows(d, site, 6,
                DelegationUIUtility.FlowHeader);
            if (stageRows.NullOrEmpty())
            {
                return;
            }

            // 卡头文字取 `Header` 行的 text（"流程"）—— 文字来源仍然只有 `Rows` 那一份，
            // 皮肤只是把它从"一行内文"提升成"卡标题"来画（原版那边照旧画成一行）。
            string cardTitle = null;
            for (int i = 0; i < stageRows.Count; i++)
            {
                if (stageRows[i].kind == DelegationStageRowKind.Header)
                {
                    cardTitle = stageRows[i].text;
                    break;
                }
            }

            const float Pad = 8f;
            float headH = cardTitle.NullOrEmpty() ? 0f : Mathf.Max(UIKit.Flat.SectionHeaderH, 20f);
            float top = c.y;
            if (c.draw)
            {
                CardChrome.Card(new Rect(c.view.x, top, c.w, flowCardH), false);
                if (!cardTitle.NullOrEmpty())
                {
                    RadiusFont.LabelAt(new Rect(c.view.x + Pad, top + Pad,
                            Mathf.Max(60f, c.w - Pad * 2f), headH),
                        cardTitle, RadiusFont.Scale.Section, Palette.Flat.InkMid,
                        TextAnchor.MiddleLeft, true, false);
                }
            }

            // 卡片内容：四周缩进 8px 的游标（y 由外层游标接管，画完再写回去）
            Cursor inner = c.Padded(Pad);
            float contentTop = c.y + Pad + headH;
            inner.y = contentTop;

            // S22：小队位置（「外围 / 藏匿点内部 / 矿点…」）画在卡头**下面第一行**
            // —— 用户要求「流程上方添加一个虚拟的小队位置」。
            for (int i = 0; i < stageRows.Count; i++)
            {
                if (stageRows[i].kind == DelegationStageRowKind.Position)
                {
                    inner.Line(stageRows[i].text, RadiusFont.Scale.Body, new Color(0.80f, 0.90f, 1f));
                }
            }

            for (int i = 0; i < stageRows.Count; i++)
            {
                DelegationStageRow row = stageRows[i];
                switch (row.kind)
                {
                    case DelegationStageRowKind.Header:
                    case DelegationStageRowKind.Position:
                        break;   // 卡头已画；位置行已画，别画两遍

                    case DelegationStageRowKind.GroupTitle:
                        inner.GroupTitle(row.text);
                        break;

                    case DelegationStageRowKind.Done:
                    case DelegationStageRowKind.DoneSummary:
                        inner.Wrapped("· " + row.text, RadiusFont.Scale.Body, Palette.Flat.InkLow);
                        break;
                    case DelegationStageRowKind.ActiveTitle:
                        // S28：火苗插在**技能名之后**（用户：「双火/火要显示在技能名后，例如 智识 双火」）。
                        // 插入位来自共用件的 `row.iconAt`，前缀 `● ` 占两个字符 ⇒ 下标 +2。
                        inner.InlineIconLine("● " + row.text, row.iconAt + 2,
                            DelegationUIUtility.PassionIconFor(row.stage),
                            RadiusFont.Scale.Body, Palette.Flat.Ink);
                        break;
                    case DelegationStageRowKind.ActiveAmbient:
                        inner.Wrapped("　　" + DelegationUIUtility.Typewriter(row.text),
                            RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                        break;
                    case DelegationStageRowKind.RestTitle:
                        // S16：休息置顶那一行
                        inner.Wrapped("· " + row.text, RadiusFont.Scale.Body, Palette.Flat.Ink);
                        break;
                    case DelegationStageRowKind.RestAmbient:
                        inner.Wrapped("　　" + DelegationUIUtility.Typewriter(row.text),
                            RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                        break;
                    case DelegationStageRowKind.Event:
                        // S15：事件留痕一行（+ 打字机）—— 事件不发信后，这是进行中唯一的事件出口
                        inner.Wrapped("! " + DelegationUIUtility.Typewriter(row.text),
                            RadiusFont.Scale.Body, Palette.Warn);
                        break;
                    case DelegationStageRowKind.ActiveBar:
                        {
                            // S15 第三期：停摆时整行偏红（条 + ETA）
                            Color barOld = GUI.color;
                            if (row.stage != null && row.stage.stalled)
                            {
                                GUI.color = new Color(1f, 0.6f, 0.55f);
                            }
                            inner.Progress(row.text, row.frac, null);
                            GUI.color = barOld;
                        }
                        break;
                }
            }

            float cardH = (inner.y - contentTop) + headH + Pad * 2f;
            c.y = top + cardH + GapH;
            if (!c.draw)
            {
                flowCardH = cardH;
            }
        }

        /// <summary>S28：流程卡在**量高趟**记下的高度（绘制趟拿它铺底框）。</summary>
        private static float flowCardH;

        /// <summary>主列的唯一一份布局（`cursor.draw == false` 时只量高）。</summary>
        /// <summary>
        /// S27：三段的**底框 + 大标题**（用户：「'作战任务'和'收集任务'，'远行队信息'增大字号，
        /// 他们是否可以像右边的概览一样，添加底框」）。
        ///
        /// 用与右栏「概览/位置/导航」**完全相同的卡片**（`CardChrome.Card` + `RadiusFont.Scale.Section`），
        /// 所以观感天然一致。⚠️ 底框必须先画、内容后画（S22 的教训：反了内容会被盖住），
        /// 但段高只能在画完内容后才知道 ⇒ 高度在**量高趟**记到静态字段里、绘制趟开头铺底
        /// （第一帧画不出来是正常的，下一帧就有了）。
        /// </summary>
        private static void SectionChrome(Cursor c, float top, float height, string title)
        {
            if (!c.draw || height <= 1f)
            {
                return;
            }
            Rect card = new Rect(c.view.x, top, c.w, height);
            CardChrome.Card(card, false);
            RadiusFont.LabelAt(new Rect(card.x + 8f, card.y + 4f, Mathf.Max(60f, card.width - 40f),
                    Mathf.Max(20f, UIKit.Flat.SectionHeaderH)),
                title, RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleLeft, true, false);
        }

        /// <summary>三段在**量高趟**记下的高度（绘制趟拿它铺底框）。</summary>
        private static float secCombatH;

        private static float secCollectH;

        private static float secCaravanH;

        /// <summary>
        /// S25：**可折叠的段头**（皮肤画法）。返回 true = 内容应当绘制。
        ///
        /// 折叠状态与「原版」**共用一份**（`DelegationUIUtility.SectionCollapsed/ToggleSection`
        /// ⇒ 切皮肤不会忽开忽合），只有画法是 Radius 自己的（双端准则：文案与状态一份、画法各自）。
        ///
        /// ⚠️ 开关画在**段头自己身上**，而段头永远在；收起时还要单独画一张"只剩卡头"的底框 ——
        /// 否则标题会跟着内容一起消失，又变成"收起来就再也找不到"的那个老坑（S22 用户报过两次）。
        /// </summary>
        private static bool SectionCollapsible(Cursor c, string label, DelegationUIUtility.SectionId id)
        {
            float top = c.y;
            float h = Mathf.Max(UIKit.Flat.SectionHeaderH, 20f);
            bool collapsed = DelegationUIUtility.SectionCollapsed(id);
            if (c.draw)
            {
                // 标题**永远**要画（S27：字号提到与「概览」卡片同档）。
                // 展开态的底框由 SectionChrome 在量高趟之后铺（第一帧没有底框是正常的）。
                RadiusFont.LabelAt(new Rect(c.view.x + 8f, top + 2f, Mathf.Max(60f, c.w - 40f), h),
                    label, RadiusFont.Scale.Section, Palette.Flat.Ink, TextAnchor.MiddleLeft, true, false);
                if (collapsed)
                {
                    Rect card = new Rect(c.view.x, top, c.w, h + 8f);
                    CardChrome.Card(card, false);
                    RadiusFont.LabelAt(new Rect(card.x + 8f, card.y + 4f, Mathf.Max(60f, card.width - 40f), h),
                        label, RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleLeft, true, false);
                }
                if (UIKit.Button(new Rect(c.view.x + c.w - 22f, top + 4f, 22f, Mathf.Max(18f, h - 4f)),
                        collapsed ? "＋" : "－", ButtonStyle.Ghost, true,
                        collapsed ? "展开" + label : "收起" + label))
                {
                    DelegationUIUtility.ToggleSection(id);
                    collapsed = !collapsed;
                }
            }
            c.y += h + 4f;
            return !collapsed;
        }

        private static void MainPass(Cursor c, Delegation d, Site site, WorldObjectComp_Delegations comp)
        {
            DelegationWorker worker = d.Worker;
            string unit = worker?.UnitName ?? "格";

            // 标题 + 地点信息卡
            float infoSize = Widgets.InfoCardButtonSize;
            if (c.draw)
            {
                RadiusFont.LabelAt(new Rect(c.view.x, c.y, c.w - infoSize - 8f,
                        RadiusFont.LineHAt(RadiusFont.Scale.Title, true)),
                    string.Format("RimDelegationTabTitle".Translate(), d.def?.label ?? "?", site.Label),
                    RadiusFont.Scale.Title, Palette.Flat.Ink, TextAnchor.MiddleLeft, true, false);
                Widgets.InfoCardButton(c.view.x + c.w - infoSize, c.y + 2f, site);
            }
            c.y += Mathf.Max(24f, RadiusFont.LineHAt(RadiusFont.Scale.Title, true)) + 4f;

            // 正文：def.description（现在只在对话框里出现，正好补到主控台）
            c.Wrapped(d.def?.description, RadiusFont.Scale.Body, Palette.Flat.InkMid);
            c.Gap();

            // ---- ① 作战任务（S24 恒常；S25 加：段头可折叠 / 情报门控 / 编队图标 / 参战人员 / 待命两按钮）
            float secCombatTop = c.y;
            if (c.draw && !DelegationUIUtility.SectionCollapsed(DelegationUIUtility.SectionId.Combat))
            {
                SectionChrome(c, secCombatTop, secCombatH, DelegationUIUtility.SectionCombat);
            }
            bool showCombat = SectionCollapsible(c, DelegationUIUtility.SectionCombat,
                DelegationUIUtility.SectionId.Combat);
            if (showCombat)
            {
                DelegationThreatSummary ts = d.threatSummary;                ts.flowLine = DelegationUIUtility.CombatPhaseLine(d);
                ts.revealed = DelegationUIUtility.ThreatRevealed(d);
                // 侦察一完成 ⇒ **自动**算一次成算（用户 S25：「完成侦察任务后，先自动进行战斗评估」）；
                // 未揭露时传 false —— 没必要白跑 200 次蒙特卡洛。
                ts.Ensure(d.caravan, site, d.participants, d.approach, ts.revealed && ts.hasThreat, d.noCombatPawns);
                List<string> combatLines = ts.Lines();
                for (int i = 0; i < combatLines.Count; i++)
                {
                    c.Wrapped(combatLines[i], RadiusFont.Scale.Meta, Palette.Flat.InkMid);
                }
                // 编队（S25：带图标；S26：**未揭露就一行都不画**）
                if (ts.revealed)
                {
                    for (int i = 0; i < ts.roster.Count; i++)
                    {
                        DelegationThreatSummary.RosterEntry entry = ts.roster[i];
                        float rowH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
                        if (c.draw)
                        {
                            float tx = c.view.x + 4f;
                            if (entry.icon != null)
                            {
                                Widgets.ThingIcon(new Rect(tx, c.y + 1f, 18f, 18f), entry.icon,
                                    null, null, 1f, null, null, 1f);
                                tx += 22f;
                            }
                            RadiusFont.LabelAt(new Rect(tx, c.y, Mathf.Max(40f, c.view.x + c.w - tx), rowH),
                                entry.Short, RadiusFont.Scale.Meta, Palette.Flat.InkMid,
                                TextAnchor.MiddleLeft, false, false);
                        }
                        c.y += rowH;
                    }
                }
                // 参战人员（用户 S25：「作战任务里面单独添加参与人员（或动物）」；
                // S27：每行前面一个**参战勾选框** —— 用户口径「作战任务里面添加一个勾选框，
                // 决定人物或动物是参加还是不参加作战」，与"参不参加委派"是两条轴）
                if (!ts.ourPawns.NullOrEmpty())
                {
                    c.Line(string.Format("参战人员（{0} 人参战 / 共 {1} 人）", ts.ourUnits, ts.ourPawns.Count),
                        RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                    for (int i = 0; i < ts.ourPawns.Count; i++)
                    {
                        Pawn p = ts.ourPawns[i];
                        float rowH = DelegationUIUtility.RowHeight;
                        bool fight = d.FightsInCombat(p);
                        if (c.draw)
                        {
                            Rect row = new Rect(c.view.x, c.y, c.w, rowH);
                            Rect toggle = new Rect(row.x, row.y + (rowH - 18f) * 0.5f, 18f, 18f);
                            Rect line = new Rect(row.x + 24f, row.y, row.width - 24f, rowH);
                            Color oldRow = GUI.color;
                            if (!fight) GUI.color = new Color(1f, 1f, 1f, 0.62f);
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
                        c.y += rowH;
                    }
                }
                // 待命：两颗决策按钮（用户 S25：「作战任务UI里面添加 进行交战 和 撤退选项」）
                if (d.AwaitingOrder)
                {
                    string signal = DelegationFlow.For(d.def).ActiveSignal(d.flow);
                    if (c.draw)
                    {
                        float bw = (c.w - 8f) * 0.5f;
                        // S27：一个参战的人都没有时禁用（点下去只会是"没人打 ⇒ 判负"）
                        bool canFight = ts.ourUnits > 0;
                        if (UIKit.Button(new Rect(c.view.x, c.y, bw, 24f), "进行交战",
                                ButtonStyle.Solid, canFight,
                                canFight ? "让队伍打这一场（进入交战段，战斗就在这一刻结算）"
                                         : "没有人参加作战 —— 先在下面的「参战人员」里勾上至少一个"))
                        {
                            if (canFight) comp.GivePhaseSignal(d, signal);
                        }
                        if (UIKit.Button(new Rect(c.view.x + bw + 8f, c.y, bw, 24f), "撤退",
                                ButtonStyle.Solid, true, "放弃这一趟：队伍带人撤回去（事件点保留，随时可以再来）"))
                        {
                            comp.OrderRetreat(d);
                        }
                    }
                    c.y += 28f;
                }
                if (ts.revealed && ts.canAssess)
                {
                    const float CBtnW = 104f;
                    if (c.draw)
                    {
                        if (UIKit.Button(new Rect(c.view.x, c.y, CBtnW, 24f), "重新推算",
                                ButtonStyle.Solid, true, "重新采样一次成算（" + RimDelegation.Combat.CombatTuning.ForecastIterations + " 次蒙特卡洛）"))
                        {
                            ts.Reroll(d.caravan, site, d.participants, d.approach, d.noCombatPawns);
                        }
                        if (UIKit.Button(new Rect(c.view.x + CBtnW + 8f, c.y, CBtnW, 24f), "查看评估",
                                ButtonStyle.Solid, true, "打开完整的威胁评估面板（编队 / 预告 / 单场推演与日志）"))
                        {
                            ThreatAssessmentEntry.Open(d.caravan, site,
                                d.Worker?.ApproachFirstStrikePenalty(d.approach) ?? 0f);
                        }
                    }
                    c.y += 28f;
                }
                c.Gap();
            }

            if (!c.draw)
            {
                secCombatH = c.y - secCombatTop;   // S27：段高在量高趟记下来，绘制趟拿它铺底框
            }
            // S29：**卡与卡之间**的空隙（用户报"三段之间没有空隙"）。刻意加在"段高之外"——
            // 否则底框会把这个间距一起吃掉，看上去还是贴着的。
            c.Gap();
            float secCollectTop = c.y;
            if (c.draw && !DelegationUIUtility.SectionCollapsed(DelegationUIUtility.SectionId.Collect))
            {
                SectionChrome(c, secCollectTop, secCollectH, DelegationUIUtility.SectionCollect);
            }

            // ---- ② 收集任务（作业模式 → 进度 → 现场物资）
            bool showCollect = SectionCollapsible(c, DelegationUIUtility.SectionCollect,
                DelegationUIUtility.SectionId.Collect);
            if (showCollect)
            {
            c.Section("RimDelegationTabTotalProgress".Translate());
            string tail = string.Format("{0:0.#}/{1} {2}", d.cellsMined, d.totalCells, unit);
            float days = d.EstimatedDaysLeft(site.Tile);
            if (days >= 0f)
            {
                tail += string.Format(" · 剩余 {0:0.##} 天", days);
            }
            c.Progress(tail, Mathf.Clamp01(d.Progress),
                DelegationUIUtility.ProgressDetailTip(d, worker, unit, worker?.OutputUnitName ?? "单位"));
            // S10：绝对时刻**另起一行**。以前并进尾注，主列只有 ~470px ⇒ 那一行必被裁
            //（用户截图标红的两处之一）。
            string finish = DelegationUIUtility.FinishDateLine(d, site);
            if (!finish.NullOrEmpty())
            {
                c.Line("　" + finish, RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            }
            c.Line(string.Format("RimDelegationTabHours".Translate(), d.ticksWorked / 2500f, d.ticksResting / 2500f),
                RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            c.Gap();

            // 现场物资（S9）：每行「已获取 X/Y 单位 · kg · 市价」，表头带市价合计与排序按钮。
            // 排序状态是本皮肤自己的阅读偏好（与原版主控台各自独立，互不影响）。
            List<DelegationPreviewItem> items = DelegationUIUtility.ProgressItemRows(d, site, itemSort);
            bool itemsHidden = DelegationUIUtility.ItemsHidden(d);
            if (itemsHidden)
            {
                // S22（用户要求）：清单还没透露时**保留这一块**，块头写「现场物资（? 类）」，下面留空。
                // 整块不画的话，玩家会以为"这条委派根本没有物资这回事"。
                c.Section(DelegationUIUtility.ProgressItemsHeader(d, null, unit, false));
                c.Gap();
            }
            else if (!items.NullOrEmpty())
            {
                const float ItemSortW = 92f;
                float headerY = c.y;
                // 表头只留"这一块是什么 + 值多少钱"：主列只有约 470px，
                // "该地点存量"（跨委派口径）放不下就进 tooltip —— 重排 ≠ 删信息。
                Rect right = c.SectionRow(DelegationUIUtility.ProgressItemsHeader(d, items, unit, false), ItemSortW);
                if (c.draw)
                {
                    TooltipHandler.TipRegion(
                        new Rect(c.view.x, headerY, Mathf.Max(60f, c.w - ItemSortW - 10f), UIKit.Flat.SectionHeaderH),
                        DelegationUIUtility.ProgressItemsHeader(d, items, unit, true));
                    // S10：短标签 + Solid（用户反馈"能点的地方看不出来"，Ghost 长得像纯文字）
                    if (UIKit.Button(right, "排序：" + DelegationUIUtility.ItemSortShort(itemSort),
                            ButtonStyle.Solid, true, "现场物资排序：市价（高→低）→ 数量（多→少）→ 名称"))
                    {
                        itemSort = DelegationUIUtility.NextItemSort(itemSort);
                    }
                }
                for (int i = 0; i < items.Count; i++)
                {
                    if (c.draw)
                    {
                        // S13：行尾加 i（查看信息卡）
                        DelegationUIUtility.DrawItemRow(new Rect(c.view.x, c.y, c.w, ItemRowH), items[i], 20f, 18f, true);
                    }
                    c.y += ItemRowH;
                }
                c.Gap();
            }

            // 战场清点（S31）：尸骸 / 本趟缴获（装备 + 就地屠宰的肉皮）/ 收押的俘虏。
            // 用户拍板 3B：尸骸也要在这张表里露一行 —— 尸体就地处理不带回家，但"宰了几具"必须看得见。
            {
                List<DelegationPreviewItem> cleanup = DelegationUIUtility.CleanupRows(d);
                string cleanupHead = DelegationUIUtility.CleanupHeader(d, cleanup);
                if (!cleanupHead.NullOrEmpty())
                {
                    c.Section(cleanupHead);
                    for (int i = 0; i < cleanup.Count; i++)
                    {
                        if (c.draw)
                        {
                            DelegationUIUtility.DrawItemRow(new Rect(c.view.x, c.y, c.w, ItemRowH),
                                cleanup[i], 20f, 18f, true);
                        }
                        c.y += ItemRowH;
                    }
                    c.Gap();
                }
            }

            // 参与者（S9/S10/S13）：排序 / 添加人员 / 查看全部 + **每行前面的 V/X 开关**
            // S27：用户要求把「参与人员」放进**收集任务**（「因为显示的是采矿作业相关的」）——
            // 这一列就是"谁去干这趟活"、与作业速率同源；作战那一边另有自己的参战勾选框。
            // （用户 S13 要求："参与者的最前面添加 V (Yes) 或 X (No) 来控制殖民者是否参与"）。
            // 所以这一列是"当前参与者 + 队里还能参加的人"，前者打勾、后者打叉，点一下即切换。
            {
                const float SortW = 86f;
                const float AddW = 92f;
                const float ViewW = 100f;
                float rightW = SortW + AddW + ViewW + 12f;
                // ⚠️ 不叫 right：上面"现场物资"那段已经有一个 right 在本方法作用域里，
                //    同名局部变量在嵌套块里是 CS0136（C# 不许遮蔽外层局部）。
                Rect pRight = c.SectionRow(string.Format("RimDelegationTabParticipants".Translate(), d.participants.Count), rightW);
                if (c.draw)
                {
                    if (UIKit.Button(new Rect(pRight.x, pRight.y, SortW, pRight.height),
                            "排序：" + DelegationUIUtility.PawnSortShort(pawnSort), ButtonStyle.Solid, true,
                            "参与者排序：技能（高→低）→ 名字 → 心情（差→好）"))
                    {
                        pawnSort = DelegationUIUtility.NextSort(pawnSort);
                    }
                    int cap = d.def != null && d.def.maxPawns > 0 ? d.def.maxPawns : int.MaxValue;
                    bool canAdd = d.participants.Count < cap;
                    if (UIKit.Button(new Rect(pRight.x + SortW + 6f, pRight.y, AddW, pRight.height), "添加人员",
                            ButtonStyle.Solid, canAdd,
                            canAdd ? "从远行队里再叫一个人加入这次委派" : string.Format("这个委派最多 {0} 人", cap)))
                    {
                        selected.OpenAddParticipantMenu(d);
                    }
                    if (UIKit.Button(new Rect(pRight.x + SortW + AddW + 12f, pRight.y, ViewW, pRight.height),
                            string.Format("RimDelegationTabViewAll".Translate(), d.participants.Count),
                            ButtonStyle.Solid, true, "列出全部参与者（点名字开信息卡）"))
                    {
                        DelegationUIUtility.OpenParticipantsMenu(d, worker, d.def, pawnSort);
                    }
                }

                DelegationUIUtility.BuildParticipantRoster(d, pawnSort, displayPawns, otherPawns);
                int need = Mathf.Max(1, d.def?.minPawns ?? 1);
                bool canRemove = d.participants.Count > need;

                float rowH = DelegationUIUtility.RowHeight;
                int shown = 0;
                shown += DrawRosterRows(c, d, worker, displayPawns, true, canRemove, rowH);
                shown += DrawRosterRows(c, d, worker, otherPawns, false, true, rowH);
                if (shown == 0)
                {
                    c.Line("—", RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                }
                c.Gap();
            }

            }   // showCollect

            if (!c.draw)
            {
                secCollectH = c.y - secCollectTop;
            }
            c.Gap();   // S29：段间空隙（同上）
            float secCaravanTop = c.y;
            if (c.draw && !DelegationUIUtility.SectionCollapsed(DelegationUIUtility.SectionId.Caravan))
            {
                SectionChrome(c, secCaravanTop, secCaravanH, DelegationUIUtility.SectionCaravan);
            }

            // ---- ③ 远行队信息（S24；S27 起参与者归「收集任务」）
            bool showCaravan = SectionCollapsible(c, DelegationUIUtility.SectionCaravan,
                DelegationUIUtility.SectionId.Caravan);
            if (showCaravan)
            {

            string overtime = DelegationUIUtility.OvertimeLine(d);
            string workStart = DelegationUIUtility.WorkStartLine(d, site);   // S14：休息时"距开工还有多久"
            string fatigue = DelegationUIUtility.FatigueRiskLine(d);
            string meal = DelegationUIUtility.MealLine(d.caravan);
            if (!overtime.NullOrEmpty() || !fatigue.NullOrEmpty() || !meal.NullOrEmpty()
                || !workStart.NullOrEmpty())
            {
                c.Section("远行队");
                c.Wrapped(overtime, RadiusFont.Scale.Meta, Palette.Warn);
                c.Wrapped(workStart, RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                c.Wrapped(fatigue, RadiusFont.Scale.Meta, Palette.Flat.InkLow);
                c.Wrapped(meal, RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            }

            // 远行队补给品（S13 用户要求："远行队的下方，添加一个远行队补给品的列表（类似现场物资），
            // 显示图标，名称，数量，总计营养，重量，市场价值"）。
            // 数据全部来自公开件 CaravanFoodItems（判据与右上角"可维持 N 天"同一把尺子）。
            List<DelegationPreviewItem> foodItems = DelegationUIUtility.CaravanFoodItems(d.caravan);
            if (!foodItems.NullOrEmpty())
            {
                c.Section(DelegationUIUtility.FoodItemsHeader(foodItems));
                for (int i = 0; i < foodItems.Count; i++)
                {
                    if (c.draw)
                    {
                        DelegationUIUtility.DrawItemRow(new Rect(c.view.x, c.y, c.w, ItemRowH), foodItems[i], 20f, 18f, true);
                    }
                    c.y += ItemRowH;
                }
                c.Gap();
            }
            else if (d.caravan != null)
            {
                // 空态也要说出来：没有补给 = 会断粮，这是玩家此刻最该知道的事
                c.Section("远行队补给品");
                c.Line("（远行队里没有可吃的补给品）", RadiusFont.Scale.Meta, Palette.Bad);
                c.Gap();
            }

            }   // showCaravan

            if (!c.draw)
            {
                secCaravanH = c.y - secCaravanTop;
            }

            c.Gap();   // S29：最后一段与「流程」块（也是卡）之间的空隙

            // ---- 流程（**不归三段**：用户 S24 拍板"三段不含流程"）
            //      皮肤侧它有独立的第四栏（S22）；这里画的是"流程栏没画"时的那一份
            //      —— 判据 `flowShown`（窄屏或老几何下流程栏放不下 ⇒ 回落到主列，信息一条不少）。
            //      放在主列**末尾**当独立块：它既不是作战、也不是收集、更不是远行队。
            if (!flowShown)
            {
                FlowPass(c, d, site);
            }

            // 暂停 / 停摆 / 事件 / 计时暂停（与页签同一批条件）
            if (d.paused)
            {
                c.Wrapped("RimDelegationTabPaused".Translate(), RadiusFont.Scale.Meta, Palette.Warn);
            }
            else if (d.IsStalled(GenTicks.TicksAbs))
            {
                c.Wrapped(string.Format("RimDelegationTabStalled".Translate(),
                    (d.stalledUntilTickAbs - GenTicks.TicksAbs) / 2500f), RadiusFont.Scale.Meta, Palette.Warn);
            }
            if (d.eventsFired > 0)
            {
                c.Line(string.Format("RimDelegationTabEvents".Translate(), d.eventsFired),
                    RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            }
            if (comp.timeoutPaused)
            {
                c.Line(string.Format("RimDelegationTabTimeoutPaused".Translate(), comp.pausedTimeoutRemaining / 60000f),
                    RadiusFont.Scale.Meta, Palette.Flat.InkLow);
            }
        }

        // ================================================================ ④ 右栏

        /// <summary>概览卡里的一行（先建成数据、再量高、最后画 —— 顺序不能反）。</summary>
        private struct GlanceEntry
        {
            public string Icon;

            /// <summary>
            /// 副标题行的小图标（S12 用户要求："这些图形是否可以绘制类似的 emoji"）。
            ///
            /// ⚠️ **不能直接写 emoji 字形**：游戏字体里没有 🔧⏳😫 这些码位，`Widgets.Label`
            ///    只会画出豆腐块（本项目已踩过一次，见 lesson）。所以这里走的是
            ///    RadiusUI 框架自带的矢量图标（`IconSet.Get("Policy/Work")` 这类路径，
            ///    纹理清单可查 `...\3786107692\Textures\RadiusUI\**`）。
            ///    名字对不上时 <see cref="DrawGlanceRow" /> 会退化成不画 —— 绝不画豆腐。
            /// </summary>
            public string SubIcon;

            public Color Tint;

            public string Main;

            /// <summary>主标题颜色（S13：补给不足 2 天时整行标红）；alpha == 0 时用默认 Ink。</summary>
            public Color MainColor;

            public string Sub;

            public Color SubColor;

            /// <summary>整行的 tooltip（S9：卡片一行放不下的说明放这里，不删信息）。</summary>
            public string Tip;

            /// <summary>
            /// 这一行是不是**可点**的（S10：用户要求"切换委派模式直接在大纲里调整"）。
            /// 可点行程会铺一层行底 + 悬停高亮 + 右端"切换"提示 —— 用户反馈"能点的地方看不出来"。
            /// </summary>
            public bool Clickable;

            public Action OnClick;
        }

        private static float CardTitleH()
        {
            return Mathf.Max(UIKit.Flat.SectionHeaderH, 20f);
        }

        /// <summary>卡片内容起点（标题下方）。</summary>
        private static float CardContentTop(Rect card)
        {
            return card.y + 8f + CardTitleH();
        }

        private static float GlanceRowH()
        {
            return RadiusFont.LineHAt(RadiusFont.Scale.Body, false)
                   + RadiusFont.LineHAt(RadiusFont.Scale.Meta, false) + 6f;
        }

        private static float GlanceCardHeight(int rows)
        {
            return 8f + CardTitleH() + Mathf.Max(1, rows) * GlanceRowH() + 8f;
        }

        private static float LocationCardHeight()
        {
            // S22：多加两行 —— 游戏内时刻 + 距天黑/天亮（用户要求）
            return 8f + CardTitleH() + MapH + 4f + RadiusFont.LineHAt(RadiusFont.Scale.Meta, false) * 3f + 8f;
        }

        private static float GotoCardHeight()
        {
            return 8f + CardTitleH() + BtnH * 2f + 4f + 8f;
        }

        private const float MapH = 128f;

        /// <summary>每个站点一个 <see cref="WorldSnapshot" />（照 Quest Menu：它是按 tile/range 缓存 Texture2D 的）。</summary>
        private static readonly Dictionary<int, WorldSnapshot> snapshots = new Dictionary<int, WorldSnapshot>();

        private static List<GlanceEntry> BuildGlanceRows(Delegation d, Site site)
        {
            DelegationWorker worker = d.Worker;
            List<GlanceEntry> rows = new List<GlanceEntry>();
            // RIM-5：在途用**真实**满意度（含吃饭记录与在外天数），不要再走"预计"重载
            float mood = DelegationUtility.DailyMoodOffset(d);
            float days = d.EstimatedDaysLeft(site.Tile);
            bool working = !d.paused && !d.IsStalled(GenTicks.TicksAbs) && d.IsWorkTime(site, GenTicks.TicksAbs);

            rows.Add(new GlanceEntry
            {
                // S12：图标按用户给的"emoji 意图"逐个对上框架自带的矢量图（字形 emoji 画不出来）
                Icon = "Policy/Work",                 // ≈ 🛠️ 作业中（原版用的是 Status/Caravan）
                SubIcon = "Stat/BarGraph",            // ≈ ⚙️ 累计作业 / 休息
                Tint = working ? Palette.Good : Palette.Flat.InkMid,
                Main = DelegationUIUtility.StatusWord(d, site),
                Sub = string.Format("RimDelegationConsoleHours".Translate(), d.ticksWorked / 2500f, d.ticksResting / 2500f),
                SubColor = Palette.Flat.InkLow
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Common/Clock",                // ≈ ⏳ 剩余
                SubIcon = "Common/Cal",               // ≈ 📅 预期于 …
                Tint = Palette.Flat.InkMid,
                Main = days >= 0f ? string.Format("剩余 {0:0.##} 天", days) : "—",
                // S9：绝对时刻（"预期于 5501年 春 3日, 14时"）优先；算不出来才退回那句说明。
                // "含固定流程在内的预计时间"这个口径改为 tooltip —— 卡片一行放不下两句，
                // 而口径不能丢（重排 ≠ 删信息）。
                Sub = DelegationUIUtility.FinishDateShort(d, site) ?? "含固定流程在内的预计时间",
                SubColor = Palette.Flat.InkLow,
                Tip = "含固定流程在内的预计时间：固定流程（侦察 / 移动 / 破门 / 撤离）与作业速率无关，已计入。"
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Action/Snooze",               // ≈ 💤 作息 / 模式
                // ≈ 📉 心情下降（0 心情时用中立的心情图标）
                SubIcon = mood < 0f ? "Stat/TrendDown" : "Stat/Mood",
                Tint = Palette.Flat.InkMid,
                Main = d.ModeLine(),
                // RIM-5：这一行的 Sub 换成满意度（唯一一份措辞：D.SatisfactionLine）
                Sub = d.SatisfactionLine(),
                SubColor = mood < 0f ? Palette.Warn : Palette.Flat.InkLow,
                // S10：模式行**本身就是切换入口**（用户要求"切换委派模式直接在大纲里调整"）。
                // 与原版页签的模式行同一套心智：能点的地方给提示，不给两个入口。
                Clickable = true,
                OnClick = () => selected?.OpenModeMenu(d),
                Tip = "点击切换委派模式（作息窗口 / 作业强度）——换班本身不扣心情，满意度按新模式重算"
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Action/Check",                // ≈ ✔️ 结束条件
                // ≈ 🛑 会中止 / 🍴 饿着也继续
                SubIcon = d.abortOnOutOfFood ? "Alert/Warning" : "Stat/Food",
                Tint = Palette.Flat.InkMid,
                Main = WorldObjectComp_Delegations.EndConditionLabelOf(d),
                Sub = d.abortOnOutOfFood
                    ? "补给耗尽时中止"
                    : string.Format("饿着也继续{0}", worker?.ActivityName ?? "开采"),
                SubColor = Palette.Flat.InkLow,
                // S11：用户要求"结束条件添加切换" —— 与模式行同一套：行即入口，右端"切换"提示。
                Clickable = true,
                OnClick = () => selected?.OpenEndConditionEditor(d),
                Tip = "点击修改结束条件（取尽 / 按天数 / 按产出配额）"
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Status/Focus",                // ≈ 👤 参与者
                SubIcon = "Status/Zzz",               // ≈ 🥱 疲劳（事故伤害倍率在 tooltip 里）
                Tint = Palette.Flat.InkMid,
                Main = string.Format("参与人员 {0} 人", d.participants.Count),
                // S10：这一段原来塞的是整句「疲劳 · 全队平均休息 55% → 工作遭受事故伤害倍率 ×1.64」，
                // 右栏只有 320px ⇒ 尾巴被裁（用户截图蓝框："参与者里面后面太长"。他要求直接移除那半句）。
                // 完整那句仍在主列「远行队」区实行，这里只留"疲劳到底有多累"这个决策变量。
                Sub = string.Format("疲劳：全队平均休息 {0}", DelegationUtility.AverageRest(d).ToStringPercent()),
                SubColor = Palette.Flat.InkLow,
                Tip = DelegationUIUtility.FatigueRiskLine(d)
            });

            // 负重：S9 用用户给的话术 —— `载重 6.8 (+3.3)kg / 119kg`
            //                + `委派结束后预计远行队载重 10.1kg`
            // 公式只有一份（DelegationUIUtility），所以主控台 / 页签 / 委派对话框三处永远一致。
            float nowMass, deltaMass, capMass;
            if (DelegationUIUtility.TryMassForecast(d, site, out nowMass, out deltaMass, out capMass))
            {
                bool warn = nowMass + deltaMass > capMass;
                rows.Add(new GlanceEntry
                {
                    Icon = "Stat/Weight",             // ≈ ⚖️ 载重
                    SubIcon = "Slot/Pack",            // ≈ 🎒 预计远行队载重
                    Tint = warn ? Palette.Bad : Palette.Flat.InkMid,
                    Main = DelegationUIUtility.MassMain(nowMass, deltaMass, capMass),
                    Sub = DelegationUIUtility.MassSub(nowMass, deltaMass, capMass),
                    SubColor = warn ? Palette.Bad : Palette.Flat.InkLow
                });
            }
            if (d.caravan != null)
            {
                string food = DelegationUIUtility.FoodDaysLine(d.caravan);
                if (!food.NullOrEmpty())
                {
                    // S13 用户要求：X < 2 天就标红警告（"右上角的全部补给可维持X天"）。
                    // 判据用原版 DaysWorthOfFood，与上面那行文字同源，不会出现"文字说 1.8 天、颜色却正常"。
                    float foodDays = DelegationUIUtility.FoodDaysLeft(d.caravan);
                    bool foodWarn = foodDays < 2f;
                    rows.Add(new GlanceEntry
                    {
                        Icon = "Stat/Food",           // ≈ 🍴 / 🍞 补给
                        SubIcon = "Occasion/Caravan", // ≈ ⛺ 远行队补给总量
                        Tint = foodWarn ? Palette.Bad : Palette.Flat.InkMid,
                        Main = food,
                        MainColor = foodWarn ? Palette.Bad : default(Color),
                        Sub = foodWarn ? "远行队补给总量（不足 2 天）" : "远行队补给总量",
                        SubColor = foodWarn ? Palette.Bad : Palette.Flat.InkLow
                    });
                }
            }
            return rows;
        }

        private static void DrawGlanceRow(Rect card, ref float y, GlanceEntry row)
        {
            float bodyH = RadiusFont.LineHAt(RadiusFont.Scale.Body, false);
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            float rowH = GlanceRowH();
            float rowTop = y;
            Rect rowRect = new Rect(card.x + 4f, rowTop, card.width - 8f, rowH);
            float ix = card.x + 8f;

            // S10：可点行程先铺底 + 悬停高亮（用户反馈"这些可以点击的是否可以换个显示配色"）。
            // 铺在内容**之前**，否则会把字盖掉（S8-c 踩过的那个坑）。
            bool hover = row.Clickable && Mouse.IsOver(rowRect);
            if (row.Clickable)
            {
                UIKit.Flat.TableRow(rowRect, 0, hover);
            }

            RadiusIcon icon = IconSet.Get(row.Icon);
            if (icon.Exists)
            {
                icon.Draw(new Rect(ix, rowTop + 2f, 18f, 18f), row.Tint);
            }
            else
            {
                // 图标名对不上时退化成一条色条，绝不画空
                CardChrome.Rounded(new Rect(ix + 6f, rowTop + 3f, 4f, rowH - 10f), row.Tint, 2f);
            }
            float tx = ix + 24f;
            // 可点行右端留出"切换"两字的位置
            float tw = Mathf.Max(40f, card.xMax - 8f - tx - (row.Clickable ? 34f : 0f));
            RadiusFont.LabelAt(new Rect(tx, rowTop, tw, bodyH), row.Main,
                RadiusFont.Scale.Body, row.MainColor.a > 0f ? row.MainColor : Palette.Flat.Ink,
                TextAnchor.MiddleLeft, false, false);

            // ---- 副标题行：可选的小图标（S12）+ 正文
            // ⚠️ 图标是**纹理**不是字形：`IconSet.Get` 找不到就 `Exists == false`，
            //    此时把文字左移回原位，绝不画豆腐块、也不留空洞。
            float subX = tx;
            if (!row.SubIcon.NullOrEmpty())
            {
                RadiusIcon subIcon = IconSet.Get(row.SubIcon);
                if (subIcon.Exists)
                {
                    subIcon.Draw(new Rect(tx, rowTop + bodyH + (metaH - 14f) * 0.5f, 14f, 14f), row.SubColor);
                    subX = tx + 18f;
                }
            }
            RadiusFont.LabelAt(new Rect(subX, rowTop + bodyH, Mathf.Max(30f, tx + tw - subX), metaH), row.Sub,
                RadiusFont.Scale.Meta, row.SubColor, TextAnchor.MiddleLeft, false, false);
            if (row.Clickable)
            {
                // "可点"的显式提示：悬停时点亮，平时用强调色（与 Ghost 按钮那种"像纯文字"区分开）
                RadiusFont.LabelAt(new Rect(card.xMax - 40f, rowTop, 32f, rowH), "切换",
                    RadiusFont.Scale.Meta, hover ? RadiusTheme.Accent : Palette.Flat.InkLow,
                    TextAnchor.MiddleRight, false, false);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    row.OnClick?.Invoke();
                }
            }
            if (!row.Tip.NullOrEmpty())
            {
                TooltipHandler.TipRegion(rowRect, row.Tip);
            }
            y += rowH;
        }

        /// <summary>
        /// 世界地图缩略图：RadiusUI 的 `WorldSnapshot`（公开件）—— `Basis` 取朝向，
        /// `Get` 返回**带内部缓存**的 Texture2D，`ProjectWorld` 把自家殖民地投影到图上画灰点。
        /// 第一次为某个站点构建时会打印一次耗时（详细日志开关下），便于确认"切换选中只卡一帧"。
        /// </summary>
        private static Texture2D ThumbnailFor(Site site)
        {
            try
            {
                Vector3 center;
                Vector3 east;
                Vector3 north;
                // ⚠️ 参数是 out（不是 ref）—— 反编译里显示成 ref，编译器按 out 校验
                if (!WorldSnapshot.Basis(site.Tile, out center, out east, out north))
                {
                    return null;
                }
                WorldSnapshot snap;
                if (!snapshots.TryGetValue(site.ID, out snap))
                {
                    snap = new WorldSnapshot();
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    snapshots[site.ID] = snap;
                    Texture2D first = snap.Get(site.Tile, east, north, WorldSnapshot.PlanetRadius * 0.1f,
                        null, default(Vector2), null);
                    if (RimDelegationMod.Settings != null && RimDelegationMod.Settings.verboseLogging)
                    {
                        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                                    / System.Diagnostics.Stopwatch.Frequency;
                        Log.Message(string.Format("[RimDelegation] 皮肤：世界缩略图首次构建：站点 {0}，用时 {1:0.0} ms",
                            site.ID, ms));
                    }
                    return first;
                }
                return snap.Get(site.Tile, east, north, WorldSnapshot.PlanetRadius * 0.1f,
                    null, default(Vector2), null);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[RimDelegation] 皮肤：世界缩略图构建失败（该卡退化为空底）：" + e.Message, 0x5E0F2);
                return null;
            }
        }

        /// <summary>位置卡：缩略图 + 目标点（正中心）+ 自家殖民地投影点 + 地点名与坐标。</summary>
        private static void DrawLocationCard(Rect card, Site site)
        {
            float top = CardContentTop(card);
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            Rect map = new Rect(card.x + 8f, top, Mathf.Max(60f, card.width - 16f), MapH);

            CardChrome.Rounded(map, Palette.Surface2, 8f);
            Texture2D tex = ThumbnailFor(site);
            if (tex != null)
            {
                // S10：用户要求"地图拉满显示" —— 原来用 ScaleToFit，方形缩略图在扁矩形里
                // 左右各留一条黑边。改成 ScaleAndCrop：铺满整块、超出的部分裁掉（不拉伸变形）。
                CardChrome.Image(map, tex, 8f, Palette.Surface, ScaleMode.ScaleAndCrop);
            }
            CardChrome.Outline(map, Palette.Border, 1f, 8f);

            Vector3 center;
            Vector3 east;
            Vector3 north;
            if (WorldSnapshot.Basis(site.Tile, out center, out east, out north))
            {
                float range = WorldSnapshot.PlanetRadius * 0.1f;
                Map home = Find.AnyPlayerHomeMap;
                if (home != null && range > 0.0001f)
                {
                    Vector2 proj;
                    if (WorldSnapshot.ProjectWorld(site.Tile, east, north, home.Tile, out proj))
                    {
                        float px = map.center.x + proj.x / range * (map.width * 0.5f);
                        float py = map.center.y - proj.y / range * (map.width * 0.5f);
                        if (map.Contains(new Vector2(px, py)))
                        {
                            Rect homeDot = new Rect(px - 4f, py - 4f, 8f, 8f);
                            CardChrome.Pill(homeDot, Palette.TextDim);
                            CardChrome.Outline(homeDot, Palette.Ink, 1f, 4f);
                        }
                    }
                }
                Rect dot = new Rect(map.center.x - 5f, map.center.y - 5f, 10f, 10f);
                CardChrome.Pill(dot, RadiusTheme.Accent);
                CardChrome.Outline(dot, Palette.Ink, 1.5f, 5f);
            }

            float lineY = map.yMax + 2f;
            RadiusFont.LabelAt(new Rect(card.x + 8f, lineY, card.width - 16f, metaH),
                string.Format("{0}（{1}）", site.Label, site.Tile),
                RadiusFont.Scale.Meta, Palette.TextMid, TextAnchor.MiddleLeft, false, false);
            lineY += metaH;

            // S22（用户要求）：游戏内的时间（年/季节/日/时）+ 距离夜晚/白天多久。
            string timeText = GameTimeText(site.Tile);
            if (!timeText.NullOrEmpty())
            {
                RadiusFont.LabelAt(new Rect(card.x + 8f, lineY, card.width - 16f, metaH),
                    timeText, RadiusFont.Scale.Meta, Palette.TextMid, TextAnchor.MiddleLeft, false, false);
            }
            lineY += metaH;
            string dayText = DaylightText(site.Tile);
            if (!dayText.NullOrEmpty())
            {
                RadiusFont.LabelAt(new Rect(card.x + 8f, lineY, card.width - 16f, metaH),
                    dayText, RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
            }
        }

        /// <summary>
        /// S22：游戏内时刻 —— 直接用原版 `GenDate.DateFullStringWithHourAt(TicksAbs, LongLatOf(tile))`
        /// （`[反编译]`）：location 传该 tile 的经纬度，原版自己按时区算，中文由原版 Keyed 出
        /// （形如「5501年 春 3日, 14时」）。**不自己拼字符串** —— 否则汉化、季节名、12/24 小时制都会分叉。
        /// </summary>
        private static string GameTimeText(PlanetTile tile)
        {
            try
            {
                return GenDate.DateFullStringWithHourAt(GenTicks.TicksAbs, Find.WorldGrid.LongLatOf(tile));
            }
            catch (Exception)
            {
                // 拿不到就少画一行：绝不为了"补一行"把整个右栏炸掉
                return null;
            }
        }

        /// <summary>
        /// S22：距离天黑 / 天亮还有多久。
        ///
        /// 昼夜边界**用原版自己的口径**：`ThoughtWorker_IsNightForNightOwl` 判"夜里"是
        /// `GenLocalDate.HourInteger(p) &gt;= 23 || &lt;= 5`（`[反编译]`）⇒ 这里同样取 23:00 / 6:00 两个边界。
        /// 为什么不去算真正的日出日落：世界地图上的事件点**没有地图**，`GenCelestial`/`WeatherManager`
        /// 都挂在 `Verse.Map` 上（世界地图没有天气与日照数据，见 Doc/流程显示-示例与旁白池.md §7）。
        /// </summary>
        private static string DaylightText(PlanetTile tile)
        {
            float hour;
            try
            {
                hour = GenLocalDate.HourFloat(tile);   // 0..24，当地时间
            }
            catch (Exception)
            {
                return null;
            }
            float until;
            string what;
            if (hour >= 23f || hour < 6f)
            {
                // 已经是夜里 ⇒ 报"距天亮"（跨零点要绕一圈）
                until = hour >= 23f ? (24f - hour) + 6f : 6f - hour;
                what = "距天亮";
            }
            else
            {
                until = 23f - hour;
                what = "距天黑";
            }
            return string.Format("{0} {1:0.#} 小时", what, until);
        }

        private static void DrawRail(Rect r)
        {
            // S19：右栏不再只服务"在途" —— 待下达（草稿）与前往中（计划）也各有概览/位置/导航，
            // 用的是同一套卡片与同一套行结构（只有"概览行怎么算"不同）。
            // ⚠️ S18 之前这里第一句是 `if (d == null || site == null) return;` ⇒
            //    计划与草稿的右栏**整块是空的**（用户图三右侧那片空白就是它）。
            if (selectedDraft != null && selectedDraft.site != null)
            {
                DelegationDraft draft = selectedDraft;
                RailRun(r, c => RailCards(c, BuildDraftGlanceRows(draft), draft.site, draft.caravan));
                return;
            }
            Delegation d = selected?.active;
            Site site = selected?.Site;
            if (d == null || site == null)
            {
                if (selected != null && selected.HasPlan)
                {
                    WorldObjectComp_Delegations comp = selected;
                    RailRun(r, c => RailCards(c, BuildPlanGlanceRows(comp), site, comp.plannedCaravan));
                }
                return;
            }
            Rect inner = r.ContractedBy(2f);
            float viewW = inner.width - 14f;
            Cursor measure = new Cursor(false, new Rect(0f, 0f, viewW, 0f), 0f);
            RailPass(measure, d, site);
            Rect view = new Rect(0f, 0f, viewW, Mathf.Max(measure.y, inner.height));

            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(inner, ref railScroll, view);
                opened = true;
                RailPass(new Cursor(true, view, 0f), d, site);
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        private static void RailPass(Cursor c, Delegation d, Site site)
        {
            RailCards(c, BuildGlanceRows(d, site), site, d.caravan);
        }

        /// <summary>
        /// 右栏三张卡（概览 / 位置 / 导航）。S19 抽出来给**三种上下文**共用：
        /// 在途（<see cref="BuildGlanceRows" />）、待下达草稿（<see cref="BuildDraftGlanceRows" />）、
        /// 前往中计划（<see cref="BuildPlanGlanceRows" />）—— 只有"概览行怎么算"不同，
        /// 卡片顺序、位置缩略图、导航按钮全都一模一样。
        ///
        /// S11 的历史：这里原来的第一张卡是「操作」（暂停 / 加班 / 改模式 / 编辑参与者），
        /// 用户把那些按钮一路指到底栏之后整张删掉，顺序回到"先看状态，再看地点与导航"。
        /// </summary>
        private static void RailCards(Cursor c, List<GlanceEntry> glance, Site site, Caravan caravan)
        {
            Rect glanceCard = c.Card("概览", GlanceCardHeight(glance.Count));
            if (c.draw)
            {
                float gy = CardContentTop(glanceCard);
                for (int i = 0; i < glance.Count; i++)
                {
                    DrawGlanceRow(glanceCard, ref gy, glance[i]);
                }
            }

            // ---- 位置：真实世界地图缩略图（RadiusUI 的 WorldSnapshot）
            Rect locCard = c.Card("位置", LocationCardHeight());
            if (c.draw)
            {
                DrawLocationCard(locCard, site);
            }

            // ---- 导航
            Rect navCard = c.Card("导航", GotoCardHeight());
            if (c.draw)
            {
                float by = CardContentTop(navCard) + 2f;
                Rect b1 = new Rect(navCard.x + 8f, by, navCard.width - 16f, BtnH);
                if (UIKit.Button(b1, "跳转至该地点", ButtonStyle.Ghost, true, "将地图镜头移至该事件点"))
                {
                    // MovementMode 是嵌套类型 `Verse.CameraJumper.MovementMode`（不是裸 MovementMode）；
                    // (…)0 = Quest Menu 用的那个值，不猜枚举名。
                    CameraJumper.TryJumpAndSelect(site, (CameraJumper.MovementMode)0);
                }
                Rect b2 = new Rect(navCard.x + 8f, by + BtnH + 4f, navCard.width - 16f, BtnH);
                if (UIKit.Button(b2, "选中该远行队", ButtonStyle.Ghost, caravan != null,
                        caravan != null ? "在世界地图上选中执行该委派的远行队" : "这一项还没有对应的远行队"))
                {
                    if (caravan != null && !caravan.Destroyed)
                    {
                        Find.WorldSelector.Select(caravan, true);
                    }
                }
            }
        }

        /// <summary>右栏的两趟跑法（量高 → 滚动视图里真画），三种上下文共用。</summary>
        private static void RailRun(Rect r, Action<Cursor> pass)
        {
            Rect inner = r.ContractedBy(2f);
            float viewW = inner.width - 14f;
            Cursor measure = new Cursor(false, new Rect(0f, 0f, viewW, 0f), 0f);
            pass(measure);
            Rect view = new Rect(0f, 0f, viewW, Mathf.Max(measure.y, inner.height));
            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(inner, ref railScroll, view);
                opened = true;
                pass(new Cursor(true, view, 0f));
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        /// <summary>
        /// 草稿（待下达）的概览行（S19）：只列**算得出来的**那些 ——
        /// 未下达状态 / 模式 / 姿态 / 结束条件 / 预计完成 / 参与者 / 载重 / 补给。
        ///
        /// 为什么不照搬在途那几行：进度条、累计作业小时、"剩余 X 天"都以 `Delegation` 实例为前提，
        /// 草稿还没有委派在跑。**宁可少一行，也不显示一行假的 0**（本项目一贯口径）。
        /// 可点的两行（模式 / 结束条件）与在途那边**同一套交互**：点行即改，不给第二个入口。
        /// </summary>
        private static List<GlanceEntry> BuildDraftGlanceRows(DelegationDraft draft)
        {
            List<GlanceEntry> rows = new List<GlanceEntry>();
            DelegationWorker worker = draft.Worker;
            Site site = draft.site;
            float mood = DelegationUtility.DailyMoodOffset(draft.def, draft.mode);
            List<Pawn> chosen = draft.ChosenList();

            rows.Add(new GlanceEntry
            {
                Icon = "Action/Draft",
                SubIcon = draft.onTile ? "Action/Play" : "Common/Cal",
                Tint = Palette.Flat.Accent,
                Main = "未下达",
                Sub = draft.onTile
                    ? "人已到位 · 确认即就地开工"
                    : "尚未开工 · 无累计作业（等待确认）",
                SubColor = Palette.Flat.InkLow
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Action/Snooze",
                SubIcon = mood < 0f ? "Stat/TrendDown" : "Stat/Mood",
                Tint = Palette.Flat.InkMid,
                Main = draft.ModeLine(),
                Sub = DelegationUIUtility.SatisfactionLineEstimated(draft.mode),
                SubColor = mood < 0f ? Palette.Warn : Palette.Flat.InkLow,
                Clickable = true,
                OnClick = draft.OpenModeMenu,
                Tip = "点击切换委派模式（作息窗口 / 作业强度）——换班本身不扣心情，满意度按新模式重算"
            });
            if (!draft.def.approaches.NullOrEmpty())
            {
                rows.Add(new GlanceEntry
                {
                    Icon = "Common/Sword",
                    Tint = Palette.Flat.InkMid,
                    Main = draft.ApproachLine(),
                    Sub = "作战姿态（决定战斗怎么结算）",
                    SubColor = Palette.Flat.InkLow,
                    Clickable = true,
                    OnClick = draft.OpenApproachMenu,
                    Tip = "点击切换作战姿态（强攻 / 潜入；潜入会掷暴露）"
                });
            }
            rows.Add(new GlanceEntry
            {
                Icon = "Action/Check",
                SubIcon = draft.abortWhenOutOfFood ? "Alert/Warning" : "Stat/Food",
                Tint = Palette.Flat.InkMid,
                Main = draft.EndConditionLabel(),
                Sub = draft.abortWhenOutOfFood
                    ? "补给耗尽时中止"
                    : string.Format("饿着也继续{0}", worker?.ActivityName ?? "开采"),
                SubColor = Palette.Flat.InkLow,
                Clickable = true,
                OnClick = draft.OpenEndConditionMenu,
                Tip = "点击修改结束条件（取尽 / 按天数 / 按产出配额）"
            });

            float perDay = (draft.mode == null || worker == null)
                ? 0f
                : worker.EstimateUnitsPerDayFor(chosen, draft.mode, site.Tile, site,
                    DelegationSatisfaction.RateFactor(DelegationSatisfaction.EstimatedValue(draft.mode, 0f)));
            rows.Add(new GlanceEntry
            {
                Icon = "Common/Clock",
                SubIcon = "Common/Cal",
                Tint = Palette.Flat.InkMid,
                Main = perDay > 0f
                    ? string.Format("预计 {0:0.#}–{1:0.#} 天", draft.dispMinCells / perDay, draft.dispMaxCells / perDay)
                    : "未知",
                Sub = perDay > 0f
                    ? string.Format("约 {0:0.#} {1}/天（按当前模式与已选人员）", perDay, worker.UnitName)
                    : (worker?.EstimateUnavailableReason(site, draft.preview, draft.exactDeposit) ?? "要等队伍到了才清楚"),
                SubColor = Palette.Flat.InkLow,
                Tip = draft.exactDeposit != null
                    ? "这一处的底细已经摸清了 ⇒ 上面是按实际规模算的"
                    : "这一处的底细要等队伍到了才清楚，所以只能给个范围"
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Status/Focus",
                SubIcon = "Status/Zzz",
                Tint = Palette.Flat.InkMid,
                Main = string.Format("参与人员 {0} 人", chosen.Count),
                Sub = string.Format("疲劳：已选人员平均休息 {0}",
                    DelegationUtility.AverageRestOf(chosen).ToStringPercent()),
                SubColor = Palette.Flat.InkLow,
                Tip = DelegationUIUtility.FatigueRiskLineOf(chosen)
            });
            if (draft.TryMassForecast(out float now, out float min, out float max, out float cap))
            {
                bool warn = now + max > cap;
                rows.Add(new GlanceEntry
                {
                    Icon = "Stat/Weight",
                    SubIcon = "Slot/Pack",
                    Tint = warn ? Palette.Bad : Palette.Flat.InkMid,
                    Main = DelegationUIUtility.MassMain(now, min, max, cap),
                    Sub = DelegationUIUtility.MassSub(now, min, max, cap),
                    SubColor = warn ? Palette.Bad : Palette.Flat.InkLow
                });
            }
            else
            {
                // S20 用户口径：「每个项目都显示，数据不知道就提示无法估算」
                // —— 条目在不在是"版面一致"的问题，值可不可信是"不许撒谎"的问题，两件事分开办。
                rows.Add(new GlanceEntry
                {
                    Icon = "Stat/Weight",
                    SubIcon = "Slot/Pack",
                    Tint = Palette.Flat.InkMid,
                    Main = "未知",
                    Sub = "现场详情需待队伍确认",
                    SubColor = Palette.Flat.InkLow
                });
            }
            AppendFoodRow(rows, draft.caravan);
            return rows;
        }

        /// <summary>
        /// 「前往中」计划的概览行（S19）：计划**还没有 `Delegation` 实例**，
        /// 所以取值全部来自 `plannedDef` / `plannedRequest`（`null` = 当初选了「延后决定」）。
        ///
        /// 这就是图三右栏空白的原因与修法：以前 `DrawRail` 在第一句就把 `d == null` 的场合 return 掉了。
        /// </summary>
        private static List<GlanceEntry> BuildPlanGlanceRows(WorldObjectComp_Delegations comp)
        {
            List<GlanceEntry> rows = new List<GlanceEntry>();
            DelegationDef def = comp.plannedDef;
            DelegationRequest req = comp.plannedRequest;
            DelegationWorker worker = def?.CreateWorker();
            bool decided = req != null;

            rows.Add(new GlanceEntry
            {
                Icon = "Action/Draft",
                SubIcon = "Common/Cal",
                Tint = Palette.Flat.Accent,
                Main = DelegationUIUtility.PlanStatusWord(comp),
                Sub = "计划已交给远行队 · 尚未开工（无累计作业）",
                SubColor = Palette.Flat.InkLow
            });
            // S20：这一行在途那边是「剩余 X 天」—— 计划给不出（现场情况与速率都要等队伍到位），
            // 但**条目照留**，值写"未知"（用户口径：每个项目都显示，不知道就填未知）。
            rows.Add(new GlanceEntry
            {
                Icon = "Common/Clock",
                SubIcon = "Common/Cal",
                Tint = Palette.Flat.InkMid,
                Main = "未知",
                Sub = "要等队伍抵达后才清楚",
                SubColor = Palette.Flat.InkLow,
                Tip = "开工之后才有进度与剩余天数；抵达时会看到实际规模"
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Action/Snooze",
                SubIcon = "Stat/Mood",
                Tint = Palette.Flat.InkMid,
                Main = decided ? DelegationUIUtility.ModeLine(req.mode) : "抵达后再定",
                Sub = decided ? "下单时已经选定的模式" : "当初选的是「延后决定」",
                SubColor = Palette.Flat.InkLow,
                Tip = decided
                    ? null
                    : "抵达后会出现一张「待下达」表单；也可以在主控台右栏点「继续决定」，现在就把它补完"
            });
            rows.Add(new GlanceEntry
            {
                Icon = "Action/Check",
                SubIcon = decided && req.abortWhenOutOfFood ? "Alert/Warning" : "Stat/Food",
                Tint = Palette.Flat.InkMid,
                Main = decided
                    ? DelegationUIUtility.EndConditionLabel(req.endCondition, req.daysLimit, req.quotaUnits, worker)
                    : "抵达后再定",
                Sub = decided
                    ? (req.abortWhenOutOfFood ? "补给耗尽时中止" : string.Format("饿着也继续{0}", worker?.ActivityName ?? "开采"))
                    : "—",
                SubColor = Palette.Flat.InkLow
            });
            if (!def.approaches.NullOrEmpty())
            {
                rows.Add(new GlanceEntry
                {
                    Icon = "Common/Sword",
                    Tint = Palette.Flat.InkMid,
                    Main = decided ? DelegationUIUtility.ApproachLabel(req.approach) : "抵达后再定",
                    Sub = "作战姿态",
                    SubColor = Palette.Flat.InkLow
                });
            }
            rows.Add(new GlanceEntry
            {
                Icon = "Status/Focus",
                SubIcon = "Status/Zzz",
                Tint = Palette.Flat.InkMid,
                Main = string.Format("参与人员 {0} 人{1}", decided ? (req.pawns?.Count ?? 0) : 0,
                    decided ? "（已定）" : "（抵达后再定）"),
                Sub = decided
                    ? string.Format("疲劳：平均休息 {0}", DelegationUtility.AverageRestOf(req.pawns).ToStringPercent())
                    : "抵达后再选人",
                SubColor = Palette.Flat.InkLow
            });
            // 载重：能拿到的照给（车队现在的占用与上限），拿不到的（产物增量）明写"无法估算"
            if (comp.plannedCaravan != null)
            {
                rows.Add(new GlanceEntry
                {
                    Icon = "Stat/Weight",
                    SubIcon = "Slot/Pack",
                    Tint = Palette.Flat.InkMid,
                    Main = string.Format("载重 {0:0.#} / {1:0.#} kg",
                        comp.plannedCaravan.MassUsage, comp.plannedCaravan.MassCapacity),
                    Sub = "预计增量未知（现场情况要等队伍到了才清楚）",
                    SubColor = Palette.Flat.InkLow
                });
            }
            AppendFoodRow(rows, comp.plannedCaravan);
            return rows;
        }

        /// <summary>补给那一行（三种上下文共用）：不足 2 天标红，判据与文字同源（`FoodDaysLeft`）。</summary>
        private static void AppendFoodRow(List<GlanceEntry> rows, Caravan caravan)
        {
            if (caravan == null)
            {
                return;
            }
            string food = DelegationUIUtility.FoodDaysLine(caravan);
            if (food.NullOrEmpty())
            {
                return;
            }
            bool warn = DelegationUIUtility.FoodDaysLeft(caravan) < 2f;
            rows.Add(new GlanceEntry
            {
                Icon = "Stat/Food",
                SubIcon = "Occasion/Caravan",
                Tint = warn ? Palette.Bad : Palette.Flat.InkMid,
                Main = food,
                MainColor = warn ? Palette.Bad : default(Color),
                Sub = warn ? "远行队补给总量（不足 2 天）" : "远行队补给总量",
                SubColor = warn ? Palette.Bad : Palette.Flat.InkLow
            });
        }

        // ================================================================ ⑤ 底部状态栏

        private static void DrawBottomBar(Rect r)
        {
            CardChrome.Fill(new Rect(r.x, r.y, r.width, 1f), Palette.Border);
            // S18：选中的是「待下达」的表单 → 底栏给三个出口（与原版主控台同一套语义与措辞）
            if (selectedDraft != null)
            {
                DrawDraftBottomBar(r);
                return;
            }
            Delegation d = selected?.active;
            Site site = selected?.Site;
            DelegationDef def = d?.def;

            // S12：选中的是「前往中」的计划 —— 底栏只给"取消计划 / 选中远行队"，
            // 暂停/加班/中止这些属于**已开工**的委派（计划还没有 Delegation 实例）。
            if (d == null && selected != null && selected.HasPlan)
            {
                DrawPlanBottomBar(r, selected);
                return;
            }

            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            float y = r.y + (r.height - metaH) * 0.5f;
            float btnY = r.y - (BtnH - r.height) * 0.5f;

            // ---- 从右往左排：中止（红）/ [结束加班] / 紧急加班（红）/ 暂停委派 / 固定
            //
            // S11：暂停委派与紧急加班从"右上角那张操作卡"搬到这里 ——
            // 用户截图里那支红色箭头就是从卡里画到这排按钮上的（"调整到最下面"）。
            // S13：用户要求"固定移动到暂停委派的左边，然后调整这几个按钮的布局" ⇒
            // 从左到右变成 `固定 | 暂停委派 | 紧急加班 | [结束加班] | 中止委派`：
            // 左边是"阅读/作业状态"类的轻动作，右边是立刻改变现场状态、越往右越破坏性。
            const float AbortW = 100f;
            const float PinW = 76f;
            const float PauseW = 104f;
            const float OvertimeW = 118f;
            const float CancelW = 92f;
            const float GapX = 6f;

            Rect abort = new Rect(r.xMax - AbortW, btnY, AbortW, BtnH);
            if (d != null && SkinButtons.Danger(abort, "RimDelegationTabAbort".Translate(), true, null))
            {
                selected.Abort(d, "RimDelegationAbortByPlayer".Translate());
            }

            float rightEdge = abort.x - GapX;
            if (d != null && def != null && def.AllowsEmergencyOvertime)
            {
                if (d.EmergencyOvertimeActive)
                {
                    Rect cancel = new Rect(rightEdge - CancelW, btnY, CancelW, BtnH);
                    if (UIKit.Button(cancel, "RimDelegationTabOvertimeCancel".Translate(), ButtonStyle.Solid, true,
                            "作废剩余加班额度，队员回到正常作息（心情代价不退还）"))
                    {
                        selected.CancelEmergencyOvertime(d);
                    }
                    rightEdge = cancel.x - GapX;
                }
                // ⚠️ 文案与页签 / 原版主控台同一条口径（`.Translate()` + string.Format：
                //    `"key".Translate(参数)` 会把 `{0:0.#}` 整段吃掉，只留"+h"）
                string overtimeLabel = d.EmergencyOvertimeActive
                    ? string.Format("RimDelegationTabOvertimeActive".Translate(), d.overtimeTicksRemaining / 2500f)
                    : string.Format("RimDelegationTabOvertime".Translate(), def.emergencyOvertimeHoursPerUse);
                Rect overtime = new Rect(rightEdge - OvertimeW, btnY, OvertimeW, BtnH);
                if (SkinButtons.Danger(overtime, overtimeLabel, !d.paused,
                        "紧急加班：买几小时\"无视工时窗口\"的额度；期间不补休息（事故伤害风险上升），心情代价按本地点第几次加班递进"))
                {
                    selected.StartEmergencyOvertime(d);
                }
                rightEdge = overtime.x - GapX;
            }
            if (d != null)
            {
                Rect pause = new Rect(rightEdge - PauseW, btnY, PauseW, BtnH);
                if (UIKit.Button(pause, d.paused ? "RimDelegationTabResume".Translate() : "RimDelegationTabPause".Translate(),
                        ButtonStyle.Solid, true,
                        d.paused ? "恢复作业：队员回到正常作息。" : "暂停委派：队员就地休息，暂停期间不产出、不计入计划天数。"))
                {
                    selected.TogglePause(d);
                }
                rightEdge = pause.x - GapX;

                // 固定在最左（用户 S13 要求）
                Rect pin = new Rect(rightEdge - PinW, btnY, PinW, BtnH);
                bool isPinned = IsPinned(selected);
                if (UIKit.Button(pin, isPinned ? "已固定" : "固定", ButtonStyle.Solid, true,
                        isPinned
                            ? "取消固定：该委派不再固定显示于列表顶部"
                            : "固定：让它固定显示在列表顶部（只影响本机的显示偏好）"))
                {
                    TogglePin(selected);
                }
                rightEdge = pin.x - GapX;
            }

            // ---- S15：结束报告开关（全局偏好），放在「固定」左边（用户指定位置）。
            // ⚠️ 用原版 `Widgets.CheckboxLabeled`：皮肤框架里暂时没有对应的勾选件，
            //    风格上偏"原版"，等功能优先跑通再换成皮肤件。
            // 宽度不足时**不画**（去 Mod 设置里改），绝不与左侧摘要叠字。
            const float ReportCheckW = 118f;
            if (rightEdge - r.x > ReportCheckW + 200f)
            {
                Rect reportRect = new Rect(rightEdge - ReportCheckW, btnY, ReportCheckW, BtnH);
                bool reportOn = RimDelegationMod.Settings?.reportOnComplete ?? false;
                Widgets.CheckboxLabeled(reportRect, "结束报告", ref reportOn);
                if (RimDelegationMod.Settings != null && reportOn != RimDelegationMod.Settings.reportOnComplete)
                {
                    RimDelegationMod.Settings.reportOnComplete = reportOn;
                    RimDelegationMod.Settings.Write();
                }
                rightEdge = reportRect.x - GapX;
            }

            // ---- 左侧：状态摘要 + 快捷键提示（宽度不够时先让提示，绝不叠到按钮上）
            float textW = Mathf.Max(80f, rightEdge - r.x - 8f);
            string left = d == null ? "—"
                : string.Format("{0} · 进度 {1}", DelegationUIUtility.StatusWord(d, site), d.Progress.ToStringPercent());
            RadiusFont.LabelAt(new Rect(r.x, y, Mathf.Min(textW, r.width * 0.34f), metaH), left,
                RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);

            string hint = "↑/↓ 选择 · Enter 暂停或继续 · Esc 关闭";
            float hintW = RadiusFont.WidthAt(hint, RadiusFont.Scale.Meta, false);
            if (textW - r.width * 0.34f > hintW + 24f)
            {
                RadiusFont.LabelAt(new Rect(r.x + r.width * 0.34f, y, textW - r.width * 0.34f, metaH), hint,
                    RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleCenter, false, false);
            }
        }

        /// <summary>
        /// 「待下达」选中时的底栏（S18）：确认下达 / 延后决定 / 取消。
        ///
        /// 措辞从 `DelegationDraftUI` 的公开常量取 —— 与**原版主控台**那三颗按钮同一个来源
        /// （双端准则：同一句话只许存在一处）。
        /// 按钮阶：确认 = Primary、延后 = Solid、取消 = Ghost；这里是"还没发生任何事的表单"，
        /// 所以**不用红色**（红色留给中止委派那种真正破坏性的动作）。
        /// </summary>
        private static void DrawDraftBottomBar(Rect r)
        {
            DelegationDraft draft = selectedDraft;
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            float y = r.y + (r.height - metaH) * 0.5f;
            float btnY = r.y - (BtnH - r.height) * 0.5f;
            const float GapX = 6f;
            bool hasDefer = draft.onDefer != null && !draft.onTile;

            // 从右往左：取消 / [延后决定] / 确认下达
            const float CancelW = 88f;
            Rect cancel = new Rect(r.xMax - CancelW, btnY, CancelW, BtnH);
            if (UIKit.Button(cancel, DelegationDraftUI.CancelLabel, ButtonStyle.Ghost, true,
                    "放弃这张表单：不下达、也不留记录（车队原地不动）"))
            {
                draft.Discard();
            }
            float rightEdge = cancel.x - GapX;

            if (hasDefer)
            {
                const float DeferW = 108f;
                Rect defer = new Rect(rightEdge - DeferW, btnY, DeferW, BtnH);
                if (UIKit.Button(defer, DelegationDraftUI.DeferLabel, ButtonStyle.Solid, true,
                        "先把车队派过去（先不定人、模式、结束条件），抵达后再决定 —— 那时看到的是实际规模"))
                {
                    draft.Defer();
                }
                rightEdge = defer.x - GapX;
            }

            const float ConfirmW = 116f;
            Rect confirm = new Rect(rightEdge - ConfirmW, btnY, ConfirmW, BtnH);
            if (UIKit.Button(confirm, DelegationDraftUI.ConfirmLabel, ButtonStyle.Primary, true,
                    "立刻带着当前选择下达（就地开工，或挂到行驶路径上、抵达即开工）"))
            {
                draft.Confirm();
            }
            rightEdge = confirm.x - GapX;

            RadiusFont.LabelAt(new Rect(r.x, y, Mathf.Max(80f, rightEdge - r.x - 8f), metaH),
                string.Format("{0} · {1} · 已选 {2} 人",
                    draft.def?.label ?? "委派", draft.EndConditionLabel(), draft.ChosenList().Count),
                RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);
        }

        /// <summary>「前往中」计划选中时的底栏（S12）：状态摘要 + 取消计划 / 选中远行队。</summary>
        private static void DrawPlanBottomBar(Rect r, WorldObjectComp_Delegations comp)
        {
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            float y = r.y + (r.height - metaH) * 0.5f;
            float btnY = r.y - (BtnH - r.height) * 0.5f;
            const float W = 104f;
            const float GapX = 6f;

            Caravan caravan = comp.plannedCaravan;
            RadiusFont.LabelAt(new Rect(r.x, y, r.width * 0.5f, metaH),
                string.Format("{0} · {1}", caravan?.Name ?? "?", DelegationUIUtility.PlanStatusWord(comp)),
                RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);

            int slot = 0;
            Rect Next()
            {
                Rect rect = new Rect(r.xMax - W * (slot + 1) - GapX * slot, btnY, W, BtnH);
                slot++;
                return rect;
            }

            if (UIKit.Button(Next(), "选中远行队", ButtonStyle.Solid, caravan != null,
                    caravan != null ? "在世界地图上选中正在前往该地点的远行队" : "这一项还没有对应的远行队"))
            {
                if (caravan != null && !caravan.Destroyed)
                {
                    Find.WorldSelector.Select(caravan, true);
                }
            }
            // S19：人到了、但那次选择没做完（当初选「延后决定」，或抵达时把窗口关了）—— 回填入口。
            // 原来它长在**主列底部**，与这里重复；现在只留这一个出口。
            if (Window_Delegations.CanResumeDecision(comp))
            {
                if (UIKit.Button(Next(), DelegationDraftUI.ResumeLabel, ButtonStyle.Solid, true,
                        "远行队已经到了，但那次选择还没做完。点这里把「待下达」的表单调出来。"))
                {
                    Window_Delegations.ResumeDecision(comp);
                }
            }
            if (SkinButtons.Danger(Next(), "取消计划", true,
                    "取消抵达动作：远行队仍会走到该地点，但抵达后不会开工。"))
            {
                comp.CancelPlan();
            }
        }

        // ================================================================ 键盘

        private static void HandleKeys(Window_Delegations win)
        {
            // 搜索框有焦点时绝不抢键（否则打字会翻列表）
            if (search != null && search.CurrentlyFocused())
            {
                return;
            }
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown)
            {
                return;
            }
            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    win.Close(true);
                    e.Use();
                    break;
                case KeyCode.UpArrow:
                    MoveSelection(-1);
                    e.Use();
                    break;
                case KeyCode.DownArrow:
                    MoveSelection(1);
                    e.Use();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    // S18：选中的是待下达的草稿时，Enter 不做事（草稿的下达要显式点按钮，
                    // 免得一个回车就把车队派出去了）
                    Delegation sel = selectedDraft == null ? selected?.active : null;
                    if (sel != null)
                    {
                        selected.TogglePause(sel);
                    }
                    e.Use();
                    break;
            }
        }

        /// <summary>
        /// 皮肤版的**历史报告**（S15 第二期）：数据与签核窗口同一份（`DelegationRecord.ToReportData`），
        /// 画法用 RadiusFont / Palette。只读视图 ⇒ 单趟直接画、自带滚动，不参与主列的两趟测高。
        ///
        /// RIM-9（2026-10-05）起**正文画法整个搬到** <see cref="ReportSkin.Draw"/> ——
        /// 因为收工签核窗口（`Dialog_DelegationReport`）也要同一份排版，
        /// 留在这里就会出现"历史里一种排法、签核窗口里另一种"的老毛病。这里只剩转交。
        /// </summary>
        private static void RecordMain(Rect r)
        {
            ReportSkin.Draw(r, selectedRecord.ToReportData(), ref recordScroll);
        }

        /// <summary>历史那一组的一行（S15 第二期）：标题（收工 / 中断）+ 结束时刻 · 时长 · 事件条数。</summary>
        private static void DrawHistoryRow(Rect row, DelegationRecord rec)
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
            float lh = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            RadiusFont.LabelAt(new Rect(row.x + 4f, row.y + 2f, row.width - 8f, lh), rec.Title,
                RadiusFont.Scale.Meta, rec.aborted ? Palette.Warn : Palette.Flat.Ink,
                TextAnchor.MiddleLeft, false, false);
            RadiusFont.LabelAt(new Rect(row.x + 4f, row.y + 2f + lh, row.width - 8f, lh), rec.SubLine,
                RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
            TooltipHandler.TipRegion(row, rec.Title + "\n" + rec.SubLine + "\n（点击查看完整报告）");
            if (Widgets.ButtonInvisible(row))
            {
                selectedRecord = rec;
                recordScroll = Vector2.zero;
            }
        }

        private static void MoveSelection(int delta)
        {
            // 方向键一动就离开历史 / 草稿（历史在列表最下方，草稿在列表最上方，方向键都不跨过去）
            selectedRecord = null;
            selectedDraft = null;
            if (visible.Count == 0)
            {
                return;
            }
            int i = visible.IndexOf(selected);
            if (i < 0)
            {
                i = 0;
            }
            i = Mathf.Clamp(i + delta, 0, visible.Count - 1);
            if (visible[i] != selected)
            {
                selected = visible[i];
                mainScroll = Vector2.zero;
            }
        }

        // ================================================================ 过滤 / 分组 / 排序

        private static void BuildVisible(List<WorldObjectComp_Delegations> all)
        {
            visible.Clear();
            string text = search?.filter?.Text;
            for (int i = 0; i < all.Count; i++)
            {
                WorldObjectComp_Delegations comp = all[i];
                Delegation d = comp?.active;
                if (d == null)
                {
                    continue;
                }
                if (!text.NullOrEmpty() && !Matches(comp, text))
                {
                    continue;
                }
                visible.Add(comp);
            }
            visible.Sort(Compare);
            RebuildBuckets();
        }

        private static bool Matches(WorldObjectComp_Delegations comp, string text)
        {
            Delegation d = comp.active;
            Site site = comp.Site;
            string needle = text.Trim();
            if (Contains(site?.Label, needle) || Contains(d.caravan?.Name, needle)
                || Contains(d.Worker?.ActivityName, needle) || Contains(d.def?.label, needle))
            {
                return true;
            }
            return false;
        }

        private static bool Contains(string haystack, string needle)
        {
            return !haystack.NullOrEmpty()
                   && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int Compare(WorldObjectComp_Delegations a, WorldObjectComp_Delegations b)
        {
            // 置顶优先（与分组一致，保证 ↑↓ 的顺序 = 眼睛看到的顺序）
            bool pa = IsPinned(a), pb = IsPinned(b);
            if (pa != pb)
            {
                return pa ? -1 : 1;
            }
            switch (sort)
            {
                case ConsoleSort.Progress:
                    return b.active.Progress.CompareTo(a.active.Progress);
                case ConsoleSort.Site:
                    return string.Compare(a.Site?.Label, b.Site?.Label, StringComparison.CurrentCulture);
                default:
                    float da = DaysLeft(a), db = DaysLeft(b);
                    if (Mathf.Approximately(da, db))
                    {
                        return string.Compare(a.Site?.Label, b.Site?.Label, StringComparison.CurrentCulture);
                    }
                    return da.CompareTo(db);
            }
        }

        private static float DaysLeft(WorldObjectComp_Delegations comp)
        {
            Site site = comp?.Site;
            if (site == null || comp.active == null)
            {
                return float.MaxValue;
            }
            float d = comp.active.EstimatedDaysLeft(site.Tile);
            return d < 0f ? float.MaxValue : d;
        }

        private static void RebuildBuckets()
        {
            pinnedBucket.Clear();
            workingBucket.Clear();
            restingBucket.Clear();
            pausedBucket.Clear();
            stalledBucket.Clear();
            for (int i = 0; i < visible.Count; i++)
            {
                WorldObjectComp_Delegations comp = visible[i];
                Delegation d = comp.active;
                Site site = comp.Site;
                if (IsPinned(comp))
                {
                    pinnedBucket.Add(comp);
                }
                else if (d.paused)
                {
                    pausedBucket.Add(comp);
                }
                else if (d.IsStalled(GenTicks.TicksAbs))
                {
                    stalledBucket.Add(comp);
                }
                else if (site != null && d.IsWorkTime(site, GenTicks.TicksAbs))
                {
                    workingBucket.Add(comp);
                }
                else
                {
                    restingBucket.Add(comp);
                }
            }
        }

        private static float GroupTotal()
        {
            float h = 0f;
            h += GroupH + pinnedBucket.Count * RowH;
            h += GroupH + workingBucket.Count * RowH;
            h += GroupH + restingBucket.Count * RowH;
            h += GroupH + pausedBucket.Count * RowH;
            h += GroupH + stalledBucket.Count * RowH;
            return h + 4f;
        }

        private static int CountWorking()
        {
            return workingBucket.Count;
        }

        private static int CountPaused()
        {
            return pausedBucket.Count;
        }

        private static int SelectedIndex()
        {
            int i = visible.IndexOf(selected);
            return i < 0 ? 0 : i;
        }

        private static void EnsureSelection(List<WorldObjectComp_Delegations> planned,
            List<DelegationDraft> drafts)
        {
            // S15 第二期：选中的是历史记录时保持不变（除非它被 FIFO 挤掉）
            if (selectedRecord != null)
            {
                List<DelegationRecord> history = RimDelegationHistory.Get(false)?.records;
                if (history != null && history.Contains(selectedRecord))
                {
                    return;
                }
                selectedRecord = null;
            }
            // S18：选中的草稿被确认 / 取消 / 失效了
            if (selectedDraft != null)
            {
                if (drafts != null && drafts.Contains(selectedDraft) && !selectedDraft.IsStale())
                {
                    return;
                }
                selectedDraft = null;
            }
            if (selected != null)
            {
                bool stillThere = (selected.active != null && visible.Contains(selected))
                                  || (selected.active == null && planned != null && planned.Contains(selected));
                if (stillThere)
                {
                    return;
                }
            }
            // S18：优先选"待下达"（它在等人拍板，不动就永远不会开工）→ 进行中 → 前往中
            if (drafts != null && drafts.Count > 0)
            {
                SelectDraft(drafts[0]);
                return;
            }
            if (visible.Count > 0)
            {
                selected = visible[0];
            }
            else
            {
                selected = planned != null && planned.Count > 0 ? planned[0] : null;
            }
        }

        /// <summary>S18：选中一张草稿（与"在途 / 前往中 / 历史"三种选中互斥）。</summary>
        private static void SelectDraft(DelegationDraft draft)
        {
            selectedDraft = draft;
            selected = null;
            selectedRecord = null;
        }

        /// <summary>
        /// S18：当前帧的草稿表（顺手清失效项）。
        ///
        /// 与 `Window_Delegations.DraftList()` 同构 —— 那边给原版侧用，这边给皮肤用；
        /// 数据源同一个（`RimDelegationDrafts` 这个 GameComponent），所以两边永远看到同一批表单。
        /// </summary>
        private static List<DelegationDraft> DraftList()
        {
            RimDelegationDrafts store = RimDelegationDrafts.Get(false);
            if (store == null)
            {
                return noDrafts;
            }
            store.Prune();
            return store.drafts ?? noDrafts;
        }

        /// <summary>
        /// 左栏那颗排序按钮的**短标签**（S10）：左栏只有 96px 给按钮，
        /// 「排序：剩余时间」放不下 ⇒ 去掉前缀，档位由 tooltip 说明。
        /// </summary>
        private static string SortShortLabel()
        {
            switch (sort)
            {
                case ConsoleSort.Progress:
                    return "进度";
                case ConsoleSort.Site:
                    return "地点";
                default:
                    return "剩余时间";
            }
        }

        // ================================================================ Pin（本地、不进存档）

        private static bool IsPinned(WorldObjectComp_Delegations comp)
        {
            Site site = comp?.Site;
            return site != null && pinned.Contains(site.ID);
        }

        private static void TogglePin(WorldObjectComp_Delegations comp)
        {
            Site site = comp?.Site;
            if (site == null)
            {
                return;
            }
            if (!pinned.Remove(site.ID))
            {
                pinned.Add(site.ID);
            }
            RimDelegationSettings s = RimDelegationMod.Settings;
            if (s != null)
            {
                s.pinnedSites = new List<int>(pinned);
                RimDelegationMod.Instance?.WriteSettings();
            }
            RebuildBuckets();
        }

        /// <summary>
        /// 从本地 Mod 配置读回置顶集合，并**清掉已经不存在的站点**（照 Quest Menu 的 `Prune` 做法）。
        /// 说明：这是皮肤自己的偏好（ModSettings），**不写存档** —— 与 README 的承诺一致。
        /// </summary>
        private static void LoadPinnedOnce()
        {
            if (pinnedLoaded)
            {
                return;
            }
            pinnedLoaded = true;
            RimDelegationSettings s = RimDelegationMod.Settings;
            if (s?.pinnedSites == null)
            {
                return;
            }
            HashSet<int> live = new HashSet<int>();
            List<WorldObjectComp_Delegations> all = DelegationRegistry.AllActive();
            for (int i = 0; i < all.Count; i++)
            {
                Site site = all[i]?.Site;
                if (site != null)
                {
                    live.Add(site.ID);
                }
            }
            for (int i = 0; i < s.pinnedSites.Count; i++)
            {
                int id = s.pinnedSites[i];
                if (live.Contains(id))
                {
                    pinned.Add(id);
                }
            }
        }

        // ================================================================ 空态 / 未选中引导

        /// <summary>
        /// 主列在"没有选中项"时的引导（S17）。
        ///
        /// 背景：原来整屏空态是**早退**掉的（只画两行居中提示就 return），三栏与底栏一起消失。
        /// 现在改成"三栏永远画，空态只落在主列这一块"：左栏仍然列出常驻的「历史」组，
        /// 右栏与底栏照常铺底，主列负责说明"现在能做什么"。
        ///
        /// ⚠️ 判据用**真实注册表**（`DelegationRegistry` / `RimDelegationHistory`）而**不是** `visible`：
        /// 搜索框把在途项全过滤掉时，该说的是"没有匹配"，不是"没有委派"。
        /// </summary>
        private static void DrawNoSelection(Rect r)
        {
            float bodyH = RadiusFont.LineHAt(RadiusFont.Scale.Body, false);
            float metaH = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);
            float y = r.y + 8f;
            RadiusFont.LabelAt(new Rect(r.x + 12f, y, r.width - 24f, bodyH),
                "RimDelegationConsoleNoSelection".Translate(),
                RadiusFont.Scale.Body, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);
            y += bodyH + GapH;

            int activeCount = DelegationRegistry.AllActive().Count;
            int plannedCount = DelegationRegistry.AllPlanned().Count;
            int historyCount = RimDelegationHistory.Get(false)?.records?.Count ?? 0;

            if (activeCount == 0 && plannedCount == 0 && historyCount == 0)
            {
                // 这个存档一条委派都还没有过 —— 给入门引导（与原版主控台同一对 Keyed）
                RadiusFont.LabelAt(new Rect(r.x + 12f, y, r.width - 24f, bodyH),
                    "RimDelegationConsoleEmpty".Translate(),
                    RadiusFont.Scale.Body, Palette.Flat.Ink, TextAnchor.MiddleLeft, false, false);
                y += bodyH + 4f;
                RadiusFont.LabelAt(new Rect(r.x + 12f, y, r.width - 24f, metaH * 3f),
                    "RimDelegationConsoleEmptyHint".Translate(),
                    RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.UpperLeft, false, true);
                y += metaH * 3f + GapH;
                RadiusFont.LabelAt(new Rect(r.x + 12f, y, r.width - 24f, metaH),
                    "已结束的委派会留在左栏「历史」里，随时可以回看结案报告。",
                    RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
            }
            else if (visible.Count == 0 && plannedCount == 0)
            {
                // 有东西，只是被搜索过滤光了
                RadiusFont.LabelAt(new Rect(r.x + 12f, y, r.width - 24f, metaH),
                    "RimDelegationConsoleNoMatch".Translate(),
                    RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
            }
        }

        // ================================================================ 量高 / 绘制游标

        /// <summary>
        /// "两趟同一份代码"的游标：`draw == false` 只推进 y（量高），`true` 才真画。
        /// 卡片区新增了 `CardBegin/CardEnd/Glance` —— 卡片内边距与行距同样只写一次。
        /// </summary>
        private sealed class Cursor
        {
            public readonly bool draw;
            public readonly Rect view;
            public readonly float w;
            public float y;

            public Cursor(bool draw, Rect view, float y)
            {
                this.draw = draw;
                this.view = view;
                this.w = view.width;
                this.y = y;
            }

            public void Line(string text, float px, Color color)
            {
                float h = RadiusFont.LineHAt(px, false);
                if (draw && !text.NullOrEmpty())
                {
                    RadiusFont.LabelAt(new Rect(view.x, y, w, h), text, px, color,
                        TextAnchor.MiddleLeft, false, false);
                }
                y += h;
            }

            public void Wrapped(string text, float px, Color color)
            {
                if (text.NullOrEmpty())
                {
                    return;
                }
                float h = Mathf.Max(RadiusFont.LineHAt(px, false), RadiusFont.HeightAt(text, w, px, false));
                if (draw)
                {
                    RadiusFont.LabelAt(new Rect(view.x, y, w, h), text, px, color,
                        TextAnchor.UpperLeft, false, true);
                }
                y += h;
            }

            public void Section(string label)
            {
                float h = Mathf.Max(UIKit.Flat.SectionHeaderH, 20f);
                if (draw && !label.NullOrEmpty())
                {
                    UIKit.Flat.SectionHeader(new Rect(view.x, y, w, h), label);
                }
                y += h;
            }

            /// <summary>
            /// 分区标题行 + 右侧动作区（S9：现场物资 / 参与者两个表头要放排序与编辑按钮）。
            ///
            /// 为什么标题只画在**左边那块**（`w − rightW − 8`）而不是整行：
            /// `RadiusFont.LabelAt` 是"画进给定矩形"，文字超出会被裁而不是换行 ——
            /// 让标题和按钮各占各的矩形，就不会出现两张字叠在一起。
            /// 返回右侧矩形；量高趟也会返回同一个矩形（只是调用方不该画）。
            /// </summary>
            public Rect SectionRow(string label, float rightW)
            {
                float h = Mathf.Max(UIKit.Flat.SectionHeaderH, 20f);
                if (draw && !label.NullOrEmpty())
                {
                    UIKit.Flat.SectionHeader(new Rect(view.x, y, Mathf.Max(60f, w - rightW - 8f), h), label);
                }
                Rect right = new Rect(view.x + w - rightW, y, rightW, h);
                y += h;
                return right;
            }

            public void Progress(string tail, float frac, string tip)
            {
                float h = Mathf.Max(16f, RadiusFont.LineHAt(RadiusFont.Scale.Body, false));
                float tailW = RadiusFont.WidthAt(tail, RadiusFont.Scale.Meta, false) + 8f;
                float barW = Mathf.Max(80f, w - tailW);
                if (draw)
                {
                    UIKit.Flat.Bar(new Rect(view.x, y, barW, 16f), frac, frac.ToStringPercent());
                    RadiusFont.LabelAt(new Rect(view.x + barW, y, tailW, h), tail,
                        RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleRight, false, false);
                    if (!tip.NullOrEmpty())
                    {
                        TooltipHandler.TipRegion(new Rect(view.x, y, w, Mathf.Max(16f, h)), tip);
                    }
                }
                y += h + 4f;
            }

            public void Gap()
            {
                // ⚠️ 不能写 `y += Gap`：同名方法 Gap() 会在类作用域里挡住那个常量。
                y += GapH;
            }

            /// <summary>
            /// S28：内容四周缩进 <paramref name="pad" /> 的**另一个游标**（卡片的 8px 内边距 = 用户要的"空隙"）。
            /// 用法：`Cursor inner = c.Padded(8f); …用 inner 画…; c.y = inner.y + 8f;`
            /// —— 高度只有一份账，外层游标负责收尾，内层只管往下走。
            /// </summary>
            public Cursor Padded(float pad)
            {
                return new Cursor(draw,
                    new Rect(view.x + pad, view.y, Mathf.Max(20f, view.width - pad * 2f), view.height),
                    y);
            }

            /// <summary>S28：流程分组小标题（作战任务 / 收集任务）—— 加粗、比正文亮一档，前面留一点空隙。</summary>
            public void GroupTitle(string label)
            {
                y += 4f;
                float h = Mathf.Max(UIKit.Flat.SectionHeaderH, 20f);
                if (draw && !label.NullOrEmpty())
                {
                    RadiusFont.LabelAt(new Rect(view.x, y, w, h), label,
                        RadiusFont.Scale.Section, Palette.Flat.Ink, TextAnchor.MiddleLeft, true, false);
                }
                y += h;
            }

            /// <summary>
            /// S28：带**行内图标**的一行（皮肤画法）。`iconAt` 是图标在 `text` 里的字符位
            /// （由共用件 `DelegationStageList.ExecutorHead` 给出）。
            ///
            /// 画法：前缀画进"前缀宽"的矩形 → 图标 → 后半句从图标右边起、宽度相应缩掉，
            /// 所以后半句的换行天然接在图标后面（不是"两行拼接"那种假对齐）。
            /// 栏太窄放不下时退回"图标在最前"的旧画法。
            /// </summary>
            public void InlineIconLine(string text, int iconAt, Texture2D icon, float px, Color color)
            {
                float lh = RadiusFont.LineHAt(px, false);
                if (icon == null || text.NullOrEmpty() || iconAt < 0 || iconAt > text.Length)
                {
                    Wrapped(text, px, color);
                    return;
                }
                const float slot = DelegationUIUtility.InlineIconSlot;
                string head = text.Substring(0, iconAt);
                string tail = text.Substring(iconAt);
                float headW = RadiusFont.WidthAt(head, px, false);
                if (headW + slot + 2f >= w)
                {
                    if (draw)
                    {
                        GUI.DrawTexture(new Rect(view.x, y + (lh - 12f) * 0.5f, 12f, 12f), icon, ScaleMode.ScaleToFit);
                    }
                    Wrapped("　" + text, px, color);
                    return;
                }
                float tailW = Mathf.Max(40f, w - headW - slot - 2f);
                float h = Mathf.Max(lh, RadiusFont.HeightAt(tail, tailW, px, false));
                if (draw)
                {
                    RadiusFont.LabelAt(new Rect(view.x, y, headW + 2f, lh), head, px, color,
                        TextAnchor.MiddleLeft, false, false);
                    Color old = GUI.color;
                    GUI.color = Color.white;   // 火苗是彩色贴图，别被调用方（停摆染红等）带跑
                    GUI.DrawTexture(new Rect(view.x + headW + 1f, y + (lh - 12f) * 0.5f, 12f, 12f),
                        icon, ScaleMode.ScaleToFit);
                    GUI.color = old;
                    RadiusFont.LabelAt(new Rect(view.x + headW + slot + 1f, y, tailW, h), tail, px, color,
                        TextAnchor.UpperLeft, false, true);
                }
                y += h;
            }

            // ---- 卡片（右栏的 概览 / 位置 / 导航）
            //
            // ⚠️ 卡片必须"高度已知"：卡片底要在内容**之前**铺，否则后画的底板会把内容整个盖掉
            //    （S8-c 第一版就是这么错的 —— `CardEnd` 里才画底 ⇒ 右栏只剩一列图标和空框）。
            //    所以照 Quest Menu 的做法先用纯函数算高度，再 `Card(...)`：draw=false 只推进 y，
            //    draw=true 铺底 + 画标题，返回卡片矩形给调用方画内容。

            public Rect Card(string title, float height)
            {
                Rect card = new Rect(view.x, y, w, height);
                if (draw)
                {
                    CardChrome.Card(card, false);
                    float h = Mathf.Max(UIKit.Flat.SectionHeaderH, 20f);
                    if (!title.NullOrEmpty())
                    {
                        RadiusFont.LabelAt(new Rect(card.x + 8f, card.y + 8f, card.width - 16f, h), title,
                            RadiusFont.Scale.Section, Palette.Flat.InkMid, TextAnchor.MiddleLeft, true, false);
                    }
                }
                y += height + GapH;
                return card;
            }
        }
    }
}
