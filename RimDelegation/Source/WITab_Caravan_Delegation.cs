using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 远行队世界地图 inspect 面板里的「委派」页签 —— S18 起是**引导页**。
    ///
    /// 为什么要退化：用户 S18 拍板「远行队页签的委派点击直接跳转到综合委派UI」。
    /// 以前这里是一套"最小空间里的缩水版委派面板"（折叠现场物资、截断参与者、查看全部…），
    /// 同一排动作按钮在原版侧与皮肤侧各画了一遍 —— 现在它们只剩主控台那一份。
    ///
    /// ⚠️ 为什么还要 `FillTab` 画一行字：原版 `InspectPaneUtility.ToggleTab` 的顺序是
    ///     `tab.OnOpen()` **然后** `pane.OpenTabType = tab.GetType()` —— 面板一定会展开，
    ///     零 Harmony 拦不住（要拦只能 patch `DoTabs`，那是本项目最不想碰的热路径）。
    ///     所以策略是：`UpdateSize` 把面板压到最矮、`FillTab` 只画一句说明 + 一颗恢复按钮，
    ///     既不挡地图，也不留一大块空白。
    ///
    /// 同时删掉的：本文件原先 ~530 行绘制、皮肤侧 `CaravanTabSkin.cs`(36KB) +
    /// `CaravanTabBridge.cs` + 皮肤的第 2 个补丁点。
    /// </summary>
    public class WITab_Caravan_Delegation : WITab
    {
        private const float Width = 620f;

        /// <summary>引导页的高度：一句说明 + 一颗按钮 + 一句提示。</summary>
        private const float StubHeight = 96f;

        public WITab_Caravan_Delegation()
        {
            labelKey = "RimDelegationTabDelegation";
        }

        /// <summary>
        /// 只有"这支车队正在委派"时才给这个页签 —— 它同时是"这队在干活"的视觉信号，
        /// 所以 S18 没有把它放宽到"所有远行队"。
        /// </summary>
        public override bool IsVisible => DelegationRegistry.For(SelCaravan) != null;

        /// <summary>
        /// 点页签 = 打开主控台并聚焦到这支车队那条委派。
        ///
        /// ⚠️ 这里同时是"页签没法只跳窗"的补偿：面板稍后仍会展开（见类注释），
        /// 但展开的是一个 96px 的引导条，而玩家的真正去处已经是主控台。
        /// </summary>
        public override void OnOpen()
        {
            base.OnOpen();
            WorldObjectComp_Delegations comp = DelegationRegistry.For(SelCaravan);
            if (comp != null)
            {
                Window_Delegations.EnsureOpen(null, comp);
            }
            else
            {
                Window_Delegations.EnsureOpen();
            }
            DelegationUtility.LogVerbose("委派页签已打开（S18 起只是引导：详情在主控台）");
        }

        protected override void UpdateSize()
        {
            size = new Vector2(Width, StubHeight);
        }

        protected override void FillTab()
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            Widgets.Label(new Rect(0f, 0f, size.x, 24f), "RimDelegationTabMoved".Translate());
            if (Widgets.ButtonText(new Rect(0f, 30f, 220f, 30f), "RimDelegationTabOpenConsole".Translate()))
            {
                WorldObjectComp_Delegations comp = DelegationRegistry.For(SelCaravan);
                if (comp != null)
                {
                    Window_Delegations.EnsureOpen(null, comp);
                }
                else
                {
                    Window_Delegations.EnsureOpen();
                }
            }
            Widgets.Label(new Rect(0f, 64f, size.x, 24f), "RimDelegationTabMovedHint".Translate());
        }
    }
}
