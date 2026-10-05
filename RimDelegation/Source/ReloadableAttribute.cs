using System;

namespace RimDelegation
{
    /// <summary>
    /// 开发期「DLL 热重载」标记 —— 配合 pardeike 的 Rimworld-Doorstop + ILReloaderLib 使用。
    ///
    /// 为什么要有这个类（而不是引用 ILReloaderLib）：
    ///   ILReloader 是按**特性类型名**匹配的（`a.GetType().Name == "ReloadableAttribute"`，
    ///   见 ILReloaderLib/Tools.cs 的 IsReloadable / IsCecilReloadable），所以只要在自己程序集里
    ///   定义一个同名特性即可，不必加 NuGet 依赖、也不会把开发期库带进发布包。
    ///
    /// 生效条件（缺一不可）：
    ///   ① 游戏根目录装了 Unity Doorstop + Doorstop.dll，且 doorstop_config.ini 里 `harmony_library`
    ///      指向 0Harmony.dll（否则 Doorstop.Entrypoint.Start() 直接 return，什么都不做）；
    ///   ② 本 dll **在游戏启动前**就已部署 —— 旧方法是在启动期加载时被登记进
    ///      `Reloader.reloadableMembers` 的，启动后再加标注是没用的；
    ///   ③ 方法**签名不能改**（匹配键是 `声明类型全名.方法名(参数类型全名)`）。
    ///
    /// 边界（反编译 ILReloaderLib 得出）：热重载只把**新 dll 里这个方法体的 IL** 整体替换掉旧方法，
    /// 类型身份与实例字段都不变 ⇒ 新增字段/属性/新类型不生效，只有方法体（含构造函数）能热改。
    ///
    /// 注意：没装 Doorstop 时这个特性完全无害（就是个没人读的元数据），可以随包发布。
    ///
    /// 2026-09-30 本机实测补充（详见《热重载-可行性评估.md》§6）：
    ///   · 靶子必须是"只碰非泛型类型"的方法 —— 方法体/被调方法签名里出现构造泛型
    ///     （`Nullable&lt;T&gt;` / `List&lt;T&gt;` / `Dictionary&lt;K,V&gt;` …）会抛
    ///     `TypeLoadException: Could not resolve type System.Nullable`1&lt;Verse.TipSignal&gt;`
    ///     （原因：Tools.ResolveType 用 Cecil 风格名查类型，.NET 只认 `[[…]]` 形式）。
    ///   · 可热重载的方法集合在**启动时**登记 ⇒ 新增/取消标注都要重启一次游戏才生效。
    ///   · 同一个 dll 被连续两次事件触发时，第二次替换会抛 NullReferenceException；
    ///     它发生在 Unpatch 之前，所以**第一次的替换仍然生效**（无害）。
    ///   · 务必配 [MethodImpl(MethodImplOptions.NoInlining)]，否则 JIT 可能把这种小方法
    ///     内联进调用点，替换成功也看不出效果。
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
    public class ReloadableAttribute : Attribute
    {
    }
}
