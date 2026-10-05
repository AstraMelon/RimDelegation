using System;
using System.Collections.Generic;
using System.Text;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 战斗日志的类别。**这是"过滤"的维度** —— 6 类互相正交，覆盖一整场战斗的全部事实。
    /// </summary>
    public enum CombatLogKind
    {
        /// <summary>开局：初始距离、天气。</summary>
        Setup = 0,

        /// <summary>移动：战线间距变化、单位位移（前进/后撤）、距离稳定。</summary>
        Move = 1,

        /// <summary>
        /// 攻击：**出手方视角的汇总**。一次出手一行，含命中率、命中数、
        /// 以及"甲弹对抗"的分布（弹开/减半/全额）与合计伤害。
        /// </summary>
        Attack = 2,

        /// <summary>
        /// 受击：**目标视角的逐次命中明细**。每次命中一行，含本次的护甲判定
        /// （净护甲、弹开/减半/全额）与命中后的剩余耐久。
        /// 比 <see cref="Attack"/> 细一层，量大时应当关掉。
        /// </summary>
        Hit = 3,

        /// <summary>状态变化：倒地、阵亡、撤退触发、脱离接触、脱离后复原。</summary>
        Status = 4,

        /// <summary>结局摘要。</summary>
        Outcome = 5,
    }

    /// <summary>日志过滤器（位标志）。</summary>
    [Flags]
    public enum CombatLogFilter
    {
        None = 0,
        Setup = 1 << 0,
        Move = 1 << 1,
        Attack = 1 << 2,
        Hit = 1 << 3,
        Status = 1 << 4,
        Outcome = 1 << 5,

        /// <summary>常用默认：除逐发明细外的全部。</summary>
        Default = Setup | Move | Attack | Status | Outcome,

        All = Setup | Move | Attack | Hit | Status | Outcome,
    }

    /// <summary>一条日志。</summary>
    public sealed class CombatLogEntry
    {
        public CombatLogKind Kind;

        /// <summary>发生的回合（1 起；开局与结局为 0）。</summary>
        public int Round;

        public string Text;

        public CombatLogEntry() { }

        public CombatLogEntry(CombatLogKind kind, int round, string text)
        {
            Kind = kind;
            Round = round;
            Text = text;
        }

        public override string ToString()
        {
            return Round > 0 ? string.Format("[{0} R{1}] {2}", Kind, Round, Text)
                             : string.Format("[{0}] {2}", Kind, Text);
        }
    }

    public static class CombatLogUtility
    {
        public static bool Allows(this CombatLogFilter filter, CombatLogKind kind)
        {
            return (filter & Bit(kind)) != 0;
        }

        public static CombatLogFilter Bit(CombatLogKind kind)
        {
            switch (kind)
            {
                case CombatLogKind.Setup: return CombatLogFilter.Setup;
                case CombatLogKind.Move: return CombatLogFilter.Move;
                case CombatLogKind.Attack: return CombatLogFilter.Attack;
                case CombatLogKind.Hit: return CombatLogFilter.Hit;
                case CombatLogKind.Status: return CombatLogFilter.Status;
                case CombatLogKind.Outcome: return CombatLogFilter.Outcome;
                default: return CombatLogFilter.None;
            }
        }

        /// <summary>按过滤器把日志摊平成文本行。</summary>
        public static List<string> ToLines(IList<CombatLogEntry> entries, CombatLogFilter filter)
        {
            List<string> lines = new List<string>();
            if (entries == null) return lines;
            for (int i = 0; i < entries.Count; i++)
            {
                CombatLogEntry e = entries[i];
                if (e == null || !filter.Allows(e.Kind)) continue;
                lines.Add(e.Round > 0
                    ? string.Format("第{0}回合：{1}", e.Round, e.Text)
                    : e.Text);
            }
            return lines;
        }

        public static string Join(IList<CombatLogEntry> entries, CombatLogFilter filter, string separator = "\n")
        {
            StringBuilder sb = new StringBuilder();
            List<string> lines = ToLines(entries, filter);
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(separator);
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }

        /// <summary>把过滤器描述成可读文本（用于 UI 与测试输出）。</summary>
        public static string Describe(CombatLogFilter filter)
        {
            List<string> parts = new List<string>();
            if ((filter & CombatLogFilter.Setup) != 0) parts.Add("开局");
            if ((filter & CombatLogFilter.Move) != 0) parts.Add("移动");
            if ((filter & CombatLogFilter.Attack) != 0) parts.Add("攻击");
            if ((filter & CombatLogFilter.Hit) != 0) parts.Add("受击");
            if ((filter & CombatLogFilter.Status) != 0) parts.Add("状态");
            if ((filter & CombatLogFilter.Outcome) != 0) parts.Add("结局");
            return parts.Count == 0 ? "（无）" : string.Join("+", parts);
        }
    }
}
