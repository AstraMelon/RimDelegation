using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>委派开始时如何处理目标地点的原版超时计时（30 天）。</summary>
    public enum DelegationTimeoutHandling
    {
        /// <summary>不干预。</summary>
        Ignore,
        /// <summary>暂停（发原版 inSignalDisable 信号，零 Harmony）。</summary>
        Pause,
        /// <summary>延长指定天数（S3 实现）。</summary>
        ExtendBy
    }

    /// <summary>
    /// 一条「委派」的数据定义：能把什么事件委派给谁、怎么结算。
    /// 一个 def 覆盖一类世界事件，加 def 不加代码（框架目标）。
    /// </summary>
    public class DelegationDef : Def
    {
        /// <summary>行为类，必须是 DelegationWorker 的子类。</summary>
        public Type workerClass = typeof(DelegationWorker);

        /// <summary>按 SitePartDef.tags 匹配目标站点。</summary>
        public List<string> targetSitePartTags = new List<string>();

        /// <summary>或精确指定 SitePartDef。</summary>
        public List<SitePartDef> targetSitePartDefs = new List<SitePartDef>();

        /// <summary>就地下达时的按钮标签（{0} = 地点名）。</summary>
        public string commandLabel;

        /// <summary>按钮说明。</summary>
        public string commandDesc;

        /// <summary>预先委派：车队行进中的 Label。</summary>
        public string planningLabel;

        /// <summary>预先委派：车队行进中的 ReportString（{0} = 地点名）。</summary>
        public string planningReportString;

        public string startLetterLabel;
        public string startLetterText;
        public string completeLetterLabel;
        public string completeLetterText;
        public string abortLetterLabel;
        public string abortLetterText;

        public int minPawns = 1;
        public int maxPawns = 12;

        /// <summary>参与者的技能（用于发经验；可空）。</summary>
        public SkillDef skillDef;

        public List<DelegationModeDef> modes = new List<DelegationModeDef>();
        public DelegationModeDef defaultMode;

        /// <summary>作战姿态（强攻 / 潜入）。空 = 这条委派不做姿态判定。</summary>
        public List<DelegationApproachDef> approaches = new List<DelegationApproachDef>();
        public DelegationApproachDef defaultApproach;

        public DelegationTimeoutHandling handleTargetTimeout = DelegationTimeoutHandling.Pause;
        public bool destroyTargetOnComplete = true;
        public bool abortIfCaravanLeavesTile = true;

        /// <summary>
        /// 战斗结算里的阵亡是否**真的**杀死参与者（默认 true）。
        ///
        /// 为什么默认真实：威胁评估面板已经承诺了"阵亡 P50/P90/最坏"，
        /// 如果把阵亡软化成重伤，那份预告就成了假话 —— 本 mod 的明确原则是"预告即契约"（DESIGN §19.12）。
        /// 想做成"永不死人"的休闲模式时，把它改成 false 即可（阵亡降级为倒地）。
        /// </summary>
        public bool casualtiesArePermanent = true;

        /// <summary>
        /// 委派动过目标存量之后，是否**禁止玩家再进图**（**默认 true**，S14 起）。
        ///
        /// 用途：① 采矿的存量是委派按 GenStep 的公式**独立推算**的，而原版只在真正生成地图时
        /// 才掷一次格数（`GenStep_PreciousLump`）—— 两者是两次独立掷骰，
        /// 所以"委派挖一半 → 进图再挖一份"能双吃（DESIGN §7.1）。
        /// ② 更根本的是**玩法口径**（用户 S14 拍板）：一个事件点只要被委派动过，
        /// 之后就只能走委派路线，原版那条"自己进去搬/打"的路要关掉 ——
        /// 物资藏匿点搬了一部分、营救失败留下的人，都不再是"进图捡漏"的理由。
        ///
        /// 为什么默认 true 而不是 false：漏开这个开关的后果（能进图 = 双吃 / 绕过委派）
        /// 比多封一个点的后果严重得多，所以让"忘记配置"倒向安全的一侧；
        /// 想让某条玩法保留进图路线的，在它的 Def 上显式写 false（那是一个有意的决定）。
        ///
        /// 实现方式是**原版的** <c>EnterCooldownComp</c>（`Site` def 本来就挂着它，
        /// `CaravanArrivalAction_VisitSite.CanVisit` 会检查 `site.EnterCooldownBlocksEntering()`）
        /// ⇒ **零 Harmony**，而且玩家看到的是原版自己的"暂时无法进入"提示。
        ///
        /// ⚠️ 它只封"原版进图"，**不封我们自己的委派菜单** —— 地点保留时剩下的目标
        /// 仍然可以再派一次委派去取（`WorldObjectComp.GetFloatMenuOptions` 不看这个冷却）。
        /// </summary>
        public bool blockMapEntryAfterWorked = true;

        /// <summary>上面那条的封禁时长（天）。默认极长 —— 该地点一旦被动过就只走委派路线。</summary>
        public float blockMapEntryDays = 9999f;

        /// <summary>委派进行中，每天给车队成员挂的心情记忆（S4）。</summary>
        public ThoughtDef dailyMoodThought;

        /// <summary>采空完成时给参与者的心情记忆（S4）。</summary>
        public ThoughtDef completeMoodThought;

        /// <summary>中断时给参与者的心情记忆（S4）。</summary>
        public ThoughtDef abortMoodThought;

        /// <summary>结束条件选项的默认值（对话框里可改）。</summary>
        public DelegationEndCondition defaultEndCondition = DelegationEndCondition.UntilDepleted;

        /// <summary>对话框里"按天数"的允许上限。</summary>
        public int maxDaysLimit = 30;

        /// <summary>
        /// **固定流程**（§19.27）：这条委派在作业前后各有几段固定时长。
        ///
        /// 空 = 没有流程（采矿/营救就是这种，行为与加这个字段之前逐字一致）。
        /// 语义与顺序由 <see cref="DelegationFlow"/> 定义：`afterWork = false` 的段按出现顺序在
        /// 作业前走完，`afterWork = true` 的段在任何收工条件成立之后走完才真正收工。
        /// </summary>
        public List<DelegationPhaseDef> flowPhases = new List<DelegationPhaseDef>();

        /// <summary>
        /// S22：**主作业段**时小队所处的虚拟位置（流程栏顶部那一行）。
        /// 空 = 不显示位置行（没有流程的委派也不会突然冒出"外围"这种词）。
        /// 例：搜刮 = 「藏匿点内部」、采矿 = 「矿点」。
        /// </summary>
        public string workSquadPosition;

        /// <summary>
        /// S22：**完成这一段之前，不把"预期获得 / 现场物资"给玩家看**
        /// （用户原话：「这部分需要完成破门才给玩家看预期获得」）。
        ///
        /// 填的是某段 `DelegationPhaseDef` 的 defName（例：`RimDelegation_Phase_StashBreach`）。
        /// 判据走 `DelegationFlow.PhaseDone`：没到那一段 ⇒ 列表为空，UI 自己会显示"未知/待确认"。
        /// 空 = 不门控（行为与加这个字段之前逐字一致）。
        /// </summary>
        public string hideItemsUntilPhase;

        /// <summary>
        /// **主作业段**的旁白池（§19.29）。
        ///
        /// 为什么挂在委派 Def 而不是 PhaseDef：主作业段（开采中/搬运中/营救中）**不是**一段
        /// `flowPhases`（它由 worker 的速率推进、没有固定时长），所以它的旁白没有别的落脚点。
        /// </summary>
        public List<string> workAmbientLines = new List<string>();

        /// <summary>**只有一个人**时主作业段的旁白池（S14）。空 = 用 `workAmbientLines`。</summary>
        public List<string> workAmbientLinesSolo = new List<string>();

        /// <summary>**该地点真有守军**时主作业段的旁白池（S14，判据 `HasThreat`）。空 = 用 `workAmbientLines`。</summary>
        public List<string> workAmbientLinesHostile = new List<string>();

        /// <summary>**队伍带驮兽**时主作业段的旁白池（S14，判据 `RaceProps.packAnimal`）。空 = 用 `workAmbientLines`。</summary>
        public List<string> workAmbientLinesPacked = new List<string>();

        /// <summary>主作业段多久换一条旁白（小时）。空 = 默认 0.5h。RIM-13 起支持区间（`"0.5~1.5"`）。</summary>
        public string workAmbientRerollHours;

        /// <summary>主作业段的说话人模式（默认 `All`：挖矿/搬东西是集体动作，写"全队"最自然）。</summary>
        public string workSpeakerMode = "All";

        /// <summary>
        /// **休息时段**的旁白池（S16 用户要求：休息时把休息置顶，并显示"睡觉 / 睡前聊天"这类内容）。
        /// 空 = 休息时只显示"休息中（距开工 X 小时）"这一行。
        /// </summary>
        public List<string> restAmbientLines = new List<string>();

        /// <summary>
        /// `flowPhases` 解析出来的前置/收尾两串（见 <see cref="DelegationFlow.For" />）。
        ///
        /// 不是 XML 字段、也不用存档：它纯粹是运行期缓存，
        /// 靠 `cachedFlowSource` 记住"是从哪个列表算出来的"来感知热重载换了 List 实例。
        /// </summary>
        public DelegationFlow cachedFlow;

        /// <summary>上面那份缓存的来源列表引用（引用一变就说明 Def 被重新解析过）。</summary>
        public List<DelegationPhaseDef> cachedFlowSource;

        // ── RIM-13 / RIM-14：主作业段与休息段的旁白池（与 PhaseDef 同一套并集逻辑）────

        /// <summary>主作业段四个池的解析缓存 + 并集。</summary>
        private readonly AmbientPoolSet workAmbientPools = new AmbientPoolSet();

        /// <summary>休息池的解析缓存。</summary>
        private readonly AmbientPoolCache restAmbientCache = new AmbientPoolCache();

        private float workRerollMin = float.NaN;

        private float workRerollMax = float.NaN;

        /// <summary>主作业段的候选池（默认 + 单人 + 驮兽 + 敌情 的并集，RIM-13）。</summary>
        public List<AmbientLine> WorkAmbientPool => workAmbientPools.Union("workAmbientLines",
            workAmbientLines, workAmbientLinesSolo, workAmbientLinesPacked, workAmbientLinesHostile);

        /// <summary>休息池（S16）。RIM-14 起它同样支持行首指令（例如只让 `[night]` 的句子在夜里出现）。</summary>
        public List<AmbientLine> RestAmbientPool
        {
            get
            {
                if (restAmbientLines.NullOrEmpty())
                {
                    return null;
                }
                if (!ReferenceEquals(restAmbientCache.source, restAmbientLines) || restAmbientCache.parsed == null)
                {
                    restAmbientCache.source = restAmbientLines;
                    restAmbientCache.parsed = AmbientPoolSet.ParseAll(restAmbientLines, "restAmbientLines");
                }
                return restAmbientCache.parsed;
            }
        }

        /// <summary>主作业段换句间隔的下界（小时，已解析）。</summary>
        public float WorkRerollMinHours
        {
            get
            {
                EnsureWorkReroll();
                return workRerollMin;
            }
        }

        /// <summary>主作业段换句间隔的上界（小时，已解析）。</summary>
        public float WorkRerollMaxHours
        {
            get
            {
                EnsureWorkReroll();
                return workRerollMax;
            }
        }

        private void EnsureWorkReroll()
        {
            if (!float.IsNaN(workRerollMin))
            {
                return;
            }
            DelegationAmbient.ParseRerollRange(workAmbientRerollHours, out workRerollMin, out workRerollMax);
        }

        public override void ResolveReferences()
        {
            base.ResolveReferences();
            // RIM-13：热重载会读进新的 `workAmbientRerollHours` ⇒ 丢掉上次解析出来的区间。
            workRerollMin = float.NaN;
        }

        /// <summary>
        /// 「紧急加班」（§19.26）每次点击买到的**无视工时窗口**额度（小时）。
        ///
        /// 0 或负数 = 这条委派不提供紧急加班（按钮不出现）。
        /// </summary>
        public float emergencyOvertimeHoursPerUse = 4f;

        /// <summary>
        /// 紧急加班的心情记忆 Def。**多 stage**：同一个地点第 n 次加班挂第 n 级
        /// （Defs 里配的是 -4/-8/-12/-16/-20）。
        ///
        /// 为空 = 加班不扣心情 —— 那是个静默失效（玩家会以为这条机制是免费的），
        /// 所以 <see cref="ModBoot"/> 会在启动自检里喊出来。
        /// </summary>
        public ThoughtDef emergencyOvertimeMoodThought;

        /// <summary>这条委派能不能用紧急加班。</summary>
        public bool AllowsEmergencyOvertime => emergencyOvertimeHoursPerUse > 0f;

        /// <summary>给 UI 的加班额度（ticks）。</summary>
        public int EmergencyOvertimeTicksPerUse =>
            emergencyOvertimeHoursPerUse <= 0f
                ? 0
                : Mathf.Max(1, (int)(emergencyOvertimeHoursPerUse * Delegation.TicksPerHour + 0.5f));

        public string gizmoIconPath;

        public DelegationWorker CreateWorker()
        {
            DelegationWorker w = (DelegationWorker)Activator.CreateInstance(workerClass);
            w.def = this;
            return w;
        }

        public DelegationModeDef ResolveMode(DelegationModeDef wanted)
        {
            if (wanted != null) return wanted;
            if (defaultMode != null) return defaultMode;
            if (!modes.NullOrEmpty()) return modes[0];
            return null;
        }

        /// <summary>空 = 这条委派没有姿态轴（普通委派，不需要清场）。</summary>
        public DelegationApproachDef ResolveApproach(DelegationApproachDef wanted)
        {
            if (wanted != null) return wanted;
            if (defaultApproach != null) return defaultApproach;
            if (!approaches.NullOrEmpty()) return approaches[0];
            return null;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            // RIM-14：旁白行首指令写错 ⇒ 报出来（写错的后果是"这一句永远不出现"，不报只会表现为文案莫名变少）
            foreach (string e in DelegationPhaseDef.AmbientConfigErrors(WorkAmbientPool))
            {
                yield return e;
            }
            foreach (string e in DelegationPhaseDef.AmbientConfigErrors(RestAmbientPool))
            {
                yield return e;
            }
            if (workerClass == null || !typeof(DelegationWorker).IsAssignableFrom(workerClass))
            {
                yield return "workerClass 必须是 DelegationWorker 的子类";
            }
            if (targetSitePartTags.NullOrEmpty() && targetSitePartDefs.NullOrEmpty())
            {
                yield return "至少要有 targetSitePartTags 或 targetSitePartDefs";
            }
            if (modes.NullOrEmpty())
            {
                yield return "modes 不能为空";
            }
            // 固定流程里出现 null（defName 写错 → 解析不到）时，那一段会被静默跳过 ⇒ 时长凭空变短。
            if (flowPhases != null)
            {
                for (int i = 0; i < flowPhases.Count; i++)
                {
                    if (flowPhases[i] == null)
                    {
                        yield return string.Format("flowPhases[{0}] 解析不到对应的 DelegationPhaseDef（defName 拼错？）", i);
                    }
                }
            }
        }
    }
}
