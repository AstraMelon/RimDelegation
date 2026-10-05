using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>人员列表的排序方式。</summary>
    public enum PawnSortMode
    {
        SkillDesc,
        Name,
        MoodAsc
    }

    /// <summary>
    /// 「现场物资 / 预期获得」列表的排序方式（S9：用户要求这两个列表都支持排序）。
    ///
    /// 市价放第一档是因为它是这三个里唯一"跨物资可比"的量：一堆钢铁和一件人格核心
    /// 没法比件数、没法比 kg，只能比"先搬哪个划算"。
    /// </summary>
    public enum ItemSortMode
    {
        ValueDesc,
        CountDesc,
        Name
    }

    /// <summary>对话框与远行队页签共用的绘制片段，保证两处长相与行为一致。</summary>
    public static class DelegationUIUtility
    {
        public const float PortraitSize = 36f;
        public const float RowHeight = 44f;
        public const float BadgeSize = 16f;
        public const float BadgeGap = 3f;

        /// <summary>
        /// 页签专用的**紧凑行**（S8 草图定的 30px 头像 / 38px 行高）。
        ///
        /// 为什么不直接把 <see cref="PortraitSize" /> 改小：那个常量同时被委派对话框用（对话框空间
        /// 宽裕、44px 行更好点），改它会连带改对话框的长相。所以只加"另一种尺寸"，
        /// 行画法仍然只有 <see cref="DrawPawnLine" /> 一处 —— 两处长漂移才是真问题。
        /// </summary>
        public const float CompactPortraitSize = 30f;

        public const float CompactRowHeight = 38f;

        /// <summary>
        /// 安全地取 worker 的「预期获得」清单。
        ///
        /// 为什么必须兜异常：这四个列表都画在**每一帧**的 UI 路径上，
        /// worker 里一次意外的空引用 = 整个对话框/页签空白（本项目的血泪教训）。
        /// </summary>
        public static List<DelegationPreviewItem> SafePreviewItems(DelegationWorker worker, Site site,
            DelegationPreview preview, DelegationDeposit deposit)
        {
            try
            {
                return worker?.PreviewItems(site, preview, deposit);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 取「预期获得」列表失败：" + ex.Message, 0x5E0D2);
                return null;
            }
        }

        /// <summary>安全地取 worker 的「现场还剩什么」清单（同上，UI 路径）。</summary>
        public static List<DelegationPreviewItem> SafeProgressItems(Delegation d, Site site)
        {
            try
            {
                return d?.Worker?.ProgressItems(d, site);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 取「现场物资」列表失败：" + ex.Message, 0x5E0D3);
                return null;
            }
        }

        // （S14）原 `FlowLines()` 已删除：流程文案改由 `StageRows`（统一阶段序列）产出。

        // ================================================================ 「流程」块（S14 阶段模型）

        private static readonly Dictionary<string, float> typewriterStart = new Dictionary<string, float>();

        /// <summary>
        /// 打字机效果（S15 第三期）：一段**新文本**逐字展开，避免整句突然蹦出来。
        ///
        /// 三个要点：
        ///   · 时钟用 `Time.realtimeSinceStartup`（不是游戏 tick）—— 报告窗口是在**暂停**状态打开的，
        ///     用游戏 tick 的话文字会永远停在第一个字；
        ///   · key 就是文本本身 ⇒ 文本一变自动重新开始，调用方不必维护状态；
        ///   · **高度必须按完整文本算**（`StageRowsHeight` 已经是这么做的），
        ///     否则每多一个字行高就变一次，整个流程块会上下跳。
        /// </summary>
        public static string Typewriter(string full, float charsPerSecond = 30f)
        {
            if (full.NullOrEmpty())
            {
                return full;
            }
            float start;
            if (!typewriterStart.TryGetValue(full, out start))
            {
                start = Time.realtimeSinceStartup;
                typewriterStart[full] = start;
            }
            float t = Time.realtimeSinceStartup - start;
            int shown = Mathf.Clamp(Mathf.CeilToInt(t * charsPerSecond), 0, full.Length);
            return shown >= full.Length ? full : full.Substring(0, shown);
        }

        // ================================================================ 主信息的三段（S24）

        /// <summary>
        /// 主信息栏的三个段头（用户 2026-09-27 原话：「委派UI 主信息内部拆分一下，拆分为作战任务（最上面）
        /// - 收集任务（中间）（开采/搜刮）- 远行队信息（最下面）」）。
        ///
        /// **只有这一份**：原版主控台、RadiusUI 皮肤主控台、原版草稿页、皮肤草稿页四处都读它
        /// （准则⑨：措辞只有一份来源；画法各自）。
        ///
        /// ⚠️ 三段是「按信息分类」不是「给委派分类」：采矿（有敌情时）与物资点都可能**同时**有作战与收集内容。
        /// 所以三段都**恒常出现** —— 「作战任务」在没有守军时写一行状态，不整段消失（S17 准则 + 用户 S24 拍板）。
        /// </summary>
        public const string SectionCombat = "作战任务";

        public const string SectionCollect = "收集任务";

        public const string SectionCaravan = "远行队信息";

        /// <summary>
        /// 「流程」这一块的名字（S28 收进共用件）。原版画成一行（`流程：`）、皮肤画成卡片标题（`流程`）、
        /// 流程栏的列头也用它 —— 三处只此一份，免得哪天改个名又漏一处（准则⑨）。
        /// </summary>
        public const string FlowHeader = "流程";

        /// <summary>三段段头的高度（S27：随字号提到中号一起加大 —— 用户 S25：「Title 更加凸显」）。</summary>
        public const float SectionHeaderH = 30f;

        /// <summary>
        /// S26：流程里的**随机描述（旁白）**是否显示。用户口径：「暂时关闭一下流程的随机描述，
        /// 有些不符合逻辑」⇒ 开关放 Mod 设置、**默认关**；两端的旁白都只从这里取判据
        /// （`DelegationStageList` 的段旁白 + `StageRows` 的休息旁白），所以不会一半关一半不关。
        /// </summary>
        public static bool AmbientEnabled => RimDelegationMod.Settings?.flowAmbientEnabled ?? true;

        /// <summary>
        /// S26：执行者的**技能激情火苗**（用户：「双火/火/无火 是否可以添加图标」）。
        ///
        /// 用的是**原版自己的**贴图：`SkillUI.PassionMinorIcon` / `PassionMajorIcon`
        /// （反编译确认是公开静态字段，内部从 `UI/Icons/PassionMinor`、`UI/Icons/PassionMajor` 载入）。
        ///
        /// 为什么要收在一个函数里：S22 当初正是因为"两端各写一遍贴图会分叉"才改用文字；
        /// 这次用户点名要图标，于是把"取哪张贴图"变成**唯一来源**，两端只负责画。
        /// </summary>
        public static Texture2D PassionIconFor(DelegationStage stage)
        {
            if (stage?.executor?.skills == null || stage.executorSkill == null)
            {
                return null;
            }
            SkillRecord rec = stage.executor.skills.GetSkill(stage.executorSkill);
            if (rec == null)
            {
                return null;
            }
            switch (rec.passion)
            {
                case Passion.Minor: return SkillUI.PassionMinorIcon;
                case Passion.Major: return SkillUI.PassionMajorIcon;
                default: return null;
            }
        }

        /// <summary>
        /// S25：**守军情报揭露了吗** —— 即流程里那个带 `revealsThreat` 的段走完了没有。
        ///
        /// 用户口径：「在侦察完成前，守军不会向玩家揭露」「完成侦察任务后，向玩家揭露敌人编队」。
        /// 一个流程里没有任何段带它（老存档、无侦察段的委派、无威胁那一岔）⇒ 一律视为已揭露。
        /// 只看**冻结后的段表**，所以有敌情/无威胁两岔各自算各自的。
        /// </summary>
        public static bool ThreatRevealed(Delegation d)
        {
            if (d?.flow == null || d.def == null)
            {
                return true;
            }
            DelegationFlow flow = DelegationFlow.For(d.def);
            return RevealedIn(flow.PreludeFor(d.flow), d.flow, true)
                && RevealedIn(flow.SuffixFor(d.flow), d.flow, false);
        }

        private static bool RevealedIn(List<DelegationPhaseDef> phases, DelegationFlowState st, bool prelude)
        {
            if (phases.NullOrEmpty())
            {
                return true;
            }
            for (int i = 0; i < phases.Count; i++)
            {
                DelegationPhaseDef phase = phases[i];
                if (phase == null || !phase.revealsThreat)
                {
                    continue;
                }
                int index = prelude ? st.preludeIndex : st.suffixIndex;
                return index > i;   // 这一段走完了才算揭露
            }
            return true;            // 这一段表里没有任何"揭露点" ⇒ 无需门控
        }

        /// <summary>三段的身份（折叠状态按它取；原版与皮肤**共用**同一份设置）。</summary>
        public enum SectionId
        {
            Combat = 0,
            Collect = 1,
            Caravan = 2,
        }

        /// <summary>S25：这一段现在是不是折叠的（状态存本地 Mod 配置，不进存档）。</summary>
        public static bool SectionCollapsed(SectionId id)
        {
            RimDelegationSettings cfg = RimDelegationMod.Settings;
            if (cfg == null)
            {
                return false;
            }
            switch (id)
            {
                case SectionId.Combat: return cfg.collapsedCombatSection;
                case SectionId.Collect: return cfg.collapsedCollectSection;
                default: return cfg.collapsedCaravanSection;
            }
        }

        /// <summary>S25：切换折叠（皮肤用它 —— 那边段头是自己画的，状态却必须同一份）。</summary>
        public static void ToggleSection(SectionId id)
        {
            RimDelegationSettings cfg = RimDelegationMod.Settings;
            if (cfg == null)
            {
                return;
            }
            switch (id)
            {
                case SectionId.Combat: cfg.collapsedCombatSection = !cfg.collapsedCombatSection; break;
                case SectionId.Collect: cfg.collapsedCollectSection = !cfg.collapsedCollectSection; break;
                default: cfg.collapsedCaravanSection = !cfg.collapsedCaravanSection; break;
            }
        }

        /// <summary>
        /// 主信息三段段头的**原版画法**（S24 立，S25 加折叠）。返回 true = 内容应当绘制（展开态）。
        ///
        /// 用户 S25：「作战任务，收集任务，远行队的 Title 更加凸显。需要可以折叠」
        ///   · 「更凸显」= 一条底色带 + 暖色文字 + 下划线，**不动字号** ——
        ///     字号一动，四处调用点的高度预算全要跟着改，而它们已经按 <see cref="SectionHeaderH" /> 排好了；
        ///   · 「可折叠」= 右端一个 `－/＋` 按钮，状态存 `RimDelegationSettings`（原版与皮肤共用，
        ///     与 S8 的 `stashItemsExpanded` 同一规矩）。
        /// ⚠️ 折叠开关**画在段头自己身上**，而段头永远在（不像 S22 那次把开关画进了被折叠的列里）。
        /// </summary>
        public static bool DrawSectionHeader(Rect rect, string label, bool draw, SectionId id)
        {
            bool collapsed = SectionCollapsed(id);
            if (!draw)
            {
                return !collapsed;
            }

            const float BtnW = 22f;
            Color old = GUI.color;
            GameFont oldFont = Text.Font;
            // S27：用户要求「Title 更加凸显」⇒ 段头用**中等字号**（比正文大一档）。
            // 高度由调用方按 SectionHeaderH 预留，所以字号与 SectionHeaderH 必须一起改。
            Text.Font = GameFont.Medium;

            GUI.color = new Color(1f, 1f, 1f, 0.06f);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, rect.height - 3f), GUI.color);
            GUI.color = new Color(0.95f, 0.89f, 0.66f);
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 3f, Mathf.Max(40f, rect.width - BtnW - 10f), rect.height),
                label);
            GUI.color = new Color(1f, 1f, 1f, 0.22f);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 2f, rect.width);

            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(rect.xMax - BtnW, rect.y + 2f, BtnW, rect.height - 6f),
                    collapsed ? "＋" : "－"))
            {
                ToggleSection(id);
                collapsed = !collapsed;
            }
            GUI.color = old;
            Text.Font = oldFont;
            return !collapsed;
        }

        /// <summary>
        /// 段头的**旧签名**（不折叠）—— 留给"整块不该折叠"的调用点（例如面板里的分行标题）。
        /// </summary>
        public static void DrawSectionHeader(Rect rect, string label, bool draw)
        {
            if (!draw || label.NullOrEmpty())
            {
                return;
            }
            Color old = GUI.color;
            GameFont oldFont = Text.Font;
            Text.Font = GameFont.Small;
            GUI.color = new Color(0.93f, 0.87f, 0.64f);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, rect.height), label);
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 2f, rect.width);
            GUI.color = old;
            Text.Font = oldFont;
        }

        /// <summary>
        /// 「本趟流程里有没有交战段、打过了没有」那一行（S23 的交战段 → S24 的作战任务段要用）。
        /// 返回 null = 这条委派的流程里根本没有交战段（那就不显示这一行）。
        /// </summary>
        public static string CombatPhaseLine(Delegation d)
        {
            if (d?.flow == null || d.def == null)
            {
                return null;
            }
            DelegationFlow flow = DelegationFlow.For(d.def);
            string label = null;
            bool done = false;
            ScanCombatPhase(flow.PreludeFor(d.flow), d.flow.preludeIndex, ref label, ref done);
            ScanCombatPhase(flow.SuffixFor(d.flow), d.flow.suffixIndex, ref label, ref done);
            if (label == null)
            {
                return null;
            }
            if (!done)
            {
                return string.Format("本趟流程包含「{0}」段（尚未走到）。", label);
            }
            return d.flowCombatResult.NullOrEmpty()
                ? string.Format("本趟已交战（「{0}」段已走完，没有留下结果摘要）。", label)
                : string.Format("本趟已交战：{0}", d.flowCombatResult);
        }

        /// <summary>流程里哪一段带 `ResolveCombat` 钩子（判据只看效果原语的类型，不认段名）。</summary>
        private static void ScanCombatPhase(List<DelegationPhaseDef> phases, int index, ref string label, ref bool done)
        {
            if (phases == null)
            {
                return;
            }
            for (int i = 0; i < phases.Count; i++)
            {
                DelegationPhaseDef phase = phases[i];
                if (phase?.onEnter == null)
                {
                    continue;
                }
                for (int j = 0; j < phase.onEnter.Count; j++)
                {
                    if (phase.onEnter[j] is DelegationEffectDef_ResolveCombat)
                    {
                        label = phase.PendingLabel;
                        done = index > i;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 「流程」块的行（S14）：**只给已完成 + 当前**（未开始的阶段完全隐藏），当前段展开成
        /// 标题 / 旁白 / 条三行。判据与排版只有这一份 —— 原版走 <see cref="DrawStageRows" />，
        /// RadiusUI 皮肤走自家的 `RadiusFont` + `UIKit.Flat.Bar`（双端准则：文字一份、画法各自）。
        /// </summary>
        public static List<DelegationStageRow> StageRows(Delegation d, Site site, int maxDoneRows, string header)
        {
            List<DelegationStage> stages = DelegationStageList.Build(d, site);

            // S16：休息时段**置顶两行**（"休息中（距开工 X 小时）" + 休息内容：睡觉 / 睡前聊天）。
            // 判据与"距离开工还有多久"那一行同源（都走 IsWorkTime，含紧急加班；停摆/暂停期间不算休息）。
            string restTitle = null;
            string restAmbient = null;
            bool awaiting = d != null && d.AwaitingOrder;
            if (awaiting)
            {
                // S25：等玩家下作战指令 ⇒ 置顶那行改写（同一条判据：这时候 IsWorkTime 也是假）
                restTitle = "待命（等待你的作战指令）";
            }
            else if (d != null && site != null && !d.paused && !d.IsStalled(GenTicks.TicksAbs)
                && !d.IsWorkTime(site, GenTicks.TicksAbs))
            {
                float hours = d.mode?.HoursUntilStart(site.Tile, GenTicks.TicksAbs) ?? 0f;
                restTitle = string.Format("休息中（距开工 {0:0.#} 小时）", hours);
                restAmbient = AmbientEnabled
                    ? DelegationAmbient.Current(d, site, DelegationAmbient.RestKey, d.def?.RestAmbientPool, 0f,
                        DelegationAmbient.PickPawn(d, DelegationAmbient.RestKey))
                    : null;
                // 休息池里也允许写 `{0}`（说话人）—— 与阶段旁白同一个约定
                if (!restAmbient.NullOrEmpty())
                {
                    string speaker = DelegationAmbient.SpeakerName(d, "rest", "RandomPawn");
                    if (!speaker.NullOrEmpty())
                    {
                        restAmbient = restAmbient.Replace("{0}", speaker);
                    }
                }
            }

            if (stages.NullOrEmpty() && restTitle.NullOrEmpty())
            {
                return null;
            }
            // S15：把"最近一条事件"一并交给排版 —— 事件不再发信后，流程块这一行是进行中的唯一出口
            List<DelegationStageRow> rows =
                DelegationStageList.Rows(stages, maxDoneRows, header, d.LastEventEntry(), restTitle, restAmbient);

            // S22：小队此刻在哪儿（外围 / 藏匿点内部 / 矿点…）—— 插在**最前面**，也就是「流程」标题之上。
            // 数据来自阶段 Def 的 `squadPosition`（见 DelegationStageList.SquadPosition）。
            string position = DelegationStageList.SquadPosition(d);
            if (rows != null && !position.NullOrEmpty())
            {
                rows.Insert(0, new DelegationStageRow
                {
                    kind = DelegationStageRowKind.Position,
                    text = "位置：" + position,
                });
            }
            return rows;
        }

        /// <summary>
        /// 阶段块的**原版画法**（原版页签 + 原版主控台详情共用），返回画完之后的 y。
        ///
        /// 字形约定（S14 查过字形表之后定的，别自己发挥）：
        ///   已完成 = `·`（U+00B7，字符集里有）+ 压暗 62%；当前段 = `●`（U+25CF，字符集里有）。
        ///   **不要用 `✓`/`✅`/`🔵`**：`✓` 只在原版的 Debug 工具里出现过，彩色 emoji 直接没有字形。
        /// 条用**同页已有的** `Widgets.FillableBar`（8px），百分比/ETA 写在条右边。
        /// </summary>
        public static float DrawStageRows(Rect r, float y, float width, List<DelegationStageRow> rows)
        {
            if (rows.NullOrEmpty())
            {
                return y;
            }
            Color old = GUI.color;
            Color dim = new Color(1f, 1f, 1f, 0.62f);
            for (int i = 0; i < rows.Count; i++)
            {
                DelegationStageRow row = rows[i];
                switch (row.kind)
                {
                    case DelegationStageRowKind.Header:
                        GUI.color = old;
                        y = StageLabel(r.x, y, width, row.text);
                        break;

                    case DelegationStageRowKind.Position:
                        // S22：小队位置（画在「流程」标题之上）。用淡青，与休息行同色系但更亮一档 ——
                        // 它既不是"发生的事"（旁白）也不是"状态异常"（停摆/事件）。
                        GUI.color = new Color(0.80f, 0.90f, 1f);
                        y = StageLabel(r.x, y, width, row.text);
                        break;

                    case DelegationStageRowKind.GroupTitle:
                        // S28：流程分组小标题（作战任务 / 收集任务）。用现成的**不可折叠**段头画法
                        // （暖色字 + 下划线，见 DrawSectionHeader(rect,label,draw)）——
                        // 与主列那三个段头的观感同源，但**不带**折叠按钮（那三个的折叠状态是另一码事）。
                        GUI.color = old;
                        DrawSectionHeader(new Rect(r.x, y, width, Text.LineHeight + 2f), row.text, true);
                        y += Text.LineHeight + 2f;
                        break;

                    case DelegationStageRowKind.Done:
                    case DelegationStageRowKind.DoneSummary:
                        GUI.color = dim;
                        y = StageLabel(r.x, y, width, "· " + row.text);
                        break;

                    case DelegationStageRowKind.ActiveTitle:
                        GUI.color = old;
                        {
                            // S28：执行者的激情火苗改成插在**技能名之后**（用户：「双火/火要显示在技能名后，
                            // 例如 智识 双火」）。插入位由 `DelegationStageList.ExecutorHead` 给出（`row.iconAt`），
                            // 这里只负责切两段 —— 前缀多一个 `● ` ⇒ 下标跟着挪 2。
                            Texture2D flame = HasInlineIcon(row) ? PassionIconFor(row.stage) : null;
                            if (flame != null)
                            {
                                y = DrawInlineIconLine(r.x, y, width, "● " + row.text, row.iconAt + 2, flame);
                            }
                            else
                            {
                                y = StageLabel(r.x, y, width, "● " + row.text);
                            }
                        }
                        break;

                    case DelegationStageRowKind.ActiveAmbient:
                        GUI.color = dim;
                        y = StageLabel(r.x + 12f, y, Mathf.Max(40f, width - 12f),
                            "“" + Typewriter(row.text) + "”");
                        break;

                    case DelegationStageRowKind.RestTitle:
                        // S16：休息置顶那一行（偏青 —— 与"橙色事件""红色停摆"区分开）
                        GUI.color = new Color(0.72f, 0.92f, 1f);
                        y = StageLabel(r.x, y, width, "· " + row.text);
                        break;

                    case DelegationStageRowKind.RestAmbient:
                        GUI.color = dim;
                        y = StageLabel(r.x + 12f, y, Mathf.Max(40f, width - 12f),
                            "“" + Typewriter(row.text) + "”");
                        break;

                    case DelegationStageRowKind.Event:
                        // S15 事件留痕一行：橙色 + `!` 前缀 + 打字机。
                        // （不用 ⚠ / emoji：已核过字形表，那些字符在游戏字体里没有字形）
                        GUI.color = new Color(1f, 0.78f, 0.42f);
                        y = StageLabel(r.x, y, width, "! " + Typewriter(row.text));
                        break;

                    case DelegationStageRowKind.ActiveBar:
                        {
                            GUI.color = old;
                            float lh = Text.LineHeight;
                            float etaW = row.text.NullOrEmpty() ? 0f : Text.CalcSize(row.text).x + 8f;
                            float barW = Mathf.Max(60f, width - 12f - etaW);
                            Rect bar = new Rect(r.x + 12f, y + (lh - 8f) * 0.5f, barW, 8f);
                            // S15 第三期：停摆时条**染红**（进度本来就冻着，颜色是"冻"的视觉语言）。
                            // 斜纹留待以后（要自绘平铺纹理，见 Doc/随机事件UI-方案评估.md §12 第三期）。
                            bool stalledBar = row.stage != null && row.stage.stalled;
                            Color barOld = GUI.color;
                            if (stalledBar)
                            {
                                GUI.color = new Color(1f, 0.5f, 0.45f);
                            }
                            Widgets.FillableBar(bar, Mathf.Clamp01(row.frac));
                            GUI.color = barOld;
                            if (!row.text.NullOrEmpty())
                            {
                                TextAnchor anchor = Text.Anchor;
                                Text.Anchor = TextAnchor.MiddleRight;
                                Widgets.Label(new Rect(r.x + 12f + barW, y, etaW, lh), row.text);
                                Text.Anchor = anchor;
                            }
                            y += lh;
                        }
                        break;
                }
            }
            GUI.color = old;
            return y;
        }

        /// <summary>阶段块的高度（纯函数，与 <see cref="DrawStageRows" /> 逐行对应）。</summary>
        public static float StageRowsHeight(List<DelegationStageRow> rows, float width)
        {
            if (rows.NullOrEmpty())
            {
                return 0f;
            }
            float lh = Text.LineHeight;
            float h = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                DelegationStageRow row = rows[i];
                switch (row.kind)
                {
                    case DelegationStageRowKind.ActiveBar:
                        h += lh;
                        break;
                    case DelegationStageRowKind.GroupTitle:
                        // S28：B 组标题 = 一行 + 下划线那 2px（与 DrawStageRows 那一格同口径）
                        h += lh + 2f;
                        break;
                    case DelegationStageRowKind.ActiveTitle:
                        // S28：当前段标题里可能插着火苗 ⇒ 高度必须用**同一个**测量式
                        h += HasInlineIcon(row)
                            ? InlineIconHeight("● " + row.text, row.iconAt + 2, width)
                            : MeasuredHeight("● " + row.text, width, lh);
                        break;
                    case DelegationStageRowKind.ActiveAmbient:
                        h += MeasuredHeight("“" + row.text + "”", Mathf.Max(40f, width - 12f), lh);
                        break;
                    default:
                        h += MeasuredHeight(row.text, width, lh);
                        break;
                }
            }
            return h;
        }

        // ================================================================ S28：行内小图标 + 流程栏底框

        /// <summary>
        /// S28：流程块底框的**内边距**（用户：「底框直接要有空隙」）。
        /// 8px 与右栏「概览」卡的内边距同档 —— 皮肤那边用的是同一张卡，所以这个数只服务原版画法。
        /// </summary>
        public const float FlowBoxPad = 8f;

        /// <summary>
        /// S28：流程块的**底框**（原版画法）。⚠️ 必须先量高再铺底：`StageRowsHeight(rows, w - 2*Pad)`
        /// 的返回值 + 上下各 <see cref="FlowBoxPad" /> 才是这张框的高度，内容再画在框内缩进 8px 处。
        /// 颜色沿用三段段头那条底带的口径（白 6% + 18% 描边），所以摆在主列末尾不突兀。
        /// </summary>
        public static void DrawFlowBox(Rect rect)
        {
            Widgets.DrawBoxSolidWithOutline(rect, new Color(1f, 1f, 1f, 0.06f), new Color(1f, 1f, 1f, 0.18f), 1);
        }

        /// <summary>这一行要不要画"插在中间"的火苗（无火 / 没配技能 ⇒ 不画）。</summary>
        public static bool HasInlineIcon(DelegationStageRow row)
        {
            return row != null && row.iconAt >= 0 && row.iconAt <= (row.text?.Length ?? 0)
                && PassionIconFor(row.stage) != null;
        }

        /// <summary>火苗的绘制尺寸（12px 图形 + 左右各 1px 空隙 = 14px 占位）。</summary>
        public const float InlineIconSlot = 14f;

        /// <summary>
        /// S28：带"行内火苗"的一行的高度（纯函数）。
        /// 火苗之后那半句的可用宽度会缩掉（前缀宽 + 火苗槽位），所以它是**先量前缀、再量后半**。
        /// </summary>
        private static float InlineIconHeight(string text, int iconAt, float width)
        {
            float lh = Text.LineHeight;
            if (text.NullOrEmpty())
            {
                return lh;
            }
            if (iconAt < 0 || iconAt > text.Length)
            {
                return MeasuredHeight(text, width, lh);
            }
            string head = text.Substring(0, iconAt);
            string tail = text.Substring(iconAt);
            float headW = Text.CalcSize(head).x;
            if (headW + InlineIconSlot + 2f >= width)
            {
                // 放不下（栏太窄）⇒ 退回旧画法：火苗在最前、整行走满宽
                return Mathf.Max(lh, Text.CalcHeight(text, width));
            }
            return Mathf.Max(lh, Text.CalcHeight(tail, Mathf.Max(40f, width - headW - InlineIconSlot - 2f)));
        }

        /// <summary>
        /// S28：画"前缀 + 火苗 + 后半句"的一行（**原版画法** —— `Widgets.Label` + `GUI.DrawTexture`）。
        ///
        /// 为什么按"切两段"而不是"整行文字里塞个占位符"：`Widgets.Label` 只认字符串，
        /// 塞什么字符都会变成豆腐块；切开画则换行天然正确（后半句的第一行就接在火苗右边）。
        /// 栏太窄放不下时退回"火苗在最前"的旧画法 —— 宁可位置旧，也不要把字切掉。
        /// 返回画完之后的 y。
        /// </summary>
        public static float DrawInlineIconLine(float x, float y, float width, string text, int iconAt, Texture2D icon)
        {
            if (icon == null || text.NullOrEmpty() || iconAt < 0 || iconAt > text.Length)
            {
                return StageLabel(x, y, width, text);
            }
            float lh = Text.LineHeight;
            string head = text.Substring(0, iconAt);
            string tail = text.Substring(iconAt);
            float headW = Text.CalcSize(head).x;
            Color oldColor = GUI.color;
            if (headW + InlineIconSlot + 2f >= width)
            {
                GUI.DrawTexture(new Rect(x, y + (lh - 12f) * 0.5f, 12f, 12f), icon, ScaleMode.ScaleToFit);
                return StageLabel(x + InlineIconSlot + 1f, y, Mathf.Max(40f, width - InlineIconSlot - 1f), text);
            }
            float tailW = Mathf.Max(40f, width - headW - InlineIconSlot - 2f);
            float h = Mathf.Max(lh, Text.CalcHeight(tail, tailW));
            Widgets.Label(new Rect(x, y, headW + 2f, lh), head);
            GUI.color = Color.white;   // 火苗是彩色贴图，别被调用方的压暗/染红带跑
            GUI.DrawTexture(new Rect(x + headW + 1f, y + (lh - 12f) * 0.5f, 12f, 12f), icon, ScaleMode.ScaleToFit);
            GUI.color = oldColor;
            Widgets.Label(new Rect(x + headW + InlineIconSlot + 1f, y, tailW, h), tail);
            return y + h;
        }

        private static float StageLabel(float x, float y, float width, string text)
        {
            float h = MeasuredHeight(text, width, Text.LineHeight);
            Widgets.Label(new Rect(x, y, width, h), text);
            return y + h;
        }

        /// <summary>
        /// 「现场物资 / 预期获得 / 远行队补给品」列表的**紧凑一行**（图标 + 名称 + 右侧明细 + 可选 i 按钮）。
        ///
        /// 对话框与页签都画同一份数据（`worker.PreviewItems` / `worker.ProgressItems`），
        /// 差别只在"画几行、要不要滚动" —— 所以行的画法必须只有一处，
        /// 否则原版页签与 RadiusUI 皮肤会长得不一样。
        ///
        /// S9 改了两点（用户要求「现场物资 显示已经获取的数量/预期总数量 市场总价值」）：
        ///   ① 明细从"只有 tooltip"改成**右对齐画在行内** —— 数字看不见等于没显示；
        ///   ② 支持 <see cref="DelegationPreviewItem.detailPrefix" />（"已获取 3/12 件 · "），
        ///      分母来自 <see cref="Delegation.itemLedger" />。
        /// S13 再补：<paramref name="withInfoButton" /> 在行尾画一枚原版 i（查看信息卡）。
        /// </summary>
        public static void DrawItemRow(Rect row, DelegationPreviewItem item, float iconSize, float textHeight,
            bool withInfoButton = false)
        {
            if (item == null)
            {
                return;
            }
            // ⚠️ textHeight 是调用方给的"期望字号高度"，但它**不能小于真实行高**：
            //    GameFont.Small 的行高是 22，画进 18px 的矩形里会被裁掉下半截
            //    （S8 实机反馈："现场物资下面显示不全"）。这里兜一道，调用方写 18 也不会缺字。
            textHeight = Mathf.Max(textHeight, Text.LineHeight);
            if (textHeight > row.height)
            {
                textHeight = row.height;
            }
            Rect iconRect = new Rect(row.x, row.y + (row.height - iconSize) * 0.5f, iconSize, iconSize);
            if (item.pawn != null)
            {
                GUI.DrawTexture(iconRect, PortraitsCache.Get(item.pawn, new Vector2(iconSize, iconSize), Rot4.South));
                TooltipHandler.TipRegion(iconRect, item.pawn.LabelShortCap);
            }
            else if (item.thingDef != null)
            {
                Widgets.ThingIcon(iconRect, item.thingDef, null, null, 1f, null, null, 1f);
                TooltipHandler.TipRegion(iconRect, item.thingDef.LabelCap);
            }

            // S13：行尾的 i（只在有 ThingDef 时才有意义 —— 人走的是头像+信息卡那条路）
            float rightReserve = 0f;
            Rect infoRect = default(Rect);
            if (withInfoButton && item.thingDef != null)
            {
                float infoSize = Widgets.InfoCardButtonSize;
                infoRect = new Rect(row.xMax - infoSize, row.y + (row.height - infoSize) * 0.5f, infoSize, infoSize);
                rightReserve = infoSize + 4f;
            }

            float textX = iconRect.xMax + 6f;
            float rowTextW = Mathf.Max(40f, row.xMax - rightReserve - textX);
            string detail = (item.detailPrefix ?? "") + (item.detail ?? "");
            float lineH = textHeight;
            float textY = row.y + (row.height - lineH) * 0.5f;

            // 明细宽度**不能吃掉整行**：名称至少要留下 40px，否则"黄金"和"已获取…"会叠在一起
            float detailW = detail.NullOrEmpty() ? 0f : Text.CalcSize(detail).x + 8f;
            detailW = Mathf.Min(detailW, Mathf.Max(0f, rowTextW - 40f));
            float labelW = Mathf.Max(40f, rowTextW - detailW);

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
                Widgets.Label(new Rect(textX + labelW, textY, Mathf.Max(40f, rowTextW - labelW), lineH), detail);
                Text.Anchor = oldAnchor;
                GUI.color = oldDetail;
            }
            if (withInfoButton && item.thingDef != null)
            {
                Widgets.InfoCardButton(infoRect.x, infoRect.y, item.thingDef);
            }
            TooltipHandler.TipRegion(row, (item.label ?? "") + "\n" + detail);
        }

        // （S14）原 `FlowLine()`（取第一行的兼容层）已删除，没有调用者了。

        // ================================================================ 事件报告（S15）

        /// <summary>
        /// 事件留痕的报告行（S15）：每行一条 `· 塌方（开工后 12.4 小时）：矿洞顶板塌了一块…`。
        ///
        /// **只有这一份实现**：完成信件、中断信件、报告窗口、将来的历史记录全都用它 ——
        /// 两处各写一份格式化，迟早会出现"信件里有、窗口里没有"这种事。
        /// </summary>
        public static List<string> EventReportLines(Delegation d, int maxLines = 12)
        {
            if (d?.eventLog.NullOrEmpty() ?? true)
            {
                return null;
            }
            List<string> lines = new List<string>();
            int from = Mathf.Max(0, d.eventLog.Count - maxLines);
            if (from > 0)
            {
                lines.Add(string.Format("（更早的 {0} 条从略）", from));
            }
            for (int i = from; i < d.eventLog.Count; i++)
            {
                DelegationEventLogEntry e = d.eventLog[i];
                if (e == null)
                {
                    continue;
                }
                float hoursIn = Mathf.Max(0f, (e.firedTickAbs - d.startedTickAbs) / Delegation.TicksPerHour);
                lines.Add(string.Format("· {0}（开工后 {1:0.#} 小时）：{2}",
                    e.Label, hoursIn, e.detail.NullOrEmpty() ? "（无描述）" : e.detail));
            }
            return lines;
        }

        /// <summary>上面那份行的整块文本（信件正文用）；没有事件时返回 null。</summary>
        public static string EventReportText(Delegation d, int maxLines = 12)
        {
            List<string> lines = EventReportLines(d, maxLines);
            if (lines.NullOrEmpty())
            {
                return null;
            }
            return string.Format("期间发生的事件（{0} 条）：\n{1}", d.eventLog.Count, string.Join("\n", lines.ToArray()));
        }

        /// <summary>
        /// 「距离开工还有多久」一行（S14 用户要求）。只在**休息中且没暂停**时返回非空。
        ///
        /// 为什么需要：休息期间进度条不动、`phaseTicks` 也不涨，没有这个数玩家只会觉得"卡住了"。
        /// 判据走 <see cref="Delegation.IsWorkTime" />（含紧急加班 —— 加班时人本来就在干活，不显示）。
        /// </summary>
        public static string WorkStartLine(Delegation d, Site site)
        {
            if (d == null || site == null || d.paused || d.mode == null)
            {
                return null;
            }
            long now = GenTicks.TicksAbs;
            if (d.IsWorkTime(site, now))
            {
                return null;
            }
            float hours = d.mode.HoursUntilStart(site.Tile, now);
            if (hours <= 0f)
            {
                return null;
            }
            // S14 定稿：只留小时数 —— 工时（`6:00 - 22:00`）在模式行里已经写过一遍了
            return string.Format("距离开工还有 {0:0.#} 小时", hours);
        }

        /// <summary>
        /// 「紧急加班」状态一行（§19.26）；没在加班时返回 null。
        ///
        /// 这一行必须存在：加班期间**不补休息**这件事在原版里没有任何可见反馈
        /// （车队不跑 Job、休息条只在需求面板上），不说出来玩家只会看到"莫名工伤变多"。
        /// </summary>
        public static string OvertimeLine(Delegation d)
        {
            if (d == null || !d.EmergencyOvertimeActive)
            {
                return null;
            }
            return string.Format("**紧急加班中**：剩余额度 {0:0.#} 小时 —— 无视工时窗口，且队员不会补休息（事故伤害风险上升）",
                d.overtimeTicksRemaining / 2500f);
        }

        // ================================================================ S31：战场清点（尸骸 / 缴获 / 俘虏）

        /// <summary>
        /// 「战场清点」表的行（S31）：就地处理的尸骸 + 实际装车的缴获（装备 / 肉皮）+ 收押的俘虏。
        ///
        /// 用户拍板 3B：**尸骸也要在物资表里有一行** —— 2B 是就地屠宰、尸体不带回家，
        /// 但"这趟宰了 N 具"这件事必须看得见（看不见等于没做，这是他反复强调过的一条）。
        ///
        /// 量纲注意：<see cref="DelegationPreviewItem.value" /> 这里用的是**基础市价 × 数量**
        /// （品质加成要造出实物才算得准）—— 那一句精确的总价在搜集段写给玩家的说明里。
        /// </summary>
        public static List<DelegationPreviewItem> CleanupRows(Delegation d)
        {
            List<DelegationPreviewItem> rows = new List<DelegationPreviewItem>();
            if (d == null)
            {
                return rows;
            }

            if (d.corpsesButchered > 0)
            {
                rows.Add(new DelegationPreviewItem
                {
                    label = "尸骸（已就地处理）",
                    detail = string.Format("×{0} 具 · 肉与皮按载重装车{1}", d.corpsesButchered, DownedAnimalNote(d)),
                    count = d.corpsesButchered,
                    unitLabel = "具",
                });
            }

            // S32「带走」档：尸骸真的在车队里（回家能上屠宰台），所以这一行也写"带上"
            if (d.corpsesHauled > 0)
            {
                rows.Add(new DelegationPreviewItem
                {
                    label = "尸骸（已带上）",
                    detail = string.Format("×{0} 具 · {1:0.#} kg · 可自行屠宰{2}",
                        d.corpsesHauled, d.corpsesHauledMass, DownedAnimalNote(d)),
                    count = d.corpsesHauled,
                    unitLabel = "具",
                    mass = d.corpsesHauledMass,
                });
            }

            if (!d.takenRows.NullOrEmpty())
            {
                for (int i = 0; i < d.takenRows.Count; i++)
                {
                    DelegationLootItem it = d.takenRows[i];
                    if (it?.def == null || it.count <= 0)
                    {
                        continue;
                    }
                    float mass = it.UnitMass * it.count;
                    rows.Add(new DelegationPreviewItem
                    {
                        thingDef = it.def,
                        label = it.def.LabelCap,
                        detail = string.Format("×{0} 件 · {1:0.#} kg · 约 {2:0} 银", it.count, mass, it.SortValue),
                        count = it.count,
                        unitLabel = "件",
                        mass = mass,
                        value = it.SortValue,
                    });
                }
            }

            if (!d.capturedPrisoners.NullOrEmpty())
            {
                for (int i = 0; i < d.capturedPrisoners.Count; i++)
                {
                    Pawn p = d.capturedPrisoners[i];
                    if (p == null)
                    {
                        continue;
                    }
                    rows.Add(new DelegationPreviewItem
                    {
                        pawn = p,
                        label = p.LabelShortCap,
                        detail = "俘虏 · 已收押随队",
                        count = 1,
                        unitLabel = "人",
                    });
                }
            }
            return rows;
        }

        /// <summary>
        /// S33：尸骸行尾那句"这里面有几只是**倒地的动物**顺手处置掉的"（没有就返回空串）。
        ///
        /// 存在的理由：同一个面板上，作战任务段写的是「敌方 0 阵亡/2 倒地」，
        /// 而战场清点表写「尸骸 ×2 具」——不加这一句，玩家会以为两个数字打架。
        /// </summary>
        private static string DownedAnimalNote(Delegation d)
        {
            return d != null && d.downedAnimalsDisposed > 0
                ? string.Format("（含 {0} 只倒地的动物）", d.downedAnimalsDisposed)
                : "";
        }

        /// <summary>「战场清点」块的表头；null = 这一趟没有可显示的东西（整块不画）。</summary>
        public static string CleanupHeader(Delegation d, List<DelegationPreviewItem> rows)
        {
            if (d == null || rows.NullOrEmpty())
            {
                return null;
            }
            float value = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null && rows[i].value > 0f)
                {
                    value += rows[i].value;
                }
            }
            string head = string.Format("战场清点（{0} 类）", rows.Count);
            if (value > 0f)
            {
                head += string.Format(" · 市价合计约 {0:0} 银", value);
            }
            if (d.prisonersTaken > 0)
            {
                head += string.Format(" · 收押 {0} 人", d.prisonersTaken);
            }
            return head;
        }

        /// <summary>
        /// 一行流程描述在多行文本里的高度（`Widgets.Label` 会换行，页签按固定行高画会重叠）。
        /// </summary>
        public static float MeasuredHeight(string text, float width, float fallback)
        {
            if (text.NullOrEmpty() || width <= 0f)
            {
                return fallback;
            }
            return Mathf.Max(fallback, Text.CalcHeight(text, width));
        }

        /// <summary>
        /// 「结束条件」的一行文案。**全项目共用这一份**：委派草稿表单（对话框与主控台「待下达」两处壳）、
        /// 在途详情（原版与 RadiusUI 皮肤两端）、地点检视面板。
        ///
        /// 为什么必须收到这里：这三档文案原本在 4 个文件里各写了一遍，而且**全是采矿口径**
        /// （"采空为止" / "采满 N 单位"）—— 于是搜刮物资藏匿点时对话框会说"采空为止"。
        ///
        /// ⚠️ 量纲有两套，别用错：
        ///   · Days   → 天（与 worker 无关）
        ///   · Quota  → **产出**单位（`OutputUnitName`）。配额比的是已交付产出，
        ///              采矿是"单位"而进度是"格"，两者不同；物资点恰好都是"件"。
        ///   · 取尽   → worker 自己给的整句（采矿"采空为止" / 物资点"搬空为止" / 营救"救出为止"）。
        /// </summary>
        public static string EndConditionLabel(DelegationEndCondition condition, int daysLimit, int quotaUnits,
            DelegationWorker worker)
        {
            switch (condition)
            {
                case DelegationEndCondition.Days:
                    return string.Format("干满 {0} 天", daysLimit);
                case DelegationEndCondition.Quota:
                    return string.Format("{0}满 {1} {2}",
                        worker?.WorkVerb ?? "采", quotaUnits, worker?.OutputUnitName ?? "单位");
                default:
                    return worker?.UntilDepletedLabel ?? "采空为止";
            }
        }

        /// <summary>
        /// 「模式」那一行的措辞。**唯一一份**：在途详情的 `Delegation.ModeLine()`、
        /// 草稿表单的 `DelegationDraft.ModeLine()`、以及皮肤右栏的"计划态概览"都调它
        /// —— 否则同一个模式在三处会有三种写法。
        /// </summary>
        public static string ModeLine(DelegationModeDef mode)
        {
            if (mode == null)
            {
                return "（无可用模式）";
            }
            // RIM-5（用户拍板 1A + 2B）：模式不再给速率系数、也不再直接挂心情 ——
            // 它只剩"作息窗口 + 作业强度"两件事；心情与效率都由满意度产出（见 SatisfactionLine）。
            return string.Format("{0} · {1} · 作业强度 {2:+0.#;-0.#;0}",
                mode.LabelCap, mode.HoursLabel, mode.workIntensity);
        }

        /// <summary>
        /// 「满意度」那一行（RIM-5）。**唯一一份措辞**：在途详情、原版页签、皮肤概览都调它，
        /// 所以四处不会出现"同一趟委派两个满意度数字"。
        ///
        /// `satisfaction` = 0..1（0.5 中性）、`moodPerDay` = 每日心情、`rate` = 作业速率系数。
        /// </summary>
        public static string SatisfactionLine(float satisfaction, float moodPerDay, float rate)
        {
            float pct = Mathf.Clamp01(satisfaction);
            string stage = DelegationSatisfaction.StageLabel(pct) ?? "—";
            // 0..1 直接印成百分比：玩家一眼能看出"比中性高还是低"，比印 0.5 这种小数直观
            return string.Format("满意度 {0} · {1} · 每天心情 {2:+0.#;-0.#;0} · 作业速率 ×{3:0.000}",
                pct.ToStringPercent(), stage, moodPerDay, rate);
        }

        /// <summary>
        /// 「满意度」一行，**给还没有 Delegation 实例的场合**（草稿 / 前往中计划）：
        /// 按"预计值"算（吃喝与远行时间未知时取中性），并在末尾标明"预计"。
        /// </summary>
        public static string SatisfactionLineEstimated(DelegationModeDef mode, float daysAway = 0f)
        {
            float satisfaction = DelegationSatisfaction.EstimatedValue(mode, daysAway);
            return SatisfactionLine(satisfaction, DelegationSatisfaction.Mood(satisfaction),
                DelegationSatisfaction.RateFactor(satisfaction)) + "（预计）";
        }

        // ── 满意度**独立一栏**（RIM-5 追加需求，2026-10-05）──────────────────────
        //
        // 用户原话：「把满意度单独拆成一栏，悬浮提供简要情报（显示影响的因素），
        //           工作模式那块的内容也要同步修改」。
        // 于是口径拆成"主文字 / 副文字 / 悬浮情报"三件，**仍然只有这一份**：
        // 皮肤概览栏、草稿主列、在途主列、原版页签都从这里取，不许各自拼串。

        /// <summary>满意度栏的**主文字**：`满意度 62%`。</summary>
        public static string SatisfactionMain(float satisfaction)
        {
            return string.Format("满意度 {0}", Mathf.Clamp01(satisfaction).ToStringPercent());
        }

        /// <summary>满意度栏的**副文字**：`干得挺顺 · 每天心情 +4 · 作业速率 ×1.036`（关掉时说明白）。</summary>
        public static string SatisfactionSub(float satisfaction, float moodPerDay, float rate)
        {
            if (!DelegationSatisfaction.Enabled)
            {
                return "已在 Mod 设置里关闭 ⇒ 心情 0、作业速率 ×1";
            }
            return string.Format("{0} · 每天心情 {1:+0.#;-0.#;0} · 作业速率 ×{2:0.000}",
                DelegationSatisfaction.StageLabel(satisfaction) ?? "—", moodPerDay, rate);
        }

        /// <summary>在途委派满意度栏的副文字（从 Delegation 直接取）。</summary>
        public static string SatisfactionSubOf(Delegation d)
        {
            float s = d?.satisfaction ?? DelegationSatisfaction.Neutral;
            return SatisfactionSub(s, DelegationSatisfaction.Mood(s),
                DelegationSatisfaction.RateFactor(s));
        }

        /// <summary>草稿 / 前往中计划满意度栏的副文字（预计口径）。</summary>
        public static string SatisfactionSubEstimated(DelegationModeDef mode, float daysAway = 0f)
        {
            float s = DelegationSatisfaction.EstimatedValue(mode, daysAway);
            return SatisfactionSub(s, DelegationSatisfaction.Mood(s),
                DelegationSatisfaction.RateFactor(s)) + "（预计）";
        }

        /// <summary>
        /// 满意度栏的**悬浮情报**：逐条摊开影响满意度的四个来源
        /// （开关 / 权重 / 子分 / 数据细节）+ 末尾一行汇总。
        /// 条目与汇总都来自 <see cref="DelegationSatisfaction.FactorLines" />（唯一来源，不在 UI 里重算）。
        /// </summary>
        public static string SatisfactionTip(Delegation d)
        {
            if (d == null)
            {
                return null;
            }
            return "满意度 = 四个来源的加权平均（0.5 = 中性）：\n"
                + string.Join("\n", DelegationSatisfaction.FactorLines(d).ToArray())
                + "\n\n来源的开关 / 权重在 Mod 设置页；曲线参数在 Defs/RimDelegation_Satisfaction.xml。";
        }

        /// <summary>满意度栏的悬浮情报（草稿 / 前往中计划的预计口径）。</summary>
        public static string SatisfactionTipEstimated(DelegationModeDef mode, float daysAway = 0f)
        {
            return "满意度（预计）= 四个来源的加权平均（0.5 = 中性）：\n"
                + string.Join("\n", DelegationSatisfaction.FactorLinesEstimated(mode, daysAway).ToArray())
                + "\n\n吃喝与在外天数要等开工后才算得准，所以这里标「预计」。";
        }

        /// <summary>
        /// 满意度的**单行紧凑写法**（给只有一行位置的场合：原版远行队页签那一行）：
        /// `62%（心情 +4/天 · 速率 ×1.036）`。完整口径仍用 <see cref="SatisfactionLine" />。
        /// </summary>
        public static string SatisfactionShort(float satisfaction, float moodPerDay, float rate)
        {
            if (!DelegationSatisfaction.Enabled)
            {
                return "已关闭（心情 0 · 速率 ×1）";
            }
            return string.Format("{0}（心情 {1:+0.#;-0.#;0}/天 · 速率 ×{2:0.000}）",
                Mathf.Clamp01(satisfaction).ToStringPercent(), moodPerDay, rate);
        }

        /// <summary>
        /// 作战姿态那一行的措辞（S18 收进共用件）。
        ///
        /// 为什么搬到这里：S5 起对话框、皮肤对话框各写了一份，S18 又多了草稿页这一处 ——
        /// 三份就是三次漂移的机会。这里留一份，`DelegationDraft.ApproachLine` 与皮肤都调它。
        /// </summary>
        public static string ApproachLabel(DelegationApproachDef approach)
        {
            if (approach == null)
            {
                return "（无可用姿态）";
            }
            return approach.LabelCap.ToString() + (approach.stealth ? "（掷暴露）" : "（战斗结算）");
        }

        /// <summary>
        /// 「疲劳 · 工作遭受事故伤害倍率」一行（§19.25）。对话框 / 页签 / 检视文本共用，保证口径一致。
        ///
        /// 这一行必须存在：疲劳在远行队里**不会**通过原版 `NeedRest` 心情体现
        /// （那条想法对未 spawn 的 pawn 直接 return false），所以不显示的话，
        /// 玩家只会看到"全天候和加班产出差不多"，完全不知道风险差在哪里。
        ///
        /// S9 换词：用户原话「工伤倍率修改为 工作遭受事故伤害倍率」（原版工伤事件的
        /// 定义处也叫"工作遭受事故伤害"，措辞与它对齐后玩家不必在两种说法之间翻译）。
        /// </summary>
        public static string FatigueRiskLine(Delegation d)
        {
            if (d == null)
            {
                return null;
            }
            float rest = DelegationUtility.AverageRest(d);
            float mult = DelegationUtility.AccidentRiskMultiplier(d);
            return string.Format("疲劳状态：全队平均休息 {0} → 工作遭受事故伤害倍率 ×{1:0.##}",
                rest.ToStringPercent(), mult);
        }

        /// <summary>
        /// 「心情影响」那半句（S21 收进共用件）。0 心情时写"不受心情影响"（用户书面语清单原话），
        /// 非 0 时写"每天心情 +X"。
        ///
        /// 为什么必须共用：模式行在**四处**各写了一遍这段话（原版草稿页汇总、皮肤草稿页、
        /// 皮肤在途概览、皮肤草稿概览），一处改词就得改四遍 —— S21 的书面语清单正是这么漏掉的。
        /// </summary>
        public static string MoodLine(float mood)
        {
            return mood == 0f
                ? "不受心情影响"
                : string.Format("每天心情 {0:+0.#;-0.#}", mood);
        }

        /// <summary>
        /// RIM-12：草稿页那一行「固定流程：侦察环境 + 移动到目标区域 + 破门 + 撤离 共 6 小时（与人数/技能无关）」。
        ///
        /// **文案与算法只有一份**（草稿页 + 皮肤草稿概览两份复制品都调这里）——
        /// 这是本项目"双端准则"的老规矩：同一句话不许在两个画法里各写一遍。
        /// 返回 null = 这条委派没有固定流程段（营救 / 救援），此时**不显示**这一行。
        /// </summary>
        public static string FixedFlowLine(DelegationDef def, Site site)
        {
            List<string> labels;
            float min;
            float max;
            float hours = DelegationFlow.FixedFlowHours(def, site, FlowHoursScale, out labels, out min, out max);
            if (hours <= 0f || labels.NullOrEmpty())
            {
                return null;
            }
            // RIM-11：有段配了随机偏移时，如实写区间（否则"约"字会把不确定性藏起来）
            string amount = max > min + 0.01f
                ? string.Format("{0:0.#}~{1:0.#} 小时", min, max)
                : string.Format("共 {0:0.#} 小时", hours);
            return string.Format("固定流程：{0} {1}（与人数/技能无关）",
                string.Join(" + ", labels.ToArray()), amount);
        }

        /// <summary>RIM-12：这条委派固定流程一共多少小时（草稿页 ETA 要把它加进去，修 P-A3）。</summary>
        public static float FixedFlowHours(DelegationDef def, Site site)
        {
            List<string> labels;
            float min;
            float max;
            return DelegationFlow.FixedFlowHours(def, site, FlowHoursScale, out labels, out min, out max);
        }

        /// <summary>
        /// RIM-12：玩家当前设置的流程时长倍率（开工时会被冻结进 `DelegationFlowState`；
        /// 草稿期还没有委派实例 ⇒ 直接读设置，口径与"完工时刻"一致）。
        /// </summary>
        public static float FlowHoursScale
        {
            get
            {
                float v = RimDelegationMod.Settings?.flowHoursScale ?? 1f;
                return v <= 0f || float.IsNaN(v) || float.IsInfinity(v) ? 1f : v;
            }
        }

        /// <summary>
        /// 「疲劳 → 工作遭受事故伤害倍率」一行，**给一批人**算（S19）。
        ///
        /// 为什么需要这个重载：草稿表单里还没有 `Delegation` 实例，只有"已勾选的人"，
        /// 而这一行恰恰是玩家在**下单那一刻**最需要看到的生理代价。公式与
        /// <see cref="FatigueRiskLine(Delegation)" /> 同源（都走 `DelegationUtility.AccidentDef`），
        /// 所以"选人时看到的倍率"与"开工后看到的倍率"不会打架。
        /// </summary>
        public static string FatigueRiskLineOf(List<Pawn> pawns)
        {
            DelegationEventDef_PawnAccident accident = DelegationUtility.AccidentDef();
            if (accident == null)
            {
                return null;
            }
            float avgRest = DelegationUtility.AverageRestOf(pawns);
            if (avgRest >= 0.999f)
            {
                return "疲劳状态：休息充足，事故伤害概率无额外加成";
            }
            float factor = DelegationUtility.FatigueFactorOf(avgRest, accident.fatigueOnsetRest);
            return string.Format("疲劳状态：已选 {0} 人平均休息 {1} → 事故伤害概率 ×{2:0.##}、伤害 ×{3:0.##}",
                pawns?.Count ?? 0, avgRest.ToStringPercent(),
                1f + (accident.maxFatigueMultiplier - 1f) * factor,
                1f + accident.damageFatigueScale * factor);
        }

        /// <summary>
        /// 「补给品」一行：远行队库存里**阶段最高的那份**食物，以及它给的两笔心情。
        /// 分开展示"标准"和"委派"是为了让玩家看懂：委派这条是额外的，不是替代。
        ///
        /// S9 换词（用户原话）：「伙食 → 补给品」「原版 0 委派-2 → 标准心情加成 0，
        /// 委派工作时的额外心情加成 -2」。措辞照用户给的字面实现，不自作主张改。
        /// </summary>
        public static string MealLine(Caravan caravan)
        {
            DelegationFoodMoodDef cfg = DelegationUtility.FoodMoodDef();
            if (cfg?.thought == null)
            {
                return null;
            }
            ThingDef food = DelegationUtility.BestCarriedFood(caravan);
            string days = FoodDaysLine(caravan);
            string tail = days.NullOrEmpty() ? "" : " · " + days;
            if (food == null)
            {
                return "补给品 · 远行队库存里没有食物（会断粮，除非改吃野果）" + tail;
            }
            int stage = cfg.StageFor(food);
            float extra = cfg.MoodOfStage(stage);
            float vanilla = DelegationUtility.MoodEffectOf(food.ingestible?.tasteThought);
            return string.Format("补给品（库存最好）· {0} · 标准心情加成 {1}，委派工作时的额外心情加成 {2}",
                food.LabelCap,
                vanilla.ToString("+0.#;-0.#;0"),
                extra.ToString("+0.#;-0.#;0")) + tail;
        }

        /// <summary>
        /// 「全部补给约可维持 N 天」（S8 页签草图里的那一行；S14 补上**腐烂时间**）。
        ///
        /// 为什么直接读原版的 <c>Caravan.DaysWorthOfFood</c> 而不是自己遍历库存：
        /// 它内部就是 `DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(caravan)` +
        /// `DaysUntilRotCalculator.ApproxDaysUntilRot(caravan)`，并且**带 3000 tick 缓存**
        /// （`cachedDaysWorthOfFoodForTicks`）—— 页签是每帧绘制的，自己遍历一遍库存等于又踩一次
        /// "per-frame 造对象"的坑（§教训）；数值也因此天然与原版车队信息栏显示的一致。
        ///
        /// S14：属性是 `(float days, float tillRot)` 的元组，原版 `CaravanUIUtility.GetDaysWorthOfFoodLabel`
        /// 的渲染口径是（反编译）：`days.ToString("0.#")`，且**当 `tillRot &lt; 600 &amp;&amp; tillRot &lt; days` 时**
        /// 追加 `" " + "(" + "DaysWorthOfFoodInfoRot".Translate($"{tillRot:0.#})")` ⇒
        /// 中文即 `7.8 (2.4)之后腐烂`。这里照抄同一条口径（包括那个"先烂后吃完"才提示的条件），
        /// 所以本行数字与玩家在原版车队面板上看到的是同一个。
        /// </summary>
        public static string FoodDaysLine(Caravan caravan)
        {
            if (caravan == null)
            {
                return null;
            }
            float days = caravan.DaysWorthOfFood.days;
            float tillRot = caravan.DaysWorthOfFood.tillRot;
            if (days <= 0f)
            {
                return "全部补给已耗尽";
            }
            string s = days >= 1000f
                ? "全部补给充足"
                : string.Format("全部补给约可维持 {0:0.#}", days);
            if (days < 1000f)
            {
                // S21 用户书面语清单：「全部补给约可维持6.8天（1.8）之后腐烂」→「…6.8（1.8）天」——
                // 单位"天"要落在最后，括号只是补一句"能吃到哪天为止还不会烂"。
                s += tillRot < 600f && tillRot < days
                    ? string.Format("（{0:0.#}）天", tillRot)
                    : " 天";
            }
            return s;
        }

        /// <summary>
        /// "还有多久腐烂"半句：原版口径是 `(2.4)之后腐烂`，**S23 起加了个"天"** ⇒ `(2.4)天之后腐烂`
        /// （用户口径：「这个修改为 (2)天之后腐烂」——"2"不带单位读不出是几天）。
        ///
        /// 括号与原版那个右括号都是**调用方拼**的（原版传进去的参数就是 `$"{tillRot:0.#})"`），
        /// 所以这里必须 `<see cref="string.Format" />` 出 "2.4)天" 再交给 Keyed —— 直接
        /// `"DaysWorthOfFoodInfoRot".Translate(2.4f)` 会走 NamedArgument 那条路，中文键里的 `{0}` 能吃上，
        /// 但格式说明符吃不上（S7 踩过的坑）。单位塞在**参数里**，所以既不用改原版 Keyed、
        /// 也不用把整句重拼一遍。取不到汉化时退回一句自拼的中文，绝不显示裸键名。
        /// </summary>
        public static string RotPhrase(float tillRotDays)
        {
            const string Key = "DaysWorthOfFoodInfoRot";
            string arg = tillRotDays.ToString("0.#") + ")天";
            if (Translator.CanTranslate(Key))
            {
                return "(" + Key.Translate(arg);
            }
            return string.Format("({0:0.#})天之后腐烂", tillRotDays);
        }

        /// <summary>
        /// 状态词：`作业中 / 休息中 / 已暂停 / 停摆中`（S8 页签、主控台窗口共用一份）。
        ///
        /// 判据统一走 <see cref="Delegation.IsWorkTime" />（含紧急加班，§19.26）——
        /// 三处各写一遍 switch 迟早会有一处忘记加班。颜色由调用方决定（原版用 GUI.color、
        /// 皮肤用 Palette），这里只给词。
        /// </summary>
        public static string StatusWord(Delegation d, Site site)
        {
            if (d == null)
            {
                return null;
            }
            if (d.paused)
            {
                return "RimDelegationTabStatusPaused".Translate().ToString();
            }
            if (d.IsStalled(GenTicks.TicksAbs))
            {
                return "RimDelegationTabStatusStalled".Translate().ToString();
            }
            // S25：停在"等作战指令"的段之前 ⇒ 状态词是**等待指令**，不是"休息中"。
            // 写"休息中"会让玩家以为队伍在按作息睡觉，而实际上它在等你点「进行交战 / 撤退」。
            if (d.AwaitingOrder)
            {
                return "等待指令";
            }
            if (site != null && d.IsWorkTime(site, GenTicks.TicksAbs))
            {
                return "RimDelegationTabStatusWorking".Translate().ToString();
            }
            return "RimDelegationTabStatusResting".Translate().ToString();
        }

        /// <summary>
        /// 「总进度」行的 tooltip（S8）：把原来各自独占一行的进度细节收进悬停提示。
        ///
        /// 为什么这么做：S8 的重排把这一行改成"百分比 + 已干多少 + ETA"，
        /// 而 worker 的 <c>ProgressLabel</c>（车队负重 / 约 N 单位）、事件点存量、
        /// 交付汇总、待交付这四行**是 S5–S7 明确要展示的信息**，重排不等于删信息 ——
        /// 所以搬进 tooltip（零高度成本、鼠标一悬停就能看到）。
        /// 做成公开件是双端准则：原版页签与皮肤都用这一份，措辞不会分叉。
        /// </summary>
        public static string ProgressDetailTip(Delegation d, DelegationWorker worker, string unit, string outputUnit)
        {
            if (d == null)
            {
                return null;
            }
            string s = worker?.ProgressLabel(d);
            if (d.deposit != null)
            {
                string line = string.Format("RimDelegationTabDeposit".Translate(),
                    d.deposit.UnitsRemaining, d.deposit.totalUnits, d.deposit.timesDelegated,
                    d.deposit.unitsDelivered, unit, outputUnit);
                s = s.NullOrEmpty() ? line : s + "\n" + line;
            }
            string delivery = worker?.DeliverySummary(d);
            if (!delivery.NullOrEmpty())
            {
                s = s.NullOrEmpty() ? delivery : s + "\n" + delivery;
            }
            if (d.oreUnits >= 1f)
            {
                string pending = string.Format("RimDelegationTabPending".Translate(), Mathf.FloorToInt(d.oreUnits));
                s = s.NullOrEmpty() ? pending : s + "\n" + pending;
            }
            return s;
        }

        /// <summary>
        /// 「总进度」行的 tooltip + **预计完成时刻**。
        ///
        /// 用途：原版页签那一行已经排满（百分比 / 已干多少 / 剩余天数），再塞一个
        /// "预期于 5501年 春 3日, 14时 完成"会把进度条挤到 80px 以下。按 S9 的拍板
        /// （"页签一行放不下时绝对时间收进 tooltip"），页签用这一份；主控台空间够，直接画在行内。
        /// </summary>
        public static string ProgressTipWithEta(Delegation d, Site site, DelegationWorker worker,
            string unit, string outputUnit)
        {
            string tip = ProgressDetailTip(d, worker, unit, outputUnit);
            string eta = FinishDateLine(d, site);
            if (eta.NullOrEmpty())
            {
                return tip;
            }
            return tip.NullOrEmpty() ? eta : tip + "\n" + eta;
        }

        // ---------------------------------------------------------------- 预计完成时刻（S9）

        /// <summary>
        /// 预计还要多少天收工；返回负数 = 估算不出来。
        ///
        /// 与 <see cref="Delegation.EstimatedDaysLeft" /> 的差别只有一个：
        /// 「干满 N 天」这个结束条件下，真正的收工时刻是"活干完"与"天数用完"里**更早**的那个 ——
        /// 直接拿工作量除以速率会把 3 天的活说成 5 天（而它到第 3 天就收工了）。
        /// </summary>
        public static float EstimatedFinishDays(Delegation d, Site site)
        {
            if (d == null || site == null)
            {
                return -1f;
            }
            float work = d.EstimatedDaysLeft(site.Tile);
            if (work < 0f)
            {
                return -1f;
            }
            if (d.endCondition == DelegationEndCondition.Days && d.daysLimit > 0)
            {
                work = Mathf.Min(work, Mathf.Max(0f, d.daysLimit - d.ElapsedDays(GenTicks.TicksAbs)));
            }
            return Mathf.Max(0f, work);
        }

        /// <summary>`预期于 5501年 春 3日, 14时 完成`；估算不出来返回 null。</summary>
        public static string FinishDateLine(Delegation d, Site site)
        {
            string at = FinishDateText(d, site);
            return at.NullOrEmpty() ? null : "预期于 " + at + " 完成";
        }

        /// <summary>同上，不带"完成"二字（右栏卡片那种一行放不下两句的地方用）。</summary>
        public static string FinishDateShort(Delegation d, Site site)
        {
            string at = FinishDateText(d, site);
            return at.NullOrEmpty() ? null : "预期于 " + at;
        }

        /// <summary>
        /// 绝对时刻的文案。走**原版** `GenDate.DateFullStringWithHourAt`（"5501年 春 3日, 14时"），
        /// 而不是自己拼 `Year/Quadrum/DayOfSeason`：
        ///   ① 它按地点经度算时区（`LongLatOf`），所以"几点完成"是真的当地时间；
        ///   ② 汉化由原版 Keyed（FullDate / LetterHour）负责，我们不必再维护一套日期措辞。
        /// </summary>
        private static string FinishDateText(Delegation d, Site site)
        {
            try
            {
                float days = EstimatedFinishDays(d, site);
                if (days < 0f || site == null || Find.WorldGrid == null)
                {
                    return null;
                }
                long ticks = GenTicks.TicksAbs + (long)System.Math.Round(days * 60000.0);
                return GenDate.DateFullStringWithHourAt(ticks, Find.WorldGrid.LongLatOf(site.Tile));
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 计算预计完成时刻失败：" + ex.Message, 0x5E0D9);
                return null;
            }
        }

        // ---------------------------------------------------------------- 载重预报（S9）
        //
        // 用户给的格式（原话）：
        //     载重 6.8 (+3.3)kg / 119kg
        //     委派结束后预计车队载重 10.1kg
        // 其中 (+3.3) = **还没装车的产物质量** = 剩余进度 × worker.MakePreview().massPerUnit。
        // ⚠️ 一条公式两处共用（主控台右栏 / 委派对话框），所以括号里的数永远等于
        //    "结束后预计 − 现在"，不会出现两处对不上的情况。

        /// <summary>算出 现值 / 还要装多少 / 上限（kg）。说不出产物质量时返回 false（宁可不画，也不画 0）。</summary>
        public static bool TryMassForecast(Delegation d, Site site, out float now, out float delta, out float cap)
        {
            now = 0f;
            delta = 0f;
            cap = 0f;
            if (d == null)
            {
                return false;
            }
            Caravan caravan = d.caravan;
            if (caravan == null || caravan.Destroyed)
            {
                return false;
            }
            float perUnit = 0f;
            try
            {
                perUnit = d.Worker?.MakePreview(site)?.massPerUnit ?? 0f;
            }
            catch (Exception)
            {
                perUnit = 0f;
            }
            if (perUnit <= 0f)
            {
                return false;
            }
            now = caravan.MassUsage;
            cap = caravan.MassCapacity;
            delta = Mathf.Max(0f, d.totalCells - d.cellsMined) * perUnit;
            return true;
        }

        /// <summary>`载重 6.8 (+3.3)kg / 119kg`。</summary>
        public static string MassMain(float now, float delta, float cap)
        {
            if (delta > 0.05f)
            {
                return string.Format("载重 {0:0.#} (+{1:0.#})kg / {2:0.#}kg", now, delta, cap);
            }
            return string.Format("载重 {0:0.#}kg / {1:0.#}kg", now, cap);
        }

        /// <summary>区间版（采矿抵达前只能给规模区间，见 Dialog_ChooseDelegation）。</summary>
        public static string MassMain(float now, float deltaMin, float deltaMax, float cap)
        {
            if (deltaMax > 0.05f)
            {
                if (Mathf.Abs(deltaMax - deltaMin) > 0.05f)
                {
                    return string.Format("载重 {0:0.#} (+{1:0.#}–{2:0.#})kg / {3:0.#}kg",
                        now, deltaMin, deltaMax, cap);
                }
                return string.Format("载重 {0:0.#} (+{1:0.#})kg / {2:0.#}kg", now, deltaMax, cap);
            }
            return string.Format("载重 {0:0.#}kg / {1:0.#}kg", now, cap);
        }

        /// <summary>`委派结束后预计远行队载重 10.1kg`（超重时补一句原因）。</summary>
        public static string MassSub(float now, float delta, float cap)
        {
            float toBe = now + Mathf.Max(0f, delta);
            string s = string.Format("委派结束后预计远行队载重 {0:0.#}kg", toBe);
            if (toBe > cap)
            {
                s += "（超出载重上限）";
            }
            return s;
        }

        /// <summary>区间版（同上；"只算产物"这句是给对话框的，作业期不需要）。</summary>
        public static string MassSub(float now, float deltaMin, float deltaMax, float cap)
        {
            float toMin = now + Mathf.Max(0f, deltaMin);
            float toMax = now + Mathf.Max(0f, deltaMax);
            string s = Mathf.Abs(toMax - toMin) > 0.05f
                ? string.Format("委派结束后预计远行队载重 {0:0.#}–{1:0.#}kg（只算产物）", toMin, toMax)
                : string.Format("委派结束后预计远行队载重 {0:0.#}kg（只算产物）", toMax);
            if (toMax > cap)
            {
                s += " ← 会超重";
            }
            return s;
        }

        // ---------------------------------------------------------------- 在途计划（S12）

        /// <summary>`已决定` / `延后决定` —— 列表行右端那个词。</summary>
        public static string PlanStatusWord(WorldObjectComp_Delegations comp)
        {
            if (comp == null || !comp.HasPlan)
            {
                return null;
            }
            return comp.PlanDecided ? "已决定" : "延后决定";
        }

        /// <summary>
        /// 「前往中」计划的详情行（主控台选中一条计划时显示）。**两端共用这一份**，
        /// 所以原版与皮肤说的计划内容不会分叉（双端准则）。
        ///
        /// 注意这里只说"计划里选了什么"，不预告抵达后的结果 —— 抵达时存量才掷定，
        /// 说成定论就是撒谎（与 §19.12「预告即契约」同一条理）。
        /// </summary>
        public static List<string> PlanLines(WorldObjectComp_Delegations comp)
        {
            if (comp == null || !comp.HasPlan)
            {
                return null;
            }
            List<string> lines = new List<string>();
            Caravan caravan = comp.plannedCaravan;
            Site site = comp.Site;
            DelegationDef def = comp.plannedDef;
            DelegationRequest req = comp.plannedRequest;

            lines.Add(string.Format("委派：{0} @ {1}", def?.label ?? "?", site?.Label ?? "?"));
            lines.Add(string.Format("{0} 正在前往该地点", caravan?.LabelCap ?? "?"));
            lines.Add(comp.PlanDecided
                ? "计划状态：已决定 —— 抵达即开工"
                : "计划状态：延后决定 —— 抵达后再选人 / 模式 / 结束条件");

            if (comp.PlanDecided && req != null)
            {
                DelegationWorker worker = def?.CreateWorker();
                DelegationModeDef mode = def?.ResolveMode(req.mode);
                if (mode != null)
                {
                    // RIM-5：模式不再给速率系数（用户拍板 1A），所以这一行只写作息 + 作业强度
                    lines.Add(string.Format("模式：{0} · {1} · 作业强度 {2:+0.#;-0.#;0}",
                        mode.LabelCap, mode.HoursLabel, mode.workIntensity));
                }
                if (req.approach != null)
                {
                    lines.Add("姿态：" + req.approach.LabelCap);
                }
                lines.Add("结束条件：" + EndConditionLabel(req.endCondition, req.daysLimit, req.quotaUnits, worker));
                lines.Add(string.Format("参与人员：{0} 人", req.pawns?.Count ?? 0));
            }
            lines.Add("本条目只反映「已经下达」的计划；抵达后由远行队自行开工。");
            return lines;
        }

        // ---------------------------------------------------------------- 远行队补给品（S13）

        /// <summary>
        /// 全部补给还能维持几天（原版 `Caravan.DaysWorthOfFood`，带 3000 tick 缓存）。
        /// 返回 float.MaxValue = 充足到算不完；返回值供"不足 2 天标红"这类判据用。
        /// </summary>
        public static float FoodDaysLeft(Caravan caravan)
        {
            if (caravan == null)
            {
                return 0f;
            }
            return caravan.DaysWorthOfFood.days;
        }

        /// <summary>
        /// 远行队随身携带的**补给品清单**（按 ThingDef 归并，S13 用户要求：像现场物资那样的列表，
        /// 给图标 / 名称 / 数量 / 总营养 / 重量 / 市价）。
        ///
        /// 数据源用原版 `CaravanInventoryUtility.AllInventoryItems`（含队员身上那份），
        /// 判据用 `def.IsNutritionGivingIngestible` —— 与 `DaysWorthOfFoodCalculator` 同一把尺子，
        /// 所以"列表里的东西"与右上角那个"可维持 N 天"不会互相矛盾。
        ///
        /// 营养取 `def.ingestible.CachedNutrition`（未 spawn 的裸 Thing 没有 StatDef 上下文）；
        /// 质量用 `BaseMass × stackCount`（与物资点 worker 同一口径）；市价 `MarketValue × count`。
        /// 排序：总营养降序（活命最相关的量），并列时按市价。
        /// </summary>
        public static List<DelegationPreviewItem> CaravanFoodItems(Caravan caravan)
        {
            if (caravan == null)
            {
                return null;
            }
            List<Thing> all;
            try
            {
                all = CaravanInventoryUtility.AllInventoryItems(caravan);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 取远行队库存失败：" + ex.Message, 0x5E0DB);
                return null;
            }
            if (all.NullOrEmpty())
            {
                return null;
            }

            List<DelegationPreviewItem> items = new List<DelegationPreviewItem>();
            Dictionary<ThingDef, int> indexOf = new Dictionary<ThingDef, int>();
            List<float> nutritionOf = new List<float>();
            // S14：每一类取"**最先腐烂的那一摞**"的剩余天数（int.MaxValue = 这类不会烂）
            List<int> minRotTicks = new List<int>();
            PlanetTile tile = caravan.Tile;
            int nowAbs = GenTicks.TicksAbs;

            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                ThingDef def = t?.def;
                if (def == null || t.Destroyed || t.stackCount <= 0 || !def.IsNutritionGivingIngestible)
                {
                    continue;
                }
                int idx;
                if (!indexOf.TryGetValue(def, out idx))
                {
                    idx = items.Count;
                    indexOf[def] = idx;
                    items.Add(new DelegationPreviewItem
                    {
                        thingDef = def,
                        label = def.LabelCap.ToString(),
                        unitLabel = "份",
                        count = 0,
                        mass = 0f,
                        value = 0f
                    });
                    nutritionOf.Add(0f);
                    minRotTicks.Add(int.MaxValue);
                }
                DelegationPreviewItem item = items[idx];
                item.count += t.stackCount;
                item.mass += def.BaseMass * t.stackCount;
                item.value += t.MarketValue * t.stackCount;
                nutritionOf[idx] += def.ingestible.CachedNutrition * t.stackCount;

                // 腐烂：口径照抄原版 `DaysUntilRotCalculator`（`TryGetComp<CompRottable>`，
                // `!Active` 视为不会烂）。温度取"当前格子的季节温度"——
                // 原版算车队那笔账时会沿路径逐格估温度，这里只看脚下那一格，属于**近似**，
                // 但同一份 `CompRottable` 状态与同一个公开 API（`ApproxTicksUntilRotWhenAtTempOfTile`）。
                int rotTicks = int.MaxValue;
                try
                {
                    CompRottable rottable = t.TryGetComp<CompRottable>();
                    if (rottable != null && rottable.Active)
                    {
                        rotTicks = rottable.ApproxTicksUntilRotWhenAtTempOfTile(tile, nowAbs);
                    }
                }
                catch (Exception)
                {
                    rotTicks = int.MaxValue;
                }
                if (rotTicks < minRotTicks[idx])
                {
                    minRotTicks[idx] = rotTicks;
                }
            }

            if (items.Count == 0)
            {
                return null;
            }

            // 按总营养降序（冒泡：条目通常个位数，不值得引比较器分配）
            for (int i = 0; i < items.Count - 1; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    bool better = nutritionOf[j] > nutritionOf[i]
                                  || (Mathf.Approximately(nutritionOf[j], nutritionOf[i])
                                      && items[j].value > items[i].value);
                    if (!better)
                    {
                        continue;
                    }
                    DelegationPreviewItem tmpItem = items[i];
                    items[i] = items[j];
                    items[j] = tmpItem;
                    float tmpNut = nutritionOf[i];
                    nutritionOf[i] = nutritionOf[j];
                    nutritionOf[j] = tmpNut;
                    int tmpRot = minRotTicks[i];
                    minRotTicks[i] = minRotTicks[j];
                    minRotTicks[j] = tmpRot;
                }
            }

            for (int i = 0; i < items.Count; i++)
            {
                items[i].detail = string.Format("×{0} 份 · 营养 {1:0.#} · {2:0.#} kg · 市价 {3:0} 银",
                    items[i].count, nutritionOf[i], items[i].mass, items[i].value);
                // S14：再接上半句腐烂时间（原版口径 `(2.4)之后腐烂`）。
                // 过了 600 天就当"永远不会烂"—— 与 `DaysUntilRotCalculator` 的哨兵值一致。
                if (minRotTicks[i] != int.MaxValue)
                {
                    float rotDays = minRotTicks[i] / 60000f;
                    if (rotDays < 600f)
                    {
                        items[i].detail += " " + RotPhrase(rotDays);
                    }
                }
            }
            return items;
        }

        /// <summary>`远行队补给品（3 类）· 营养合计 12.5 · 市价合计 240 银`。</summary>
        public static string FoodItemsHeader(List<DelegationPreviewItem> items)
        {
            string head = string.Format("远行队补给品（{0} 类）", items?.Count ?? 0);
            if (items.NullOrEmpty())
            {
                return head;
            }
            float value = 0f;
            float nutrition = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                DelegationPreviewItem item = items[i];
                if (item == null)
                {
                    continue;
                }
                value += Mathf.Max(0f, item.value);
                nutrition += NutritionOf(item);
            }
            head += string.Format(" · 营养合计 {0:0.#} · 市价合计 {1:0} 银", nutrition, value);
            return head;
        }

        /// <summary>
        /// 单行营养合计（`def.ingestible.CachedNutrition × 数量`）。
        ///
        /// 为什么不从 `item.detail` 里把营养解析出来：S9 的教训 —— `detail` 是**给人看的一整句话**，
        /// 参与计算的量必须有单独的机器可读字段；这里数量已经有 `count`，营养按同一口径重算即可。
        /// 未 spawn 的裸 Thing 没有 StatDef 上下文，所以用 `CachedNutrition` 而不是 `GetStatValue`。
        /// </summary>
        public static float NutritionOf(DelegationPreviewItem item)
        {
            if (item?.thingDef?.ingestible == null)
            {
                return 0f;
            }
            return item.thingDef.ingestible.CachedNutrition * Mathf.Max(0, item.count);
        }

        // ---------------------------------------------------------------- 参与者开关（S13）

        /// <summary>
        /// 备好参与者列表：<paramref name="participants" /> 是**当前参加的人**（排前面），
        /// <paramref name="others" /> 是队里还能参加、但没参加的人（排后面）。
        ///
        /// 为什么要连"没参加的人"一起列：S13 用户要求"参与者的最前面添加 V/X 来控制殖民者是否参与"，
        /// 那就得在一张表里同时看得见两种状态，否则玩家没有"加入"的入口（原来要点「添加人员」菜单）。
        /// 两个列表都由调用方**复用**（每帧别 new）。
        /// </summary>
        public static void BuildParticipantRoster(Delegation d, PawnSortMode sort,
            List<Pawn> participants, List<Pawn> others)
        {
            participants?.Clear();
            others?.Clear();
            if (d == null)
            {
                return;
            }
            if (participants != null)
            {
                participants.AddRange(d.participants);
                SortPawns(participants, sort, d.def);
            }
            List<Pawn> eligible = DelegationUtility.EligiblePawns(d.caravan);
            for (int i = 0; i < eligible.Count; i++)
            {
                Pawn p = eligible[i];
                if (p == null || d.participants.Contains(p))
                {
                    continue;
                }
                others?.Add(p);
            }
            if (others != null)
            {
                SortPawns(others, sort, d.def);
            }
        }

        private static readonly Color ToggleOn = new Color(0.45f, 0.85f, 0.45f);
        private static readonly Color ToggleOff = new Color(0.85f, 0.45f, 0.40f);

        /// <summary>
        /// 参与者行最前面那个「参加 / 不参加」开关（S13）。返回 true = 本帧被点了。
        ///
        /// 用字形 `✓` / `×` 而不是图形 emoji：`✓` 在原版 UI 里有用例，`×` 是 Latin-1 字符，
        /// 而**彩色 emoji 会渲染成豆腐块**（S8 已踩过，见 lesson）——
        /// 两端也共用这一份，所以原版与皮肤上的这个开关长得一样、行为一样。
        /// </summary>
        public static bool DrawParticipantToggle(Rect rect, bool participating, Pawn pawn, bool enabled)
        {
            Color tint = participating ? ToggleOn : ToggleOff;
            Widgets.DrawBoxSolidWithOutline(rect, new Color(tint.r, tint.g, tint.b, 0.22f), tint, 1);

            Color oldColor = GUI.color;
            TextAnchor oldAnchor = Text.Anchor;
            GameFont oldFont = Text.Font;
            GUI.color = enabled ? tint : new Color(tint.r, tint.g, tint.b, 0.45f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Small;
            Widgets.Label(rect, participating ? "✓" : "×");
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
            GUI.color = oldColor;

            TooltipHandler.TipRegion(rect, participating
                ? (pawn?.LabelShortCap.ToString() ?? "") + "：点击移出这次委派"
                : (pawn?.LabelShortCap.ToString() ?? "") + "：点击加入这次委派");
            return enabled && Widgets.ButtonInvisible(rect);
        }

        // ---------------------------------------------------------------- 现场物资清单（S9）

        /// <summary>`市价（高→低）` / `数量（多→少）` / `名称`。</summary>
        public static string ItemSortLabel(ItemSortMode mode)
        {
            switch (mode)
            {
                case ItemSortMode.CountDesc:
                    return "数量（多→少）";
                case ItemSortMode.Name:
                    return "名称";
                default:
                    return "市价（高→低）";
            }
        }

        public static ItemSortMode NextItemSort(ItemSortMode mode)
        {
            switch (mode)
            {
                case ItemSortMode.ValueDesc:
                    return ItemSortMode.CountDesc;
                case ItemSortMode.CountDesc:
                    return ItemSortMode.Name;
                default:
                    return ItemSortMode.ValueDesc;
            }
        }

        /// <summary>
        /// 窄表头专用的**短排序标签**（S10）。
        ///
        /// 为什么要另开一份：主控台那两个表头只有 100~120px 给按钮，而「排序：市价（高→低）」
        /// 这种长文案会被按钮自己裁掉（用户截图：主控台里那两处"显示不全"之一）。
        /// 长文案留给宽度充足的委派对话框工具条；窄处用带箭头的短标签 + tooltip 解释。
        /// </summary>
        public static string ItemSortShort(ItemSortMode mode)
        {
            switch (mode)
            {
                case ItemSortMode.CountDesc:
                    return "数量↓";
                case ItemSortMode.Name:
                    return "名称";
                default:
                    return "市价↓";
            }
        }

        /// <summary>同 <see cref="ItemSortShort" />，用于参与者表头。</summary>
        public static string PawnSortShort(PawnSortMode mode)
        {
            switch (mode)
            {
                case PawnSortMode.Name:
                    return "名字";
                case PawnSortMode.MoodAsc:
                    return "心情↑";
                default:
                    return "技能↓";
            }
        }

        public static void SortItems(List<DelegationPreviewItem> items, ItemSortMode mode)
        {
            if (items == null || items.Count < 2)
            {
                return;
            }
            switch (mode)
            {
                case ItemSortMode.CountDesc:
                    items.Sort((a, b) => b.count.CompareTo(a.count));
                    break;
                case ItemSortMode.Name:
                    items.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.CurrentCulture));
                    break;
                default:
                    items.Sort((a, b) => b.value.CompareTo(a.value));
                    break;
            }
        }

        /// <summary>
        /// 作业期的「现场物资」清单：**已排序** + **已填好每行的「已获取 X/Y 单位 ·」前缀**。
        ///
        /// 分母 (`Y`) 来自开工那一刻抄的台账（<see cref="Delegation.itemLedger" />），
        /// 分子 = 分母 − 现场剩余。旧存档在途的委派没有台账（或这类没有件数概念）⇒ 不填前缀，
        /// 行里只剩 worker 自己的"剩余 …"，绝不会凭空显示一个 0 当分母。
        /// </summary>
        public static List<DelegationPreviewItem> ProgressItemRows(Delegation d, Site site, ItemSortMode sort)
        {
            List<DelegationPreviewItem> items = SafeProgressItems(d, site);
            if (items.NullOrEmpty())
            {
                return items;
            }
            SortItems(items, sort);
            for (int i = 0; i < items.Count; i++)
            {
                DelegationPreviewItem item = items[i];
                if (item == null || item.thingDef == null || item.count <= 0)
                {
                    continue;
                }
                DelegationItemLedgerEntry entry = d?.LedgerFor(item.thingDef);
                if (entry == null || entry.expectedCount <= 0)
                {
                    continue;
                }
                int expected = Mathf.Max(item.count, entry.expectedCount);
                int obtained = Mathf.Clamp(expected - item.count, 0, expected);
                string unitLabel = item.unitLabel.NullOrEmpty() ? entry.unitLabel : item.unitLabel;
                item.detailPrefix = string.Format("已获取 {0}/{1}{2} · ",
                    obtained, expected, unitLabel.NullOrEmpty() ? "" : " " + unitLabel);
            }
            return items;
        }

        /// <summary>
        /// S22：这一条委派的清单现在是不是**还被流程挡着**（`DelegationDef.hideItemsUntilPhase` 指定的那一段还没走完）。
        ///
        /// 两端共用这一条判据。被挡着时列表是空的，**但块头还得照画**（写成「现场物资（? 类）」）——
        /// 整块不画的话，玩家会以为"这条委派根本没有物资这回事"
        /// （用户口径：「当现场物资还没有透露给玩家的时候，左边应该显示一个问号」
        /// 「是否可以也添加现场物资（? 类），然后下面是空的」）。
        /// </summary>
        public static bool ItemsHidden(Delegation d)
        {
            string gate = d?.def?.hideItemsUntilPhase;
            if (gate.NullOrEmpty())
            {
                return false;
            }
            return !DelegationFlow.For(d.def).PhaseDone(d?.flow, gate);
        }

        /// <summary>
        /// 「现场物资」块的表头：`现场物资（3 类）· 市价合计约 1234 银 · 该地点存量 5/40 件`。
        ///
        /// 市价合计只算**现场还剩的**（与列表里那些行同源）；该地点存量是跨委派的累计口径，
        /// 所以刻意带上"该地点"三个字，免得和"本次委派"的进度混为一谈。
        ///
        /// <paramref name="includeSiteDeposit" /> = false 时去掉最后那一段：
        /// 原版页签只有 620px 宽，而这一段是"换个口径说同一件事"，放不下就挂 tooltip
        /// （重排 ≠ 删信息，S8 立的规矩）。
        /// </summary>
        public static string ProgressItemsHeader(Delegation d, List<DelegationPreviewItem> items, string unit,
            bool includeSiteDeposit = true)
        {
            if (ItemsHidden(d))
            {
                // 还没透露：数量写「? 类」，市价合计与存量**一概不写** —— 它们是同一条信息的其余部分，
                // 只把数字换成"?"会显得像"已经知道了、只是值未知"。
                return "现场物资（? 类）";
            }
            string head = string.Format("现场物资（{0} 类）", items?.Count ?? 0);
            float value = 0f;
            bool hasValue = false;
            if (!items.NullOrEmpty())
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] != null && items[i].value > 0f)
                    {
                        value += items[i].value;
                        hasValue = true;
                    }
                }
            }
            if (hasValue)
            {
                // S10：去掉「约」——这一行要在 470px 的主列里和排序按钮共处，省一个字都算数
                head += string.Format(" · 市价合计 {0:0} 银", value);
            }
            if (includeSiteDeposit && d?.deposit != null && !unit.NullOrEmpty())
            {
                head += string.Format(" · 该地点存量 {0}/{1} {2}",
                    d.deposit.UnitsRemaining, d.deposit.totalUnits, unit);
            }
            return head;
        }

        /// <summary>
        /// 「查看全部 N 人」—— 原版 FloatMenu 列出全部参与者（点一条开该殖民者的信息卡）。
        ///
        /// 放在 DelegationUIUtility（而不是皮肤里）是双端准则的要求：皮肤只负责"画"，
        /// 名单、排序、点击行为都由 RimDelegation 给；否则原版与皮肤两处的名单口径会分叉。
        /// </summary>
        public static void OpenParticipantsMenu(Delegation d, DelegationWorker worker, DelegationDef def,
            PawnSortMode sortMode)
        {
            if (d?.participants.NullOrEmpty() ?? true)
            {
                return;
            }
            List<Pawn> list = new List<Pawn>(d.participants);
            SortPawns(list, sortMode, def);
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == null)
                {
                    continue;
                }
                string label = PawnLine(p, worker, def);
                options.Add(new FloatMenuOption(label, delegate
                {
                    Find.WindowStack.Add(new Dialog_InfoCard(p));
                }));
            }
            if (options.Count > 0)
            {
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>
        /// 画"外貌头像 + 一行文字 + 心情/受伤角标 + 原版信息卡按钮"。
        /// row 是整行矩形，头像画在左端，角标贴在信息卡按钮左边。
        /// 头像写法与原版 TransferableUIUtility.DrawOverseerIcon 一致。
        /// </summary>
        public static void DrawPawnLine(Rect row, Pawn p, string text, float portraitSize = PortraitSize)
        {
            Rect portraitRect = new Rect(row.x, row.y + (row.height - portraitSize) * 0.5f, portraitSize, portraitSize);
            GUI.DrawTexture(portraitRect, PortraitsCache.Get(p, new Vector2(portraitSize, portraitSize), Rot4.South));
            TooltipHandler.TipRegion(portraitRect, p.LabelShortCap);

            float infoSize = Widgets.InfoCardButtonSize;
            Rect infoRect = new Rect(row.xMax - infoSize, row.y + (row.height - infoSize) * 0.5f, infoSize, infoSize);

            // 角标：从右往左排 [受伤][休息][心情] (i)
            float badgeY = row.y + (row.height - BadgeSize) * 0.5f;
            Rect injuryRect = new Rect(infoRect.x - BadgeGap - BadgeSize, badgeY, BadgeSize, BadgeSize);
            Rect restRect = new Rect(injuryRect.x - BadgeGap - BadgeSize, badgeY, BadgeSize, BadgeSize);
            Rect moodRect = new Rect(restRect.x - BadgeGap - BadgeSize, badgeY, BadgeSize, BadgeSize);
            DrawMoodBadge(moodRect, p);
            DrawRestBadge(restRect, p);
            DrawInjuryBadge(injuryRect, p);

            Widgets.Label(LabelRectFor(row, portraitSize), text);
            Widgets.InfoCardButton(infoRect.x, infoRect.y, p);
        }

        /// <summary>文字热区（避开角标与信息卡按钮）——调用方拿它做点击/勾选区域。</summary>
        public static Rect LabelRectFor(Rect row, float portraitSize = PortraitSize)
        {
            float infoSize = Widgets.InfoCardButtonSize;
            // ⚠️ 角标个数必须与 `DrawPawnLine` 里画的一致（心情 / 休息 / 受伤 三个）
            float reserved = infoSize + 3f * (BadgeSize + BadgeGap) + 8f;
            float x = row.x + portraitSize + 8f;
            return new Rect(x, row.y, Mathf.Max(40f, row.xMax - reserved - x), row.height);
        }

        /// <summary>
        /// 休息值角标（S27，用户要求：「这几个框除了心情以外，额外添加休息值的显示」）。
        /// 颜色按**剩余休息**分级（与心情同向：高=绿、低=红），tooltip 给百分比与"还能干多久"的提示。
        /// 它与工伤倍率同源（`DelegationUtility.FatigueFactor` 就是看全队平均休息）⇒ 玩家能直接看出"谁快撑不住了"。
        /// </summary>
        public static void DrawRestBadge(Rect rect, Pawn p)
        {
            Need_Rest rest = p?.needs?.rest;
            if (rest == null)
            {
                return;
            }
            float pct = rest.CurLevelPercentage;
            Widgets.DrawBoxSolidWithOutline(rect, MoodColor(pct), Color.black, 1);
            TooltipHandler.TipRegion(rect, string.Format("休息 {0}{1}",
                pct.ToStringPercent(),
                pct < 0.35f ? "（疲劳：工伤风险偏高）" : ""));
        }

        /// <summary>心情角标：颜色分级（绿→黄→橙→红），tooltip 给数值与心情档位。</summary>
        public static void DrawMoodBadge(Rect rect, Pawn p)
        {
            Need_Mood mood = p?.needs?.mood;
            if (mood == null)
            {
                return;
            }
            float pct = mood.CurLevelPercentage;
            Widgets.DrawBoxSolidWithOutline(rect, MoodColor(pct), Color.black, 1);
            TooltipHandler.TipRegion(rect, string.Format("心情 {0}（{1}）", pct.ToStringPercent(), mood.MoodString));
        }

        /// <summary>受伤角标：没伤就不画；倒地用更深的红。</summary>
        public static void DrawInjuryBadge(Rect rect, Pawn p)
        {
            if (p?.health?.hediffSet == null)
            {
                return;
            }
            float pain = p.health.hediffSet.PainTotal;
            int injuries = CountInjuries(p);
            if (injuries <= 0 && pain <= 0.001f && !p.Downed)
            {
                return;
            }
            Color color = p.Downed ? new Color(0.55f, 0.08f, 0.08f) : new Color(0.85f, 0.35f, 0.20f);
            Widgets.DrawBoxSolidWithOutline(rect, color, Color.black, 1);
            string tip = string.Format("受伤 {0} 处 · 疼痛 {1}", injuries, pain.ToStringPercent());
            if (p.Downed)
            {
                tip += " · 已倒地";
            }
            TooltipHandler.TipRegion(rect, tip);
        }

        public static int CountInjuries(Pawn p)
        {
            List<Hediff> hediffs = p?.health?.hediffSet?.hediffs;
            if (hediffs == null)
            {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i] is Hediff_Injury)
                {
                    count++;
                }
            }
            return count;
        }

        private static Color MoodColor(float pct)
        {
            if (pct >= 0.7f)
            {
                return new Color(0.35f, 0.72f, 0.35f);
            }
            if (pct >= 0.45f)
            {
                return new Color(0.74f, 0.74f, 0.30f);
            }
            if (pct >= 0.25f)
            {
                return new Color(0.85f, 0.55f, 0.20f);
            }
            return new Color(0.80f, 0.25f, 0.25f);
        }

        // ---------------------------------------------------------------- 排序
        public static string SortLabel(PawnSortMode mode)
        {
            switch (mode)
            {
                case PawnSortMode.Name:
                    return "名字";
                case PawnSortMode.MoodAsc:
                    return "心情（差→好）";
                default:
                    return "技能（高→低）";
            }
        }

        public static PawnSortMode NextSort(PawnSortMode mode)
        {
            switch (mode)
            {
                case PawnSortMode.SkillDesc:
                    return PawnSortMode.Name;
                case PawnSortMode.Name:
                    return PawnSortMode.MoodAsc;
                default:
                    return PawnSortMode.SkillDesc;
            }
        }

        public static void SortPawns(List<Pawn> pawns, PawnSortMode mode, DelegationDef def)
        {
            if (pawns == null || pawns.Count < 2)
            {
                return;
            }
            switch (mode)
            {
                case PawnSortMode.Name:
                    pawns.Sort((a, b) => string.Compare(a.LabelShortCap, b.LabelShortCap, StringComparison.CurrentCulture));
                    break;
                case PawnSortMode.MoodAsc:
                    pawns.Sort((a, b) => MoodPct(a).CompareTo(MoodPct(b)));
                    break;
                default:
                    pawns.Sort((a, b) => SkillLevel(b, def).CompareTo(SkillLevel(a, def)));
                    break;
            }
        }

        public static int SkillLevel(Pawn p, DelegationDef def)
        {
            if (p?.skills == null || def?.skillDef == null)
            {
                return -1;
            }
            SkillRecord rec = p.skills.GetSkill(def.skillDef);
            return rec?.Level ?? -1;
        }

        public static float MoodPct(Pawn p)
        {
            return p?.needs?.mood?.CurLevelPercentage ?? 1f;
        }

        /// <summary>参与者一行文字（技能细节由 worker 提供）。</summary>
        public static string PawnLine(Pawn p, DelegationWorker worker, DelegationDef def)
        {
            string detail = worker?.PawnDetail(p);
            if (detail.NullOrEmpty() && def?.skillDef != null && p.skills != null)
            {
                detail = string.Format("{0} {1}", def.skillDef.LabelCap, p.skills.GetSkill(def.skillDef).Level);
            }
            string extra = null;
            if (p.Downed)
            {
                extra = "（倒地）";
            }
            else if (p.InMentalState)
            {
                extra = "（精神崩溃）";
            }
            string body = detail;
            if (!extra.NullOrEmpty())
            {
                body = body.NullOrEmpty() ? extra : body + " " + extra;
            }
            return body.NullOrEmpty() ? p.LabelShortCap : p.LabelShortCap + "   " + body;
        }
    }
}
