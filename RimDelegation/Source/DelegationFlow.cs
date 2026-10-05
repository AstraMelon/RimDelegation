using System.Collections.Generic;
using System.Text;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一次委派的**固定流程进度**（§19.27）。挂在 <see cref="Delegation"/> 上、随存档持久化。
    ///
    /// 为什么把三个计数器单独打包成 IExposable 而不是散在 Delegation 上：
    /// Delegation 已经有 30+ 个字段，而这一组只在"这条 Def 配了 flowPhases"时才有意义，
    /// 打包之后 Scribe 只有一行、语义也不会跟别的字段混在一起。
    /// </summary>
    public class DelegationFlowState : IExposable
    {
        /// <summary>前置流程走到第几段（0..前置段数）。等于段数 = 前置已走完，可以开工。</summary>
        public int preludeIndex;

        /// <summary>收尾流程走到第几段（0..收尾段数）。只在 `pendingEndReason` 非空时有意义。</summary>
        public int suffixIndex;

        /// <summary>当前段已经走过的 ticks（前置与收尾共用 —— 两者在时间上互斥）。</summary>
        public float phaseTicks;

        /// <summary>
        /// 非空 = **已经在收尾**：某个收工条件（取空/装满/按天数/按配额）已经成立，
        /// 理由先记在这里，等收尾段走完再用它发完成信。
        ///
        /// 为什么理由要存下来：收尾期间 `TargetDepleted` / `WorkerEndReason` 仍然成立，
        /// 但那时再问一次可能已经问不出同一个理由（例：撤离途中车队被卸了货、不再"装满"）。
        /// </summary>
        public string pendingEndReason;

        // ---- 阶段旁白（§19.29）：只有"当前阶段"的这一组有意义，阶段一换就重掷 ----

        /// <summary>当前旁白属于哪个阶段（"pre0"/"work"/"suf1"）；换阶段 ⇒ 必掷一条新的。</summary>
        public string ambientStageKey;

        /// <summary>当前旁白在下标池里的下标。</summary>
        public int ambientVariant;

        /// <summary>
        /// RIM-13：当前旁白的**稳定 id**（`来源池#池内下标` 的 FNV-1a）。
        ///
        /// 为什么不再靠下标：候选集随情境变化（单人/驮兽/守军/时段/条件），同一个下标在不同情境下
        /// 指向的不是同一句话；而且 RIM-14 给条目加了条件字段后，旧存档的下标语义已经失效。
        /// 存 id 之后"读档换一句"只在**池内容真的变了**时才发生（找不到就重掷，不会越界、不会空指针）。
        /// </summary>
        public int ambientPickId;

        /// <summary>RIM-13：下一次重掷还要等多少 ticks（区间间隔逐次掷定；0 = 下 tick 就重掷）。</summary>
        public int ambientNextRerollTicks;

        /// <summary>RIM-13：最近掷定过的几条（不重复窗口，最多 3 条，见 `MaxRecentWindow`）。</summary>
        public List<int> ambientRecent;

        /// <summary>RIM-14：`once` 标签的旁白已经说过的那些 id（每支委派各记一份）。</summary>
        public List<int> ambientSeenOnce;

        /// <summary>
        /// RIM-12：**开工那一刻冻结**的"流程固定时长倍率"。
        ///
        /// 为什么冻结而不是实时读设置：`flow.phaseTicks` 是绝对进度，而 `DelegationPhaseDef.Ticks`
        /// 若实时乘当前设置，玩家在委派进行中改一下倍率，条与文字会**在同一帧里跳变**
        /// （用户拍板 `甲`：倍率只对**新开工**的委派生效）。
        /// </summary>
        public float flowHoursScale = 1f;

        /// <summary>
        /// RIM-11：**开工那一刻掷定**的"带抖动的段时长"（`hoursJitter` 的落点）。
        ///
        /// 存成"段名 → 小时数"两条平行列表（而不是按下标），因为段表本身可能被冻结过滤
        /// （有敌情 / 无敌情两条岔路），按下标对不齐；按 defName 查也天然容忍"别的 mod 撤了一段"。
        /// 空/null = 这一段没配抖动（老存档也是空 ⇒ 行为与改动前逐字一致）。
        /// </summary>
        public List<string> jitteredPhase;

        /// <summary>见 <see cref="jitteredPhase" />：与它一一对应的**最终**小时数（已含倍率与偏移）。</summary>
        public List<float> jitteredHours;

        /// <summary>
        /// RIM-11/12：这一段在**这一趟**里的实际小时数 = 开工时掷定的抖动值（若有），否则 `hours × 倍率`。
        /// 显示（条/字/ETA）与实际推进（`AdvancePrelude`/`AdvanceSuffix`）都只能走这一个入口。
        /// </summary>
        public float HoursOf(DelegationPhaseDef phase, float scale)
        {
            if (phase == null)
            {
                return 0f;
            }
            if (jitteredPhase != null && jitteredHours != null)
            {
                int n = Mathf.Min(jitteredPhase.Count, jitteredHours.Count);
                for (int i = 0; i < n; i++)
                {
                    if (jitteredPhase[i] == phase.defName)
                    {
                        return jitteredHours[i];
                    }
                }
            }
            return phase.EffectiveHours(scale);
        }

        /// <summary>见 <see cref="HoursOf" />：折算成 ticks（最小 1）。</summary>
        public int TicksOf(DelegationPhaseDef phase, float scale)
        {
            return DelegationPhaseDef.TicksForHours(HoursOf(phase, scale));
        }

        /// <summary>上一次掷定的时刻（用来算"每 0.5h 换一条"）。</summary>
        public int ambientPickedTick;

        // ---- S23：开局冻结的段表（「有敌情」两岔流程）--------------------------
        //
        // 为什么必须冻结：进度只记下标（preludeIndex），而段表以前是"每条 Def 一份"的常量。
        // 一旦段表随"这个地方有没有守军"变化，中途任何一次态变（读档后 hidden 部件揭示、
        // 将来某条事件给地点加守军）都会让下标指到**别的段**——而存档里只有下标，无从纠正。
        // 所以开工那一刻按当前敌情解析一次，把生效的段名存下来，之后这次委派就按它走到底。

        /// <summary>
        /// 本次委派实际生效的前置段（defName）。**null = 这些段里没有任何条件** ⇒ 直接用 Def 的全段
        /// （省一份存档数据；老存档也是 null，语义与加这个字段之前完全一致）。
        /// </summary>
        public List<string> activePrelude;

        /// <summary>同上，收尾段。</summary>
        public List<string> activeSuffix;

        /// <summary>
        /// S25：玩家已经下达过的**流程信号**（`DelegationPhaseDef.pauseUntilSignal` 里那些名字）。
        /// null/空 = 从来没有段在等指令（绝大多数委派都是这种，老存档也读成空）。
        /// </summary>
        public List<string> signals;

        /// <summary>这个信号下过了吗。</summary>
        public bool HasSignal(string name)
        {
            if (name.NullOrEmpty() || signals == null)
            {
                return false;
            }
            return signals.Contains(name);
        }

        /// <summary>下达一个流程信号（幂等）。</summary>
        public void GiveSignal(string name)
        {
            if (name.NullOrEmpty())
            {
                return;
            }
            if (signals == null)
            {
                signals = new List<string>();
            }
            if (!signals.Contains(name))
            {
                signals.Add(name);
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref preludeIndex, "preludeIndex", 0);
            Scribe_Values.Look(ref suffixIndex, "suffixIndex", 0);
            Scribe_Values.Look(ref phaseTicks, "phaseTicks", 0f);
            Scribe_Values.Look(ref pendingEndReason, "pendingEndReason");
            Scribe_Values.Look(ref ambientStageKey, "roAmbientStageKey");
            Scribe_Values.Look(ref ambientVariant, "roAmbientVariant", 0);
            Scribe_Values.Look(ref ambientPickedTick, "roAmbientPickedTick", 0);
            Scribe_Values.Look(ref ambientPickId, "roAmbientPickId", 0);
            Scribe_Values.Look(ref ambientNextRerollTicks, "roAmbientNextReroll", 0);
            Scribe_Collections.Look(ref ambientRecent, "roAmbientRecent", LookMode.Value);
            Scribe_Collections.Look(ref ambientSeenOnce, "roAmbientSeenOnce", LookMode.Value);
            Scribe_Values.Look(ref flowHoursScale, "roFlowHoursScale", 1f);
            Scribe_Collections.Look(ref jitteredPhase, "roJitteredPhase", LookMode.Value);
            Scribe_Collections.Look(ref jitteredHours, "roJitteredHours", LookMode.Value);
            Scribe_Collections.Look(ref activePrelude, "roActivePrelude", LookMode.Value);
            Scribe_Collections.Look(ref activeSuffix, "roActiveSuffix", LookMode.Value);

            // S23：段进入钩子的记号（哪一段已经"进入过"）—— 必须存档：
            // 交战段的钩子会真的打一场，读档后重跑一次等于白送一场战斗。
            Scribe_Values.Look(ref enteredPhase, "roEnteredPhase");
            Scribe_Collections.Look(ref signals, "roSignals", LookMode.Value);
        }

        /// <summary>
        /// S23：**段进入钩子**的记号（`"pre:段名"` / `"suf:段名"`）。
        /// 与 <see cref="preludeIndex" /> 分开记是因为"进入第 0 段"与"下标还是 0"在开工第一 tick
        /// 无法区分 —— 而钩子必须恰好执行一次。
        /// </summary>
        public string enteredPhase;

        /// <summary>
        /// 把冻结下来的 defName 清单解析回段对象。解析不到就跳过（别的 mod 撤了它 / XML 改了名），
        /// 与 `DelegationFlow.For` 里"null 段跳过"同一条规矩。
        ///
        /// **故意不缓存**：热重载会重建 Def 实例，缓存住旧实例会让"改 XML 没反应"
        /// （Def 侧那份缓存为这件事专门比过 List 引用，这里更简单 —— 每次查表，段数只有个位数）。
        /// </summary>
        public static List<DelegationPhaseDef> Resolve(List<string> names)
        {
            if (names == null)
            {
                return null;
            }
            List<DelegationPhaseDef> list = new List<DelegationPhaseDef>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                DelegationPhaseDef p = DefDatabase<DelegationPhaseDef>.GetNamedSilentFail(names[i]);
                if (p != null)
                {
                    list.Add(p);
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 把 <see cref="DelegationDef.flowPhases" /> 解释成"前置段 + 收尾段"两串，并负责推进与显示。
    ///
    /// 语义（DESIGN §19.27）：
    ///   · 前置段按 `flowPhases` 里的顺序在**作业之前**依次走完，期间 `DelegationWorker.Tick` 不被调用
    ///     —— 这就是"固定时间"的落点：那段时间不是速率问题，是流程问题；
    ///   · 收尾段在**任何**收工条件成立之后才走，走完才真正 `Complete`；
    ///   · **中断不走收尾** —— 撤不出来是因为被打断了，本来就该立刻结束（见 WorldObjectComp_Delegations.Abort）。
    /// </summary>
    public class DelegationFlow
    {
        /// <summary>没有流程（采矿/营救就是这种）：两个列表都空，所有判定都退化成"立即完成"。</summary>
        public static readonly DelegationFlow Empty =
            new DelegationFlow(new List<DelegationPhaseDef>(), new List<DelegationPhaseDef>());

        public readonly List<DelegationPhaseDef> prelude;
        public readonly List<DelegationPhaseDef> suffix;

        private DelegationFlow(List<DelegationPhaseDef> prelude, List<DelegationPhaseDef> suffix)
        {
            this.prelude = prelude;
            this.suffix = suffix;
        }

        public bool HasPhases => prelude.Count > 0 || suffix.Count > 0;

        public bool HasSuffix => suffix.Count > 0;

        // ── S23：读段表的唯一入口 ───────────────────────────────────────────
        //
        // 全部读表点都必须走这两个方法（只读 `prelude` / `suffix` 字段会静默忽略冻结结果，
        // 症状是"有敌情的矿点走了无威胁那条流程"）。冻结清单为空/null 时退回 Def 的全段。

        /// <summary>本次委派实际生效的前置段（含冻结）。</summary>
        public List<DelegationPhaseDef> PreludeFor(DelegationFlowState st)
        {
            return DelegationFlowState.Resolve(st?.activePrelude) ?? prelude;
        }

        /// <summary>本次委派实际生效的收尾段（含冻结）。</summary>
        public List<DelegationPhaseDef> SuffixFor(DelegationFlowState st)
        {
            return DelegationFlowState.Resolve(st?.activeSuffix) ?? suffix;
        }

        // ── Def → 流程（缓存在 Def 自己身上）────────────────────────────────
        //
        // 这段会被每 tick 的 TickDelegation 与每帧的 UI 调用，所以必须缓存；
        // 但缓存**不能**用 Dictionary<defName, …>：热重载会重建 Def 实例，
        // 按名字缓存会一直返回旧段表（改 XML 没反应）。
        // 所以缓存在 Def 实例上，并记住"是从哪个 flowPhases 列表算出来的" ——
        // 热重载时 `ParseAndProcessXML` 会把 `flowPhases` 换成新的 List 实例，
        // 引用一比就知道要重算（Def 对象身份本身是保留的）。
        public static DelegationFlow For(DelegationDef def)
        {
            if (def?.flowPhases.NullOrEmpty() ?? true)
            {
                return Empty;
            }
            if (def.cachedFlow != null && ReferenceEquals(def.cachedFlowSource, def.flowPhases))
            {
                return def.cachedFlow;
            }

            List<DelegationPhaseDef> pre = new List<DelegationPhaseDef>();
            List<DelegationPhaseDef> post = new List<DelegationPhaseDef>();
            for (int i = 0; i < def.flowPhases.Count; i++)
            {
                DelegationPhaseDef phase = def.flowPhases[i];
                if (phase == null)
                {
                    continue;   // ConfigErrors 已经报过；这里跳过，别让一次写错的 defName 把 UI 打崩
                }
                (phase.afterWork ? post : pre).Add(phase);
            }
            DelegationFlow flow = (pre.Count == 0 && post.Count == 0) ? Empty : new DelegationFlow(pre, post);
            def.cachedFlow = flow;
            def.cachedFlowSource = def.flowPhases;
            return flow;
        }

        /// <summary>
        /// S23：**开工那一刻冻结**本次生效的段表（甲B 方案，用户 2026-09-27 拍板）。
        ///
        /// 调用点只有一处：<c>WorldObjectComp_Delegations.StartDelegation</c>
        /// （与 S9 抄物资台账同一个位置 —— 都必须紧贴 `new Delegation(...)`，越早越不会漏）。
        ///
        /// 判据是 `ThreatAssessmentEntry.HasThreat(site)`：读的是任务生成时就掷好并存盘的
        /// `SitePartParams.threatPoints`，不需要生成地图。矿点的 `hiddenSitePartsPossible = false`
        /// ⇒ 守军对玩家是可见的（委派对话框里已经写了「守军：威胁点数 …」），
        /// 所以"开工前就定下来"与玩家的预期一致，不是事后翻盘。
        /// </summary>
        public static void Freeze(DelegationDef def, DelegationFlowState st, Site site)
        {
            if (def == null || st == null)
            {
                return;
            }
            DelegationFlow f = For(def);
            bool hasThreat = ThreatAssessmentEntry.HasThreat(site);
            st.activePrelude = Names(f.prelude, hasThreat);
            st.activeSuffix = Names(f.suffix, hasThreat);
            // RIM-12（用户拍板 `甲`）：时长倍率**在这里冻结一次**，与段表同一处、同一时刻。
            // 冻结之后这一趟就按它走到底 —— 玩家中途改设置不会让进度条/文字跳变。
            st.flowHoursScale = RimDelegationMod.Settings?.flowHoursScale ?? 1f;
            if (st.flowHoursScale <= 0f || float.IsNaN(st.flowHoursScale) || float.IsInfinity(st.flowHoursScale))
            {
                st.flowHoursScale = 1f;
            }
            // RIM-11：段时长的**随机偏移**也在这里掷一次（用户口径「一个固定值加减偏移值」）。
            // 掷定结果进存档 ⇒ 读档不重掷、UI 与推进同源。
            FreezeJitter(def, st, f);
            DelegationUtility.LogVerbose(string.Format(
                "冻结流程段：{0} @ {1}（有敌情={2}）⇒ 前置 {3} 段 / 收尾 {4} 段",
                def.defName, site?.Label, hasThreat,
                st.activePrelude?.Count ?? f.prelude.Count,
                st.activeSuffix?.Count ?? f.suffix.Count));
        }

        /// <summary>
        /// 按敌情过滤出一串 defName；**没有任何段带条件时返回 null**（＝不必冻结，省一份存档数据）。
        /// </summary>
        private static List<string> Names(List<DelegationPhaseDef> src, bool hasThreat)
        {
            if (src.NullOrEmpty())
            {
                return null;
            }
            bool anyCondition = false;
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i] != null && src[i].HasCondition)
                {
                    anyCondition = true;
                    break;
                }
            }
            if (!anyCondition)
            {
                return null;
            }
            List<string> names = new List<string>();
            for (int i = 0; i < src.Count; i++)
            {
                DelegationPhaseDef p = src[i];
                if (p != null && p.Matches(hasThreat))
                {
                    names.Add(p.defName);
                }
            }
            return names;
        }

        /// <summary>
        /// RIM-12：这一趟的**流程时长倍率**（开工时冻结；没冻结过/写坏 ⇒ 1×）。
        /// 老存档与"没有 flow 状态"的委派都退回 1×，行为与加倍率之前逐字一致。
        /// </summary>
        public static float ScaleOf(DelegationFlowState st)
        {
            if (st == null || st.flowHoursScale <= 0f || float.IsNaN(st.flowHoursScale)
                || float.IsInfinity(st.flowHoursScale))
            {
                return 1f;
            }
            return st.flowHoursScale;
        }

        /// <summary>
        /// RIM-12：按 Def 算"这条委派的**固定流程**一共多少小时"（前置 + 收尾，按敌情分支过滤）。
        ///
        /// 为什么必须单独有一个纯函数：草稿期**还没有** `Delegation` 实例，复用不了
        /// <see cref="Delegation.FlowRemainingTicks" />；而草稿页那个"预计 X–Y 天完"过去
        /// **完全不含**固定流程 ⇒ 玩家点完「下达」会发现完成时间凭空往后跳 3.5–7 小时（P-A3）。
        /// 段名一并给出来，供草稿页那行玩家可见文案使用。
        /// </summary>
        public static float FixedFlowHours(DelegationDef def, Site site, float scale, out List<string> labels)
        {
            float min;
            float max;
            return FixedFlowHours(def, site, scale, out labels, out min, out max);
        }

        /// <summary>
        /// RIM-11：同上，但额外给出**考虑随机偏移后的上下界**（草稿页那行据此显示"5.5~6.5 小时"）。
        /// 上界只对配了 `hoursJitter` 的段展开 —— 没配抖动时 min == max == 合计。
        /// </summary>
        public static float FixedFlowHours(DelegationDef def, Site site, float scale, out List<string> labels,
            out float minHours, out float maxHours)
        {
            labels = new List<string>();
            minHours = 0f;
            maxHours = 0f;
            if (def == null)
            {
                return 0f;
            }
            DelegationFlow f = For(def);
            if (!f.HasPhases)
            {
                return 0f;
            }
            bool hasThreat = ThreatAssessmentEntry.HasThreat(site);
            float hours = 0f;
            float minAcc = 0f;
            float maxAcc = 0f;
            Accumulate(f.prelude, hasThreat, scale, labels, ref hours, ref minAcc, ref maxAcc);
            Accumulate(f.suffix, hasThreat, scale, labels, ref hours, ref minAcc, ref maxAcc);
            minHours = minAcc;
            maxHours = maxAcc;
            return hours;
        }

        /// <summary>
        /// RIM-11：把带 `hoursJitter` 的段在**开工那一刻**掷一次，结果冻进
        /// <see cref="DelegationFlowState.jitteredHours" />（与段表、倍率同一处、同一时刻）。
        ///
        /// 掷法（brainstorm 定稿）：
        ///   · 分布 = 均匀 `U(base − j, base + j)`；
        ///   · **量化到 0.25 小时**（15 分钟）的网格 —— UI 只显示一位小数，不量化会写出
        ///     "1.8347h" 这种既不可读、也让人误以为精确的小数；
        ///   · 下限 0.25h（再短就不像"做了一件事"了）；
        ///   · 只用**冻结后的段表**（不该给"这次根本不会走的段"掷骰子 —— 那会让读档日志里出现假信息）。
        ///
        /// 一个段都不带抖动 ⇒ **完全不写存档键**（老存档与默认配置的存档大小一字不变）。
        /// </summary>
        private static void FreezeJitter(DelegationDef def, DelegationFlowState st, DelegationFlow f)
        {
            List<DelegationPhaseDef> pre = st.activePrelude != null
                ? DelegationFlowState.Resolve(st.activePrelude)
                : f.prelude;
            List<DelegationPhaseDef> suf = st.activeSuffix != null
                ? DelegationFlowState.Resolve(st.activeSuffix)
                : f.suffix;
            List<string> names = null;
            List<float> hours = null;
            CollectJitter(pre, st.flowHoursScale, ref names, ref hours);
            CollectJitter(suf, st.flowHoursScale, ref names, ref hours);
            st.jitteredPhase = names;
            st.jitteredHours = hours;
            if (names != null)
            {
                DelegationUtility.LogVerbose(string.Format("冻结段时长抖动（{0} 段）：{1}",
                    names.Count, string.Join(" / ", DescribeJitter(names, hours).ToArray())));
            }
        }

        private static void CollectJitter(List<DelegationPhaseDef> src, float scale, ref List<string> names,
            ref List<float> hours)
        {
            if (src.NullOrEmpty())
            {
                return;
            }
            for (int i = 0; i < src.Count; i++)
            {
                DelegationPhaseDef p = src[i];
                if (p == null || p.JitterHours <= 0f)
                {
                    continue;
                }
                if (names == null)
                {
                    names = new List<string>();
                    hours = new List<float>();
                }
                names.Add(p.defName);
                hours.Add(RollJitteredHours(p.EffectiveHours(scale), p.JitterHours));
            }
        }

        /// <summary>均匀取一个值、量化到 0.25h 网格、下限 0.25h（RIM-11 的掷法定稿）。</summary>
        public static float RollJitteredHours(float baseHours, float jitter)
        {
            if (jitter <= 0f)
            {
                return baseHours;
            }
            float raw = Rand.Range(baseHours - jitter, baseHours + jitter);
            float quantized = Mathf.Round(raw * 4f) / 4f;
            return quantized < 0.25f ? 0.25f : quantized;
        }

        private static List<string> DescribeJitter(List<string> names, List<float> hours)
        {
            List<string> list = new List<string>();
            for (int i = 0; i < names.Count && i < hours.Count; i++)
            {
                list.Add(string.Format("{0}={1:0.##}h", names[i], hours[i]));
            }
            return list;
        }

        private static void Accumulate(List<DelegationPhaseDef> src, bool hasThreat, float scale, List<string> labels,
            ref float hours, ref float minHours, ref float maxHours)
        {
            if (src.NullOrEmpty())
            {
                return;
            }
            for (int i = 0; i < src.Count; i++)
            {
                DelegationPhaseDef p = src[i];
                if (p == null || !p.Matches(hasThreat))
                {
                    continue;
                }
                float h = p.EffectiveHours(scale);
                float j = p.JitterHours;
                hours += h;
                minHours += Mathf.Max(0.25f, h - j);
                maxHours += h + j;
                if (labels != null)
                {
                    labels.Add(p.PendingLabel);
                }
            }
        }

        // ── 推进 ────────────────────────────────────────────────────────────

        /// <summary>前置是否还有段没走完。</summary>
        public bool InPrelude(DelegationFlowState st)
        {
            return st != null && st.preludeIndex < PreludeFor(st).Count;
        }

        /// <summary>正在收尾（有收工理由在等着，但收尾段还没走完）。</summary>
        public bool InSuffix(DelegationFlowState st)
        {
            return st != null && !st.pendingEndReason.NullOrEmpty() && st.suffixIndex < SuffixFor(st).Count;
        }

        /// <summary>收尾是否需要走（有收尾段、且还没走完）。</summary>
        public bool NeedsSuffix(DelegationFlowState st)
        {
            List<DelegationPhaseDef> suf = SuffixFor(st);
            return st != null && suf.Count > 0 && st.suffixIndex < suf.Count;
        }

        /// <summary>
        /// 推进前置流程。返回 true = 前置还没走完（本 tick 不该让 worker 干活）。
        /// 走完最后一段的那一 tick 返回 false，好让同一次调用里就能开始作业。
        /// </summary>
        public bool AdvancePrelude(DelegationFlowState st, int delta)
        {
            if (!InPrelude(st) || delta <= 0)
            {
                return InPrelude(st);
            }
            st.phaseTicks += delta;
            List<DelegationPhaseDef> pre = PreludeFor(st);
            while (st.preludeIndex < pre.Count)
            {
                DelegationPhaseDef phase = pre[st.preludeIndex];
                // S25：这一段**在等玩家指令**（`pauseUntilSignal` 还没下达）⇒ 停在它跟前，
                // 既不推进也不触发它的 `onEnter` —— 这就是"远行队待命、等玩家决定打不打"。
                if (!phase.pauseUntilSignal.NullOrEmpty() && !st.HasSignal(phase.pauseUntilSignal))
                {
                    return true;   // 前置"还没走完"：worker 不干活、UI 显示待命
                }
                int need = st.TicksOf(phase, st.flowHoursScale);
                if (st.phaseTicks < need)
                {
                    break;
                }
                st.phaseTicks -= need;
                st.preludeIndex++;
            }
            if (st.preludeIndex >= pre.Count)
            {
                st.phaseTicks = 0f;
                return false;
            }
            return true;
        }

        /// <summary>推进收尾流程。返回 true = 收尾全部走完（调用方应当 Complete(pendingEndReason)）。</summary>
        public bool AdvanceSuffix(DelegationFlowState st, int delta)
        {
            if (st == null)
            {
                return true;   // 老存档 / 没有流程状态：视为"收尾已经走完"
            }
            List<DelegationPhaseDef> suf = SuffixFor(st);
            if (suf.Count == 0)
            {
                return true;
            }
            st.phaseTicks += delta;
            while (st.suffixIndex < suf.Count)
            {
                DelegationPhaseDef phase = suf[st.suffixIndex];
                int need = st.TicksOf(phase, st.flowHoursScale);
                if (st.phaseTicks < need)
                {
                    break;
                }
                st.phaseTicks -= need;
                st.suffixIndex++;
            }
            return st.suffixIndex >= suf.Count;
        }

        /// <summary>
        /// S22：当前这一段是不是**不可打断**的（`DelegationPhaseDef.uninterruptible`）。
        /// 调用方是 `TickDelegation` 的工时门控：为 true 时，即使已经出了工时窗口也**照常推进**这一段
        /// （用户口径：「正在侦察环境的时候，不会因为时段进入了休息而打断」）。
        /// </summary>
        public bool UninterruptibleActive(DelegationFlowState st)
        {
            if (st == null)
            {
                return false;
            }
            // S26：**在等玩家指令的段不算"已经开始"** ⇒ 它的 uninterruptible 不成立。
            // 少了这一条，交战段（uninterruptible=true）会在待命期间绕过工时门控继续往下走
            // ⇒ 玩家还没点「进行交战」，战斗就自己打完了（用户 S26 第 1 条报的正是这个）。
            if (IsGated(st))
            {
                return false;
            }
            List<DelegationPhaseDef> pre = PreludeFor(st);
            if (InPrelude(st))
            {
                return pre[st.preludeIndex].uninterruptible;
            }
            if (InSuffix(st))
            {
                return SuffixFor(st)[st.suffixIndex].uninterruptible;
            }
            return false;
        }

        /// <summary>
        /// S25：此刻流程是不是**卡在一个"等玩家信号"的段之前**（远行队就地待命）。
        /// 不额外存状态：判据完全由"冻结段表 + 已下达的信号"推出来 ⇒ 读档后自然一致。
        /// </summary>
        public bool IsGated(DelegationFlowState st)
        {
            return !ActiveSignal(st).NullOrEmpty();
        }

        /// <summary>此刻在等哪个信号（没有则 null）。</summary>
        public string ActiveSignal(DelegationFlowState st)
        {
            if (st == null)
            {
                return null;
            }
            DelegationPhaseDef phase = null;
            if (InPrelude(st))
            {
                phase = PreludeFor(st)[st.preludeIndex];
            }
            else if (InSuffix(st))
            {
                phase = SuffixFor(st)[st.suffixIndex];
            }
            if (phase == null || phase.pauseUntilSignal.NullOrEmpty())
            {
                return null;
            }
            return st.HasSignal(phase.pauseUntilSignal) ? null : phase.pauseUntilSignal;
        }

        /// <summary>当前正在走的那一段（前置优先；都不在 ⇒ null）。显示与判据共用，避免两处各算一遍。</summary>
        public DelegationPhaseDef ActivePhase(DelegationFlowState st)
        {
            if (st == null)
            {
                return null;
            }
            if (InPrelude(st))
            {
                return PreludeFor(st)[st.preludeIndex];
            }
            if (InSuffix(st))
            {
                return SuffixFor(st)[st.suffixIndex];
            }
            return null;
        }

        /// <summary>
        /// S22：名为 <paramref name="defName" /> 的那一段**是否已经走完**。
        /// 用途：搜刮的「预期获得」要等**破门**完成之后才给玩家看（`DelegationDef.hideItemsUntilPhase`）。
        /// 老存档 / 没配这一段 ⇒ 一律 false（＝还没到），宁可少给信息也不提前剧透。
        /// </summary>
        public bool PhaseDone(DelegationFlowState st, string defName)
        {
            if (st == null || defName.NullOrEmpty())
            {
                return false;
            }
            List<DelegationPhaseDef> pre = PreludeFor(st);
            for (int i = 0; i < pre.Count; i++)
            {
                if (pre[i].defName == defName)
                {
                    return st.preludeIndex > i;
                }
            }
            List<DelegationPhaseDef> suf = SuffixFor(st);
            for (int i = 0; i < suf.Count; i++)
            {
                if (suf[i].defName == defName)
                {
                    return st.suffixIndex > i;
                }
            }
            return false;
        }

        // ── 显示 ────────────────────────────────────────────────────────────
        //
        // S14 起「流程」块的文案全部由 DelegationStageList（统一阶段序列）产出，
        // 这里原来的 Lines() / CurrentPhaseLabel() / AppendLine() 已删除（死代码）。
        // 保留 HasPhases：Delegation.FlowRemainingTicks() 还在用它。
    }
}
