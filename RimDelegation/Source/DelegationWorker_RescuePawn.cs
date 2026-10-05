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
    /// 「救人」委派：把事件点上的人（难员 / 囚犯）救出来，全程不进地图。
    ///
    /// 难员与囚犯**共用这一个 worker** —— 两者的差别全部是数据：
    ///   · 囚犯营救的守卫 100% 是炮塔（`PrisonerRescueQuestThreat` 池只有 `Turrets`，且
    ///     `siteThreatChance = 1`），而牢房在进图时是未探明的（`GenStep_PrisonerWillingToJoin`
    ///     末尾 `map.fogGrid.Refog(...)`）⇒ 所以那条 def 挂「强攻 + 潜入」两个姿态；
    ///   · 难员躺在露天地里、倒地且双腿不能动，威胁是 7 选 1（70% 概率有）⇒ 只挂「强攻」。
    ///
    /// 引擎侧的全部依据（`part.things[0]` 存人、原版加入路径、倒地者能进车队）见
    /// <see cref="RescueUtility"/> 的类注释 —— 那里逐条写了反编译结论。
    ///
    /// 进度单位 = **百分点**（0..100）。复用宿主的 `totalCells / cellsMined`，
    /// 于是 `TargetDepleted`（= 干完了）与 `destroyTargetOnComplete`（销毁地点 ⇒
    /// 触发任务里的 `QuestNode_NoWorldObject` 收尾）都原样可用。
    /// </summary>
    public class DelegationWorker_RescuePawn : DelegationWorker
    {
        /// <summary>进度单位是"点"（0..100 的百分比刻度）。</summary>
        public override string UnitName => "点";

        public override string WorkVerb => "救";

        /// <summary>句子里的活动名（"委派营救中" / "恢复营救" / "饿着也继续营救"）。</summary>
        public override string ActivityName => "营救";

        /// <summary>产出量纲 = 人（这条委派的"产出"就是救出来的人）。</summary>
        public override string OutputUnitName => "人";

        /// <summary>取尽那一档的文案。"救空为止"不是中文，所以整句换成"救出为止"。</summary>
        public override string UntilDepletedLabel => "救出为止";

        /// <summary>状态行 Keyed 键后缀（`RimDelegationCaravanDelegating_Rescue`）。</summary>
        public override string StatusKeySuffix => "Rescue";

        /// <summary>被救的人不是"可被事件放大的抽象规模"，保持继承的 false。</summary>
        public override bool AllowsScaleIncrease => false;

        // ── 目标与预览 ──────────────────────────────────────────────────

        public static Pawn Target(Site site, DelegationDef def)
        {
            return RescueUtility.TargetPawn(site, def);
        }

        /// <summary>事件点上还有没有可救的人（`CanStart` 之外的一个快速判据）。</summary>
        public static bool HasTarget(Site site, DelegationDef def)
        {
            Pawn p = Target(site, def);
            return p != null && !p.Dead;
        }

        public override Texture2D GetGizmoIcon(Site site)
        {
            return Target(site, def)?.def?.uiIcon;
        }

        /// <summary>预览是**精确值**：人就在存档里，不用等地图生成。</summary>
        public override DelegationPreview MakePreview(Site site)
        {
            Pawn p = Target(site, def);
            return new DelegationPreview
            {
                resourceDef = null,
                minUnits = (int)RescueUtility.TotalWork,
                maxUnits = (int)RescueUtility.TotalWork,
                yieldPerUnit = 1,
                massPerUnit = 0f,   // 人不是"产出物"，不预告负重
            };
        }

        public override string PreviewLabel(Site site, DelegationPreview preview, DelegationDeposit exactDeposit)
        {
            Pawn p = Target(site, def);
            if (p == null)
            {
                // ③ 已经抵达/清点过却仍然没有人 ⇒ 这**才**叫"没有可救的人了"。
                //    抵达时 RollDeposit 会按原版同一口径把目标掷定并写回，所以此时"空"是确定的结论。
                if (exactDeposit != null)
                {
                    return exactDeposit.workerRolledContents
                        ? "事件点上没有可救的人了：委派抵达时已经清点过这一处，确实是空的。"
                        : "事件点上没有可救的人了：这一处本来就是空的。";
                }

                // ② 还没抵达、又读不到 ⇒ 这是"**未定**"，不是"没有"。
                //
                //    S6 之前的文案直接断言"已经没有可救的人了" —— 那是假话：原版这条目标有两级来源
                //    （SitePart.things → ImportantPawnComp），两级都没有时还会在生成地图的那一刻现生成。
                //    玩家看到的"未出发就说没人"就是这么来的（与 S5-b 物资点把"还没掷"说成"没有"同一个病）。
                return "现场目标未定：还没人到现场确认过，这个点上到底有没有人、是谁，都要等队伍到了才知道。\n" +
                       "下达委派后，队伍抵达时会确认这个人的身份并记住他 —— 到时候是谁，进图看到的也是谁。";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(p.LabelShortCap).Append(" · ").Append(p.KindLabel);
            if (p.ageTracker != null)
            {
                sb.Append(" · ").Append(p.ageTracker.AgeBiologicalYears).Append(" 岁");
            }
            sb.Append(" · 健康 ").Append(p.health?.summaryHealth?.SummaryHealthPercent.ToStringPercent() ?? "?");
            if (p.Downed) sb.Append(" · **倒地**");

            // 两类目标的叙事差别
            if (IsPrisonerSite(site, def))
            {
                sb.Append("\n被关在牢房里（进图时牢房是**未探明**的），守卫是固定炮塔。");
            }
            else
            {
                sb.Append("\n因重伤倒地、双腿无法行动 —— 需要抬走，不会自己走。");
            }

            // 人还在原版第 ② 级（地点组件）里时，交代清楚抵达那一刻会发生什么
            if (RescueUtility.TargetPart(site, def) == null)
            {
                sb.Append("\n这位在原版里挂在地点组件上（进图时才取出）；委派抵达时会按同一口径把人接到事件点上。");
            }

            string guard = ThreatAssessmentEntry.SummaryLine(site);
            if (!guard.NullOrEmpty())
            {
                sb.Append("\n").Append(guard);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 「预期获得」列表（S6）：救人的"产出"就是这个人本身 —— 一行，图标用头像，
        /// 副标题写清身份与健康度。读不到目标时返回 null（由 PreviewLabel 去解释"未定"）。
        /// </summary>
        public override List<DelegationPreviewItem> PreviewItems(Site site, DelegationPreview preview,
            DelegationDeposit exactDeposit)
        {
            Pawn p = Target(site, def);
            if (p == null)
            {
                return null;
            }
            string role = IsPrisonerSite(site, def) ? "囚犯" : "无行动能力难民";
            string state = p.Dead ? "**已死亡**" : (p.Downed ? "倒地（需抬走）" : "可自行行动");
            DelegationPreviewItem item = new DelegationPreviewItem
            {
                thingDef = null,
                pawn = p,
                label = p.LabelShortCap.ToString(),
                detail = string.Format("{0} · {1} · 健康 {2} · 产出 1 人",
                    role, state, p.health?.summaryHealth?.SummaryHealthPercent.ToStringPercent() ?? "?")
            };
            return new List<DelegationPreviewItem> { item };
        }

        public override string PawnDetail(Pawn p)
        {
            if (p == null)
            {
                return null;
            }
            DealWithSkill(p, out int best, out string label);
            return string.Format("专业度 {0}（{1}）", best, label);
        }

        /// <summary>取这个人在"姿态相关技能"里的最高等级，用于进度与潜入判定。</summary>
        private void DealWithSkill(Pawn p, out int best, out string label)
        {
            best = 0;
            label = "无";
            List<SkillDef> skillDefs = RelevantSkillDefs();
            if (p?.skills == null || skillDefs.NullOrEmpty())
            {
                return;
            }
            for (int i = 0; i < skillDefs.Count; i++)
            {
                SkillDef sd = skillDefs[i];
                if (sd == null) continue;
                int lvl = p.skills.GetSkill(sd)?.Level ?? 0;
                if (lvl > best)
                {
                    best = lvl;
                    label = sd.LabelCap.ToString();
                }
            }
        }

        private List<SkillDef> RelevantSkillDefs()
        {
            if (!def.approaches.NullOrEmpty())
            {
                for (int i = 0; i < def.approaches.Count; i++)
                {
                    DelegationApproachDef a = def.approaches[i];
                    if (a != null && !a.skillDefs.NullOrEmpty())
                    {
                        return a.skillDefs;
                    }
                }
            }
            return null;
        }

        // ── 速率与估算 ──────────────────────────────────────────────────

        private static int BestSkillOf(List<Pawn> pawns, DelegationDef def)
        {
            List<SkillDef> skillDefs = null;
            if (!def.approaches.NullOrEmpty())
            {
                for (int i = 0; i < def.approaches.Count; i++)
                {
                    DelegationApproachDef a = def.approaches[i];
                    if (a != null && !a.skillDefs.NullOrEmpty()) { skillDefs = a.skillDefs; break; }
                }
            }
            return RescueUtility.BestSkillLevel(pawns, skillDefs);
        }

        private static int UsableCount(List<Pawn> pawns)
        {
            if (pawns == null) return 0;
            int n = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p != null && !p.Dead && !p.Downed) n++;
            }
            return n;
        }

        public override float EstimatedUnitsPerDay(Delegation d, PlanetTile tile)
        {
            if (d == null) return 0f;
            return DayRate(d.participants, d.mode, def);
        }

        public override float EstimateUnitsPerDayFor(List<Pawn> pawns, DelegationModeDef mode, PlanetTile tile, Site site = null)
        {
            return DayRate(pawns, mode, def);
        }

        private static float DayRate(List<Pawn> pawns, DelegationModeDef mode, DelegationDef def)
        {
            if (mode == null) return 0f;
            float perHour = RescueUtility.PercentPerWorkHour(UsableCount(pawns), BestSkillOf(pawns, def), mode);
            return perHour * 24f * mode.WorkFractionPerDay;
        }

        // ── 作战姿态 ────────────────────────────────────────────────────

        public override float ApproachFirstStrikePenalty(DelegationApproachDef approach)
        {
            if (approach == null || !approach.stealth || !approach.guardsGetFirstStrike)
            {
                return 0f;
            }
            return approach.firstStrikeFactor;
        }

        public override string ApproachForecast(Site site, List<Pawn> pawns, DelegationApproachDef approach)
        {
            if (approach == null)
            {
                return null;
            }
            if (!approach.stealth)
            {
                return "强攻：按战斗结算，建议先用「威胁评估」看成功率与伤亡区间";
            }

            int guards = RescueUtility.GuardUnitCount(site);
            int skill = RescueUtility.BestSkillLevel(pawns, approach.skillDefs);
            float exposure = RescueUtility.StealthExposure(site, pawns, approach);
            string s = string.Format("潜入：暴露概率 {0}（守卫 {1} · 专业度 {2} · {3} 人）",
                exposure.ToStringPercent(), guards, skill, UsableCount(pawns));
            if (approach.guardsGetFirstStrike)
            {
                s += "\n　被发现则转入强攻，且**守军先手一轮**（我方开局耐久下降，威胁评估里已计入）";
            }
            return s;
        }

        // ── worker 生命周期 ─────────────────────────────────────────────

        /// <summary>
        /// 只在事件点上调用一次（抵达时）。这里**不掷骰**复刻战斗或规模 —— 目标是存档里的既有实物。
        ///
        /// 但 S6 补了一步"归一"：原版读目标是两级优先级
        /// （`SitePart.things` → `ImportantPawnComp.pawn` → 生成地图时现生成），
        /// 人在后两级时我们**读不到**（第 ② 级其实读得到，但进图后会因 comp 的 PostDestroy
        /// 把已救出的人一起清掉，所以必须搬到第 ① 级；第 ③ 级则按原版同一口径现生成并写回）。
        /// 详见 <see cref="RescueUtility.TryEnsureTarget" />。
        /// </summary>
        public override void RollDeposit(DelegationDeposit dep, Site site)
        {
            if (dep == null)
            {
                return;
            }
            bool ok = RescueUtility.TryEnsureTarget(site, def, out string report, out bool generated);
            // 这个标记的语义是"这份目标/清单是委派替原版掷的"（物资点与营救点共用）：
            // 只有"两级来源都空、我们现造了一个人"时才是 true —— 原有目标绝不能打这个标。
            dep.workerRolledContents = ok && generated;
            DelegationUtility.LogVerbose(
                $"救援点目标归一：{site?.Label} → {(ok ? report : "失败：" + report)}");

            Pawn p = Target(site, def);
            dep.resourceDef = null;
            dep.totalUnits = (p != null && !p.Dead) ? 1 : 0;   // 就一个人
            dep.rolledUnits = 0f;
            dep.yieldPerUnit = 1;
        }

        public override void OnStart(Delegation d, Site site)
        {
            if (d == null)
            {
                return;
            }
            d.totalCells = (int)RescueUtility.TotalWork;
            d.yieldPerCell = 1;
            d.cellsMined = 0f;
            d.workerStage = RescueUtility.StagePending;
            d.workerAbortReason = null;
            if (d.deposit != null)
            {
                d.deposit.timesDelegated++;
            }
        }

        public override void Tick(Delegation d, Site site, int delta)
        {
            if (d == null || delta <= 0)
            {
                return;
            }
            if (Target(site, def) == null)
            {
                // 人已经不在事件点上了（被别的来源取走 / 已死亡）：
                // 以实物为准收工，别空转。
                if (d.workerStage != RescueUtility.StageExtracted)
                {
                    d.workerAbortReason = "事件点上已经没有可救的人了";
                    d.cellsMined = Mathf.Min(d.cellsMined, RescueUtility.TotalWork - 0.1f);
                }
                return;
            }

            // ── 阶段 1：清场（只做一次）──
            if (d.workerStage == RescueUtility.StagePending)
            {
                StringBuilder report = new StringBuilder();
                RescueUtility.ResolveClearance(d, site, d.caravan, d.approach,
                    def.casualtiesArePermanent, report);
                SendClearanceLetter(d, site, report);
                if (d.workerStage == RescueUtility.StageAssaultLost)
                {
                    return;   // 下一次 TickDelegation 会走 WorkerAbortReason
                }
            }

            if (d.workerStage == RescueUtility.StageAssaultLost)
            {
                return;
            }

            // ── 阶段 2：救治 / 破门 / 抬运 ──
            float rate = RescueUtility.PercentPerWorkHour(UsableCount(d.participants),
                BestSkillOf(d.participants, def), d.mode);
            float hours = delta / (float)Delegation.TicksPerHour;
            float before = d.cellsMined;
            d.cellsMined = Mathf.Min(RescueUtility.TotalWork, d.cellsMined + rate * hours);

            // ── 阶段 3：干完的那一刻立刻尝试抬人，成败决定"完成"还是"中断"──
            if (d.cellsMined >= RescueUtility.TotalWork && d.workerStage != RescueUtility.StageExtracted)
            {
                TryExtract(d, site);
            }
        }

        /// <summary>
        /// 抬人。成功 ⇒ 让 `TargetDepleted` 成立（宿主会发完成信并销毁地点）；
        /// 失败 ⇒ 把进度压回 99.x 并置中断理由，让地点**保留**（人还在原地，可以再来）。
        /// </summary>
        private void TryExtract(Delegation d, Site site)
        {
            Caravan caravan = d.caravan;
            if (caravan == null || caravan.Destroyed)
            {
                d.workerAbortReason = "远行队已不存在，无法接人";
                ClampBelowComplete(d);
                return;
            }

            Pawn recruiter = BestRecruiter(d.participants);
            string report;
            if (RescueUtility.ExtractAndRecruit(site, def, caravan, recruiter, out report))
            {
                d.workerStage = RescueUtility.StageExtracted;
                d.workerAbortReason = null;
                d.extractionReport = report;
                if (d.deposit != null)
                {
                    d.deposit.unitsMined += 1;
                }
            }
            else
            {
                d.workerAbortReason = "接人失败：" + report;
                ClampBelowComplete(d);
            }
        }

        private static void ClampBelowComplete(Delegation d)
        {
            d.cellsMined = Mathf.Min(d.cellsMined, RescueUtility.TotalWork - 0.1f);
        }

        /// <summary>入伙登记的"招募者"——取社交最高的人，让原版的意见/历史记在合理的人头上。</summary>
        private static Pawn BestRecruiter(List<Pawn> pawns)
        {
            if (pawns == null)
            {
                return null;
            }
            Pawn best = null;
            int bestSocial = int.MinValue;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p?.skills == null || p.Dead) continue;
                int social = p.skills.GetSkill(SkillDefOf.Social)?.Level ?? 0;
                if (social > bestSocial)
                {
                    bestSocial = social;
                    best = p;
                }
            }
            return best;
        }

        private void SendClearanceLetter(Delegation d, Site site, StringBuilder report)
        {
            if (report == null || report.Length == 0)
            {
                return;
            }
            bool lost = d.workerStage == RescueUtility.StageAssaultLost;
            LetterDef letterDef = lost ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent;
            string label = lost ? "委派清场失败" : "委派清场完成";
            string text = "在 " + (site?.Label ?? "目标地点") + " 的清场结算：\n\n" + report.ToString().TrimEndNewlines();
            try
            {
                Find.LetterStack.ReceiveLetter(label, text, letterDef, new LookTargets(site));
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 清场信件发送失败：" + ex.Message, 0x5E0CC);
            }
        }

        public override string WorkerAbortReason(Delegation d, Site site)
        {
            return d?.workerAbortReason;
        }

        public override string DepletedReason(Delegation d)
        {
            return "人员已救出";
        }

        public override int FlushDeliveries(Delegation d, Caravan caravan)
        {
            return 0;   // 没有"产出缓冲"，人被直接抬进车队
        }

        // ── UI ──────────────────────────────────────────────────────────

        public override string ProgressLabel(Delegation d)
        {
            if (d == null)
            {
                return null;
            }
            string stage;
            switch (d.workerStage)
            {
                case RescueUtility.StagePending: stage = "尚未清场"; break;
                case RescueUtility.StageStealthOk: stage = "潜入成功，未交战"; break;
                case RescueUtility.StageAssaultWon: stage = "强攻获胜，正在接人"; break;
                case RescueUtility.StageAssaultLost: stage = "清场失败"; break;
                case RescueUtility.StageExtracted: stage = "已抬上远行队"; break;
                default: stage = "—"; break;
            }
            float percent = d.cellsMined / Mathf.Max(1f, RescueUtility.TotalWork) * 100f;
            string s = string.Format("救援进度 {0:0.#}/{1:0} 点（{2:0.#}%）· {3}",
                d.cellsMined, (int)RescueUtility.TotalWork, percent, stage);
            if (d.rolledValue > 0f && d.workerStage != RescueUtility.StageStealthOk)
            {
                s += string.Format("\n清场战斗种子 {0:0}", d.rolledValue);
            }
            return s;
        }

        public override string DeliverySummary(Delegation d)
        {
            if (d == null)
            {
                return null;
            }
            if (!d.extractionReport.NullOrEmpty())
            {
                return d.extractionReport;
            }
            return d.workerStage == RescueUtility.StageStealthOk
                ? "潜入成功，未与守军交战"
                : "本次未救出人员";
        }

        private static bool IsPrisonerSite(Site site, DelegationDef def)
        {
            if (!def.targetSitePartTags.NullOrEmpty() && def.targetSitePartTags.Contains("PrisonerWillingToJoin"))
            {
                return true;
            }
            SitePart part = RescueUtility.TargetPart(site, def);
            return part?.def != null && part.def.defName == "PrisonerWillingToJoin";
        }
    }
}
