using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    public enum DelegationStageState
    {
        Done,
        Active,
    }

    /// <summary>
    /// 「流程」块里的**一个阶段**（§19.29）。三种来源合成同一个序列：
    ///   ① `DelegationPhaseDef` 的**前置**段（侦察/移动/破门…）
    ///   ② **主作业**段（开采中/搬运中/营救中）—— 它**不是** flowPhases，由 worker 的速率推进
    ///   ③ `DelegationPhaseDef` 的**收尾**段（撤离…）
    ///
    /// 为什么要把 ② 也塞进这个序列：用户 S14 的草图要求"阶段累积展开"（已完成 + 当前），
    /// 而"搬运物资"就是委派的主体工作 —— 不并进来，列表会在最能体现进度的地方断掉。
    /// </summary>
    public class DelegationStage
    {
        /// <summary>稳定标识（"pre0" / "work" / "suf1"）：旁白掷定与说话人哈希都按它记账。</summary>
        public string key = "";

        /// <summary>阶段名（固定段 = Def 的 label；主作业段 = worker 的 ActivityName）。</summary>
        public string title = "";

        /// <summary>量纲（"格"/"件"/"人"），只有主作业段的进度文案要用。</summary>
        public string unit = "";

        public DelegationStageState state = DelegationStageState.Active;

        /// <summary>0..1（固定段 = 已走 tick / 总 tick；主作业段 = worker 进度）。</summary>
        public float progress01;

        /// <summary>进行中的进度文案（"0.3h / 1h" 或 "8.4/40 件"）。</summary>
        public string progressText;

        /// <summary>进行中的动作措辞（"正在侦察环境中…" / "开采中"），与说话人拼成标题行。</summary>
        public string activeText;

        /// <summary>这一段已花的小时数（已完成 = 总耗时；进行中 = 已走的部分）。</summary>
        public float elapsedHours;

        /// <summary>固定段总时长（小时）；主作业段 = -1（它没有固定时长）。</summary>
        public float totalHours = -1f;

        /// <summary>进行中：预计还需多少小时（&lt;0 = 算不出来 ⇒ 不显示）。</summary>
        public float etaHours = -1f;

        /// <summary>已完成的尾注（"Chisa · 耗时 1h"）。</summary>
        public string doneNote;

        /// <summary>进行中：当前这一条旁白（可能为 null）。</summary>
        public string ambient;

        /// <summary>
        /// S28：这一段归「作战任务」组还是「收集任务」组（判据在 `DelegationPhaseDef.InCombatFlow`）。
        /// 主作业段（开采/搜刮/营救）一律算收集 —— 它是"这趟活本身"，
        /// 而营救没有流程段 ⇒ 也不会因此冒出一个错的分组标题（`Rows` 只在有流程段时才分组）。
        /// </summary>
        public bool combat;

        /// <summary>进行中：说话人（"全队" / 人名）。</summary>
        public string speakerName;

        /// <summary>
        /// S22：这一段**还没开始**（小队在休息、阶段进度仍是 0）。UI 据此改口径：
        /// 标题写成「将带队侦察环境」而不是「正在带队侦察环境…」，并且**不显示旁白**
        /// （用户口径：「如果抵达的时候不在工作时间，则小队在外围休息…下面的随机描述暂时不显示」）。
        /// </summary>
        public bool pendingStart;

        /// <summary>
        /// S22：这一段**自己还一点都没走**（前置/收尾段看 `phaseTicks`，主作业段看 `d.Progress`）。
        ///
        /// ⚠️ 不能用 `elapsedHours <= 0` 判断：主作业段的 `elapsedHours` 是**累计作业时长**
        /// （含前面几段固定流程走过的 2h），拿它当"这段还没开始"的判据 ⇒ 休息时照样写「搜刮中」
        /// 并把旁白也画出来（用户截图报的正是这个）。
        /// </summary>
        public bool untouched;

        /// <summary>S22：这一段此刻**被休息时段暂停**（不是停摆、也不是玩家暂停）。条尾的 ETA 要跟着改口径。</summary>
        public bool resting;

        /// <summary>S22：还没开始时用的将来时整句（例：`将带队侦察环境` / `等待搜刮中`）。由 <see cref="DelegationStageList.Build" /> 算好。</summary>
        public string pendingText;

        /// <summary>S22：这一段的执行者（按 `DelegationPhaseDef.skillDef` 挑技能最高者；没配技能则 null ⇒ 沿用旁白那套说话人）。</summary>
        public Pawn executor;

        /// <summary>S22：执行者技能（与 <see cref="executor" /> 配对；UI 用它画「射击 无火 10」）。空 = 不显示技能。</summary>
        public RimWorld.SkillDef executorSkill;

        /// <summary>进行中：当前段是否被随机事件**停摆**（S15）—— 条会冻结、ETA 改写成停摆剩余。</summary>
        public bool stalled;

        /// <summary>停摆还剩多少小时（<see cref="stalled" /> 为真时有效）。</summary>
        public float stallHoursLeft;

        // ---- 下面三个字段是"当前段的口径"，只给 DelegationAmbient.Tick 用，UI 不直接读
        /// <summary>候选池（RIM-13：四个池的并集，够不够格由 `DelegationAmbient.Matches` 按当下状况判）。</summary>
        public List<AmbientLine> pool;
        /// <summary>换句间隔的下界（小时，RIM-13 起是区间）。</summary>
        public float rerollMinHours = DelegationAmbient.DefaultRerollHours;
        /// <summary>换句间隔的上界（小时）。</summary>
        public float rerollMaxHours = DelegationAmbient.DefaultRerollHours;
        public string speakerMode = "RandomPawn";
    }

    public enum DelegationStageRowKind
    {
        Header,        // "流程"
        Position,      // S22：小队此刻的虚拟位置（"位置：外围"）—— 画在「流程」标题**之上**
        GroupTitle,    // S28：流程分组小标题（"作战任务" / "收集任务"）
        Done,          // 一段已完成（一行）
        DoneSummary,   // 多段已完成压成一行汇总
        ActiveTitle,   // 当前段标题（"● 搬运物资（8.4/40 件）"）
        ActiveAmbient, // 当前段旁白（缩进一行）
        ActiveBar,     // 当前段的条 + 「预计还需 Xh」（停摆时改写成「停摆中 · 剩余 Xh」）
        Event,         // 事件留痕一行（S15：进行中唯一的事件出口）
        RestTitle,     // S16：休息置顶那一行（「休息中（距开工 X 小时）」）
        RestAmbient,   // S16：休息内容（睡觉 / 睡前聊天）
    }

    /// <summary>
    /// 一行"已经决定好怎么排版"的阶段行（**只有文字与比例，没有画法**）。
    ///
    /// 为什么要有这一层：原版页签用 `Widgets.Label` + `Widgets.FillableBar`，RadiusUI 皮肤用
    /// `RadiusFont` + `UIKit.Flat.Bar` —— 两端的**画法**必须各自实现（字体、行高、配色都不同），
    /// 但"哪几行、什么文字、条画多少"只能有一份，否则两端迟早说的不是一件事（双端准则）。
    /// </summary>
    public class DelegationStageRow
    {
        public DelegationStageRowKind kind;
        public string text;
        public float frac;
        public DelegationStage stage;

        /// <summary>
        /// S28：激情火苗要插在 <see cref="text" /> 的哪个字符位（-1 = 不插）。
        ///
        /// 用户原话：「流程里面的双火/火要显示在技能名后，例如 智识 双火」——
        /// 所以图标的位置由**造文字的那一份代码**（`ExecutorHead`）一并给出，两端只负责"切两段、中间画图"，
        /// 不去猜技能名在第几个字（猜法会在改文案时静默错位）。
        /// </summary>
        public int iconAt = -1;
    }

    /// <summary>
    /// 「流程」块的**统一阶段序列**（§19.29）：前置段 + 主作业段 + 收尾段拼成一个有序列表，
    /// 并只输出「已完成 + 当前」——**未开始的阶段一个字都不给**（用户 S14 拍板"完全隐藏"）。
    /// 原版页签 / 原版主控台 / RadiusUI 皮肤三端共用这一份。
    /// </summary>
    public static class DelegationStageList
    {
        /// <summary>主作业段在阶段序列里的固定 key。</summary>
        public const string WorkKey = "work";

        /// <summary>
        /// 进行中措辞要不要用"带队"版本（S14 用户要求）：队伍超过 1 人、且这一段是"由某个人代表"
        /// （`speakerMode = RandomPawn`）时才用 —— `All` 模式本来就说"全队"，不需要改口径。
        /// </summary>
        private static bool GroupPhrase(DelegationPhaseDef phase, Delegation d)
        {
            return d?.participants != null && d.participants.Count > 1
                && !"All".Equals(phase.speakerMode) && !"None".Equals(phase.speakerMode);
        }

        /// <summary>
        /// 单人时强制说话人模式为 `RandomPawn`（其实就他自己）：
        /// 单人池的句子写着"一个人…"，标题却写"全队 开采中"会自相矛盾（S14）。
        /// </summary>
        private static string EffectiveSpeakerMode(string mode, Delegation d)
        {
            if (d?.participants != null && d.participants.Count <= 1)
            {
                return "RandomPawn";
            }
            return mode;
        }

        /// <summary>
        /// 按"几个人 + 有没有驮兽 + 这个地方有没有守军"选旁白池（S14）。
        ///
        /// RIM-13 **已删除短路优先级链**（原来"单人池 &gt; 驮兽池 &gt; 有敌情池 &gt; 默认池"，
        /// 选中前面那个就再也不看后面 —— 于是"一个人去打有守军的矿点"永远听不到敌情句）。
        /// 现在四个池**合并成候选集**，每一条各带自己的标签，由
        /// <see cref="DelegationAmbient.Matches" /> 按当下状况判够不够格。
        /// </summary>
        /// <summary>这条委派当前该显示的阶段序列；null = 没有任何可显示的东西。</summary>
        public static List<DelegationStage> Build(Delegation d, Site site)
        {
            if (d == null)
            {
                return null;
            }
            DelegationFlow flow = DelegationFlow.For(d.def);
            DelegationFlowState st = d.flow;
            // RIM-12：这一趟的固定段时长倍率（开工时冻结；老存档 = 1×）
            float scale = DelegationFlow.ScaleOf(st);
            List<DelegationStage> list = new List<DelegationStage>();

            // ---- ① 前置段：只画到"当前段"为止（后面的阶段尚未发生，不剧透）
            // S23：段表走"含冻结"的访问器 —— 有敌情的矿点不该在流程栏里显示"环境侦察"
            List<DelegationPhaseDef> prePhases = flow.PreludeFor(st);
            for (int i = 0; i < prePhases.Count; i++)
            {
                DelegationPhaseDef phase = prePhases[i];
                // ⚠️ 老存档可能没有 flow（`st == null`）：这时**不能**把前置段当成"已完成"，
                //    否则会凭空显示一串没发生过的阶段。没有 flow 就当作"前置已过"，直接显示主作业段。
                bool done = st != null && st.preludeIndex > i;
                bool current = !done && st != null && st.preludeIndex == i;
                if (!done && !current)
                {
                    break;   // 未开始（及其后的全部）：不输出
                }
                DelegationStage stage = new DelegationStage
                {
                    key = "pre" + i,
                    title = phase.PendingLabel,
                    combat = phase.InCombatFlow,   // S28：流程分组（作战 / 收集）
                };
                if (done)
                {
                    MarkDone(stage, st.HoursOf(phase, scale), phase.speakerMode, d);
                }
                else
                {
                    float walked = st.phaseTicks / Delegation.TicksPerHour;
                    float phaseHours = st.HoursOf(phase, scale);
                    stage.elapsedHours = walked;
                    stage.totalHours = phaseHours;
                    stage.untouched = st.phaseTicks <= 0.001f;
                    stage.activeText = phase.ProgressLabelTextFor(GroupPhrase(phase, d));
                    MarkActive(stage, Mathf.Clamp01(st.phaseTicks / DelegationPhaseDef.TicksForHours(phaseHours)),
                        Mathf.Max(0f, phaseHours - walked), d, site,
                        string.Format("{0:0.#}h / {1:0.#}h", walked, phaseHours),
                        phase.AmbientPool,
                        phase.RerollMinHours, phase.RerollMaxHours,
                        EffectiveSpeakerMode(phase.speakerMode, d),
                        phase.skillDef);
                }
                list.Add(stage);
            }

            // ---- ② 主作业段：前置走完才存在（前置还没走完 ⇒ 它还没开始 ⇒ 不输出）
            if (!flow.InPrelude(st))
            {
                DelegationWorker worker = d.Worker;
                string unit = worker?.UnitName ?? "格";
                string speakerMode = EffectiveSpeakerMode(d.def?.workSpeakerMode, d);
                DelegationStage work = new DelegationStage
                {
                    key = WorkKey,
                    title = worker?.ActivityName ?? "作业",
                    unit = unit,
                    activeText = (worker?.ActivityName ?? "作业") + "中",
                    elapsedHours = d.ticksWorked / Delegation.TicksPerHour,
                    // S28：主作业段（开采/搜刮/营救）是"这趟活本身"，归**收集**那一组
                    combat = false,
                };
                bool workDone = st != null && !st.pendingEndReason.NullOrEmpty();
                if (workDone)
                {
                    // 活已经干完、正在走收尾段：这一行转成"已完成"
                    MarkDone(work, work.elapsedHours, speakerMode, d);
                    work.doneNote = string.Format("{0} · 耗时 {1:0.#}h · {2:0.#}/{3} {4}",
                        DelegationAmbient.SpeakerName(d, WorkKey, speakerMode), work.elapsedHours,
                        d.cellsMined, d.totalCells, unit);
                }
                else
                {
                    float eta = DelegationUIUtility.EstimatedFinishDays(d, site) * 24f;
                    // 主作业段"还没开始"的判据是**自己的产出为 0**（`d.Progress`），不是 elapsedHours
                    work.untouched = d.Progress <= 0.001f;
                    MarkActive(work, d.Progress, eta, d, site,
                        string.Format("{0:0.#}/{1} {2}", d.cellsMined, d.totalCells, unit),
                        d.def?.WorkAmbientPool,
                        d.def?.WorkRerollMinHours ?? DelegationAmbient.DefaultRerollHours,
                        d.def?.WorkRerollMaxHours ?? DelegationAmbient.DefaultRerollHours,
                        speakerMode, null);
                }
                list.Add(work);
            }

            // ---- ③ 收尾段：只有"已经出现收工理由"之后才有意义
            if (st != null && !st.pendingEndReason.NullOrEmpty())
            {
                List<DelegationPhaseDef> sufPhases = flow.SuffixFor(st);
                for (int j = 0; j < sufPhases.Count; j++)
                {
                    DelegationPhaseDef phase = sufPhases[j];
                    bool done = st.suffixIndex > j;
                    bool current = st.suffixIndex == j;
                    if (!done && !current)
                    {
                        break;
                    }
                    DelegationStage stage = new DelegationStage
                    {
                        key = "suf" + j,
                        title = phase.PendingLabel,
                        combat = phase.InCombatFlow,   // S28：流程分组（作战 / 收集）
                    };
                    if (done)
                    {
                        MarkDone(stage, st.HoursOf(phase, scale), phase.speakerMode, d);
                    }
                    else
                    {
                        float walked = st.phaseTicks / Delegation.TicksPerHour;
                        float phaseHours = st.HoursOf(phase, scale);
                        stage.elapsedHours = walked;
                        stage.totalHours = phaseHours;
                        stage.untouched = st.phaseTicks <= 0.001f;
                        stage.activeText = phase.ProgressLabelTextFor(GroupPhrase(phase, d));
                        MarkActive(stage, Mathf.Clamp01(st.phaseTicks / DelegationPhaseDef.TicksForHours(phaseHours)),
                            Mathf.Max(0f, phaseHours - walked), d, site,
                            string.Format("{0:0.#}h / {1:0.#}h", walked, phaseHours),
                            phase.AmbientPool,
                            phase.RerollMinHours, phase.RerollMaxHours,
                            EffectiveSpeakerMode(phase.speakerMode, d),
                            phase.skillDef);
                    }
                    list.Add(stage);
                }
            }

            // S15：停摆标记挂到"当前段"上 —— 渲染时条会冻结并改写成「停摆中 · 剩余 Xh」。
            // 为什么**不**新插一个 stall 阶段：阶段序列的"当前段"必须唯一（旁白掷定与 Rows 排版都依赖它），
            // 插一段立刻就是两个 Active。挂在当前段上视觉等价，而且四端自动一致。
            DelegationStage activeStage = ActiveStage(list);
            if (activeStage != null && d.IsStalled(GenTicks.TicksAbs))
            {
                activeStage.stalled = true;
                activeStage.stallHoursLeft = Mathf.Max(0f,
                    (d.stalledUntilTickAbs - GenTicks.TicksAbs) / Delegation.TicksPerHour);
            }

            // S22：**休息期间的"当前段"是被暂停的那一段** ——
            //   ① 文案改成等待式（主作业段 = 「等待搜刮中」；固定段 = 「将带队侦察环境」/「将侦察环境」）；
            //   ② **一律不显示旁白**：那一段现在没在发生（用户口径："不显示下面的随机描述"）。
            // 判据用 `untouched`（这一段自己有没有产出）而不是 elapsedHours —— 见该字段的注释。
            if (activeStage != null && site != null && !d.paused
                && !d.IsStalled(GenTicks.TicksAbs)
                && !d.IsWorkTime(site, GenTicks.TicksAbs))
            {
                activeStage.resting = true;
                activeStage.pendingStart = activeStage.untouched;
                if (activeStage.pendingStart)
                {
                    bool group = d.participants != null && d.participants.Count > 1;
                    // 主作业段用它自己的进行中措辞加"等待"前缀（`搜刮中` → `等待搜刮中`，用户给的原话）
                    activeStage.pendingText = activeStage.key == WorkKey
                        ? "等待" + activeStage.activeText
                        : (group ? "将带队" : "将") + activeStage.title;
                }
                activeStage.ambient = null;
            }
            return list.Count == 0 ? null : list;
        }

        /// <summary>当前正在进行的那个阶段（没有则 null）。旁白掷定靠它取"当前段的口径"。</summary>
        public static DelegationStage ActiveStage(List<DelegationStage> stages)
        {
            if (stages == null)
            {
                return null;
            }
            for (int i = 0; i < stages.Count; i++)
            {
                if (stages[i].state == DelegationStageState.Active)
                {
                    return stages[i];
                }
            }
            return null;
        }

        /// <summary>
        /// S22：小队**当前所处的虚拟位置**（流程栏顶部那一行：外围 / 营地 / 目标点内部）。
        ///
        /// 判据：按流程顺序把"已经过去"的段（已完成 + 当前）扫一遍，取**最后一个写了
        /// `squadPosition` 的段**；主作业期间取 `DelegationDef.workSquadPosition`。
        /// 没有流程 / 一个都没写 ⇒ null（不显示位置行，免得凭空冒出"外围"这种词）。
        ///
        /// 为什么不复用 `Build()`：这只是"人现在在哪儿"，与阶段行的排版无关，单独扫一遍更便宜也更直白。
        /// </summary>
        public static string SquadPosition(Delegation d)
        {
            if (d?.def == null)
            {
                return null;
            }
            DelegationFlow flow = DelegationFlow.For(d.def);
            DelegationFlowState st = d.flow;
            string pos = null;

            // S23：同样走"含冻结"的段表 —— 否则有敌情的矿点在"外围"之外还会冒出无威胁流程的位置
            List<DelegationPhaseDef> prePhases = flow.PreludeFor(st);
            for (int i = 0; i < prePhases.Count; i++)
            {
                bool done = st != null && st.preludeIndex > i;
                bool current = st != null && st.preludeIndex == i;
                if (!done && !current)
                {
                    break;
                }
                string p = prePhases[i].squadPosition;
                if (!p.NullOrEmpty())
                {
                    pos = p;
                }
            }

            if (!flow.InPrelude(st) && !d.def.workSquadPosition.NullOrEmpty())
            {
                pos = d.def.workSquadPosition;
            }

            if (st != null && !st.pendingEndReason.NullOrEmpty())
            {
                List<DelegationPhaseDef> sufPhases = flow.SuffixFor(st);
                for (int j = 0; j < sufPhases.Count; j++)
                {
                    bool done = st.suffixIndex > j;
                    bool current = st.suffixIndex == j;
                    if (!done && !current)
                    {
                        break;
                    }
                    string p = sufPhases[j].squadPosition;
                    if (!p.NullOrEmpty())
                    {
                        pos = p;
                    }
                }
            }
            return pos;
        }

        /// <summary>
        /// 排版成"行"：标题 + 已完成（超过 <paramref name="maxDoneRows" /> 段就压成一行汇总）+ 当前段三行。
        /// 两端各自按 kind 画，条与配色各用自家的件。
        /// </summary>
        public static List<DelegationStageRow> Rows(List<DelegationStage> stages, int maxDoneRows, string header,
            DelegationEventLogEntry lastEvent = null, string restTitle = null, string restAmbient = null)
        {
            List<DelegationStageRow> rows = new List<DelegationStageRow>();
            if (stages.NullOrEmpty() && restTitle.NullOrEmpty())
            {
                return rows;
            }
            if (!header.NullOrEmpty())
            {
                rows.Add(new DelegationStageRow { kind = DelegationStageRowKind.Header, text = header });
            }

            // S16：休息**置顶**（用户要求）—— 休息时段进度本来就不动，把"在休息 + 休息内容
            // （睡觉 / 睡前聊天）"放最上面，玩家一眼就知道现在为什么没有进度。
            // 做成"两行"而不是新阶段：阶段序列的"当前段"必须唯一（旁白掷定与 Rows 排版都依赖它）。
            if (!restTitle.NullOrEmpty())
            {
                rows.Add(new DelegationStageRow { kind = DelegationStageRowKind.RestTitle, text = restTitle });
                if (!restAmbient.NullOrEmpty())
                {
                    rows.Add(new DelegationStageRow { kind = DelegationStageRowKind.RestAmbient, text = restAmbient });
                }
            }
            if (stages == null)
            {
                return rows;
            }

            List<DelegationStage> done = new List<DelegationStage>();
            DelegationStage active = null;
            for (int i = 0; i < stages.Count; i++)
            {
                if (stages[i].state == DelegationStageState.Done)
                {
                    done.Add(stages[i]);
                }
                else if (active == null)
                {
                    active = stages[i];
                }
            }

            // S28：**阶段行先分两束收着**（作战 / 收集），最后再拼回去 —— 组标题只在"有流程段"时插。
            //
            // 为什么这样拼是安全的：组内保持原先后，而**现实里的段表顺序恰好就是"作战在前、收集在后"**
            // （采矿有敌情那条 = 战术侦察/战斗评估/交战 → 建立营地 → 开采 → 撤离），
            // 所以分组不会把时间顺序打乱。要是哪天出现"收集 → 作战 → 收集"的段表，
            // 组内顺序仍然正确，只是组的位置会集中 —— 届时要改的是段表，不是这里。
            List<DelegationStageRow> combatRows = new List<DelegationStageRow>();
            List<DelegationStageRow> collectRows = new List<DelegationStageRow>();
            bool hasPhaseStage = false;
            for (int i = 0; i < stages.Count; i++)
            {
                if (stages[i].key != WorkKey)
                {
                    hasPhaseStage = true;
                    break;
                }
            }

            if (done.Count > 0 && done.Count <= maxDoneRows)
            {
                for (int i = 0; i < done.Count; i++)
                {
                    DelegationStage s = done[i];
                    (s.combat ? combatRows : collectRows).Add(new DelegationStageRow
                    {
                        kind = DelegationStageRowKind.Done,
                        stage = s,
                        text = s.doneNote.NullOrEmpty() ? s.title : s.title + "（" + s.doneNote + "）",
                    });
                }
            }
            else if (done.Count > 0)
            {
                // 压成一行：`侦察 · 移动 · 破门（已完成 3 段，共 4h）`
                // 汇总一行里**不分组**（它是"已完成多少"的计数，拆成两行反而看不清总数）；
                // 归属按其中**最后**一段算 —— 那一行紧跟在当前段的组上面，读起来最顺。
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                float hours = 0f;
                for (int i = 0; i < done.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(" · ");
                    }
                    sb.Append(done[i].title);
                    hours += done[i].elapsedHours;
                }
                DelegationStage lastDone = done[done.Count - 1];
                (lastDone.combat ? combatRows : collectRows).Add(new DelegationStageRow
                {
                    kind = DelegationStageRowKind.DoneSummary,
                    stage = lastDone,
                    text = string.Format("{0}（已完成 {1} 段，共 {2:0.#}h）", sb, done.Count, hours),
                });
            }

            if (active != null)
            {
                // `● Chisa 正在侦察环境中…（0.3h / 1h）` / `● 全队 搬运中（8.4/40 件）`
                // S22：还没开始的段用将来时 —— `● Aemeath（射击 🔥 10）将带队侦察环境（0h / 1h）`
                //
                // S28：火苗从"整行最左边"挪到**技能名之后**（用户：「双火/火要显示在技能名后，
                // 例如 智识 双火」）。位置由 ExecutorHead 一并给出（`iconAt`），两端只切两段、中间画图。
                int iconAt;
                string skillHead = ExecutorHead(active, out iconAt);
                string head = skillHead.NullOrEmpty()
                    ? (active.speakerName.NullOrEmpty() ? "" : active.speakerName + " ")
                    : skillHead;
                string what = active.pendingStart && !active.pendingText.NullOrEmpty()
                    ? active.pendingText
                    : active.activeText;
                (active.combat ? combatRows : collectRows).Add(new DelegationStageRow
                {
                    kind = DelegationStageRowKind.ActiveTitle,
                    stage = active,
                    frac = active.progress01,
                    text = string.Format("{0}{1}（{2}）", head, what, active.progressText),
                    iconAt = iconAt,
                });
                // 还没开始 ⇒ 不显示旁白（用户口径："下面的随机描述暂时不显示"）
                if (!active.pendingStart && !active.ambient.NullOrEmpty())
                {
                    (active.combat ? combatRows : collectRows).Add(new DelegationStageRow
                    {
                        kind = DelegationStageRowKind.ActiveAmbient,
                        stage = active,
                        text = active.ambient,
                    });
                }
                (active.combat ? combatRows : collectRows).Add(new DelegationStageRow
                {
                    kind = DelegationStageRowKind.ActiveBar,
                    stage = active,
                    frac = active.progress01,
                    // 停摆时条冻结（进度本来就不涨）并改写成"停摆中 · 剩余 Xh" ——
                    // 这是"进度停了"的唯一可见解释，不能省。
                    //
                    // S22：休息时段改口径 —— `etaHours` 算的是**还要多少作业时间**，
                    // 写「预计还需 0.2h」会被读成"还要等 0.2 小时"（实际要等到明天）。改成「开工后还需 Xh」：
                    // 信息一个字没少，但不再骗人（"距开工 X 小时"由置顶那一行说）。
                    text = active.stalled
                        ? string.Format("停摆中 · 剩余 {0:0.#}h", active.stallHoursLeft)
                        : (active.etaHours >= 0f
                            ? (active.resting
                                ? string.Format("开工后还需 {0:0.#}h", active.etaHours)
                                : string.Format("预计还需 {0:0.#}h", active.etaHours))
                            : null),
                });
            }

            // S15：事件留痕那一行（最近一条）。事件不再发信之后，**这是进行中唯一的事件出口**
            // （另一条路是底部按钮角标 → 主控台；结束时还有报告信）。
            //
            // S29：**出自作战段的那条**（例：交战结算的"交战结束（Victory…）伤亡落实：…"）改成排进
            // 「作战任务」组内 —— 用户原话「图一的信息应该在作战任务里面」。它原来一律吊在流程块最末尾，
            // 而那一刻正在开采（收集组），看上去就像开采留下的东西。随机事件仍留在末尾。
            DelegationStageRow eventRow = null;
            if (lastEvent != null)
            {
                eventRow = new DelegationStageRow
                {
                    kind = DelegationStageRowKind.Event,
                    text = string.Format("{0}：{1}", lastEvent.Label, lastEvent.detail ?? ""),
                };
                if (lastEvent.combatPhase)
                {
                    combatRows.Add(eventRow);
                }
            }

            // ---- S28：拼回主列表 —— 作战组在前、收集组在后，各带一个小标题
            //      （标题文字直接复用三段段名的同一份常量，所以"流程里的组名"与"主列的段名"永不分叉）
            if (hasPhaseStage)
            {
                if (combatRows.Count > 0)
                {
                    rows.Add(new DelegationStageRow
                    {
                        kind = DelegationStageRowKind.GroupTitle,
                        text = DelegationUIUtility.SectionCombat,
                    });
                    rows.AddRange(combatRows);
                }
                if (collectRows.Count > 0)
                {
                    rows.Add(new DelegationStageRow
                    {
                        kind = DelegationStageRowKind.GroupTitle,
                        text = DelegationUIUtility.SectionCollect,
                    });
                    rows.AddRange(collectRows);
                }
            }
            else
            {
                // 这条委派没有流程段（例：两条营救）⇒ 一个组标题都不插，画面与 S28 之前逐字一致
                rows.AddRange(combatRows);
                rows.AddRange(collectRows);
            }

            if (eventRow != null && !lastEvent.combatPhase)
            {
                rows.Add(eventRow);
            }
            return rows;
        }

        // ---- 内部：把一段标成已完成 / 进行中（只有这里设置，避免两处口径分叉）

        private static void MarkDone(DelegationStage stage, float hours, string speakerMode, Delegation d)
        {
            stage.state = DelegationStageState.Done;
            stage.elapsedHours = hours;
            stage.totalHours = hours;
            stage.progress01 = 1f;
            stage.speakerMode = speakerMode.NullOrEmpty() ? "RandomPawn" : speakerMode;
            stage.doneNote = string.Format("{0} · 耗时 {1:0.#}h",
                DelegationAmbient.SpeakerName(d, stage.key, stage.speakerMode), hours);
        }

        private static void MarkActive(DelegationStage stage, float frac, float etaHours, Delegation d, Site site,
            string progressText, List<AmbientLine> pool, float rerollMinHours, float rerollMaxHours, string speakerMode,
            RimWorld.SkillDef skill = null)
        {
            stage.state = DelegationStageState.Active;
            stage.progress01 = Mathf.Clamp01(frac);
            stage.etaHours = etaHours;
            stage.progressText = progressText;
            stage.pool = pool;
            stage.rerollMinHours = rerollMinHours <= 0f ? DelegationAmbient.DefaultRerollHours : rerollMinHours;
            stage.rerollMaxHours = rerollMaxHours <= 0f ? DelegationAmbient.DefaultRerollHours : rerollMaxHours;
            stage.speakerMode = speakerMode.NullOrEmpty() ? "RandomPawn" : speakerMode;

            // S22：配了技能的段 ⇒ 执行者 = **技能最高者**（用户要求"系统自动选择小人"），
            // 并且他同时就是这一段的叙事主语（否则会出现"名字写着 A、实际按 B 的技能算"这种两套口径）。
            // 没配技能 ⇒ 沿用旁白那套按 key 哈希抽的人（老行为逐字不变）。
            stage.executor = PickExecutor(skill, d);
            stage.executorSkill = stage.executor != null ? skill : null;
            stage.speakerName = stage.executor != null
                ? stage.executor.LabelShort
                : DelegationAmbient.SpeakerName(d, stage.key, stage.speakerMode);

            // S26：用户要求「暂时关闭一下流程的随机描述，有些不符合逻辑」⇒ 由 Mod 设置统一开关。
            // 关掉时**连掷定都不做**（不留"关了但存档里还在换"的痕迹）。
            // RIM-13/14：选池与条件判据都在 `DelegationAmbient` 里（多标签并集 + 时机/说话人判据）。
            string line = DelegationUIUtility.AmbientEnabled
                ? DelegationAmbient.Current(d, site, stage.key, pool, stage.progress01, stage.executor)
                : null;
            // 旁白里的 `{0}` = 说话人（"Chisa 正带队向 物品藏匿点 进发…"那种句子要用它）
            if (!line.NullOrEmpty() && !stage.speakerName.NullOrEmpty())
            {
                line = line.Replace("{0}", stage.speakerName);
            }
            stage.ambient = line;
        }

        /// <summary>
        /// 按技能挑执行者：**技能等级最高者**，等级并列时取名单里靠前的那个
        /// （`d.participants` 的顺序来自存档，跨帧跨读档都稳定 ⇒ 不会"跳人"）。
        /// </summary>
        private static Pawn PickExecutor(RimWorld.SkillDef skill, Delegation d)
        {
            if (skill == null || d?.participants == null || d.participants.Count == 0)
            {
                return null;
            }
            Pawn best = null;
            int bestLevel = -1;
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                if (p?.skills == null || p.Dead || p.Downed)
                {
                    continue;
                }
                int lv = p.skills.GetSkill(skill)?.Level ?? 0;
                if (lv > bestLevel)
                {
                    bestLevel = lv;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>
        /// 执行者的「技能 激情 等级」那一段（例：`射击 🔥 10`）—— 用户草图里的那个括号。
        ///
        /// ⚠️ S26 起激情**不再用文字**：用户点名要图标（「双火/火/无火 是否可以添加图标」）⇒
        /// 这里只给"技能 + 等级"，火苗由两端各自画 <see cref="DelegationUIUtility.PassionIconFor" />
        /// 返回的原版贴图（取贴图只有那一份实现，所以两端不会分叉 —— 这正是 S22 当初的顾虑）。
        ///
        /// ⚠️ S28 起火苗位置也由这里给出（`iconAt`）：用户要「双火/火显示在技能名后，例如 智识 双火」，
        /// 于是返回 `<c>人名（技能名 等级）</c>` 的同时给出"图标该插在第几个字符"＝人名 + `（` + 技能名
        /// 之后、等级之前。**两端都不许自己找技能名的位置**：改一次文案（加个空格）那种算法就静默错位。
        /// 没有火苗（无火 / 没配技能）时 `iconAt = -1`。
        ///
        /// 返回 null = 这一段没有执行者技能（调用方退回"人名 + 动作"的老写法）。
        /// </summary>
        private static string ExecutorHead(DelegationStage stage, out int iconAt)
        {
            iconAt = -1;
            if (stage?.executor == null || stage.executorSkill == null || stage.executor.skills == null)
            {
                return null;
            }
            RimWorld.SkillRecord rec = stage.executor.skills.GetSkill(stage.executorSkill);
            if (rec == null)
            {
                return null;
            }
            string skillName = stage.executorSkill.LabelCap;
            string skillText = string.Format("{0} {1}", skillName, rec.Level);
            if (DelegationUIUtility.PassionIconFor(stage) != null)
            {
                // 相对整行（`text`）的下标 —— head 本身就是 text 的开头，所以可以直接用
                iconAt = (stage.speakerName?.Length ?? 0) + 1 + skillName.Length;
            }
            return stage.speakerName + "（" + skillText + "）";
        }
    }
}
