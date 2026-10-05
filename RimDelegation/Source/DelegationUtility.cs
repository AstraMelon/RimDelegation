using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>双入口共用的匹配 / 校验 / 兜底逻辑。</summary>
    public static class DelegationUtility
    {
        /// <summary>按 SitePartDef.tags / targetSitePartDefs 找能作用于该地点的委派 def。</summary>
        public static List<DelegationDef> MatchingDefs(Site site)
        {
            List<DelegationDef> result = new List<DelegationDef>();
            if (site == null)
            {
                return result;
            }
            List<DelegationDef> all = DefDatabase<DelegationDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                if (Matches(all[i], site))
                {
                    result.Add(all[i]);
                }
            }
            return result;
        }

        public static bool Matches(DelegationDef def, Site site)
        {
            if (def == null || site?.parts == null)
            {
                return false;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (part?.def == null)
                {
                    continue;
                }
                if (!def.targetSitePartDefs.NullOrEmpty() && def.targetSitePartDefs.Contains(part.def))
                {
                    return true;
                }
                if (!def.targetSitePartTags.NullOrEmpty() && !part.def.tags.NullOrEmpty())
                {
                    for (int j = 0; j < part.def.tags.Count; j++)
                    {
                        if (def.targetSitePartTags.Contains(part.def.tags[j]))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>车队里可以参加委派的人：能行动的自由人/奴隶（排除囚犯、倒地、动物）。</summary>
        public static List<Pawn> EligiblePawns(Caravan caravan)
        {
            List<Pawn> list = new List<Pawn>();
            if (caravan == null)
            {
                return list;
            }
            List<Pawn> pawns = caravan.PawnsListForReading;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || p.Downed || !p.RaceProps.Humanlike || p.IsPrisoner)
                {
                    continue;
                }
                list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// 把"计划时选中的人"与"抵达时车队实际还有的人"取交集。
        /// 全部失效时回退到全队（计划期间车队被拆分/合并的兜底）。
        /// </summary>
        public static List<Pawn> ResolveParticipants(Caravan caravan, List<Pawn> requested)
        {
            List<Pawn> eligible = EligiblePawns(caravan);
            if (requested.NullOrEmpty())
            {
                return eligible;
            }
            List<Pawn> result = new List<Pawn>();
            for (int i = 0; i < requested.Count; i++)
            {
                if (requested[i] != null && eligible.Contains(requested[i]))
                {
                    result.Add(requested[i]);
                }
            }
            return result.Count > 0 ? result : eligible;
        }

        /// <summary>菜单/按钮的可用性判定（FloatMenuAcceptanceReport 三态语义见 DESIGN.md §2.2）。</summary>
        public static FloatMenuAcceptanceReport CanStart(Caravan caravan, Site site, DelegationDef def)
        {
            if (def == null || site == null)
            {
                return false;
            }
            if (site.Destroyed)
            {
                return FloatMenuAcceptanceReport.WithFailReason("地点已不存在");
            }
            if (site.HasMap)
            {
                // 已经进过图 → 不提供委派入口
                return false;
            }
            if (!Matches(def, site))
            {
                return false;
            }
            WorldObjectComp_Delegations comp = site.GetComponent<WorldObjectComp_Delegations>();
            if (comp == null)
            {
                return false;
            }
            if (comp.Depleted)
            {
                return FloatMenuAcceptanceReport.WithFailReason("此地点已被委派采空");
            }
            if (comp.active != null)
            {
                return FloatMenuAcceptanceReport.WithFailReason("此地点已有委派在进行中");
            }
            if (caravan == null || caravan.Destroyed)
            {
                return false;
            }
            int need = def.minPawns < 1 ? 1 : def.minPawns;
            if (EligiblePawns(caravan).Count < need)
            {
                return FloatMenuAcceptanceReport.WithFailReason($"至少需要 {need} 名可行动人员");
            }
            return true;
        }

        /// <summary>找到该地点对应的原版 30 天超时计时器（QuestPart_WorldObjectTimeout）。</summary>
        public static QuestPart_WorldObjectTimeout FindTimeoutPart(Site site)
        {
            if (site == null)
            {
                return null;
            }
            List<Quest> quests = Find.QuestManager.QuestsListForReading;
            for (int i = 0; i < quests.Count; i++)
            {
                Quest quest = quests[i];
                if (quest == null || quest.State != QuestState.Ongoing)
                {
                    continue;
                }
                List<QuestPart> parts = quest.PartsListForReading;
                for (int j = 0; j < parts.Count; j++)
                {
                    if (parts[j] is QuestPart_WorldObjectTimeout timeout && timeout.worldObject == site)
                    {
                        return timeout;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 暂停目标地点的超时计时，并把"剩余多少 ticks"带出来。
        ///
        /// QuestPartActivable.State 是只读的（private state + State =&gt; state），
        /// 但 Notify_QuestSignalReceived 是 public，发 inSignalDisable 就是原版
        /// "site.MapGenerated 之后停表"所用的同一条路径 → 零 Harmony、零反射。
        ///
        /// ⚠️ QuestPart_Delay.TicksLeft 在禁用状态恒返回 0（源码：if (State != Enabled) return 0），
        ///    所以剩余时间必须在发信号【之前】读，否则读到的永远是 0。
        /// </summary>
        public static bool TryPauseTimeout(Site site, out int remainingTicks, out string report)
        {
            remainingTicks = 0;
            report = null;
            QuestPart_WorldObjectTimeout part = FindTimeoutPart(site);
            if (part == null)
            {
                report = "没找到 QuestPart_WorldObjectTimeout（该地点可能不是任务生成的）";
                return false;
            }
            if (part.State != QuestPartState.Enabled)
            {
                report = $"计时部件当前状态为 {part.State}，无需暂停";
                return false;
            }
            if (part.inSignalDisable.NullOrEmpty())
            {
                report = "该计时部件的 inSignalDisable 为空，无法用信号暂停";
                return false;
            }
            remainingTicks = part.TicksLeft;
            part.Notify_QuestSignalReceived(new Signal(part.inSignalDisable, false));
            report = $"已发送信号 '{part.inSignalDisable}'；剩余 {remainingTicks} ticks（{remainingTicks / 60000f:0.#} 天），当前状态 = {part.State}";
            return part.State == QuestPartState.Disabled;
        }

        /// <summary>
        /// 恢复被暂停的超时计时。
        ///
        /// 这里必须用反射，原因：inSignalEnable 为 null 且 reactivatable = false，
        /// 所以 Notify_QuestSignalReceived 永远无法把部件重新 Enable。
        /// 步骤：① 反射把 private state 写回 Enabled；
        ///       ② TicksLeft = enableTick + delayTicks - now，所以把"被暂停掉的时长"补进
        ///          公开字段 delayTicks 即可精确续上，不需要碰 enableTick。
        /// </summary>
        public static bool TryResumeTimeout(Site site, int pausedTicks, out string report)
        {
            report = null;
            QuestPart_WorldObjectTimeout part = FindTimeoutPart(site);
            if (part == null)
            {
                report = "没找到 QuestPart_WorldObjectTimeout";
                return false;
            }
            if (part.State == QuestPartState.Enabled)
            {
                report = "计时部件本来就是启用状态";
                return true;
            }
            FieldInfo stateField = AccessTools.Field(typeof(QuestPartActivable), "state");
            if (stateField == null)
            {
                report = "反射失败：找不到 QuestPartActivable.state（游戏版本可能变了）";
                return false;
            }
            stateField.SetValue(part, QuestPartState.Enabled);
            if (pausedTicks > 0)
            {
                part.delayTicks += pausedTicks;
            }
            report = $"已恢复；补回 {pausedTicks} ticks（{pausedTicks / 60000f:0.#} 天），delayTicks = {part.delayTicks}，TicksLeft = {part.TicksLeft}";
            return part.State == QuestPartState.Enabled;
        }

        /// <summary>给一个人挂一条心情记忆（ThoughtDef 为空则跳过）。</summary>
        public static void GrantThought(Pawn p, ThoughtDef thought)
        {
            if (p == null || thought == null || p.needs?.mood?.thoughts?.memories == null || p.Dead)
            {
                return;
            }
            p.needs.mood.thoughts.memories.TryGainMemory(thought, null, null);
        }

        /// <summary>某条 ThoughtDef 的心情影响（取第一阶段），用于 UI 显示。</summary>
        public static float MoodEffectOf(ThoughtDef thought)
        {
            if (thought?.stages == null || thought.stages.Count == 0)
            {
                return 0f;
            }
            return thought.stages[0].baseMoodEffect;
        }

        /// <summary>委派期间"每天"的心情合计（def 基础 + 模式额外）。</summary>
        public static float DailyMoodOffset(DelegationDef def, DelegationModeDef mode)
        {
            return MoodEffectOf(def?.dailyMoodThought) + MoodEffectOf(mode?.dailyMoodThought);
        }

        // ── 疲劳 → 工伤（§19.25）────────────────────────────────────────────
        //
        // 为什么必须由我们来算这笔账：
        //   · `NeedRest`（疲劳心情 -6/-12/-18）**在车队里根本不生效** ——
        //     它是情境型想法且 ThoughtDef 没写 validWhileDespawned，
        //     ThoughtUtility.CanGetThought 会直接 return false。
        //   · `DelegationWorker_Mining.Tick` 也从不读 rest。
        //   ⇒ 休息条在委派里原本是"死"的。把疲劳接到事故率上，长工时模式才有真实代价。

        /// <summary>全队平均休息水平（0..1）。无人 / 没有 rest 需求时返回 1（= 不惩罚）。</summary>
        public static float AverageRest(Delegation d)
        {
            return AverageRestOf(d?.participants);
        }

        /// <summary>一组人的平均休息水平（0..1）。空集合 / 没有 rest 需求时返回 1。</summary>
        public static float AverageRestOf(List<Pawn> pawns)
        {
            if (pawns == null)
            {
                return 1f;
            }
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead)
                {
                    continue;
                }
                Need_Rest rest = p.needs?.rest;
                if (rest == null)
                {
                    continue;
                }
                sum += Mathf.Clamp01(rest.CurLevel);
                n++;
            }
            return n == 0 ? 1f : sum / n;
        }

        /// <summary>疲劳系数 0..1：平均休息 ≥ onset 时为 0，掉到 0 时为 1。线性。</summary>
        public static float FatigueFactor(Delegation d, float onsetRest)
        {
            return FatigueFactorOf(AverageRest(d), onsetRest);
        }

        public static float FatigueFactorOf(float avgRest, float onsetRest)
        {
            if (onsetRest <= 0f)
            {
                return 0f;
            }
            return Mathf.Clamp01((onsetRest - avgRest) / onsetRest);
        }

        private static DelegationEventDef_PawnAccident cachedAccidentDef;

        /// <summary>工伤事件 Def（UI 要拿它的参数算"当前事故倍率"）。没有就返回 null。</summary>
        public static DelegationEventDef_PawnAccident AccidentDef()
        {
            // Def 在开发者模式下可能被热重载成新实例，所以这里每次都验一下 defName
            DelegationEventDef_PawnAccident cached = cachedAccidentDef;
            if (cached != null && !cached.defName.NullOrEmpty())
            {
                return cached;
            }
            List<DelegationEventDef> all = DefDatabase<DelegationEventDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is DelegationEventDef_PawnAccident acc)
                {
                    cachedAccidentDef = acc;
                    return acc;
                }
            }
            return null;
        }

        /// <summary>当前工伤风险倍率（1 = 无额外风险）。UI 显示用，兜住异常。</summary>
        public static float AccidentRiskMultiplier(Delegation d)
        {
            DelegationEventDef_PawnAccident acc = AccidentDef();
            if (acc == null || d == null)
            {
                return 1f;
            }
            try
            {
                return acc.ChanceMultiplier(d);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 计算工伤倍率失败：" + ex, 0x5E0C4);
                return 1f;
            }
        }

        // ── 野外伙食（§19.25）──────────────────────────────────────────────

        private static DelegationFoodMoodDef cachedFoodMoodDef;

        /// <summary>野外伙食 Def（全局只该有一条）。没有就返回 null。</summary>
        public static DelegationFoodMoodDef FoodMoodDef()
        {
            DelegationFoodMoodDef cached = cachedFoodMoodDef;
            if (cached != null && !cached.defName.NullOrEmpty())
            {
                return cached;
            }
            List<DelegationFoodMoodDef> all = DefDatabase<DelegationFoodMoodDef>.AllDefsListForReading;
            if (all.Count == 0)
            {
                return null;
            }
            cachedFoodMoodDef = all[0];
            return cachedFoodMoodDef;
        }

        /// <summary>
        /// 如果这个人正在某个委派里，就按刚吃下去的东西补一条「野外伙食」记忆。
        /// 由 `Patch_Thing_Ingested_FieldMeal`（`Thing.Ingested` 的后缀）调用。
        ///
        /// 阶段心情为 0 时**不挂**：0 心情的记忆不会出现在需求列表里（会被 `MoodOffset() != 0f` 过滤掉），
        /// 挂了只是白占内存。
        /// </summary>
        public static void GrantFieldMeal(Thing food, Pawn ingester)
        {
            if (food?.def == null || ingester == null)
            {
                return;
            }
            if (ingester.Dead || !ingester.RaceProps.Humanlike || !food.def.IsNutritionGivingIngestible)
            {
                return;
            }
            if (DelegationRegistry.AnyActiveFor(ingester) == null)
            {
                return;
            }
            DelegationFoodMoodDef cfg = FoodMoodDef();
            if (cfg?.thought == null)
            {
                return;
            }

            int stage = cfg.StageFor(food.def);
            if (cfg.MoodOfStage(stage) == 0f)
            {
                return;
            }
            try
            {
                Thought_Memory memory = ThoughtMaker.MakeThought(cfg.thought, stage);
                if (memory != null)
                {
                    ingester.needs?.mood?.thoughts?.memories?.TryGainMemory(memory);
                }
            }
            catch (Exception ex)
            {
                // 吃饭在车队 tick 里，异常绝不能让整局崩掉
                Log.WarningOnce("[RimDelegation] 野外伙食记忆挂载失败：" + ex, 0x5E0C5);
            }
        }

        /// <summary>车队库存里阶段最高的那份食物（`TryGetBestFood` 之外只用于 UI 预告）。</summary>
        public static ThingDef BestCarriedFood(Caravan caravan)
        {
            if (caravan == null)
            {
                return null;
            }
            DelegationFoodMoodDef cfg = FoodMoodDef();
            if (cfg == null)
            {
                return null;
            }
            ThingDef best = null;
            int bestStage = int.MinValue;
            List<Thing> items = CaravanInventoryUtility.AllInventoryItems(caravan);
            for (int i = 0; i < items.Count; i++)
            {
                ThingDef food = items[i]?.def;
                if (food == null || !food.IsNutritionGivingIngestible)
                {
                    continue;
                }
                int stage = cfg.StageFor(food);
                if (best == null || stage > bestStage)
                {
                    bestStage = stage;
                    best = food;
                }
            }
            return best;
        }

        public static void LogVerbose(string msg)
        {
            if (RimDelegationMod.Settings != null && RimDelegationMod.Settings.verboseLogging)
            {
                Log.Message("[RimDelegation] " + msg);
            }
        }

        /// <summary>
        /// 把 count 个 thingDef 交付进车队库存。
        /// Caravan.AddPawnOrItem → CaravanInventoryUtility.GiveThing（含负重与人份分配）。
        /// 按 stackLimit 拆堆，避免生成超大堆叠。
        /// </summary>
        public static int DeliverThingToCaravan(Caravan caravan, ThingDef thingDef, int count)
        {
            if (caravan == null || thingDef == null || count <= 0)
            {
                return 0;
            }
            int stackLimit = Mathf.Max(1, thingDef.stackLimit);
            int remaining = count;
            int delivered = 0;
            while (remaining > 0)
            {
                int chunk = Mathf.Min(remaining, stackLimit);
                Thing thing = ThingMaker.MakeThing(thingDef);
                thing.stackCount = chunk;
                caravan.AddPawnOrItem(thing, false);
                remaining -= chunk;
                delivered += chunk;
            }
            return delivered;
        }

        // ================================================================ S30：缴获（作战侧战利品）

        /// <summary>
        /// 从一次交战里挑出**打掉的那些敌人**留下的东西（用户口径：「作战任务的主要是缴获敌人装备」）。
        ///
        /// 判据：`CombatResult.Units` 与 `CombatScene.Units` **同序**，而场景里的敌方严格按
        /// `ThreatRosterFactory` 的编队顺序加入 ⇒ 用敌方下标对齐即可（不按名字，同名守军会配错）。
        /// 阵亡与倒地**都算** —— 他们都已经退出战斗了（跑掉的守军把东西带走了）。
        /// </summary>
        public static List<DelegationLootItem> CaptureLoot(Combat.CombatSetup setup, Combat.CombatResult result)
        {
            List<DelegationLootItem> loot = new List<DelegationLootItem>();
            if (setup?.EnemyLoot == null || result?.Units == null)
            {
                return loot;
            }
            int enemyIndex = -1;
            for (int i = 0; i < result.Units.Count; i++)
            {
                Combat.UnitReport u = result.Units[i];
                if (u == null || u.IsMine)
                {
                    continue;
                }
                enemyIndex++;
                if (!u.Dead && !u.Downed)
                {
                    continue;
                }
                for (int j = 0; j < setup.EnemyLoot.Count; j++)
                {
                    DelegationLootItem it = setup.EnemyLoot[j];
                    if (it != null && it.enemyIndex == enemyIndex)
                    {
                        loot.Add(it);
                    }
                }
            }
            return loot;
        }

        /// <summary>
        /// 把战利品装进车队：**能装多少装多少**（受车队剩余载重限制），值钱的先装。
        ///
        /// 用户问过"份量口径"，这里给的是**载重闸门**这一档：不额外打折，也不凭空全收 ——
        /// 装不下的留在原地（与物资藏匿点"装满就收工"同一个道理）。返回给玩家看的那句话。
        ///
        /// 品质与材质都还原（缴获回来的枪该是"极佳"就是"极佳"）；**耐久不还原**（见 `DelegationLootItem`）。
        /// </summary>
        public static string TakeLoot(Caravan caravan, List<DelegationLootItem> loot,
            List<DelegationLootItem> takenInto = null)
        {
            if (caravan == null || caravan.Destroyed)
            {
                return null;
            }
            if (loot.NullOrEmpty())
            {
                return "战场上没剩下能带走的东西。";
            }

            List<DelegationLootItem> sorted = new List<DelegationLootItem>(loot);
            sorted.Sort((a, b) => b.SortValue.CompareTo(a.SortValue));

            float free = Mathf.Max(0f, caravan.MassCapacity - caravan.MassUsage);
            int taken = 0, left = 0;
            float mass = 0f, value = 0f;
            List<string> shown = new List<string>();
            for (int i = 0; i < sorted.Count; i++)
            {
                DelegationLootItem item = sorted[i];
                if (item?.def == null || item.count <= 0)
                {
                    continue;
                }
                float unitMass = Mathf.Max(0f, item.UnitMass);
                int take = item.count;
                if (item.def.stackLimit > 1 && unitMass > 0.0001f)
                {
                    // 可堆叠：能装几件装几件（部分装载比"整叠放弃"更合理）
                    take = Mathf.Clamp(Mathf.FloorToInt((free + 0.0001f) / unitMass), 0, item.count);
                }
                else if (unitMass * take > free + 0.0001f)
                {
                    take = 0;
                }
                if (take <= 0)
                {
                    left += item.count;
                    continue;
                }

                Thing thing;
                try
                {
                    thing = ThingMaker.MakeThing(item.def, item.stuff);
                    thing.stackCount = take;
                    CompQuality cq = (thing as ThingWithComps)?.TryGetComp<CompQuality>();
                    if (cq != null && item.quality >= 0)
                    {
                        cq.SetQuality((QualityCategory)item.quality, null);
                    }
                    caravan.AddPawnOrItem(thing, false);
                }
                catch (Exception ex)
                {
                    Log.WarningOnce("[RimDelegation] 缴获交货失败（" + item.def.defName + "）：" + ex.Message, 0x5E0CA1);
                    left += item.count;
                    continue;
                }

                taken += take;
                mass += unitMass * take;
                value += thing.MarketValue * Mathf.Max(1, thing.stackCount);
                free -= unitMass * take;
                if (takenInto != null)
                {
                    // S31：记一笔"实际装车的东西"，给 UI 的「战场清点」表用（lootBag 马上要被清空）
                    takenInto.Add(new DelegationLootItem
                    {
                        def = item.def,
                        stuff = item.stuff,
                        count = take,
                        quality = item.quality,
                        enemyIndex = item.enemyIndex,
                    });
                }
                if (shown.Count < 4)
                {
                    shown.Add(item.def.LabelCap + (take > 1 ? " ×" + take : ""));
                }
                if (take < item.count)
                {
                    left += item.count - take;
                }
            }

            if (taken <= 0)
            {
                return "战场上的东西一件也装不下（车队已满载），只好留在原地。";
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("缴获 {0} 件（合计 {1:0.#} kg · 市价约 {2:0} 银）：{3}",
                taken, mass, value, string.Join("、", shown.ToArray()));
            if (sorted.Count > shown.Count)
            {
                sb.Append(" 等");
            }
            if (left > 0)
            {
                sb.AppendFormat("\n还有 {0} 件装不下，留在原地。", left);
            }
            return sb.ToString();
        }

        // ================================================================ S31：打扫战场（尸骸 / 俘虏）

        /// <summary>
        /// S31：**打扫战场**（用户 2026-09-28 拍板：2B 就地屠宰、3B 尸骸进物资表、4A 无条件收倒地者、
        /// 5A 范围可配、6A 不做毁尸灭迹）。
        ///
        /// ⚠️ 只在**真结算**路径调用（`CombatSceneFactory.Build(..., keepPawns: true)`）——
        /// 那一刻 `setup.EnemyPawns` 里还留着真人；预告/评估路径早就销毁了，这里什么也拿不到。
        ///
        /// 分流（敌方下标与 `result.Units` 同序）：
        ///   · **阵亡** → 按 `CorpseCleanupMode` 三档处置（就地 `Pawn.ButcherProducts` / 造真尸体装车 / 丢弃）；
        ///   · **倒地 + non-humanlike（动物）** → S33：**补刀**，与阵亡者走同一套三档
        ///     （旧行为是"什么都不做，由 finally 销毁" ⇒ 玩家拿不到任何战果，见下面的长注释）；
        ///   · **倒地 + humanlike** → 若开启收押：`Caravan.AddPawn` 会按原版规则**自动**把非本阵营的人
        ///     `guest.CapturedBy(玩家阵营)` 成囚犯（判据 `CaravanUtility.ShouldAutoCapture`：
        ///     humanlike + 没死 + 阵营不同）——我们不自己 SetGuestStatus，免得绕过原版记账；
        ///   · **其余**（跑掉的 / 还站着的 / 炮塔）→ 什么都不做，由调用方在 finally 里销毁保留的 pawn。
        ///
        /// 副产品：`d.corpsesButchered`（尸骸数，3B 要在物资表里显示这一行）、
        /// `d.downedAnimalsDisposed`（S33：其中有多少只是补刀的倒地动物）、
        /// `d.prisonersTaken` + `d.capturedPrisoners`（俘虏，UI 用真人头像行）。
        /// </summary>
        public static void CleanupBattlefield(Delegation d, Combat.CombatSetup setup, Combat.CombatResult result,
            CorpseCleanupMode corpseMode, bool capturePrisoners)
        {
            if (d == null || setup?.EnemyPawns == null || result?.Units == null)
            {
                return;
            }
            if (d.lootBag == null)
            {
                d.lootBag = new List<DelegationLootItem>();
            }
            if (d.capturedPrisoners == null)
            {
                d.capturedPrisoners = new List<Pawn>();
            }
            if (d.pendingCorpses == null)
            {
                d.pendingCorpses = new List<Corpse>();
            }

            int enemyIndex = -1;
            for (int i = 0; i < result.Units.Count; i++)
            {
                Combat.UnitReport u = result.Units[i];
                if (u == null || u.IsMine)
                {
                    continue;
                }
                enemyIndex++;
                if (enemyIndex >= setup.EnemyPawns.Count)
                {
                    break;
                }
                Pawn p = setup.EnemyPawns[enemyIndex];
                if (p == null || p.Destroyed || p.RaceProps == null)
                {
                    continue;   // 炮塔这类没有"真人"
                }

                if (u.Dead)
                {
                    ApplyCombatGoodwill(setup.Site, p, u, d.participants);
                    DisposeEnemyCorpse(d, p, corpseMode);
                    continue;
                }

                // S33：**倒地的动物**也得有归宿（用户 2026-09-28 实测反馈：「打完两个猴子后，
                //       没有获得尸体，也没有屠宰提示」）。
                //
                // 为什么这一类一定会出现：本模型"短促战斗只产生倒地、不产生阵亡"（§19.17.5 的已知偏差）
                // —— 猴子这种小体型动物的耐久池只有 45（`0.45 × HealthPoolPerScale 100`），倒地阈值 25%
                //（11.25），而殖民者一支步枪每发约 11 点 ⇒ 几乎必然在"打到 ≤25%"那一刻先倒地，
                // 之后再也不会被打死（`Out` 之后就退出索敌了）。所以"打死的动物"是少数，
                // 绝大多数动物都以倒地收场。
                //
                // 旧行为（S31 §19.96）是"非 humanlike 的倒地动物 → 什么都不做，由 finally 销毁"，
                // 于是玩家视角就是"打赢了两只猴子，既没有尸体也没有屠宰提示"——战果凭空蒸发。
                // 收押语义对动物不成立（`CaravanUtility.ShouldAutoCapture` 只认 humanlike），
                // 所以倒地的动物按**补刀**处置，与阵亡者**走同一套**尸体三档
                //（立刻处理 = 原版屠宰口径；带走 = 造真尸体装车；丢弃 = 什么都不做）。
                if (u.Downed && !p.RaceProps.Humanlike)
                {
                    ApplyCombatGoodwill(setup.Site, p, u, d.participants);
                    DisposeEnemyCorpse(d, p, corpseMode);
                    if (corpseMode != CorpseCleanupMode.Discard)
                    {
                        d.downedAnimalsDisposed++;
                    }
                    continue;
                }

                if (u.Downed && capturePrisoners && p.RaceProps.Humanlike && !p.Dead)
                {
                    ApplyCombatGoodwill(setup.Site, p, u, d.participants);
                    if (CaptureInto(d.caravan, p))
                    {
                        d.capturedPrisoners.Add(p);
                        d.prisonersTaken++;
                    }
                }
            }
        }

        /// <summary>
        /// 「伤了人家的人，就得赔人家的关系」—— 复用原版自己的算式，不发明第二套（准则②）。
        ///
        /// 反编译依据（RimWorld 1.6 / MVID 61e41735…）：
        ///   `Faction.Notify_MemberTookDamage(Pawn member, DamageInfo dinfo)` 里唯一的好感算式是
        ///       goodwillChange = (int)(-1.3f × Mathf.Min(100f, dinfo.Amount))
        ///   前置条件：`dinfo.Instigator.Faction == Faction.OfPlayer`、`dinfo.Def.ExternalViolenceFor(member)`、
        ///   双方**尚未敌对**（`!this.HostileTo(instigator.Faction)` ⇒ 海盗/机械族天然不扣好感）、
        ///   成员不在狂暴／越狱／奴隶／任务帮手状态（我们生成的守军都不沾这几条）。
        ///
        /// ⚠️ 为什么**不走** `Pawn.Kill → HomeFaction.Notify_MemberDied`：那条路的好感分支被
        ///   `map != null &amp;&amp; map.IsPlayerHome` 卡死（反编译原样）—— 本系统的抽象战斗**没有地图**，
        ///   事件点也不是玩家主基地 ⇒ 那条分支永远不会跑。所以"伤害"这一条才是没有地图时
        ///   唯一既能算出好感变化、又仍然是原版算法的路。
        ///
        /// 近似（同时写在玩家可见处：`DelegationWorker_WorkSite.InspectWarning`）：
        ///   抽象战斗不逐发子弹结算，这里按**本模型的耐久损失**折算成**一次**伤害事件
        ///   （`(1 − HealthFractionEnd) × HealthPoolPerScale(100)`）再交给上面的原版算式。
        ///   为什么一次就够：原版算式把单次伤害封顶在 100，而打死一个人（humanlike 耐久池 =
        ///   `100 × healthScale`）累计伤害也就在 100 上下 ⇒ 一次 100 点与"五发各 20 点"得到同一个数。
        ///
        /// 已知不适用：**打输了撤退**的那条路不走 `CleanupBattlefield`，所以"只挂彩没打赢"这一趟
        /// 不扣好感（原版会扣）。
        /// </summary>
        public static void ApplyCombatGoodwill(Site site, Pawn enemy, Combat.UnitReport u, List<Pawn> ourPawns)
        {
            if (site?.Faction == null || enemy == null || u == null)
            {
                return;
            }
            Faction enemyFaction = enemy.Faction;
            Faction player = Faction.OfPlayer;
            if (enemyFaction == null || player == null || enemyFaction == player)
            {
                return;
            }

            Pawn instigator = FirstPlayerPawn(ourPawns);
            if (instigator == null)
            {
                return;   // 没有可以当"动手的人"的殖民者 ⇒ 原版算式也无从谈起
            }

            float lost = 1f - Mathf.Clamp01(u.HealthFractionEnd);
            float amount = Mathf.Max(1f, lost * 100f);
            try
            {
                // 具名传参是必须的：这个 ctor 的第 3/4 个位置参数是 armorPenetration 与 angle（都是 float），
                // 第 5 个才是 instigator（反编译签名：DamageInfo 的 14 参 ctor，其余参数都有默认值）。
                // `intendedTarget` 刻意留空 ⇒ 原版 `Faction.IsMutuallyHostileCrossfire` 直接返回 false，
                // 不会去碰"谁打谁"那几条需要地图的分支。
                DamageInfo dinfo = new DamageInfo(DamageDefOf.Bullet, amount, instigator: instigator);
                enemyFaction.Notify_MemberTookDamage(enemy, dinfo);
            }
            catch (Exception ex)
            {
                // 与 §19.14① 同一个未验证区（对未 spawn 的 pawn 走原版通知链），必须兜住：
                // 一次委派结算不能因为"赔关系"这种附加效果把游戏打崩。
                Log.WarningOnce("[RimDelegation] 结算派系好感失败（原版 Notify_MemberTookDamage）：" + ex, 0x5E0CB1);
            }
        }

        /// <summary>车队侧第一个能代表"动手方"的殖民者（原版算式要求 instigator.Faction == 玩家派系）。</summary>
        private static Pawn FirstPlayerPawn(List<Pawn> pawns)
        {
            if (pawns == null)
            {
                return null;
            }
            Faction player = Faction.OfPlayer;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p != null && !p.Dead && p.Faction == player)
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>
        /// S32/S33：把一具"已经归我们处置"的敌方躯体按 <see cref="CorpseCleanupMode" /> 落地。
        ///
        /// 两类躯体共用它：**阵亡者**（战斗模型判死）与**倒地的动物**（S33 补刀）。
        /// 两者在原版语义上都是"可以屠宰的东西"，区别只是前者＝尸体、后者＝活体，
        /// 而下面两条原版路径**都接受活体**（反编译确认）：
        ///   · `Pawn.ButcherProducts(butcher, 1f)` —— 原版屠宰/屠宰台走的就是它，只校验
        ///     `RaceProps.meatDef` / `leatherDef`（外加动物专属部位），**不校验死亡**；
        ///   · `Pawn.MakeCorpse(assignedGrave, inBed, bedRotation)` —— 只校验 `holdingOwner == null`
        ///     与 `RaceProps.corpseDef != null`，同样不校验死亡（它由本类的 S32 路径先造尸体再 `SetDead`）。
        /// </summary>
        private static void DisposeEnemyCorpse(Delegation d, Pawn p, CorpseCleanupMode corpseMode)
        {
            if (corpseMode == CorpseCleanupMode.ButcherHere)
            {
                ButcherInPlace(d, p);
            }
            else if (corpseMode == CorpseCleanupMode.HaulHome)
            {
                MakeCorpseForHaul(d, p);
            }
            // Discard：什么都不做（那具躯体由调用方的 finally 销毁）
        }

        /// <summary>
        /// S32「带走」档：把这名阵亡者变成**一具真尸体**放进待搬队列（回家自己上屠宰台）。
        ///
        /// ⚠️ 顺序与手法都是刻意的：
        ///   ① 先 `Pawn.MakeCorpse`（public，且**不需要地图/settle**：只校验"人不在容器里"+ 有 corpseDef）；
        ///   ② 再 `health.SetDead()` 标记死亡 —— 而**不是** `Pawn.Kill`：
        ///      反编译确认 `Kill` 对"未 spawn、非世界 pawn、不在容器"的人会**自己再造一具尸体**
        ///      （那个返回值拿不到 ⇒ 白造一具泄漏，还可能把 InnerPawn 的归属搅乱）；
        ///      `SetDead()` 只改 `healthState`，干净（反编译：只有一句 `healthState = Dead`）。
        ///   ③ `timeOfDeath` 要自己填：`MakeCorpse` 不管它，而它决定原版那 2.5 天开始腐烂。
        ///
        /// S33：**倒地的动物**走的是同一条路（它 `!Dead`，正是第 ② 步要落位的那种情况）。
        /// </summary>
        private static void MakeCorpseForHaul(Delegation d, Pawn p)
        {
            try
            {
                Corpse corpse = p.MakeCorpse(null, false, 0f);
                if (corpse == null)
                {
                    return;   // 没有 corpseDef（机械族之类）⇒ 没有尸体可带
                }
                if (!p.Dead)
                {
                    p.health.SetDead();
                }
                corpse.timeOfDeath = GenTicks.TicksAbs;
                d.pendingCorpses.Add(corpse);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 造尸体失败（" + (p.kindDef?.defName ?? "?") + "）：" + ex.Message, 0x5E0CA5);
            }
        }

        /// <summary>
        /// S32：把待搬的尸体装车（"能装多少装多少"）。返回带走的具数/总重；**装不下或没搬的当场销毁**
        /// （丢弃），并且**无论成败都清空队列**（与 `lootBag` 同一条防刷规矩）。
        /// </summary>
        public static void TakeCorpses(Caravan caravan, List<Corpse> pending, Delegation d,
            out int hauled, out float mass)
        {
            hauled = 0;
            mass = 0f;
            if (pending.NullOrEmpty())
            {
                return;
            }
            float free = caravan != null && !caravan.Destroyed
                ? Mathf.Max(0f, caravan.MassCapacity - caravan.MassUsage)
                : 0f;
            for (int i = 0; i < pending.Count; i++)
            {
                Corpse corpse = pending[i];
                if (corpse == null)
                {
                    continue;
                }
                float w = 0f;
                try
                {
                    w = corpse.GetStatValue(StatDefOf.Mass);
                }
                catch (Exception)
                {
                    w = 0f;
                }
                bool taken = false;
                if (caravan != null && !caravan.Destroyed && w <= free + 0.0001f)
                {
                    try
                    {
                        caravan.AddPawnOrItem(corpse, false);
                        taken = !corpse.Destroyed;
                    }
                    catch (Exception ex)
                    {
                        Log.WarningOnce("[RimDelegation] 尸骸装车失败：" + ex.Message, 0x5E0CA6);
                    }
                }
                if (taken)
                {
                    hauled++;
                    mass += w;
                    free -= w;
                }
                else if (!corpse.Destroyed)
                {
                    corpse.Destroy();   // 丢弃（原版 Destroy 会连内部 pawn 一起收尾）
                }
            }
            pending.Clear();
            if (d != null)
            {
                d.corpsesHauled += hauled;
                d.corpsesHauledMass += mass;
            }
        }

        /// <summary>
        /// S32：委派**结束/中断**时的收尾 —— 把"还没搬走的东西"丢掉。
        ///
        /// 为什么必须做：待搬的**尸骸是真尸体**（`Corpse` 里裹着那个 pawn）。队伍没走到搜集段就散了的话，
        /// 这两份队列会随委派对象一起变成垃圾 —— 不写进存档（委派已经没了）但也没人收尾。
        /// 原版 `Corpse.Destroy` 会连内部 pawn 一起处理，所以这一步是干净的。
        /// </summary>
        public static void DiscardPendingHaul(Delegation d)
        {
            if (d == null)
            {
                return;
            }
            if (!d.pendingCorpses.NullOrEmpty())
            {
                for (int i = 0; i < d.pendingCorpses.Count; i++)
                {
                    Corpse c = d.pendingCorpses[i];
                    if (c != null && !c.Destroyed)
                    {
                        c.Destroy();
                    }
                }
                d.pendingCorpses.Clear();
            }
            if (d.lootBag != null)
            {
                d.lootBag.Clear();
            }
        }

        /// <summary>
        /// 就地宰掉一具敌方躯体（阵亡者，或 S33 起补刀的倒地动物），把产物**记成账**
        /// （不直接造实物 —— 那些要等「搜集战利品」段按载重闸门搬，否则等于绕过载重、一口气全收）。
        /// </summary>
        private static void ButcherInPlace(Delegation d, Pawn p)
        {
            Pawn butcher = null;
            if (d.participants != null)
            {
                for (int i = 0; i < d.participants.Count; i++)
                {
                    Pawn c = d.participants[i];
                    if (c != null && !c.Dead && !c.Downed)
                    {
                        butcher = c;
                        break;
                    }
                }
            }

            int kinds = 0;
            try
            {
                // 效率恒为 1：本项目的"屠宰技能"还没有建模（与段耗时挂钩技能同一期待办），
                // 不假装按技能打折。人肉的食人/心情规则由原版产物自己带（我们没绕过它）。
                foreach (Thing t in p.ButcherProducts(butcher, 1f))
                {
                    if (t == null || t.def == null || t.stackCount <= 0)
                    {
                        continue;
                    }
                    d.lootBag.Add(new DelegationLootItem
                    {
                        def = t.def,
                        stuff = t.Stuff,
                        count = Mathf.Max(1, t.stackCount),
                        quality = -1,   // 屠宰产物没有品质
                        enemyIndex = -1,
                    });
                    kinds++;
                    t.Destroy();   // 只是账：实物在搜集段再造，免得同一批东西拿两遍
                }
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 就地屠宰失败（" + (p.kindDef?.defName ?? "?") + "）：" + ex.Message, 0x5E0CA2);
                return;
            }
            if (kinds > 0 || p.RaceProps.corpseDef != null)
            {
                d.corpsesButchered++;
            }
        }

        /// <summary>
        /// 把一名倒地的守军收押进车队（原版路径：`Caravan.AddPawn` 内部会自动 `CapturedBy`）。
        ///
        /// ⚠️ 先 `PassToWorld`：这些 pawn 是"造出来从没 spawn、也从没登记进 WorldPawns"的临时对象
        /// （见 `ThreatRosterFactory.Discard` 的注释），直接塞进车队会留下一个存档里也不认识的幽灵成员。
        /// </summary>
        private static bool CaptureInto(Caravan caravan, Pawn p)
        {
            if (caravan == null || caravan.Destroyed)
            {
                return false;
            }
            try
            {
                Find.WorldPawns.PassToWorld(p);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 把守军登记进世界失败（仍尝试收押）：" + ex.Message, 0x5E0CA3);
            }
            try
            {
                caravan.AddPawn(p, false);
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 收押失败：" + ex);
                return false;
            }
            if (!caravan.ContainsPawn(p))
            {
                Log.WarningOnce("[RimDelegation] 车队没有接收这名俘虏（" + p.LabelShortCap + "）", 0x5E0CA4);
                return false;
            }
            return true;
        }
    }
}
