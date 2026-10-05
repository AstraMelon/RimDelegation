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
            listing.CheckboxLabeled("详细日志（委派开始/中断/完成都写入 Player.log）", ref Settings.verboseLogging);
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
            // S26：用户口径「暂时关闭一下流程的随机描述，有些不符合逻辑」⇒ 默认关，想看时在这里开
            listing.CheckboxLabeled("流程显示随机描述（旁白）（默认关）", ref Settings.flowAmbientEnabled);
            listing.Label("勾上 = 流程块里显示阶段旁白与休息闲聊（每 0.5 小时换一条）。关掉时连掷定都不做。");
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
            listing.GapLine();
            listing.Label("工作时长由【委派模式】定义（默认 6:00–22:00），在选择委派的对话框里切换。");
            listing.End();
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
