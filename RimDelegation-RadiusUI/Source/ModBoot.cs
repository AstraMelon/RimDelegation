using RadiusUI.Framework;
using Verse;

namespace RimDelegationRadiusUI
{
    /// <summary>
    /// 程序集被加载后，游戏在主线程调用带 [StaticConstructorOnStartup] 类型的静态构造函数。
    /// 此时所有 Mod 类都已实例化、所有 Def 都已加载 —— 是挂 Harmony 补丁的时机。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ModBoot
    {
        /// <summary>Harmony 实例 ID：全局唯一，用本 mod 的 packageId。</summary>
        public const string HarmonyId = "duskmelon.rimdelegation.radiusui";

        static ModBoot()
        {
            // 声明我们要的框架「代数」。代数不满足时框架会写一条明确日志并在主菜单提示，
            // 而不是让我们在运行时撞上 MissingMethodException（框架 generation 32）。
            FrameworkVersion.Require("RimDelegation - Radius UI", 32);

            SkinPatch.Init();
            RadiusUISkinMod.ApplyAll();

            Log.Message(string.Format(
                "[RimDelegation-RadiusUI] 已加载 | 委派主控台：{0}（{1}）",
                SkinPatch.Console.Applied ? "启用" : "停用", SkinPatch.Console.Status));
        }
    }
}
