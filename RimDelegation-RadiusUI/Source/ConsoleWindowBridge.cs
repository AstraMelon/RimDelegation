using System;
using System.Reflection;
using RadiusUI.Framework;
using RimDelegation;
using UnityEngine;
using Verse;

namespace RimDelegationRadiusUI
{
    /// <summary>
    /// 用 Radius UI 画「委派」主控台窗口的**窗口底与关闭按钮**。
    ///
    /// 为什么要实现整个 <see cref="IWindowDrawing" />：RimDelegation 的 `Window_Delegations` 走的是
    /// `Window()` 那个默认构造，替不掉它的窗口底；但 `Verse.Window.windowDrawing` 是个**私有字段**，
    /// 反射换成我们这份即可 —— Radius UI - Quest Menu 是自己在构造里传 `new RadiusWindowDrawing()`，
    /// 我们没法改它的构造，就改字段（同一个效果）。
    ///
    /// 关键时序（反编译 `Verse.Window.InnerWindowOnGUI`）：
    ///   `windowDrawing.DoWindowBackground(windowRect.AtZero())` **发生在 `BeginGroup(inRect)` 之前**，
    ///   所以这里能在**整个窗口矩形**上画圆角底板 —— 也就是说不会出现"卡片比窗口小 18px 的透明边"
    ///   那种对话框皮肤的老毛病（README 已知限制 2）。内容仍然画在 inRect 里，天然就是 18px 内边距。
    ///
    /// 其余成员一律转交 `DefaultWindowDrawing`：只换我们要换的两个，别的一律保持原版行为
    /// （`BeginGroup/EndGroup` 尤其不能乱来，绘制组失衡会让整个窗口错位）。
    /// </summary>
    internal sealed class ConsoleWindowDrawing : IWindowDrawing
    {
        private readonly DefaultWindowDrawing fallback = new DefaultWindowDrawing();

        public GUIStyle EmptyStyle => fallback.EmptyStyle;

        public void DoWindowBackground(Rect rect)
        {
            // S9 bugfix 的配套：窗口被"切回原版"（或皮肤连续异常自动回退）之后，
            // 这个 ConsoleWindowDrawing 实例**仍然挂在窗口上**（换字段是永久的），
            // 所以这里必须自己交回原版窗口底 —— 否则玩家点了「切回原版」，
            // 看到的还是 Radius 的深色圆角面板，会以为按钮没生效。
            if (!SkinPatch.Console.Applied)
            {
                fallback.DoWindowBackground(rect);
                return;
            }
            CardChrome.Rounded(rect, Palette.Surface0, 12f);
            CardChrome.Outline(rect, Palette.Border, 1f, 12f);
        }

        public bool DoCloseButton(Rect rect, string text)
        {
            return fallback.DoCloseButton(rect, text);
        }

        /// <summary>
        /// 右上角那个小 ✕。
        ///
        /// 皮肤在管这个窗口时**不画**：标题栏自己已经画了一枚「关闭」IconButton，
        /// 这里再画一个就会出现两个 X（而且框架的 `CloseRect` 是从 `xMax − 12` 起算的，
        /// 26px 方块有 14px 落在窗口外被裁掉 —— 实测表现为右上角一个缺角的橙色方块）。
        /// 皮肤关掉时用 `CloseXIn`（`xMax − 30, y + 4`，完全在窗口内）兜底，别让玩家没法关窗。
        /// </summary>
        public bool DoClostButtonSmall(Rect rect)
        {
            if (SkinPatch.Console.Applied)
            {
                return false;
            }
            return UIKit.Flat.CloseXIn(rect);
        }

        public void BeginGroup(Rect rect)
        {
            fallback.BeginGroup(rect);
        }

        public void EndGroup()
        {
            fallback.EndGroup();
        }

        public void DoGrayOut(Rect rect)
        {
            fallback.DoGrayOut(rect);
        }
    }

    /// <summary>
    /// `Window_Delegations` 的反射桥：只做一件事 —— 把窗口底换成 <see cref="ConsoleWindowDrawing" />。
    ///
    /// 失败策略与另外两个桥一致：解析失败 ⇒ 这一块压根不打补丁（游戏里就是 RimDelegation 原版窗口），
    /// 并且把原因写进 `Player.log` 与设置页的"补丁状态"。
    /// </summary>
    internal sealed class ConsoleWindowBridge
    {
        private static FieldInfo fieldDrawing;
        private static string resolveError = "未解析";

        private readonly Window_Delegations win;

        public ConsoleWindowBridge(Window_Delegations win)
        {
            this.win = win;
        }

        public static bool Ready { get; private set; }

        public static bool Resolve(out string error)
        {
            error = resolveError;
            if (Ready)
            {
                return true;
            }
            try
            {
                fieldDrawing = typeof(Window).GetField("windowDrawing",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (fieldDrawing == null)
                {
                    resolveError = "找不到 Verse.Window.windowDrawing";
                    error = resolveError;
                    Ready = false;
                    return false;
                }
                if (!fieldDrawing.FieldType.IsAssignableFrom(typeof(ConsoleWindowDrawing)))
                {
                    // 字段类型变了（原版改了实现类）—— 宁可退回原版窗口，也不要抛在绘制路径上
                    resolveError = "windowDrawing 的字段类型是 " + fieldDrawing.FieldType.Name
                                   + "，无法装入 " + nameof(ConsoleWindowDrawing);
                    error = resolveError;
                    Ready = false;
                    return false;
                }
                resolveError = null;
                error = null;
                Ready = true;
                return true;
            }
            catch (Exception e)
            {
                resolveError = "反射 windowDrawing 异常：" + e.Message;
                error = resolveError;
                Ready = false;
                return false;
            }
        }

        /// <summary>
        /// 装上 Radius 的窗口底（幂等，每帧调用没关系）。
        /// 顺带把 `drawShadow` 关掉：圆角面板 + 原版方形阴影边看起会"毛边"。
        /// </summary>
        public bool InstallChrome()
        {
            if (!Ready)
            {
                return false;
            }
            try
            {
                win.drawShadow = false;
                if (fieldDrawing.GetValue(win) is ConsoleWindowDrawing)
                {
                    return true;
                }
                fieldDrawing.SetValue(win, new ConsoleWindowDrawing());
                return true;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[RimDelegation-RadiusUI] 换主控台窗口底失败（本窗口仍用原版窗口底）：" + e.Message, 0x5E0E1);
                return false;
            }
        }
    }
}
