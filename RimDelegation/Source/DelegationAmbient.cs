using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 阶段旁白（§19.29 / RIM-13 / RIM-14）：给**当前阶段**挑一条趣味台词，并按 Def 的节奏换词；再给这一段定一个"说话人"。
    ///
    /// 两条硬规矩（S14 用户要求 + 踩过的坑）：
    ///   ① **不许在 OnGUI 里当场随机** —— 那会每帧换一句，画面抖得像坏掉。
    ///      所以掷定结果写进 <see cref="DelegationFlowState" />（随存档走），UI 只读。
    ///   ② **说话人是确定性哈希、不掷骰** —— 用户草图里每一段是一个不同的人
    ///      （侦察 Chisa / 移动 Aemeath / 破门 Denia），而且已完成的行不该在读档后改名。
    ///      哈希的输入是 `defName + stageKey`，所以"哪一段是谁"在同一条委派里永远是同一个答案。
    ///
    /// RIM-13 起的选池：**多标签并集 + 权重**（不再"选了单人池就永远不看敌情池"）；
    /// RIM-14 起每条还能带时机/说话人判据（进度 / 小时 / 特质 / 全球事件 / 一次性）。
    /// </summary>
    public static class DelegationAmbient
    {
        /// <summary>阶段内重掷旁白的默认间隔（小时）。Def 上可覆盖（`ambientRerollHours`，支持 `"0.5~1.5"` 区间）。</summary>
        public const float DefaultRerollHours = 0.5f;

        /// <summary>说话人模式 = 全队时的显示名。</summary>
        public const string AllTeamLabel = "全队";

        /// <summary>不重复窗口的上限：记住最近 min(候选数 - 1, 3) 条，彻底消掉 A-B-A-B。</summary>
        public const int MaxRecentWindow = 3;

        /// <summary>休息时用的阶段 key（S16）。</summary>
        public const string RestKey = "rest";

        // ── 说话人 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 这一段的说话人。
        /// mode：`RandomPawn`（默认，按哈希挑一个参与者）/ `All`（全队）/ `None`（不写人名）。
        /// </summary>
        public static string SpeakerName(Delegation d, string stageKey, string mode)
        {
            if (mode == "None")
            {
                return null;
            }
            if (mode == "All")
            {
                return AllTeamLabel;
            }
            Pawn p = PickPawn(d, stageKey);
            return p == null ? AllTeamLabel : p.LabelShortCap;
        }

        /// <summary>这一段由谁"代表"（确定性：同一条委派的同一段永远同一个人）。</summary>
        public static Pawn PickPawn(Delegation d, string stageKey)
        {
            List<Pawn> list = d?.participants;
            if (list.NullOrEmpty())
            {
                return null;
            }
            int h = StableHash((d.def?.defName ?? "?") + "|" + stageKey) & 0x7FFFFFFF;
            return list[h % list.Count];
        }

        // ── 重掷节奏 ────────────────────────────────────────────────────────

        /// <summary>
        /// 解析 `ambientRerollHours`：既接受单个值（`"0.5"`），也接受区间（`"0.5~1.5"`）。
        /// 空/写坏 ⇒ 默认 0.5~0.5（与改动前逐字一致）。
        /// </summary>
        public static void ParseRerollRange(string spec, out float min, out float max)
        {
            min = DefaultRerollHours;
            max = DefaultRerollHours;
            if (spec.NullOrEmpty())
            {
                return;
            }
            string s = spec.Trim();
            int sep = s.IndexOf('~');
            if (sep < 0)
            {
                sep = s.IndexOf('-', 1);   // 允许 "0.5-1.5"（负数不可能出现在小时数上）
            }
            float a;
            float b;
            if (sep < 0)
            {
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out a) && a > 0f)
                {
                    min = a;
                    max = a;
                }
                return;
            }
            bool okA = float.TryParse(s.Substring(0, sep).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a);
            bool okB = float.TryParse(s.Substring(sep + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b);
            if (!okA || !okB)
            {
                return;
            }
            if (a <= 0f)
            {
                a = DefaultRerollHours;
            }
            if (b <= 0f)
            {
                b = a;
            }
            min = Mathf.Min(a, b);
            max = Mathf.Max(a, b);
        }

        /// <summary>抽下一次重掷的间隔（区间内均匀）。</summary>
        public static int RollRerollTicks(float min, float max)
        {
            float hours = max <= min ? min : Rand.Range(min, max);
            int ticks = Mathf.RoundToInt(hours * Delegation.TicksPerHour);
            return ticks < 1 ? 1 : ticks;
        }

        // ── 情境 ────────────────────────────────────────────────────────────

        /// <summary>此刻的情境（标签判据与条件判据都从这里读，保证"一次掷定只看一份事实"）。</summary>
        public struct Context
        {
            public Delegation d;
            public Site site;
            public bool solo;
            public bool group;
            public bool packed;
            public bool hostile;
            public bool night;
            /// <summary>当地小时（0..24）。</summary>
            public float hour;
            /// <summary>本段进度（0..1）。主作业段 = `d.Progress`；固定段 = `phaseTicks / Ticks`。休息 = 0。</summary>
            public float progress01;
            /// <summary>这一段的说话人（`ts=` 判据要问的就是他）。</summary>
            public Pawn speaker;
            /// <summary>休息时段（S16）—— 休息池不受 solo/packed/hostile 影响，只受小时/特质/事件影响。</summary>
            public bool resting;
        }

        /// <summary>按当前队伍与地点拼出情境。<paramref name="progress01" /> 由调用方给（只有它随阶段而异）。</summary>
        public static Context BuildContext(Delegation d, Site site, float progress01, string stageKey, Pawn speaker)
        {
            Context ctx = new Context
            {
                d = d,
                site = site,
                progress01 = Mathf.Clamp01(progress01),
                speaker = speaker,
            };
            int count = d?.participants?.Count ?? 0;
            ctx.solo = count <= 1;
            ctx.group = count > 1;
            ctx.packed = HasPackAnimal(d);
            ctx.hostile = site != null && ThreatAssessmentEntry.HasThreat(site);
            ctx.hour = -1f;
            if (site != null)
            {
                // 与 `DelegationModeDef.IsWorkingNow` 同一套算法（不需要地图，只要经度）
                ctx.hour = GenDate.HourFloat(GenTicks.TicksAbs, Find.WorldGrid.LongLatOf(site.Tile).x);
                ctx.night = ctx.hour >= AmbientLineSyntax.NightFrom || ctx.hour < AmbientLineSyntax.NightTo;
            }
            return ctx;
        }

        /// <summary>这条旁白此刻能不能说（标签 + 条件全过）。</summary>
        public static bool Matches(AmbientLine line, ref Context ctx)
        {
            if (line == null || line.syntaxError != null)
            {
                return false;
            }
            for (int i = 0; i < line.tags.Count; i++)
            {
                string tag = line.tags[i];
                switch (tag)
                {
                    case AmbientLineSyntax.TagSolo:
                        if (!ctx.solo) return false;
                        break;
                    case AmbientLineSyntax.TagGroup:
                        if (!ctx.group) return false;
                        break;
                    case AmbientLineSyntax.TagPacked:
                        if (!ctx.packed) return false;
                        break;
                    case AmbientLineSyntax.TagHostile:
                        if (!ctx.hostile) return false;
                        break;
                    case AmbientLineSyntax.TagNight:
                        if (!ctx.night) return false;
                        break;
                    case AmbientLineSyntax.TagDay:
                        if (ctx.night) return false;
                        break;
                    case AmbientLineSyntax.TagEarly:
                        if (ctx.progress01 >= AmbientLineSyntax.EarlyUntil) return false;
                        break;
                    case AmbientLineSyntax.TagMid:
                        if (ctx.progress01 < AmbientLineSyntax.EarlyUntil || ctx.progress01 > AmbientLineSyntax.MidUntil) return false;
                        break;
                    case AmbientLineSyntax.TagLate:
                        if (ctx.progress01 <= AmbientLineSyntax.MidUntil) return false;
                        break;
                    default:
                        return false;   // 未知标签（ConfigErrors 已报）⇒ 这一句不许出现，而不是随便说
                }
            }
            if (line.minProgress > 0f && ctx.progress01 < line.minProgress)
            {
                return false;
            }
            if (line.maxProgress < 1f && ctx.progress01 > line.maxProgress)
            {
                return false;
            }
            if (line.hourFrom >= 0f && !HourInRange(ctx.hour, line.hourFrom, line.hourTo))
            {
                return false;
            }
            if (!line.traitAny.NullOrEmpty() && !AnyParticipantHasTrait(ctx.d, line.traitAny, line.traitAnyDegree))
            {
                return false;
            }
            if (!line.traitSpeaker.NullOrEmpty() && !HasTrait(ctx.speaker, line.traitSpeaker, line.traitSpeakerDegree))
            {
                return false;
            }
            if (!line.gameCondition.NullOrEmpty() && !GameConditionActive(line.gameCondition))
            {
                return false;
            }
            if (line.once && ctx.d?.flow != null && ctx.d.flow.ambientSeenOnce != null
                && ctx.d.flow.ambientSeenOnce.Contains(line.id))
            {
                return false;
            }
            return true;
        }

        private static bool HourInRange(float hour, float from, float to)
        {
            if (hour < 0f)
            {
                return true;   // 拿不到当地时间（没有 site）⇒ 不因它否掉一条旁白
            }
            if (to < from)
            {
                return hour >= from || hour < to;   // 跨零点
            }
            return hour >= from && hour <= to;
        }

        private static bool HasTrait(Pawn p, string traitDefName, int degree)
        {
            if (p?.story?.traits == null)
            {
                return false;
            }
            TraitDef def = DefDatabase<TraitDef>.GetNamedSilentFail(traitDefName);
            if (def == null)
            {
                return false;
            }
            return degree == int.MinValue ? p.story.traits.HasTrait(def) : p.story.traits.HasTrait(def, degree);
        }

        private static bool AnyParticipantHasTrait(Delegation d, string traitDefName, int degree)
        {
            List<Pawn> list = d?.participants;
            if (list.NullOrEmpty())
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (HasTrait(list[i], traitDefName, degree))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>全球事件是否正在生效（`Find.World.GameConditionManager`，**不需要地图** —— 委派全程不进图）。</summary>
        private static bool GameConditionActive(string defName)
        {
            GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                return false;
            }
            GameConditionManager mgr = Find.World?.GameConditionManager;
            return mgr != null && mgr.ConditionIsActive(def);
        }

        /// <summary>车队里有没有驮兽（`RaceProps.packAnimal` 的活体动物）—— 没有就别提驮兽。</summary>
        public static bool HasPackAnimal(Delegation d)
        {
            List<Pawn> list = d?.caravan?.PawnsListForReading;
            if (list == null)
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p != null && !p.Dead && p.RaceProps != null && p.RaceProps.packAnimal)
                {
                    return true;
                }
            }
            return false;
        }

        // ── 候选与抽签 ──────────────────────────────────────────────────────

        /// <summary>
        /// 把池里的条目按此刻的情境筛成候选（**并集**，不再短路）。
        /// 返回的列表可能是空列表（这一段此刻一句都没有），调用方据此决定"不显示旁白"。
        /// </summary>
        public static List<AmbientLine> Candidates(List<AmbientLine> pool, ref Context ctx)
        {
            List<AmbientLine> list = new List<AmbientLine>();
            if (pool.NullOrEmpty())
            {
                return list;
            }
            for (int i = 0; i < pool.Count; i++)
            {
                if (Matches(pool[i], ref ctx))
                {
                    list.Add(pool[i]);
                }
            }
            return list;
        }

        /// <summary>按权重抽一条（避开 `exclude` 里的 id；实在避不开就退化成全候选）。</summary>
        private static AmbientLine WeightedPick(List<AmbientLine> cands, List<int> exclude)
        {
            if (cands.NullOrEmpty())
            {
                return null;
            }
            List<AmbientLine> pool = cands;
            if (!exclude.NullOrEmpty())
            {
                List<AmbientLine> narrowed = new List<AmbientLine>();
                for (int i = 0; i < cands.Count; i++)
                {
                    if (!exclude.Contains(cands[i].id))
                    {
                        narrowed.Add(cands[i]);
                    }
                }
                if (narrowed.Count > 0)
                {
                    pool = narrowed;
                }
            }
            float total = 0f;
            for (int i = 0; i < pool.Count; i++)
            {
                total += pool[i].EffectiveWeight;
            }
            if (total <= 0f)
            {
                return pool[Rand.Range(0, pool.Count)];
            }
            float r = Rand.Value * total;
            for (int i = 0; i < pool.Count; i++)
            {
                r -= pool[i].EffectiveWeight;
                if (r <= 0f)
                {
                    return pool[i];
                }
            }
            return pool[pool.Count - 1];
        }

        /// <summary>
        /// 当前该显示的那一条旁白（**UI 路径**）。
        /// 正常读的是 Tick 掷定并存档的 id；UI 比 Tick 先跑到（同一 tick 内的顺序问题）时
        /// 用"确定性哈希"兜底挑一条 —— 保证同一帧内不会闪，也不改状态。
        /// </summary>
        public static string Current(Delegation d, Site site, string stageKey, List<AmbientLine> pool, float progress01,
            Pawn speaker)
        {
            if (pool.NullOrEmpty())
            {
                return null;
            }
            Context ctx = BuildContext(d, site, progress01, stageKey, speaker);
            List<AmbientLine> cands = Candidates(pool, ref ctx);
            if (cands.Count == 0)
            {
                return null;
            }
            int pickedId = d?.flow != null && d.flow.ambientStageKey == stageKey ? d.flow.ambientPickId : 0;
            if (pickedId != 0)
            {
                for (int i = 0; i < cands.Count; i++)
                {
                    if (cands[i].id == pickedId)
                    {
                        return cands[i].text;
                    }
                }
            }
            int idx = (StableHash((d?.def?.defName ?? "?") + "|" + stageKey + "|line") & 0x7FFFFFFF) % cands.Count;
            return cands[idx].text;
        }

        /// <summary>
        /// 每 tick 调一次（宿主 <c>WorldObjectComp_Delegations.TickDelegation</c>）：
        /// 阶段换了就必掷一条；同阶段内到点（**区间**间隔，`ambientRerollHours`）换一条，且避开最近几条。
        /// </summary>
        public static void Tick(Delegation d, Site site)
        {
            if (d == null || site == null)
            {
                return;
            }
            // 暂停期间**不换词**（用户 S14 追加要求）：人都停下来了，旁白却每 0.5h 变一句很出戏。
            // 恢复后不需要额外补偿 —— 那时 `now - ambientPickedTick` 早就超过间隔，下一 tick 自然重掷。
            if (d.paused)
            {
                return;
            }
            if (d.flow == null)
            {
                d.flow = new DelegationFlowState();
            }

            // S16：休息时段优先给"休息"掷定（睡觉 / 睡前聊天）—— 休息那两行是**置顶**的，
            // 如果只有一句"睡前聊天"挂着不动，看起来就假了。
            bool resting = !d.IsStalled(GenTicks.TicksAbs) && !d.IsWorkTime(site, GenTicks.TicksAbs);
            string key;
            List<AmbientLine> pool;
            float rerollMin;
            float rerollMax;
            float progress01;
            Pawn speaker;
            if (resting)
            {
                key = RestKey;
                pool = d.def?.RestAmbientPool;
                rerollMin = DefaultRerollHours;
                rerollMax = DefaultRerollHours;
                progress01 = 0f;
                speaker = PickPawn(d, key);
            }
            else
            {
                DelegationStage active = DelegationStageList.ActiveStage(DelegationStageList.Build(d, site));
                if (active == null)
                {
                    return;
                }
                key = active.key;
                pool = active.pool;
                rerollMin = active.rerollMinHours;
                rerollMax = active.rerollMaxHours;
                progress01 = active.progress01;
                speaker = active.executor ?? PickPawn(d, key);
            }
            if (pool.NullOrEmpty())
            {
                return;
            }

            Context ctx = BuildContext(d, site, progress01, key, speaker);
            ctx.resting = resting;
            if (resting)
            {
                // 休息时"队伍几个人/有没有驮兽/有没有守军"都不该影响闲聊（S16 语义），
                // 但用户若显式写了 `[solo]` / `[night]` 这类标签，仍按事实判。
                ctx.hostile = false;
            }
            List<AmbientLine> cands = Candidates(pool, ref ctx);
            if (cands.Count == 0)
            {
                return;
            }

            int now = GenTicks.TicksGame;
            bool stageChanged = d.flow.ambientStageKey != key;
            // 老存档没有 `ambientNextRerollTicks`（= 0）⇒ 不能因此永远不换词；按"最小 1 tick"处理，
            // 也就是"下一 tick 就重掷一次"，之后走正常区间。
            int dueTicks = d.flow.ambientNextRerollTicks > 0 ? d.flow.ambientNextRerollTicks : 1;
            bool due = !stageChanged && now - d.flow.ambientPickedTick >= dueTicks;
            if (!stageChanged && !due)
            {
                return;
            }

            List<int> exclude = RecentWindow(d, cands.Count);
            AmbientLine picked = WeightedPick(cands, exclude);
            if (picked == null)
            {
                return;
            }
            d.flow.ambientStageKey = key;
            d.flow.ambientPickId = picked.id;
            d.flow.ambientVariant = cands.IndexOf(picked);
            d.flow.ambientPickedTick = now;
            d.flow.ambientNextRerollTicks = RollRerollTicks(rerollMin, rerollMax);
            RememberPick(d, picked);
            DelegationUtility.LogVerbose(string.Format("阶段旁白：{0} → {1}#{2}（候选 {3} 条，来源 {4}）",
                key, picked.sourceKey, picked.indexInSource, cands.Count, picked.tags.Count == 0 ? "无标签" : string.Join(",", picked.tags.ToArray())));
        }

        /// <summary>最近几条的 id（不重复窗口 = min(候选数 - 1, 3)）。</summary>
        private static List<int> RecentWindow(Delegation d, int candidateCount)
        {
            List<int> recent = d?.flow?.ambientRecent;
            int window = Mathf.Min(candidateCount - 1, MaxRecentWindow);
            List<int> exclude = new List<int>();
            if (window <= 0 || recent.NullOrEmpty())
            {
                return exclude;
            }
            int start = Mathf.Max(0, recent.Count - window);
            for (int i = start; i < recent.Count; i++)
            {
                exclude.Add(recent[i]);
            }
            return exclude;
        }

        private static void RememberPick(Delegation d, AmbientLine picked)
        {
            if (d.flow.ambientRecent == null)
            {
                d.flow.ambientRecent = new List<int>();
            }
            d.flow.ambientRecent.Add(picked.id);
            while (d.flow.ambientRecent.Count > MaxRecentWindow)
            {
                d.flow.ambientRecent.RemoveAt(0);
            }
            if (picked.once)
            {
                if (d.flow.ambientSeenOnce == null)
                {
                    d.flow.ambientSeenOnce = new List<int>();
                }
                if (!d.flow.ambientSeenOnce.Contains(picked.id))
                {
                    d.flow.ambientSeenOnce.Add(picked.id);
                }
            }
        }

        /// <summary>FNV-1a：**不能**用 string.GetHashCode()（.NET 的字符串哈希不保证跨运行稳定，
        /// 拿它当"确定性"会变成"每次开游戏换一个人"）。</summary>
        public static int StableHash(string s)
        {
            unchecked
            {
                int h = unchecked((int)2166136261);   // FNV offset basis（写成字面量会被当成 uint）
                for (int i = 0; i < s.Length; i++)
                {
                    h = (h ^ s[i]) * 16777619;
                }
                return h;
            }
        }
    }
}
