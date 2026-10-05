using RimWorld;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「委派」主控台窗口的原版页签空壳（S8）。
    ///
    /// 正常情况下**永远不会有人看到它**：底部按钮的 `Activate()` 直接开
    /// <see cref="Window_Delegations" />，不走 `MainTabsRoot`。
    /// 留这个类只为兜住那些会去问 `MainButtonDef.TabWindow` 的原版路径
    /// （热键、`Notify_SwitchedMap`、其它 mod 直接调 `ToggleTab`）——
    /// 没有 `tabWindowClass` 时那一路会在 `Activator.CreateInstance(null)` 上炸掉。
    ///
    /// 手法与 Radius UI - Quest Menu 的 `MainTab_RadiusQuests` 相同：
    /// 把自己缩成 1×1、在 `PostOpen` 里开真正的窗口，然后立刻关掉自己。
    /// </summary>
    public class MainTabWindow_Delegations : MainTabWindow
    {
        public override Vector2 RequestedTabSize => new Vector2(1f, 1f);

        public MainTabWindow_Delegations()
        {
            doWindowBackground = false;
            drawShadow = false;
            soundAppear = null;
            soundClose = null;
        }

        public override void PostOpen()
        {
            base.PostOpen();
            Window_Delegations.EnsureOpen();
            Close(false);
            Find.MainTabsRoot.EscapeCurrentTab(false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 空的：这个壳只负责"把玩家送到浮动窗口"，自己一帧都不画
        }
    }
}
