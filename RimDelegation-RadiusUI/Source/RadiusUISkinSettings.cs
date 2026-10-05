using System.Collections.Generic;
using Verse;

namespace RimDelegationRadiusUI
{
    /// <summary>
    /// 本试验 mod 的全部设置。注意：不写任何存档数据 —— 只进 Mod 配置。
    /// </summary>
    public class RadiusUISkinSettings : ModSettings
    {
        /// <summary>总开关。</summary>
        public bool enabled = true;

        /// <summary>
        /// 底部「委派」按钮打开的**主控台窗口**（Window_Delegations）上皮肤（S8-b）。
        ///
        /// S18 起它是皮肤**唯一**的开关：远行队「委派」页签已退化成引导页（点开就打开本窗口），
        /// 旧版委派对话框退化成了 Mod 设置里的逃生门（按定义就该用 RimDelegation 原版样式，
        /// 免得玩家分不清自己到底在哪个壳里）—— 于是 `skinCaravanTab` / `skinDialog`
        /// 连同 `CaravanTabSkin` / `CaravanTabBridge` / `DelegationDialogSkin` / `DialogBridge`
        /// 一起删掉了。老配置文件里那两个键会被原版 Scribe 忽略，不影响读档。
        /// </summary>
        public bool skinConsole = true;

        /// <summary>皮肤接管/回退时往 Player.log 写详细信息。</summary>
        public bool verbose;

        /// <summary>
        /// 主控台里"置顶"的站点 ID（S8-b）。
        ///
        /// 存在**本地 Mod 配置**里而不是存档里：本 mod 承诺不写存档数据（README），
        /// 置顶只是皮肤层的阅读偏好。代价说清楚：这是全局的，另一个存档里若有同 ID 的站点，
        /// 那条委派也会显示为置顶（纯观感，无副作用）；加载时会清掉已不存在的站点（Prune）。
        /// </summary>
        public List<int> pinnedSites = new List<int>();

        // ── 四个区域的折叠开关（S22 用户要求：「最左边的过滤器，流程，中间的核心信息框，概览这几个显示上
        //    是否可以进行折叠？」）────────────────────────────────────────────────
        //
        // 同样是**本地阅读偏好**，不进存档：折叠的左栏/流程/主列/概览都收成 26px 竖条，
        // 开关统一画在每个区域顶端 22px 的「列头」里（见 DelegationConsoleSkin.Draw）。
        public bool collapsedLeft;

        /// <summary>流程栏折叠 —— 折叠后流程块**回到主列里**（与"宽度不够自动回落"是同一条路径）。</summary>
        public bool collapsedFlow;

        public bool collapsedMain;

        public bool collapsedRail;

        // ── S34：四个区域的「图钉」（用户原话：「上面折叠展开的+-号按钮左边添加一个图钉PIN按钮，
        //    点击后可以固定展开」）────────────────────────────────────────────────
        //
        // 语义只有一条：**钉住的区域固定保持展开** ——
        //   ① 不吃「悬浮焦点」的自动收起（见 `hoverFocus` 与 `DelegationConsoleSkin.UpdateColumnFocus`）；
        //   ② 与 `collapsed*` 撞车时以图钉为准：有效折叠态 = `collapsed* && !pinned*`；
        //   ③ 点那一栏的「－」＝ 收起**并顺带拔钉**（按钮说的话必须算数）；钉住时只剩「－」可点，
        //      所以不存在"钉住了却被收起来"的矛盾状态。
        // 与 `collapsed*` 一样是**本地阅读偏好**（不进存档），按钮画在列头 ± 的左边。
        public bool pinnedLeft;

        public bool pinnedFlow;

        public bool pinnedMain;

        public bool pinnedRail;

        /// <summary>
        /// S29：**悬浮焦点**（用户：「验证是否可以采取焦点的做法，如果鼠标悬浮在流程，则展开流程，折叠左栏；
        /// 如果鼠标悬浮在左栏，则展开左栏」）。
        ///
        /// S34 起语义改成**不对称**的（用户原话：「请修改成这样：鼠标悬浮在流程的时候展开流程，
        /// 然后折叠左栏。鼠标悬浮在左栏的时候展开左栏，但是不折叠流程」）：
        ///   ① 鼠标在**流程栏** ⇒ 展开流程、收起左栏；
        ///   ② 鼠标在**左栏** ⇒ 只展开左栏，**流程栏保持它自己的状态**（不再被收掉）；
        ///      两栏宽度真的挤不下时，流程栏照 S22 的规则回落进主列 —— 那是版面取舍，不是"被折叠"。
        /// 只影响这两栏（主列/概览不参与：它们的折叠是"看大图/操作"的场合）。
        /// 鼠标离开两栏 ⇒ 回到下面这四个 `collapsed*`（用户自己的设置）。
        /// **只在两栏都是"展开"状态时生效** —— 这样"我特意收起左栏"不会被鼠标路过又弹开；
        /// 被图钉钉住的栏不算"被收起"（`collapsed* && !pinned*` 才是生效的折叠态）。
        /// 焦点状态**不写进配置**（每帧算出来的），所以不会因为鼠标移动而反复写文件。
        /// </summary>
        public bool hoverFocus = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref skinConsole, "skinConsole", true);
            Scribe_Values.Look(ref verbose, "verbose", false);
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
        }
    }
}
