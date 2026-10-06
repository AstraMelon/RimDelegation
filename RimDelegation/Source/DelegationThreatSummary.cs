using System;
using System.Collections.Generic;
using System.Text;
using RimDelegation.Combat;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「作战任务」段的数据（S24 立，S25 加情报门控与自动评估）。
    ///
    /// 为什么要单独一个类：这一段的**两条数据都很贵**，而主列是每帧重绘的 ——
    ///   ① 守军编队要跑 `PawnGroupMakerUtility.GeneratePawns`（真的生成一遍 pawn 再销毁）；
    ///   ② 成算是 200 次蒙特卡洛。
    /// 所以这里做成"**带键的缓存**"：键 =（地点 + 车队 + 姿态 + 参与者名单，含每人健康粗档），
    /// 任何一项变了 ⇒ 下一帧自动重算。
    ///
    /// 单一来源：守军编队的**分组规则**也在这里（<see cref="RosterEntries" />），
    /// 威胁评估面板与主列都调它 —— 免得"面板里是 ×7、主列里是 7 行"这种两套口径。
    /// </summary>
    public sealed class DelegationThreatSummary
    {
        /// <summary>编队里的一行（S25：带图标 ⇒ 两端都画得出"这是人 / 动物 / 炮塔"）。</summary>
        public sealed class RosterEntry
        {
            public ThingDef icon;
            public string name;
            public int count;
            public CombatUnitSnapshot unit;

            /// <summary>短标签（主列用）：`海盗 ×7`。</summary>
            public string Short => count > 1 ? name + " ×" + count : name;

            /// <summary>长明细（评估面板用）：耐久 / 射程 / 命中 / 射速 / 伤害 / 破甲 / 护甲。</summary>
            public string Detail => string.Format(
                "耐久 {0:0} · 射程 {1} · 命中 {2:P0}→{3:P0} · 射速 {4:0.##} · 伤害 {5:0.#} · 破甲 {6:0.##} · 护甲 {7:0.##}",
                unit.MaxHealth, unit.IsMelee ? "近战" : unit.Range.ToString("0.#"),
                unit.AccuracyNear, unit.AccuracyFar, unit.ShotsPerRound,
                unit.DamagePerShot, unit.ArmorPen, unit.ArmorRating);
        }

        // ── 事实（确定性；一次生成，之后不再变）──

        /// <summary>这个地点有没有威胁部件（`SitePartParams.threatPoints &gt; 0`）。</summary>
        public bool hasThreat;

        public int threatPartCount;
        public float totalThreatPoints;

        /// <summary>含**未探明**的威胁部件（物资点是 hiddenSitePartsPossible，矿点不是）。</summary>
        public bool hasHiddenThreat;

        /// <summary>有几个威胁部件无法用无地图模型评估（需进图清剿）。</summary>
        public int unassessableParts;

        public bool canAssess;

        /// <summary>不能评估的原因（`CombatSetup.BlockReason()`）；能评估时为 null。</summary>
        public string blockReason;

        public int ourUnits;
        public int enemyUnits;

        /// <summary>
        /// S25：**守军情报是否已经向玩家揭露**（＝流程里那个带 `revealsThreat` 的段走完了）。
        /// 由调用方每帧注入（`DelegationUIUtility.ThreatRevealed`）—— 草稿页没有流程，
        /// 那边一律按"已揭露"处理（派不派单本来就要看守军）。
        /// </summary>
        public bool revealed = true;

        /// <summary>敌方编队（分组后，带图标）。</summary>
        public readonly List<RosterEntry> roster = new List<RosterEntry>();

        /// <summary>能打的自己人（**真 Pawn** ⇒ 两端都能画头像行）。</summary>
        public readonly List<Pawn> ourPawns = new List<Pawn>();

        /// <summary>我方被排除出战的成员及原因（没武器 / 倒地…）。</summary>
        public readonly List<string> ourNotes = new List<string>();

        /// <summary>编队推算过程（面板用；主列只给条数）。</summary>
        public readonly List<string> enemyNotes = new List<string>();

        /// <summary>
        /// 「本趟流程里有没有交战段、打过了没有」那一行（S23 的数据，由调用方注入）。
        /// </summary>
        public string flowLine;

        // ── 成算 ──

        public bool forecastReady;
        public ForecastResult forecast;
        public int seed = 4242;

        /// <summary>成算用的同一张战场（不存档、不进 Def，只是一组快照）。</summary>
        private CombatScene scene;

        /// <summary>已经自动试过一次成算（S25：侦察完成后**自动**算一次；失败也不每帧重试）。</summary>
        private bool forecastAttempted;

        private string key;
        private bool computed;

        /// <summary>已经把事实算出来过（无论成算有没有算）。</summary>
        public bool Computed => computed;

        /// <summary>
        /// 保证数据可用：键变了就重建事实；<paramref name="requestForecast" /> = true 时**自动算一次**成算
        /// （S25：侦察完成后由调用方传 true；已经算过就不会再算，除非点「重新推算」）。
        ///
        /// 每帧调用是安全的 —— 键没变时它什么都不做（这正是"缓存"的意义）。
        /// </summary>
        public void Ensure(Caravan caravan, Site site, List<Pawn> participants,
            DelegationApproachDef approach, bool requestForecast, List<Pawn> excludeFromCombat = null)
        {
            string nowKey = MakeKey(caravan, site, approach, participants, excludeFromCombat);
            bool changed = !computed || key != nowKey;
            if (changed)
            {
                Build(caravan, site, participants, approach, excludeFromCombat);
                key = nowKey;
                forecastReady = false;
                forecastAttempted = false;
            }
            if (requestForecast && !forecastReady && !forecastAttempted)
            {
                forecastAttempted = true;   // 先记后跑：失败也不每帧重试（"静默失败"至少不会变成卡帧）
                RunForecast();
            }
        }

        /// <summary>重算事实并**重掷成算种子**（「重新推算」按钮：玩家想要另一组采样时用）。</summary>
        public void Reroll(Caravan caravan, Site site, List<Pawn> participants, DelegationApproachDef approach,
            List<Pawn> excludeFromCombat = null)
        {
            seed = Rand.RangeInclusive(1, 999999);
            computed = false;
            forecastAttempted = false;
            Ensure(caravan, site, participants, approach, true, excludeFromCombat);
        }

        /// <summary>
        /// 缓存键。参与者那一段带上**健康粗档**（0–100）：人受伤/倒地之后成算本来就该变，
        /// 而 `thingIDNumber` 单独用是发现不了这件事的。
        /// </summary>
        private static string MakeKey(Caravan caravan, Site site, DelegationApproachDef approach, List<Pawn> pawns,
            List<Pawn> exclude)
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append(site?.ID ?? -1).Append('|').Append(caravan?.ID ?? -1).Append('|')
              .Append(approach?.defName ?? "-").Append('|');
            if (pawns != null)
            {
                sb.Append(pawns.Count);
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p == null) continue;
                    int hp = p.health?.summaryHealth != null
                        ? Mathf.RoundToInt(p.health.summaryHealth.SummaryHealthPercent * 100f)
                        : 0;
                    sb.Append(',').Append(p.thingIDNumber).Append(':').Append(hp);
                }
            }
            // S27：**"不参战"名单也是键的一部分** —— 玩家勾一下就要重算战场与成算。
            sb.Append("|x:");
            if (exclude != null)
            {
                for (int i = 0; i < exclude.Count; i++)
                {
                    if (exclude[i] != null) sb.Append(exclude[i].thingIDNumber).Append(',');
                }
            }
            return sb.ToString();
        }

        private void Build(Caravan caravan, Site site, List<Pawn> participants, DelegationApproachDef approach,
            List<Pawn> excludeFromCombat)
        {
            computed = true;
            scene = null;
            forecast = null;
            forecastReady = false;
            roster.Clear();
            ourPawns.Clear();
            ourNotes.Clear();
            enemyNotes.Clear();
            canAssess = false;
            blockReason = null;
            ourUnits = 0;
            enemyUnits = 0;

            hasThreat = ThreatAssessmentEntry.HasThreat(site);
            threatPartCount = ThreatAssessmentEntry.ThreatPartCount(site);
            totalThreatPoints = ThreatAssessmentEntry.TotalThreatPoints(site);
            hasHiddenThreat = ThreatAssessmentEntry.HasHiddenThreat(site);
            unassessableParts = ThreatAssessmentEntry.UnassessablePartCount(site);

            if (site == null || !hasThreat)
            {
                return;
            }

            // RIM-27 归一：与营救清场 / 交战段 / 对话框走**同一个纯函数**。
            // 旧写法漏了 `stealth` 条件 ⇒ 囚犯营救选「强攻」时这里按 1.0 算，而真结算按 0 算，
            // 主列比真打少吃一轮敌方火力（违背"预告即契约" §19.12）。
            float penalty = DelegationApproachDef.FirstStrikePenalty(approach);

            CombatSetup setup;
            try
            {
                setup = ThreatAssessmentEntry.Build(caravan, site, penalty, excludeFromCombat);
            }
            catch (Exception ex)
            {
                // 画布抛异常 = 整个面板白屏，所以这里必须兜住（与事件、预览列表同一条规矩）
                blockReason = "评估失败（" + ex.GetType().Name + "）";
                Log.WarningOnce("[RimDelegation] 构建战场失败：" + ex.Message, 0x5E0E1);
                return;
            }

            scene = setup.Scene;
            canAssess = setup.CanAssess;
            blockReason = setup.BlockReason();
            ourUnits = scene?.CountMine() ?? 0;
            enemyUnits = scene?.CountEnemies() ?? 0;
            // 主列只列**敌方**编队；我方走"参战人员"那一段（那里是真 Pawn，能画头像）
            roster.AddRange(RosterEntries(scene, false));
            ourPawns.AddRange(setup.OurPawns);
            ourNotes.AddRange(setup.OurNotes);
            enemyNotes.AddRange(setup.EnemyNotes);
        }

        private void RunForecast()
        {
            if (!canAssess || scene == null)
            {
                return;
            }
            try
            {
                forecast = Forecast.Run(scene, CombatTuning.ForecastIterations, seed, s => new RandRng(s));
                forecastReady = forecast != null;
            }
            catch (Exception ex)
            {
                forecast = null;
                forecastReady = false;
                Log.WarningOnce("[RimDelegation] 作战任务段的成算失败：" + ex.Message, 0x5E0E2);
            }
        }

        /// <summary>
        /// 「情报未明」时的那一行（S25）。用户口径：「在侦察完成前，守军不会向玩家揭露」
        /// 「完成侦察任务后，向玩家揭露敌人编队」。
        /// </summary>
        public const string IntelHiddenLine = "守军情报未明 —— 完成侦察后揭晓。";

        /// <summary>
        /// 这一段的**文字行**（段头由两端各自画；段名在 <see cref="DelegationUIUtility.SectionCombat" /> 里）。
        ///
        /// ⚠️ **恒常**（用户 S24 拍板）：没有守军时返回一行状态，绝不返回空。
        /// ⚠️ **情报门控**（用户 S25）：<see cref="revealed" /> = false 时只给一行"未明"。
        /// </summary>
        public List<string> Lines()
        {
            List<string> lines = new List<string>();

            if (!hasThreat)
            {
                lines.Add("此地没有守军 —— 本趟不涉及作战。");
                if (!flowLine.NullOrEmpty())
                {
                    lines.Add(flowLine);
                }
                return lines;
            }

            if (!revealed)
            {
                lines.Add(IntelHiddenLine);
                if (!flowLine.NullOrEmpty())
                {
                    lines.Add(flowLine);
                }
                return lines;
            }

            StringBuilder head = new StringBuilder();
            head.AppendFormat("守军：威胁点数 {0:0} · {1} 个威胁部件", totalThreatPoints, threatPartCount);
            if (hasHiddenThreat)
            {
                head.Append(" · 含未探明的威胁");
            }
            lines.Add(head.ToString());

            if (unassessableParts > 0)
            {
                lines.Add(string.Format("其中 {0} 个无法无地图评估（需进入地图清剿）", unassessableParts));
            }
            if (!roster.NullOrEmpty())
            {
                StringBuilder sb = new StringBuilder("编队：");
                for (int i = 0; i < roster.Count; i++)
                {
                    if (i > 0) sb.Append(" · ");
                    sb.Append(roster[i].Short);
                }
                lines.Add(sb.ToString());
            }
            lines.Add(canAssess
                ? string.Format("我方可战 {0} 人{1}", ourUnits,
                    ourNotes.Count > 0 ? string.Format("（另有 {0} 人被排除，见评估面板）", ourNotes.Count) : "")
                : string.Format("我方可战 {0} 人{1}", ourUnits,
                    ourNotes.Count > 0 ? string.Format("（另有 {0} 人被排除）", ourNotes.Count) : ""));
            if (!canAssess && !blockReason.NullOrEmpty())
            {
                lines.Add("⚠ " + blockReason);
            }
            if (!flowLine.NullOrEmpty())
            {
                lines.Add(flowLine);
            }

            if (canAssess)
            {
                if (forecastReady && forecast != null)
                {
                    lines.Add(string.Format("成算：成功率 {0} · 撤退率 {1} · 失败率 {2}",
                        forecast.WinRate.ToString("P0"), forecast.RetreatRate.ToString("P0"),
                        forecast.DefeatRate.ToString("P0")));
                    lines.Add(string.Format("用时 P50 {0:0.#}h / P90 {1:0.#}h · 伤员 {2}/{3} · 阵亡 {4}/{5}",
                        forecast.DaysP50 * 24f, forecast.DaysP90 * 24f,
                        forecast.CasualtiesP50, forecast.CasualtiesP90,
                        forecast.DeathsP50, forecast.DeathsP90));
                }
                else
                {
                    lines.Add("成算：尚未推算（点「重新推算」）");
                }
            }
            return lines;
        }

        // ================================================================ 编队分组（单一来源）

        /// <summary>
        /// 把战场上的单位按"同名 + 同关键数值"分组合并成行（`迷你炮塔 ×7`）。
        ///
        /// **威胁评估面板与主列都走这一份**（S24 去重）：面板用 <see cref="RosterEntry.Detail" />，
        /// 主列只用 <see cref="RosterEntry.Short" />（那一列只有约 468px）。
        /// </summary>
        public static List<RosterEntry> RosterEntries(CombatScene scene, bool mine)
        {
            List<RosterEntry> entries = new List<RosterEntry>();
            if (scene?.Units == null)
            {
                return entries;
            }

            for (int i = 0; i < scene.Units.Count; i++)
            {
                CombatUnitSnapshot u = scene.Units[i];
                if (u == null || u.IsMine != mine) continue;

                RosterEntry found = null;
                for (int e = 0; e < entries.Count; e++)
                {
                    if (SameLine(entries[e].unit, u)) { found = entries[e]; break; }
                }
                if (found != null)
                {
                    found.count++;
                }
                else
                {
                    entries.Add(new RosterEntry
                    {
                        icon = IconDefByName(u.IconDefName),
                        name = u.Name,
                        count = 1,
                        unit = u,
                    });
                }
            }
            return entries;
        }

        /// <summary>
        /// RIM-35：把核心快照里的 `IconDefName` 还原成真 `ThingDef`（`ThingDef.uiIcon` 用来画编队行图标）。
        ///
        /// 核心（`Source/Combat/Core`）不许出现 `Verse` 类型，否则 `Prototype` / `CombatLab` 两个离线工程
        /// 直接 CS0246（它们**刻意不引用 RimWorld / Unity**）—— 所以"名字 → Def"这一步只能在游戏侧做。
        /// 用 `GetNamedSilentFail`：拿不到就给 null，两端画法都已有 `icon != null` 判空。
        /// </summary>
        private static ThingDef IconDefByName(string defName)
        {
            if (defName.NullOrEmpty()) return null;
            return DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        }

        /// <summary>合并判据：名字 + 全部关键数值都要一致，免得把两个恰好同名的不同单位算成一个。</summary>
        private static bool SameLine(CombatUnitSnapshot a, CombatUnitSnapshot b)
        {
            return a.Name == b.Name
                && Mathf.Approximately(a.MaxHealth, b.MaxHealth)
                && Mathf.Approximately(a.Range, b.Range)
                && Mathf.Approximately(a.DamagePerShot, b.DamagePerShot)
                && Mathf.Approximately(a.ShotsPerRound, b.ShotsPerRound)
                && Mathf.Approximately(a.MoveSpeed, b.MoveSpeed)
                && Mathf.Approximately(a.ArmorPen, b.ArmorPen)
                && a.IsMelee == b.IsMelee
                && a.HasTerrainAdvantage == b.HasTerrainAdvantage;
        }

        /// <summary>把编队压成文本行（评估面板与日志用；主列走 <see cref="RosterEntries" /> 画图标）。</summary>
        public static List<string> RosterLines(CombatScene scene, bool mine, bool detailed)
        {
            List<RosterEntry> entries = RosterEntries(scene, mine);
            List<string> lines = new List<string>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                lines.Add(detailed ? "  " + entries[i].Short + "  " + entries[i].Detail : entries[i].Short);
            }
            return lines;
        }
    }
}
