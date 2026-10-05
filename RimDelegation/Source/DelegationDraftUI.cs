using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 草稿表单的**唯一一份**画法（S18）。
    ///
    /// 从 <see cref="Dialog_ChooseDelegation" /> 里整段搬过来的，只做了三件事：
    ///   ① 所有状态改问 <see cref="DelegationDraft" />（字段全 public，皮肤零反射）；
    ///   ② 三个出口（确认 / 延后 / 取消）**不在这里画** —— 由宿主画
    ///      （对话框画在自己底栏，主控台画在右栏底栏），位置统一用 <see cref="ButtonY" />；
    ///   ③ 版式支持**窄列**：主控台右栏最窄只有 ~370px，而对话框是 640px 固定宽。
    ///      凡是横排多件的那几行都改成了按可用宽度分配，宁可少画单位也不叠字。
    ///
    /// ⚠️ 汇总区（底部那几行）的行文案与行高由**同一批数据**算出来再画（<see cref="BuildSummary" />），
    ///    而不是"先按固定 180 预留、再逐行画" —— 固定高度在窄列里会因自动换行而互相压住
    ///    （S8 的页签正是被"算高度的条件"与"真画的条件"分叉坑过一次）。
    /// </summary>
    public static class DelegationDraftUI
    {
        /// <summary>底部出口按钮那一行的高度（宿主在那条带上画自己的按钮）。</summary>
        public const float ButtonRowHeight = 34f;

        /// <summary>「预期获得」列表：每行高度与最多可见行数（超出的滚动）。</summary>
        public const float ItemRowHeight = 32f;
        public const int MaxItemRows = 3;

        /// <summary>
        /// 顶部描述块的初始矩形高度。
        /// ⚠️ 它**只约束 `Listing_Standard.Begin` 的矩形**，真正的纵向位置走 `top.CurHeight`
        ///    （描述长短不一），所以整块会自然下移，不会跟后面的行重叠。
        /// </summary>
        private const float TopBlockHeight = 214f;

        /// <summary>
        /// 三个出口的措辞（S18）。
        ///
        /// 为什么做成公开常量而不是各自硬编码：原版主控台与 RadiusUI 皮肤主控台**都要画这三颗按钮**
        /// （双端准则），两处各写一遍就会出现"一边叫确认下达、一边叫确认委派"。
        /// 与 `DelegationUIUtility.EndConditionLabel` 同一个思路：能共用的措辞只留一处。
        /// </summary>
        public const string ConfirmLabel = "确认下达";

        public const string DeferLabel = "延后决定";

        public const string CancelLabel = "取消";

        /// <summary>「前往中」那一行的补救入口（人到了、但那次决定没做完）。</summary>
        public const string ResumeLabel = "继续决定";

        /// <summary>出口按钮的 y（宿主用它对齐三颗按钮）。</summary>
        public static float ButtonY(Rect content)
        {
            return content.yMax - ButtonRowHeight;
        }

        /// <summary>
        /// 宿主排版：给定整个可用区，反推出「正文矩形」与「按钮区矩形」。
        ///
        /// 为什么要有这个函数：正文为了给按钮让位，必须在**自己内部**预留
        /// <see cref="ButtonRowHeight" />；而按钮区实际可能占两行（窄列）。
        /// 两边的算术一旦各写一份，就会出现"正文比按钮高 34px"这种谁都看不出来的错位。
        /// 这里保证 <c>ButtonY(content) == buttons.y</c> 恒成立。
        /// </summary>
        public static void Layout(Rect inner, float buttonsHeight, float bottomGap,
            out Rect content, out Rect buttons)
        {
            buttons = new Rect(inner.x, inner.yMax - bottomGap - buttonsHeight, inner.width, buttonsHeight);
            content = new Rect(inner.x, inner.y, inner.width, buttons.y + ButtonRowHeight - inner.y);
        }

        // ================================================================ 主体

        /// <summary>
        /// 画一张草稿表单的**全部正文**（含底部汇总区），并为其下方的按钮行预留
        /// <see cref="ButtonRowHeight" />。<paramref name="content" /> 不要包含标题 ——
        /// 标题由宿主画（对话框画 def@site，主控台画"车队 → 地点"）。
        /// </summary>
        public static void Draw(DelegationDraft draft, Rect content)
        {
            if (draft == null || draft.def == null || content.width <= 40f || content.height <= 80f)
            {
                return;
            }
            DelegationWorker worker = draft.Worker;
            if (worker == null)
            {
                Widgets.Label(new Rect(content.x, content.y, content.width, 30f),
                    "这条委派的数据不完整 —— 可能是相关模组被移除或改名了。");
                return;
            }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            List<SummaryLine> summary = BuildSummary(draft, worker, content.width);
            float summaryH = 0f;
            for (int i = 0; i < summary.Count; i++)
            {
                summaryH += summary[i].height;
            }
            float listBottom = content.yMax - ButtonRowHeight - summaryH;
            float y = content.y;

            // ---- ① 作战任务（S24 恒常；S25：段头可折叠、更凸显）
            bool showCombat = DelegationUIUtility.DrawSectionHeader(
                new Rect(content.x, y, content.width, DelegationUIUtility.SectionHeaderH),
                DelegationUIUtility.SectionCombat, true, DelegationUIUtility.SectionId.Combat);
            y += DelegationUIUtility.SectionHeaderH;
            if (showCombat)
            {
                y = DrawCombatBlock(draft, new Rect(content.x, y, content.width, 0f));
            }

            // ---- ② 收集任务（描述 / 预期获得 / 结束条件 / 补给中止）
            bool showCollect = DelegationUIUtility.DrawSectionHeader(
                new Rect(content.x, y, content.width, DelegationUIUtility.SectionHeaderH),
                DelegationUIUtility.SectionCollect, true, DelegationUIUtility.SectionId.Collect);
            y += DelegationUIUtility.SectionHeaderH;
            if (showCollect)
            {

            Listing_Standard top = new Listing_Standard();
            top.Begin(new Rect(content.x, y, content.width, TopBlockHeight));
            if (!draft.def.description.NullOrEmpty())
            {
                top.Label(draft.def.description);
            }
            string previewText = worker.PreviewLabel(draft.site, draft.preview, draft.exactDeposit);
            if (!previewText.NullOrEmpty())
            {
                top.Label(previewText);
            }
            DrawPreviewItems(draft, worker, top);
            if (draft.exactDeposit != null && draft.exactDeposit.HasBeenWorked)
            {
                // 量纲交给 worker：采矿是"格/单位"，物资藏匿点是"件"
                top.Label(string.Format("此事件点已被委派 {0} 次，累计{1}出 {2:0.#} {3}、累计交付 {4} {5}",
                    draft.exactDeposit.timesDelegated, worker.WorkVerb, draft.exactDeposit.unitsMined,
                    worker.UnitName, draft.exactDeposit.unitsDelivered, worker.OutputUnitName));
            }
            // ⚠️ "模式"= 作业作息（干多久 / 速率 / 心情代价）⇒ 归**收集任务**；
            //    有姿态轴时"姿态"那一行在作战任务段里（见 DrawCombatBlock）。
            if (top.ButtonText("委派模式：" + draft.ModeLine() + "   （点击切换）"))
            {
                draft.OpenModeMenu();
            }
            top.End();

            y += top.CurHeight + 2f;

            // ---- 结束条件 / 补给中止（都是"这次收集怎么收工"，属收集任务）
            y = DrawEndConditionRow(draft, new Rect(content.x, y, content.width, 30f)) + 2f;
            Widgets.CheckboxLabeled(new Rect(content.x, y, content.width, 30f),
                string.Format("补给耗尽时中止（取消勾选 = 饿着也继续{0}）", worker.ActivityName),
                ref draft.abortWhenOutOfFood, false, null, null, false, false);
            y += 32f;

            Widgets.Label(new Rect(content.x, y, content.width, Text.LineHeight),
                string.Format("人数下限 {0}，上限 {1}，共 {2} 人可选",
                    Mathf.Max(1, draft.def.minPawns), draft.def.maxPawns, draft.candidates.Count));
            y += Text.LineHeight + 2f;

            // ---- 工具条：排序 / 全选 / 全不选
            DrawToolbarRow(draft, new Rect(content.x, y, content.width, 28f));
            y += 32f;

            // ---- 人员列表（占满剩下的空间，自己滚动）
            float listH = Mathf.Max(60f, listBottom - y - 4f);
            Rect listRect = new Rect(content.x, y, content.width, listH);
            float viewHeight = Mathf.Max(draft.candidates.Count * DelegationUIUtility.RowHeight + 8f, listRect.height);
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(60f, content.width - 24f), viewHeight);
            Widgets.BeginScrollView(listRect, ref draft.scroll, viewRect);
            if (draft.candidates.Count == 0)
            {
                Widgets.Label(new Rect(0f, 0f, viewRect.width, 30f), "远行队里没有可参加委派的人员。");
            }
            for (int i = 0; i < draft.candidates.Count; i++)
            {
                DrawPawnRow(draft, new Rect(0f, i * DelegationUIUtility.RowHeight, viewRect.width,
                    DelegationUIUtility.RowHeight), draft.candidates[i], worker);
            }
            Widgets.EndScrollView();

            }   // showCollect

            // ---- ③ 远行队信息（S27 起只放"这一趟的代价"：人数说明与底部汇总；参战/参与名单各归其段）
            bool showCaravan = DelegationUIUtility.DrawSectionHeader(
                new Rect(content.x, y, content.width, DelegationUIUtility.SectionHeaderH),
                DelegationUIUtility.SectionCaravan, true, DelegationUIUtility.SectionId.Caravan);
            y += DelegationUIUtility.SectionHeaderH;
            if (showCaravan)
            {

            // ---- 底部汇总（行高在 BuildSummary 里已经算好，这里只落笔）
            float sy = listBottom;
            for (int i = 0; i < summary.Count; i++)
            {
                SummaryLine line = summary[i];
                Color old = GUI.color;
                if (line.warn)
                {
                    GUI.color = new Color(1f, 0.55f, 0.45f);
                }
                else if (line.dim)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.75f);
                }
                Widgets.Label(new Rect(content.x, sy, content.width, line.height), line.text);
                GUI.color = old;
                sy += line.height;
            }
            }   // showCaravan
        }

        // ================================================================ 汇总区

        private sealed class SummaryLine
        {
            public string text;
            public float height;
            public bool warn;
            public bool dim;
        }

        /// <summary>
        /// 汇总区的行文案 + 行高。**量高与绘制共用这一份**，所以不会出现"算 180 实际画 210"。
        /// 每行高度走 <see cref="DelegationUIUtility.MeasuredHeight" />，窄列里自动换行也不会互相压住。
        /// </summary>
        private static List<SummaryLine> BuildSummary(DelegationDraft draft, DelegationWorker worker, float width)
        {
            List<SummaryLine> lines = new List<SummaryLine>();
            List<Pawn> chosen = draft.ChosenList();

            float perDay = draft.mode == null ? 0f : worker.EstimateUnitsPerDayFor(chosen, draft.mode,
                draft.site.Tile, draft.site);
            float mood = DelegationUtility.DailyMoodOffset(draft.def, draft.mode);
            string moodLine = DelegationUIUtility.MoodLine(mood);
            Add(lines, string.Format("已选 {0} 人 · 结束条件：{1} · {2}",
                chosen.Count, draft.EndConditionLabel(), moodLine), width);

            if (perDay > 0f)
            {
                Add(lines, string.Format("预计 {0:0.#}–{1:0.#} 天{2}完（约 {3:0.#} {4}/天）",
                    draft.dispMinCells / perDay, draft.dispMaxCells / perDay,
                    worker.WorkVerb, perDay, worker.UnitName), width);
            }
            else
            {
                // 默认文案会把"清单还没掷"也归因成"缺人/缺模式"，所以先问 worker 要原因
                Add(lines, worker.EstimateUnavailableReason(draft.site, draft.preview, draft.exactDeposit)
                    ?? "无法估算（模式或人员缺失）", width);
            }

            // 作战姿态的成算（如潜入暴露概率）—— 由 worker 提供，这里不耦合具体玩法
            string approachForecast = worker.ApproachForecast(draft.site, chosen, draft.approach);
            if (!approachForecast.NullOrEmpty())
            {
                Add(lines, approachForecast, width);
            }

            // 疲劳 → 工伤倍率（§19.25）：用"当前已选的人"算，所以勾选人员时这行会变。
            // 原版 `NeedRest` 的疲劳心情在车队里不生效，这行是玩家唯一能看到长工时生理代价的地方。
            // 文案与公式只有一份（DelegationUIUtility.FatigueRiskLineOf），皮肤右栏也调它。
            Add(lines, DelegationUIUtility.FatigueRiskLineOf(chosen), width);

            // 补给品（§19.25）：原版餐食心情 + 委派专属的「野外补给」余味
            string mealLine = DelegationUIUtility.MealLine(draft.caravan);
            if (!mealLine.NullOrEmpty())
            {
                Add(lines, mealLine, width);
            }

            // As-Is / To-Be 负重（只算产物）—— 公式只有一份（`DelegationDraft.TryMassForecast`），
            // 所以它与主控台在途详情、皮肤右栏"概览"上的那个数字永远对得上。
            if (draft.TryMassForecast(out float massNow, out float massMin, out float massMax, out float massCap))
            {
                Add(lines, DelegationUIUtility.MassMain(massNow, massMin, massMax, massCap), width);
                Add(lines, DelegationUIUtility.MassSub(massNow, massMin, massMax, massCap), width,
                    warn: massNow + massMax > massCap);
            }

            return lines;
        }

        private static void Add(List<SummaryLine> lines, string text, float width, bool warn = false,
            bool dim = false)
        {
            if (text.NullOrEmpty())
            {
                return;
            }
            lines.Add(new SummaryLine
            {
                text = text,
                height = DelegationUIUtility.MeasuredHeight(text, width, Text.LineHeight),
                warn = warn,
                dim = dim
            });
        }

        // ================================================================ 子块

        /// <summary>
        /// 「预期获得」列表（S6）：图标 + 名称 + 数量/质量/市价。
        ///
        /// 只画前 <see cref="MaxItemRows" /> 行、其余滚动：纵向预算主要留给"选人列表"，
        /// 一个 7×7 密室里能有十几种物资，全画出来会把选人区挤没。
        /// 清单读不到时 worker 返回 null ⇒ 这里什么都不画，由 PreviewLabel 去解释"未定"。
        /// </summary>
        private static void DrawPreviewItems(DelegationDraft draft, DelegationWorker worker, Listing_Standard top)
        {
            List<DelegationPreviewItem> items;
            try
            {
                items = worker.PreviewItems(draft.site, draft.preview, draft.exactDeposit);
            }
            catch (Exception ex)
            {
                // 预览列表画不出来绝不能让整个界面炸掉（画布抛异常 = 面板全空）
                Log.WarningOnce("[RimDelegation] 绘制「预期获得」列表失败：" + ex.Message, 0x5E0D1);
                return;
            }
            if (items.NullOrEmpty())
            {
                return;
            }

            top.Label(string.Format("预期获得（{0} 项）", items.Count));
            int visible = Mathf.Min(items.Count, MaxItemRows);
            Rect rect = top.GetRect(visible * ItemRowHeight + 4f);
            Rect view = new Rect(0f, 0f, Mathf.Max(60f, rect.width - 18f), items.Count * ItemRowHeight + 4f);
            Widgets.BeginScrollView(rect, ref draft.itemScroll, view);
            for (int i = 0; i < items.Count; i++)
            {
                DrawItemRow(new Rect(0f, i * ItemRowHeight, view.width, ItemRowHeight), items[i]);
            }
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 「预期获得」一行。⚠️ S8 起改成**单行**排版（名称在左、明细右对齐）：
        /// 原来在一行里塞两个 16/15px 的矩形去画 22px 的字 —— `GameFont.Small` 的真实行高是 22，
        /// 矩形比字矮就会裁掉上下截（与主控台那次同一个根因）；何况 32px 行高本就放不下两行 22。
        /// </summary>
        private static void DrawItemRow(Rect row, DelegationPreviewItem item)
        {
            if (item == null)
            {
                return;
            }
            const float Icon = 28f;
            Rect iconRect = new Rect(row.x, row.y + (ItemRowHeight - Icon) * 0.5f, Icon, Icon);
            if (item.pawn != null)
            {
                GUI.DrawTexture(iconRect, PortraitsCache.Get(item.pawn, new Vector2(Icon, Icon), Rot4.South));
                TooltipHandler.TipRegion(iconRect, item.pawn.LabelShortCap);
            }
            else if (item.thingDef != null)
            {
                Widgets.ThingIcon(iconRect, item.thingDef, null, null, 1f, null, null, 1f);
                TooltipHandler.TipRegion(iconRect, item.thingDef.LabelCap);
            }

            float textX = iconRect.xMax + 6f;
            float textW = Mathf.Max(40f, row.xMax - textX);
            float lineH = Text.LineHeight;
            float textY = row.y + (row.height - lineH) * 0.5f;

            string detail = item.detail ?? "";
            float detailW = detail.NullOrEmpty() ? 0f : Text.CalcSize(detail).x + 8f;
            float labelW = Mathf.Max(40f, textW - detailW);

            Color old = GUI.color;
            if (item.uncertain)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.65f);
            }
            Widgets.Label(new Rect(textX, textY, labelW, lineH), item.label ?? "");
            GUI.color = old;

            if (!detail.NullOrEmpty())
            {
                Color oldDetail = GUI.color;
                TextAnchor oldAnchor = Text.Anchor;
                GUI.color = new Color(1f, 1f, 1f, 0.7f);
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(textX + labelW, textY, Mathf.Max(40f, textW - labelW), lineH), detail);
                Text.Anchor = oldAnchor;
                GUI.color = oldDetail;
            }
            TooltipHandler.TipRegion(row, (item.label ?? "") + "\n" + (item.detail ?? ""));
        }

        /// <summary>
        /// 「作战任务」段的内容（S24）：守军 → 编队 → 我方可战/被排除 → 成算 → 动作。
        ///
        /// **与主列同一份数据、同一份文案**：数据走 <see cref="DelegationThreatSummary" />（带缓存，
        /// 见那里的注释），文字走它的 <c>Lines()</c>，段名走 <see cref="DelegationUIUtility.SectionCombat" />。
        /// 所以草稿页与主控台不会出现"两处说法不一样"。
        ///
        /// 有姿态轴时，"模式 / 姿态"那一行也落在这里（姿态是作战概念）；
        /// 没有姿态轴的委派，模式按钮留在「收集任务」里。
        /// </summary>
        private static float DrawCombatBlock(DelegationDraft draft, Rect rect)
        {
            float y = rect.y;
            DelegationThreatSummary s = draft.threatSummary;
            s.Ensure(draft.caravan, draft.site, draft.ChosenList(), draft.approach, false);
            // S26：**待下达这一屏不揭露守军情报**（用户：「待下达状态的时候，敌方编队和威胁点数就揭露了
            // -- 不要揭露」）。情报要靠"派人过去侦察"换来，这正是这一屏的取舍本身。
            s.revealed = false;

            List<string> lines = s.Lines();
            for (int i = 0; i < lines.Count; i++)
            {
                float h = DelegationUIUtility.MeasuredHeight(lines[i], rect.width, Text.LineHeight);
                Widgets.Label(new Rect(rect.x, y, rect.width, h), lines[i]);
                y += h;
            }

            if (!draft.def.approaches.NullOrEmpty())
            {
                // 只有姿态归作战任务；"模式"是作业作息（干多久），永远留在收集任务里
                Rect row = new Rect(rect.x, y, rect.width, 30f);
                if (Widgets.ButtonText(row, "姿态：" + draft.ApproachLine()))
                {
                    draft.OpenApproachMenu();
                }
                y += 34f;
            }

            if (s.revealed && s.canAssess)
            {
                Rect row = new Rect(rect.x, y, rect.width, 30f);
                float half = (row.width - 8f) * 0.5f;
                if (Widgets.ButtonText(new Rect(row.x, row.y, half, 30f), "重新推算"))
                {
                    s.Reroll(draft.caravan, draft.site, draft.ChosenList(), draft.approach);
                }
                if (Widgets.ButtonText(new Rect(row.x + half + 8f, row.y, half, 30f), "威胁评估"))
                {
                    // 把当前姿态的"守军先手"折扣一起传进去 —— 否则这里看到的成功率
                    // 会比委派实际结算时高一轮火力，违背"预告即契约"。
                    ThreatAssessmentEntry.Open(draft.caravan, draft.site,
                        draft.Worker?.ApproachFirstStrikePenalty(draft.approach) ?? 0f);
                }
                y += 34f;
            }
            return y + 4f;
        }

        // ---------------------------------------------------------------- 结束条件（下拉框）

        /// <summary>
        /// 结束条件行：标签 + 下拉 + 数值微调。宽度按可用空间分配 ——
        /// 主控台右栏最窄 ~370px，而"标签 66 + 下拉 210 + 微调 200"是照 640 宽排的。
        /// </summary>
        private static float DrawEndConditionRow(DelegationDraft draft, Rect row)
        {
            const float LabelW = 66f;
            Widgets.Label(new Rect(row.x, row.y, LabelW, row.height), "结束条件：");
            float avail = Mathf.Max(80f, row.width - LabelW - 10f);
            float dropW = Mathf.Clamp(avail * 0.46f, 120f, 210f);
            float stepW = Mathf.Max(80f, avail - dropW - 10f);

            Rect dropdown = new Rect(row.x + LabelW, row.y, dropW, row.height);
            if (Widgets.ButtonText(dropdown, draft.EndConditionLabel() + "   ▼"))
            {
                draft.OpenEndConditionMenu();
            }

            Rect step = new Rect(dropdown.xMax + 10f, row.y, stepW, row.height);
            if (draft.endCondition == DelegationEndCondition.Days)
            {
                IntStepper(step, ref draft.daysLimit, 1, Mathf.Max(1, draft.def.maxDaysLimit), "天");
            }
            else if (draft.endCondition == DelegationEndCondition.Quota)
            {
                IntStepper(step, ref draft.quotaUnits, 1, draft.QuotaCap(), draft.Worker.OutputUnitName);
            }
            else
            {
                // S21：用户书面语清单里对"取尽"这一档写着「！删除这个描述！」——
                // 上面那行「结束条件：…为止」已经说清了，"无需设定：一直干到…"纯属重复，
                // 所以这里什么都不画（步进器位留空即可）。
                // 另外把 `Rect step` 的构造留给真正需要它的两档，避免"画了又没人看"的错觉。
            }
            return row.yMax;
        }

        /// <summary>[-] 数值 [+]（窄的时候自动省掉单位、再窄就把数字框压小）。</summary>
        private static void IntStepper(Rect rect, ref int value, int min, int max, string unit)
        {
            float btnW = 26f;
            float numW = 62f;
            bool showUnit = !unit.NullOrEmpty() && rect.width >= 162f;
            if (rect.width < 118f)
            {
                btnW = 22f;
                numW = Mathf.Max(28f, rect.width - btnW * 2f - 8f);
                showUnit = false;
            }
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, btnW, rect.height), "-"))
            {
                value--;
            }
            if (Widgets.ButtonText(new Rect(rect.x + btnW + numW + 4f, rect.y, btnW, rect.height), "+"))
            {
                value++;
            }
            value = Mathf.Clamp(value, min, Mathf.Max(min, max));
            Widgets.Label(new Rect(rect.x + btnW + 4f, rect.y, numW, rect.height), value.ToString());
            if (showUnit)
            {
                Widgets.Label(new Rect(rect.x + btnW * 2f + numW + 6f, rect.y, 34f, rect.height), unit);
            }
        }

        // ---------------------------------------------------------------- 工具条

        private static void DrawToolbarRow(DelegationDraft draft, Rect row)
        {
            float sortW = Mathf.Clamp(row.width * 0.42f, 96f, 200f);
            if (Widgets.ButtonText(new Rect(row.x, row.y, sortW, row.height),
                "排序：" + DelegationUIUtility.SortLabel(draft.sortMode)))
            {
                draft.sortMode = DelegationUIUtility.NextSort(draft.sortMode);
                draft.SortCandidates();
            }
            float bx = row.x + sortW + 8f;
            if (Widgets.ButtonText(new Rect(bx, row.y, 70f, row.height), "全选"))
            {
                for (int i = 0; i < draft.candidates.Count; i++)
                {
                    draft.selected.Add(draft.candidates[i]);
                }
            }
            bx += 76f;
            if (Widgets.ButtonText(new Rect(bx, row.y, 80f, row.height), "全不选"))
            {
                draft.selected.Clear();
            }
            bx += 86f;
            // 剩下的宽度才够写"已勾选 x/y"（窄列里宁可省掉它，也不叠在按钮上）
            if (row.xMax - bx >= 90f)
            {
                Widgets.Label(new Rect(bx, row.y, row.xMax - bx, row.height),
                    string.Format("已勾选 {0}/{1}", draft.selected.Count, draft.candidates.Count));
            }
        }

        // ---------------------------------------------------------------- 人员行

        private static void DrawPawnRow(DelegationDraft draft, Rect row, Pawn p, DelegationWorker worker)
        {
            bool sel = draft.selected.Contains(p);

            if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            Widgets.Checkbox(row.x + 4f, row.y + (row.height - 24f) * 0.5f, ref sel, 24f, false, false, null, null);

            Rect body = new Rect(row.x + 34f, row.y, row.width - 34f, row.height);
            DelegationUIUtility.DrawPawnLine(body, p, DelegationUIUtility.PawnLine(p, worker, draft.def));

            if (Widgets.ButtonInvisible(DelegationUIUtility.LabelRectFor(body)))
            {
                sel = !sel;
            }

            if (sel != draft.selected.Contains(p))
            {
                if (sel)
                {
                    draft.selected.Add(p);
                }
                else
                {
                    draft.selected.Remove(p);
                }
            }
        }
    }
}
