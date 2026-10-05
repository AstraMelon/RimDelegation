using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using RimDelegation.Combat;

namespace RimDelegation.CombatLab
{
    /// <summary>
    /// 战斗计算验证器主窗口。
    ///
    /// 左：我方编队 · 右：敌方编队，各自可加模板单位 / 批量加 / 删选中 / 清空，并逐项改数值。
    /// 中：场景参数。下：动作按钮。结果页：预告 / 统计详情 / 战报 / 逐人终局。
    ///
    /// 布局全部显式摆放（TableLayoutPanel + 固定尺寸），不做自动排版魔法，
    /// 便于用 --render 离屏截图核对。
    /// </summary>
    public sealed class MainForm : Form
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>一侧的编辑器控件组（我方、敌方各一套，互不干扰）。</summary>
        private sealed class EditorFields
        {
            public TextBox Name;
            public NumericUpDown Hp, AccNear, AccFar, Range, MoveSpeed, Shots, Dmg, Ap, Armor;
            public CheckBox Melee, TerrainAdvantage;
            public bool Suppress;
        }

        // ── 模型 ────────────────────────────────────────────────────────
        private readonly List<LabUnit> units = new List<LabUnit>();
        private LabSceneParams sceneParams = new LabSceneParams();
        private readonly List<LabUnitTemplate> ourTemplates;
        private readonly List<LabUnitTemplate> enemyTemplates;
        private readonly List<LabPresets.Preset> presets;
        private readonly List<LabWeather.Preset> weatherPresets = LabWeather.All();

        // ── 控件 ────────────────────────────────────────────────────────
        private ComboBox presetCombo;
        private ListView ourList, enemyList, finalList;
        private ComboBox ourTemplateCombo, enemyTemplateCombo;
        private EditorFields ourEditor, enemyEditor;
        private NumericUpDown coverNum, enemyFactorNum, disengageNum, downFracNum,
                              roundsNum, ticksNum, retreatFracNum, seedNum,
                              startDistNum, meleeRangeNum, nearBandNum, noAdvNum,
                              standoffNum, fallbackNum;
        private ComboBox retreatKindCombo, ourPrioCombo, enemyPrioCombo, closingCombo, weatherCombo,
                         standoffPolicyCombo;

        /// <summary>日志过滤器复选框（下标 = <see cref="CombatLogKind"/>）。</summary>
        private CheckBox[] logChecks;
        private Label logCountLabel;
        private TextBox forecastBox, statsBox;
        private ListBox logList;
        private TabControl tabs;
        private TabPage forecastPage, statsPage, logPage, finalPage;
        private ToolStripStatusLabel statusScene, statusMsg;

        /// <summary>最近一次「打一场」的结果 —— 日志过滤复选框靠它做即时重渲染。</summary>
        private CombatResult lastResult;

        public MainForm()
        {
            ourTemplates = LabPresets.OurTemplates();
            enemyTemplates = LabPresets.EnemyTemplates();
            presets = LabPresets.All();

            Text = "RimDelegation · 战斗计算验证器 (CombatLab)";
            ClientSize = new Size(1320, 980);
            MinimumSize = new Size(1100, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 246, 250);

            BuildUi();
            LoadPreset(presets[0]);
        }

        // ════════════════════════════════════════════════════════════════
        //  界面构建
        // ════════════════════════════════════════════════════════════════

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(8, 8, 8, 0),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));    // 预设条
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 46f));     // 双方编队
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156f));   // 场景参数
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));    // 动作按钮
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 54f));     // 结果页
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));    // 状态栏

            root.Controls.Add(BuildPresetBar(), 0, 0);

            TableLayoutPanel sides = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 4, 0, 4),
            };
            sides.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            sides.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            sides.Controls.Add(BuildSide(true), 0, 0);
            sides.Controls.Add(BuildSide(false), 1, 0);
            root.Controls.Add(sides, 0, 1);

            root.Controls.Add(BuildSceneParams(), 0, 2);
            root.Controls.Add(BuildActions(), 0, 3);
            root.Controls.Add(BuildResults(), 0, 4);

            StatusStrip strip = new StatusStrip { SizingGrip = false };
            statusScene = new ToolStripStatusLabel("—") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusMsg = new ToolStripStatusLabel("就绪") { ForeColor = Color.DimGray };
            strip.Items.Add(statusScene);
            strip.Items.Add(statusMsg);
            root.Controls.Add(strip, 0, 5);

            Controls.Add(root);
        }

        private Control BuildPresetBar()
        {
            FlowLayoutPanel p = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
            };

            p.Controls.Add(MakeLabel("预设对局", 60));
            presetCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 250,
                Margin = new Padding(0, 4, 6, 0),
            };
            for (int i = 0; i < presets.Count; i++) presetCombo.Items.Add(presets[i]);
            presetCombo.SelectedIndex = 0;
            p.Controls.Add(presetCombo);

            Button load = new Button { Text = "载入", Width = 66, Height = 26, Margin = new Padding(0, 3, 12, 0) };
            load.Click += delegate { LoadPreset(presetCombo.SelectedItem as LabPresets.Preset); };
            p.Controls.Add(load);

            Button selfTest = new Button { Text = "离线自测（30 项）", Width = 150, Height = 26, Margin = new Padding(0, 3, 12, 0) };
            selfTest.Click += delegate { RunSelfTest(); };
            p.Controls.Add(selfTest);

            p.Controls.Add(MakeLabel("预设与控制台 `dotnet run -- demo` 同参数 —— 两边胜率应当一致。", 520));
            return p;
        }

        private Control BuildSide(bool mine)
        {
            GroupBox g = new GroupBox
            {
                Text = mine ? "我方（远行队）" : "敌方（威胁）",
                Dock = DockStyle.Fill,
                Margin = new Padding(mine ? 0 : 4, 0, mine ? 4 : 0, 0),
                Padding = new Padding(6),
            };

            TableLayoutPanel t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
            };
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 112f));

            // ── 工具条 ──
            FlowLayoutPanel bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0),
            };

            ComboBox combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 160,
                Margin = new Padding(0, 3, 4, 0),
            };
            List<LabUnitTemplate> src = mine ? ourTemplates : enemyTemplates;
            for (int i = 0; i < src.Count; i++) combo.Items.Add(src[i]);
            combo.SelectedIndex = 0;
            if (mine) ourTemplateCombo = combo; else enemyTemplateCombo = combo;
            bar.Controls.Add(combo);

            Button add = new Button { Text = "添加", Width = 54, Height = 26, Margin = new Padding(0, 3, 3, 0) };
            add.Click += delegate { AddFromTemplate(mine, 1); };
            bar.Controls.Add(add);

            Button add5 = new Button { Text = "+5", Width = 42, Height = 26, Margin = new Padding(0, 3, 3, 0) };
            add5.Click += delegate { AddFromTemplate(mine, 5); };
            bar.Controls.Add(add5);

            Button del = new Button { Text = "删除选中", Width = 76, Height = 26, Margin = new Padding(0, 3, 3, 0) };
            del.Click += delegate { RemoveSelected(mine); };
            bar.Controls.Add(del);

            Button clr = new Button { Text = "清空", Width = 54, Height = 26, Margin = new Padding(0, 3, 0, 0) };
            clr.Click += delegate { ClearSide(mine); };
            bar.Controls.Add(clr);
            t.Controls.Add(bar, 0, 0);

            // ── 列表 ──
            ListView lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = true,
                HideSelection = false,
                Font = new Font("Microsoft YaHei UI", 9f),
                Margin = new Padding(0, 2, 0, 2),
            };
            lv.Columns.Add("名称", 88);
            lv.Columns.Add("耐久", 44, HorizontalAlignment.Right);
            lv.Columns.Add("近命中", 48, HorizontalAlignment.Right);
            lv.Columns.Add("远命中", 48, HorizontalAlignment.Right);
            lv.Columns.Add("射程", 42, HorizontalAlignment.Right);
            lv.Columns.Add("移动", 42, HorizontalAlignment.Right);
            lv.Columns.Add("射速", 44, HorizontalAlignment.Right);
            lv.Columns.Add("伤害", 44, HorizontalAlignment.Right);
            lv.Columns.Add("破甲", 42, HorizontalAlignment.Right);
            lv.Columns.Add("护甲", 42, HorizontalAlignment.Right);
            lv.Columns.Add("特性", 72);
            lv.SelectedIndexChanged += delegate { OnListSelectionChanged(mine); };
            if (mine) ourList = lv; else enemyList = lv;
            t.Controls.Add(lv, 0, 1);

            t.Controls.Add(MakeLabel(mine ? "选中单位（改动即时生效）" : "选中单位（改动即时生效）", 320), 0, 2);

            // ── 编辑器（每侧独立一套控件，互不覆盖）──
            EditorFields f = new EditorFields();
            FlowLayoutPanel editor = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                Margin = new Padding(0),
            };

            f.Name = new TextBox { Width = 92, Margin = new Padding(0, 20, 4, 0), Font = new Font("Microsoft YaHei UI", 9f) };
            f.Name.TextChanged += delegate { ApplyEditor(mine, f); };
            editor.Controls.Add(f.Name);

            f.Hp = MakeField(editor, "耐久", 1, 100000, 5, 0);
            f.AccNear = MakeField(editor, "近距命中率", 0, 1, 0.01m, 3);
            f.AccFar = MakeField(editor, "远距命中率", 0, 1, 0.01m, 3);
            f.Range = MakeField(editor, "射程(格)", 0, 200, 1, 1);
            f.MoveSpeed = MakeField(editor, "移动(格/回合)", 0, 50, 0.5m, 1);
            f.Shots = MakeField(editor, "射速/回合", 0, 60, 0.5m, 2);
            f.Dmg = MakeField(editor, "伤害", 0, 500, 1, 1);
            f.Ap = MakeField(editor, "破甲", 0, 5, 0.01m, 3);
            f.Armor = MakeField(editor, "护甲", 0, 5, 0.01m, 3);

            f.Melee = MakeCheck(editor, "近战");
            f.TerrainAdvantage = MakeCheck(editor, "地形优势");

            f.Hp.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.AccNear.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.AccFar.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.Range.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.MoveSpeed.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.Shots.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.Dmg.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.Ap.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.Armor.ValueChanged += delegate { ApplyEditor(mine, f); };
            f.Melee.CheckedChanged += delegate { ApplyEditor(mine, f); };
            f.TerrainAdvantage.CheckedChanged += delegate { ApplyEditor(mine, f); };

            t.Controls.Add(editor, 0, 3);

            if (mine) ourEditor = f; else enemyEditor = f;

            g.Controls.Add(t);
            return g;
        }

        private Control BuildSceneParams()
        {
            GroupBox g = new GroupBox
            {
                Text = "场景参数",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2),
                Padding = new Padding(6),
            };

            FlowLayoutPanel p = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                Margin = new Padding(0),
            };

            // ── 空间（§19.20）──
            startDistNum = MakeField(p, "初始距离(格)", 0, 300, 5, 1);
            meleeRangeNum = MakeField(p, "近战射程(格)", 0, 20, 0.5m, 1);
            nearBandNum = MakeField(p, "近端距离(格)", 0, 100, 1, 1);
            noAdvNum = MakeField(p, "无优势射程系数", 0, 2, 0.05m, 2);
            closingCombo = MakeCombo(p, "移动策略", new object[]
            {
                ClosingPolicy.UntilShortestEngaged, ClosingPolicy.UntilRangedEngaged, ClosingPolicy.Never,
            });
            standoffNum = MakeField(p, "纵深上限(格)", 0, 100, 1, 1);
            fallbackNum = MakeField(p, "后撤速度系数", 0, 2, 0.05m, 2);
            standoffPolicyCombo = MakeWideCombo(p, "纵深策略", new object[]
            {
                StandoffPolicy.OnlyIfOutranging, StandoffPolicy.ToMaxRange, StandoffPolicy.Never,
            }, 190);

            // ── 其余 ──
            coverNum = MakeField(p, "掩体通过率", 0, 1, 0.05m, 2);
            enemyFactorNum = MakeField(p, "敌方输出修正", 0, 5, 0.05m, 2);
            disengageNum = MakeField(p, "脱离接触折扣", 0, 1, 0.05m, 2);
            downFracNum = MakeField(p, "倒地阈值", 0.01m, 1, 0.05m, 2);
            ticksNum = MakeField(p, "每回合ticks", 1, 5000, 50, 0);
            roundsNum = MakeField(p, "回合上限", 1, 5000, 10, 0);
            retreatFracNum = MakeField(p, "撤退伤亡阈值", 0, 1, 0.05m, 2);

            retreatKindCombo = MakeCombo(p, "撤退策略", new object[]
            {
                RetreatPolicyKind.CasualtyFraction, RetreatPolicyKind.OnAnyDown,
                RetreatPolicyKind.OnAnyDeath, RetreatPolicyKind.Never,
            });
            ourPrioCombo = MakeCombo(p, "我方目标", new object[]
            {
                TargetPriority.Strongest, TargetPriority.Weakest, TargetPriority.Random,
            });
            enemyPrioCombo = MakeCombo(p, "敌方目标", new object[]
            {
                TargetPriority.Weakest, TargetPriority.Strongest, TargetPriority.Random,
            });
            weatherCombo = MakeWideCombo(p, "天气", weatherPresets.ToArray(), 230);

            startDistNum.Value = 40m;
            meleeRangeNum.Value = 1.5m;
            nearBandNum.Value = 8m;
            noAdvNum.Value = 0.75m;
            standoffNum.Value = 12m;
            fallbackNum.Value = 0.50m;
            standoffPolicyCombo.SelectedItem = StandoffPolicy.OnlyIfOutranging;
            closingCombo.SelectedItem = ClosingPolicy.UntilShortestEngaged;

            coverNum.Value = 1.00m;
            enemyFactorNum.Value = 1.00m;
            disengageNum.Value = 0.50m;
            downFracNum.Value = 0.25m;
            ticksNum.Value = 250;
            roundsNum.Value = 240;
            retreatFracNum.Value = 0.34m;
            retreatKindCombo.SelectedItem = RetreatPolicyKind.CasualtyFraction;
            ourPrioCombo.SelectedItem = TargetPriority.Strongest;
            enemyPrioCombo.SelectedItem = TargetPriority.Weakest;
            weatherCombo.SelectedIndex = 0;

            EventHandler sync = delegate { PullSceneParams(); };
            startDistNum.ValueChanged += sync;
            meleeRangeNum.ValueChanged += sync;
            nearBandNum.ValueChanged += sync;
            noAdvNum.ValueChanged += sync;
            standoffNum.ValueChanged += sync;
            fallbackNum.ValueChanged += sync;
            standoffPolicyCombo.SelectedIndexChanged += sync;
            closingCombo.SelectedIndexChanged += sync;
            coverNum.ValueChanged += sync;
            enemyFactorNum.ValueChanged += sync;
            disengageNum.ValueChanged += sync;
            downFracNum.ValueChanged += sync;
            ticksNum.ValueChanged += sync;
            roundsNum.ValueChanged += sync;
            retreatFracNum.ValueChanged += sync;
            retreatKindCombo.SelectedIndexChanged += sync;
            ourPrioCombo.SelectedIndexChanged += sync;
            enemyPrioCombo.SelectedIndexChanged += sync;
            weatherCombo.SelectedIndexChanged += sync;

            g.Controls.Add(p);
            return g;
        }

        private Control BuildActions()
        {
            FlowLayoutPanel p = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0, 2, 0, 2),
            };

            Button b1 = new Button { Text = "计算预告（200 次）", Width = 158, Height = 30, Margin = new Padding(0, 2, 6, 0) };
            b1.Click += delegate { RunForecast(200); };
            p.Controls.Add(b1);

            Button b2 = new Button { Text = "跑 1000 场统计", Width = 138, Height = 30, Margin = new Padding(0, 2, 12, 0) };
            b2.Click += delegate { RunStats(1000); };
            p.Controls.Add(b2);

            p.Controls.Add(MakeLabel("随机种子", 60));
            seedNum = new NumericUpDown { Minimum = 0, Maximum = 1000000, Value = 4242, Width = 74, Margin = new Padding(0, 6, 6, 0) };
            p.Controls.Add(seedNum);

            Button b3 = new Button { Text = "打一场", Width = 88, Height = 30, Margin = new Padding(0, 2, 12, 0) };
            b3.Click += delegate { RunBattle(); };
            p.Controls.Add(b3);

            Button b4 = new Button { Text = "导出快照文本", Width = 118, Height = 30, Margin = new Padding(0, 2, 6, 0) };
            b4.Click += delegate { ExportSnapshot(); };
            p.Controls.Add(b4);

            Button b5 = new Button { Text = "导入快照文本…", Width = 128, Height = 30, Margin = new Padding(0, 2, 0, 0) };
            b5.Click += delegate { ImportSnapshot(); };
            p.Controls.Add(b5);

            return p;
        }

        private Control BuildResults()
        {
            tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };

            forecastPage = new TabPage("预告");
            forecastBox = MakeReadonlyText();
            forecastPage.Controls.Add(forecastBox);

            statsPage = new TabPage("统计详情");
            statsBox = MakeReadonlyText();
            statsPage.Controls.Add(statsBox);

            logPage = new TabPage("战报");

            TableLayoutPanel logLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
            };
            logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            FlowLayoutPanel filters = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0),
            };
            filters.Controls.Add(MakeLabel("日志过滤", 62));

            logChecks = new CheckBox[6];
            string[] kindNames = { "开局", "移动", "攻击", "受击", "状态", "结局" };
            CombatLogFilter[] kindBits =
            {
                CombatLogFilter.Setup, CombatLogFilter.Move, CombatLogFilter.Attack,
                CombatLogFilter.Hit, CombatLogFilter.Status, CombatLogFilter.Outcome,
            };
            for (int k = 0; k < 6; k++)
            {
                CheckBox cb = new CheckBox
                {
                    Text = kindNames[k],
                    Width = 62,
                    Checked = (CombatLogFilter.Default & kindBits[k]) != 0,
                    Margin = new Padding(0, 4, 2, 0),
                };
                cb.CheckedChanged += delegate { RefreshLogView(); };
                logChecks[k] = cb;
                filters.Controls.Add(cb);
            }

            Button allBtn = new Button { Text = "全选", Width = 54, Height = 24, Margin = new Padding(8, 3, 3, 0) };
            allBtn.Click += delegate { SetLogChecks(CombatLogFilter.All); };
            filters.Controls.Add(allBtn);

            Button noneBtn = new Button { Text = "全不选", Width = 62, Height = 24, Margin = new Padding(0, 3, 3, 0) };
            noneBtn.Click += delegate { SetLogChecks(CombatLogFilter.None); };
            filters.Controls.Add(noneBtn);

            Button defBtn = new Button { Text = "默认", Width = 54, Height = 24, Margin = new Padding(0, 3, 10, 0) };
            defBtn.Click += delegate { SetLogChecks(CombatLogFilter.Default); };
            filters.Controls.Add(defBtn);

            logCountLabel = MakeLabel("（还没打过）", 460);
            logCountLabel.ForeColor = Color.DimGray;
            filters.Controls.Add(logCountLabel);

            logLayout.Controls.Add(filters, 0, 0);

            logList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Font = new Font("Microsoft YaHei UI", 9f),
                HorizontalScrollbar = true,
            };
            logLayout.Controls.Add(logList, 0, 1);

            logPage.Controls.Add(logLayout);

            finalPage = new TabPage("逐人终局");
            finalList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Microsoft YaHei UI", 9f),
            };
            finalList.Columns.Add("势力", 54);
            finalList.Columns.Add("名称", 110);
            finalList.Columns.Add("状态", 60);
            finalList.Columns.Add("剩余耐久", 90, HorizontalAlignment.Right);
            finalList.Columns.Add("剩余比例", 80, HorizontalAlignment.Right);
            finalList.Columns.Add("终局纵深(格)", 88, HorizontalAlignment.Right);
            finalPage.Controls.Add(finalList);

            tabs.TabPages.Add(forecastPage);
            tabs.TabPages.Add(statsPage);
            tabs.TabPages.Add(logPage);
            tabs.TabPages.Add(finalPage);
            return tabs;
        }

        private static TextBox MakeReadonlyText()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f),
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  小工具
        // ════════════════════════════════════════════════════════════════

        private static Label MakeLabel(string text, int width)
        {
            return new Label
            {
                Text = text,
                Width = width,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 6, 4, 0),
            };
        }

        private static NumericUpDown MakeField(Control parent, string label, decimal min, decimal max,
                                               decimal inc, int decimals)
        {
            Panel host = new Panel { Width = 92, Height = 48, Margin = new Padding(0, 0, 4, 0) };
            host.Controls.Add(new Label
            {
                Text = label,
                Location = new Point(0, 0),
                Size = new Size(90, 16),
                ForeColor = Color.FromArgb(70, 70, 80),
            });
            NumericUpDown nud = new NumericUpDown
            {
                Location = new Point(0, 18),
                Width = 86,
                Minimum = min,
                Maximum = max,
                Increment = inc,
                DecimalPlaces = decimals,
            };
            host.Controls.Add(nud);
            parent.Controls.Add(host);
            return nud;
        }

        private static ComboBox MakeCombo(Control parent, string label, object[] items)
        {
            Panel host = new Panel { Width = 116, Height = 48, Margin = new Padding(0, 0, 4, 0) };
            host.Controls.Add(new Label
            {
                Text = label,
                Location = new Point(0, 0),
                Size = new Size(114, 16),
                ForeColor = Color.FromArgb(70, 70, 80),
            });
            ComboBox cb = new ComboBox
            {
                Location = new Point(0, 18),
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            for (int i = 0; i < items.Length; i++) cb.Items.Add(items[i]);
            host.Controls.Add(cb);
            parent.Controls.Add(host);
            return cb;
        }

        /// <summary>宽度更大的下拉（用于天气这种带长名字的）。</summary>
        private static ComboBox MakeWideCombo(Control parent, string label, object[] items, int width)
        {
            Panel host = new Panel { Width = width + 6, Height = 48, Margin = new Padding(0, 0, 4, 0) };
            host.Controls.Add(new Label
            {
                Text = label,
                Location = new Point(0, 0),
                Size = new Size(width, 16),
                ForeColor = Color.FromArgb(70, 70, 80),
            });
            ComboBox cb = new ComboBox
            {
                Location = new Point(0, 18),
                Width = width,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            for (int i = 0; i < items.Length; i++) cb.Items.Add(items[i]);
            host.Controls.Add(cb);
            parent.Controls.Add(host);
            return cb;
        }

        /// <summary>与数字输入框同一行对齐的复选框。</summary>
        private static CheckBox MakeCheck(Control parent, string label)
        {
            CheckBox cb = new CheckBox
            {
                Text = label,
                Width = 82,
                Height = 24,
                Margin = new Padding(0, 18, 4, 0),
                Checked = true,
            };
            parent.Controls.Add(cb);
            return cb;
        }

        private static string F1(float v) => v.ToString("F1", Inv);
        private static string F2(float v) => v.ToString("F2", Inv);
        private static string P0(float v) => v.ToString("P0", Inv);

        // ── 日志过滤 ────────────────────────────────────────────────────

        /// <summary>从复选框读出当前的日志过滤器。</summary>
        private CombatLogFilter CurrentLogFilter()
        {
            if (logChecks == null) return CombatLogFilter.Default;
            CombatLogFilter[] bits =
            {
                CombatLogFilter.Setup, CombatLogFilter.Move, CombatLogFilter.Attack,
                CombatLogFilter.Hit, CombatLogFilter.Status, CombatLogFilter.Outcome,
            };
            CombatLogFilter f = CombatLogFilter.None;
            for (int k = 0; k < logChecks.Length; k++)
                if (logChecks[k] != null && logChecks[k].Checked) f |= bits[k];
            return f;
        }

        private void SetLogChecks(CombatLogFilter f)
        {
            if (logChecks == null) return;
            CombatLogFilter[] bits =
            {
                CombatLogFilter.Setup, CombatLogFilter.Move, CombatLogFilter.Attack,
                CombatLogFilter.Hit, CombatLogFilter.Status, CombatLogFilter.Outcome,
            };
            for (int k = 0; k < logChecks.Length; k++)
                if (logChecks[k] != null) logChecks[k].Checked = (f & bits[k]) != 0;
        }

        private void UpdateLogCountHint()
        {
            if (logCountLabel == null) return;

            // 没打过就提示过滤器构成；打过之后由 RunBattle 覆盖成实际条数
            if (lastResult == null)
            {
                logCountLabel.Text = "当前过滤：" + CombatLogUtility.Describe(CurrentLogFilter());
                return;
            }

            CombatLogFilter f = CurrentLogFilter();
            int shown = lastResult.LogLines(f).Count;
            int hidden = lastResult.Entries.Count - shown;
            logCountLabel.Text = string.Format(Inv,
                "共 {0} 条（攻击 {1} / 受击 {2} / 移动 {3} / 状态 {4}）· 当前显示 {5} 条，过滤掉 {6} 条",
                lastResult.Entries.Count,
                lastResult.CountOf(CombatLogKind.Attack), lastResult.CountOf(CombatLogKind.Hit),
                lastResult.CountOf(CombatLogKind.Move), lastResult.CountOf(CombatLogKind.Status),
                shown, hidden);
        }

        /// <summary>重新按当前过滤器刷新战报列表（切换复选框时立刻生效，不用重打）。</summary>
        private void RefreshLogView()
        {
            if (lastResult == null) { UpdateLogCountHint(); return; }

            CombatLogFilter f = CurrentLogFilter();
            List<string> lines = lastResult.LogLines(f);

            logList.BeginUpdate();
            logList.Items.Clear();
            for (int i = 0; i < lines.Count; i++) logList.Items.Add(lines[i]);
            logList.EndUpdate();

            UpdateLogCountHint();
        }

        private void SetStatus(string msg)
        {
            statusMsg.Text = msg;
        }

        // ════════════════════════════════════════════════════════════════
        //  编队操作
        // ════════════════════════════════════════════════════════════════

        private void LoadPreset(LabPresets.Preset preset)
        {
            if (preset == null) return;
            units.Clear();
            for (int i = 0; i < preset.Units.Count; i++) units.Add(preset.Units[i].Clone());
            sceneParams = preset.Params.Clone();
            PushSceneParamsToUi();
            RefreshAll();
            SetStatus("已载入预设：" + preset.Name + " —— " + preset.Note);
        }

        private void AddFromTemplate(bool mine, int count)
        {
            ComboBox combo = mine ? ourTemplateCombo : enemyTemplateCombo;
            LabUnitTemplate t = combo.SelectedItem as LabUnitTemplate;
            if (t == null) return;

            int existing = 0;
            for (int i = 0; i < units.Count; i++) if (units[i].IsMine == mine) existing++;

            for (int i = 0; i < count; i++)
            {
                LabUnit u = LabUnit.FromTemplate(t, mine);
                if (count > 1 || IsDuplicateName(u.Name)) u.Name = t.Name + (existing + i + 1);
                units.Add(u);
            }
            RefreshAll();
            SetStatus(string.Format(Inv, "已加入 {0} × {1}", count, t.Name));
        }

        private bool IsDuplicateName(string name)
        {
            for (int i = 0; i < units.Count; i++) if (units[i].Name == name) return true;
            return false;
        }

        private void RemoveSelected(bool mine)
        {
            ListView lv = mine ? ourList : enemyList;
            List<LabUnit> doomed = new List<LabUnit>();
            for (int i = 0; i < lv.SelectedItems.Count; i++)
            {
                LabUnit u = lv.SelectedItems[i].Tag as LabUnit;
                if (u != null) doomed.Add(u);
            }
            for (int i = 0; i < doomed.Count; i++) units.Remove(doomed[i]);
            RefreshAll();
            SetStatus("已删除 " + doomed.Count + " 个单位");
        }

        private void ClearSide(bool mine)
        {
            for (int i = units.Count - 1; i >= 0; i--) if (units[i].IsMine == mine) units.RemoveAt(i);
            RefreshAll();
            SetStatus(mine ? "已清空我方" : "已清空敌方");
        }

        private void RefreshAll()
        {
            FillList(ourList, true);
            FillList(enemyList, false);
            OnListSelectionChanged(true);
            OnListSelectionChanged(false);
            RefreshSceneStatus();
        }

        private void FillList(ListView lv, bool mine)
        {
            int keep = lv.SelectedIndices.Count > 0 ? lv.SelectedIndices[0] : -1;
            lv.BeginUpdate();
            lv.Items.Clear();
            for (int i = 0; i < units.Count; i++)
            {
                LabUnit u = units[i];
                if (u.IsMine != mine) continue;
                lv.Items.Add(MakeRow(u));
            }
            lv.EndUpdate();
            // 没有任何选中项时自动选第一行 —— 否则编辑器会显示一排 0，看起来像坏了
            if (lv.Items.Count > 0)
            {
                int pick = (keep >= 0 && keep < lv.Items.Count) ? keep : 0;
                lv.Items[pick].Selected = true;
            }
        }

        private ListViewItem MakeRow(LabUnit u)
        {
            ListViewItem it = new ListViewItem(new[]
            {
                u.Name,
                u.Hp.ToString("F0", Inv),
                u.AccNear.ToString("P0", Inv),
                u.AccFar.ToString("P0", Inv),
                u.Range.ToString("F0", Inv),
                u.MoveSpeed.ToString("F1", Inv),
                u.Shots.ToString("F1", Inv),
                u.Dmg.ToString("F0", Inv),
                u.Ap.ToString("F2", Inv),
                u.Armor.ToString("F2", Inv),
                (u.IsMelee ? "近战 " : "") + (u.HasTerrainAdvantage ? "优势" : ""),
            });
            it.Tag = u;
            return it;
        }

        private void UpdateRow(ListView lv, LabUnit u)
        {
            for (int i = 0; i < lv.Items.Count; i++)
            {
                if (!ReferenceEquals(lv.Items[i].Tag, u)) continue;
                ListViewItem fresh = MakeRow(u);
                for (int c = 0; c < fresh.SubItems.Count && c < lv.Items[i].SubItems.Count; c++)
                    lv.Items[i].SubItems[c].Text = fresh.SubItems[c].Text;
                break;
            }
        }

        private LabUnit SelectedUnit(bool mine)
        {
            ListView lv = mine ? ourList : enemyList;
            if (lv == null || lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Tag as LabUnit;
        }

        private void OnListSelectionChanged(bool mine)
        {
            EditorFields f = mine ? ourEditor : enemyEditor;
            if (f == null) return;
            LabUnit u = SelectedUnit(mine);
            if (u == null) return;

            f.Suppress = true;
            try
            {
                f.Name.Text = u.Name;
                f.Hp.Value = ClampDecimal(u.Hp, f.Hp);
                f.AccNear.Value = ClampDecimal(u.AccNear, f.AccNear);
                f.AccFar.Value = ClampDecimal(u.AccFar, f.AccFar);
                f.Range.Value = ClampDecimal(u.Range, f.Range);
                f.MoveSpeed.Value = ClampDecimal(u.MoveSpeed, f.MoveSpeed);
                f.Shots.Value = ClampDecimal(u.Shots, f.Shots);
                f.Dmg.Value = ClampDecimal(u.Dmg, f.Dmg);
                f.Ap.Value = ClampDecimal(u.Ap, f.Ap);
                f.Armor.Value = ClampDecimal(u.Armor, f.Armor);
                f.Melee.Checked = u.IsMelee;
                f.TerrainAdvantage.Checked = u.HasTerrainAdvantage;
            }
            finally { f.Suppress = false; }
        }

        private static decimal ClampDecimal(float v, NumericUpDown n)
        {
            decimal d = (decimal)v;
            if (d < n.Minimum) d = n.Minimum;
            if (d > n.Maximum) d = n.Maximum;
            return d;
        }

        private void ApplyEditor(bool mine, EditorFields f)
        {
            if (f == null || f.Suppress) return;
            LabUnit u = SelectedUnit(mine);
            if (u == null) return;

            u.Name = f.Name.Text;
            u.Hp = (float)f.Hp.Value;
            u.AccNear = (float)f.AccNear.Value;
            u.AccFar = (float)f.AccFar.Value;
            u.Range = (float)f.Range.Value;
            u.MoveSpeed = (float)f.MoveSpeed.Value;
            u.Shots = (float)f.Shots.Value;
            u.Dmg = (float)f.Dmg.Value;
            u.Ap = (float)f.Ap.Value;
            u.Armor = (float)f.Armor.Value;
            u.IsMelee = f.Melee.Checked;
            u.HasTerrainAdvantage = f.TerrainAdvantage.Checked;
            UpdateRow(mine ? ourList : enemyList, u);
            RefreshSceneStatus();
        }

        private void PullSceneParams()
        {
            sceneParams = new LabSceneParams
            {
                StartDistance = (float)startDistNum.Value,
                MeleeRange = (float)meleeRangeNum.Value,
                NearBand = (float)nearBandNum.Value,
                NoAdvantageRangeFactor = (float)noAdvNum.Value,
                MaxStandoff = (float)standoffNum.Value,
                FallbackFactor = (float)fallbackNum.Value,
                Standoff = standoffPolicyCombo.SelectedItem is StandoffPolicy
                    ? (StandoffPolicy)standoffPolicyCombo.SelectedItem : StandoffPolicy.OnlyIfOutranging,
                Closing = closingCombo.SelectedItem is ClosingPolicy
                    ? (ClosingPolicy)closingCombo.SelectedItem : ClosingPolicy.UntilShortestEngaged,
                CoverFactor = (float)coverNum.Value,
                EnemyOutputFactor = (float)enemyFactorNum.Value,
                DisengageFactor = (float)disengageNum.Value,
                DownHealthFraction = (float)downFracNum.Value,
                TicksPerRound = (int)ticksNum.Value,
                MaxRounds = (int)roundsNum.Value,
                RetreatFraction = (float)retreatFracNum.Value,
                WeatherPreset = Math.Max(0, weatherCombo.SelectedIndex),
                RetreatKind = retreatKindCombo.SelectedItem is RetreatPolicyKind
                    ? (RetreatPolicyKind)retreatKindCombo.SelectedItem : RetreatPolicyKind.CasualtyFraction,
                OurPriority = ourPrioCombo.SelectedItem is TargetPriority
                    ? (TargetPriority)ourPrioCombo.SelectedItem : TargetPriority.Strongest,
                EnemyPriority = enemyPrioCombo.SelectedItem is TargetPriority
                    ? (TargetPriority)enemyPrioCombo.SelectedItem : TargetPriority.Weakest,
            };
            FillList(ourList, true);
            FillList(enemyList, false);
            RefreshSceneStatus();
        }

        private void PushSceneParamsToUi()
        {
            startDistNum.Value = ClampDecimal(sceneParams.StartDistance, startDistNum);
            meleeRangeNum.Value = ClampDecimal(sceneParams.MeleeRange, meleeRangeNum);
            nearBandNum.Value = ClampDecimal(sceneParams.NearBand, nearBandNum);
            noAdvNum.Value = ClampDecimal(sceneParams.NoAdvantageRangeFactor, noAdvNum);
            standoffNum.Value = ClampDecimal(sceneParams.MaxStandoff, standoffNum);
            fallbackNum.Value = ClampDecimal(sceneParams.FallbackFactor, fallbackNum);
            standoffPolicyCombo.SelectedItem = sceneParams.Standoff;
            closingCombo.SelectedItem = sceneParams.Closing;
            coverNum.Value = ClampDecimal(sceneParams.CoverFactor, coverNum);
            enemyFactorNum.Value = ClampDecimal(sceneParams.EnemyOutputFactor, enemyFactorNum);
            disengageNum.Value = ClampDecimal(sceneParams.DisengageFactor, disengageNum);
            downFracNum.Value = ClampDecimal(sceneParams.DownHealthFraction, downFracNum);
            ticksNum.Value = Math.Max(ticksNum.Minimum, Math.Min(ticksNum.Maximum, sceneParams.TicksPerRound));
            roundsNum.Value = Math.Max(roundsNum.Minimum, Math.Min(roundsNum.Maximum, sceneParams.MaxRounds));
            retreatFracNum.Value = ClampDecimal(sceneParams.RetreatFraction, retreatFracNum);
            if (sceneParams.WeatherPreset >= 0 && sceneParams.WeatherPreset < weatherCombo.Items.Count)
                weatherCombo.SelectedIndex = sceneParams.WeatherPreset;
            retreatKindCombo.SelectedItem = sceneParams.RetreatKind;
            ourPrioCombo.SelectedItem = sceneParams.OurPriority;
            enemyPrioCombo.SelectedItem = sceneParams.EnemyPriority;
        }

        private void RefreshSceneStatus()
        {
            int mine = 0, foes = 0;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].IsMine) mine++; else foes++;
            }
            string problem;
            CombatScene scene = SceneBuilder.Build(units, sceneParams, out problem);
            if (scene == null)
            {
                statusScene.Text = string.Format(Inv, "我方 {0} · 敌方 {1} · {2}", mine, foes, problem);
                return;
            }

            float eq = scene.EquilibriumDistance();
            string closing = scene.Closing == ClosingPolicy.Never
                ? "不移动"
                : string.Format(Inv, "收敛到 {0:0.#} 格", eq);

            statusScene.Text = string.Format(Inv,
                "我方 {0}（{1}/回合） · 敌方 {2}（{3}/回合，×{4:F2}） · 距离 {5:0.#}→{6} · 掩体 {7:P0} · 天气期望 ×{8:F2} · 撤退 {9}",
                scene.CountMine(), F1(scene.ExpectedOurDamagePerRound()),
                scene.CountEnemies(), F1(scene.ExpectedEnemyDamagePerRound()),
                scene.EnemyOutputFactor, scene.StartDistance, closing,
                scene.CoverFactor, scene.ExpectedWeatherMultiplier(), scene.Retreat);
        }

        // ════════════════════════════════════════════════════════════════
        //  运行
        // ════════════════════════════════════════════════════════════════

        private CombatScene TryBuildScene()
        {
            string problem;
            CombatScene scene = SceneBuilder.Build(units, sceneParams, out problem);
            if (scene == null) SetStatus("无法运行：" + problem);
            return scene;
        }

        private static RngFactory Pure => seed => new XorShiftRng(seed);

        /// <summary>供 --render 调用，让离屏截图里也有结果内容。</summary>
        public void RunForecastForRender()
        {
            RunForecast(200);
        }

        /// <summary>供 --render 调用：打一场并切到「战报」页，截日志过滤 UI。</summary>
        public void RunBattleForRender()
        {
            SetLogChecks(CombatLogFilter.All);
            RunBattle();
        }

        private void RunForecast(int iterations)
        {
            CombatScene scene = TryBuildScene();
            if (scene == null) return;

            try
            {
                ForecastResult f = Forecast.Run(scene, iterations, 20240601, Pure);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("【场景】");
                sb.AppendLine(string.Format(Inv, "  我方 {0} 人 · 期望输出 {1} /回合",
                    scene.CountMine(), F1(scene.ExpectedOurDamagePerRound())));
                sb.AppendLine(string.Format(Inv, "  敌方 {0} 个 · 期望输出 {1} /回合（已含修正 ×{2}）",
                    scene.CountEnemies(), F1(scene.ExpectedEnemyDamagePerRound()), F2(scene.EnemyOutputFactor)));
                sb.AppendLine(string.Format(Inv, "  掩体通过率 {0} · 每回合 {1} ticks（{2} 秒）· 回合上限 {3}（{4} 天）",
                    P0(sceneParams.CoverFactor), scene.TicksPerRound, F1(scene.TicksPerRound / 60f),
                    scene.MaxRounds, F2(scene.MaxRounds * scene.TicksPerRound / 60000f)));
                sb.AppendLine(string.Format(Inv, "  撤退策略 {0} · 我方目标 {1} · 敌方目标 {2}",
                    scene.Retreat, scene.OurPriority, scene.EnemyPriority));
                sb.AppendLine();
                sb.AppendLine("【预告】");
                sb.AppendLine(string.Format(Inv, "  成功率（威胁解除）  {0}", P0(f.WinRate)));
                sb.AppendLine(string.Format(Inv, "  撤退率 {0}    失败率 {1}", P0(f.RetreatRate), P0(f.DefeatRate)));
                sb.AppendLine(string.Format(Inv, "  预计用时   P50 {0} 小时 / P90 {1} 小时",
                    F2(f.DaysP50 * 24f), F2(f.DaysP90 * 24f)));
                sb.AppendLine(string.Format(Inv, "  伤员       P50 {0} / P90 {1} / 最坏 {2}",
                    f.CasualtiesP50, f.CasualtiesP90, f.CasualtiesMax));
                sb.AppendLine(string.Format(Inv, "  阵亡       P50 {0} / P90 {1} / 最坏 {2}",
                    f.DeathsP50, f.DeathsP90, f.DeathsMax));
                sb.AppendLine(string.Format(Inv, "  回合数     P50 {0} / P90 {1}", f.RoundsP50, f.RoundsP90));
                sb.AppendLine(string.Format(Inv, "  结局多为   {0}", f.ModalOutcome));
                sb.AppendLine();
                sb.AppendLine(string.Format(Inv,
                    "预告 = CombatSimulator.Simulate 跑 {0} 次取分位；结算走的是同一个函数（§19.12）。", iterations));
                sb.AppendLine("P90 是「最坏情况」承诺，不按均值给。");

                forecastBox.Text = sb.ToString();
                tabs.SelectedTab = forecastPage;
                SetStatus(string.Format(Inv, "预告完成（{0} 次）· 成功率 {1}", iterations, P0(f.WinRate)));
            }
            catch (Exception ex)
            {
                SetStatus("预告失败：" + ex.Message);
                forecastBox.Text = ex.ToString();
            }
        }

        private void RunStats(int n)
        {
            CombatScene scene = TryBuildScene();
            if (scene == null) return;

            try
            {
                ForecastResult f = Forecast.Run(scene, 200, 555, Pure);

                Dictionary<CombatOutcome, int> outcomes = new Dictionary<CombatOutcome, int>();
                int maxCas = scene.CountMine() + 1;
                int[] casHist = new int[maxCas + 1];
                int[] deadHist = new int[3];
                long roundSum = 0;
                int roundMin = int.MaxValue, roundMax = 0;
                int exceed = 0, wins = 0;

                for (int i = 0; i < n; i++)
                {
                    CombatResult res = CombatSimulator.Simulate(scene, new XorShiftRng(900000 + i * 104729));

                    int c;
                    outcomes.TryGetValue(res.Outcome, out c);
                    outcomes[res.Outcome] = c + 1;

                    int idx = res.OurCasualties;
                    if (idx < 0) idx = 0;
                    if (idx > maxCas) idx = maxCas;
                    casHist[idx]++;

                    int di = res.OurDead > 2 ? 2 : res.OurDead;
                    deadHist[di]++;

                    roundSum += res.Rounds;
                    if (res.Rounds < roundMin) roundMin = res.Rounds;
                    if (res.Rounds > roundMax) roundMax = res.Rounds;

                    if (res.OurCasualties > f.CasualtiesP90) exceed++;
                    if (res.ThreatCleared) wins++;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Format(Inv, "结算 {0} 场（种子区 900000+，与预告的种子区不重叠）", n));
                sb.AppendLine();
                sb.AppendLine("【结局分布】");
                foreach (KeyValuePair<CombatOutcome, int> kv in outcomes)
                    sb.AppendLine(string.Format(Inv, "  {0,-16} {1,6} 场   {2}", kv.Key, kv.Value, P0((float)kv.Value / n)));
                sb.AppendLine();
                sb.AppendLine("【我方伤员数分布】");
                for (int i = 0; i <= maxCas; i++)
                    if (casHist[i] > 0)
                        sb.AppendLine(string.Format(Inv, "  {0} 人伤/亡 : {1,6} 场   {2}", i, casHist[i], P0((float)casHist[i] / n)));
                sb.AppendLine();
                sb.AppendLine("【我方阵亡数分布】");
                for (int i = 0; i < deadHist.Length; i++)
                    if (deadHist[i] > 0)
                        sb.AppendLine(string.Format(Inv, "  {0}{1} : {2,6} 场   {3}",
                            i, i == 2 ? "+" : "", deadHist[i], P0((float)deadHist[i] / n)));
                sb.AppendLine();
                sb.AppendLine("【用时】");
                sb.AppendLine(string.Format(Inv, "  回合  平均 {0} · 最短 {1} · 最长 {2}",
                    F1((float)roundSum / n), roundMin, roundMax));
                sb.AppendLine(string.Format(Inv, "  天数  平均 {0}",
                    F2(roundSum / (float)n * scene.TicksPerRound / 60000f)));
                sb.AppendLine();
                sb.AppendLine("【预告包络校验】（引擎自洽性，不是保真度）");
                sb.AppendLine(string.Format(Inv, "  预告 P90 伤员 = {0}", f.CasualtiesP90));
                sb.AppendLine(string.Format(Inv, "  实际超出 P90 的比例 = {0}（名义 10%）", P0((float)exceed / n)));
                sb.AppendLine(string.Format(Inv, "  实际胜率 = {0}（预告 {1}）", P0((float)wins / n), P0(f.WinRate)));
                sb.AppendLine();
                sb.AppendLine("  超出比例明显偏离 10% ⇒ 预告与实际不是同一引擎的采样，或方差被低估。");

                statsBox.Text = sb.ToString();
                tabs.SelectedTab = statsPage;
                SetStatus(string.Format(Inv, "统计完成（{0} 场）· 实际胜率 {1} · 超出 P90 比例 {2}",
                    n, P0((float)wins / n), P0((float)exceed / n)));
            }
            catch (Exception ex)
            {
                SetStatus("统计失败：" + ex.Message);
                statsBox.Text = ex.ToString();
            }
        }

        private void RunBattle()
        {
            CombatScene scene = TryBuildScene();
            if (scene == null) return;

            try
            {
                int seed = (int)seedNum.Value;

                // ⚠️ 日志是**写入时**按 LogFilter 过滤的，所以必须把 UI 的选择装到场景上，
                //    否则某些类别根本不会被记录（只改显示是拿不到数据的）。
                scene.LogFilter = CurrentLogFilter();
                CombatResult res = CombatSimulator.Simulate(scene, new XorShiftRng(seed));
                lastResult = res;

                finalList.BeginUpdate();
                finalList.Items.Clear();
                for (int i = 0; i < res.Units.Count; i++)
                {
                    UnitReport u = res.Units[i];
                    ListViewItem it = new ListViewItem(new[]
                    {
                        u.IsMine ? "我方" : "敌方",
                        u.Name,
                        u.Dead ? "阵亡" : (u.Downed ? "倒地" : "存活"),
                        u.HealthEnd.ToString("F1", Inv),
                        u.HealthFractionEnd.ToString("P0", Inv),
                        u.FinalStandoff.ToString("F1", Inv),
                    });
                    it.ForeColor = u.IsMine ? Color.FromArgb(20, 70, 140) : Color.FromArgb(150, 40, 40);
                    finalList.Items.Add(it);
                }
                finalList.EndUpdate();

                RefreshLogView();          // 按当前过滤器填充战报列表
                tabs.SelectedTab = logPage;
                SetStatus(string.Format(Inv, "seed={0} · {1}", seed, res.Summary()));
            }
            catch (Exception ex)
            {
                SetStatus("战斗失败：" + ex.Message);
                logList.Items.Add(ex.ToString());
            }
        }

        private void RunSelfTest()
        {
            try
            {
                StringBuilder capture = new StringBuilder();
                SelfTest.Report report;
                using (StringWriter sw = new StringWriter(capture, Inv))
                {
                    report = SelfTest.RunAll(sw);
                }
                capture.AppendLine();
                capture.AppendLine("──────────────────────────────");
                capture.AppendLine(string.Format(Inv, "通过 {0} / 失败 {1}", report.Passed, report.Failed));

                statsBox.Text = capture.ToString();
                tabs.SelectedTab = statsPage;
                SetStatus(string.Format(Inv, "离线自测：通过 {0} / 失败 {1}", report.Passed, report.Failed));
            }
            catch (Exception ex)
            {
                SetStatus("自测失败：" + ex.Message);
                statsBox.Text = ex.ToString();
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  快照导入 / 导出（与控制台 --dump / --replay 互通）
        // ════════════════════════════════════════════════════════════════

        private void ExportSnapshot()
        {
            CombatScene scene = TryBuildScene();
            if (scene == null) return;
            ShowTextDialog("导出快照（可粘贴进控制台 replay）", SnapshotCodec.Encode(scene), true);
        }

        private void ImportSnapshot()
        {
            string text = ShowTextDialog("导入快照（粘贴控制台 `dotnet run -- dump <场景>` 的输出）", "", true);
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                CombatScene scene = SnapshotCodec.Decode(text);
                units.Clear();
                for (int i = 0; i < scene.Units.Count; i++)
                {
                    CombatUnitSnapshot s = scene.Units[i];
                    units.Add(new LabUnit
                    {
                        Name = s.Name,
                        IsMine = s.IsMine,
                        Hp = s.MaxHealth,
                        AccNear = s.AccuracyNear,
                        AccFar = s.AccuracyFar,
                        Range = s.Range,
                        MoveSpeed = s.MoveSpeed,
                        IsMelee = s.IsMelee,
                        HasTerrainAdvantage = s.HasTerrainAdvantage,
                        Shots = s.ShotsPerRound,
                        Dmg = s.DamagePerShot,
                        Ap = s.ArmorPen,
                        Armor = s.ArmorRating,
                        Cat = s.Category,
                    });
                }

                sceneParams = new LabSceneParams
                {
                    StartDistance = scene.StartDistance,
                    MeleeRange = scene.MeleeRange,
                    NearBand = scene.NearBand,
                    NoAdvantageRangeFactor = scene.NoAdvantageRangeFactor,
                    MaxStandoff = scene.MaxStandoff,
                    FallbackFactor = scene.FallbackFactor,
                    Standoff = scene.Standoff,
                    Closing = scene.Closing,
                    CoverFactor = scene.CoverFactor,
                    EnemyOutputFactor = scene.EnemyOutputFactor,
                    DisengageFactor = scene.DisengageFactor,
                    DownHealthFraction = scene.DownHealthFraction,
                    TicksPerRound = scene.TicksPerRound,
                    MaxRounds = scene.MaxRounds,
                    OurPriority = scene.OurPriority,
                    EnemyPriority = scene.EnemyPriority,
                    RetreatKind = scene.Retreat.Kind,
                    RetreatFraction = scene.Retreat.CasualtyFraction,
                    WeatherPreset = 0,   // 天气表已随快照带过来，UI 侧下标归零（基线）
                };

                PushSceneParamsToUi();
                RefreshAll();
                SetStatus(string.Format(Inv, "已导入快照：我方 {0} · 敌方 {1} · 初始距离 {2:0.#} 格 · 天气项 {3}",
                    scene.CountMine(), scene.CountEnemies(), scene.StartDistance, scene.WeatherTable.Count));
            }
            catch (Exception ex)
            {
                SetStatus("导入失败：" + ex.Message);
                MessageBox.Show(this, ex.Message, "快照解析失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>简易多行文本对话框（导出可复制 / 导入可粘贴）。</summary>
        private string ShowTextDialog(string title, string initial, bool editable)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = title;
                dlg.ClientSize = new Size(860, 470);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.Font = new Font("Microsoft YaHei UI", 9f);

                TextBox box = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    ReadOnly = !editable,
                    Text = initial ?? "",
                    Font = new Font("Consolas", 9.5f),
                };

                Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
                Button ok = new Button { Text = "确定", Width = 90, Height = 28, DialogResult = DialogResult.OK };
                ok.Location = new Point(dlg.ClientSize.Width - 200, 8);
                Button copy = new Button { Text = "复制", Width = 90, Height = 28 };
                copy.Location = new Point(dlg.ClientSize.Width - 100, 8);
                copy.Click += delegate
                {
                    try { Clipboard.SetText(box.Text); SetStatus("已复制到剪贴板"); }
                    catch { SetStatus("剪贴板被占用，复制失败"); }
                };
                Button cancel = new Button { Text = "取消", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
                cancel.Location = new Point(dlg.ClientSize.Width - 300, 8);

                bottom.Controls.Add(ok);
                bottom.Controls.Add(copy);
                bottom.Controls.Add(cancel);

                dlg.Controls.Add(box);
                dlg.Controls.Add(bottom);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                DialogResult r = dlg.ShowDialog(this);
                return r == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
