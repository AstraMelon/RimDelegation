using System.Collections.Generic;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一条委派固定流程里的**一段**（§19.27）。例：地图侦察 1h / 移动到目标区域 2h / 破门 1h / 撤离 2h。
    ///
    /// 为什么是独立的 Def 而不是 `DelegationDef` 上的匿名字段：
    ///   · 段的名字要能翻译（`label` 走 DefInjected），而匿名字段做不到；
    ///   · 同一个阶段可以被多条委派复用（"撤离"显然不止物资点用得上）；
    ///   · 符合"加 def 不加代码"的框架目标 —— 新增一段流程只写 XML。
    ///
    /// 语义（由 <see cref="DelegationFlow"/> 解释）：
    ///   `afterWork = false` → **前置**阶段：在作业之前按 `flowPhases` 的顺序依次走完；
    ///   `afterWork = true`  → **收尾**阶段：在任意收工条件成立之后走，走完才真正收工。
    /// 两个列表各按 `DelegationDef.flowPhases` 里的出现顺序执行。
    /// </summary>
    public class DelegationPhaseDef : Def
    {
        /// <summary>这一段固定占用的游戏内小时数（1 小时 = 2500 ticks）。</summary>
        public float hours = 1f;

        /// <summary>
        /// RIM-11：这一段时长的**随机偏移**（小时）。空 / `0` = 固定不变（与改动前逐字一致）。
        ///
        /// 用户 2026-10-05 提出的口径：「撤离时间是否可以随机，**一个固定值加减偏移值**」。
        /// 写法就是"正数 = ±N"：`<hoursJitter>0.5</hoursJitter>` ⇒ 2h 那一段落在 1.5–2.5h。
        ///
        /// 掷定时机（**关键**）：`DelegationFlow.Freeze`（开工那一刻）掷一次，结果存进
        /// `DelegationFlowState.jitteredHours`。绝不在读数时掷 —— `Ticks` 被 UI 每帧读，
        /// 当场随机会变成"进度条每帧换个长度"。
        ///
        /// 落点（本机现值）：三个「撤离」段各 ±0.5h。理由 —— 撤离是"收摊走路"，
        /// 受负重/地形/天色影响最自然，而且它是**收尾段**，抖动不会把 ETA 说成谎
        /// （前置段抖动会让"预计完工时刻"在开工那一刻就不可信）。
        /// 觉得太飘就把这一格删掉或写 `0`，那一秒起行为立刻回到固定值。
        /// </summary>
        public string hoursJitter;

        /// <summary>偏移幅度（小时，≥0）。解析失败/未配 ⇒ 0 = 不抖。</summary>
        public float JitterHours
        {
            get
            {
                if (hoursJitter.NullOrEmpty())
                {
                    return 0f;
                }
                float v;
                if (!float.TryParse(hoursJitter.Trim().TrimStart('±', '+', '-'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v))
                {
                    return 0f;
                }
                return v < 0f ? 0f : v;
            }
        }

        /// <summary>
        /// true = 收尾阶段（干完之后才走）。
        ///
        /// 为什么需要这条轴、而不是"全放在开工前"：物资点的「撤离」在叙事上必然发生在装车之后
        /// —— 先把固定 4 小时的前置（侦察/移动/破门）走完，装完车，再走「撤离」。
        /// </summary>
        public bool afterWork;

        /// <summary>
        /// **进行中**的措辞（"正在侦察环境中…"）。空 = 用 `"正在" + label + "…"` 兜底。
        ///
        /// 为什么要单独一格而不是拿 label 拼：同一个阶段的"过程"与"结果"在中文里常常不同词
        /// （侦察环境 → 侦察完成；移动到目标地点 → 已抵达目标地点），而 `label` 还需要当阶段名用。
        /// </summary>
        public string progressLabel;

        /// <summary>
        /// 这一段的**旁白池**（§19.29）：进行中时从里面挑一条显示在流程块里。
        /// 空 = 这一段不显示旁白（只画标题与条）。
        /// </summary>
        public List<string> ambientLines = new List<string>();

        /// <summary>
        /// **队伍带驮兽**时用的旁白池（S14 用户指出：池里提到驮兽，但队伍可能没有驮兽）。
        /// 空 = 有驮兽时也用默认池。判据 = 车队里有 `RaceProps.packAnimal` 的活体动物。
        /// </summary>
        public List<string> ambientLinesPacked = new List<string>();

        /// <summary>
        /// **只有一个人**时用的旁白池（S14 用户指出："全队踩着…""有人说…"在单人时都不成立）。
        /// 空 = 单人时也用默认池。
        /// </summary>
        public List<string> ambientLinesSolo = new List<string>();

        /// <summary>
        /// **该地点真有守军**时用的旁白池（判据 `ThreatAssessmentEntry.HasThreat`）。
        ///
        /// 为什么需要（S14 用户指出）：`ItemStash` / `PreciousLump` 的主件
        /// `wantsThreatPoints = false` ⇒ 这类点常常**没有敌人**，而默认池里写着
        /// "记下了守军换岗的规律" —— 那就是在讲一件没发生的事。空 = 一律用默认池。
        /// </summary>
        public List<string> ambientLinesHostile = new List<string>();

        /// <summary>
        /// **多人时**的进行中措辞（S14 用户要求）：队伍超过 1 人时"XXX 正在移动到目标地点…"
        /// 看着像他一个人在走，应改成"XXX 正在带领远行队移动…"。
        /// 空 = 多人时也用 `progressLabel`（不区分）。
        /// </summary>
        public string progressLabelGroup;

        /// <summary>
        /// 阶段内多久换一条旁白（小时）。空 = 默认 0.5h。
        ///
        /// RIM-13：从"一个固定数"升级成**区间** —— 写 `"0.5~1.5"` 时每次抽完重新掷下一次的间隔，
        /// 换句节奏不再是整齐的半小时节拍（"太机械"是用户 S30 报的问题之一）。
        /// 只写一个数（`"1"`）仍然等于"固定 1 小时"，与改动前逐字一致。
        /// </summary>
        public string ambientRerollHours;

        /// <summary>
        /// S22：这一段**不可被休息时段打断**（用户原话：「正在侦察环境的时候，不会因为时段进入了休息而打断」
        /// 「正在带领小队移动的时候…」「正在破门的时候…」）。
        ///
        /// 语义：到了工时窗口之外**照常推进**这一段（`TickDelegation` 的工时门控为它让路），
        /// 并且这段时间照计 `ticksWorked`（人醒着在干活，不是在休息）。
        /// 只该配给"一旦开始就必须做完"的动作：侦察 / 移动 / 破门。
        /// </summary>
        public bool uninterruptible;

        /// <summary>
        /// S22：小队在这一段处于**哪个虚拟位置**（流程栏顶部那一行「外围 / 营地 / 目标点内部」）。
        /// 空 = 沿用上一段的位置（不想让每个 Def 都写一遍）。
        /// </summary>
        public string squadPosition;

        /// <summary>
        /// S22：这一段的**执行者技能**（用于显示「Aemeath（射击 无火 10）将带队侦察环境」，
        /// 并据此挑执行者：技能最高者；并列时沿用旁白那套哈希，保证不跳人）。
        /// 空 = 这一段的执行者仍按 `speakerMode` 抽（旁白口径）。
        ///
        /// ⚠️ 段**耗时**还没有挂技能（那是下一期的事，见 Doc/流程栏与技能挂钩-方案评估.md §4）：
        /// 现在这一格只影响"谁去做 + 怎么显示"。
        /// </summary>
        public RimWorld.SkillDef skillDef;

        /// <summary>
        /// S23：这一段**只在该地点真有守军时**才出现在本次流程里（判据
        /// <see cref="ThreatAssessmentEntry.HasThreat" />，读存档里的 `SitePartParams.threatPoints`）。
        ///
        /// 语义（甲B 方案，用户 2026-09-27 拍板）：条件**在开工那一刻冻结一次**，
        /// 之后这次委派就按冻结出来的段表走到底 —— 进度只记下标（`preludeIndex`），
        /// 若让段表随状态变化，中途一次态变就会让下标指到别的段（且存档里只有下标，无从纠正）。
        /// 冻结点：`WorldObjectComp_Delegations.StartDelegation`（与 S9 抄物资台账同一处）。
        /// </summary>
        public bool requireThreat;

        /// <summary>S23：这一段只在该地点**没有**守军时才出现。与 <see cref="requireThreat" /> 互斥。</summary>
        public bool requireNoThreat;

        /// <summary>
        /// S25：**这一段走完就向玩家揭露守军情报**（作战任务段从"未明"变成完整编队 + 自动成算）。
        ///
        /// 用户口径：「在侦察完成前，守军不会向玩家揭露」「完成侦察任务后，向玩家揭露敌人编队」。
        /// 采矿的「战术侦察」就带它。判据只看**冻结后的段表**：一个流程里一个都没带 ⇒ 一律视为已揭露
        /// （老存档、无侦察段的委派、无威胁分支的行为都逐字不变）。
        /// </summary>
        public bool revealsThreat;

        /// <summary>
        /// S25：**进入这一段之前必须等玩家给这个信号**（例：交战的 `engage`）。
        ///
        /// 语义：流程走到它跟前就**停住** —— 不进入、也不触发它的 `onEnter`；
        /// `Delegation.IsWorkTime` 随之为假 ⇒ 远行队就地待命（走休息分支，照常计休息），
        /// 直到 `DelegationFlowState.signals` 里出现这个名字。
        ///
        /// 用户口径：「然后远行队进入休息状态，作战任务UI里面添加 进行交战 和 撤退选项」。
        /// </summary>
        public string pauseUntilSignal;

        /// <summary>
        /// S28：这一段在「流程」块里归到哪一组 —— `Combat`（作战任务）或 `Collect`（收集任务）。
        ///
        /// 用户原话：「流程拆分一下，拆分显示为作战的和收集任务的」。分组只影响**显示**，
        /// 不影响段表的顺序与语义（组内仍按原先后排列，所以"作战 → 收集"在采矿那条上正好就是时间顺序）。
        ///
        /// 空 = **按条件推断**：带 `requireThreat`（只有真有守军才出现的段）或 `pauseUntilSignal`
        /// （等玩家下作战指令的段）⇒ 作战；其余 ⇒ 收集。判据见 <see cref="InCombatFlow" />。
        /// 推断是给"新增一段却忘了标"兜底的默认值；要显式指定就在 XML 里写一格 `flowCategory`。
        /// </summary>
        public string flowCategory;

        /// <summary>S28：这一段属于「作战任务」那一组吗（见 <see cref="flowCategory" />）。</summary>
        public bool InCombatFlow
        {
            get
            {
                if (!flowCategory.NullOrEmpty())
                {
                    return "Combat".Equals(flowCategory.Trim(), System.StringComparison.OrdinalIgnoreCase);
                }
                return requireThreat || !pauseUntilSignal.NullOrEmpty();
            }
        }

        /// <summary>
        /// S26：这一段 `onEnter` 产生的文字**等这一段走完**再写进留痕。
        ///
        /// 用户口径：「交战流程完成前，结果就出来了 -- 需要等待交战结束再出结果」——
        /// 战斗本来就是在进入该段的那一刻结算完成的，但**结果**属于"打完了"，
        /// 提前贴出来等于剧透（而且与「正在交火…」的进度条自相矛盾）。
        /// </summary>
        public bool deferNoteUntilDone;

        /// <summary>
        /// S23：**段进入钩子** —— 进入这一段的那一刻执行一次的效果（不是每 tick）。
        ///
        /// 为什么需要：固定段在此之前只是"累加 ticks、越过下标"，没有任何回调；
        /// 而「交战」这一段的本质是**一次性结算**（进入即跑一遍模拟战斗，1h 是叙事耗时），
        /// 与营救的清场同款（`RescueUtility.ResolveClearance`）。有了它，交战段本身
        /// 仍然只是 XML 里一条 `<li Class="RimDelegation.DelegationEffectDef_ResolveCombat" />`。
        /// </summary>
        public List<DelegationEffectDef> onEnter = new List<DelegationEffectDef>();

        /// <summary>这一段在"这次委派的敌情状态"下是否生效（冻结时问一次）。</summary>
        public bool Matches(bool hasThreat)
        {
            if (requireThreat && !hasThreat) return false;
            if (requireNoThreat && hasThreat) return false;
            return true;
        }

        /// <summary>这一段是否带条件（没有任何条件时，冻结逻辑会省下整份存档数据）。</summary>
        public bool HasCondition => requireThreat || requireNoThreat;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (requireThreat && requireNoThreat)
            {
                yield return "同时写了 requireThreat 与 requireNoThreat —— 这一段永远不会生效";
            }
            if (pauseUntilSignal != null && pauseUntilSignal.Trim().Length == 0)
            {
                yield return "pauseUntilSignal 写成了空白 —— 会变成永远等不到的信号（流程卡死在该段之前）";
            }
            // RIM-14：旁白行首指令写错时**必须报出来**。写错的后果是"这一句永远不出现"
            // （未知标签一律判不成立），不报的话只会表现为"文案莫名少了"。
            foreach (string e in AmbientConfigErrors(AmbientPool))
            {
                yield return e;
            }
        }

        /// <summary>把池里所有解析失败的行报成一行（`defName` 前缀由 Def 系统自己加）。</summary>
        internal static IEnumerable<string> AmbientConfigErrors(List<AmbientLine> pool)
        {
            if (pool == null)
            {
                yield break;
            }
            for (int i = 0; i < pool.Count; i++)
            {
                AmbientLine line = pool[i];
                if (line != null && line.syntaxError != null)
                {
                    yield return string.Format("旁白池 {0} 第 {1} 条写法有误：{2}｜原文：{3}",
                        line.sourceKey, line.indexInSource + 1, line.syntaxError,
                        TrimForLog(line.text));
                }
            }
        }

        private static string TrimForLog(string s)
        {
            if (s == null)
            {
                return "";
            }
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }

        /// <summary>
        /// 这一段"由谁说话"：`RandomPawn`（默认：按 `defName + 阶段` 哈希挑一个参与者，
        /// 同一条委派里每段固定一个人）/ `All`（全队）/ `None`（不写人名）。
        /// </summary>
        public string speakerMode = "RandomPawn";

        /// <summary>还没轮到的阶段怎么称呼（就是 Def 的 label）。</summary>
        public string PendingLabel => label.NullOrEmpty() ? "（未命名阶段）" : label;

        public string ProgressLabelText => progressLabel.NullOrEmpty() ? "正在" + PendingLabel + "…" : progressLabel;

        /// <summary>
        /// 进行中措辞，按"队伍是不是只有一个人"选（S14 用户要求）：
        /// 多人 → `progressLabelGroup`（"正在带领远行队移动…"）；单人 → `progressLabel`（"正在移动到目标地点…"）。
        /// </summary>
        public string ProgressLabelTextFor(bool group)
        {
            if (group && !progressLabelGroup.NullOrEmpty())
            {
                return progressLabelGroup;
            }
            return ProgressLabelText;
        }

        /// <summary>（`doneLabel` 已在 S14 删除：完成行的文案改用段名 + 说话人 + 耗时，那个字段没地方显示。）</summary>

        /// <summary>这一段的固定 tick 数（最小 1，避免 0 小时段落变成死循环）。= 1× 倍率下的值。</summary>
        public int Ticks => TicksWithScale(1f);

        /// <summary>
        /// RIM-12：按**开工时冻结的**时长倍率算这一段的生效小时数。
        /// 传 ≤0 或 NaN 一律当 1×（与加倍率之前逐字一致）。
        /// </summary>
        public float EffectiveHours(float scale)
        {
            if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale))
            {
                return hours;
            }
            return hours * scale;
        }

        /// <summary>RIM-12：按冻结倍率算这一段的固定 tick 数（最小 1）。</summary>
        public int TicksWithScale(float scale)
        {
            return TicksForHours(EffectiveHours(scale));
        }

        /// <summary>小时数 → ticks（最小 1；0 小时段落不会变成死循环）。</summary>
        public static int TicksForHours(float hours)
        {
            return hours <= 0f ? 1 : (int)(hours * Delegation.TicksPerHour + 0.5f);
        }

        /// <summary>显示用："1h" / "2.5h"（与 UI 的短句风格一致）。</summary>
        public string HoursLabel => hours.ToString("0.##") + "h";

        // ── RIM-13 / RIM-14：旁白池 ─────────────────────────────────────────

        /// <summary>四个池的解析缓存 + 并集（运行期，不进 XML、不进存档）。</summary>
        private readonly AmbientPoolSet ambientPools = new AmbientPoolSet();

        private float rerollMin = float.NaN;

        private float rerollMax = float.NaN;

        /// <summary>
        /// 这一刻的**候选池** = 默认 + 单人 + 驮兽 + 敌情四个池的并集（RIM-13 的多标签并集）。
        /// 条目各自带标签，够不够格由 `DelegationAmbient.Matches` 按当前状况判
        /// —— 所以"一个人去打有守军的矿点"现在同时能听到单人句与敌情句。
        /// </summary>
        public List<AmbientLine> AmbientPool =>
            ambientPools.Union("ambientLines", ambientLines, ambientLinesSolo, ambientLinesPacked, ambientLinesHostile);

        /// <summary>换句间隔的下界（小时，已解析）。</summary>
        public float RerollMinHours
        {
            get
            {
                EnsureReroll();
                return rerollMin;
            }
        }

        /// <summary>换句间隔的上界（小时，已解析）。</summary>
        public float RerollMaxHours
        {
            get
            {
                EnsureReroll();
                return rerollMax;
            }
        }

        private void EnsureReroll()
        {
            if (!float.IsNaN(rerollMin))
            {
                return;
            }
            DelegationAmbient.ParseRerollRange(ambientRerollHours, out rerollMin, out rerollMax);
        }

        public override void ResolveReferences()
        {
            base.ResolveReferences();
            if (hours < 0f)
            {
                hours = 0f;
            }
            // RIM-13：热重载会把 `ambientRerollHours` 读成新值 ⇒ 丢掉上次解析出来的区间。
            rerollMin = float.NaN;
        }
    }
}
