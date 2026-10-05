using System;
using System.Collections.Generic;
using System.Globalization;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 抽象战斗的回合引擎（DESIGN.md §19.4 / §19.5.1 / §19.16 / §19.20 / §19.21）。
    ///
    /// 纯函数式：给定 <see cref="CombatScene"/> 与 <see cref="IRng"/>，输出
    /// <see cref="CombatResult"/>。不引用任何游戏类型，不持有跨调用的状态，
    /// 因此同一个函数既用于**预告**（跑 N 次取分位），也用于**实际结算**（跑 1 次）。
    ///
    /// **空间模型（§19.21 逐单位距离带）**
    /// <code>
    /// 战线间距 gap ──┬── 我方纵深 depth[i] ──→ 单位 i 距敌方前线 = gap + depth[i]
    ///                └── 敌方纵深 depth[j] ──→ 单位 j 距我方前线 = gap + depth[j]
    ///
    /// 攻击距离(i, j) = gap + depth[i] + depth[j]      （近战忽略 depth[j]：绕过前线直取后排）
    /// 期望纵深(i)    = Clamp(有效射程(i) − gap, 0, MaxStandoff)
    /// </code>
    /// 于是长射程单位会在短射程单位把战线拉近时**后撤**，保住自己的交战距离 ——
    /// 这是单标量距离模型做不到的。
    ///
    /// **战斗日志（§19.22）** 按 <see cref="CombatLogKind"/> 分 6 类记录，
    /// 由 <see cref="CombatScene.LogFilter"/> 控制写不写（预告会关掉以省时间）。
    /// </summary>
    public static class CombatSimulator
    {
        public static CombatResult Simulate(CombatScene scene, IRng rng)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            return new Runner(scene, rng).Run();
        }

        // ════════════════════════════════════════════════════════════════
        //  一次模拟的全部可变状态都关在这里，保证 Simulate 无副作用泄漏
        // ════════════════════════════════════════════════════════════════
        private sealed class Runner
        {
            private readonly CombatScene scene;
            private readonly IRng rng;
            private readonly CombatResult result = new CombatResult();

            private readonly List<UnitState> all;
            private readonly List<UnitState> order;

            /// <summary>每个单位相对**己方前线**的纵深（与 all 同下标）。</summary>
            private readonly float[] depth;

            private readonly int myTotal;

            /// <summary>战线间距（双方前线之间的距离）。</summary>
            private float gap;

            private float weather = 1f;
            private string weatherName = "—";
            private bool loggedEngaged;
            private bool loggedStable;

            public Runner(CombatScene scene, IRng rng)
            {
                this.scene = scene;
                this.rng = rng;
                scene.Validate();

                all = new List<UnitState>(scene.Units.Count);
                for (int i = 0; i < scene.Units.Count; i++)
                {
                    CombatUnitSnapshot snap = scene.Units[i];
                    all.Add(new UnitState { Snap = snap, Health = snap.MaxHealth, Index = i });
                    result.Units.Add(new UnitReport
                    {
                        Name = snap.Name,
                        IsMine = snap.IsMine,
                        HealthStart = snap.MaxHealth,
                        HealthEnd = snap.MaxHealth,
                    });
                }

                depth = new float[all.Count];
                order = new List<UnitState>(all);
                myTotal = scene.CountMine();
                gap = scene.StartDistance;
            }

            public CombatResult Run()
            {
                weather = SampleWeather();

                Log(CombatLogKind.Setup, 0, string.Format(CultureInfo.InvariantCulture,
                    "开局：战线间距 {0:0.#} 格，天气 {1}（命中率 ×{2:0.##}），纵深上限 {3:0.#} 格。",
                    gap, weatherName, weather, scene.MaxStandoff));

                int round = 0;
                while (round < scene.MaxRounds)
                {
                    round++;

                    // ── 1. 交火：只有射程够得着的单位能出手 ────────────────
                    rng.Shuffle(order);
                    for (int i = 0; i < order.Count; i++)
                    {
                        UnitState attacker = order[i];
                        if (attacker.Out) continue;

                        UnitState target = PickTarget(attacker);
                        if (target == null) continue;          // 够不着任何人，等移动

                        NoteEngaged(round);
                        Attack(attacker, target, round, OutputMultiplier(attacker));
                    }

                    // ── 2. 结束判定，顺序有讲究（§19.10）────────────────────
                    if (CountActive(true) == 0 && CountDead(true) == myTotal)
                        return Finish(CombatOutcome.Defeat, round);

                    if (CountActive(false) == 0)
                        return Finish(CountDead(true) > 0 ? CombatOutcome.PyrrhicVictory : CombatOutcome.Victory, round);

                    if (CountActive(true) == 0)
                        return Finish(CombatOutcome.Defeat, round);

                    if (ShouldRetreat())
                    {
                        DisengageRound(round);
                        return Finish(CombatOutcome.Retreat, round);
                    }

                    // ── 3. 移动：纵深调整 + 战线推进 ────────────────────────
                    Advance(round);
                }

                return Finish(CombatOutcome.Timeout, round);
            }

            // ── 日志 ────────────────────────────────────────────────────

            private void Log(CombatLogKind kind, int round, string text)
            {
                if (!scene.LogFilter.Allows(kind)) return;
                result.Entries.Add(new CombatLogEntry(kind, round, text));
            }

            // ── 空间 ────────────────────────────────────────────────────

            private float EffectiveRange(UnitState u)
            {
                return scene.EffectiveRange(u.Snap);
            }

            private float DepthOf(UnitState u)
            {
                return depth[u.Index];
            }

            private float AttackDistance(UnitState attacker, UnitState target)
            {
                return CombatScene.AttackDistance(gap, attacker.Snap, DepthOf(attacker), DepthOf(target));
            }

            private void NoteEngaged(int round)
            {
                if (loggedEngaged) return;
                loggedEngaged = true;
                Log(CombatLogKind.Setup, round, string.Format(CultureInfo.InvariantCulture,
                    "开始交火：战线间距 {0:0.#} 格。", gap));
            }

            private void Advance(int round)
            {
                if (scene.Closing == ClosingPolicy.Never) return;

                float before = gap;

                // ① 每个单位把纵深调整到期望值（后撤慢、前压快）
                for (int i = 0; i < all.Count; i++)
                {
                    UnitState u = all[i];
                    if (u.Out) continue;

                    float pref = PreferredDepthFor(u);
                    float d = depth[i];
                    float moved;
                    bool fallingBack = pref > d + 0.001f;

                    if (fallingBack)
                        moved = Math.Min(pref - d, scene.FallbackFactor * Math.Max(0f, u.Snap.MoveSpeed));
                    else if (pref < d - 0.001f)
                        moved = Math.Min(d - pref, Math.Max(0f, u.Snap.MoveSpeed));
                    else
                        continue;

                    depth[i] = fallingBack ? d + moved : d - moved;

                    if (moved >= 0.5f)
                    {
                        Log(CombatLogKind.Move, round, string.Format(CultureInfo.InvariantCulture,
                            "{0} {1} {2:0.#} 格 → 纵深 {3:0.#}（距敌前线 {4:0.#}）",
                            u.Snap.Name, fallingBack ? "后撤" : "前压", moved, depth[i], gap + depth[i]));
                    }
                }

                // ② 前线推进：只有"站在前线上、而且够不着"的单位会压低战线间距
                float step = FrontAdvance(true) + FrontAdvance(false);
                if (step > 0f)
                {
                    float eq = scene.EquilibriumDistance();
                    float next = gap - step;
                    if (next < eq) next = eq;
                    if (next < gap - 0.001f)
                    {
                        gap = next;
                        Log(CombatLogKind.Move, round, string.Format(CultureInfo.InvariantCulture,
                            "战线间距 {0:0.#} → {1:0.#} 格。", before, gap));
                    }
                }

                if (!loggedStable && gap <= scene.EquilibriumDistance() + 0.001f)
                {
                    loggedStable = true;
                    Log(CombatLogKind.Move, round, string.Format(CultureInfo.InvariantCulture,
                        "战线间距稳定在 {0:0.#} 格。", gap));
                }
            }

            /// <summary>
            /// 本单位当前的期望纵深。按 <see cref="StandoffPolicy"/> 决定要不要后撤：
            /// 只有**自己能压制对手射程、而且撤得出去**时，后撤才有意义（§19.21.4）。
            /// </summary>
            private float PreferredDepthFor(UnitState u)
            {
                if (scene.Standoff == StandoffPolicy.Never) return 0f;

                float r = EffectiveRange(u);
                if (scene.Standoff == StandoffPolicy.OnlyIfOutranging)
                {
                    float enemyMin = EnemyMinEffectiveRange(u);
                    if (r <= enemyMin) return 0f;                                     // 射程不占优
                    if (gap + Math.Max(0f, scene.MaxStandoff) <= enemyMin) return 0f; // 撤不出去
                }
                return CombatMath.Clamp(r - gap, 0f, Math.Max(0f, scene.MaxStandoff));
            }

            /// <summary>对某单位而言，敌方**存活**单位中最短的有效射程。</summary>
            private float EnemyMinEffectiveRange(UnitState u)
            {
                float best = float.MaxValue;
                bool any = false;
                for (int i = 0; i < all.Count; i++)
                {
                    UnitState e = all[i];
                    if (e.Out || e.Snap.IsMine == u.Snap.IsMine) continue;
                    float r = EffectiveRange(e);
                    if (r < best) best = r;
                    any = true;
                }
                return any ? best : 0f;
            }

            /// <summary>某方的前线推进速度：取"在前线上、且仍够不着"的单位中最慢者。</summary>
            private float FrontAdvance(bool mine)
            {
                float slowest = float.MaxValue;
                bool any = false;

                for (int i = 0; i < all.Count; i++)
                {
                    UnitState u = all[i];
                    if (u.Out || u.Snap.IsMine != mine) continue;
                    if (depth[i] > 0.001f) continue;                              // 不在前线上，不带动战线
                    if (scene.Closing == ClosingPolicy.UntilRangedEngaged && u.Snap.IsMelee) continue;
                    if (EffectiveRange(u) >= gap) continue;                        // 已经够得着，停下
                    if (u.Snap.MoveSpeed < slowest) slowest = u.Snap.MoveSpeed;
                    any = true;
                }
                return any ? Math.Max(0f, slowest) : 0f;
            }

            private float SampleWeather()
            {
                if (scene.WeatherTable.Count == 0) { weatherName = "无"; return 1f; }

                float total = 0f;
                for (int i = 0; i < scene.WeatherTable.Count; i++)
                    total += Math.Max(0f, scene.WeatherTable[i].Weight);
                if (total <= 0f) { weatherName = "无"; return 1f; }

                float roll = rng.Range(0f, total);
                float acc = 0f;
                for (int i = 0; i < scene.WeatherTable.Count; i++)
                {
                    WeatherSample w = scene.WeatherTable[i];
                    float weight = Math.Max(0f, w.Weight);
                    if (weight <= 0f) continue;
                    acc += weight;
                    if (roll < acc) { weatherName = w.Name; return w.AccuracyMultiplier; }
                }

                WeatherSample last = scene.WeatherTable[scene.WeatherTable.Count - 1];
                weatherName = last.Name;
                return last.AccuracyMultiplier;
            }

            // ── 攻击 ────────────────────────────────────────────────────

            private float OutputMultiplier(UnitState attacker)
            {
                return attacker.Snap.IsMine ? 1f : scene.EnemyOutputFactor;
            }

            /// <summary>
            /// 一次出手。**日志分两层**：
            ///   • <see cref="CombatLogKind.Attack"/> —— 出手方视角的汇总（一行/次）
            ///   • <see cref="CombatLogKind.Hit"/>    —— 目标视角的逐次命中明细（一次/命中）
            /// 两者都带"甲弹对抗"信息。
            /// </summary>
            private void Attack(UnitState attacker, UnitState target, int round, float shotMultiplier)
            {
                float dist = AttackDistance(attacker, target);
                float hit = scene.HitChanceAt(attacker.Snap, dist, scene.EnvironmentMultiplier(weather));
                int shots = RollShotCount(attacker.Snap.ShotsPerRound * shotMultiplier);
                if (shots <= 0) return;

                float pDeflect, pHalf, pFull;
                CombatMath.ArmorProbabilities(target.Snap.ArmorRating, attacker.Snap.ArmorPen,
                                              out pDeflect, out pHalf, out pFull);

                int hits = 0, deflect = 0, half = 0, full = 0;
                float totalDamage = 0f;

                for (int s = 0; s < shots; s++)
                {
                    if (target.Out) break;
                    if (!rng.Chance(hit)) continue;                     // 未命中

                    hits++;
                    ArmorRollResult roll;
                    float dmg = CombatMath.RollPostArmorDamage(
                        rng, attacker.Snap.DamagePerShot, target.Snap.ArmorRating, attacker.Snap.ArmorPen, out roll);

                    switch (roll)
                    {
                        case ArmorRollResult.Deflected: deflect++; break;
                        case ArmorRollResult.Halved: half++; break;
                        case ArmorRollResult.Full: full++; break;
                    }
                    totalDamage += dmg;

                    // 先算好受击前后的耐久再写日志 —— 早期版本在 ApplyDamage 之前写，
                    // 导致"剩余"显示的是命中**前**的值，与"本发 N 伤害"自相矛盾。
                    float before = Math.Max(0f, target.Health);
                    float after = Math.Max(0f, before - dmg);

                    Log(CombatLogKind.Hit, round, string.Format(CultureInfo.InvariantCulture,
                        "{0} 受击（{1}）@{2:0.#} 格 · 甲 {3:0.##} − 破甲 {4:0.##} = 净 {5:0.##} → {6} · 本发 {7:0.#} 伤害 · 耐久 {8:0.#} → {9:0.#}/{10:0.#}",
                        target.Snap.Name, attacker.Snap.Name, dist,
                        target.Snap.ArmorRating, attacker.Snap.ArmorPen,
                        Math.Max(0f, target.Snap.ArmorRating - attacker.Snap.ArmorPen),
                        roll.Label(), dmg, before, after, target.MaxHealth));

                    if (dmg > 0f) ApplyDamage(target, dmg, round, attacker);
                }

                Log(CombatLogKind.Attack, round, string.Format(CultureInfo.InvariantCulture,
                    "{0} → {1} @{2:0.#} 格 · 命中率 {3:P0} · 射击 {4} · 命中 {5} · 甲弹对抗 弹开 {6} / 减半 {7} / 全额 {8} · 合计 {9:0.#} 伤害{10}",
                    attacker.Snap.Name, target.Snap.Name, dist, hit, shots, hits,
                    deflect, half, full, totalDamage,
                    target.Out ? "（目标已退出战斗）" : ""));
            }

            /// <summary>把小数射击次数按概率进位，保证长期均值不被抹掉。</summary>
            private int RollShotCount(float shotsPerRound)
            {
                if (shotsPerRound <= 0f) return 0;
                int whole = (int)shotsPerRound;
                float frac = shotsPerRound - whole;
                if (frac > 0f && rng.Chance(frac)) whole++;
                return whole;
            }

            private void ApplyDamage(UnitState target, float dmg, int round, UnitState attacker)
            {
                if (dmg <= 0f || target.Out) return;

                target.Health -= dmg;

                if (target.Health <= 0f)
                {
                    target.Health = 0f;
                    target.Dead = true;
                    target.Downed = true;
                    Log(CombatLogKind.Status, round, string.Format(CultureInfo.InvariantCulture,
                        "{0}{1} 阵亡（被 {2} 打死）。",
                        target.Snap.IsMine ? "我方 " : "敌方 ", target.Snap.Name, attacker.Snap.Name));
                    return;
                }

                if (!target.Downed && target.HealthFraction <= scene.DownHealthFraction)
                {
                    target.Downed = true;
                    Log(CombatLogKind.Status, round, string.Format(CultureInfo.InvariantCulture,
                        "{0}{1} 倒地（耐久 {2:P0} ≤ 阈值 {3:P0}）。",
                        target.Snap.IsMine ? "我方 " : "敌方 ", target.Snap.Name,
                        target.HealthFraction, scene.DownHealthFraction));
                }
            }

            /// <summary>撤退时的一轮"脱离接触"：只有敌方开火，输出打折。</summary>
            private void DisengageRound(int round)
            {
                Log(CombatLogKind.Status, round, "我方脱离接触。");

                List<UnitState> enemies = new List<UnitState>();
                for (int i = 0; i < order.Count; i++)
                    if (!order[i].Snap.IsMine && !order[i].Out) enemies.Add(order[i]);

                for (int i = 0; i < enemies.Count; i++)
                {
                    UnitState target = PickTarget(enemies[i]);
                    if (target == null) continue;
                    Attack(enemies[i], target, round, scene.DisengageFactor * OutputMultiplier(enemies[i]));
                }
            }

            // ── 目标选择与结束条件 ──────────────────────────────────────

            /// <summary>
            /// 索敌（§19.5.1）。**只考虑射程够得着的目标** —— 够不着任何人的返回 null，
            /// 该单位本回合按兵不动（去移动）。
            ///
            /// 同分用蓄水池抽样随机决出：早期版本用严格 <c>score &gt; bestScore</c> 比较，
            /// 同分时永远保留名册里的第一个候选，于是 Random 退化成"永远打第一个"。
            /// 见 SelfTest.TargetingTieBreakIsRandom。
            /// </summary>
            private UnitState PickTarget(UnitState attacker)
            {
                bool wantMine = !attacker.Snap.IsMine;
                TargetPriority priority = attacker.Snap.IsMine ? scene.OurPriority : scene.EnemyPriority;
                float myRange = EffectiveRange(attacker);

                UnitState best = null;
                float bestScore = 0f;
                int ties = 0;

                for (int i = 0; i < all.Count; i++)
                {
                    UnitState c = all[i];
                    if (c.Out || c.Snap.IsMine != wantMine) continue;
                    if (AttackDistance(attacker, c) > myRange) continue;      // 够不着

                    float score = ScoreTarget(priority, c);

                    if (best == null || score > bestScore)
                    {
                        best = c;
                        bestScore = score;
                        ties = 1;
                    }
                    else if (score == bestScore)
                    {
                        ties++;
                        if (rng.Range(0, ties) == 0) best = c;
                    }
                }
                return best;
            }

            private static float ScoreTarget(TargetPriority priority, UnitState c)
            {
                switch (priority)
                {
                    case TargetPriority.Strongest:
                        return c.Snap.ThreatWeight;
                    case TargetPriority.Weakest:
                        return -c.HealthFraction;
                    default:
                        return 0f;   // Random：全候选同分 ⇒ 蓄水池抽样给出等概率
                }
            }

            private bool ShouldRetreat()
            {
                switch (scene.Retreat.Kind)
                {
                    case RetreatPolicyKind.Never:
                        return false;
                    case RetreatPolicyKind.OnAnyDown:
                        for (int i = 0; i < all.Count; i++)
                            if (all[i].Snap.IsMine && all[i].Downed && !all[i].Dead) return true;
                        return false;
                    case RetreatPolicyKind.OnAnyDeath:
                        return CountDead(true) > 0;
                    case RetreatPolicyKind.CasualtyFraction:
                        int casual = myTotal - CountActive(true);
                        return myTotal > 0 && (float)casual / myTotal >= scene.Retreat.CasualtyFraction;
                    default:
                        return false;
                }
            }

            private int CountActive(bool mine)
            {
                int n = 0;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Snap.IsMine == mine && !all[i].Out) n++;
                return n;
            }

            private int CountDead(bool mine)
            {
                int n = 0;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Snap.IsMine == mine && all[i].Dead) n++;
                return n;
            }

            private CombatResult Finish(CombatOutcome outcome, int round)
            {
                result.Outcome = outcome;
                result.Rounds = round;
                result.Ticks = round * scene.TicksPerRound;
                result.FinalDistance = gap;
                result.WeatherName = weatherName;
                result.WeatherMultiplier = weather;

                for (int i = 0; i < all.Count; i++)
                {
                    UnitState st = all[i];
                    UnitReport rep = result.Units[i];
                    rep.HealthEnd = st.Health;
                    rep.Dead = st.Dead;
                    rep.Downed = st.Downed;
                    rep.FinalStandoff = depth[i];

                    if (st.Snap.IsMine)
                    {
                        result.OurTotal++;
                        if (st.Dead) result.OurDead++;
                        else if (st.Downed) result.OurDowned++;
                    }
                    else
                    {
                        result.EnemyTotal++;
                        if (st.Dead) result.EnemyDead++;
                        else if (st.Downed) result.EnemyDowned++;
                    }
                }

                Log(CombatLogKind.Outcome, 0, result.Summary());
                return result;
            }

            private sealed class UnitState
            {
                public CombatUnitSnapshot Snap;
                public int Index;
                public float Health;
                public bool Dead;
                public bool Downed;
                public bool Out => Dead || Downed;
                public float MaxHealth => Snap.MaxHealth <= 0f ? 1f : Snap.MaxHealth;
                public float HealthFraction => CombatMath.Clamp01(Health / MaxHealth);
            }
        }
    }
}
