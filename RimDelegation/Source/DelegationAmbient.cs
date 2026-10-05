using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 阶段旁白（§19.29）：给**当前阶段**挑一条趣味台词，并按 Def 的节奏换词；再给这一段定一个"说话人"。
    ///
    /// 两条硬规矩（S14 用户要求 + 踩过的坑）：
    ///   ① **不许在 OnGUI 里当场随机** —— 那会每帧换一句，画面抖得像坏掉。
    ///      所以掷定结果写进 <see cref="DelegationFlowState" />（随存档走），UI 只读。
    ///   ② **说话人是确定性哈希、不掷骰** —— 用户草图里每一段是一个不同的人
    ///      （侦察 Chisa / 移动 Aemeath / 破门 Denia），而且已完成的行不该在读档后改名。
    ///      哈希的输入是 `defName + stageKey`，所以"哪一段是谁"在同一条委派里永远是同一个答案。
    /// </summary>
    public static class DelegationAmbient
    {
        /// <summary>阶段内重掷旁白的默认间隔（小时）。Def 上可覆盖（`ambientRerollHours`）。</summary>
        public const float DefaultRerollHours = 0.5f;

        /// <summary>说话人模式 = 全队时的显示名。</summary>
        public const string AllTeamLabel = "全队";

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

        /// <summary>
        /// 当前该显示的那一条旁白。
        /// 正常路径读的是 Tick 掷定并存档的下标；如果 UI 比 Tick 先跑到（同一 tick 内的顺序问题），
        /// 就用"确定性哈希"兜底挑一条 —— 保证同一帧内不会闪，也不改状态。
        /// </summary>
        public static string Current(Delegation d, string stageKey, List<string> pool)
        {
            if (pool.NullOrEmpty())
            {
                return null;
            }
            int idx;
            if (d?.flow != null && d.flow.ambientStageKey == stageKey)
            {
                idx = Mathf.Clamp(d.flow.ambientVariant, 0, pool.Count - 1);
            }
            else
            {
                idx = (StableHash((d.def?.defName ?? "?") + "|" + stageKey + "|line") & 0x7FFFFFFF) % pool.Count;
            }
            return pool[idx];
        }

        /// <summary>
        /// 每 tick 调一次（宿主 <c>WorldObjectComp_Delegations.TickDelegation</c>）：
        /// 阶段换了就必掷一条；同阶段内每 `rerollHours` 小时换一条（且尽量不重复上一条）。
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
            List<string> pool;
            float reroll;
            if (resting)
            {
                key = "rest";
                pool = d.def?.restAmbientLines;
                reroll = DefaultRerollHours;
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
                reroll = active.rerollHours;
            }
            if (pool.NullOrEmpty())
            {
                return;
            }

            int now = GenTicks.TicksGame;
            bool stageChanged = d.flow.ambientStageKey != key;
            bool due = !stageChanged && reroll > 0f
                && now - d.flow.ambientPickedTick >= Mathf.RoundToInt(reroll * Delegation.TicksPerHour);
            if (!stageChanged && !due)
            {
                return;
            }

            int next = Rand.Range(0, pool.Count);
            if (!stageChanged && pool.Count > 1 && next == d.flow.ambientVariant)
            {
                next = (next + 1) % pool.Count;   // 换一条，别在原地打转
            }
            d.flow.ambientStageKey = key;
            d.flow.ambientVariant = next;
            d.flow.ambientPickedTick = now;
            DelegationUtility.LogVerbose($"阶段旁白：{key} → #{next}");
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
