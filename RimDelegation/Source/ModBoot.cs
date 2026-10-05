using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimDelegationRadiusUI;
using RimWorld;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 程序集被加载后，游戏在主线程调用带 [StaticConstructorOnStartup] 类型的静态构造函数，
    /// 此时所有 Def 已加载完毕——这是挂 Harmony 补丁的推荐时机。
    ///
    /// S1 阶段本 mod 的目标是【零 Harmony】：双入口全部走 WorldObjectComp + CaravanArrivalAction。
    /// 这里保留引导代码，是为了 S3 万一需要补 QuestPart 时才不用改工程结构。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ModBoot
    {
        /// <summary>Harmony 实例 ID：全局唯一，用 packageId。</summary>
        public const string HarmonyId = "duskmelon.rimdelegation";

        /// <summary>
        /// RIM-3（2026-10-05）：主控台皮肤需要的 Radius UI Framework「代数」。
        /// 低于它时框架自己会写一条明确日志并在主菜单提示，而不是让我们在运行时
        /// 撞上 `MissingMethodException`（沿用原皮肤 mod 的同一约定）。
        /// </summary>
        public const int SkinRequiredGeneration = 32;

        static ModBoot()
        {
            Harmony harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(ModBoot).Assembly);

            // ── RIM-3：主控台皮肤（原独立 mod「RimDelegation - Radius UI」）并入本体 ──────────
            // ① 向框架声明代数；
            // ② 解析 `Verse.Window.windowDrawing` 私有字段（换窗口底要用它）。这一步失败 ⇒
            //    本会话主控台整个走原版画法（`DelegationConsoleSkin.Draw` 直接返回 false）。
            RadiusUI.Framework.FrameworkVersion.Require("RimDelegation", SkinRequiredGeneration);
            bool skinOk = ConsoleWindowBridge.Resolve(out string skinError);
            if (!skinOk)
            {
                Log.Warning("[RimDelegation] 主控台皮肤不可用，本会话使用原版主控台画法：" + skinError);
            }

            Log.Message(string.Format(
                "[RimDelegation] 已加载 | Harmony {0} | 已打补丁方法 {1} 个 | DelegationDef {2} 个 | DelegationModeDef {3} 个 | DelegationEventDef {4} 个 | DelegationFoodMoodDef {5} 个 | DelegationPhaseDef {6} 个 | WorldObjectDef {7} 个 | 主控台画法 {8}",
                typeof(Harmony).Assembly.GetName().Version,
                harmony.GetPatchedMethods().Count(),
                DefDatabase<DelegationDef>.DefCount,
                DefDatabase<DelegationModeDef>.DefCount,
                DefDatabase<DelegationEventDef>.DefCount,
                DefDatabase<DelegationFoodMoodDef>.DefCount,
                DefDatabase<DelegationPhaseDef>.DefCount,
                DefDatabase<WorldObjectDef>.DefCount,
                skinOk ? "Radius UI 皮肤" : "原版（皮肤不可用）"));

            // 随机事件的 XML 若写坏了，游戏只会静默地"一个事件都不触发"。
            // 这里在启动时就把它喊出来（§19.24）。
            if (DefDatabase<DelegationEventDef>.DefCount == 0)
            {
                Log.Warning("[RimDelegation] 没有加载到任何 DelegationEventDef —— 随机事件不会触发，请检查 Defs/RimDelegation_DelegationEvents.xml");
            }
            // 同理：野外伙食 Def 缺失的症状是"吃饭一点额外心情都没有"，也是静默失败（§19.25）。
            if (DefDatabase<DelegationFoodMoodDef>.DefCount == 0)
            {
                Log.Warning("[RimDelegation] 没有加载到任何 DelegationFoodMoodDef —— 野外伙食不会生效，请检查 Defs/RimDelegation_FoodMood.xml");
            }

            CheckSiteComps();
            CheckOvertimeAndFlow();
            CheckS23Primitives();
            CheckS25FlowGates();
            CheckMainButton();
            CheckTargetTags();
            LogDelegationDefs();
        }

        /// <summary>
        /// 启动自检（2026-09-30，随第五条委派"工作站清剿"一起加）：
        /// **匹配标签一个件都匹配不上**的委派。
        ///
        /// 症状极隐蔽：Def 加载成功、`LogDelegationDefs` 的清单齐全、UI 也不报错，
        /// 但玩家在世界地图上永远看不到这条委派的入口（表现就是"右键地点没有委派选项"）——
        /// 因为它要的 `SitePartDef.tags` 在运行时合并后的 Def 库里根本不存在，
        /// 拼错一个字母（`Worksite`）就够。
        ///
        /// 为什么是 Warning 而不是 Error：没装对应 DLC 时那个标签天然不存在（例如没装 Ideology
        /// 就没有 `WorkSite`），这是**正常**的；把标签名打出来，玩家/我们一眼能分清
        /// "拼错了"还是"没装那个包"。
        /// </summary>
        private static void CheckTargetTags()
        {
            List<DelegationDef> defs = DefDatabase<DelegationDef>.AllDefsListForReading;
            List<SitePartDef> parts = DefDatabase<SitePartDef>.AllDefsListForReading;
            if (defs == null || parts == null)
            {
                return;
            }
            for (int i = 0; i < defs.Count; i++)
            {
                DelegationDef def = defs[i];
                if (def?.targetSitePartTags.NullOrEmpty() ?? true)
                {
                    continue;
                }
                for (int t = 0; t < def.targetSitePartTags.Count; t++)
                {
                    string tag = def.targetSitePartTags[t];
                    if (tag.NullOrEmpty())
                    {
                        continue;
                    }
                    if (!AnyPartWithTag(parts, tag))
                    {
                        Log.Warning("[RimDelegation] DelegationDef「" + def.defName + "」要的 SitePartDef 标签「" + tag +
                                    "」在当前 Def 库里一个件都没有 → 这条委派永远不会出现在世界地图上" +
                                    "（拼错了？还是对应的模组/DLC 没装？）");
                    }
                }
            }
        }

        /// <summary>Def 库里有没有任何一个 SitePartDef 带这个标签。</summary>
        private static bool AnyPartWithTag(List<SitePartDef> parts, string tag)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                List<string> tags = parts[i]?.tags;
                if (tags == null)
                {
                    continue;
                }
                for (int j = 0; j < tags.Count; j++)
                {
                    if (tags[j] == tag)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 启动自检（S23）：事件原语与段条件带来的两处新静默失败。
        ///
        ///   ① 一条事件既没有 <c>effects</c> 也没有 <c>flavorLines</c> ⇒ 它触发了也什么都不会发生
        ///      （症状：留痕里出现一条"（无描述）"，玩家以为机制坏了）；
        ///   ② `hideItemsUntilPhase` 指向一个**带 requireThreat 的段** ⇒ 没有守军的那类地点
        ///      永远走不到那一段，"预期获得 / 现场物资"会永久锁着。
        /// </summary>
        private static void CheckS23Primitives()
        {
            List<DelegationEventDef> events = DefDatabase<DelegationEventDef>.AllDefsListForReading;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    DelegationEventDef e = events[i];
                    if (e == null)
                    {
                        continue;
                    }
                    if (e.effects.NullOrEmpty() && e.flavorLines.NullOrEmpty())
                    {
                        Log.Warning("[RimDelegation] 事件「" + e.defName + "」既没有 effects 也没有 flavorLines" +
                                    " —— 它触发之后不会做任何事、也不会留下描述。" +
                                    "请检查 Defs/RimDelegation_DelegationEvents.xml");
                    }
                }
            }

            List<DelegationDef> defs = DefDatabase<DelegationDef>.AllDefsListForReading;
            if (defs == null)
            {
                return;
            }
            for (int i = 0; i < defs.Count; i++)
            {
                DelegationDef def = defs[i];
                if (def == null || def.hideItemsUntilPhase.NullOrEmpty())
                {
                    continue;
                }
                DelegationPhaseDef gate = DefDatabase<DelegationPhaseDef>.GetNamedSilentFail(def.hideItemsUntilPhase);
                if (gate == null)
                {
                    Log.Error("[RimDelegation] DelegationDef「" + def.defName + "」的 hideItemsUntilPhase 指向「"
                              + def.hideItemsUntilPhase + "」，但没有这条 DelegationPhaseDef —— 清单会被永久锁住。");
                }
                else if (gate.requireThreat)
                {
                    Log.Error("[RimDelegation] DelegationDef「" + def.defName + "」的 hideItemsUntilPhase 指向带 requireThreat 的段「"
                              + gate.defName + "」—— 没有守军的地点永远走不到那一段，清单会被永久锁住。");
                }
            }
        }

        /// <summary>
        /// 启动自检：屏幕底部「委派」按钮的 Def 是否真的加载到（S8）。
        ///
        /// 症状是"底部根本没有那个按钮"—— 又一个静默失败：XML 路径写错、defName 拼错、
        /// 或者别的 mod 把 MainButtonDef 覆盖掉了，都只表现为"按钮不见了"，游戏不会报错。
        /// 图标只检查 iconPath 非空（**不去碰 `def.Icon`**：那会立刻触发贴图加载，
        /// 贴在启动自检里一旦抛异常就是整局起不来；贴图缺失时 `ContentFinder` 自己会刷错误）。
        /// </summary>
        private static void CheckMainButton()
        {
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamedSilentFail("RimDelegation_Delegations");
            if (def == null)
            {
                Log.Error("[RimDelegation] 没有加载到 MainButtonDef「RimDelegation_Delegations」—— 屏幕底部不会有「委派」按钮。" +
                          "请检查 Defs/RimDelegation_MainButtons.xml 是否随包发布。");
                return;
            }
            if (def.workerClass != typeof(MainButtonWorker_Delegations))
            {
                Log.Error("[RimDelegation] MainButtonDef「RimDelegation_Delegations」的 workerClass 是 "
                          + (def.workerClass != null ? def.workerClass.Name : "null")
                          + "，不是 RimDelegation.MainButtonWorker_Delegations —— 按钮会点不动或走错流程。");
            }
            if (def.tabWindowClass == null)
            {
                Log.Warning("[RimDelegation] MainButtonDef「RimDelegation_Delegations」没有 tabWindowClass —— " +
                            "若原版某条路径去问 def.TabWindow（热键 / Notify_SwitchedMap / 别的 mod 调 ToggleTab）会抛异常。");
            }
            if (def.iconPath.NullOrEmpty())
            {
                Log.Warning("[RimDelegation] MainButtonDef「RimDelegation_Delegations」没有 iconPath —— " +
                            "按钮会退化成纯文字（Textures/UI/Icons/RimDelegation_Delegations.png 是否随包发布？）。");
            }
        }

        /// <summary>
        /// 启动自检：S6 新增的两处"配漏了也不会报错"的静默失败。
        ///
        ///   ① 开了紧急加班却没配心情 Def ⇒ 加班变成**免费**（玩家会以为这机制没有代价，
        ///      而实际上它绕过了工时窗口），必须喊出来；
        ///   ② 配了 flowPhases 却一条 DelegationPhaseDef 都没加载到 ⇒ 那几段固定时间
        ///      会被**静默跳过**（`DelegationFlow` 里 null 段被跳过），
        ///      玩家看到的是"说好的 4 小时前置呢"。
        /// </summary>
        private static void CheckOvertimeAndFlow()
        {
            List<DelegationDef> defs = DefDatabase<DelegationDef>.AllDefsListForReading;
            if (defs == null)
            {
                return;
            }
            for (int i = 0; i < defs.Count; i++)
            {
                DelegationDef def = defs[i];
                if (def == null)
                {
                    continue;
                }
                if (def.AllowsEmergencyOvertime && def.emergencyOvertimeMoodThought == null)
                {
                    Log.Error("[RimDelegation] DelegationDef「" + def.defName + "」开了紧急加班，但没有配置" +
                              " emergencyOvertimeMoodThought —— 加班会变成免费的（不扣心情）。请检查 Defs/RimDelegation_Delegations.xml");
                }
                if (!def.flowPhases.NullOrEmpty() && DefDatabase<DelegationPhaseDef>.DefCount == 0)
                {
                    Log.Error("[RimDelegation] DelegationDef「" + def.defName + "」配置了 flowPhases，但一条 DelegationPhaseDef 都没加载到" +
                              " —— 固定流程会被整体跳过。请检查 Defs/RimDelegation_Delegations.xml");
                }
            }
        }

        /// <summary>
        /// 所有 `worldObjectClass = RimWorld.Planet.Site` 的 WorldObjectDef。
        ///
        /// 必须全部挂上委派宿主组件 —— 它们是**互相独立的 def**，
        /// 只在 `Patches/RimDelegation_SiteComps.xml` 里写 `Site` 一个的话，
        /// 建在另外四种 def 上的事件点会完全没有委派入口（以及威胁评估）。
        /// </summary>
        private static readonly string[] SiteLikeWorldObjectDefs =
        {
            "Site", "SpaceSite", "ClaimableSite", "ClaimableSpaceSite", "Mechhive",
        };

        /// <summary>
        /// 启动自检：委派宿主组件是否真的挂上去了。
        ///
        /// 症状是"右键地点没有委派选项、地点面板也不显示委派"—— 典型的静默失败，
        /// 而原因大概率在 `Patches/RimDelegation_SiteComps.xml`（xpath 没匹配上、
        /// 或 patch 文件路径不对）。与其等玩家右键之后发现没反应，不如开局就喊出来。
        /// </summary>
        private static void CheckSiteComps()
        {
            for (int i = 0; i < SiteLikeWorldObjectDefs.Length; i++)
            {
                string name = SiteLikeWorldObjectDefs[i];
                WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail(name);
                if (def == null)
                {
                    continue;   // 没有这个 def（例如没装 Odyssey）—— 正常，跳过
                }
                if (!HasComp(def, typeof(WorldObjectCompProperties_Delegations)))
                {
                    Log.Error("[RimDelegation] WorldObjectDef「" + name + "」上没有 WorldObjectCompProperties_Delegations" +
                              " —— 建在它上面的事件点不会有委派入口。请检查 Patches/RimDelegation_SiteComps.xml 的 xpath。");
                }
                if (!HasComp(def, typeof(WorldObjectCompProperties_ThreatAssessment)))
                {
                    Log.Error("[RimDelegation] WorldObjectDef「" + name + "」上没有 WorldObjectCompProperties_ThreatAssessment" +
                              " —— 建在它上面的事件点不会有威胁评估入口。请检查 Patches/RimDelegation_SiteComps.xml 的 xpath。");
                }
            }
        }

        /// <summary>
        /// 启动自检（S25）：流程里"等玩家信号"的段必须配得上"揭露情报"的段。
        ///
        /// 症状：带 `pauseUntilSignal` 的段会让流程**停在它跟前**，而只有作战任务段的
        /// 「进行交战」按钮会下达那个信号；若整条流程里没有任何段带 `revealsThreat`，
        /// 玩家永远看不到那两颗按钮 ⇒ **委派永久卡死**且一声不吭。这是典型静默失败，启动就要喊。
        /// </summary>
        private static void CheckS25FlowGates()
        {
            List<DelegationDef> defs = DefDatabase<DelegationDef>.AllDefsListForReading;
            if (defs == null)
            {
                return;
            }
            for (int i = 0; i < defs.Count; i++)
            {
                DelegationDef def = defs[i];
                if (def?.flowPhases.NullOrEmpty() ?? true)
                {
                    continue;
                }
                bool needsSignal = false;
                bool revealsIntel = false;
                int combatAt = -1;
                int lootAt = -1;
                for (int j = 0; j < def.flowPhases.Count; j++)
                {
                    DelegationPhaseDef p = def.flowPhases[j];
                    if (p == null) continue;
                    if (!p.pauseUntilSignal.NullOrEmpty()) needsSignal = true;
                    if (p.revealsThreat) revealsIntel = true;
                    if (HasPhaseEffect<DelegationEffectDef_ResolveCombat>(p)) combatAt = j;
                    if (HasPhaseEffect<DelegationEffectDef_GainLoot>(p)) lootAt = j;
                }
                if (needsSignal && !revealsIntel)
                {
                    Log.Error("[RimDelegation] DelegationDef「" + def.defName + "」有段在等玩家信号（pauseUntilSignal），" +
                              "但整条流程里没有任何段带 revealsThreat —— 作战任务段不会揭露守军情报，" +
                              "「进行交战 / 撤退」两颗按钮就永远不会出现，流程会卡死在那个段之前。");
                }
                // S30：「搜集战利品」段的缴获清单是**交战段**抄下来的（`d.lootBag`）⇒ 它必须排在交战段之后，
                // 否则那一段永远拿到一张空清单：玩家看不到任何战利品，而且一句报错都没有。
                if (lootAt >= 0 && (combatAt < 0 || lootAt < combatAt))
                {
                    Log.Error("[RimDelegation] DelegationDef「" + def.defName + "」的「搜集战利品」段（挂 GainLoot 的那段）" +
                              (combatAt < 0 ? "，但整条流程里没有交战段（挂 ResolveCombat 的段）" : "排在了交战段之前") +
                              " —— 缴获清单是交战结算时抄下来的，这样排永远捡不到东西。");
                }
            }
        }

        /// <summary>这一段挂了这个类型的效果原语吗（读 `onEnter`）。</summary>
        private static bool HasPhaseEffect<T>(DelegationPhaseDef p) where T : DelegationEffectDef
        {
            if (p?.onEnter == null)
            {
                return false;
            }
            for (int i = 0; i < p.onEnter.Count; i++)
            {
                if (p.onEnter[i] is T)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasComp(WorldObjectDef def, System.Type propsType)        {
            if (def?.comps == null)
            {
                return false;
            }
            for (int i = 0; i < def.comps.Count; i++)
            {
                if (def.comps[i] != null && propsType.IsInstanceOfType(def.comps[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把委派 Def 的清单与各自的匹配标签打进日志。
        ///
        /// 症状是"某个地点右键没有委派选项"时，这行就是第一现场：
        /// 清单里少了某条 ⇒ Defs 没加载；清单齐全但标签对不上 ⇒ 是标签的问题。
        /// </summary>
        private static void LogDelegationDefs()
        {
            List<DelegationDef> defs = DefDatabase<DelegationDef>.AllDefsListForReading;
            if (defs == null || defs.Count == 0)
            {
                Log.Error("[RimDelegation] 没有加载到任何 DelegationDef —— 委派功能整体不可用，请检查 Defs/RimDelegation_Delegations.xml");
                return;
            }
            StringBuilder sb = new StringBuilder("[RimDelegation] DelegationDef 清单：");
            for (int i = 0; i < defs.Count; i++)
            {
                DelegationDef d = defs[i];
                sb.Append("\n  · ").Append(d.defName)
                  .Append("  worker=").Append(d.workerClass != null ? d.workerClass.Name : "?")
                  .Append("  tags=[")
                  .Append(d.targetSitePartTags != null ? string.Join(",", d.targetSitePartTags) : "")
                  .Append("]");
            }
            Log.Message(sb.ToString());
        }
    }
}
