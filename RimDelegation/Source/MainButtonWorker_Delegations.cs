using RimWorld;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 屏幕底部「委派」按钮（S8）。
    ///
    /// 为什么是"新增一个底部按钮"而不是像 Radius UI - Quest Menu 那样去劫持原版按钮：
    /// 我们只是**多**一个入口，不需要替换任何原版东西 —— 那样就还得借壳原版 MainTabWindow
    /// （`RequestedTabSize = (1,1)` + `PostOpen` 里关掉自己）。原版 `MainButtonDef` 是标准 XML Def，
    /// `workerClass` 指过来即可，`InterfaceTryActivate()` 原版实现也只是调 `Activate()`，
    /// **不需要 `tabWindowClass`**（我们仍然给了一个空壳页签兜底，见 MainTabWindow_Delegations）。
    ///
    /// ⚠️ S10 修的一个真 bug（用户报："没有委派的时候，下面的委派 UI 直接不显示了"）：
    /// 原版 `MainButtonWorker.DoButton` 的**首段**就是
    ///     `if (Disabled) { Widgets.DrawAtlas(rect, ButtonSubtleAtlas); ...; return; }`
    /// —— `Disabled` 为真时它只铺一块空底就返回，而**图标与文字都在 return 之后才画**。
    /// 我们的按钮是"有 iconPath 就不画文字"（`label = def.Icon == null ? text : ""`），
    /// 所以 S8 那条"没有在途委派就 Disabled"实际效果是**按钮整个消失**，不是"变灰"。
    /// 修法：`Disabled` 只保留原版语义（没地图 / 正在规划路线 / 正在选点），
    /// "没有在途委派"改为**照常画 + 压暗**（见 <see cref="DoButton" />），
    /// 点开主控台会看到空态提示（`RimDelegationConsoleEmpty`）。
    ///
    /// 三个覆盖点都对着"按钮该怎么表现"：
    ///   · <see cref="Activate"/>：开/切主控台窗口；
    ///   · <see cref="DoButton"/>：没有在途委派时压暗，但**图标必须还在**；
    ///   · <see cref="ButtonBarPercent"/>：按钮自带的那条进度 —— 显示**最拖后腿的那条委派**。
    /// </summary>
    public class MainButtonWorker_Delegations : MainButtonWorker
    {
        /// <summary>没有在途委派时按钮的不透明度（"灰掉"的观感，但仍然看得见、点得动）。</summary>
        private const float IdleAlpha = 0.55f;

        /// <summary>
        /// 只保留原版那三个条件（没地图 / 在规划路线 / 正在选点）。
        ///
        /// 为什么不把"没有在途委派"也算进来：见类注释 —— 原版会因此**不画图标**。
        /// </summary>
        public override bool Disabled => base.Disabled;

        public override float ButtonBarPercent => DelegationRegistry.SlowestProgress();

        /// <summary>
        /// 照原版画（图标 + 进度条 + tooltip 一个不少），只在"没有在途委派"时整体压暗。
        ///
        /// 用 `GUI.color` 压暗而不是自绘：原版 `DoButton` 里的
        /// `Widgets.ButtonTextSubtle` / `GUI.DrawTexture` 全都吃 `GUI.color`，
        /// 所以一个乘算就够，不必复制它的 30 行。
        /// </summary>
        public override void DoButton(Rect rect)
        {
            // S18：有「待下达」的表单时也算"有事要办" —— 那时按钮不该压暗，
            // 否则玩家把主控台关掉之后就再没有"还有张表没填完"的信号了。
            bool idle = DelegationRegistry.ActiveCount() == 0 && RimDelegationDrafts.Count() == 0;
            if (!idle)
            {
                base.DoButton(rect);
                DrawUnseenBadge(rect);
                return;
            }
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, IdleAlpha);
            try
            {
                base.DoButton(rect);
            }
            finally
            {
                GUI.color = old;
            }
            DrawUnseenBadge(rect);
        }

        /// <summary>
        /// 未查看事件的角标（S15）：事件不再发信之后，这是"玩家不打开面板也能知道出了事"的唯一信号。
        /// 用实心方块 + 数字 —— 零美术资源，也不依赖 emoji 字形（字形表里那些都没有）。
        /// </summary>
        private static void DrawUnseenBadge(Rect rect)
        {
            int unseen = DelegationRegistry.UnseenEventCount();
            // S18：有待下达的表单时，左下角给一颗琥珀色小方块。
            // 与右上角那个红角标**语义不同**，所以刻意用两个位置两种颜色：
            //   红 = "有随机事件你还没看"，琥珀 = "有张表在等你填"。
            int drafts = RimDelegationDrafts.Count();
            if (drafts > 0)
            {
                const float draftSize = 12f;
                Rect mark = new Rect(rect.x, rect.yMax - draftSize, draftSize, draftSize);
                Color oldMark = GUI.color;
                GUI.color = Color.white;
                Widgets.DrawBoxSolid(mark, new Color(0.95f, 0.72f, 0.25f, 0.95f));
                GUI.color = oldMark;
                TooltipHandler.TipRegion(mark, string.Format("有 {0} 张「待下达」的委派表单", drafts));
            }
            if (unseen <= 0)
            {
                return;
            }
            const float size = 16f;
            Rect badge = new Rect(rect.xMax - size, rect.y, size, size);
            Widgets.DrawBoxSolid(badge, new Color(0.85f, 0.25f, 0.2f, 0.95f));
            Color old = GUI.color;
            TextAnchor oldAnchor = Text.Anchor;
            GameFont oldFont = Text.Font;
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Tiny;
            Widgets.Label(badge, unseen > 9 ? "9+" : unseen.ToString());
            GUI.color = old;
            Text.Anchor = oldAnchor;
            Text.Font = oldFont;
            TooltipHandler.TipRegion(badge, string.Format(
                "有 {0} 条随机事件还没看过（打开委派主控台即清零）", unseen));
        }

        public override void Activate()
        {
            // S15：打开面板就算"看过了" —— 角标清零
            DelegationRegistry.MarkAllEventsSeen();
            Window_Delegations.Toggle();
        }
    }
}
