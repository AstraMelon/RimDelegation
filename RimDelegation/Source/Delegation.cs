using System;
using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一次委派的实例状态。挂在 WorldObjectComp_Delegations 上（跟着地点走），
    /// 因为被消耗的稀缺资源是事件点本身；车队中途消失时由地点侧检测并中断。
    /// </summary>
    public class Delegation : IExposable
    {
        public DelegationDef def;
        public DelegationModeDef mode;

        /// <summary>作战姿态（强攻 / 潜入）；null = 无姿态轴。</summary>
        public DelegationApproachDef approach;

        /// <summary>
        /// worker 自己的**阶段标记**，语义由 worker 定义。
        ///
        /// 加这一格而不是给每种玩法都在 <see cref="Delegation"/> 上加字段，
        /// 是为了让这个类不随玩法数量膨胀（与 `resourceDef` / `rolledValue` 的"worker 语义"一脉相承）。
        /// 例：救援委派用它记"清场是否已结算、结果如何"。
        /// </summary>
        public int workerStage;

        /// <summary>worker 自己的中断理由；见 <see cref="DelegationWorker.WorkerAbortReason"/>。空 = 不中断。</summary>
        public string workerAbortReason;

        /// <summary>worker 自己的"收尾说明"（例：救援委派的接人结果），用于完成/中断信件。</summary>
        public string extractionReport;

        public Caravan caravan;
        public List<Pawn> participants = new List<Pawn>();

        /// <summary>
        /// 事件点的存量（由地点持有并 Scribe，这里只引用，不重复存）。
        /// 读档后由 WorldObjectComp_Delegations.PostExposeData 重新挂上。
        /// </summary>
        public DelegationDeposit deposit;

        // 由 worker 在 OnStart 里解析出来的目标内容
        public ThingDef resourceDef;
        public float rolledValue;
        public int totalCells;
        public int yieldPerCell;

        /// <summary>
        /// 「现场物资」台账（S9）：开工那一刻现场有多少、值多少，按 ThingDef 归并。
        ///
        /// 只用来当**分母**（"已获取 3/12 件"里的 12）。开工后不再更新 —— 现场实时数量
        /// 一律问 worker 的 <c>ProgressItems</c>，两者相减就是"已获取"。
        /// 旧存档（或没有清单概念的玩法）为 null，UI 退回"只显示剩余"。
        /// </summary>
        public List<DelegationItemLedgerEntry> itemLedger;

        /// <summary>
        /// 跨 tick 累积的"搬运预算余数"（kg）。目前只有 <see cref="DelegationWorker_TakeItemStash"/> 用。
        ///
        /// 为什么必须持久化这个小数：搬运速率是按**负重**算的（kg/作业小时），
        /// 但现场物资是**离散**的，单件质量常常大于一个 tick 的预算。
        /// 如果不跨 tick 累积，就只剩"每 tick 至少搬走一件"这一条路来避免死锁 ——
        /// 而那是 60 件/秒，速率完全失效。累积之后是"攒够一件才搬走一件"：慢，但精确。
        /// </summary>
        public float haulCarryOverKg;

        // 进度
        public float cellsMined;
        public int ticksWorked;
        public int ticksResting;
        public long startedTickAbs;

        // 结束条件与风险姿态（S4，来自 DelegationRequest）
        public DelegationEndCondition endCondition = DelegationEndCondition.UntilDepleted;
        public int daysLimit = 5;
        public int quotaUnits = 400;
        public bool abortOnOutOfFood = true;

        // 心情（S4）
        public int ticksSinceMoodTick;
        public int moodTicksGranted;

        // 产出（S2）
        /// <summary>已采出但尚未交付到车队库存的数量（会保留小数余数）。</summary>
        public float oreUnits;

        /// <summary>已交付到车队库存的总数。</summary>
        public int oreDelivered;

        public int ticksSinceFlush;

        public bool massWarned;

        /// <summary>断粮但选择了"继续挖"时，只提醒一次。</summary>
        public bool outOfFoodWarned;

        // ── 暂停（§19.24）────────────────────────────────────────────────

        /// <summary>玩家手动暂停：不开采、不产出、**人员就地休息**。</summary>
        public bool paused;

        /// <summary>
        /// 暂停累计掉的 ticks。用来把"按天数收工"的预算冻住 ——
        /// 暂停 3 天不该把 5 天的计划耗掉。
        /// </summary>
        public long pausedTicksTotal;

        /// <summary>暂停累计（显示用，与 pausedTicksTotal 同值，单独留着方便 UI）。</summary>
        public int ticksPaused;

        // ── 停摆（§19.24 随机事件造成的时间损失）─────────────────────────

        /// <summary>停摆到此刻为止（TicksAbs）。停摆**照样消耗天数预算**，这是事件的时间代价。</summary>
        public int stalledUntilTickAbs;

        /// <summary>停摆累计（显示用）。</summary>
        public int ticksStalled;

        // ── 固定流程（§19.27）────────────────────────────────────────────

        /// <summary>
        /// 前置/收尾流程的进度。永远非 null（读档后由 ExposeData 兜回一个空实例），
        /// 因为它在 TickDelegation 里每 tick 都被读。
        /// </summary>
        public DelegationFlowState flow = new DelegationFlowState();

        /// <summary>
        /// S23：**段进入钩子**给出的中止理由（例：交战失利、此地有抽象模型兜不住的守军）。
        ///
        /// 非空 ⇒ 宿主在下一次判定里直接 <c>Abort</c>。走这条路而不是收尾流程，是因为
        /// "打输了"与"干完了"是两件事（与营救的清场失败同款，用户 2026-09-27 拍板：交战失败走 Abort）。
        /// </summary>
        public string flowAbortReason;

        /// <summary>
        /// S23：最近一次交战的结果摘要（`CombatResult.Summary()`）。
        /// 目前只进报告/留痕；用户留白的「搜集战利品」段将来要读它（所以现在就存下来，别只活在内存里）。
        /// </summary>
        public string flowCombatResult;

        /// <summary>
        /// S30：这一趟的**缴获**（打完仗从倒下的守军身上清点出来的东西，见 <see cref="DelegationLootItem" />）。
        ///
        /// 在「交战」段结算那一刻抄好（那时敌人的装备清单还在手上），由**「搜集战利品」段**真正搬上车
        /// —— 两段之间可能隔着存读档，所以必须进存档（`LookMode.Deep`）。
        /// </summary>
        public List<DelegationLootItem> lootBag = new List<DelegationLootItem>();

        /// <summary>
        /// S31：**本趟真正装上车的东西**（装备 + 就地屠宰的肉/皮），给 UI 的「战场清点」表用。
        ///
        /// 为什么另开一份而不是读 `lootBag`：`lootBag` 是"待搬"，搜集段搬完就清空了
        /// （清空是为了防读档重搬刷战利品）—— 清空之后玩家就再也看不到本趟拿过什么，所以要留一份**账**。
        /// </summary>
        public List<DelegationLootItem> takenRows = new List<DelegationLootItem>();

        /// <summary>S31：就地处理掉的**尸骸数**（2B：不带尸体回家，但要在物资表里给它一行）。
        /// S33 起这个数**含补刀处置掉的倒地动物**（见 <see cref="downedAnimalsDisposed" />）。</summary>
        public int corpsesButchered;

        /// <summary>
        /// S33：**倒地的动物**里，被队伍顺手处置掉（就地宰杀 / 造成尸体带走）的只数。
        ///
        /// 为什么单记一份：`corpsesButchered` / `corpsesHauled` 把"打死的"和"补刀的"混成一个数，
        /// 而玩家一眼看到的是「战场清点：尸骸 ×2 具」+「作战任务：敌方 0 阵亡/2 倒地」——
        /// 两句必须能对上，所以倒地的动物单独记一格，供 UI 写「（含 N 只倒地的动物）」。
        /// </summary>
        public int downedAnimalsDisposed;

        /// <summary>
        /// S32：**等着被搬走的真尸体**（用户 2026-09-28：「尸体不带回家，应该要可以选择，带走或者立刻处理或者丢弃」）。
        ///
        /// 只在"带走"档里非空：交战时用 `Pawn.MakeCorpse` 造出真尸体（原版屠宰台认它），
        /// 由「搜集战利品」段按载重装车。⚠️ **深度存档**（尸体里裹着那个 pawn 的完整状态），
        /// 所以搬完必须清空 —— 与 `lootBag` 同一条规矩（防读档重搬刷战利品）。
        /// </summary>
        public List<Corpse> pendingCorpses = new List<Corpse>();

        /// <summary>S32：**已经带回家**的尸骸数（"带走"档的实际装车量，给 UI 那一行用）。</summary>
        public int corpsesHauled;

        /// <summary>S32：带回家的尸骸总重（kg），给 UI 显示。</summary>
        public float corpsesHauledMass;

        /// <summary>S31：收押的**俘虏数**（历史计数，不随后续死亡/离开变化）。</summary>
        public int prisonersTaken;

        /// <summary>S31：收押进来的那几名俘虏（真 Pawn、已经是车队成员，所以按引用存是安全的）。</summary>
        public List<Pawn> capturedPrisoners = new List<Pawn>();

        /// <summary>
        /// S26：段钩子留下、但**要等这一段走完才公布**的文字（见 `DelegationPhaseDef.deferNoteUntilDone`）。
        /// 三个字段一起用：正文 / 属于哪一段（defName）/ 段落标题。
        /// </summary>
        public string flowPendingNote;

        public string flowPendingNotePhase;

        public string flowPendingNoteLabel;

        /// <summary>S29：这条待公布的段结果出自**作战**段（决定它将来落在流程块的哪个分组下）。</summary>
        public bool flowPendingNoteCombat;

        /// <summary>有没有待公布的段结果。</summary>
        public bool HasPendingNote => !flowPendingNote.NullOrEmpty();

        /// <summary>把段钩子的文字先存起来（等这一段走完再进留痕）。</summary>
        public void QueuePhaseNote(string label, string phaseDefName, string text, bool combatPhase = false)
        {
            if (text.NullOrEmpty())
            {
                return;
            }
            flowPendingNote = text;
            flowPendingNotePhase = phaseDefName;
            flowPendingNoteLabel = label;
            flowPendingNoteCombat = combatPhase;
        }

        /// <summary>
        /// S24：「作战任务」段的数据缓存（运行期，**不进存档** —— 它随时能重算，而且含 Def/场景引用）。
        /// 必须缓存的理由见 <see cref="DelegationThreatSummary" />：守军编队要生成一遍 pawn、
        /// 成算是 200 次蒙特卡洛，而主列是每帧重绘的。
        /// </summary>
        public DelegationThreatSummary threatSummary = new DelegationThreatSummary();

        /// <summary>
        /// S27：**被玩家排除出这一场战斗**的成员（用户：「作战任务里面添加一个勾选框，
        /// 决定人物或动物是参加还是不参加作战」）。他们仍然属于这次委派（照常开采/搬运），只是不参战。
        /// 存引用：pawn 的生命周期与存档一致。
        /// </summary>
        public List<Pawn> noCombatPawns = new List<Pawn>();

        /// <summary>这个人/动物参不参加作战。</summary>
        public bool FightsInCombat(Pawn p)
        {
            return p != null && (noCombatPawns == null || !noCombatPawns.Contains(p));
        }

        // ── 紧急加班（§19.26）────────────────────────────────────────────

        /// <summary>
        /// 剩余加班额度（ticks）。&gt; 0 时**工时窗口被绕过**：人继续干，但休息条不回升。
        ///
        /// 为什么是"额度倒扣"而不是一个 bool 开关：额度只在"本来该休息"的时段被消耗
        /// （工时段里照常按窗口走，不扣额度），所以「4 小时」的语义是**额外**的 4 小时，
        /// 而不是"从此刻起 4 小时"。
        /// </summary>
        public int overtimeTicksRemaining;

        /// <summary>此刻是否在加班作业（有额度）。</summary>
        public bool EmergencyOvertimeActive => overtimeTicksRemaining > 0;

        /// <summary>
        /// **工时门控的唯一判据**：此刻这个人算不算"在干活"。
        ///
        /// 为什么必须只有这一处：判据原本散在四处（本类的 TickDelegation、
        /// DelegationRegistry、Patch_CaravanNeedsTracker_Rest、两个 UI），
        /// 紧急加班要"绕过工时窗口"，只要漏掉其中一处就会出现
        /// "界面说在加班、但休息条还在回升"或者"人在干活却不动进度"这类半真半假的状态。
        /// </summary>
        public bool IsWorkTime(Site site, long nowAbs)
        {
            if (mode == null || site == null)
            {
                return false;
            }
            // S25：**等玩家下作战指令**期间不算工时（远行队就地待命）。
            // 必须挂在唯一判据上：否则会出现"界面说在待命、流程却往前拱"这种半真半假的状态。
            if (AwaitingOrder)
            {
                return false;
            }
            return EmergencyOvertimeActive || mode.IsWorkingNow(site.Tile, nowAbs);
        }

        /// <summary>
        /// S25：此刻是不是**在等玩家下作战指令**（流程停在一个 `pauseUntilSignal` 段之前）。
        /// 不额外存状态，直接由流程表 + 已下达的信号推出来 ⇒ 读档后自然一致。
        /// </summary>
        public bool AwaitingOrder => def != null && flow != null && DelegationFlow.For(def).IsGated(flow);

        // ── 随机事件（§19.24）────────────────────────────────────────────

        public int ticksSinceEventRoll;

        public int eventsFired;

        /// <summary>留痕上限（FIFO）—— 防存档膨胀；报告与流程块只需要最近这些。</summary>
        public const int MaxEventLog = 50;

        /// <summary>
        /// 事件留痕（S15）：最近若干条已发生的随机事件。
        /// **为什么必须存**：`Apply()` 的文字原本只进信件正文，事件过去便无从查起 ——
        /// 而"流程块那一行 / 结束报告 / 历史记录"三件事都要用它。
        /// </summary>
        public List<DelegationEventLogEntry> eventLog;

        /// <summary>最近触发过的事件与其时刻（两条平行列表，便于 Scribe_Collections）。</summary>
        public List<DelegationEventDef> recentEventDefs = new List<DelegationEventDef>();
        public List<int> recentEventTicks = new List<int>();

        private DelegationWorker worker;

        public DelegationWorker Worker
        {
            get
            {
                if (worker == null && def != null)
                {
                    worker = def.CreateWorker();
                }
                return worker;
            }
        }

        public Delegation()
        {
        }

        public Delegation(DelegationDef def, DelegationRequest request, Caravan caravan, Site site, DelegationDeposit deposit)
        {
            this.def = def;
            this.caravan = caravan;
            this.deposit = deposit;
            mode = request?.mode;
            approach = request?.approach;
            participants = request?.pawns ?? new List<Pawn>();
            endCondition = request?.endCondition ?? DelegationEndCondition.UntilDepleted;
            daysLimit = request?.daysLimit ?? 5;
            quotaUnits = request?.quotaUnits ?? 400;
            abortOnOutOfFood = request?.abortWhenOutOfFood ?? true;
            startedTickAbs = GenTicks.TicksAbs;
            Worker?.OnStart(this, site);
        }

        /// <summary>
        /// 开工后**立刻**抄一份现场台账（分母）。由宿主在 <c>Worker.OnStart</c> 之后调用一次。
        ///
        /// 为什么必须放在 OnStart 之后：采矿 worker 是在 OnStart 里才把
        /// <c>resourceDef / totalCells / yieldPerCell</c> 解析出来的，而
        /// <c>ProgressItems</c> 要靠这几格才能算出"还剩多少单位"。
        ///
        /// 抄不到（worker 返回 null / 空容器 / 没有 thingDef）就留空列表 —— UI 会退回旧写法，
        /// 绝不显示一个 0 当分母。
        /// </summary>
        public void CaptureItemLedger(Site site)
        {
            itemLedger = new List<DelegationItemLedgerEntry>();
            List<DelegationPreviewItem> items;
            try
            {
                items = Worker?.ProgressItems(this, site);
            }
            catch (Exception ex)
            {
                // 台账抄不到只是少一行"已获取"，绝不能让开工流程崩掉
                Log.WarningOnce("[RimDelegation] 抄录现场物资台账失败：" + ex.Message, 0x5E0D8);
                return;
            }
            if (items.NullOrEmpty())
            {
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                DelegationPreviewItem item = items[i];
                if (item?.thingDef == null || item.count <= 0)
                {
                    continue;
                }
                itemLedger.Add(new DelegationItemLedgerEntry
                {
                    thingDef = item.thingDef,
                    unitLabel = item.unitLabel,
                    expectedCount = item.count,
                    expectedMass = item.mass,
                    expectedValue = item.value
                });
            }
        }

        /// <summary>按 ThingDef 找台账行（找不到返回 null = 这类没底账）。</summary>
        public DelegationItemLedgerEntry LedgerFor(ThingDef def)
        {
            if (def == null || itemLedger == null)
            {
                return null;
            }
            for (int i = 0; i < itemLedger.Count; i++)
            {
                if (itemLedger[i]?.thingDef == def)
                {
                    return itemLedger[i];
                }
            }
            return null;
        }

        /// <summary>已采出的总量（含尚未交付的余数），用于"按产出配额收工"的判定。</summary>
        public int MinedUnitsTotal => oreDelivered + Mathf.FloorToInt(oreUnits);

        /// <summary>一小时 = 2500 ticks（与 FlushIntervalTicks 一致）。</summary>
        public const int TicksPerHour = 2500;

        /// <summary>已过去的**有效**委派天数 —— 暂停的时间不计入（停摆的时间计入）。</summary>
        public float ElapsedDays(long nowAbs)
        {
            return (nowAbs - startedTickAbs - pausedTicksTotal) / 60000f;
        }

        /// <summary>当前是否处于事件造成的停摆中。</summary>
        public bool IsStalled(long nowAbs)
        {
            return nowAbs < stalledUntilTickAbs;
        }

        /// <summary>把作业停摆若干小时。</summary>
        public void StallFor(float hours)
        {
            if (hours <= 0f) return;
            int ticks = Mathf.RoundToInt(hours * TicksPerHour);
            long until = (long)GenTicks.TicksAbs + ticks;
            if (until > stalledUntilTickAbs)
            {
                stalledUntilTickAbs = (int)Mathf.Min(until, int.MaxValue);
            }
        }

        /// <summary>该事件此刻是否还在冷却中。</summary>
        public bool EventCooldownActive(DelegationEventDef def, int nowAbs)
        {
            if (def == null || recentEventDefs == null || recentEventTicks == null) return false;
            float cooldownTicks = def.minDaysBetween * 60000f;
            for (int i = 0; i < recentEventDefs.Count && i < recentEventTicks.Count; i++)
            {
                if (!ReferenceEquals(recentEventDefs[i], def)) continue;
                return (nowAbs - recentEventTicks[i]) < cooldownTicks;
            }
            return false;
        }

        /// <summary>记录一次事件触发（用于冷却判定）。</summary>
        public void MarkEventFired(DelegationEventDef def, int nowAbs)
        {
            if (def == null) return;
            if (recentEventDefs == null) recentEventDefs = new List<DelegationEventDef>();
            if (recentEventTicks == null) recentEventTicks = new List<int>();

            for (int i = 0; i < recentEventDefs.Count && i < recentEventTicks.Count; i++)
            {
                if (!ReferenceEquals(recentEventDefs[i], def)) continue;
                recentEventTicks[i] = nowAbs;
                return;
            }
            recentEventDefs.Add(def);
            recentEventTicks.Add(nowAbs);
        }

        // ── 事件留痕（S15）─────────────────────────────────────────────────

        /// <summary>记一条事件留痕（宿主 <c>FireEvent</c> 调用）。FIFO，超过上限丢最旧的。</summary>
        public void LogEvent(DelegationEventDef def, string detail)
        {
            if (def == null)
            {
                return;
            }
            AppendLogEntry(def, null, detail);
        }

        /// <summary>
        /// S23：**段进入钩子**产生的文字（交战结算、抽象模型兜不住的守军…）的留痕。
        ///
        /// 走与随机事件**同一条**通道（`eventLog`）⇒ 流程块的事件行、结束报告、历史记录
        /// 三处自动都能看到它，不必为"段的结果"再开第二套显示（也就不会出现"只有一处记得写"）。
        /// <paramref name="phaseLabel" /> 会取代事件名显示（例：`交战`）。
        /// <paramref name="combatPhase" />（S29）= 这条结果出自作战段 ⇒ 流程块把它排进「作战任务」组。
        /// </summary>
        public void LogPhaseNote(string phaseLabel, string detail, bool combatPhase = false)
        {
            if (detail.NullOrEmpty())
            {
                return;
            }
            AppendLogEntry(null, phaseLabel, detail, combatPhase);
        }

        private void AppendLogEntry(DelegationEventDef def, string phaseLabel, string detail,
            bool combatPhase = false)
        {
            if (eventLog == null)
            {
                eventLog = new List<DelegationEventLogEntry>();
            }
            eventLog.Add(new DelegationEventLogEntry
            {
                def = def,
                phaseLabel = phaseLabel,
                combatPhase = combatPhase,
                firedTickAbs = GenTicks.TicksAbs,
                detail = detail,
                seen = false,
            });
            while (eventLog.Count > MaxEventLog)
            {
                eventLog.RemoveAt(0);
            }
        }

        /// <summary>还没被玩家看过的事件条数 —— S15 底部「委派」按钮的角标用它。</summary>
        public int UnseenEventCount()
        {
            if (eventLog == null)
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < eventLog.Count; i++)
            {
                if (eventLog[i] != null && !eventLog[i].seen)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>把留痕全部标成"已看过"（打开主控台时调用）。</summary>
        public void MarkEventsSeen()
        {
            if (eventLog == null)
            {
                return;
            }
            for (int i = 0; i < eventLog.Count; i++)
            {
                if (eventLog[i] != null)
                {
                    eventLog[i].seen = true;
                }
            }
        }

        /// <summary>最近一条留痕（流程块那一行用它）；没有则 null。</summary>
        public DelegationEventLogEntry LastEventEntry()
        {
            if (eventLog == null || eventLog.Count == 0)
            {
                return null;
            }
            return eventLog[eventLog.Count - 1];
        }

        /// <summary>最近一次触发的事件（显示用）。</summary>
        public DelegationEventDef LastEventDef()
        {
            if (recentEventDefs == null || recentEventDefs.Count == 0) return null;
            int best = -1;
            int bestTick = int.MinValue;
            for (int i = 0; i < recentEventDefs.Count && i < recentEventTicks.Count; i++)
            {
                if (recentEventTicks[i] > bestTick)
                {
                    bestTick = recentEventTicks[i];
                    best = i;
                }
            }
            return best >= 0 ? recentEventDefs[best] : null;
        }

        /// <summary>按当前结束条件，是否该收工了。返回原因（null = 继续）。</summary>
        public string EndConditionReached(long nowAbs)
        {
            switch (endCondition)
            {
                case DelegationEndCondition.Days:
                    if (daysLimit > 0 && ElapsedDays(nowAbs) >= daysLimit)
                    {
                        return $"按计划干满 {daysLimit} 天";
                    }
                    return null;
                case DelegationEndCondition.Quota:
                    if (quotaUnits > 0 && MinedUnitsTotal >= quotaUnits)
                    {
                        return $"已达到产出配额 {quotaUnits} 单位";
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>收工时目标是否真的被采空（决定要不要销毁地点）。</summary>
        public bool TargetDepleted => totalCells > 0 && cellsMined >= (float)totalCells;

        public bool IsComplete => totalCells > 0 && cellsMined >= (float)totalCells;

        public float Progress => totalCells <= 0 ? 0f : Mathf.Clamp01(cellsMined / totalCells);

        /// <summary>预计剩余天数；返回负数表示无法估算。</summary>
        public float EstimatedDaysLeft(PlanetTile tile)
        {
            float perDay = Worker?.EstimatedUnitsPerDay(this, tile) ?? 0f;
            if (perDay <= 0f)
            {
                return -1f;
            }
            float days = (totalCells - cellsMined) / perDay;
            // 固定流程（§19.27）与速率无关：那几段是流程时间，加人不加快，
            // 所以必须**加**在速率估算之外 —— 否则前置 4h 的活会系统性低估工期。
            days += FlowRemainingTicks() / 60000f;
            return days;
        }

        /// <summary>还没走完的固定流程 ticks（前置 + 正在进行的收尾）。没有流程时返回 0。</summary>
        public float FlowRemainingTicks()
        {
            DelegationFlow f = DelegationFlow.For(def);
            if (!f.HasPhases || flow == null)
            {
                return 0f;
            }
            float ticks = 0f;
            // S23：段表要走"含冻结"的访问器 —— 读 Def 上的全段会把这条委派根本没走的段
            // （例如无威胁时的战术侦察/评估/交战）算进预计剩余时间。
            List<DelegationPhaseDef> pre = f.PreludeFor(flow);
            for (int i = flow.preludeIndex; i < pre.Count; i++)
            {
                ticks += pre[i].Ticks;
            }
            // 收尾只在"已经进入收尾"之后才算进 ETA —— 那是干完之后的事，
            // 提前把它加进"预计剩余"会让玩家以为收工时间变长了。
            if (!flow.pendingEndReason.NullOrEmpty())
            {
                List<DelegationPhaseDef> suf = f.SuffixFor(flow);
                for (int j = flow.suffixIndex; j < suf.Count; j++)
                {
                    ticks += suf[j].Ticks;
                }
            }
            ticks -= flow.phaseTicks;   // 当前这一段的余量（前置与收尾共用同一个计数器）
            return Mathf.Max(0f, ticks);
        }

        public string ModeLine()
        {
            string line;
            if (mode == null)
            {
                line = "（未指定模式）";
            }
            else
            {
                line = string.Format("{0} · {1} · 速率 ×{2:0.##}", mode.LabelCap, mode.HoursLabel, mode.workRateMultiplier);
            }
            if (approach != null)
            {
                line += " · " + approach.LabelCap;
            }
            return line;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref mode, "mode");
            Scribe_Defs.Look(ref approach, "approach");
            Scribe_Values.Look(ref workerStage, "workerStage", 0);
            Scribe_Values.Look(ref workerAbortReason, "workerAbortReason");
            Scribe_Values.Look(ref extractionReport, "extractionReport");
            Scribe_References.Look(ref caravan, "caravan");
            Scribe_Collections.Look(ref participants, "participants", LookMode.Reference);
            // 「已获取 X/Y」的分母（S9）：旧存档里为 null ⇒ UI 退回"只显示剩余"
            Scribe_Collections.Look(ref itemLedger, "itemLedger", LookMode.Deep);
            Scribe_Defs.Look(ref resourceDef, "resourceDef");
            Scribe_Values.Look(ref rolledValue, "rolledValue", 0f);
            Scribe_Values.Look(ref totalCells, "totalCells", 0);
            Scribe_Values.Look(ref yieldPerCell, "yieldPerCell", 0);
            Scribe_Values.Look(ref cellsMined, "cellsMined", 0f);
            Scribe_Values.Look(ref haulCarryOverKg, "haulCarryOverKg", 0f);
            Scribe_Values.Look(ref ticksWorked, "ticksWorked", 0);
            Scribe_Values.Look(ref ticksResting, "ticksResting", 0);
            Scribe_Values.Look(ref startedTickAbs, "startedTickAbs", 0L);
            Scribe_Values.Look(ref oreUnits, "oreUnits", 0f);
            Scribe_Values.Look(ref oreDelivered, "oreDelivered", 0);
            Scribe_Values.Look(ref ticksSinceFlush, "ticksSinceFlush", 0);
            Scribe_Values.Look(ref massWarned, "massWarned", false);
            Scribe_Values.Look(ref outOfFoodWarned, "outOfFoodWarned", false);
            Scribe_Values.Look(ref endCondition, "endCondition", DelegationEndCondition.UntilDepleted);
            Scribe_Values.Look(ref daysLimit, "daysLimit", 5);
            Scribe_Values.Look(ref quotaUnits, "quotaUnits", 400);
            Scribe_Values.Look(ref abortOnOutOfFood, "abortOnOutOfFood", true);
            Scribe_Values.Look(ref ticksSinceMoodTick, "ticksSinceMoodTick", 0);
            Scribe_Values.Look(ref moodTicksGranted, "moodTicksGranted", 0);

            // 暂停 / 停摆 / 随机事件（§19.24）
            Scribe_Values.Look(ref paused, "paused", false);
            Scribe_Values.Look(ref pausedTicksTotal, "pausedTicksTotal", 0L);
            Scribe_Values.Look(ref ticksPaused, "ticksPaused", 0);
            Scribe_Values.Look(ref stalledUntilTickAbs, "stalledUntilTickAbs", 0);
            Scribe_Values.Look(ref ticksStalled, "ticksStalled", 0);

            // 固定流程（§19.27）与紧急加班（§19.26）
            Scribe_Deep.Look(ref flow, "flow");
            // S23：段钩子的中止理由与交战摘要（都是"这一次委派"的事实，必须跟着存档走）
            Scribe_Values.Look(ref flowAbortReason, "roFlowAbortReason");
            Scribe_Values.Look(ref flowCombatResult, "roFlowCombatResult");
            Scribe_Collections.Look(ref lootBag, "roLootBag", LookMode.Deep);
            if (lootBag == null)
            {
                lootBag = new List<DelegationLootItem>();
            }
            // S31：战场清点的账（装车的东西 / 尸骸数 / 俘虏）
            Scribe_Collections.Look(ref takenRows, "roTakenRows", LookMode.Deep);
            if (takenRows == null)
            {
                takenRows = new List<DelegationLootItem>();
            }
            Scribe_Values.Look(ref corpsesButchered, "roCorpsesButchered", 0);
            // S32：带走的尸骸（真尸体，深度存档）+ 装车统计
            Scribe_Collections.Look(ref pendingCorpses, "roPendingCorpses", LookMode.Deep);
            if (pendingCorpses == null)
            {
                pendingCorpses = new List<Corpse>();
            }
            Scribe_Values.Look(ref corpsesHauled, "roCorpsesHauled", 0);
            Scribe_Values.Look(ref corpsesHauledMass, "roCorpsesHauledMass", 0f);
            // S33：补刀处置掉的倒地动物（UI 那一行要写"含 N 只倒地的动物"）
            Scribe_Values.Look(ref downedAnimalsDisposed, "roDownedAnimalsDisposed", 0);
            Scribe_Values.Look(ref prisonersTaken, "roPrisonersTaken", 0);
            Scribe_Collections.Look(ref capturedPrisoners, "roCapturedPrisoners", LookMode.Reference);
            if (capturedPrisoners == null)
            {
                capturedPrisoners = new List<Pawn>();
            }
            Scribe_Values.Look(ref flowPendingNote, "roFlowPendingNote");
            Scribe_Values.Look(ref flowPendingNotePhase, "roFlowPendingNotePhase");
            Scribe_Values.Look(ref flowPendingNoteLabel, "roFlowPendingNoteLabel");
            Scribe_Values.Look(ref flowPendingNoteCombat, "roFlowPendingNoteCombat", false);
            Scribe_Collections.Look(ref noCombatPawns, "roNoCombatPawns", LookMode.Reference);
            Scribe_Values.Look(ref overtimeTicksRemaining, "overtimeTicksRemaining", 0);
            Scribe_Values.Look(ref ticksSinceEventRoll, "ticksSinceEventRoll", 0);
            Scribe_Values.Look(ref eventsFired, "eventsFired", 0);
            Scribe_Collections.Look(ref recentEventDefs, "recentEventDefs", LookMode.Def);
            Scribe_Collections.Look(ref recentEventTicks, "recentEventTicks", LookMode.Value);
            // S15：事件留痕（流程块那一行 / 结束报告 / 历史记录都读它）
            Scribe_Collections.Look(ref eventLog, "eventLog", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (participants == null) participants = new List<Pawn>();
                // 流程进度是每 tick 都要读的对象，读档后绝不能是 null
                if (flow == null) flow = new DelegationFlowState();
                if (recentEventDefs == null) recentEventDefs = new List<DelegationEventDef>();
                if (recentEventTicks == null) recentEventTicks = new List<int>();
                // 两条平行列表长度必须一致，否则冷却判定会错位
                while (recentEventTicks.Count < recentEventDefs.Count) recentEventTicks.Add(0);
                while (recentEventDefs.Count < recentEventTicks.Count) recentEventDefs.Add(null);

                // 模式 Def 被改名/删除后，旧存档里的 `mode` 会解析成 null ——
                // 那会让 DelegationRegistry 直接跳过这次委派（`d?.mode == null`），
                // 表现是"读档后委派静止不动"。退回到 def 的默认模式，别让它卡死。
                if (mode == null)
                {
                    mode = def?.defaultMode;
                    if (mode != null)
                    {
                        Log.Warning("[RimDelegation] 委派模式在 Def 里找不到了，已退回默认模式「"
                            + mode.label + "」（地点：" + (caravan?.Label ?? "?") + "）");
                    }
                }
            }
        }
    }
}
