using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using RimDelegation.Combat;

namespace RimDelegation.CombatLab
{
    /// <summary>
    /// RimDelegation CombatLab —— 游戏外的战斗计算验证器。
    ///
    ///   RimDelegation.CombatLab.exe                 打开界面
    ///   RimDelegation.CombatLab.exe --smoke         无头自检：跑预设 + 预告 + 一场战斗，打印到 stdout
    ///   RimDelegation.CombatLab.exe --render x.png  把界面离屏渲染成 PNG（用于人工核对布局）
    ///   RimDelegation.CombatLab.exe --selftest      无头跑那 17 项断言
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { /* WinExe 且无控制台时会失败，忽略 */ }

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            switch (mode)
            {
                case "--smoke": return Smoke();
                case "--sweep": return Sweep(args.Length > 1 ? args[1] : "cover",
                                             args.Length > 2 ? args[2] : null);
                case "--selftest": return SelfTestCli();
                case "--render": return Render(args.Length > 1 ? args[1] : "combatlab.png");
                case "--help":
                case "-h":
                    Console.WriteLine("RimDelegation CombatLab");
                    Console.WriteLine("  无参数        打开界面");
                    Console.WriteLine("  --smoke      无头自检（预设 + 预告 + 一场战斗）");
                    Console.WriteLine("  --selftest   无头跑 17 项断言");
                    Console.WriteLine("  --render png 离屏渲染界面");
                    return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            TrySetCjkFont();

            Application.Run(new MainForm());
            return 0;
        }

        private static void TrySetCjkFont()
        {
            try
            {
                using (Font probe = new Font("Microsoft YaHei UI", 9f))
                {
                    if (string.Equals(probe.Name, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase))
                        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9f));
                }
            }
            catch
            {
                // 字体不存在就用系统默认，不值得因此启动失败
            }
        }

        /// <summary>
        /// 参数扫描：固定预设，只动一个场景参数，看它对结果的影响。
        ///
        ///   --sweep cover [预设名]        掩体通过率 1.00 → 0.30
        ///   --sweep enemyfactor [预设名]  敌方输出修正 0.20 → 2.00
        ///
        /// 这个模式是回答"某个字段到底管什么"的标准手段：
        /// 一个字段若真的接进了模拟，扫描它就必须让结果单调变化；
        /// 若扫描结果纹丝不动，那它就是纯装饰（§19.17.3② 就是这么抓出来的）。
        /// </summary>
        private static int Sweep(string field, string presetName)
        {
            LabPresets.Preset preset = null;
            foreach (LabPresets.Preset p in LabPresets.All())
            {
                if (presetName == null || p.Name.StartsWith(presetName)) { preset = p; break; }
            }
            if (preset == null)
            {
                Console.Error.WriteLine("找不到预设：" + presetName);
                return 2;
            }

            float[] values;
            string label;
            switch (field.ToLowerInvariant())
            {
                case "cover":
                    values = new[] { 1.00f, 0.90f, 0.80f, 0.70f, 0.60f, 0.50f, 0.40f, 0.30f };
                    label = "掩体通过率";
                    break;
                case "enemyfactor":
                    values = new[] { 0.20f, 0.50f, 0.80f, 1.00f, 1.25f, 1.50f, 2.00f };
                    label = "敌方输出修正";
                    break;
                case "noadv":
                    values = new[] { 1.00f, 0.90f, 0.80f, 0.75f, 0.70f, 0.60f, 0.50f, 0.40f };
                    label = "无地形优势的射程系数";
                    break;
                case "start":
                    values = new[] { 10f, 15f, 20f, 25f, 30f, 40f, 50f, 60f, 80f };
                    label = "初始交战距离";
                    break;
                case "standoff":
                    values = new[] { 0f, 2f, 5f, 8f, 12f, 16f, 24f, 40f };
                    label = "纵深上限（0 = 退化成单标量距离）";
                    break;
                default:
                    Console.Error.WriteLine("未知扫描字段：" + field + "（可用：cover | enemyfactor | noadv | start | standoff）");
                    return 2;
            }

            Console.WriteLine("参数扫描：" + label + " · 预设「" + preset.Name + "」");
            Console.WriteLine();
            Console.WriteLine("   取值 |   成功率 | 伤员P50 | 阵亡P50 | 回合P50 | 用时(时) | 我方输出/回合");
            Console.WriteLine("  ------+----------+---------+---------+---------+----------+-------------");

            const int iters = 300;
            for (int i = 0; i < values.Length; i++)
            {
                LabSceneParams p = preset.Params.Clone();
                string field_l = field.ToLowerInvariant();
                if (field_l == "cover") p.CoverFactor = values[i];
                else if (field_l == "enemyfactor") p.EnemyOutputFactor = values[i];
                else if (field_l == "noadv") p.NoAdvantageRangeFactor = values[i];
                else if (field_l == "start") p.StartDistance = values[i];
                else if (field_l == "standoff") p.MaxStandoff = values[i];

                string problem;
                CombatScene scene = SceneBuilder.Build(preset.Units, p, out problem);
                if (scene == null) { Console.Error.WriteLine(problem); return 2; }

                ForecastResult f = Forecast.Run(scene, iters, 20240601, seed => new XorShiftRng(seed));

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,5:F2} | {1,7:P0} | {2,7} | {3,7} | {4,7} | {5,8:F2} | {6,12:F1}",
                    values[i], f.WinRate, f.CasualtiesP50, f.DeathsP50, f.RoundsP50,
                    f.DaysP50 * 24f, scene.ExpectedOurDamagePerRound()));
            }
            return 0;
        }

        private static int SelfTestCli()
        {
            Console.OutputEncoding = Encoding.UTF8;
            SelfTest.Report report = SelfTest.RunAll(Console.Out);
            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "通过 {0} / 失败 {1}", report.Passed, report.Failed));
            return report.Failed == 0 ? 0 : 1;
        }

        /// <summary>无头自检：验证"UI 模型 → 场景"这层胶水是对的（不需要窗口）。</summary>
        private static int Smoke()
        {
            Console.OutputEncoding = Encoding.UTF8;
            int failures = 0;

            foreach (LabPresets.Preset preset in LabPresets.All())
            {
                string problem;
                CombatScene scene = SceneBuilder.Build(preset.Units, preset.Params, out problem);
                Console.WriteLine("── " + preset.Name + " ──────────────────────────");
                if (scene == null)
                {
                    Console.WriteLine("  构建失败：" + problem);
                    failures++;
                    continue;
                }

                ForecastResult f = Forecast.Run(scene, 200, 20240601, seed => new XorShiftRng(seed));
                CombatResult one = CombatSimulator.Simulate(scene, new XorShiftRng(1));

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  我方 {0} vs 敌方 {1} · 输出 {2:F1} vs {3:F1} · 成功率 {4:P0} · 伤员 {5}/{6} · 多为 {7}",
                    scene.CountMine(), scene.CountEnemies(),
                    scene.ExpectedOurDamagePerRound(), scene.ExpectedEnemyDamagePerRound(),
                    f.WinRate, f.CasualtiesP50, f.CasualtiesP90, f.ModalOutcome));
                Console.WriteLine("  单场(seed=1)：" + one.Summary());

                // ★ 交叉验证：本 UI 的预设与控制台 Scenarios 里的同名场景必须给出一致的胜率。
                //   两个工程链接的是同一批源文件，所以这是对"共用同一核心"的**进程内**实测，
                //   而不是靠"引用了同一个文件"的推论。
                if (crossChecks.TryGetValue(preset.Name, out string consoleSceneName))
                {
                    CombatScene consoleScene = ScenarioByName(consoleSceneName);
                    if (consoleScene != null)
                    {
                        ForecastResult cf = Forecast.Run(consoleScene, 200, 20240601, seed => new XorShiftRng(seed));
                        float delta = Math.Abs(cf.WinRate - f.WinRate);
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  交叉验证 vs 控制台 {0}：成功率 {1:P0} vs {2:P0}（差 {3:P0}）",
                            consoleSceneName, f.WinRate, cf.WinRate, delta));
                        if (delta > 0.08f)
                        {
                            Console.WriteLine("  [FAIL] 同一预设在两处给出不一致的胜率");
                            failures++;
                        }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "smoke: OK" : ("smoke: " + failures + " 项失败"));
            return failures == 0 ? 0 : 1;
        }

        /// <summary>UI 预设名 → 控制台 <c>Scenarios</c> 里的场景名。</summary>
        private static readonly Dictionary<string, string> crossChecks = new Dictionary<string, string>
        {
            { "猎杀人类（8 野兽冲锋）", "manhunters" },
            { "海盗哨所（我方无掩体）", "outpost" },
            { "休眠机械族（偷袭）", "sleepingmechs" },
            { "炮塔阵地（必败）", "turrets" },
        };

        private static CombatScene ScenarioByName(string name)
        {
            Dictionary<string, Func<CombatScene>> all = Scenarios.All();
            Func<CombatScene> f;
            return all.TryGetValue(name, out f) ? f() : null;
        }

        /// <summary>离屏渲染界面，用于人工核对布局（不需要交互）。</summary>
        private static int Render(string path)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            TrySetCjkFont();

            using (MainForm form = new MainForm())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);   // 屏幕外，避免闪一下
                form.Show();
                Application.DoEvents();

                // 先跑一次预告，让结果区也有内容
                form.RunForecastForRender();
                Application.DoEvents();

                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(path, ImageFormat.Png);
                }

                // 再截一张「战报」页 —— 那是日志过滤 UI 所在，单独渲染才能核对
                form.RunBattleForRender();
                Application.DoEvents();

                string logPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                    Path.GetFileNameWithoutExtension(path) + "-log.png");
                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(logPath, ImageFormat.Png);
                }
                Console.WriteLine("已渲染：" + Path.GetFullPath(logPath));

                form.Hide();
            }

            Console.WriteLine("已渲染：" + Path.GetFullPath(path));

            // 离屏渲染没有跑 Application.Run，WinForms 会留下未退出的窗口消息队列。
            // 先 flush 再硬退出，避免 stdout 缓冲丢失（尤其是被管道捕获时）。
            Console.Out.Flush();
            Environment.Exit(0);
            return 0;
        }
    }
}
