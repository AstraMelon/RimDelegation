using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimDelegation.Combat
{
    /// <summary>战斗结局 —— 对应 DESIGN.md §19.10。</summary>
    public enum CombatOutcome
    {
        /// <summary>敌方全部退出战斗，我方无阵亡。</summary>
        Victory = 0,
        /// <summary>胜利但我方有阵亡。</summary>
        PyrrhicVictory = 1,
        /// <summary>触发撤退策略退出。</summary>
        Retreat = 2,
        /// <summary>我方全部退出战斗。</summary>
        Defeat = 3,
        /// <summary>回合上限，按撤退处理。</summary>
        Timeout = 4,
    }

    /// <summary>单个单位的终局状态。</summary>
    public sealed class UnitReport
    {
        public string Name;
        public bool IsMine;
        public float HealthStart;
        public float HealthEnd;
        public bool Dead;
        public bool Downed;

        /// <summary>结束时相对己方前线的纵深（格）。</summary>
        public float FinalStandoff;

        public float HealthFractionEnd => HealthStart <= 0f ? 0f : CombatMath.Clamp01(HealthEnd / HealthStart);
    }

    /// <summary>
    /// 一场抽象战斗的结果。**可复现**：相同种子 + 相同场景 ⇒ 逐字段一致。
    /// </summary>
    public sealed class CombatResult
    {
        public CombatOutcome Outcome;
        public int Rounds;
        public int Ticks;

        /// <summary>结束时的**战线间距**（格）。注意逐单位距离见 <see cref="UnitReport.FinalStandoff"/>。</summary>
        public float FinalDistance;

        /// <summary>本场采样到的天气（名字与命中率乘子）。</summary>
        public string WeatherName = "—";
        public float WeatherMultiplier = 1f;

        public int OurDead;
        public int OurDowned;
        public int OurTotal;

        public int EnemyDead;
        public int EnemyDowned;
        public int EnemyTotal;

        public readonly List<UnitReport> Units = new List<UnitReport>();

        /// <summary>
        /// 结构化战斗日志（§19.22）。**写入受 <see cref="CombatScene.LogFilter"/> 控制** ——
        /// 预告的蒙特卡洛会关掉它，所以这里可能是空的。
        /// </summary>
        public readonly List<CombatLogEntry> Entries = new List<CombatLogEntry>();

        /// <summary>我方伤亡总数（阵亡 + 倒地），预告的统计对象。</summary>
        public int OurCasualties => OurDead + OurDowned;

        /// <summary>敌方伤亡总数。</summary>
        public int EnemyCasualties => EnemyDead + EnemyDowned;

        /// <summary>威胁是否已被解除 —— 只有 Victory / PyrrhicVictory 才置位（§19.10）。</summary>
        public bool ThreatCleared => Outcome == CombatOutcome.Victory || Outcome == CombatOutcome.PyrrhicVictory;

        public float DurationDays => Ticks / 60000f;

        /// <summary>按过滤器取日志文本行。</summary>
        public List<string> LogLines(CombatLogFilter filter)
        {
            return CombatLogUtility.ToLines(Entries, filter);
        }

        /// <summary>按过滤器取日志全文。</summary>
        public string LogText(CombatLogFilter filter)
        {
            return CombatLogUtility.Join(Entries, filter);
        }

        /// <summary>某一类日志的条数（UI 用来显示"有多少条被过滤掉了"）。</summary>
        public int CountOf(CombatLogKind kind)
        {
            int n = 0;
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i] != null && Entries[i].Kind == kind) n++;
            return n;
        }

        /// <summary>结果的稳定摘要，用于确定性自测比对。**不含日志**（日志受过滤器影响）。</summary>
        public string Fingerprint()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Outcome).Append('|').Append(Rounds).Append('|')
              .Append(OurDead).Append('/').Append(OurDowned).Append('|')
              .Append(EnemyDead).Append('/').Append(EnemyDowned).Append('|')
              .Append(Math.Round(FinalDistance, 2).ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(WeatherName).Append('@').Append(Math.Round(WeatherMultiplier, 3).ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < Units.Count; i++)
            {
                UnitReport u = Units[i];
                sb.Append('|').Append(u.Name).Append(':')
                  .Append(u.Dead ? 'D' : (u.Downed ? 'd' : 'a'))
                  .Append(':').Append(Math.Round(u.HealthEnd, 3).ToString(CultureInfo.InvariantCulture))
                  .Append(':').Append(Math.Round(u.FinalStandoff, 2).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} · {1} 回合（{2:0.00} 天）· 终战线间距 {3:0.#} 格 · 天气 {4}×{5:0.##} · 我方 {6} 阵亡/{7} 倒地 · 敌方 {8} 阵亡/{9} 倒地 · 威胁解除={10}",
                Outcome, Rounds, DurationDays, FinalDistance, WeatherName, WeatherMultiplier,
                OurDead, OurDowned, EnemyDead, EnemyDowned, ThreatCleared);
        }
    }
}
