using System.Collections.Generic;
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

        // ── 主控台皮肤（Radius UI）的本地阅读偏好 ────────────────────────────────
        //
        // RIM-3（2026-10-05）：皮肤并入本体 ⇒ 原来的 `RadiusUISkinSettings` 也并进这里。
        // 为什么必须并：合并后只有一个 Mod 类（`RimDelegationMod`），而 Mod 设置页只能有一个，
        // 再开第二个 `ModSettings` 实例就会出现"两块设置、玩家不知道哪块管哪块"。
        // 全部是**本地阅读偏好**，不进存档。
        // ⚠️ 合并后皮肤原本那份配置（`..._RadiusUISkinMod.xml`）不再被读取：键名一致但文件名变了，
        //    玩家需要重新勾一次（纯观感，无副作用）。

        /// <summary>
        /// 主控台里"置顶"的站点 ID（S8-b）。
        ///
        /// 存在**本地 Mod 配置**里而不是存档里：本 mod 承诺不写存档数据，
        /// 置顶只是皮肤层的阅读偏好。代价说清楚：这是全局的，另一个存档里若有同 ID 的站点，
        /// 那条委派也会显示为置顶（纯观感，无副作用）；加载时会清掉已不存在的站点（Prune）。
        /// </summary>
        public List<int> pinnedSites = new List<int>();

        // ── 四个区域的折叠开关（S22 用户要求：「最左边的过滤器，流程，中间的核心信息框，概览这几个显示上
        //    是否可以进行折叠？」）────────────────────────────────────────────────
        //
        // 折叠的左栏/流程/主列/概览都收成 26px 竖条，开关统一画在每个区域顶端 22px 的「列头」里。
        public bool collapsedLeft;

        /// <summary>流程栏折叠 —— 折叠后流程块**回到主列里**（与"宽度不够自动回落"是同一条路径）。</summary>
        public bool collapsedFlow;

        public bool collapsedMain;

        public bool collapsedRail;

        // ── S34：四个区域的「图钉」（用户原话：「上面折叠展开的+-号按钮左边添加一个图钉PIN按钮，
        //    点击后可以固定展开」）────────────────────────────────────────────────
        //
        // 语义只有一条：**钉住的区域固定保持展开** ——
        //   ① 不吃「悬浮焦点」的自动收起；
        //   ② 与 `collapsed*` 撞车时以图钉为准：有效折叠态 = `collapsed* && !pinned*`；
        //   ③ 点那一栏的「－」＝ 收起**并顺带拔钉**（按钮说的话必须算数）。
        public bool pinnedLeft;

        public bool pinnedFlow;

        public bool pinnedMain;

        public bool pinnedRail;

        /// <summary>
        /// S29/S34：**悬浮焦点**（用户原话：「鼠标悬浮在流程，则展开流程，折叠左栏；鼠标悬浮在左栏，则展开左栏」；
        /// S34 改成**不对称**：「鼠标悬浮在左栏的时候展开左栏，但是不折叠流程」）。
        ///
        /// 只影响左栏与流程栏；被图钉钉住的栏不算"被收起"。焦点状态每帧算、**不写配置**。
        /// </summary>
        public bool hoverFocus = true;

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
            // 主控台皮肤（原 RadiusUISkinSettings，RIM-3 并入；键名一字未改）
            Scribe_Collections.Look(ref pinnedSites, "pinnedSites", LookMode.Value);
            Scribe_Values.Look(ref collapsedLeft, "collapsedLeft", false);
            Scribe_Values.Look(ref collapsedFlow, "collapsedFlow", false);
            Scribe_Values.Look(ref collapsedMain, "collapsedMain", false);
            Scribe_Values.Look(ref collapsedRail, "collapsedRail", false);
            Scribe_Values.Look(ref pinnedLeft, "pinnedLeft", false);
            Scribe_Values.Look(ref pinnedFlow, "pinnedFlow", false);
            Scribe_Values.Look(ref pinnedMain, "pinnedMain", false);
            Scribe_Values.Look(ref pinnedRail, "pinnedRail", false);
            Scribe_Values.Look(ref hoverFocus, "hoverFocus", true);
            if (pinnedSites == null)
            {
                pinnedSites = new List<int>();
            }
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
