using RadiusUI.Framework;
using UnityEngine;
using Verse;

namespace RimDelegationRadiusUI
{
    /// <summary>
    /// Mod 入口。游戏在 LoadedModManager.CreateModClasses() 里枚举所有非抽象的 Verse.Mod
    /// 子类并实例化，所以 About.xml 不需要声明类名；构造函数必须接收 ModContentPack。
    ///
    /// 设置页刻意用 Radius UI 自己的 SettingsPage 画 —— 顺便验证框架的设置页组件是否好用。
    /// </summary>
    public class RadiusUISkinMod : Mod
    {
        public static RadiusUISkinMod Instance { get; private set; }

        public static RadiusUISkinSettings Settings { get; private set; }

        public RadiusUISkinMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<RadiusUISkinSettings>();
        }

        public override string SettingsCategory()
        {
            return "RimDelegation - Radius UI";
        }

        public static void ApplyAll()
        {
            RadiusUISkinSettings s = Settings;
            bool master = s == null || s.enabled;
            SkinPatch.SetEnabled(SkinPatch.Console, master && (s == null || s.skinConsole));
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            RadiusUISkinSettings s = Settings;
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            SettingsPage.Heading(listing, "RimDelegation - Radius UI（试验）");
            SettingsPage.Note(listing,
                "只换绘制，不换逻辑：候选名单、模式、结束条件、暂停/中止与三个出口回调，全部仍由 RimDelegation 自己执行。");

            bool enabled = s.enabled;
            SettingsPage.Checkbox(listing, "总开关", ref enabled, "关掉 = 两块都立刻恢复 RimDelegation 原版，不需要重启。");
            s.enabled = enabled;

            SettingsPage.Heading(listing, "皮肤范围");
            bool console = s.skinConsole;
            SettingsPage.Checkbox(listing, "委派主控台窗口", ref console,
                "屏幕底部「委派」按钮打开的那个浮动窗口（含窗口底、关闭按钮、待下达表单）。",
                enabled, "总开关已关闭");
            s.skinConsole = console;

            bool verbose = s.verbose;
            SettingsPage.Checkbox(listing, "详细日志", ref verbose, "把皮肤接管 / 回退 / 异常写进 Player.log。");
            s.verbose = verbose;

            SettingsPage.Heading(listing, "主控台阅读偏好");
            bool hoverFocus = s.hoverFocus;
            SettingsPage.Checkbox(listing, "悬浮焦点（左栏 / 流程）", ref hoverFocus,
                "鼠标落在流程栏 ⇒ 展开流程、收起左栏；落在左栏 ⇒ 只展开左栏，**不收起流程**。" +
                "鼠标离开两栏就回到你自己的折叠设置；被图钉（列头 ＋/－ 左边的 PIN）固定展开的栏不参与。" +
                "只在两栏都是展开状态时生效。");
            s.hoverFocus = hoverFocus;

            // 勾选状态一变就立刻重挂/摘补丁
            ApplyAll();

            SettingsPage.Heading(listing, "补丁状态");
            StatusLine(listing, "委派主控台：", SkinPatch.Console);

            if (SettingsPage.ResetButton(listing, "恢复默认"))
            {
                s.enabled = true;
                s.skinConsole = true;
                s.verbose = false;
                s.hoverFocus = true;
                ApplyAll();
            }

            listing.End();
        }

        private static void StatusLine(Listing_Standard listing, string prefix, SkinPatch.Slot slot)
        {
            SettingsPage.Note(listing, prefix + slot.Status);
            if (!slot.Available)
            {
                SettingsPage.Note(listing, "  ↑ 这一块不可用，已自动回退原版 —— 通常是 RimDelegation 的内部字段/方法改名了。");
            }
        }
    }
}
