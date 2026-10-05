using RadiusUI.Framework;
using UnityEngine;
using Verse;

namespace RimDelegationRadiusUI
{
    /// <summary>
    /// 皮肤共用的按钮件（S8 从当时的页签皮肤里抽出来的，S18 起由委派主控台皮肤独用）。
    ///
    /// RIM-3（2026-10-05）：随 Radius UI 皮肤整体并入本体程序集，命名空间保持不变。
    ///
    /// 为什么必须共用：RadiusUI 的 `ButtonStyle` 只有 Primary / Solid / Ghost 三档
    /// （反编译确认只有这三个静态字段），没有"危险"档；我们自己铺的那层红按钮
    /// 一旦在两处各写一遍，迟早会出现"页签里是红、主控台里不是"。
    /// </summary>
    internal static class SkinButtons
    {
        /// <summary>
        /// 破坏性动作的**红按钮**（用户要求：紧急加班 / 中止委派 用红色）。
        /// 照 `UIKit.Button` 的内部画法铺一层：圆角底色 → 悬停高亮 → 描边 → 居中标签 → 收点击。
        /// </summary>
        public static bool Danger(Rect r, string label, bool enabled, string tip)
        {
            Color fill = new Color(0.60f, 0.17f, 0.17f);
            if (!enabled)
            {
                fill.a = 0.45f;
            }
            CardChrome.Rounded(r, fill, 8f);
            if (Mouse.IsOver(r) && enabled)
            {
                CardChrome.Hover(r, 8f);
            }
            CardChrome.Outline(r, new Color(0.86f, 0.36f, 0.36f), 1f, 8f);
            RadiusFont.Label(r, label, GameFont.Small, false,
                enabled ? Palette.InkOnAccent : Palette.TextFaint, TextAnchor.MiddleCenter, false);
            if (!tip.NullOrEmpty())
            {
                TooltipHandler.TipRegion(r, tip);
            }
            return Widgets.ButtonInvisible(r, true) && enabled;
        }
    }
}
