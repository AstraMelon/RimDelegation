using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 物资藏匿点委派：不生成地图，直接在现场清点并装车。
    ///
    /// ── 为什么可以"不切图" ────────────────────────────────────────────────
    /// `SitePartWorker_ItemStash.Notify_GeneratedByQuestGen` 在**任务生成那一刻**就把实物
    /// 塞进了 `SitePart.things`（`ThingOwner&lt;Thing&gt;`，`dontTickContents = true`），
    /// 而 `SitePart.ExposeData` 用 `Scribe_Deep.Look(ref things, "things", this)` 存它
    /// ⇒ 不解包地图就能读到，也能存读档。
    ///
    /// `GenStep_ItemStash.ScatterAt` 的取值优先级是
    /// `parms.sitePart.things` → `ItemStashContentsComp.contents` → `thingSetMakerDef ?? MapGen_DefaultStockpile`。
    /// 本 worker 按**同一个优先级**取，所以"取走多少、地图生成时就少多少"，两边不会打架：
    ///   · 取走一部分 → 进图时 BaseGen 用的是剩下的那份，天然一致；
    ///   · 全部取走   → `part.things` 空掉，此时进图会**重掷**一份全新物资
    ///     ⇒ 所以 `destroyTargetOnComplete = true` 在这里不是可选项（防双吃）。
    /// S14 起这两条退居**兜底**：Def 上 `<blockMapEntryAfterWorked>true</blockMapEntryAfterWorked>`
    /// 让"装过车的地点"整体封禁进入（详见 `WorldObjectComp_Delegations.ApplyEntryBlock`），
    /// 玩家不再有"委派搬一半、自己进去搬剩下"这条第二条路。
    ///
    /// ── 进度单位 = 1 件 ──────────────────────────────────────────────────
    /// 复用宿主的 `totalCells / cellsMined / oreDelivered`（宿主零改动就能判"取空 → Complete → site.Destroy()"）。
    /// 物件是**离散**的，"件"是唯一精确的整数单位；改用 kg 会因为 `totalCells` 是 int
    /// 而出现"收尾余量永远凑不满"的死锁。
    ///
    /// ── 速率口径：按负重 ─────────────────────────────────────────────────
    /// 每个作业 tick 先算这一队人的**搬运质量预算**：
    ///     预算(kg) = Σ 可作业者 `MassUtility.Capacity(pawn)` × <see cref="HaulTripsPerWorkHour"/>
    ///                × (delta / 2500) × 模式系数
    /// 再把预算依次"花"在现场的物件上 —— **件数由质量决定，不由件数决定**：
    /// 壮劳力/驮兽多 → 每小时搬走的质量多 → 重货搬得慢、轻货搬得快。
    /// 预算不够一件时**跨 tick 攒着**（<see cref="Delegation.haulCarryOverKg"/>），
    /// 而**不是**搞"每 tick 至少搬一件"那种会让速率彻底失效的兜底（那是 60 件/秒）。
    ///
    /// ── 车队负重是真实的收工理由 ─────────────────────────────────────────
    /// 装车会真的增加 `Caravan.MassUsage`（`AddPawnOrItem` → 队员库存 → `CollectionsMassCalculator`）。
    /// 剩余空间装不下任何一件时不再清点，<see cref="WorkerEndReason"/> 报告"车队已装满"，
    /// 宿主据此收工 —— **没取空 ⇒ 地点不销毁、原版失效计时恢复** ⇒
    /// 剩下的物资留在原地，玩家卸完货可以回来接着委派（`deposit` 跨多次委派累计，不会重掷）。
    /// S14 起该地点同时被封禁进入 ⇒ 剩下的**只能**再派一次委派来取，不能自己进图搬
    /// （用户拍板的口径：事件点被委派动过之后就只走委派路线）。
    ///
    /// ── 与矿点不同的两点 ─────────────────────────────────────────────────
    /// ① 预览是**精确值**：实物本来就在存档里，不需要等 GenStep 掷（见 <see cref="MakePreview"/>）。
    /// ② <see cref="AllowsScaleIncrease"/> 必须保持 false（继承基类）：规模 = 现场实际件数，
    ///    被"富矿脉"那类事件 +n 会让 `cellsMined` 永远追不上 `totalCells`。
    /// </summary>
    public class DelegationWorker_TakeItemStash : DelegationWorker
    {
        /// <summary>
        /// 每人每**作业小时**能完成的"装卸趟数"。
        ///
        /// 一个"趟" = 把 `MassUtility.Capacity(pawn)` 从藏匿处搬上车队并卸下。
        /// 原版 `MassUtility.Capacity(p) = p.BodySize × 35f`，人类 BodySize 1.0 ⇒ **35 kg/趟**。
        ///
        /// 为什么是"数小时一趟"而不是"几分钟一趟"：物资藏匿点是 `GenStep_ItemStash` 生成的
        /// **7×7 密室**（`CellRect.CenteredOn(c, 7, 7)`，通常是结构内部的库房），
        /// 要找到、要破门、要清点、要打包 —— 不是"弯腰从地上捡起来"。
        ///
        /// 取 0.25（= 4 小时一趟）时的量纲（单人，35 kg/趟）：
        ///   正常模式（8h/天）= 35 × 0.25 × 8 = **70 kg/作业天**
        ///   ⇒ 60 kg 的小藏匿点 ≈ 0.9 天；200 kg ≈ 2.9 天；4 个人 ≈ 1/4。
        /// 想调手感只改这一个常量，量纲（kg/作业小时）不变。
        /// </summary>
        public const float HaulTripsPerWorkHour = 0.25f;

        /// <summary>
        /// 本次委派实际生效的"每人每作业小时趟数"。
        ///
        /// 基类 = <see cref="HaulTripsPerWorkHour" />（0.25，= 4 小时一趟，按 7×7 密室标定）。
        /// 为什么做成虚属性：2026-09-30 用户拍板 —— **工作站点单开一档 0.5**。
        /// 营地是开阔场地、储物堆就铺在地上，没有"要找到、要破门、要清点、要打包"这几道工序；
        /// 沿用 0.25 会让伐木点搬得离谱地慢（实测 603 kg 木头，单人正常模式要 8.6 个作业天）。
        /// 常量本身**保持 0.25 不动**：它是"密室"那一档的事实规格，不许被悄悄改掉。
        /// </summary>
        public virtual float TripsPerWorkHour => HaulTripsPerWorkHour;

        /// <summary>搬运预算余数的上限（kg）。防止现场清空后余数无限累积。</summary>
        public const float MaxCarryOverKg = 500f;

        /// <summary>
        /// `DelegationDef.skillDef` **填了才生效**的 XP 系数（每 tick、每人）。
        ///
        /// 原版搬运不给经验，所以默认不填 skillDef ⇒ 一分 XP 都不给（这是正确的原版行为）。
        /// 想给的话在 def 里填 skillDef 即可；0.035 取采矿系数（0.07）的一半，
        /// 因为搬运的技术含量低于采矿，但没有原版公式可抄 —— 这是个 `[建议]` 值。
        /// </summary>
        public const float XpPerTick = 0.035f;

        /// <summary>进度单位是"件"（不是矿点那种"格"）—— 宿主与对话框的量纲文案靠它。</summary>
        public override string UnitName => "件";

        /// <summary>动词是"搬"（不是"采"）。</summary>
        public override string WorkVerb => "搬";

        /// <summary>句子里的活动名：远行队信息栏会说"委派搜刮中"而不是"委派开采中"。</summary>
        public override string ActivityName => "搜刮";

        /// <summary>产出量纲也是"件"（物资点搬一件就是一件产出 —— 这里两者同值是必然，不是巧合）。</summary>
        public override string OutputUnitName => "件";

        /// <summary>状态行 Keyed 键后缀（`RimDelegationCaravanDelegating_Search` / `RimDelegationPawnWorking_Search`）。</summary>
        public override string StatusKeySuffix => "Search";

        /// <summary>
        /// 读不到清单时，按原版口径掷骰用的市价上限（银）。
        ///
        /// `[验证 + 近似]` 原版的掷骰点是 `SymbolResolver_Stockpile.Resolve` 的 else 分支：
        ///     totalMarketValueRange = rp.stockpileMarketValue ?? Mathf.Min(cells.Count * 130f, 1800f)
        /// 其中 `cells.Count` 是 BaseGen 房间里"可站立且没东西"的格数 —— **要地图生成完才知道**。
        /// 唯一能确定的是那个 1800 上限：7×7 密室的格数在 25~49 之间，
        /// 而 cells ≥ 14 时 `cells × 130 ≥ 1800`，所以**绝大多数情况就是 1800**。
        /// 这里直接取上限，并在预览文案里把这条近似写给玩家看（见 <see cref="PreviewLabel"/>）。
        /// </summary>
        public const float StashMarketValueCap = 1800f;

        // ── 目标定位 ────────────────────────────────────────────────────────

        /// <summary>
        /// 现场物资容器。按原版 GenStep 的同一优先级：
        /// ① 事件点上标签匹配的 `SitePart.things`（任务生成路径，绝大多数情况）；
        /// ② `ItemStashContentsComp.contents`（其它路径，如小行星/轨道物资点）。
        /// 都没有 → null。
        /// </summary>
        public static ThingOwner LootOwner(Site site, DelegationDef def)
        {
            if (site?.parts != null)
            {
                for (int i = 0; i < site.parts.Count; i++)
                {
                    SitePart part = site.parts[i];
                    if (part?.things == null || !part.things.Any)
                    {
                        continue;
                    }
                    if (!PartMatches(def, part))
                    {
                        continue;
                    }
                    return part.things;
                }
            }
            ItemStashContentsComp comp = site?.GetComponent<ItemStashContentsComp>();
            if (comp?.contents != null && comp.contents.Any)
            {
                return comp.contents;
            }
            return null;
        }

        /// <summary>该 SitePart 是不是这条委派 def 声明的目标（语义同 DelegationUtility.Matches）。</summary>
        private static bool PartMatches(DelegationDef def, SitePart part)
        {
            if (part?.def == null)
            {
                return false;
            }
            if (def == null)
            {
                return true;
            }
            if (!def.targetSitePartDefs.NullOrEmpty() && def.targetSitePartDefs.Contains(part.def))
            {
                return true;
            }
            if (!def.targetSitePartTags.NullOrEmpty() && !part.def.tags.NullOrEmpty())
            {
                for (int i = 0; i < part.def.tags.Count; i++)
                {
                    if (def.targetSitePartTags.Contains(part.def.tags[i]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>这条 def 声明的那个 SitePart（**不管**它有没有实物）。没有就返回 null。</summary>
        public static SitePart TargetPart(Site site, DelegationDef def)
        {
            if (site?.parts == null)
            {
                return null;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (part?.def != null && PartMatches(def, part))
                {
                    return part;
                }
            }
            return null;
        }

        /// <summary>
        /// 现场清单是不是**读不到**（= 原版要等生成地图时才掷）。
        ///
        /// 判据就是原版 `GenStep_ItemStash.ScatterAt` 的两级回退：
        ///   ① `parms.sitePart.things` 有实物 → 用它；
        ///   ② 否则 `ItemStashContentsComp.contents` 有实物 → 用它；
        ///   ③ 都没有 → 用 `thingSetMakerDef ?? MapGen_DefaultStockpile` **现掷**。
        /// 所以"读不到"的准确含义是①②都拿不到东西 —— 此时原版那一份还不存在。
        ///
        /// ⚠️ 这条与"这个点是不是任务生成的"是两件事，但对委派而言**结果一样**：
        ///    · 任务生成的点：清单在 `SitePartWorker_ItemStash.Notify_GeneratedByQuestGen` 里就写好了 ⇒ 读得到；
        ///    · 其它来源的点（或任务生成时 `points = 0` 掷出空清单）：`part.things` 为空/为 null ⇒ 读不到。
        /// </summary>
        public static bool ContentsUnreadable(Site site, DelegationDef def)
        {
            return LootOwner(site, def) == null;
        }

        /// <summary>
        /// 该 SitePartDef 上挂的 `GenStep_ItemStash`。
        ///
        /// `[验证]` 挂载方式是 `GenStepDef.linkWithSite`（类型是 **SitePartDef 引用**，
        /// 不是字符串），而 `SitePartDef.ExtraGenSteps` 正是"遍历全部 GenStepDef 取 linkWithSite == this"
        /// 的索引 —— 于是不需要自己去扫 DefDatabase（矿点那边用的是 `GenStepDefOf.PreciousLump` 直取）。
        /// </summary>
        private static GenStep_ItemStash FindItemStashGenStep(SitePartDef partDef)
        {
            List<GenStepDef> steps = partDef?.ExtraGenSteps;
            if (steps == null)
            {
                return null;
            }
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i]?.genStep is GenStep_ItemStash gs)
                {
                    return gs;
                }
            }
            return null;
        }

        /// <summary>
        /// 读不到清单时，按**原版同一口径**把这份清单掷出来并**写回 `SitePart.things`**。
        ///
        /// ── 为什么必须写回（这是全篇最关键的一步）─────────────────────────
        /// 写回之后，`GenStep_ItemStash.ScatterAt` 的第一优先级（`parms.sitePart.things`）就命中了，
        /// 于是**无论**玩家继续委派还是进图，看到的都是同一份清单 ——
        /// 不存在"委派一份、进图再掷一份"的双吃（挖矿那两次独立掷骰的处境在这里根本不存在）。
        ///
        /// ── 口径 ────────────────────────────────────────────────────
        /// ThingSetMaker 取该 SitePartDef 自己的 `GenStep_ItemStash.thingSetMakerDef`
        /// （Core 的 `ItemStash` 是 null ⇒ 回退 `MapGen_DefaultStockpile`，与 GenStep 完全一致）。
        /// 参数照抄 `SymbolResolver_Stockpile` 的 else 分支：techLevel / makingFaction 取地点所属派系，
        /// totalMarketValueRange 见 <see cref="StashMarketValueCap"/> 的说明（唯一一处近似）。
        /// </summary>
        public static bool TryRollMissingContents(Site site, DelegationDef def, out string report)
        {
            report = null;
            SitePart part = TargetPart(site, def);
            if (part == null)
            {
                report = "这个地点上没有匹配的 SitePart";
                return false;
            }

            ThingSetMakerDef maker = FindItemStashGenStep(part.def)?.thingSetMakerDef
                                     ?? ThingSetMakerDefOf.MapGen_DefaultStockpile;
            if (maker?.root == null)
            {
                report = "既没有 thingSetMakerDef 也没有 MapGen_DefaultStockpile，无法掷定清单";
                return false;
            }

            Faction faction = site.Faction;
            ThingSetMakerParams parms = new ThingSetMakerParams
            {
                techLevel = faction?.def?.techLevel ?? TechLevel.Undefined,
                makingFaction = faction,
                totalMarketValueRange = new FloatRange(StashMarketValueCap, StashMarketValueCap)
            };
            List<Thing> list = maker.root.Generate(parms);
            if (list.NullOrEmpty())
            {
                report = $"{maker.defName} 没有产出任何物资（市价上限 {StashMarketValueCap:0} 银）";
                return false;
            }

            // 与原版 Notify_GeneratedByQuestGen 逐字同构：new ThingOwner<Thing>(part, oneStackOnly: false)
            // + dontTickContents = true（纯静态库存，不需要 tick）+ TryAddRangeOrTransfer。
            if (part.things == null)
            {
                part.things = new ThingOwner<Thing>(part, oneStackOnly: false) { dontTickContents = true };
            }
            part.things.TryAddRangeOrTransfer(list, canMergeWithExistingStacks: false);
            if (!part.things.Any)
            {
                report = "掷出了清单但没能放进 SitePart.things";
                return false;
            }
            report = string.Format("{0} 项 / 市价约 {1:0} 银（{2}）",
                part.things.Count, GenThing.GetMarketValue(list), maker.defName);
            return true;
        }

        // ── 度量 ────────────────────────────────────────────────────────────

        private static bool Usable(Thing t)
        {
            return t != null && !t.Destroyed && t.def != null && t.stackCount > 0;
        }

        /// <summary>
        /// 把容器内容快照成 `List&lt;Thing&gt;`。
        ///
        /// 刻意**不用** `InnerListForReading`：那个属性在泛型 `ThingOwner&lt;T&gt;` 上，
        /// 而非泛型的 `ThingOwner`（`SitePart.things` 与 `ItemStashContentsComp.contents`
        /// 的声明类型）只保证 `IEnumerable&lt;Thing&gt;`。
        /// 快照还有个额外好处：后面 `Take` 会改动容器，先快照就不会在迭代中被改。
        /// </summary>
        private static List<Thing> Snapshot(ThingOwner owner)
        {
            List<Thing> list = new List<Thing>();
            if (owner == null)
            {
                return list;
            }
            foreach (Thing t in owner)
            {
                list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// 一个物件的质量（kg）= `ThingDef.BaseMass`（就是 `GetStatValueAbstract(StatDefOf.Mass)`）
        /// × `stackCount`。
        ///
        /// 刻意**不用** `thing.GetStatValue(StatDefOf.Mass)`：那个会跑
        /// `StatPart_GearAndInventoryMass` / `StatPart_AddedBodyPartsMass` 这些面向
        /// 已 spawn 的 Pawn 的部件，而事件点上的物资是未 spawn 的裸 Thing。
        /// </summary>
        public static float MassOf(Thing t)
        {
            return t?.def == null ? 0f : t.def.BaseMass * t.stackCount;
        }

        public static int CountUsable(ThingOwner owner)
        {
            if (owner == null)
            {
                return 0;
            }
            List<Thing> list = Snapshot(owner);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (Usable(list[i]))
                {
                    n++;
                }
            }
            return n;
        }

        public static float TotalMass(ThingOwner owner)
        {
            if (owner == null)
            {
                return 0f;
            }
            List<Thing> list = Snapshot(owner);
            float kg = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                if (Usable(list[i]))
                {
                    kg += MassOf(list[i]);
                }
            }
            return kg;
        }

        /// <summary>现场市价合计。用原版同一把尺子（`GenThing.GetMarketValue`，`SitePartWorker_ItemStash` 也用它）。</summary>
        public static float TotalValue(ThingOwner owner)
        {
            return owner == null ? 0f : GenThing.GetMarketValue(Snapshot(owner));
        }

        /// <summary>现场最轻的那件（kg）。判断"车队还能不能再装一件"用。</summary>
        private static float LightestMass(ThingOwner owner)
        {
            if (owner == null)
            {
                return 0f;
            }
            List<Thing> list = Snapshot(owner);
            float lightest = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                if (!Usable(list[i]))
                {
                    continue;
                }
                float m = MassOf(list[i]);
                if (m < lightest)
                {
                    lightest = m;
                }
            }
            return lightest == float.MaxValue ? 0f : lightest;
        }

        /// <summary>市价最高的那件（当按钮图标与预览的"代表物"）。</summary>
        public static Thing HighestValueThing(Site site, DelegationDef def)
        {
            ThingOwner owner = LootOwner(site, def);
            if (owner == null)
            {
                return null;
            }
            List<Thing> list = Snapshot(owner);
            Thing best = null;
            float bestValue = -1f;
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                if (!Usable(t))
                {
                    continue;
                }
                if (t.MarketValue > bestValue)
                {
                    bestValue = t.MarketValue;
                    best = t;
                }
            }
            return best;
        }

        /// <summary>一队人的负重上限合计（kg）。倒地者不计产能，恢复后会自动回来。</summary>
        public static float TotalCapacity(List<Pawn> pawns)
        {
            if (pawns == null)
            {
                return 0f;
            }
            float cap = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || p.Downed)
                {
                    continue;
                }
                cap += MassUtility.Capacity(p, null);
            }
            return cap;
        }

        /// <summary>这一队人本 tick 能搬的质量（kg）。</summary>
        public static float MassBudget(Delegation d, int delta,
            float tripsPerWorkHour = HaulTripsPerWorkHour)
        {
            if (d?.participants == null || delta <= 0)
            {
                return 0f;
            }
            float capacity = TotalCapacity(d.participants);
            if (capacity <= 0f)
            {
                return 0f;
            }
            float hours = delta / (float)Delegation.TicksPerHour;
            float multiplier = d.mode?.workRateMultiplier ?? 1f;
            return capacity * tripsPerWorkHour * hours * multiplier;
        }

        /// <summary>这一队人一天能搬几件（含工时占比与模式系数）。avgItemMass = 0 时无法估算。</summary>
        public static float ItemsPerDay(List<Pawn> pawns, DelegationModeDef mode, float avgItemMass,
            float tripsPerWorkHour = HaulTripsPerWorkHour)
        {
            if (mode == null || avgItemMass <= 0f)
            {
                return 0f;
            }
            float capacity = TotalCapacity(pawns);
            if (capacity <= 0f)
            {
                return 0f;
            }
            float hoursPerDay = 24f * mode.WorkFractionPerDay;
            float kgPerDay = capacity * tripsPerWorkHour * hoursPerDay * mode.workRateMultiplier;
            return kgPerDay / avgItemMass;
        }

        /// <summary>事件点上物件的平均质量（kg）。deposit 里存的 rolledUnits 就是总质量。</summary>
        public static float AverageItemMass(DelegationDeposit dep)
        {
            if (dep == null || dep.totalUnits <= 0)
            {
                return 0f;
            }
            return Mathf.Max(0f, dep.rolledUnits) / dep.totalUnits;
        }

        /// <summary>现场（抵达前预览用）的物件平均质量。</summary>
        private static float AverageMassOnSite(Site site, DelegationDef def)
        {
            ThingOwner owner = LootOwner(site, def);
            int count = CountUsable(owner);
            return count <= 0 ? 0f : TotalMass(owner) / count;
        }

        // ── worker 接口 ─────────────────────────────────────────────────────

        /// <summary>
        /// 只在事件点上调用一次：把"现场有什么"抄进事件点存量。
        ///
        /// 与矿点的关键差别：这里**不掷骰**（实物本来就在存档里）—— **除非读不到**。
        /// 读不到（`part.things` 与 `ItemStashContentsComp.contents` 都拿不到东西）时，
        /// 原版本来就要等到生成地图才用 `MapGen_DefaultStockpile` 现掷；
        /// 此时委派按**同一口径**掷一份并写回 `SitePart.things`（见 <see cref="TryRollMissingContents"/>），
        /// 于是"掷出来的是什么，进图也是什么"，两边共用一份。
        ///
        /// 为什么不"读不到就当 0"：`totalUnits = 0` ⇒ `totalCells = 0` ⇒
        /// `TargetDepleted` 恒为假、`Tick` 直接 return ⇒ 委派 0/0 空转、永不完成、永不销毁，
        /// 而玩家看到的是"现场没有可搬运的物资"这句**假话**（清单根本还没掷过）。
        /// </summary>
        public override void RollDeposit(DelegationDeposit dep, Site site)
        {
            if (dep == null)
            {
                return;
            }
            if (ContentsUnreadable(site, def))
            {
                bool rolled = TryRollMissingContents(site, def, out string report);
                dep.workerRolledContents = rolled;
                DelegationUtility.LogVerbose(rolled
                    ? $"物资点现场清单读不到 ⇒ 委派按原版口径掷定并写回：{site?.Label} → {report}"
                    : $"物资点现场清单读不到且掷定失败（按空处理）：{site?.Label} → {report}");
            }
            ThingOwner owner = LootOwner(site, def);
            dep.resourceDef = HighestValueThing(site, def)?.def;
            dep.totalUnits = CountUsable(owner);      // 件数
            dep.rolledUnits = TotalMass(owner);       // 总质量（kg）—— worker 语义下的"原始掷值"
            dep.yieldPerUnit = 1;                     // 一件进一件
        }

        public override void OnStart(Delegation d, Site site)
        {
            DelegationDeposit dep = d?.deposit;
            if (dep == null)
            {
                return;
            }
            d.resourceDef = dep.resourceDef;
            d.yieldPerCell = 1;
            d.totalCells = dep.UnitsRemaining;        // 本次只能搬"这个点还剩下"的件数
            dep.timesDelegated++;
        }

        public override void Tick(Delegation d, Site site, int delta)
        {
            if (d == null || d.deposit == null || d.totalCells <= 0 || delta <= 0)
            {
                return;
            }
            Caravan caravan = d.caravan;
            if (caravan == null || caravan.Destroyed)
            {
                return;
            }

            ThingOwner owner = LootOwner(site, def);
            if (owner == null)
            {
                // 现场已经没有任何物资了。若存量快照还说有剩（被别的来源取走 / 快照偏大），
                // 以实物为准补齐进度，让宿主走正常的"取空 → 销毁地点"路径，而不是就地空转。
                if (d.oreDelivered < d.totalCells)
                {
                    DelegationUtility.LogVerbose(
                        $"[RimDelegation] 物资点实物提前清空：{site?.Label}，{d.oreDelivered}/{d.totalCells} 件，按实物为准收工");
                    d.cellsMined = d.totalCells;
                }
                return;
            }

            int quota = d.totalCells - Mathf.FloorToInt(d.cellsMined);
            if (quota <= 0)
            {
                return;
            }

            // 本 tick 可用的质量预算 = 本 tick 产出 + 上一次攒下的余数
            float budget = MassBudget(d, delta, TripsPerWorkHour) + Mathf.Max(0f, d.haulCarryOverKg);

            // 车队还能装多少（kg）
            float freeSpace = Mathf.Max(0f, caravan.MassCapacity - caravan.MassUsage);

            List<Thing> list = Snapshot(owner);
            List<Thing> batch = new List<Thing>();
            for (int i = 0; i < list.Count && batch.Count < quota; i++)
            {
                Thing t = list[i];
                if (!Usable(t))
                {
                    continue;
                }
                float mass = MassOf(t);
                // 这件现在搬不动或者装不下 —— 跳过它去看后面的轻货，
                // 别让队首的一件重货把整队人堵住。
                if (mass > budget || mass > freeSpace)
                {
                    continue;
                }
                budget -= mass;
                freeSpace -= mass;
                batch.Add(t);
            }

            // 余数跨 tick 累积（离散物件必须这样攒，否则就只能"每 tick 一件"而速率失效）
            d.haulCarryOverKg = Mathf.Clamp(budget, 0f, MaxCarryOverKg);

            if (batch.Count == 0)
            {
                return;
            }

            int loaded = 0;
            float loadedKg = 0f;
            for (int i = 0; i < batch.Count; i++)
            {
                Thing t = batch[i];
                float mass = MassOf(t);
                Thing taken = owner.Take(t);
                if (taken == null)
                {
                    continue;
                }
                if (!LoadIntoCaravan(caravan, taken))
                {
                    owner.TryAdd(taken);   // 装车失败就放回现场，绝不能把东西弄丢
                    continue;
                }
                loaded++;
                loadedKg += mass;
            }
            if (loaded <= 0)
            {
                return;
            }

            // 三处账要一起走：委派进度、交付统计、事件点累计（跨委派）
            d.cellsMined += loaded;
            d.oreDelivered += loaded;
            d.rolledValue += loadedKg;                 // worker 语义：本次委派已搬走的质量（kg）
            d.deposit.unitsMined += loaded;
            d.deposit.unitsDelivered += loaded;

            if (!owner.Any)
            {
                d.haulCarryOverKg = 0f;                // 现场空了，余数没有意义
            }

            // 技能经验：默认 skillDef 为空 ⇒ 不给（原版搬运不给经验）。
            if (def.skillDef != null)
            {
                float coefficient = XpPerTick * delta * (d.mode?.workRateMultiplier ?? 1f);
                for (int i = 0; i < d.participants.Count; i++)
                {
                    Pawn p = d.participants[i];
                    if (p?.skills == null || p.Dead)
                    {
                        continue;
                    }
                    p.skills.Learn(def.skillDef, coefficient);
                }
            }
        }

        /// <summary>
        /// 本 worker 是"边搬边装车"：<see cref="Tick"/> 里已经把物件直接交给车队了，
        /// 所以没有"待交付缓冲"，这里刻意什么都不做（`d.oreUnits` 恒为 0）。
        /// 好处是**中断天然等于"已搬走的都在车上"**，不需要额外结算。
        /// </summary>
        public override int FlushDeliveries(Delegation d, Caravan caravan)
        {
            return 0;
        }

        /// <summary>
        /// 装不下下一件了就收工。**这是负重口径真正产生决策的地方**：
        /// 没取空 ⇒ 地点不销毁、原版失效计时恢复 ⇒ 剩下的留原地，卸完货可以回来接着搬。
        /// </summary>
        public override string WorkerEndReason(Delegation d, Site site)
        {
            if (d?.caravan == null || d.deposit == null)
            {
                return null;
            }
            ThingOwner owner = LootOwner(site, def);
            if (owner == null || !owner.Any)
            {
                return null;   // 现场空了 —— 交给宿主的 TargetDepleted 判定
            }

            float free = d.caravan.MassCapacity - d.caravan.MassUsage;
            float lightest = LightestMass(owner);
            // 还有空间装下至少一件 → 继续干
            if (free > 0f && (lightest <= 0f || free >= lightest))
            {
                return null;
            }

            // 一件都搬过、且连一个作业小时都没干满的时候不报 ——
            // 免得"车队落地就已经超重"时立刻弹一封什么都没干的完成信。
            if (d.oreDelivered <= 0 && d.ticksWorked < Delegation.TicksPerHour)
            {
                return null;
            }

            return string.Format(
                "远行队已装满（{0:0.#}/{1:0.#} kg），剩下的物资留在原地，卸货后可再来委派",
                d.caravan.MassUsage, d.caravan.MassCapacity);
        }

        public override Texture2D GetGizmoIcon(Site site)
        {
            // 用现场最值钱那件东西的图标：数据驱动、保证存在、主题也对
            return HighestValueThing(site, def)?.def?.uiIcon;
        }

        public override float EstimatedUnitsPerDay(Delegation d, PlanetTile tile)
        {
            return d == null ? 0f : ItemsPerDay(d.participants, d.mode, AverageItemMass(d.deposit),
                TripsPerWorkHour);
        }

        public override float EstimateUnitsPerDayFor(List<Pawn> pawns, DelegationModeDef mode, PlanetTile tile, Site site = null)
        {
            return ItemsPerDay(pawns, mode, AverageMassOnSite(site, def), TripsPerWorkHour);
        }

        /// <summary>
        /// 物资点的预览是**精确值**、不是区间：实物本来就在存档里，不用等 GenStep 掷。
        ///
        /// 例外：现场读不到实物时（原版要到生成地图时才掷），这里**给 0 而不是编一个数**，
        /// 由 <see cref="PreviewLabel"/> 如实说"内容未定"—— 对话框底部也据此显示
        /// <see cref="EstimateUnavailableReason"/> 而不是那句误导的"模式或人员缺失"。
        /// </summary>
        public override DelegationPreview MakePreview(Site site)
        {
            ThingOwner owner = LootOwner(site, def);
            int count = CountUsable(owner);
            float totalMass = TotalMass(owner);
            return new DelegationPreview
            {
                resourceDef = HighestValueThing(site, def)?.def,
                minUnits = count,
                maxUnits = count,
                yieldPerUnit = 1,
                massPerUnit = count > 0 ? totalMass / count : 0f
            };
        }

        /// <summary>
        /// 对话框底部"估算不出来"时给玩家看的原因。
        ///
        /// 默认那句"无法估算（模式或人员缺失）"在物资点上是**误导**：
        /// 算不出来的真实原因是这份清单还没掷（人跟模式都在）。
        /// </summary>
        public override string EstimateUnavailableReason(Site site, DelegationPreview preview, DelegationDeposit deposit)
        {
            if (deposit == null && ContentsUnreadable(site, def))
            {
                return "未知：藏匿点物资需待队伍侦察确认。";
            }
            return null;
        }

        /// <summary>
        /// 现场清单的三态文案。**必须分开说**，因为这三件事完全不同：
        ///   ① 有实物        → 精确值（件数 / kg / 市价）
        ///   ② 读不到、还没清点 → "内容未定"（原版要到生成地图时才掷；这是**不确定**，不是"没有"）
        ///   ③ 已经清点/掷定过（exactDeposit 非空）、现场为空 → 这才叫"现场没有可搬运的物资"
        ///
        /// 改之前只有 ①③ 两态，②被并进③ ⇒ 玩家在还没抵达时被告知"现场没有可搬运的物资"，
        /// 而真相是"清单还没掷"。用户报的"物品没有正常生成"正是被这句话误导的直接后果。
        /// </summary>
        public override string PreviewLabel(Site site, DelegationPreview preview, DelegationDeposit exactDeposit)
        {
            ThingOwner owner = LootOwner(site, def);
            if (owner != null && owner.Any)
            {
                string s = string.Format("现场 {0} 件物资 · 合计 {1:0.#} kg · 市价约 {2:0} 银（精确值）",
                    CountUsable(owner), TotalMass(owner), TotalValue(owner));
                if (exactDeposit != null && exactDeposit.HasBeenWorked)
                {
                    s += string.Format("\n此事件点已被委派 {0} 次，累计搬走 {1} 件（存量跨委派累计，不会重掷）",
                        exactDeposit.timesDelegated, exactDeposit.unitsDelivered);
                }
                return s;
            }

            // ② 还没清点过：如实说"不确定"，并交代我们会怎么处理
            if (exactDeposit == null)
            {
                return "现场情报未知：目标区域尚未勘察，藏匿点内物资的种类与数量需待队伍抵达后确认。\n" +
                       "委派下达后，队伍抵达时将按市价上限（" +
                       string.Format("{0:0}", StashMarketValueCap) + "银）进行估值与登记——清点结果即为实际获取物资。";
            }

            // ③ 已经清点过（exactDeposit 非空 = 抵达过），现场确实是空的
            return exactDeposit.workerRolledContents
                ? "现场没有可搬运的物资：委派已经清点过这一处，确实是空的。"
                : "现场没有可搬运的物资：这一处本来就是空的。";
        }

        public override string PawnDetail(Pawn p)
        {
            if (p == null)
            {
                return null;
            }
            float cap = MassUtility.Capacity(p, null);
            return string.Format("负重上限 {0:0.#} kg · 可搬 {1:0.#} kg/作业小时",
                cap, cap * TripsPerWorkHour);
        }

        /// <summary>
        /// 「预期获得」列表（S6）：**按 ThingDef 归并**。
        ///
        /// 为什么要归并：藏匿点是 7×7 密室，里面常常是几十堆同类物资（`SitePart.things` 里
        /// 每堆一个 Thing），逐堆画会刷出几十行同名条目 —— 那不是"清单"，是噪音。
        /// 排序按市价降序：玩家最关心"最值钱的是什么"。
        ///
        /// 清单读不到时返回 null（而不是空列表）：UI 据此**不画**这一块，
        /// 由 <see cref="PreviewLabel"/> 的三态文案去解释"内容未定"。
        /// </summary>
        public override List<DelegationPreviewItem> PreviewItems(Site site, DelegationPreview preview,
            DelegationDeposit exactDeposit)
        {
            // S22 门控：`DelegationDef.hideItemsUntilPhase` 一配，本条委派的「预期获得」就**只在破门之后**才给看。
            // 而"预期获得"这个视图天生只出现在**开工前**（草稿 / 旧对话框）⇒ 门控在这里等价于"永不在开工前剧透"。
            // 用户口径：「这部分需要完成破门才给玩家看预期获得」。
            if (ItemsHiddenByPhase(null))
            {
                return null;
            }
            return GroupOwner(LootOwner(site, def), false);
        }

        /// <summary>
        /// 作业进行中"现场还剩什么"（S7 修的那条："本次进度里看不到物资具体是什么"）。
        ///
        /// 直接复用同一份归并逻辑读**现场当前**的内容 —— 搬走一件就少一件，
        /// 所以页签上看到的就是"还没搬走的那几类"，与进度条天然一致（不需要另记一份账）。
        ///
        /// S9：`forProgress = true` ⇒ 明细里不再写"×N 件"（UI 会用台账算出
        /// 「已获取 3/12 件」写在行首，再写一遍件数就成重复信息了）。
        /// </summary>
        public override List<DelegationPreviewItem> ProgressItems(Delegation d, Site site)
        {
            // S22：同一道门控 —— 破门之前，连"现场还剩什么"也不给看（人还在门外）。
            if (ItemsHiddenByPhase(d))
            {
                return null;
            }
            return GroupOwner(LootOwner(site, def), true);
        }

        /// <summary>
        /// S22：清单是不是还被流程门控挡着（`DelegationDef.hideItemsUntilPhase` 指定的那一段**还没走完**）。
        ///
        /// `d == null`（开工前的草稿 / 旧对话框）⇒ 视为"还没到"，一律挡着。
        /// 没配这个字段 ⇒ 一律放行（行为与加字段之前逐字一致）。
        /// </summary>
        private bool ItemsHiddenByPhase(Delegation d)
        {
            string gate = def?.hideItemsUntilPhase;
            if (gate.NullOrEmpty())
            {
                return false;
            }
            return !DelegationFlow.For(def).PhaseDone(d?.flow, gate);
        }

        /// <summary>
        /// 把现场容器按 ThingDef 归并成列表行（按市价降序）。
        ///
        /// <paramref name="forProgress" /> = true 时是"作业期还剩什么"（明细不带件数，
        /// 件数由 UI 按台账写成"已获取 X/Y 件"），false 时是"预期获得"（件数必须写在明细里，
        /// 因为那一块没有台账可依）。
        /// </summary>
        private static List<DelegationPreviewItem> GroupOwner(ThingOwner owner, bool forProgress)
        {
            if (owner == null || !owner.Any)
            {
                return null;
            }

            List<Thing> things = Snapshot(owner);
            List<DelegationPreviewItem> items = new List<DelegationPreviewItem>();
            Dictionary<ThingDef, int> indexOf = new Dictionary<ThingDef, int>();
            List<int> counts = new List<int>();
            List<float> masses = new List<float>();
            List<float> values = new List<float>();

            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (!Usable(t))
                {
                    continue;
                }
                int idx;
                if (!indexOf.TryGetValue(t.def, out idx))
                {
                    idx = items.Count;
                    indexOf[t.def] = idx;
                    items.Add(new DelegationPreviewItem
                    {
                        thingDef = t.def,
                        label = t.def.LabelCap.ToString(),
                        unitLabel = "件",
                        value = 0f
                    });
                    counts.Add(0);
                    masses.Add(0f);
                    values.Add(0f);
                }
                counts[idx] += t.stackCount;
                masses[idx] += MassOf(t);
                values[idx] += t.MarketValue * t.stackCount;
            }

            if (items.Count == 0)
            {
                return null;
            }

            // 冒泡式按市价降序（条目数通常是 10 以内，不值得为它引一个比较器分配）
            for (int i = 0; i < items.Count - 1; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (values[j] <= values[i])
                    {
                        continue;
                    }
                    Swap(items, i, j);
                    Swap(counts, i, j);
                    Swap(masses, i, j);
                    Swap(values, i, j);
                }
            }

            for (int i = 0; i < items.Count; i++)
            {
                items[i].count = counts[i];
                items[i].mass = masses[i];
                items[i].detail = forProgress
                    ? string.Format("{0:0.#} kg · 市价约 {1:0} 银", masses[i], values[i])
                    : string.Format("×{0} 件 · {1:0.#} kg · 市价约 {2:0} 银", counts[i], masses[i], values[i]);
                items[i].value = values[i];
            }
            return items;
        }

        private static void Swap<T>(List<T> list, int a, int b)
        {
            T tmp = list[a];
            list[a] = list[b];
            list[b] = tmp;
        }

        public override string ProgressLabel(Delegation d)
        {
            if (d == null)
            {
                return null;
            }
            float percent = d.cellsMined / Mathf.Max(1, d.totalCells) * 100f;
            string s = string.Format("已搬走 {0}/{1} 件（{2:0.#}%）", d.oreDelivered, d.totalCells, percent);
            if (d.rolledValue > 0f)
            {
                s += string.Format(" · 累计 {0:0.#} kg", d.rolledValue);
            }
            if (d.caravan != null)
            {
                s += string.Format(" · 远行队负重 {0:0.#}/{1:0.#} kg", d.caravan.MassUsage, d.caravan.MassCapacity);
            }
            if (d.haulCarryOverKg >= 0.1f)
            {
                // 这行是解释"为什么这一件搬得慢"的唯一线索，不能省
                s += string.Format("\n正在搬下一件：已攒搬运预算 {0:0.#} kg", d.haulCarryOverKg);
            }
            return s;
        }

        public override string DeliverySummary(Delegation d)
        {
            if (d == null || d.oreDelivered <= 0)
            {
                return "本次未搬走任何物资";
            }
            return string.Format("已搬走 {0} 件物资、合计 {1:0.#} kg", d.oreDelivered, d.rolledValue);
        }

        // ── 工具 ────────────────────────────────────────────────────────────

        private static bool LoadIntoCaravan(Caravan caravan, Thing t)
        {
            try
            {
                // 与原版 Caravan.AddPawnOrItem 的内部行为一致：
                // 物品走 CaravanInventoryUtility.GiveThing（含负重与人份分配），Pawn 走 AddPawn。
                caravan.AddPawnOrItem(t, false);
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[RimDelegation] 物资装车失败（物品已放回事件点）：" + ex, 0x5E0C7);
                return false;
            }
        }
    }
}
