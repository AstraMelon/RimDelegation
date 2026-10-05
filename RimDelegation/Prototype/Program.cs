using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RimDelegation.Combat;

namespace RimDelegation.Prototype
{
    /// <summary>
    /// RimDelegation 战斗计算原型的独立入口。
    ///
    ///   dotnet run -- selftest            跑全部自测（默认）
    ///   dotnet run -- demo                打印各威胁场景的预告与一场样战斗报
    ///   dotnet run -- dump [场景]         打印样例场景的快照文本（供游戏内抓到的数据复用）
    ///   dotnet run -- battle [场景] [种子] [日志过滤]
    ///   dotnet run -- replay &lt;file&gt; [日志过滤]
    ///
    /// 日志过滤 = 逗号分隔的类别：setup,move,attack,hit,status,outcome
    /// 缺省为 <c>setup,move,attack,status,outcome</c>（不含逐发"受击"明细，那层太细）。
    /// 例如只看移动：<c>battle outpost 4242 move</c>
    ///
    /// 本工程只编译 Combat/ 下的**纯计算**文件，不引用 RimWorld / Unity。
    /// 游戏侧的适配层（真 pawn → 快照）与开发 gizmo 另行实装，见 DESIGN.md §19.17。
    /// </summary>
    public static class Program
    {
        private static CombatLogFilter logFilter = CombatLogFilter.Default;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "selftest";

            switch (mode)
            {
                case "selftest":
                    return RunSelfTest();
                case "demo":
                    logFilter = ParseFilter(args.Length > 1 ? args[1] : null, CombatLogFilter.None);
                    Demo();
                    return 0;
                case "dump":
                    return Dump(args.Length > 1 ? args[1] : "outpost");
                case "battle":
                    logFilter = ParseFilter(args.Length > 3 ? args[3] : null, CombatLogFilter.Default);
                    return Battle(args.Length > 1 ? args[1] : "outpost",
                                  args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 4242);
                case "replay":
                    if (args.Length < 2) { Console.Error.WriteLine("用法：replay <快照文件> [日志过滤]"); return 2; }
                    logFilter = ParseFilter(args.Length > 2 ? args[2] : null, CombatLogFilter.Default);
                    return Replay(args[1]);
                default:
                    Console.Error.WriteLine("未知模式：" + mode);
                    Console.Error.WriteLine("可用：selftest | demo [过滤] | dump [场景] | battle [场景] [种子] [过滤] | replay <file> [过滤]");
                    Console.Error.WriteLine("过滤 = setup,move,attack,hit,status,outcome 的逗号组合；all / none 亦可");
                    return 2;
            }
        }

        /// <summary>解析日志过滤器。null/空 ⇒ fallback；"all"/"none" 为快捷方式。</summary>
        private static CombatLogFilter ParseFilter(string text, CombatLogFilter fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;

            string t = text.Trim().ToLowerInvariant();
            if (t == "all") return CombatLogFilter.All;
            if (t == "none") return CombatLogFilter.None;
            if (t == "default") return CombatLogFilter.Default;

            CombatLogFilter f = CombatLogFilter.None;
            string[] parts = t.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                switch (parts[i])
                {
                    case "setup": f |= CombatLogFilter.Setup; break;
                    case "move": f |= CombatLogFilter.Move; break;
                    case "attack": f |= CombatLogFilter.Attack; break;
                    case "hit": f |= CombatLogFilter.Hit; break;
                    case "status": f |= CombatLogFilter.Status; break;
                    case "outcome": f |= CombatLogFilter.Outcome; break;
                    default:
                        Console.Error.WriteLine("未知日志类别：" + parts[i]);
                        break;
                }
            }
            return f;
        }

        private static void PrintLog(CombatResult res, string indent = "  ")
        {
            List<string> lines = res.LogLines(logFilter);
            for (int i = 0; i < lines.Count; i++) Console.WriteLine(indent + lines[i]);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}（日志过滤：{1}；共 {2} 条，其中攻击 {3} / 受击 {4} / 移动 {5} / 状态 {6}）",
                indent, CombatLogUtility.Describe(logFilter), res.Entries.Count,
                res.CountOf(CombatLogKind.Attack), res.CountOf(CombatLogKind.Hit),
                res.CountOf(CombatLogKind.Move), res.CountOf(CombatLogKind.Status)));
        }

        private static int RunSelfTest()
        {
            Console.WriteLine("RimDelegation 战斗计算原型 —— 自测（离线，无游戏依赖）");
            Console.WriteLine();

            // 环境自检：只诊断，不计入断言。
            // 起因：demo 里 `{1:0.1}` 这类自定义格式串把 0 渲染成了 "01"，
            // 而 `{0:F3}` / `{0:0.000}` 正常。把事实打出来，避免以后重复踩。
            Console.WriteLine("环境自检（仅诊断）：");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  InvariantCulture 小数点 = '{0}' · CurrentCulture = '{1}' · UI = '{2}'",
                CultureInfo.InvariantCulture.NumberFormat.NumberDecimalSeparator,
                CultureInfo.CurrentCulture.Name,
                CultureInfo.CurrentUICulture.Name));
            Console.WriteLine("  \"0.1\" 于 0f     = '" + (0f).ToString("0.1", CultureInfo.InvariantCulture) + "'");
            Console.WriteLine("  \"0.0\" 于 0f     = '" + (0f).ToString("0.0", CultureInfo.InvariantCulture) + "'");
            Console.WriteLine("  \"0.1\" 于 69.85f = '" + (69.85f).ToString("0.1", CultureInfo.InvariantCulture) + "'");
            Console.WriteLine("  \"0.000\" 于 4f   = '" + (4f).ToString("0.000", CultureInfo.InvariantCulture) + "'");
            Console.WriteLine("  \"F1\"  于 69.85f = '" + (69.85f).ToString("F1", CultureInfo.InvariantCulture) + "'");
            Console.WriteLine();

            SelfTest.Report report = SelfTest.RunAll();

            Console.WriteLine();
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "通过 {0} / 失败 {1}", report.Passed, report.Failed));
            if (report.Failed > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败项：");
                foreach (string f in report.Failures) Console.WriteLine("  · " + f);
            }
            Console.WriteLine("──────────────────────────────────────────────");
            return report.Failed == 0 ? 0 : 1;
        }

        private static void Demo()
        {
            Console.WriteLine("RimDelegation 战斗计算原型 —— 场景预告");
            Console.WriteLine();

            foreach (KeyValuePair<string, Func<CombatScene>> kv in Scenarios.All())
            {
                CombatScene scene = kv.Value();
                ForecastResult f = Forecast.Run(scene, 400, 20240601, seed => new XorShiftRng(seed));

                Console.WriteLine("── " + kv.Key + " ──────────────────────────────");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  我方 {0} 人 · 期望输出 {1:F3}/回合 · 敌方 {2} 个 · 期望输出 {3:F3}/回合",
                    scene.CountMine(), scene.ExpectedOurDamagePerRound(),
                    scene.CountEnemies(), scene.ExpectedEnemyDamagePerRound()));
                Console.WriteLine("  " + f.Describe());
                Console.WriteLine();
            }

            Console.WriteLine("── 样战斗报（outpost, seed=4242）──────────────");
            CombatResult res = CombatSimulator.Simulate(Scenarios.Outpost(), new XorShiftRng(4242));
            CombatLogFilter saved = logFilter;
            if (logFilter == CombatLogFilter.None) logFilter = CombatLogFilter.Default;
            PrintLog(res);
            logFilter = saved;
            Console.WriteLine();
            Console.WriteLine("  逐人终局：");
            for (int i = 0; i < res.Units.Count; i++)
            {
                UnitReport u = res.Units[i];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    {0}{1,-10} {2,-4} 耐久 {3,6:F1}/{4,-6:F1} · 纵深 {5,5:F1}",
                    u.IsMine ? "[我] " : "[敌] ", u.Name,
                    u.Dead ? "阵亡" : (u.Downed ? "倒地" : "存活"),
                    u.HealthEnd, u.HealthStart, u.FinalStandoff));
            }
        }

        private static int Dump(string name)
        {
            CombatScene scene = SceneByName(name);
            if (scene == null) return 2;
            Console.Write(SnapshotCodec.Encode(scene));
            return 0;
        }

        /// <summary>单场战斗诊断：跑一次并把战报与逐人终局打全。</summary>
        private static int Battle(string name, int seed)
        {
            CombatScene scene = SceneByName(name);
            if (scene == null) return 2;

            CombatResult res = CombatSimulator.Simulate(scene, new XorShiftRng(seed));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "场景 {0} · seed {1} · 我方 {2} vs 敌方 {3}（敌方输出修正 ×{4:F2}，撤退={5}）",
                name, seed, scene.CountMine(), scene.CountEnemies(),
                scene.EnemyOutputFactor, scene.Retreat));

            PrintLog(res);

            Console.WriteLine();
            Console.WriteLine("  逐人终局：");
            for (int i = 0; i < res.Units.Count; i++)
            {
                UnitReport u = res.Units[i];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    {0}{1,-10} {2,-4} 耐久 {3,6:F1}/{4,-6:F1} · 纵深 {5,5:F1}",
                    u.IsMine ? "[我] " : "[敌] ", u.Name,
                    u.Dead ? "阵亡" : (u.Downed ? "倒地" : "存活"),
                    u.HealthEnd, u.HealthStart, u.FinalStandoff));
            }

            // 每回合我方剩余输出，用来判断"输出是否随减员塌掉"
            Console.WriteLine();
            Console.WriteLine("  我方初始期望输出 " + scene.ExpectedOurDamagePerRound().ToString("F3", CultureInfo.InvariantCulture));
            return 0;
        }

        /// <summary>从场景库里取一个场景，并把当前的日志过滤器装上去。</summary>
        private static CombatScene SceneByName(string name, bool applyLogFilter = true)
        {
            Dictionary<string, Func<CombatScene>> all = Scenarios.All();
            Func<CombatScene> factory;
            if (!all.TryGetValue(name ?? "", out factory))
            {
                Console.Error.WriteLine("未知场景：" + name);
                Console.Error.WriteLine("可用：" + string.Join(" | ", all.Keys));
                return null;
            }
            CombatScene scene = factory();
            // ⚠️ 只改打印过滤器是不够的 —— 日志是**写入时**按 LogFilter 过滤的，
            //    所以必须把过滤意图装到场景上，否则某些类别根本不会被记录。
            if (applyLogFilter) scene.LogFilter = logFilter;
            return scene;
        }

        private static int Replay(string path)
        {
            string text = File.ReadAllText(path);
            CombatScene scene = SnapshotCodec.Decode(text);
            scene.LogFilter = logFilter;   // 日志是写入时过滤的，必须装到场景上

            Console.WriteLine("从快照复演：" + path);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "我方 {0} · 敌方 {1} · 回合上限 {2} · 敌方输出修正 ×{3:F2}",
                scene.CountMine(), scene.CountEnemies(), scene.MaxRounds, scene.EnemyOutputFactor));
            Console.WriteLine();

            ForecastResult f = Forecast.Run(scene, 200, 1, seed => new XorShiftRng(seed));
            Console.WriteLine("预告：" + f.Describe());
            Console.WriteLine();

            CombatResult res = CombatSimulator.Simulate(scene, new XorShiftRng(1));
            PrintLog(res);
            return 0;
        }
    }
}
