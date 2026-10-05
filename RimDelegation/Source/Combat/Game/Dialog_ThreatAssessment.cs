using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 「威胁评估」对话框 —— 战斗算法在游戏里的第一个可见产物。
    ///
    /// 它做两件事（DESIGN.md §19.12 / §19.22）：
    ///   1. **计算预告**：对真实战场跑 200 次蒙特卡洛，给出成功率 / 伤员 P50·P90 / 用时；
    ///   2. **打一场**：按种子跑一次，展示分类战斗日志（含甲弹对抗明细）。
    ///
    /// 两件事跑的是**同一个** <c>CombatSimulator.Simulate</c> —— 这正是"预告即契约"。
    /// </summary>
    public class Dialog_ThreatAssessment : Window
    {
        private readonly CombatSetup setup;

        private ForecastResult forecast;
        private CombatResult battle;
        private string message = "尚未计算。";
        private string bodyText = "";

        private readonly StringBuilder body = new StringBuilder();
        private Vector2 scroll;
        private int seed = 4242;

        // 日志过滤（打一场后可用）
        private CombatLogFilter logFilter = CombatLogFilter.Default;

        public override Vector2 InitialSize => new Vector2(720f, 660f);

        public Dialog_ThreatAssessment(CombatSetup setup)
        {
            this.setup = setup;
            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard ls = new Listing_Standard();
            ls.Begin(inRect);

            if (setup == null || setup.Scene == null)
            {
                ls.Label("无法构建战场。");
                ls.End();
                return;
            }

            // ── 标题 ──
            Text.Font = GameFont.Medium;
            ls.Label(setup.Site.LabelCap);
            Text.Font = GameFont.Small;

            string reason = setup.BlockReason();
            if (reason != null)
            {
                GUI.color = new Color(1f, 0.6f, 0.4f);
                ls.Label("⚠ " + reason);
                GUI.color = Color.white;
            }

            ls.GapLine();

            // ── 双方编队 ──
            ls.Label(string.Format("我方 {0} 人 · 敌方 {1} 个 · 威胁点数 {2:0}",
                setup.Scene.CountMine(), setup.Scene.CountEnemies(), setup.TotalThreatPoints));
            ls.Gap(4f);

            float listHeight = 118f;
            Rect ourRect = ls.GetRect(listHeight);
            Rect enemyRect = ls.GetRect(listHeight + 4f);
            DrawRoster(ourRect, true);
            DrawRoster(enemyRect, false);

            ls.Gap(4f);

            // ── 按钮 ──
            Rect row = ls.GetRect(32f);
            float w = (row.width - 16f) / 3f;

            if (Widgets.ButtonText(new Rect(row.x, row.y, w, 30f), "计算预告（" + CombatTuning.ForecastIterations + " 次）"))
                RunForecast();
            if (Widgets.ButtonText(new Rect(row.x + w + 8f, row.y, w, 30f), "打一场（种子 " + seed + "）"))
                RunBattle();
            if (Widgets.ButtonText(new Rect(row.x + (w + 8f) * 2f, row.y, w, 30f), "换个种子"))
            {
                seed = Rand.RangeInclusive(1, 999999);
                message = "已换种子：" + seed;
            }

            ls.Gap(2f);

            // ── 日志过滤（只在打过之后有意义） ──
            if (battle != null)
            {
                Rect filterRow = ls.GetRect(28f);
                float x = filterRow.x;
                DrawFilterToggle(ref x, filterRow.y, "开局", CombatLogFilter.Setup);
                DrawFilterToggle(ref x, filterRow.y, "移动", CombatLogFilter.Move);
                DrawFilterToggle(ref x, filterRow.y, "攻击", CombatLogFilter.Attack);
                DrawFilterToggle(ref x, filterRow.y, "受击", CombatLogFilter.Hit);
                DrawFilterToggle(ref x, filterRow.y, "状态", CombatLogFilter.Status);
                DrawFilterToggle(ref x, filterRow.y, "结局", CombatLogFilter.Outcome);
                if (Widgets.ButtonText(new Rect(x + 4f, filterRow.y + 3f, 46f, 22f), "全选"))
                    logFilter = CombatLogFilter.All;
                if (Widgets.ButtonText(new Rect(x + 54f, filterRow.y + 3f, 46f, 22f), "默认"))
                    logFilter = CombatLogFilter.Default;

                if (GUI.changed) RebuildBody();
            }

            ls.Gap(2f);

            // ── 结果区 ──
            Rect statusRect = ls.GetRect(20f);
            Widgets.Label(statusRect, message);
            if (setup.Unresolved.Count > 0)
            {
                Rect warnRect = ls.GetRect(20f);
                GUI.color = new Color(1f, 0.7f, 0.4f);
                Widgets.Label(warnRect, "未处理威胁：" + string.Join("、", setup.Unresolved));
                GUI.color = Color.white;
            }

            Rect outRect = ls.GetRect(inRect.height - ls.CurHeight - 44f);
            DrawScrollable(outRect);

            ls.Gap(4f);
            Rect closeRow = ls.GetRect(30f);
            if (Widgets.ButtonText(new Rect(closeRow.xMax - 120f, closeRow.y, 120f, 28f), "关闭"))
                Close();

            ls.End();
        }

        private void DrawFilterToggle(ref float x, float y, string label, CombatLogFilter bit)
        {
            bool on = (logFilter & bit) != 0;
            Rect r = new Rect(x, y, 56f, 24f);
            bool next = on;
            Widgets.CheckboxLabeled(r, label, ref next);
            if (next != on)
            {
                logFilter = next ? (logFilter | bit) : (logFilter & ~bit);
                GUI.changed = true;
            }
            x += 58f;
        }

        private void DrawRoster(Rect rect, bool mine)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(4f);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(mine ? "我方（进攻方，无地形优势）" : "敌方（守军，有地形优势）");

            // S24：分组与行文案搬到 `DelegationThreatSummary.RosterLines` ——
            // 主列那段「作战任务」与这张面板共用同一份，免得出现"面板 ×7、主列 7 行"。
            List<string> lines = DelegationThreatSummary.RosterLines(setup.Scene, mine, true);
            for (int i = 0; i < lines.Count; i++)
            {
                sb.AppendLine(lines[i]);
            }
            Widgets.Label(inner, sb.ToString().TrimEndNewlines());
        }

        private void DrawScrollable(Rect outRect)
        {
            Widgets.DrawMenuSection(outRect);
            Rect inner = outRect.ContractedBy(4f);
            float h = Mathf.Max(inner.height, Text.CalcHeight(bodyText, inner.width) + 8f);
            Rect view = new Rect(0f, 0f, inner.width - 16f, h);
            Widgets.BeginScrollView(inner, ref scroll, view);
            Widgets.Label(view, bodyText);
            Widgets.EndScrollView();
        }

        // ── 运行 ────────────────────────────────────────────────────────

        private void RunForecast()
        {
            try
            {
                forecast = Forecast.Run(setup.Scene, CombatTuning.ForecastIterations, seed,
                                        s => new RandRng(s));
                message = string.Format("预告完成（{0} 次）· 成功率 {1}",
                    CombatTuning.ForecastIterations, forecast.WinRate.ToString("P0"));
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 威胁评估：预告失败 " + ex);
                message = "预告失败：" + ex.Message;
            }
            RebuildBody();
        }

        private void RunBattle()
        {
            try
            {
                setup.Scene.LogFilter = logFilter == CombatLogFilter.None ? CombatLogFilter.Default : logFilter;
                using (RandRng rng = new RandRng(seed))
                {
                    battle = CombatSimulator.Simulate(setup.Scene, rng);
                }
                message = "seed=" + seed + " · " + battle.Summary();
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 威胁评估：战斗失败 " + ex);
                message = "战斗失败：" + ex.Message;
            }
            RebuildBody();
        }

        /// <summary>把预告与战报拼成要显示的文本。</summary>
        private void RebuildBody()
        {
            body.Length = 0;

            if (!setup.CanAssess)
            {
                body.AppendLine("【无法评估】");
                body.AppendLine("  " + setup.BlockReason());
                body.AppendLine();
            }

            if (forecast != null)
            {
                body.AppendLine("【预告】");
                body.AppendLine("  成功率（威胁解除）  " + forecast.WinRate.ToString("P0"));
                body.AppendLine("  撤退率 " + forecast.RetreatRate.ToString("P0") +
                                "    失败率 " + forecast.DefeatRate.ToString("P0"));
                body.AppendLine("  预计用时   P50 " + (forecast.DaysP50 * 24f).ToString("0.0") +
                                " 小时 / P90 " + (forecast.DaysP90 * 24f).ToString("0.0") + " 小时");
                body.AppendLine("  伤员       P50 " + forecast.CasualtiesP50 +
                                " / P90 " + forecast.CasualtiesP90 + " / 最坏 " + forecast.CasualtiesMax);
                body.AppendLine("  阵亡       P50 " + forecast.DeathsP50 +
                                " / P90 " + forecast.DeathsP90 + " / 最坏 " + forecast.DeathsMax);
                body.AppendLine("  结局多为   " + forecast.ModalOutcome);
                body.AppendLine();
            }

            if (battle != null)
            {
                body.AppendLine("【战斗日志】过滤：" + CombatLogUtility.Describe(logFilter) +
                                "（共 " + battle.Entries.Count + " 条，其中攻击 " + battle.CountOf(CombatLogKind.Attack) +
                                " / 受击 " + battle.CountOf(CombatLogKind.Hit) +
                                " / 移动 " + battle.CountOf(CombatLogKind.Move) +
                                " / 状态 " + battle.CountOf(CombatLogKind.Status) + "）");
                List<string> lines = battle.LogLines(logFilter);
                for (int i = 0; i < lines.Count; i++) body.AppendLine("  " + lines[i]);
                body.AppendLine();
                body.AppendLine("【逐人终局】");
                for (int i = 0; i < battle.Units.Count; i++)
                {
                    UnitReport u = battle.Units[i];
                    body.Append("  ").Append(u.IsMine ? "[我] " : "[敌] ").Append(u.Name.PadRight(10))
                        .Append(u.Dead ? "阵亡" : (u.Downed ? "倒地" : "存活"))
                        .Append("  耐久 ").Append(u.HealthEnd.ToString("0.#")).Append("/").Append(u.HealthStart.ToString("0.#"))
                        .Append("  纵深 ").Append(u.FinalStandoff.ToString("0.#"))
                        .AppendLine();
                }
                body.AppendLine();
            }

            body.AppendLine("【评估假设】（无地图战斗里无法确定、只能设定的量，见 DESIGN.md §19.19）");
            body.Append(CombatSceneFactory.DescribeAssumptions());

            if (setup.EnemyNotes.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("【敌方编队推算过程】");
                for (int i = 0; i < setup.EnemyNotes.Count; i++) body.AppendLine("  · " + setup.EnemyNotes[i]);
            }
            if (setup.OurNotes.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("【我方被排除的成员】");
                for (int i = 0; i < setup.OurNotes.Count; i++) body.AppendLine("  · " + setup.OurNotes[i]);
            }

            bodyText = body.ToString();
            scroll = Vector2.zero;
        }
    }
}
