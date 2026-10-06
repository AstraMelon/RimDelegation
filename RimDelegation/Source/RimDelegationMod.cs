using Verse;

namespace RimDelegation
{
    /// <summary>
    /// Mod 入口类。游戏在 LoadedModManager.CreateModClasses() 中枚举
    /// typeof(Mod).InstantiableDescendantsAndSelf()，把本程序集里所有非抽象
    /// Verse.Mod 子类自动实例化，因此 About.xml 不需要声明类名；
    /// 但构造函数必须接收 ModContentPack。
    /// </summary>
    public class RimDelegationMod : Mod
    {
        public static RimDelegationMod Instance { get; private set; }

        public static RimDelegationSettings Settings { get; private set; }

        public RimDelegationMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<RimDelegationSettings>();
        }

        public override string SettingsCategory()
        {
            return Content.Name;
        }

        /// <summary>
        /// Mod 设置窗口。
        ///
        /// 开发期提示（2026-09-30 实测）：本方法**不要**打 [Reloadable] —— 它一被热替换就抛
        /// `TypeLoadException: Could not resolve type System.Nullable`1&lt;Verse.TipSignal&gt;`。
        /// 原因是 ILReloaderLib 的 Tools.ResolveType 拿 Cecil 风格名去查类型，遇到**构造泛型**
        /// （`Nullable&lt;T&gt;` / `List&lt;T&gt;` / `Dictionary&lt;K,V&gt;` …）解析不了。
        /// 要把某段逻辑做成可热重载，就把它隔离成"只碰非泛型类型"的小方法并打 [Reloadable]，
        /// 同时加 MethodImplOptions.NoInlining（否则 JIT 会把小方法内联进调用点，替换了也看不出效果）。
        /// 详见《热重载-可行性评估.md》§6。
        /// </summary>
        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("详细日志（委派生命周期 + 主控台皮肤都写进 Player.log）", ref Settings.verboseLogging);
            listing.GapLine();
            listing.CheckboxLabeled("抵达后仍需确认（预先委派抵达时再弹一次选择框）", ref Settings.requireConfirmOnArrival);
            listing.Label("关掉它 = 预先委派到地方直接开工。");
            listing.GapLine();
            listing.CheckboxLabeled("开采期间的随机事件（默认开）", ref Settings.randomEventsEnabled);
            listing.Label("关掉它 = 委派期间不再发生塌方 / 富矿脉 / 受挫 / 闹别扭 / 事故。");
            listing.GapLine();
            listing.CheckboxLabeled("页签里「现场物资」默认展开（默认开）", ref Settings.stashItemsExpanded);
            listing.Label("关掉它 = 委派页签默认收起现场物资列表（原版与 RadiusUI 皮肤共用这一份设置）；空间不足时页签会自动折叠，与这里无关。");
            listing.GapLine();
            // S26：用户口径「暂时关闭一下流程的随机描述，有些不符合逻辑」⇒ 先关；
            // RIM-16：机制根子修完，按 §七-10 拍板 `10A` **恢复默认开**。
            listing.CheckboxLabeled("流程显示随机描述（旁白）（默认开）", ref Settings.flowAmbientEnabled);
            listing.Label("勾上 = 流程块里显示阶段旁白与休息闲聊（RIM-13 起每段按自己配的节奏换，默认 0.5~1.5 小时随机）。关掉时连掷定都不做。");
            listing.GapLine();
            // RIM-12（用户拍板 2A + 12A + 甲）：固定流程段的全局时长倍率。
            if (listing.ButtonTextLabeled("流程固定段时长倍率", Settings.FlowHoursScaleLabel()))
            {
                Settings.CycleFlowHoursScale();
            }
            listing.Label("　只影响**固定流程段**（侦察 / 移动 / 破门 / 交战 / 撤离…），不影响作业速率（那是满意度的事）。\n"
                + "　档位：0.5× / 0.75× / 1× / 1.5× / 2×；1× = 与改动前逐字一致。\n"
                + "　改动**只对之后新开工的委派生效**：已经在路上的那条按开工时冻结的值走到底，所以进度条不会跳变。");
            listing.GapLine();
            // S15：事件不再弹信（只进流程块 + 结束报告）；这个开关决定"结束那一刻要不要打断一下"。
            listing.CheckboxLabeled("委派结束时弹出签核报告（默认关）", ref Settings.reportOnComplete);
            listing.Label("勾上 = 收工 / 中断时弹一份报告窗口（**会暂停游戏**，点「确认」关闭；同时会写进完成或中断的信件）。\n"
                + "不勾 = 事件只出现在流程块那一行、底部「委派」按钮的角标，以及结束时的信件里。");
            listing.GapLine();
            listing.CheckboxLabeled("随机事件提示音（默认关）", ref Settings.eventSoundEnabled);
            listing.Label("勾上 = 有实质影响的事件（停摆 / 损失 / 受伤）发生时播一声轻提示音，报告窗口出现时也响一下。\n"
                + "纯叙事事件（Flavor）永远不响。");
            listing.GapLine();
            // S18：四条下达路径都收进主控台之后，旧对话框只剩这个逃生门。
            listing.CheckboxLabeled("使用旧版委派对话框（默认关）", ref Settings.legacyDelegationDialog);
            listing.Label("不勾（默认）= 就地委派 / 右键地点 / 抵达后决定，全部在「委派」主控台里完成（左栏「待下达」）。\n"
                + "勾上 = 回到 S5–S17 那个一窗到底的模态对话框（万一新流程出问题，这是不用改 dll 的退路）。");
            listing.GapLine();
            // S31（用户拍板 5A）：打扫战场的**范围**做成可配档 —— 三件事各自一格，默认全开。
            listing.GapLine();
            listing.Label("打扫战场（打赢之后顺手做什么，默认全开）：");
            listing.CheckboxLabeled("　缴获装备（倒下的守军身上的枪、甲、背包）", ref Settings.cleanupTakeEquipment);
            // S32：尸体处置三档（用户 2026-09-28：「应该要可以选择，带走或者立刻处理或者丢弃」）
            if (listing.ButtonTextLabeled("　阵亡守军的尸体", CorpseModeLabel(Settings.corpseCleanup)))
            {
                Settings.corpseCleanup = NextCorpseMode(Settings.corpseCleanup);
            }
            listing.Label("　　带走 = 造出真尸体按载重装车，回家自己上屠宰台（原版那一套，会腐烂）；\n"
                + "　　立刻处理 = 就地按原版口径宰掉，只把肉/皮装车（默认，不产生尸体）；\n"
                + "　　丢弃 = 什么都不做，就地销毁。产物走原版屠宰口径，所以人肉照常触发食人与心情规则。");
            listing.CheckboxLabeled("　收押倒地守军（当俘虏带回）", ref Settings.cleanupCapturePrisoners);
            listing.Label("　　走原版收押路径（原阵营会记一笔），他们照常吃补给、也可能死在路上。");
            // RIM-29（拍板 4B）：四条载重路径的「可超载额度」。默认 0 = 与改动前逐字一致。
            if (listing.ButtonTextLabeled("　可超载额度", Settings.OverloadQuotaLabel()))
            {
                Settings.CycleOverloadQuota();
            }
            listing.Label("　　战利品 / 尸骸 / 现场物资 / 采矿产出四条路**统一**先装满车，再按这个额度继续装；\n"
                + "　　到额度上限还装不下的才会丢，并在信件里写明丢了多少。**默认 0 = 绝不超载**（与改动前一致）。\n"
                + "　　⚠️ 原版超重是「完全不能移动」而不是减速：额度给大了，车队回程会走不动。");
            listing.GapLine();
            // RIM-3（2026-10-05）：Radius UI 皮肤已并入本体，这里只留**阅读偏好**。
            // 按用户拍板，**没有**"皮肤总开关 / 切回原版主控台"这两项：主控台只有一种画法，
            // 皮肤代码与原版画法在同一个程序集里（见 Window_Delegations.DelegateToSkin）。
            listing.Label("主控台（Radius UI 皮肤）：");
            listing.CheckboxLabeled("　悬浮焦点（左栏 / 流程）", ref Settings.hoverFocus);
            listing.Label("　　鼠标落在流程栏 ⇒ 展开流程、收起左栏；落在左栏 ⇒ 只展开左栏，**不收起流程**。\n"
                + "　　鼠标离开两栏就回到你自己的折叠设置；被图钉（列头 ＋/－ 左边的 PIN）固定展开的栏不参与。\n"
                + "　　折叠 / 图钉本身在主控台的列头里点，与这里无关（它们是本地偏好，不进存档）。");
            listing.Label("工作时长由【委派模式】定义（默认 6:00–22:00），在选择委派的对话框里切换。");
            listing.GapLine();
            // RIM-5（用户拍板 7A + 8A）：满意度是"心情 + 效率"的唯一来源，四个来源各自开关 + 权重。
            // 数值双落点：曲线参数与默认值在 Def（DelegationSatisfactionDef），这里是玩家实际生效值。
            listing.Label("满意度（RIM-5：心情与效率的唯一来源，模式不再直接给这两样）：");
            listing.CheckboxLabeled("　启用满意度", ref Settings.satisfactionEnabled);
            listing.Label("　　关掉它 = 满意度恒为中性（每天心情 0、作业速率 ×1），四个来源一律不算。");
            if (Settings.satisfactionEnabled)
            {
                DrawSatisfactionRow(listing, "最近一段时间的吃喝（近 3 天每次吃饭）",
                    DelegationSatisfactionSource.Meals);
                DrawSatisfactionRow(listing, "游戏难度（和平 → 高分；冷酷 → 低分）",
                    DelegationSatisfactionSource.Difficulty);
                DrawSatisfactionRow(listing, "远行时间（从远行队「实际开始移动」起算）",
                    DelegationSatisfactionSource.TravelTime);
                DrawSatisfactionRow(listing, "作业强度（轻松 +3 / 常规 0 / 加班 −4 / 全天候 −6）",
                    DelegationSatisfactionSource.WorkIntensity);
                if (listing.ButtonTextLabeled("　效率幅度（作业速率系数）",
                        string.Format("±{0:0.00}", Settings.satisfactionEfficiencyRange)))
                {
                    Settings.CycleEfficiencyRange();
                }
                if (listing.ButtonTextLabeled("　心情幅度（每日记忆）",
                        string.Format("×{0:0.##}", Settings.satisfactionMoodScale)))
                {
                    Settings.CycleMoodScale();
                }
                listing.Label(string.Format(
                    "　　公式：满意度 = Σ(开关 × 权重 × 子分) ÷ Σ(开关 × 权重)（0.5 = 中性）；"
                    + "作业速率 = 1 + (满意度 − 0.5) × 2 × {0:0.00}；每日心情 = 档位值 × {1:0.##}。\n"
                    + "　　子分：吃喝 = 近 3 天吃饭心情均值；难度 = 当前 {2:0.00}（原版 colonistMoodOffset）；"
                    + "远行 = 出门 1 天内中性、之后 9 天线性降到 0；强度 = 0.5 + 模式强度 ÷ 12。\n"
                    + "　　权重档位只有 0 / 0.5 / 1 / 1.5 / 2（本项目不用滑条）：想要别的浮点数，"
                    + "直接改 Config\\ModSettings 里的同一批键即可。",
                    Settings.satisfactionEfficiencyRange, Settings.satisfactionMoodScale,
                    DelegationSatisfaction.DifficultyValue()));
                if (listing.ButtonTextLabeled("　恢复满意度默认值", "从 Defs 重新取值"))
                {
                    Settings.ResetSatisfactionToDefaults();
                }
            }
            listing.End();
        }

        /// <summary>满意度某一个来源的一行：勾选框（开关）+ 档位按钮（权重）。</summary>
        private static void DrawSatisfactionRow(Listing_Standard listing, string label,
            DelegationSatisfactionSource source)
        {
            RimDelegationSettings s = Settings;
            float weight = s.SourceWeight(source);
            bool on = s.SourceEnabled(source);
            // ⚠️ `Listing_Standard.CheckboxLabeled` 是 **void**（不是"点了没有"的 bool），
            //    所以写法是"传引用、回来对比"。
            listing.CheckboxLabeled(string.Format("　{0}", label), ref on);
            if (on != s.SourceEnabled(source))
            {
                s.SetSourceEnabled(source, on);
            }
            if (listing.ButtonTextLabeled("　　权重", string.Format("{0:0.##}", weight)))
            {
                s.CycleSourceWeight(source);
            }
        }

        /// <summary>S32：尸体处置三档的显示名（玩家的词：带走 / 立刻处理 / 丢弃）。</summary>
        private static string CorpseModeLabel(CorpseCleanupMode mode)
        {
            switch (mode)
            {
                case CorpseCleanupMode.HaulHome: return "带走（回家自行屠宰）";
                case CorpseCleanupMode.Discard: return "丢弃（什么都不做）";
                default: return "立刻处理（就地屠宰，只带肉/皮）";
            }
        }

        private static CorpseCleanupMode NextCorpseMode(CorpseCleanupMode mode)
        {
            switch (mode)
            {
                case CorpseCleanupMode.HaulHome: return CorpseCleanupMode.ButcherHere;
                case CorpseCleanupMode.ButcherHere: return CorpseCleanupMode.Discard;
                default: return CorpseCleanupMode.HaulHome;
            }
        }
    }
}
