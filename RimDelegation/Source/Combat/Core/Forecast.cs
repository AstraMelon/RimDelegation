using System;
using System.Collections.Generic;
using System.Globalization;

namespace RimDelegation.Combat
{
    /// <summary>按种子产出随机源 —— 让核心与"到底用哪个 RNG"解耦。</summary>
    public delegate IRng RngFactory(int seed);

    /// <summary>
    /// 预告的统计结果（DESIGN.md §19.12 / §19.5）。
    ///
    /// **P50 给"预计"，P90 给"最坏情况"。** 刻意按 P90 而非均值承诺最坏情况，
    /// 因为玩家真正会记住的是"你说最多死 1 个，结果死了 3 个"。
    /// </summary>
    public sealed class ForecastResult
    {
        public int Iterations;
        public float WinRate;
        public float RetreatRate;
        public float DefeatRate;

        public int CasualtiesP50, CasualtiesP90, CasualtiesMax;
        public int DeathsP50, DeathsP90, DeathsMax;
        public float DaysP50, DaysP90;
        public int RoundsP50, RoundsP90;

        public CombatOutcome ModalOutcome;

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "成功率 {0:P0} · 预计用时 {1:0.0}–{2:0.0} 小时 · 伤员 {3}（最坏 {4}）· 阵亡 {5}（最坏 {6}）· 结局多为 {7}",
                WinRate,
                DaysP50 * 24f, DaysP90 * 24f,
                CasualtiesP50, CasualtiesP90,
                DeathsP50, DeathsP90,
                ModalOutcome);
        }
    }

    /// <summary>
    /// 蒙特卡洛预告。**与实际结算跑的是同一个 <see cref="CombatSimulator.Simulate"/>**，
    /// 所以预告不是"另一个近似公式"，而是同一引擎的采样统计 ——
    /// 结构上不可能出现"预告 95% 却全灭"。
    /// </summary>
    public static class Forecast
    {
        public static ForecastResult Run(CombatScene scene, int iterations, int baseSeed, RngFactory rngFactory)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (rngFactory == null) throw new ArgumentNullException(nameof(rngFactory));
            if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));

            // 跑 200~1000 次还逐条拼日志字符串是纯浪费 —— 克隆一份关掉日志再跑。
            // 克隆只做一次（不是每次迭代），且 Simulate 不修改场景，所以结果与原件一致。
            CombatScene fast = scene.Clone();
            fast.LogFilter = CombatLogFilter.None;

            int[] casualties = new int[iterations];
            int[] deaths = new int[iterations];
            int[] rounds = new int[iterations];
            Dictionary<CombatOutcome, int> outcomeCounts = new Dictionary<CombatOutcome, int>();

            int wins = 0, retreats = 0, defeats = 0;

            for (int i = 0; i < iterations; i++)
            {
                IRng rng = rngFactory(unchecked(baseSeed + i * 7919));
                CombatResult r;
                try
                {
                    r = CombatSimulator.Simulate(fast, rng);
                }
                finally
                {
                    // 游戏侧的 RandRng 是 IDisposable（PushState/PopState）。
                    // 不释放会让随机栈每迭代涨一层 —— 必须在核心这里兜住，
                    // 而不是指望每个调用方记得 using（§19.16.5）。
                    IDisposable d = rng as IDisposable;
                    if (d != null) d.Dispose();
                }
                casualties[i] = r.OurCasualties;
                deaths[i] = r.OurDead;
                rounds[i] = r.Rounds;

                int c;
                outcomeCounts.TryGetValue(r.Outcome, out c);
                outcomeCounts[r.Outcome] = c + 1;

                if (r.ThreatCleared) wins++;
                else if (r.Outcome == CombatOutcome.Retreat || r.Outcome == CombatOutcome.Timeout) retreats++;
                else defeats++;
            }

            Array.Sort(casualties);
            Array.Sort(deaths);
            Array.Sort(rounds);

            ForecastResult f = new ForecastResult
            {
                Iterations = iterations,
                WinRate = (float)wins / iterations,
                RetreatRate = (float)retreats / iterations,
                DefeatRate = (float)defeats / iterations,

                CasualtiesP50 = Percentile(casualties, 0.50f),
                CasualtiesP90 = Percentile(casualties, 0.90f),
                CasualtiesMax = casualties[iterations - 1],

                DeathsP50 = Percentile(deaths, 0.50f),
                DeathsP90 = Percentile(deaths, 0.90f),
                DeathsMax = deaths[iterations - 1],

                RoundsP50 = Percentile(rounds, 0.50f),
                RoundsP90 = Percentile(rounds, 0.90f),
            };

            f.DaysP50 = f.RoundsP50 * scene.TicksPerRound / 60000f;
            f.DaysP90 = f.RoundsP90 * scene.TicksPerRound / 60000f;

            int best = -1;
            foreach (KeyValuePair<CombatOutcome, int> kv in outcomeCounts)
            {
                if (kv.Value > best) { best = kv.Value; f.ModalOutcome = kv.Key; }
            }

            return f;
        }

        /// <summary>最近秩百分位（nearest-rank）。数组必须已升序。</summary>
        public static int Percentile(int[] sorted, float p)
        {
            if (sorted == null || sorted.Length == 0) return 0;
            if (p <= 0f) return sorted[0];
            if (p >= 1f) return sorted[sorted.Length - 1];
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        public static float Percentile(float[] sorted, float p)
        {
            if (sorted == null || sorted.Length == 0) return 0f;
            if (p <= 0f) return sorted[0];
            if (p >= 1f) return sorted[sorted.Length - 1];
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }
    }
}
