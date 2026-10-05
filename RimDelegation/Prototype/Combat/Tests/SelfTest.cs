using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 原型自测。**全部离线可跑**，不依赖 RimWorld / Unity。
    ///
    /// 覆盖三类断言：
    ///   1. 数学正确性 —— 解析护甲分布 vs vanilla 算法的逐字转写（参照实现）
    ///   2. 引擎不变量 —— 确定性、场景不被改写、随机性只走注入的 IRng
    ///   3. 行为合理性 —— 单调性、结局判定、撤退语义、预告包络
    ///
    /// 注意第 3 类里的"预告包络"是**引擎自洽性**检查（同一引擎采样 vs 结算），
    /// 不是**保真度**检查 —— 保真度要用游戏内真地图战斗的数据标定，见 DESIGN.md §19.16.6。
    /// </summary>
    public static class SelfTest
    {
        public sealed class Report
        {
            public int Passed;
            public int Failed;
            public readonly List<string> Failures = new List<string>();

            /// <summary>报告输出目标。控制台用 Console.Out；桌面 UI 用 StringWriter 捕获后显示。</summary>
            public TextWriter Out = Console.Out;
        }

        private static void Check(Report r, string name, bool ok, string detail = null)
        {
            if (ok)
            {
                r.Passed++;
                r.Out.WriteLine("  [PASS] " + name);
            }
            else
            {
                r.Failed++;
                string line = name + (detail == null ? "" : "  → " + detail);
                r.Failures.Add(line);
                r.Out.WriteLine("  [FAIL] " + line);
            }
        }

        private static RngFactory Pure => seed => new XorShiftRng(seed);

        public static Report RunAll()
        {
            return RunAll(Console.Out);
        }

        public static Report RunAll(TextWriter output)
        {
            Report r = new Report { Out = output ?? TextWriter.Null };

            r.Out.WriteLine("== 1. 数学正确性 ==");
            ArmorDistributionMatchesVanillaReference(r);
            ExpectedDamageMatchesVanillaReference(r);

            r.Out.WriteLine("== 2. 引擎不变量 ==");
            DeterminismSameSeedSameResult(r);
            DifferentSeedsProduceVariation(r);
            SceneIsNotMutated(r);
            RandomnessOnlyThroughInjectedRng(r);

            r.Out.WriteLine("== 3. 行为合理性 ==");
            BetterArmorReducesCasualties(r);
            MoreFirepowerReducesEnemySurvivors(r);
            EnemyOutputFactorAffectsOutcome(r);
            CoverFactorIsSymmetricAndLengthensFight(r);
            SpatialOpeningIsOutOfRange(r);
            TerrainAdvantageFiresFirst(r);
            MeleeDragsDistanceToMeleeRange(r);
            ClosingPolicyNeverKeepsDistance(r);
            WeatherIsSampledPerBattle(r);
            StandoffPreservesLongRangeDistance(r);
            StandoffPolicyRespectsOutRanging(r);
            ZeroStandoffDegeneratesToSingleLine(r);
            LogFilterIsRespected(r);
            LogRecordsArmorInteraction(r);
            ForecastLeavesCallerSceneUntouched(r);
            TargetingTieBreakIsRandom(r);
            RetreatPolicyEndsFightEarlier(r);
            ShortFightProducesDownedNotDead(r);
            InvulnerableTargetTimesOut(r);
            OverwhelmingForceIsCleanVictory(r);
            RetreatWritesDisengageLine(r);
            ForecastEnvelopeHolds(r);
            CodecRoundTrip(r);

            return r;
        }

        // ── 1. 数学 ─────────────────────────────────────────────────────

        private static void ArmorDistributionMatchesVanillaReference(Report r)
        {
            // 覆盖：无护甲 / 常规 / 护甲高于破甲 / 护甲远高于破甲（num > 1）
            float[][] cases =
            {
                new[] { 0.31f, 0.16f },
                new[] { 0.45f, 0.14f },
                new[] { 0.05f, 0.05f },
                new[] { 0.90f, 0.00f },
                new[] { 0.30f, 0.50f },   // 破甲高于护甲 ⇒ 应全部全额
                new[] { 2.00f, 0.00f },   // num > 1 ⇒ 应永不全额
            };

            const int N = 400000;
            bool allOk = true;
            StringBuilder detail = new StringBuilder();

            foreach (float[] c in cases)
            {
                float armor = c[0], ap = c[1];
                float pDeflect, pHalf, pFull;
                CombatMath.ArmorProbabilities(armor, ap, out pDeflect, out pHalf, out pFull);

                XorShiftRng rng = new XorShiftRng(12345);
                int nDeflect = 0, nHalf = 0, nFull = 0;
                for (int i = 0; i < N; i++)
                {
                    float dmg = CombatMath.ReferenceVanillaPostArmorDamage(rng, 10f, armor, ap);
                    if (dmg <= 0f) nDeflect++;
                    else if (dmg < 10f) nHalf++;
                    else nFull++;
                }

                float oDeflect = (float)nDeflect / N, oHalf = (float)nHalf / N, oFull = (float)nFull / N;
                const float tol = 0.006f;   // ≈ 3σ，N=4e5
                bool ok = Math.Abs(oDeflect - pDeflect) < tol
                       && Math.Abs(oHalf - pHalf) < tol
                       && Math.Abs(oFull - pFull) < tol;
                if (!ok)
                {
                    allOk = false;
                    detail.Append(string.Format(CultureInfo.InvariantCulture,
                        "[armor={0} ap={1} 解析 {2:0.000}/{3:0.000}/{4:0.000} 实测 {5:0.000}/{6:0.000}/{7:0.000}] ",
                        armor, ap, pDeflect, pHalf, pFull, oDeflect, oHalf, oFull));
                }
            }

            Check(r, "解析护甲分布 == vanilla ApplyArmor 分支链（" + cases.Length + " 组 × " + N + " 次）",
                  allOk, allOk ? null : detail.ToString());
        }

        private static void ExpectedDamageMatchesVanillaReference(Report r)
        {
            float[][] cases =
            {
                new[] { 12f, 0.31f, 0.16f },
                new[] { 18f, 0.55f, 0.30f },
                new[] { 6f, 0.18f, 0.09f },
                new[] { 22f, 0.40f, 0.22f },
                new[] { 15f, 0.60f, 0.22f },
            };

            const int N = 400000;
            bool allOk = true;
            StringBuilder detail = new StringBuilder();

            foreach (float[] c in cases)
            {
                float amount = c[0], armor = c[1], ap = c[2];
                float expected = CombatMath.ExpectedPostArmorDamage(amount, armor, ap);

                XorShiftRng rng = new XorShiftRng(999);
                double sum = 0;
                for (int i = 0; i < N; i++)
                    sum += CombatMath.ReferenceVanillaPostArmorDamage(rng, amount, armor, ap);
                double observed = sum / N;

                // RoundRandom 是无偏的（概率进位），所以这里只该有采样误差
                float tol = Math.Max(0.05f, amount * 0.01f);
                bool ok = Math.Abs(observed - expected) < tol;
                if (!ok)
                {
                    allOk = false;
                    detail.Append(string.Format(CultureInfo.InvariantCulture,
                        "[dmg={0} armor={1} ap={2} 解析期望 {3:0.0000} 实测 {4:0.0000}] ",
                        amount, armor, ap, expected, observed));
                }
            }

            Check(r, "解析期望伤害 == 参照实现采样均值（" + cases.Length + " 组 × " + N + " 次）",
                  allOk, allOk ? null : detail.ToString());
        }

        // ── 2. 不变量 ───────────────────────────────────────────────────

        private static void DeterminismSameSeedSameResult(Report r)
        {
            string first = null;
            bool ok = true;
            for (int i = 0; i < 50; i++)
            {
                CombatResult res = CombatSimulator.Simulate(Scenarios.Outpost(), new XorShiftRng(4242));
                string fp = res.Fingerprint();
                if (first == null) first = fp;
                else if (fp != first) { ok = false; break; }
            }
            Check(r, "同种子 × 50 次 ⇒ 结果指纹完全一致", ok, ok ? null : "指纹发生漂移");
        }

        private static void DifferentSeedsProduceVariation(Report r)
        {
            HashSet<string> fps = new HashSet<string>();
            for (int i = 0; i < 200; i++)
                fps.Add(CombatSimulator.Simulate(Scenarios.Outpost(), new XorShiftRng(i * 131 + 7)).Fingerprint());
            Check(r, "不同种子 × 200 次 ⇒ 结果有分布（非退化）", fps.Count >= 5,
                  "不同指纹数 = " + fps.Count);
        }

        private static void SceneIsNotMutated(Report r)
        {
            CombatScene scene = Scenarios.Manhunters();
            string before = SnapshotCodec.Encode(scene);
            for (int i = 0; i < 20; i++)
                CombatSimulator.Simulate(scene, new XorShiftRng(i));
            string after = SnapshotCodec.Encode(scene);
            Check(r, "Simulate 不修改输入场景（20 次后快照逐字节一致）", before == after,
                  before == after ? null : "场景被就地改写");
        }

        private sealed class CountingRng : IRng
        {
            private readonly IRng inner;
            public long ValueCalls, ChanceCalls, RangeCalls;
            public CountingRng(IRng inner) { this.inner = inner; }
            public float Value { get { ValueCalls++; return inner.Value; } }
            public bool Chance(float p) { ChanceCalls++; return inner.Chance(p); }
            public int Range(int min, int max) { RangeCalls++; return inner.Range(min, max); }
            public long Total => ValueCalls + ChanceCalls + RangeCalls;
        }

        private static void RandomnessOnlyThroughInjectedRng(Report r)
        {
            CountingRng a = new CountingRng(new XorShiftRng(77));
            CombatSimulator.Simulate(Scenarios.Manhunters(), a);

            CountingRng b = new CountingRng(new XorShiftRng(77));
            CombatSimulator.Simulate(Scenarios.Manhunters(), b);

            bool consumed = a.Total > 0;
            bool reproducible = a.Total == b.Total && a.ValueCalls == b.ValueCalls
                             && a.ChanceCalls == b.ChanceCalls && a.RangeCalls == b.RangeCalls;

            Check(r, "随机性只经由注入的 IRng（且调用次数可复现）", consumed && reproducible,
                  string.Format(CultureInfo.InvariantCulture,
                      "Value={0} Chance={1} Range={2}", a.ValueCalls, a.ChanceCalls, a.RangeCalls));
        }

        // ── 3. 行为 ─────────────────────────────────────────────────────

        private static double AverageCasualties(CombatScene scene, int rounds, int seedBase)
        {
            double sum = 0;
            for (int i = 0; i < rounds; i++)
                sum += CombatSimulator.Simulate(scene, new XorShiftRng(seedBase + i * 104729)).OurCasualties;
            return sum / rounds;
        }

        private static void BetterArmorReducesCasualties(Report r)
        {
            double[] avg = new double[4];
            float[] armors = { 0.00f, 0.20f, 0.40f, 0.60f };

            for (int k = 0; k < armors.Length; k++)
            {
                CombatScene scene = Scenarios.Manhunters();
                scene.Retreat.Kind = RetreatPolicyKind.Never;   // 让伤害累积，不被撤退截断
                for (int i = 0; i < scene.Units.Count; i++)
                    if (scene.Units[i].IsMine) scene.Units[i].ArmorRating = armors[k];
                avg[k] = AverageCasualties(scene, 400, 5000);
            }

            bool monotone = avg[0] > avg[1] && avg[1] > avg[2] && avg[2] > avg[3];
            Check(r, "护甲单调性：0.00→0.60 我方平均伤亡严格下降", monotone,
                  string.Format(CultureInfo.InvariantCulture, "{0:F3} / {1:F3} / {2:F3} / {3:F3}",
                      avg[0], avg[1], avg[2], avg[3]));
        }

        private static void MoreFirepowerReducesEnemySurvivors(Report r)
        {
            double[] avg = new double[2];
            float[] dmgScale = { 1.0f, 2.0f };

            for (int k = 0; k < 2; k++)
            {
                CombatScene scene = Scenarios.Outpost();
                for (int i = 0; i < scene.Units.Count; i++)
                    if (scene.Units[i].IsMine) scene.Units[i].DamagePerShot *= dmgScale[k];

                double sum = 0;
                int n = 400;
                for (int i = 0; i < n; i++)
                {
                    CombatResult res = CombatSimulator.Simulate(scene, new XorShiftRng(9000 + i * 7919));
                    sum += res.EnemyTotal - res.EnemyDead - res.EnemyDowned;
                }
                avg[k] = sum / n;
            }

            Check(r, "火力单调性：伤害 ×2 ⇒ 敌方残存减少", avg[1] < avg[0],
                  string.Format(CultureInfo.InvariantCulture, "残存 {0:0.00} → {1:0.00}", avg[0], avg[1]));
        }

        private static void EnemyOutputFactorAffectsOutcome(Report r)
        {
            // 回归测试：原型早期 EnemyOutputFactor 只进了预告的数字展示，没进模拟，
            // 导致"偷袭机械族"和"正面打机械族"跑出完全一样的结果。
            double[] avg = new double[2];
            float[] factors = { 1.0f, 0.2f };

            for (int k = 0; k < 2; k++)
            {
                CombatScene scene = Scenarios.Outpost();
                scene.Retreat.Kind = RetreatPolicyKind.Never;
                scene.EnemyOutputFactor = factors[k];
                avg[k] = AverageCasualties(scene, 400, 77000);
            }

            Check(r, "EnemyOutputFactor 真的进了模拟（敌方输出 ×0.2 ⇒ 我方伤亡下降）",
                  avg[1] < avg[0],
                  string.Format(CultureInfo.InvariantCulture, "×1.0 → {0:F3} ；×0.2 → {1:F3}", avg[0], avg[1]));
        }

        // ── 场景参数语义 ────────────────────────────────────────────────

        /// <summary>兵力完全对称的镜像对局（双方同一套单位、同一策略）。</summary>
        private static CombatScene MirrorProbe()
        {
            CombatScene s = new CombatScene
            {
                OurPriority = TargetPriority.Strongest,
                EnemyPriority = TargetPriority.Strongest,   // 完全镜像
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.CasualtyFraction, CasualtyFraction = 0.34f },
            };
            for (int side = 0; side < 2; side++)
            {
                bool mine = side == 0;
                for (int i = 1; i <= 4; i++)
                {
                    s.Add(new CombatUnitSnapshot
                    {
                        Name = (mine ? "我" : "敌") + i,
                        IsMine = mine,
                        MaxHealth = 100f,
                        AccuracyNear = 0.75f, AccuracyFar = 0.45f,
                        Range = 25f, MoveSpeed = 4.5f,
                        HasTerrainAdvantage = true,
                        ShotsPerRound = 3f, DamagePerShot = 12f,
                        ArmorPen = 0.16f, ArmorRating = 0.31f,
                        Category = ArmorCategory.Sharp, ThreatWeight = 0.6f * 3f * 12f,
                    });
                }
            }
            return s;
        }

        /// <summary>
        /// 「掩体通过率」的语义。它是**双方对称**的命中率折损（替代 vanilla 的
        /// <c>ShotReport.PassCoverChance</c>），所以：
        ///   • 一定**显著拉长战斗**；
        ///   • 但"胜负中性"**只在双方兵力结构对称时才成立**。
        ///
        /// ⚠️ 第二条是本轮实测**修正**过的结论。早先版本（无空间模型、另一套场景）
        /// 扫出 54%→60%，我据此写了"几乎不改变胜负"；换成现在的场景后是 88%→69%
        /// —— 19 个百分点。原因是**对称折损 ≠ 对称结果**：命中率减半等于战斗时长翻倍，
        /// 而长战斗对"总耐久 × 持续输出"更强的一方有利。双方单位数/血池/输出结构不同时，
        /// 这个旋钮就会实质改变胜负。详见 §19.16.8。
        /// </summary>
        private static void CoverFactorIsSymmetricAndLengthensFight(Report r)
        {
            float[] scales = { 1.0f, 0.5f };

            // ── ① 拉长战斗（任意场景都成立）──
            double[] rounds = new double[2];
            for (int k = 0; k < 2; k++)
            {
                CombatScene scene = Scenarios.Manhunters();
                for (int i = 0; i < scene.Units.Count; i++)
                {
                    scene.Units[i].AccuracyNear *= scales[k];
                    scene.Units[i].AccuracyFar *= scales[k];
                }
                rounds[k] = Forecast.Run(scene, 300, 20240601, Pure).RoundsP50;
            }
            Check(r, "掩体通过率：对称折损 ⇒ 显著拉长战斗",
                  rounds[1] > rounds[0] * 1.4,
                  string.Format(CultureInfo.InvariantCulture, "回合 P50 {0:F0} → {1:F0}", rounds[0], rounds[1]));

            // ── ② 镜像对局下胜负中性 ──
            double[] mirrorWin = new double[2];
            for (int k = 0; k < 2; k++)
            {
                CombatScene scene = MirrorProbe();
                for (int i = 0; i < scene.Units.Count; i++)
                {
                    scene.Units[i].AccuracyNear *= scales[k];
                    scene.Units[i].AccuracyFar *= scales[k];
                }
                mirrorWin[k] = Forecast.Run(scene, 300, 777, Pure).WinRate;
            }
            Check(r, "掩体通过率：**镜像对局**下胜负中性（对称的兵打对称的仗）",
                  Math.Abs(mirrorWin[1] - mirrorWin[0]) < 0.12,
                  string.Format(CultureInfo.InvariantCulture, "成功率 {0:P0} → {1:P0}", mirrorWin[0], mirrorWin[1]));

            // ── ③ 兵力不对称时它**会**改变结果（把修正后的结论钉住）──
            double[] asymWin = new double[2];
            for (int k = 0; k < 2; k++)
            {
                CombatScene scene = Scenarios.Manhunters();
                for (int i = 0; i < scene.Units.Count; i++)
                {
                    scene.Units[i].AccuracyNear *= scales[k];
                    scene.Units[i].AccuracyFar *= scales[k];
                }
                asymWin[k] = Forecast.Run(scene, 300, 20240601, Pure).WinRate;
            }
            Check(r, "掩体通过率：兵力结构不对称时**会**改变胜负（不是纯时间旋钮）",
                  Math.Abs(asymWin[1] - asymWin[0]) > 0.08,
                  string.Format(CultureInfo.InvariantCulture, "成功率 {0:P0} → {1:P0}", asymWin[0], asymWin[1]));
        }

        // ── 空间模型（§19.20 / §19.21）──────────────────────────────────

        /// <summary>取某一类日志的文本（结构化日志后，测试按类别取而非按字符串位置）。</summary>
        private static List<string> LogOf(CombatResult res, CombatLogKind kind)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < res.Entries.Count; i++)
            {
                CombatLogEntry e = res.Entries[i];
                if (e != null && e.Kind == kind) lines.Add(e.Text);
            }
            return lines;
        }

        /// <summary>某类日志里是否出现过指定片段；返回它的回合号（0 = 开局/结局，-1 = 没找到）。</summary>
        private static int RoundOf(CombatResult res, CombatLogKind kind, string fragment)
        {
            for (int i = 0; i < res.Entries.Count; i++)
            {
                CombatLogEntry e = res.Entries[i];
                if (e == null || e.Kind != kind) continue;
                if (e.Text != null && e.Text.Contains(fragment)) return e.Round;
            }
            return -1;
        }

        /// <summary>从战报里读出"开始交火"发生在第几回合。</summary>
        private static int EngagedRound(CombatResult res)
        {
            return RoundOf(res, CombatLogKind.Setup, "开始交火");
        }

        /// <summary>
        /// 对战探针：双方各 1 人、一击必杀、命中 100%。
        /// 唯一的不对称是**地形优势** —— 有优势方射程满额，无优势方射程 ×NoAdvantageRangeFactor。
        /// 因此"谁先开火"完全由空间机制决定。
        /// </summary>
        private static CombatScene DuelProbe(bool ourTerrainAdvantage)
        {
            CombatScene s = new CombatScene
            {
                StartDistance = 40f,
                NoAdvantageRangeFactor = 0.6f,
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.Never },
                MaxRounds = 30,
            };
            s.Add(new CombatUnitSnapshot
            {
                Name = "我", IsMine = true, MaxHealth = 1000f,
                AccuracyNear = 1f, AccuracyFar = 1f, Range = 25f, MoveSpeed = 4.5f,
                HasTerrainAdvantage = ourTerrainAdvantage,
                ShotsPerRound = 1f, DamagePerShot = 1000f, ArmorPen = 5f,
                ArmorRating = 0f, Category = ArmorCategory.Sharp, ThreatWeight = 1000f,
            });
            s.Add(new CombatUnitSnapshot
            {
                Name = "敌", IsMine = false, MaxHealth = 1000f,
                AccuracyNear = 1f, AccuracyFar = 1f, Range = 25f, MoveSpeed = 4.5f,
                HasTerrainAdvantage = !ourTerrainAdvantage,
                ShotsPerRound = 1f, DamagePerShot = 1000f, ArmorPen = 5f,
                ArmorRating = 0f, Category = ArmorCategory.Sharp, ThreatWeight = 1000f,
            });
            return s;
        }

        /// <summary>
        /// 需求里的"初始距离双方都无法射击"：默认初始距离 40 格 > 常见射程 25，
        /// 所以第一回合双方都开不了火，只在接近。
        /// </summary>
        private static void SpatialOpeningIsOutOfRange(Report r)
        {
            CombatResult res = CombatSimulator.Simulate(Scenarios.Outpost(), new XorShiftRng(4242));
            int engaged = EngagedRound(res);
            bool openingLogged = RoundOf(res, CombatLogKind.Setup, "开局：战线间距 40") >= 0;

            Check(r, "空间模型：开局双方都够不着，第一回合无人开火",
                  openingLogged && engaged >= 2,
                  string.Format(CultureInfo.InvariantCulture, "首次交火在第 {0} 回合", engaged));
        }

        /// <summary>
        /// 地形优势的语义：**有优势方射程满额，无优势方射程被压缩** ⇒
        /// 无优势方必须先顶着火力接近。两个方向都测，避免只验证了一侧。
        /// </summary>
        private static void TerrainAdvantageFiresFirst(Report r)
        {
            CombatResult ours = CombatSimulator.Simulate(DuelProbe(true), new XorShiftRng(7));
            CombatResult theirs = CombatSimulator.Simulate(DuelProbe(false), new XorShiftRng(7));

            // 我方占优：我们先开火 ⇒ 零伤亡解决对手
            bool weWin = ours.Outcome == CombatOutcome.Victory
                      && ours.OurCasualties == 0
                      && ours.EnemyCasualties > 0;

            // 敌方占优：他们先开火 ⇒ 我们被打倒，而对手毫发无伤
            //   （注意结局是 Defeat 而不是 Victory —— 我方只有 1 人，死了就全员退出）
            bool weLose = theirs.OurCasualties > 0
                       && theirs.EnemyCasualties == 0;

            Check(r, "地形优势：占优方先开火（我方占优 ⇒ 零伤亡解决对手）", weWin, ours.Summary());
            Check(r, "地形优势：无优势方顶着火力接近（敌方占优 ⇒ 我方被打倒，对手无伤）", weLose, theirs.Summary());
        }

        /// <summary>
        /// 近战会把交战距离一路拉到 MeleeRange —— 这是真实行为（野兽冲锋），
        /// 也让"远程能在冲锋途中白打几轮"成为可建模的战术。
        /// </summary>
        private static void MeleeDragsDistanceToMeleeRange(Report r)
        {
            CombatScene s = new CombatScene
            {
                StartDistance = 40f,
                Closing = ClosingPolicy.UntilShortestEngaged,
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.Never },
                MaxRounds = 60,
            };
            s.Add(new CombatUnitSnapshot
            {
                Name = "枪手", IsMine = true, MaxHealth = 500f,
                AccuracyNear = 0.5f, AccuracyFar = 0.3f, Range = 25f, MoveSpeed = 4.5f,
                HasTerrainAdvantage = true, ShotsPerRound = 1f, DamagePerShot = 5f,
                Category = ArmorCategory.Sharp, ThreatWeight = 3f,
            });
            s.Add(new CombatUnitSnapshot
            {
                Name = "刀手", IsMine = true, MaxHealth = 500f,
                AccuracyNear = 0.6f, AccuracyFar = 0.6f, Range = 25f, MoveSpeed = 5.5f,
                IsMelee = true, HasTerrainAdvantage = true, ShotsPerRound = 1f, DamagePerShot = 5f,
                Category = ArmorCategory.Sharp, ThreatWeight = 3f,
            });
            s.Add(new CombatUnitSnapshot
            {
                Name = "肉盾", IsMine = false, MaxHealth = 100000f,   // 打不死，确保跑到平衡点
                AccuracyNear = 0f, AccuracyFar = 0f, Range = 25f, MoveSpeed = 4f,
                HasTerrainAdvantage = true, ShotsPerRound = 0f, DamagePerShot = 0f,
                Category = ArmorCategory.Sharp, ThreatWeight = 0f,
            });

            CombatResult res = CombatSimulator.Simulate(s, new XorShiftRng(11));
            bool ok = Math.Abs(res.FinalDistance - s.MeleeRange) < 0.01f;

            Check(r, "空间模型：近战把交战距离拉到 MeleeRange",
                  ok, string.Format(CultureInfo.InvariantCulture,
                      "终距 {0:0.##}（期望 {1:0.##}）", res.FinalDistance, s.MeleeRange));
        }

        private static void ClosingPolicyNeverKeepsDistance(Report r)
        {
            CombatScene s = Scenarios.Outpost();
            s.Closing = ClosingPolicy.Never;

            CombatResult res = CombatSimulator.Simulate(s, new XorShiftRng(3));
            bool ok = Math.Abs(res.FinalDistance - s.StartDistance) < 0.01f;

            Check(r, "空间模型：Closing=Never ⇒ 全程停在初始距离（双方都够不着）",
                  ok && res.Rounds >= s.MaxRounds,
                  string.Format(CultureInfo.InvariantCulture,
                      "终距 {0:0.#} · {1} 回合 · {2}", res.FinalDistance, res.Rounds, res.Outcome));
        }

        /// <summary>
        /// 天气是**逐场采样**（不是取期望值），所以：
        ///   • 不同种子会抽到不同天气；
        ///   • 差天气（雾 ×0.5）会显著拉长战斗。
        /// </summary>
        private static void WeatherIsSampledPerBattle(Report r)
        {
            CombatScene mixed = Scenarios.Outpost();
            mixed.WeatherTable.Add(new WeatherSample("Clear", 1.0f, 1f));
            mixed.WeatherTable.Add(new WeatherSample("Fog", 0.5f, 1f));

            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < 60; i++)
                seen.Add(CombatSimulator.Simulate(mixed, new XorShiftRng(i * 7919 + 3)).WeatherName);

            Check(r, "天气：逐场采样（60 个种子下出现 ≥2 种天气）",
                  seen.Contains("Clear") && seen.Contains("Fog"),
                  "观测到：" + string.Join("/", seen));

            CombatScene clear = Scenarios.Outpost();
            clear.WeatherTable.Add(new WeatherSample("Clear", 1.0f, 1f));
            CombatScene fog = Scenarios.Outpost();
            fog.WeatherTable.Add(new WeatherSample("Fog", 0.5f, 1f));

            ForecastResult fClear = Forecast.Run(clear, 200, 4242, Pure);
            ForecastResult fFog = Forecast.Run(fog, 200, 4242, Pure);

            Check(r, "天气：差天气显著拉长战斗（雾 ×0.5 ⇒ 回合数上升）",
                  fFog.RoundsP50 > fClear.RoundsP50,
                  string.Format(CultureInfo.InvariantCulture,
                      "Clear {0:F0} 回合 → Fog {1:F0} 回合", fClear.RoundsP50, fFog.RoundsP50));
        }

        // ── 逐单位距离带（§19.21）────────────────────────────────────────

        /// <summary>
        /// 狙击手（射程 40）+ 霰弹手（射程 12）打一个打不死的靶子。
        /// 霰弹手把战线拉到 12 格，**狙击手应当后撤保住自己的距离** ——
        /// 这是逐单位纵深存在的全部理由。单标量模型下两人都会被拉到 12。
        /// </summary>
        private static CombatScene StandoffProbe(float maxStandoff)
        {
            CombatScene s = new CombatScene
            {
                StartDistance = 40f,
                MaxStandoff = maxStandoff,
                Closing = ClosingPolicy.UntilShortestEngaged,
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.Never },
                MaxRounds = 60,
            };
            s.Add(new CombatUnitSnapshot
            {
                Name = "狙击手", IsMine = true, MaxHealth = 500f,
                AccuracyNear = 0.5f, AccuracyFar = 0.4f, Range = 40f, MoveSpeed = 4.5f,
                HasTerrainAdvantage = true, ShotsPerRound = 1f, DamagePerShot = 5f,
                Category = ArmorCategory.Sharp, ThreatWeight = 3f,
            });
            s.Add(new CombatUnitSnapshot
            {
                Name = "霰弹手", IsMine = true, MaxHealth = 500f,
                AccuracyNear = 0.6f, AccuracyFar = 0.3f, Range = 12f, MoveSpeed = 4.5f,
                HasTerrainAdvantage = true, ShotsPerRound = 1f, DamagePerShot = 5f,
                Category = ArmorCategory.Sharp, ThreatWeight = 3f,
            });
            // 肉盾射程刻意设短（12）：否则"撤不出去"的新判据会拦住狙击手后撤
            // （Outranging 需要 gap + MaxStandoff > 敌方射程），测的就不是后撤本身了。
            s.Add(new CombatUnitSnapshot
            {
                Name = "肉盾", IsMine = false, MaxHealth = 100000f,
                AccuracyNear = 0f, AccuracyFar = 0f, Range = 12f, MoveSpeed = 4f,
                HasTerrainAdvantage = true, ShotsPerRound = 0f, DamagePerShot = 0f,
                Category = ArmorCategory.Sharp, ThreatWeight = 0f,
            });
            return s;
        }

        private static UnitReport Find(CombatResult res, string name)
        {
            for (int i = 0; i < res.Units.Count; i++)
                if (res.Units[i].Name == name) return res.Units[i];
            return null;
        }

        private static void StandoffPreservesLongRangeDistance(Report r)
        {
            CombatResult res = CombatSimulator.Simulate(StandoffProbe(12f), new XorShiftRng(21));
            UnitReport sniper = Find(res, "狙击手");
            UnitReport shotgun = Find(res, "霰弹手");
            if (sniper == null || shotgun == null) { Check(r, "逐单位纵深：取到单位", false, "找不到单位"); return; }

            bool gapOk = Math.Abs(res.FinalDistance - 12f) < 0.6f;         // 战线被霰弹手拉到 12
            bool sniperBack = sniper.FinalStandoff > 8f;                    // 狙击手后撤
            bool shotgunFront = shotgun.FinalStandoff < 1f;                 // 霰弹手在前线

            Check(r, "逐单位纵深：长射程单位后撤保住自己的交战距离",
                  gapOk && sniperBack && shotgunFront,
                  string.Format(CultureInfo.InvariantCulture,
                      "战线 {0:0.#} · 狙击手纵深 {1:0.#} · 霰弹手纵深 {2:0.#}",
                      res.FinalDistance, sniper.FinalStandoff, shotgun.FinalStandoff));
        }

        /// <summary>
        /// 纵深策略：**只有能压制对手射程时才后撤**。
        ///
        /// 这是被扫描推翻过一次的默认值：无条件"拉到最大射程"在本场景里
        /// 会把胜率从 8% 压到 0%（哨所局我方射程 19.4 &lt; 海盗 24，后撤毫无收益，
        /// 只把自己推到命中率最低的位置）。
        /// </summary>
        private static void StandoffPolicyRespectsOutRanging(Report r)
        {
            // ① 被对手压制（哨所局：突击步枪 19.4 < 海盗 24）⇒ 步枪手不后撤
            //    注意狙击手有效射程 30 > 24，它**本来就该后撤** —— 所以只查步枪手。
            CombatScene outranged = Scenarios.Outpost();
            outranged.Standoff = StandoffPolicy.OnlyIfOutranging;
            CombatResult a = CombatSimulator.Simulate(outranged, new XorShiftRng(4242));
            UnitReport rifleA = Find(a, "Chisa");

            CombatScene forced = Scenarios.Outpost();
            forced.Standoff = StandoffPolicy.ToMaxRange;
            CombatResult b = CombatSimulator.Simulate(forced, new XorShiftRng(4242));
            UnitReport rifleB = Find(b, "Chisa");

            Check(r, "纵深策略：被对手压制射程的单位不后撤（OnlyIfOutranging）",
                  rifleA != null && rifleB != null
                  && rifleA.FinalStandoff < 0.01f && rifleB.FinalStandoff > 0.5f,
                  string.Format(CultureInfo.InvariantCulture,
                      "步枪手纵深：OnlyIfOutranging {0:0.#} · ToMaxRange {1:0.#}",
                      rifleA == null ? -1f : rifleA.FinalStandoff,
                      rifleB == null ? -1f : rifleB.FinalStandoff));

            // ② 能压制对手（猎杀人类：我方 19~30 vs 野兽近战 1.5）⇒ 照常后撤
            CombatScene outranging = Scenarios.Manhunters();
            outranging.Standoff = StandoffPolicy.OnlyIfOutranging;
            CombatResult c = CombatSimulator.Simulate(outranging, new XorShiftRng(4242));

            float maxDepthC = 0f;
            for (int i = 0; i < c.Units.Count; i++)
                if (c.Units[i].IsMine && c.Units[i].FinalStandoff > maxDepthC) maxDepthC = c.Units[i].FinalStandoff;

            Check(r, "纵深策略：能压制对手射程时照常后撤（野兽是近战 ⇒ 我方拉开距离）",
                  maxDepthC > 0.5f,
                  string.Format(CultureInfo.InvariantCulture, "我方最大纵深 {0:0.#}", maxDepthC));
        }

        /// <summary>纵深上限设为 0 ⇒ 退化成原来的"单标量距离"模型（所有人都贴在前线）。</summary>
        private static void ZeroStandoffDegeneratesToSingleLine(Report r)
        {
            CombatResult res = CombatSimulator.Simulate(StandoffProbe(0f), new XorShiftRng(21));
            bool allZero = true;
            for (int i = 0; i < res.Units.Count; i++)
                if (Math.Abs(res.Units[i].FinalStandoff) > 0.01f) allZero = false;

            Check(r, "逐单位纵深：MaxStandoff=0 ⇒ 退化为单标量距离（全体纵深为 0）",
                  allZero, allZero ? null : "仍有单位带纵深");
        }

        // ── 战斗日志（§19.22）────────────────────────────────────────────

        /// <summary>日志过滤器必须真的控制写入（而不只是显示时过滤）。</summary>
        private static void LogFilterIsRespected(Report r)
        {
            CombatScene all = Scenarios.Outpost();
            all.LogFilter = CombatLogFilter.All;
            CombatResult resAll = CombatSimulator.Simulate(all, new XorShiftRng(31));

            CombatScene none = Scenarios.Outpost();
            none.LogFilter = CombatLogFilter.None;
            CombatResult resNone = CombatSimulator.Simulate(none, new XorShiftRng(31));

            CombatScene moveOnly = Scenarios.Outpost();
            moveOnly.LogFilter = CombatLogFilter.Move;
            CombatResult resMove = CombatSimulator.Simulate(moveOnly, new XorShiftRng(31));

            bool allHasEvery = resAll.CountOf(CombatLogKind.Setup) > 0
                            && resAll.CountOf(CombatLogKind.Move) > 0
                            && resAll.CountOf(CombatLogKind.Attack) > 0
                            && resAll.CountOf(CombatLogKind.Hit) > 0
                            && resAll.CountOf(CombatLogKind.Status) > 0
                            && resAll.CountOf(CombatLogKind.Outcome) > 0;

            bool noneEmpty = resNone.Entries.Count == 0;

            bool moveOnlyOk = true;
            for (int i = 0; i < resMove.Entries.Count; i++)
                if (resMove.Entries[i].Kind != CombatLogKind.Move) moveOnlyOk = false;

            Check(r, "日志：All 时 6 类都记到（含逐发受击明细）", allHasEvery,
                  string.Format(CultureInfo.InvariantCulture,
                      "开局{0} 移动{1} 攻击{2} 受击{3} 状态{4} 结局{5}",
                      resAll.CountOf(CombatLogKind.Setup), resAll.CountOf(CombatLogKind.Move),
                      resAll.CountOf(CombatLogKind.Attack), resAll.CountOf(CombatLogKind.Hit),
                      resAll.CountOf(CombatLogKind.Status), resAll.CountOf(CombatLogKind.Outcome)));

            Check(r, "日志：None 时一条都不写（预告用它省时间）", noneEmpty,
                  "条数 = " + resNone.Entries.Count);

            Check(r, "日志：只开 Move 时不含其它类别", moveOnlyOk && resMove.Entries.Count > 0,
                  "条数 = " + resMove.Entries.Count);
        }

        /// <summary>攻击与受击两类日志都必须带上"甲弹对抗"信息。</summary>
        private static void LogRecordsArmorInteraction(Report r)
        {
            CombatScene s = Scenarios.Outpost();
            s.LogFilter = CombatLogFilter.All;
            CombatResult res = CombatSimulator.Simulate(s, new XorShiftRng(4242));

            List<string> attacks = LogOf(res, CombatLogKind.Attack);
            List<string> hits = LogOf(res, CombatLogKind.Hit);

            bool attackOk = false;
            for (int i = 0; i < attacks.Count; i++)
                if (attacks[i].Contains("甲弹对抗") && attacks[i].Contains("弹开") && attacks[i].Contains("破甲") == false)
                { attackOk = attacks[i].Contains("命中率") && attacks[i].Contains("合计"); break; }
            if (!attackOk)
                for (int i = 0; i < attacks.Count; i++)
                    if (attacks[i].Contains("甲弹对抗") && attacks[i].Contains("弹开") && attacks[i].Contains("命中率"))
                    { attackOk = true; break; }

            bool hitOk = false;
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].Contains("净") && hits[i].Contains("破甲")
                    && (hits[i].Contains("弹开") || hits[i].Contains("减半") || hits[i].Contains("全额"))
                    && hits[i].Contains("耐久") && hits[i].Contains("→"))
                { hitOk = true; break; }

            Check(r, "日志：攻击行含甲弹对抗汇总（命中率 / 弹开·减半·全额 / 合计伤害）",
                  attackOk, attacks.Count > 0 ? attacks[0] : "无攻击行");
            Check(r, "日志：受击行含逐发甲弹判定（净护甲 / 档位 / 耐久前后）",
                  hitOk, hits.Count > 0 ? hits[0] : "无受击行");
        }

        /// <summary>预告跑蒙特卡洛时会克隆场景关掉日志，**不能污染调用方的场景**。</summary>
        private static void ForecastLeavesCallerSceneUntouched(Report r)
        {
            CombatScene scene = Scenarios.Outpost();
            scene.LogFilter = CombatLogFilter.All;
            string before = SnapshotCodec.Encode(scene);

            Forecast.Run(scene, 50, 5, Pure);

            string after = SnapshotCodec.Encode(scene);
            bool filterKept = scene.LogFilter == CombatLogFilter.All;

            // 而且调用方的场景仍然会记日志
            CombatResult res = CombatSimulator.Simulate(scene, new XorShiftRng(1));

            Check(r, "预告不污染调用方场景（LogFilter 未被改写、场景逐字节不变）",
                  before == after && filterKept,
                  string.Format(CultureInfo.InvariantCulture,
                      "LogFilter 保持 All = {0}；场景编码一致 = {1}", filterKept, before == after));
            Check(r, "预告之后调用方自己跑仍能拿到日志", res.Entries.Count > 0,
                  "条数 = " + res.Entries.Count);
        }

        // ── 索敌 ────────────────────────────────────────────────────────

        /// <summary>
        /// 探针场景：1 个"一击倒一个、必定命中、每回合只开一枪"的我方单位，
        /// 对阵 5 个**完全相同**的靶子（零输出）。
        ///
        /// 于是战报里第一条"敌方 X 倒地"就是这一局第一次索敌的结果 ——
        /// 索敌策略对结果的影响被隔离到只剩"选了谁"。
        /// </summary>
        private static CombatScene TargetingProbe(TargetPriority priority)
        {
            CombatScene s = new CombatScene
            {
                OurPriority = priority,
                EnemyPriority = TargetPriority.Random,
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.Never },
                MaxRounds = 30,
                // 索敌测试要隔离"选谁"，所以直接从近距离开始、不移动
                StartDistance = 5f,
                Closing = ClosingPolicy.Never,
            };

            // 我方：80 伤害打在 100 耐久的靶子上 ⇒ 剩 20（20% ≤ 25% 倒地阈值）⇒ 恰好倒地不死
            s.Add(new CombatUnitSnapshot
            {
                Name = "我", IsMine = true, MaxHealth = 1000f,
                AccuracyNear = 1f, AccuracyFar = 1f, Range = 25f, MoveSpeed = 0f,
                ShotsPerRound = 1f, DamagePerShot = 80f,
                ArmorPen = 5f, ArmorRating = 2f, Category = ArmorCategory.Sharp,
                ThreatWeight = 1000f,
            });

            // 5 个完全相同的靶子：耐久/护甲/威胁权重全部一致 ⇒ 强制同分
            for (int i = 1; i <= 5; i++)
            {
                s.Add(new CombatUnitSnapshot
                {
                    Name = "靶" + i, IsMine = false, MaxHealth = 100f,
                    AccuracyNear = 0f, AccuracyFar = 0f, Range = 25f, MoveSpeed = 0f,
                    ShotsPerRound = 0f, DamagePerShot = 0f,
                    ArmorPen = 0f, ArmorRating = 0f, Category = ArmorCategory.Sharp,
                    ThreatWeight = 0f,
                });
            }
            return s;
        }

        private static string FirstDownedEnemy(CombatResult res)
        {
            List<string> status = LogOf(res, CombatLogKind.Status);
            for (int i = 0; i < status.Count; i++)
            {
                string line = status[i];
                int a = line.IndexOf("敌方 ", StringComparison.Ordinal);
                if (a < 0) continue;
                int b = line.IndexOf("倒地", StringComparison.Ordinal);
                if (b < 0) continue;
                if (b > a) return line.Substring(a + 3, b - a - 3);
            }
            return null;
        }

        private static int DistinctFirstTargets(TargetPriority priority)
        {
            HashSet<string> seen = new HashSet<string>();
            for (int seed = 0; seed < 200; seed++)
            {
                CombatResult res = CombatSimulator.Simulate(TargetingProbe(priority), new XorShiftRng(seed * 104729 + 13));
                string name = FirstDownedEnemy(res);
                if (name != null) seen.Add(name);
            }
            return seen.Count;
        }

        /// <summary>
        /// 索敌"同分随机"回归测试。
        ///
        /// 早期实现用严格 <c>score &gt; bestScore</c> 比较，同分时永远保留名册里的第一个候选：
        ///   • Random 策略（所有单位恒 0 分）退化成"永远打第一个"，根本不是随机；
        ///   • Strongest / Weakest 在分值相同的单位间也永远偏向名册顺序
        ///     （敌人开局全部满血时 Weakest 分值全为 -1，于是永远集火我方第一个单位）。
        ///
        /// 5 个完全相同的靶子 + 200 个种子：正确的实现应当 5 个名字都出现；
        /// 退化的实现只会出现 1 个。
        /// </summary>
        private static void TargetingTieBreakIsRandom(Report r)
        {
            int rnd = DistinctFirstTargets(TargetPriority.Random);
            int strong = DistinctFirstTargets(TargetPriority.Strongest);
            int weak = DistinctFirstTargets(TargetPriority.Weakest);

            Check(r, "索敌同分随机：Random 真的随机（200 种子下 ≥4 个不同首目标）",
                  rnd >= 4, "不同首目标数 = " + rnd);
            Check(r, "索敌同分随机：Strongest 在威胁相同的目标间不偏向名册顺序",
                  strong >= 4, "不同首目标数 = " + strong);
            Check(r, "索敌同分随机：Weakest 在全员满血时不偏向名册顺序",
                  weak >= 4, "不同首目标数 = " + weak);
        }

        private static void RetreatPolicyEndsFightEarlier(Report r)
        {
            // 必须用"注定要输"的编队：只有先有人倒地、且还有人站着，撤退策略才有区别。
            // 势均力敌或必胜局里两种策略结果相同，测不出东西。
            //
            // 用 OnAnyDown 而非 OnAnyDeath：倒地单位会被目标选择跳过（与 vanilla AI 一致），
            // 所以短促战斗只产生倒地、几乎不产生阵亡 —— 这条模型性质本身由
            // ShortFightProducesDownedNotDead 单独断言。
            double never = AvgRounds(Scenarios.LosingFight(RetreatPolicyKind.Never), 400, 31000);
            double onDown = AvgRounds(Scenarios.LosingFight(RetreatPolicyKind.OnAnyDown), 400, 31000);
            double frac = AvgRounds(Scenarios.LosingFight(RetreatPolicyKind.CasualtyFraction), 400, 31000);

            Check(r, "撤退策略生效：任一倒地即撤退 ⇒ 平均回合数少于死战到底",
                  onDown < never,
                  string.Format(CultureInfo.InvariantCulture, "Never={0:F1} OnAnyDown={1:F1}", never, onDown));

            // 三档排序：二元触发器过于怯战，比例式介于两者之间。
            // 这条断言把"默认策略为什么是比例式"钉住（§19.9 / §19.17）。
            Check(r, "撤退激进度排序：任一倒地 < 伤亡比例(34%) ≤ 死战到底",
                  onDown <= frac && frac <= never,
                  string.Format(CultureInfo.InvariantCulture,
                      "OnAnyDown={0:F1} Casualty34%={1:F1} Never={2:F1}", onDown, frac, never));
        }

        private static double AvgRounds(CombatScene scene, int n, int seedBase)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += CombatSimulator.Simulate(scene, new XorShiftRng(seedBase + i * 7919)).Rounds;
            return sum / n;
        }

        /// <summary>
        /// 把"短促战斗先倒地后阵亡"这条模型性质钉成断言。
        ///
        /// 它解释了为什么默认撤退策略不能是 OnAnyDeath：后者在这套模型里
        /// 等价于"永不撤退"（见 RetreatPolicy.Default 的注释与 DESIGN.md §19.17）。
        /// </summary>
        private static void ShortFightProducesDownedNotDead(Report r)
        {
            int withDowned = 0, withDead = 0;
            const int N = 400;
            for (int i = 0; i < N; i++)
            {
                CombatResult res = CombatSimulator.Simulate(
                    Scenarios.LosingFight(RetreatPolicyKind.Never), new XorShiftRng(48000 + i * 7919));
                if (res.OurDowned > 0) withDowned++;
                if (res.OurDead > 0) withDead++;
            }

            bool ok = withDowned > N * 0.5 && withDead == 0;
            Check(r, "模型性质：短促战斗产生倒地而非阵亡（失血致死是小时级尺度）", ok,
                  string.Format(CultureInfo.InvariantCulture, "{0}/{1} 局有倒地，{2}/{1} 局有阵亡",
                      withDowned, N, withDead));
        }

        private static void InvulnerableTargetTimesOut(Report r)
        {
            CombatResult res = CombatSimulator.Simulate(Scenarios.Invulnerable(), new XorShiftRng(1));
            Check(r, "护甲可完全弹开且敌方无输出 ⇒ 打到回合上限（Timeout）",
                  res.Outcome == CombatOutcome.Timeout && res.OurCasualties == 0 && res.EnemyCasualties == 0,
                  res.Summary());
        }

        private static void OverwhelmingForceIsCleanVictory(Report r)
        {
            CombatResult res = CombatSimulator.Simulate(Scenarios.Overwhelming(), new XorShiftRng(2));
            Check(r, "压倒性优势 ⇒ Victory 且零伤亡、威胁解除",
                  res.Outcome == CombatOutcome.Victory && res.OurCasualties == 0 && res.ThreatCleared,
                  res.Summary());
        }

        private static void RetreatWritesDisengageLine(Report r)
        {
            int retreats = 0;
            bool found = false;
            for (int i = 0; i < 300; i++)
            {
                CombatResult res = CombatSimulator.Simulate(
                    Scenarios.LosingFight(RetreatPolicyKind.OnAnyDown), new XorShiftRng(60000 + i));
                if (res.Outcome != CombatOutcome.Retreat) continue;
                retreats++;
                if (RoundOf(res, CombatLogKind.Status, "脱离接触") >= 0) found = true;
            }
            Check(r, "撤退结局包含一轮「脱离接触」并写入战报", found && retreats > 0,
                  string.Format(CultureInfo.InvariantCulture, "300 局中出现 {0} 次撤退", retreats));
        }

        private static void ForecastEnvelopeHolds(Report r)
        {
            CombatScene scene = Scenarios.Outpost();

            // 预告用的种子区
            ForecastResult f = Forecast.Run(scene, 200, 100000, Pure);

            // 结算用的种子区（与预告完全不重叠）
            const int N = 2000;
            int over = 0;
            for (int i = 0; i < N; i++)
            {
                CombatResult res = CombatSimulator.Simulate(scene, new XorShiftRng(900000 + i * 104729));
                if (res.OurCasualties > f.CasualtiesP90) over++;
            }
            float rate = (float)over / N;

            // 名义上 P90 应被超过约 10%；留出采样与"两个种子区不共享"的余量
            Check(r, "预告 P90 包络成立（自洽性检查，非保真度）",
                  rate <= 0.18f,
                  string.Format(CultureInfo.InvariantCulture,
                      "P90={0} 超出比例={1:P1}（名义 10%）", f.CasualtiesP90, rate));
        }

        private static void CodecRoundTrip(Report r)
        {
            CombatScene original = Scenarios.SleepingMechanoids();
            string text = SnapshotCodec.Encode(original);
            CombatScene restored = SnapshotCodec.Decode(text);

            string fpA = CombatSimulator.Simulate(original, new XorShiftRng(123)).Fingerprint();
            string fpB = CombatSimulator.Simulate(restored, new XorShiftRng(123)).Fingerprint();

            Check(r, "快照编解码往返后战斗结果一致（离线复演可用）", fpA == fpB,
                  fpA == fpB ? null : "编解码丢失信息");
        }
    }
}
