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
    /// 为什么要实现整个 <see cref="IWindowDrawing" />：`Window_Delegations` 走的是
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
    ///
    /// RIM-3（2026-10-05，皮肤并入本体）后的变化：
    ///   · 不再有"皮肤被摘掉"这个状态 ⇒ 窗口底与关闭按钮的 <c>Applied</c> 分支全部删除；
    ///   · 唯一的失败路径是**反射拿不到 `windowDrawing` 字段**（原版改了实现）⇒
    ///     <see cref="Ready" /> 为 false，此时皮肤整段不接管，主控台落回原版画法（连窗口底也是原版的）。
    /// </summary>
    internal sealed class ConsoleWindowDrawing : IWindowDrawing
    {
        private readonly DefaultWindowDrawing fallback = new DefaultWindowDrawing();

        public GUIStyle EmptyStyle => fallback.EmptyStyle;

        public void DoWindowBackground(Rect rect)
        {
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
        /// 皮肤**不画**：标题栏自己已经画了一枚「关闭」IconButton，
        /// 这里再画一个就会出现两个 X（而且框架的 `CloseRect` 是从 `xMax − 12` 起算的，
        /// 26px 方块有 14px 落在窗口外被裁掉 —— 实测表现为右上角一个缺角的橙色方块）。
        /// </summary>
        public bool DoClostButtonSmall(Rect rect)
        {
            return false;
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
    /// RIM-3 之前它是"皮肤 mod"的桥，解析失败就当皮肤不存在；合并后主控台**默认就是皮肤画法**，
    /// 所以语义变成"**主控台能否用皮肤的窗口底**"：
    ///   · <see cref="Ready" /> = false ⇒ 皮肤整段不接管（<see cref="DelegationConsoleSkin.Draw" /> 返回 false）
    ///     ⇒ 主控台走原版画法（连窗口底也是原版）。这是"界面绝不空窗"的最后一道，
    ///     而且是**启动时一次性判定**的，不会中途变。
    ///   · 反射成功、只是这一帧 SetValue 失败 ⇒ 只有窗口底是原版的，内容照旧由皮肤画。
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
                Log.WarningOnce("[RimDelegation] 皮肤：换主控台窗口底失败（本窗口仍用原版窗口底）：" + e.Message, 0x5E0F1);
                return false;
            }
        }
    }
}
