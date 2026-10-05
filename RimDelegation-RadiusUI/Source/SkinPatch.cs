using System;
using System.Reflection;
using HarmonyLib;
using RimDelegation;
using UnityEngine;
using Verse;

namespace RimDelegationRadiusUI
{
    /// <summary>
    /// 补丁管理 + 入口前缀。
    ///
    /// S18 起只剩**一个**补丁点（另两个随 S18 的重构一起删掉了：
    /// 「远行队委派页签」退化成引导页 ⇒ `CaravanTabSkin`/`CaravanTabBridge` 无用；
    /// 旧版委派对话框退化成 Mod 设置里的逃生门 ⇒ `DelegationDialogSkin`/`DialogBridge` 无用）：
    ///   <c>Window_Delegations.DoWindowContents(Rect)</c> —— 底部按钮打开的「委派」主控台。
    ///
    /// 为什么不去 patch 那些下达入口的调用点：它们分别位于 lambda 与 `Arrived()` 里，
    /// 签名一变就崩；而主控台是稳定的方法。也不去 patch `WindowStack.Add`
    /// —— 那是全局热路径，代价与风险都不划算。
    ///
    /// 失败策略：
    ///   · 反射解析失败 → 不打补丁，游戏里就是 RimDelegation 原版主控台；
    ///   · 绘制连续异常 3 次 → 本会话停用皮肤并解锁补丁（设置页里能一键切回）。
    /// </summary>
    internal static class SkinPatch
    {
        private const int MaxFailures = 3;

        /// <summary>一个补丁点的全部状态。</summary>
        internal sealed class Slot
        {
            public readonly string Name;
            public MethodBase Method;
            public HarmonyMethod Prefix;

            /// <summary>
            /// 回退期间挂的**后置**补丁（S9 bugfix）：皮肤被摘掉时，原版画完之后叠一颗
            /// 「切回 RadiusUI 皮肤」小按钮 —— 否则玩家从皮肤里点「切回原版」之后就找不到回路了。
            /// 只有主控台有这个（它是唯一带"切回原版"按钮的面板）。
            /// </summary>
            public HarmonyMethod Postfix;

            public bool Applied;
            public bool AppliedPostfix;
            public int Failures;

            public Slot(string name)
            {
                Name = name;
            }

            public bool Available { get; set; }

            public string Status { get; set; } = "未初始化";
        }

        private static Harmony harmony;

        public static readonly Slot Console = new Slot("委派主控台窗口");

        public static void Init()
        {
            try
            {
                harmony = new Harmony(ModBoot.HarmonyId);
            }
            catch (Exception e)
            {
                Console.Status = "Harmony 初始化失败：" + e.Message;
                Log.Error("[RimDelegation-RadiusUI] " + Console.Status + "\n" + e);
                return;
            }

            // 委派主控台窗口（S8-b；S18 起是皮肤的主战场）
            // 它需要反射换掉窗口底（Verse.Window.windowDrawing 是私有字段），所以走桥
            bool bridgeOk = ConsoleWindowBridge.Resolve(out string bridgeError);
            InitSlot(Console, typeof(Window_Delegations), "DoWindowContents", new[] { typeof(Rect) },
                nameof(ConsolePrefix), bridgeOk, bridgeError);
            // S9 bugfix：这块被摘掉时要能点回皮肤，所以额外挂一颗 postfix
            Console.Postfix = ResolvePostfix(nameof(ConsoleRestorePostfix));
        }

        /// <summary>解析一个后置补丁方法（解析不到就返回 null ⇒ 这一块没有"回皮肤"入口，不影响其它功能）。</summary>
        private static HarmonyMethod ResolvePostfix(string name)
        {
            try
            {
                MethodInfo m = typeof(SkinPatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
                return m == null ? null : new HarmonyMethod(m);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void InitSlot(Slot slot, Type type, string methodName, Type[] args,
            string prefixName, bool bridgeOk, string bridgeError)
        {
            try
            {
                if (!bridgeOk)
                {
                    slot.Available = false;
                    slot.Status = "不可用（已回退原版）：" + bridgeError;
                    Log.Warning("[RimDelegation-RadiusUI] " + slot.Name + " " + slot.Status);
                    return;
                }

                slot.Method = AccessTools.Method(type, methodName, args);
                if (slot.Method == null)
                {
                    slot.Available = false;
                    slot.Status = "不可用（已回退原版）：找不到 " + type.Name + "." + methodName;
                    Log.Warning("[RimDelegation-RadiusUI] " + slot.Name + " " + slot.Status);
                    return;
                }

                MethodInfo prefixMethod = typeof(SkinPatch).GetMethod(prefixName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (prefixMethod == null)
                {
                    slot.Available = false;
                    slot.Status = "不可用：内部错误，找不到 " + prefixName;
                    Log.Error("[RimDelegation-RadiusUI] " + slot.Name + " " + slot.Status);
                    return;
                }

                slot.Prefix = new HarmonyMethod(prefixMethod);
                slot.Available = true;
                slot.Status = "就绪（尚未启用）";
            }
            catch (Exception e)
            {
                slot.Available = false;
                slot.Status = "初始化异常（已回退原版）：" + e.Message;
                Log.Error("[RimDelegation-RadiusUI] " + slot.Name + " " + slot.Status + "\n" + e);
            }
        }

        public static void SetEnabled(Slot slot, bool on)
        {
            if (!slot.Available)
            {
                return;
            }
            try
            {
                if (on && !slot.Applied)
                {
                    harmony.Patch(slot.Method, slot.Prefix);
                    slot.Applied = true;
                    slot.Failures = 0;
                    // 皮肤接管期间不挂"切回皮肤"按钮 —— 那时该显示的是"切回原版"
                    SetPostfix(slot, false);
                    slot.Status = "已启用（Radius UI 皮肤接管）";
                }
                else if (!on && slot.Applied)
                {
                    harmony.Unpatch(slot.Method, slot.Prefix.method);
                    slot.Applied = false;
                    // S9 bugfix：摘掉皮肤的同时挂上"回皮肤"入口
                    SetPostfix(slot, true);
                    slot.Status = "已停用（使用 RimDelegation 原版）";
                }
                else if (!on)
                {
                    // 本来就是停用状态（例如启动时设置就是关的）：只保证"回皮肤"入口在
                    SetPostfix(slot, true);
                }
            }
            catch (Exception e)
            {
                slot.Status = "切换失败：" + e.Message;
                Log.Error("[RimDelegation-RadiusUI] " + slot.Name + " " + slot.Status + "\n" + e);
            }
        }

        /// <summary>
        /// 挂/摘"回皮肤"后置补丁。
        ///
        /// 为什么必须单独记一个 <c>AppliedPostfix</c>：Harmony 的 Patch/Unpatch 对同一方法幂等性
        /// 不保证，重复 Unpatch 会抛"未找到补丁"；而 ApplyAll() 会被设置页每帧调用 ——
        /// 状态位就是让重复调用安全的唯一办法。
        /// </summary>
        private static void SetPostfix(Slot slot, bool on)
        {
            if (slot.Postfix == null || slot.AppliedPostfix == on)
            {
                return;
            }
            if (on)
            {
                harmony.Patch(slot.Method, postfix: slot.Postfix);
                slot.AppliedPostfix = true;
            }
            else
            {
                harmony.Unpatch(slot.Method, slot.Postfix.method);
                slot.AppliedPostfix = false;
            }
        }

        // ---------------------------------------------------------------- 入口前缀

        private static bool ConsolePrefix(Window_Delegations __instance, Rect inRect)
        {
            if (!Console.Applied)
            {
                return true;
            }
            return !TryDraw(Console, () => DelegationConsoleSkin.Draw(__instance, inRect));
        }

        /// <returns>true = 皮肤已接管本帧，调用方应返回 false 跳过原版绘制。</returns>
        private static bool TryDraw(Slot slot, Func<bool> draw)
        {
            try
            {
                return draw();
            }
            catch (Exception e)
            {
                slot.Failures++;
                Log.Error(string.Format("[RimDelegation-RadiusUI] {0} 绘制异常（第 {1}/{2} 次）：{3}",
                    slot.Name, slot.Failures, MaxFailures, e));
                if (slot.Failures >= MaxFailures)
                {
                    Log.Error("[RimDelegation-RadiusUI] " + slot.Name +
                        " 连续失败，本会话停用该皮肤，回到 RimDelegation 原版。");
                    SetEnabled(slot, false);
                }
                return false; // 异常 → 让原版接手，绝不让界面卡住
            }
        }

        // ---------------------------------------------------------------- 回退期的"回皮肤"入口

        /// <summary>
        /// 原版主控台上的「切回 RadiusUI 皮肤」按钮（S9 bugfix）。
        ///
        /// 用户报的症状（原话）：「从RadiusUI切换成原版UI后，原版UI没有重新切回RadiusUI的按钮」。
        /// 根因：皮肤标题栏那颗「切回原版」把 `skinConsole` 置 false ⇒ `ApplyAll()` 把本块的
        /// **prefix 摘掉** ⇒ 皮肤整段不再运行，"回皮肤"的入口只剩 Mod 设置里那个复选框 ——
        /// 玩家在窗口里找不到回路。修法：摘 prefix 的**同一个动作里**挂上这颗 postfix，
        /// 原版照原样画，我们只在左下角叠一颗小按钮（原版那一角是列表区的空白处）。
        ///
        /// 为什么必须由皮肤自己补：这是本 mod 的设计承诺 ——
        /// RimDelegation 一行代码都不依赖皮肤，所以"回皮肤"的入口只能长在皮肤这一侧。
        /// </summary>
        private static void ConsoleRestorePostfix(Rect inRect)
        {
            try
            {
                const float W = 190f;
                const float H = 26f;
                Rect r = new Rect(inRect.x + 2f, inRect.yMax - H - 2f, W, H);
                // 原版窗口底是深色的，铺一块半透明底让按钮在任何窗口位置都看得清
                Widgets.DrawBoxSolid(r.ExpandedBy(3f), new Color(0f, 0f, 0f, 0.55f));
                if (Widgets.ButtonText(r, "切回 RadiusUI 皮肤"))
                {
                    RadiusUISkinSettings s = RadiusUISkinMod.Settings;
                    if (s != null)
                    {
                        s.skinConsole = true;
                        s.enabled = true;              // 总开关可能也是关的，一并打开
                        RadiusUISkinMod.Instance?.WriteSettings();
                    }
                    RadiusUISkinMod.ApplyAll();
                }
                TooltipHandler.TipRegion(r, "重新启用 RadiusUI 皮肤版委派主控台（也可在 Mod 设置里恢复）");
            }
            catch (Exception e)
            {
                // 这颗按钮画不出来绝不能影响原版主控台 —— 它只是"回皮肤"的便利入口
                Log.WarningOnce("[RimDelegation-RadiusUI] 「切回皮肤」按钮绘制失败：" + e.Message, 0x5E0E3);
            }
        }
    }
}
