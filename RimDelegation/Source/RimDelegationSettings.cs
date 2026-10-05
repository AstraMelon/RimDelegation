using Verse;

namespace RimDelegation
{
    /// <summary>
    /// S32：阵亡守军的**尸体怎么办**（用户 2026-09-28：「尸体不带回家，应该要可以选择，带走或者立刻处理或者丢弃」）。
    ///
    /// 三档互相排斥，因为它们决定的是**同一件事**：那具尸体最后落在哪。
    /// </summary>
    public enum CorpseCleanupMode
    {
        /// <summary>带走：造出真尸体、由「搜集战利品」段按载重装车，回家自己宰（原版屠宰台那一套）。</summary>
        HaulHome = 0,

        /// <summary>立刻处理（默认）：就地按原版口径宰掉，只把肉/皮记账装车 —— 不产生尸体实体。</summary>
        ButcherHere = 1,

        /// <summary>丢弃：什么都不做，阵亡者连同临时的真 pawn 一起就地销毁。</summary>
        Discard = 2,
    }

    /// <summary>
    /// Mod 设置。继承 Verse.ModSettings 后由 Scribe 自动读写到
    /// Config\ModSettings\&lt;模组文件夹名&gt;_&lt;Mod 类名&gt;.xml
    /// </summary>
    public class RimDelegationSettings : ModSettings
    {
        /// <summary>把委派生命周期事件写进日志。</summary>
        public bool verboseLogging = false;

        /// <summary>预先委派抵达目标格时，是否再弹一次确认框（默认关 = 抵达即开工）。</summary>
        public bool requireConfirmOnArrival = false;

        /// <summary>
        /// 开采期间的随机事件（塌方 / 发现富矿脉 / 受挫 / 闹别扭 / 事故）是否开启。
        /// 默认开。关掉后 RollEvents 直接返回，一整天都不会掷骰。
        /// </summary>
        public bool randomEventsEnabled = true;

        /// <summary>
        /// 页签里「现场物资」列表默认是否展开（S8）。
        ///
        /// 为什么放进设置而不是页签的私有字段：原版页签与 RadiusUI 皮肤**共用这一份**，
        /// 否则切皮肤时折叠状态会分叉（同一个玩家看到的界面忽开忽合）。
        /// 另外它是"手动偏好"：空间不足时页签仍会**自动折叠**（见 WITab_Caravan_Delegation）。
        /// </summary>
        public bool stashItemsExpanded = true;

        /// <summary>
        /// 委派结束（收工或中断）时弹一份**签核报告**（S15）。
        ///
        /// 用户拍板：报告窗口要**打断**（`forcePause = true`：弹出即暂停、必须点确认才关），
        /// 而事件本身**零弹出**（不发 Letter、不发 Message）—— 平时靠流程块那一行与底部按钮角标知情。
        /// 这是全局偏好（Mod 设置、不进存档），原版主控台与皮肤主控台的底栏都能勾。
        /// </summary>
        public bool reportOnComplete = false;

        /// <summary>
        /// 随机事件发生时播一声提示音（S15 第三期）。**默认关**。
        ///
        /// 为什么默认关：本次改动的核心是"零弹出、不打断心流"（用户口径）——
        /// 声音同样是一种打断，所以想要的人自己开。
        /// </summary>
        public bool eventSoundEnabled = false;

        /// <summary>
        /// 委派下单是否走**旧版模态对话框**（S18）。**默认关**。
        ///
        /// S18 起四条下达路径（就地 gizmo / 右键预先委派 / 右键就地开工 / 抵达后决定）
        /// 全部落到「委派」主控台的「待下达」草稿里，`Dialog_ChooseDelegation` 因此没有调用点了。
        /// 但它是 S5–S9 一路验收过来的那条路，所以薄壳留着，并配一个开关当**逃生门**：
        /// 万一主控台草稿出了怪事，玩家不必等我们改 dll，勾一下就能回到老流程。
        /// 这也让皮肤侧的「委派对话框」补丁点仍然有存在的意义。
        /// </summary>
        public bool legacyDelegationDialog = false;

        /// <summary>
        /// 主信息三段（作战任务 / 收集任务 / 远行队信息）各自是否折叠（S25）。
        ///
        /// 用户口径：「作战任务，收集任务，远行队的 Title 更加凸显。需要可以折叠」。
        /// 与 <see cref="stashItemsExpanded" /> 同一规矩：**原版与皮肤共用这一份**（本地 Mod 配置、不进存档），
        /// 否则切皮肤时折叠状态会分叉。
        /// </summary>
        public bool collapsedCombatSection;

        public bool collapsedCollectSection;

        public bool collapsedCaravanSection;

        /// <summary>
        /// 流程里的**随机描述（旁白）**是否显示（S26）。**默认关**。
        ///
        /// 用户口径：「暂时关闭一下流程的随机描述，有些不符合逻辑」—— 先关，等他看够了再开。
        /// 判据只有一份：<see cref="DelegationUIUtility.AmbientEnabled" />。
        /// </summary>
        public bool flowAmbientEnabled = false;

        // ── 打扫战场（S31，用户拍板 5A「做成可配档」）─────────────────────────────
        //
        // 三个轴各自一格：缴获装备 / 就地屠宰尸骸 / 收押倒地守军。
        // 默认全开（"打赢就顺手收拾干净"），关掉某一格 ⇒ 那件事**一步都不做**
        // （不是"做了但不显示"）：例如关掉屠宰，阵亡的守军就地销毁，一具都不处理。

        /// <summary>S31：缴获倒下的守军**身上的装备**（枪、甲、背包里的东西）。</summary>
        public bool cleanupTakeEquipment = true;

        /// <summary>
        /// S32：**阵亡守军的尸体怎么办**（默认「立刻处理」= S31 的行为）。
        /// 带走 / 立刻处理 / 丢弃 三选一，见 <see cref="CorpseCleanupMode" />。
        /// </summary>
        public CorpseCleanupMode corpseCleanup = CorpseCleanupMode.ButcherHere;

        /// <summary>S31：收押**倒地**的守军当俘虏（用户拍板 4A：无条件收）。走原版 `Caravan.AddPawn` 自动收押。</summary>
        public bool cleanupCapturePrisoners = true;

        // ── 「委派」主控台窗口的几何（S8）───────────────────────────────────────
        // 窗口可拖拽可缩放，位置与尺寸存在这里，下次打开原样恢复。
        // 抄的是 Radius UI - Quest Menu 的做法：-1 = 还没记录过（用默认位置），
        // 并且用 geomVersion 做一次"几何作废"的迁移开关。
        public float winX = -1f;

        public float winY = -1f;

        public float winW;

        public float winH;

        public int geomVersion;

        /// <summary>
        /// 几何版本。S22 从 1 → 2：主控台要多容纳一栏「流程」（皮肤侧四栏布局），
        /// 默认窗口宽从 1180 提到 1490 —— 老玩家存档里记着旧的 1180，不提版本就**永远看不到流程栏**
        /// （只会一直走"放不下 ⇒ 流程回主列"的回落分支）。所以这一版必须作废旧几何。
        /// </summary>
        public const int CurrentGeomVersion = 2;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref requireConfirmOnArrival, "requireConfirmOnArrival", false);
            Scribe_Values.Look(ref randomEventsEnabled, "randomEventsEnabled", true);
            Scribe_Values.Look(ref stashItemsExpanded, "stashItemsExpanded", true);
            Scribe_Values.Look(ref reportOnComplete, "reportOnComplete", false);
            Scribe_Values.Look(ref eventSoundEnabled, "eventSoundEnabled", false);
            Scribe_Values.Look(ref legacyDelegationDialog, "legacyDelegationDialog", false);
            Scribe_Values.Look(ref collapsedCombatSection, "collapsedCombatSection", false);
            Scribe_Values.Look(ref collapsedCollectSection, "collapsedCollectSection", false);
            Scribe_Values.Look(ref collapsedCaravanSection, "collapsedCaravanSection", false);
            Scribe_Values.Look(ref flowAmbientEnabled, "flowAmbientEnabled", false);
            Scribe_Values.Look(ref cleanupTakeEquipment, "cleanupTakeEquipment", true);
            Scribe_Values.Look(ref corpseCleanup, "corpseCleanup", CorpseCleanupMode.ButcherHere);
            Scribe_Values.Look(ref cleanupCapturePrisoners, "cleanupCapturePrisoners", true);
            Scribe_Values.Look(ref winX, "winX", -1f);
            Scribe_Values.Look(ref winY, "winY", -1f);
            Scribe_Values.Look(ref winW, "winW", 0f);
            Scribe_Values.Look(ref winH, "winH", 0f);
            Scribe_Values.Look(ref geomVersion, "geomVersion", 0);

            // 手改配置文件 / 别的 mod 写坏时会出现 NaN/Infinity，直接 clamp 会把它带进布局，
            // 表现为"窗口彻底消失"。这里先洗一遍。
            winX = Scrub(winX, -1f);
            winY = Scrub(winY, -1f);
            winW = Scrub(winW, 0f);
            winH = Scrub(winH, 0f);
            if (geomVersion < CurrentGeomVersion)
            {
                winX = -1f;
                winY = -1f;
                winW = 0f;
                winH = 0f;
                geomVersion = CurrentGeomVersion;
            }
        }

        private static float Scrub(float v, float fallback)
        {
            if (!float.IsNaN(v) && !float.IsInfinity(v))
            {
                return v;
            }
            return fallback;
        }
    }
}
