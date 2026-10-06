using System;
using System.Collections.Generic;
using System.Text;
using RimDelegation.Combat;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「效果原语」（S23）：一件事的最小可组合单位。
    ///
    /// 为什么要有它：在此之前，"有机制的事件"每加一条就要新写一个 <see cref="DelegationEventDef" />
    /// 子类（塌方 / 富矿脉 / 受挫 / 闹别扭 / 工伤 五个类各一套 <c>Apply</c>）—— 而写 C# 的代价不是
    /// 多写几行，是**退游戏 → 编译 → 部署 → 重启**（Hot Reload mod 只热重载 Def，不重载程序集）。
    /// 拆成原语之后，"塌方"变成 XML 里两条原语的组合，改完存盘就能看效果。
    ///
    /// 为什么**不是 Def**：本类要写成 `List<DelegationEffectDef>` 的内联元素
    /// （`<li Class="RimDelegation.DelegationEffectDef_Stall"><hours>1~4</hours></li>`）。
    /// `Class=` 内联多态这个写法本项目已经在用（`Patches/RimDelegation_SiteComps.xml` 给 Site 追加
    /// `<li Class="RimDelegation.WorldObjectCompProperties_Delegations" />`），而 Def 类型的列表元素
    /// 走的是"按 defName 查表"那条路 —— 混在一起会变成"内联出来的 Def 不进 DefDatabase"。
    /// 所以原语与 `CompProperties` 同类：普通对象、无 defName、可以内联，也可以被多条事件共用一份实例。
    ///
    /// 复用点：<see cref="DelegationEventDef.effects" />（随机事件）与
    /// <see cref="DelegationPhaseDef.onEnter" />（段进入钩子，交战段就是一条 `Effect_ResolveCombat`）。
    /// </summary>
    public abstract class DelegationEffectDef
    {
        /// <summary>
        /// 结果文案模板（占位符含义见各子类的注释）。空 = 用子类内置的兜底句。
        /// **一条效果说一件事**：多条效果的文字由宿主按顺序拼（各自自带句末标点）。
        /// </summary>
        public string resultText;

        /// <summary>执行这件事，返回给玩家看的那句话（null = 这件事没有可见结果）。</summary>
        public abstract string Apply(Delegation d, Site site);

        /// <summary>按模板格式化；模板没写或写坏（占位符数量不匹配）时退回兜底句，绝不抛异常。</summary>
        protected string Format(string fallback, params object[] args)
        {
            string t = resultText.NullOrEmpty() ? fallback : resultText;
            if (t.NullOrEmpty())
            {
                return null;
            }
            try
            {
                return string.Format(t, args);
            }
            catch (FormatException)
            {
                return t;   // 玩家改 XML 时手滑写坏了占位符 —— 显示原文，别把事件炸掉
            }
        }
    }

    /// <summary>
    /// 停摆：作业被打断一段时间。停摆**照样消耗"按天数收工"的预算**（§19.24 的时间代价）。
    ///
    /// 占位符：{0} = 小时数。
    /// </summary>
    public class DelegationEffectDef_Stall : DelegationEffectDef
    {
        public FloatRange hours = new FloatRange(1f, 4f);

        public override string Apply(Delegation d, Site site)
        {
            if (d == null)
            {
                return null;
            }
            float h = hours.RandomInRange;
            d.StallFor(h);
            return Format("作业停摆了 {0} 小时。", h.ToString("0.#"));
        }
    }

    /// <summary>
    /// 损失**已采但尚未交付**的矿石（塌方的机制部分）。交付过的部分不受影响 ——
    /// 那是车队背上的东西，塌方埋不掉。
    ///
    /// 占位符：{0} = 损失单位数。
    /// </summary>
    public class DelegationEffectDef_LoseOre : DelegationEffectDef
    {
        public FloatRange fraction = new FloatRange(0.2f, 0.5f);

        /// <summary>至少损失这么多（≥1 ⇒ 事件不会空转；存量不足时按存量取）。</summary>
        public int minUnits = 1;

        public override string Apply(Delegation d, Site site)
        {
            if (d == null || d.oreUnits <= 0f)
            {
                return null;
            }
            int lost = Mathf.FloorToInt(d.oreUnits * fraction.RandomInRange);
            if (lost < minUnits)
            {
                lost = Mathf.Min(minUnits, Mathf.FloorToInt(d.oreUnits));
            }
            if (lost <= 0)
            {
                return null;
            }
            d.oreUnits = Mathf.Max(0f, d.oreUnits - lost);
            return Format("损失了 {0} 单位已经采出、还没运回车上的矿石。", lost);
        }
    }

    /// <summary>
    /// 发现富矿脉：事件点总规模变大（原 <c>DelegationEventDef_BonusYield</c> 的机制部分，公式逐字保留）。
    ///
    /// **加多少必须看矿种**（§19.24.7）：① 按矿点总格数的比例；② 按稀有度压制；
    /// ③ 按价值封顶。固定加格数会给黄金/零部件矿凭空灌进几千银。
    ///
    /// ⚠️ 只能作用于"目标规模本来就是掷定出来的抽象量"的委派（`DelegationWorker.AllowsScaleIncrease`）
    /// —— 对"规模 = 现场实际件数"的委派，凭空加规模会让 `cellsMined` 永远追不上 `totalCells`。
    /// 这条结构不变量在 <c>Apply</c> 里再查一次（DEV 按钮会绕过静态闸门）。
    ///
    /// 占位符：{0} = 矿名，{1} = 加了几格，{2} = 原来的格数，{3} = 尾注（折算价值 / 顺手采出）。
    /// </summary>
    public class DelegationEffectDef_GainScale : DelegationEffectDef
    {
        /// <summary>相对**该矿点总格数**的加成比例。</summary>
        public FloatRange bonusFraction = new FloatRange(0.15f, 0.35f);

        /// <summary>无论怎么算，至少加这么多格（保证事件不空转）。</summary>
        public int minBonusUnits = 1;

        /// <summary>按价值封顶（银两）。0 = 不封顶。</summary>
        public float maxBonusValue = 1200f;

        /// <summary>"常见矿"的参考单价 —— 用它做稀有度基准。钢铁 = 1.9。</summary>
        public float rarityReferenceValue = 1.9f;

        /// <summary>稀有度压制系数的下限。</summary>
        public float minRarityScale = 0.25f;

        /// <summary>顺手采出一部分（占加成的比例），让玩家立刻看到产出。</summary>
        public float immediateFraction = 0.25f;

        public override string Apply(Delegation d, Site site)
        {
            if (d == null || d.deposit == null || !(d.Worker?.AllowsScaleIncrease ?? false))
            {
                return "（这一处的规模本来就不是能变多的东西，事件对它没有效果 —— 已忽略）";
            }

            ThingDef mineable = d.resourceDef;
            ThingDef ore = mineable?.building?.mineableThing;
            float oreValue = ore != null ? Mathf.Max(0.01f, ore.BaseMarketValue) : 0f;
            int perCellYield = Mathf.Max(0, d.yieldPerCell);

            int totalCells = Mathf.Max(1, d.deposit.totalUnits);

            // ① 按比例
            int n = Mathf.RoundToInt(totalCells * bonusFraction.RandomInRange);

            // ② 按稀有度压制
            float rarityScale = 1f;
            if (oreValue > 0f && rarityReferenceValue > 0f)
            {
                rarityScale = Mathf.Clamp(rarityReferenceValue / oreValue, minRarityScale, 1f);
                n = Mathf.RoundToInt(n * rarityScale);
            }

            // ③ 按价值封顶
            float perCellValue = perCellYield * oreValue;
            if (maxBonusValue > 0f && perCellValue > 0f)
            {
                int valueCap = Mathf.FloorToInt(maxBonusValue / perCellValue);
                if (valueCap < n) n = valueCap;
            }

            n = Mathf.Max(minBonusUnits, n);

            d.deposit.totalUnits += n;
            d.deposit.rolledUnits += n;   // 必须同步，否则 UI 的"剩余/总数"对不上
            // ★ 关键：`d.totalCells` 是开工时从 UnitsRemaining 抓的快照，不跟着加的话
            //   新露出来的矿脉当场谁也采不到，"要挖更久"就成了空话。
            if (d.totalCells > 0) d.totalCells += n;

            float immediate = 0f;
            if (immediateFraction > 0f)
            {
                immediate = n * immediateFraction;
                d.oreUnits += immediate;
            }

            string oreName = ore != null ? ore.LabelCap.ToString() : "矿石";
            StringBuilder tail = new StringBuilder();
            if (oreValue > 0f)
            {
                tail.Append("，折算约 ").Append((n * perCellValue).ToString("0")).Append(" 银");
            }
            if (immediate > 0f)
            {
                tail.Append("，并已顺手采出一部分");
            }
            tail.Append("。");

            string main = Format("塌落的岩层后面露出一条更富的 {0} 矿脉：事件点总规模 +{1} 格（原 {2} 格）{3}",
                oreName, n, totalCells, tail.ToString());
            // 尾注解释数字怎么来的（玩家可见的透明性，§19.24.7）—— 模板只覆盖主句，这一行始终跟着
            return main + "\n（加成按矿点规模的 " + bonusFraction.ToString() + " 计算，" +
                   "再按稀有度 ×" + rarityScale.ToString("0.##") + " 压制，" +
                   (maxBonusValue > 0f ? "并按 " + maxBonusValue.ToString("0") + " 银封顶）" : "不封顶）");
        }
    }

    /// <summary>
    /// 给参与者挂一条记忆型心情（原 <c>DelegationEventDef_Mood</c> 的机制部分）。
    ///
    /// 占位符：{0} = 受影响人数。
    /// </summary>
    public class DelegationEffectDef_Thought : DelegationEffectDef
    {
        public ThoughtDef thought;

        public override string Apply(Delegation d, Site site)
        {
            if (d == null || thought == null || d.participants == null)
            {
                return null;
            }
            int n = 0;
            List<Pawn> list = d.participants;
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == null || p.Dead) continue;
                if (p.needs?.mood?.thoughts?.memories != null)
                {
                    p.needs.mood.thoughts.memories.TryGainMemory(thought);
                    n++;
                }
            }
            return n > 0 ? Format("队伍里为这事闹了别扭，{0} 个人心情受影响。", n) : null;
        }
    }

    /// <summary>
    /// 真的对参与者施加伤害（原 <c>DelegationEventDef_PawnAccident</c> 的机制部分）。
    ///
    /// 伤害随疲劳放大（§19.25）—— 这是长工时唯一的生理代价（车队里原版疲劳心情不生效）。
    /// 概率那一半在条件原语 <see cref="DelegationConditionDef_FatigueMultiplier" /> 里。
    ///
    /// `resultText` 是**开头那句**（默认"作业时出了事故："），后面自动接伤亡明细与疲劳注脚。
    /// </summary>
    public class DelegationEffectDef_Damage : DelegationEffectDef
    {
        public DamageDef damageDef;
        public FloatRange damageAmount = new FloatRange(6f, 12f);
        public IntRange victims = new IntRange(1, 1);

        /// <summary>全队平均休息低于这个水平才开始放大伤害。</summary>
        public float fatigueOnsetRest = 0.7f;

        /// <summary>伤害量随疲劳放大的比例（0.5 ⇒ 疲劳满时伤害 ×1.5）。0 = 不放大。</summary>
        public float damageFatigueScale = 0.5f;

        public override string Apply(Delegation d, Site site)
        {
            if (d == null)
            {
                return null;
            }
            List<Pawn> pool = new List<Pawn>();
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                if (p != null && !p.Dead && !p.Downed) pool.Add(p);
            }
            if (pool.Count == 0) return null;

            float fatigue = DelegationUtility.FatigueFactor(d, fatigueOnsetRest);
            float damageScale = 1f + damageFatigueScale * fatigue;

            int n = Mathf.Clamp(victims.RandomInRange, 0, pool.Count);
            DamageDef def = damageDef ?? DamageDefOf.Blunt;

            StringBuilder sb = new StringBuilder();
            sb.Append(resultText.NullOrEmpty() ? "作业时出了事故：" : resultText);
            for (int i = 0; i < n; i++)
            {
                Pawn p = pool.RandomElement();
                pool.Remove(p);
                float amount = damageAmount.RandomInRange * damageScale;
                if (i > 0) sb.Append("、");

                // ⚠️ "无地图施加伤害"的实测点（DESIGN §19.14 第 1 项）：兜住异常，
                //    不能让一次随机事件把游戏打崩。
                try
                {
                    p.TakeDamage(new DamageInfo(def, amount));
                    sb.Append(p.LabelShort).Append(" 受伤（").Append(amount.ToString("0.#")).Append(" 点 ").Append(def.label).Append("）");
                }
                catch (Exception ex)
                {
                    sb.Append(p.LabelShort).Append(" 受伤失败（").Append(ex.GetType().Name).Append("）");
                    Log.WarningOnce("[RimDelegation] 无地图施加伤害失败（§19.14①）：" + ex, 0x5E0C3);
                }
            }
            if (fatigue > 0.01f)
            {
                sb.Append("\n（全队平均休息 ").Append(DelegationUtility.AverageRest(d).ToStringPercent())
                  .Append("，疲劳让这起事故的伤害 ×").Append(damageScale.ToString("0.##")).Append("）");
            }
            return sb.ToString();
        }
    }

    /// <summary>只写一句话的效果（不给机制、只给叙事；事件也可以直接写 <c>resultText</c>）。</summary>
    public class DelegationEffectDef_Text : DelegationEffectDef
    {
        public override string Apply(Delegation d, Site site)
        {
            return resultText.NullOrEmpty() ? null : resultText;
        }
    }

    /// <summary>
    /// **交战**（S23）：把"这一趟打了一场"这件事真的跑一遍模拟战斗并落到参与者身上。
    ///
    /// 为什么是"段进入时一次结算"而不是每 tick 推进：模拟战斗本身是一次性求解
    /// （`CombatSimulator.Simulate`），所在段的 1 小时是**叙事耗时**（交火、清点、包扎），
    /// 不是"战斗模型跑了一小时"。这一点与营救的清场完全同款（`RescueUtility.ResolveClearance`）。
    ///
    /// 与"预告即契约"（§19.12）的关系：这里用的 `CombatSceneFactory.Build(caravan, site, penalty)`
    /// 与威胁评估面板里那份**是同一份战场**（同样的参与者、装备、天气、先手折扣），
    /// 所以面板上算出来的胜率分布与实际结算同源。
    ///
    /// 打输时（用户 2026-09-27 拍板）：**中止委派**，带着伤员撤 —— 与营救一致，
    /// 不是"完成"，所以走 <see cref="Delegation.flowAbortReason" /> 而不是收尾流程。
    ///
    /// 占位符：{0} = 战斗摘要，{1} = 伤亡明细（没有则不占位时留空）。
    /// </summary>
    public class DelegationEffectDef_ResolveCombat : DelegationEffectDef
    {
        /// <summary>是否计入当前作战姿态的"守军先手一轮"折扣（没有姿态时为 0）。</summary>
        public bool useApproachFirstStrike = true;

        /// <summary>打输是否中止委派。默认 true（用户 2026-09-27 拍板）。</summary>
        public bool abortOnDefeat = true;

        public override string Apply(Delegation d, Site site)
        {
            if (d == null || site == null || d.caravan == null)
            {
                return null;
            }

            // RIM-27 归一：判据只有 DelegationApproachDef.FirstStrikePenalty 一份
            // （旧写法漏了 `stealth` 条件 ⇒ 与主列/对话框/营救结算三处互不相同）。
            // `useApproachFirstStrike` 仍是 Def 级的功能开关（默认 true），不是第二份判据。
            float firstStrike = useApproachFirstStrike
                ? DelegationApproachDef.FirstStrikePenalty(d.approach)
                : 0f;

            CombatSetup setup = CombatSceneFactory.Build(d.caravan, site, firstStrike, d.noCombatPawns, keepPawns: true);
            if (!setup.CanAssess)
            {
                // 抽象模型兜不住（例如 Mechanoid 集群）：不装作打赢，也不静默通过 —— 直接中止
                d.flowAbortReason = "此地存在无法无地图评估的守军（需进入地图清剿）：" + setup.BlockReason();
                setup.DestroyUnusedPawns();
                return "战斗无法在这套模型里推演，队伍原地待命。";
            }

            int seed = Rand.RangeInclusive(1, 999999);
            d.combatSeed = seed;   // RIM-30：战斗种子有独立字段；rolledValue 只留给 worker 的"已搬 kg"语义

            CombatResult result;
            try
            {
                using (RandRng rng = new RandRng(seed))
                {
                    result = CombatSimulator.Simulate(setup.Scene, rng);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 委派交战结算失败：" + ex);
                setup.DestroyUnusedPawns();   // S31：保留的真 pawn 必须收尾，绝不泄漏
                d.flowAbortReason = "交战结算失败（" + ex.GetType().Name + "）";
                return null;
            }

            // S30：把**打掉的那些守军**身上的东西抄成缴获清单（真 pawn 此刻早就销毁了，
            // 清单是在 `ThreatRosterFactory` 里趁销毁前抄下来的，见 `CombatSetup.EnemyLoot`）。
            // 交给「搜集战利品」段真正搬上车 —— 那一段排在交战段之后、建立营地之前（用户 2026-09-28 拍板）。
            // S31：这一步也受"打扫战场范围"设置管（5A）。
            //
            // ⚠️ 顺序：**先**装备、**后**打扫战场 —— 打扫战场会把就地屠宰的肉/皮 `Add` 进同一个 `lootBag`，
            //    反过来写的话这行装备赋值会把那批产物整个盖掉（本轮自己踩过一次）。
            RimDelegationSettings cfg = RimDelegationMod.Settings;
            d.lootBag = (cfg == null || cfg.cleanupTakeEquipment)
                ? DelegationUtility.CaptureLoot(setup, result)
                : new List<DelegationLootItem>();

            // S31：**打扫战场**（用户 2026-09-28 拍板 2B/4A/5A）——就地屠宰阵亡者、收押倒地的守军。
            // 放在这里而不是「搜集战利品」段：尸体/俘虏都要**真 pawn**，而他们只在这一刻还活着；
            // 宰出来的肉皮只是**记账**（并进上面的 lootBag），仍然由那一段按载重闸门搬
            //（所以载重限制没有被绕过）。
            try
            {
                DelegationUtility.CleanupBattlefield(d, setup, result,
                    cfg?.corpseCleanup ?? CorpseCleanupMode.ButcherHere,
                    cfg == null || cfg.cleanupCapturePrisoners);
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 打扫战场失败：" + ex);
            }
            finally
            {
                setup.DestroyUnusedPawns();   // 没被收编的临时 pawn 一律销毁（幂等）
            }

            StringBuilder casualties = new StringBuilder();
            RescueUtility.ApplyCasualties(result, d.participants, d.def?.casualtiesArePermanent ?? true, casualties);

            d.flowCombatResult = result.Summary();
            if (result.ThreatCleared)
            {
                return Format("交战结束（{0}）{1}", result.Summary(),
                    casualties.Length > 0 ? "\n" + casualties.ToString().TrimEnd() : "");
            }

            string loss = "交火失利（" + result.Outcome + "），远行队带着伤员撤了。" +
                          (casualties.Length > 0 ? "\n" + casualties.ToString().TrimEnd() : "");
            if (abortOnDefeat)
            {
                d.flowAbortReason = loss;
            }
            return loss;
        }
    }

    /// <summary>
    /// S30：**搜集战利品**（用户 2026-09-28 拍板：「搜集战利品」段位置 —— A 单开一段，
    /// 插在交战段之后、建立营地之前）。
    ///
    /// 与「交战」段同一个套路：段**进入那一刻**把东西一次性搬上车，段的时长（0.5h）是叙事耗时
    /// （清点、搬抬、捆扎），不是"搬了半小时"。
    ///
    /// 份量口径 = **载重闸门**：车队剩多少载重就装多少，值钱的先装，装不下的留在原地并写进说明
    /// （与物资藏匿点"装满就收工"同一个道理）。没有额外打折 —— 缴获多少由"打掉了谁"决定。
    ///
    /// 不变量：**只搬 `d.lootBag`**（交战段抄下来的那份），本效果不去读敌人 ——
    /// 那些 pawn 在交战结算时就已经销毁了。
    /// </summary>
    public class DelegationEffectDef_GainLoot : DelegationEffectDef
    {
        public override string Apply(Delegation d, Site site)
        {
            if (d?.caravan == null)
            {
                return null;
            }
            string note = DelegationUtility.TakeLoot(d.caravan, d.lootBag, d.takenRows);
            if (d.lootBag != null)
            {
                // 搬完就清空：这份清单的使命结束了（否则读档后会被再搬一次 = 刷战利品）
                d.lootBag.Clear();
            }
            // S32「带走」档：尸骸同样在这段按载重装车（装不下的当场丢弃）
            int hauled;
            float hauledMass;
            DelegationUtility.TakeCorpses(d.caravan, d.pendingCorpses, d, out hauled, out hauledMass);
            if (hauled > 0)
            {
                string corpseLine = string.Format("另带回尸骸 ×{0}（合计 {1:0.#} kg，回家可自行屠宰）", hauled, hauledMass);
                note = note.NullOrEmpty() ? corpseLine : note + "\n" + corpseLine;
            }
            return note;
        }
    }
}
