using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 矿点委派：把原版 JobDriver_Mine 的采矿速率公式搬到世界地图上。
    ///
    /// 全部常量/公式来自 1.6 Assembly-CSharp 反编译（见 DESIGN.md §6.3）：
    ///   BaseTicksBetweenPickHits = 100，自然岩石每击 80 伤害，矿格 MaxHitPoints = 1500
    ///   → 每格 19 击（18×80 = 1440 + 收尾一击打剩余 60）
    ///   MiningSpeed = 1 × (0.04 + 0.12 × 采矿等级)，最后被 Clamp 到 minValue = 0.1
    ///   ticksToPickHit = round(100 / MiningSpeed)
    ///   EffectiveMineableYield = round(mineableYield × difficulty.mineYieldFactor)
    /// </summary>
    public class DelegationWorker_Mining : DelegationWorker
    {
        /// <summary>原版 JobDriver_Mine 里每次挥镐的 XP 系数。</summary>
        public const float XpPerTick = 0.07f;

        /// <summary>1500 HP / 80 伤害 = 18 满击 + 1 收尾击。</summary>
        public const int PicksPerCell = 19;

        public const int BaseTicksBetweenPickHits = 100;

        /// <summary>
        /// 矿点的规模是 `GenStep_PreciousLump` 用价值区间（3500-5000）掷出来的**抽象格数**，
        /// 所以"富矿脉"这类 +n 格的事件对它是成立的（多出来的格由 GenStep 按同一公式生成）。
        /// </summary>
        public override bool AllowsScaleIncrease => true;

        /// <summary>每格需要的 tick 数（照抄 JobDriver_Mine.ResetTicksToPickHit）。</summary>
        public static int TicksPerCell(Pawn p)
        {
            float speed = p.GetStatValue(StatDefOf.MiningSpeed);
            int interval = (int)Math.Round(BaseTicksBetweenPickHits / speed);
            if (interval < 1)
            {
                interval = 1;
            }
            return PicksPerCell * interval;
        }

        /// <summary>
        /// 游戏内 1 小时的**作业**产出（格）。1 小时 = 2500 ticks。
        ///
        /// 为什么显示这个而不是"秒/格"：秒/格 是"一格要挥多久镐"，
        /// 数值在 5~250 秒之间跳，玩家没法拿它跟"一天能挖几格"直接对上；
        /// 「格/作业小时」和对话框底部的「格/天」是同一量纲，只差"工时占比 × 模式系数"一层。
        ///
        /// 这里是**单人基础速率**，不含 `DelegationModeDef.workRateMultiplier`
        /// （×0.9 / ×1.0 / ×1.1）—— 模式对队里每个人一视同仁，不影响横向比较，
        /// 而模式自己的系数就写在紧邻的模式行上。
        /// </summary>
        public static float CellsPerWorkHour(Pawn p)
        {
            if (p == null || p.Dead || p.Downed)
            {
                return 0f;
            }
            int ticksPerCell = TicksPerCell(p);
            return ticksPerCell <= 0 ? 0f : Delegation.TicksPerHour / (float)ticksPerCell;
        }

        /// <summary>从 SitePart.parms.preciousLumpResources 读矿种（任务生成时就已存盘，不需要生成地图）。</summary>
        public static ThingDef ResolveMineableDef(Site site)
        {
            if (site?.parts == null)
            {
                return null;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (part?.def == SitePartDefOf.PreciousLump && part.parms != null)
                {
                    return part.parms.preciousLumpResources;
                }
            }
            return null;
        }

        /// <summary>复刻 GenStep_PreciousLump 的价值区间（3500-5000）。</summary>
        public static FloatRange LumpValueRange()
        {
            if (GenStepDefOf.PreciousLump?.genStep is GenStep_PreciousLump gs)
            {
                return gs.totalValueRange;
            }
            return new FloatRange(3500f, 5000f);
        }

        /// <summary>复刻 GenStep_PreciousLump.Generate 的矿格数公式。</summary>
        public static int TotalCellsFor(float value, ThingDef mineableDef)
        {
            if (mineableDef?.building?.mineableThing == null || mineableDef.building.mineableYield <= 0)
            {
                return 1;
            }
            float denom = mineableDef.building.mineableYield * mineableDef.building.mineableThing.BaseMarketValue;
            if (denom <= 0f)
            {
                return 1;
            }
            return Mathf.Max(Mathf.RoundToInt(value / denom), 1);
        }

        public override void OnStart(Delegation d, Site site)
        {
            DelegationDeposit dep = d.deposit;
            if (dep == null)
            {
                return;
            }
            d.resourceDef = dep.resourceDef;
            d.yieldPerCell = dep.yieldPerUnit;
            d.totalCells = dep.UnitsRemaining; // 本次委派只能采"这个地点还剩下"的格数
            dep.timesDelegated++;
        }

        /// <summary>只在事件点上调用一次：掷定格数与每格产出，之后永远复用。</summary>
        public override void RollDeposit(DelegationDeposit dep, Site site)
        {
            dep.resourceDef = ResolveMineableDef(site);
            dep.rolledUnits = LumpValueRange().RandomInRange;
            dep.totalUnits = TotalCellsFor(dep.rolledUnits, dep.resourceDef);
            dep.yieldPerUnit = dep.resourceDef?.building?.EffectiveMineableYield ?? 0;
        }

        public override void Tick(Delegation d, Site site, int delta)
        {
            DelegationDeposit dep = d.deposit;
            if (dep == null || d.totalCells <= 0)
            {
                return;
            }

            float multiplier = d.mode?.workRateMultiplier ?? 1f;
            ThingDef mineableDef = d.resourceDef;
            float dropChance = mineableDef?.building?.mineableDropChance ?? 1f;
            bool wasteable = mineableDef?.building?.mineableYieldWasteable ?? true;

            float cellsThisDelta = 0f;
            float yieldWeighted = 0f; // Σ(格数 × 该矿工的 MiningYield)

            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                // 倒地只是暂时不计入产能 —— 恢复后会自动回来（不要在 comp 里把人踢出名单）
                if (p == null || p.Dead || p.Downed)
                {
                    continue;
                }
                // 每人挖自己那一格 → 并行推进。
                // MiningSpeed 只改"多久挥一镐"，不改单次伤害（JobDriver_Mine 每击固定 80）。
                float cells = (float)delta / TicksPerCell(p) * multiplier;
                cellsThisDelta += cells;
                yieldWeighted += cells * p.GetStatValue(StatDefOf.MiningYield);

                // 委派必须自己发 XP：世界 pawn 不跑 Job，JobDriver_Mine 的 Learn 不会触发
                if (def.skillDef != null && p.skills != null)
                {
                    p.skills.Learn(def.skillDef, XpPerTick * delta * multiplier);
                }
            }
            if (cellsThisDelta <= 0f)
            {
                return;
            }

            // ★ 受"这个事件点还剩多少"封顶：避免重复采集的关键一行
            float remaining = d.totalCells - d.cellsMined;
            float added = Mathf.Min(cellsThisDelta, remaining);
            if (added <= 0f)
            {
                return;
            }

            // 单格产出 = EffectiveMineableYield × MiningYield(那个矿工)，照抄
            // Mineable.Notify_TookMiningDamage 的 yieldPct 累计（总伤害 = MaxHitPoints ⇒ yieldPct = MiningYield）。
            // 多人时用本 tick 的加权均值近似（同批矿工技能相同时就是精确值）。
            float avgMiningYield = yieldWeighted / cellsThisDelta;
            float oreThisDelta = added * d.yieldPerCell * avgMiningYield * dropChance;

            if (!wasteable)
            {
                // 原版只有 mineableYieldWasteable = true 才按 yieldPct 比例出货；
                // 否则必须"整格采完"才给全量 → 用前后整格数之差折算。
                // （原版矿点都是 wasteable = true，这里是为了兼容非原版矿物）
                int before = Mathf.FloorToInt(d.cellsMined + 1E-05f);
                d.cellsMined += added;
                int after = Mathf.FloorToInt(d.cellsMined + 1E-05f);
                int newCells = Mathf.Max(0, after - before);
                oreThisDelta *= newCells / added;
            }
            else
            {
                d.cellsMined += added;
            }

            // ★ 存量扣减记在事件点上（不是记在委派上）：中止后重新委派会接着采，不会重掷
            dep.unitsMined += added;
            d.oreUnits += oreThisDelta;
        }

        public override Texture2D GetGizmoIcon(Site site)
        {
            // 用矿物的 uiIcon 当按钮图标：数据驱动、保证存在、主题也对
            return ResolveMineableDef(site)?.building?.mineableThing?.uiIcon;
        }

        public override int FlushDeliveries(Delegation d, Caravan caravan)
        {
            int amount = Mathf.FloorToInt(d.oreUnits);
            if (amount <= 0 || caravan == null)
            {
                return 0;
            }
            ThingDef thingDef = d.resourceDef?.building?.mineableThing;
            if (thingDef == null)
            {
                d.oreUnits = 0f;
                return 0;
            }
            int delivered = DelegationUtility.DeliverThingToCaravan(caravan, thingDef, amount);
            d.oreUnits -= delivered;
            d.oreDelivered += delivered;
            if (d.deposit != null)
            {
                d.deposit.unitsDelivered += delivered; // 累计到事件点上（跨委派的统计）
            }
            return delivered;
        }

        public override string DeliverySummary(Delegation d)
        {
            ThingDef thingDef = d.resourceDef?.building?.mineableThing;
            if (thingDef == null)
            {
                return "未能交付：矿种未识别";
            }
            if (d.oreDelivered <= 0)
            {
                return "本次未产出任何矿物";
            }
            float value = d.oreDelivered * thingDef.BaseMarketValue;
            return string.Format("已交付 {0} × {1}（约 {2:0} 银）", d.oreDelivered, thingDef.LabelCap, value);
        }

        /// <summary>这一队人一天能挖几格（含工时占比与模式系数）。</summary>
        public static float CellsPerDay(List<Pawn> pawns, DelegationModeDef mode, PlanetTile tile)
        {
            float perDay = 0f;
            if (pawns != null)
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    if (pawns[i] == null || pawns[i].Dead || pawns[i].Downed)
                    {
                        continue;
                    }
                    perDay += 60000f / TicksPerCell(pawns[i]);
                }
            }
            return perDay * mode.WorkFractionPerDay * mode.workRateMultiplier;
        }

        public override float EstimatedUnitsPerDay(Delegation d, PlanetTile tile)
        {
            return CellsPerDay(d.participants, d.mode, tile);
        }

        public override float EstimateUnitsPerDayFor(List<Pawn> pawns, DelegationModeDef mode, PlanetTile tile, Site site = null)
        {
            return mode == null ? 0f : CellsPerDay(pawns, mode, tile);
        }

        public override DelegationPreview MakePreview(Site site)
        {
            ThingDef mineableDef = ResolveMineableDef(site);
            FloatRange range = LumpValueRange();
            int yieldPerCell = mineableDef?.building?.EffectiveMineableYield ?? 0;
            return new DelegationPreview
            {
                resourceDef = mineableDef,
                minUnits = TotalCellsFor(range.min, mineableDef),
                maxUnits = TotalCellsFor(range.max, mineableDef),
                yieldPerUnit = yieldPerCell,
                // 每"格"最终装进车队的质量 = 每格产出单位数 × 该矿物的单位质量。
                // 对话框据此预告"采完后车队多重"，量纲与物资点那条 def 一致。
                massPerUnit = yieldPerCell * (mineableDef?.building?.mineableThing?.BaseMass ?? 0f)
            };
        }

        public override string PreviewLabel(Site site, DelegationPreview preview, DelegationDeposit exactDeposit)
        {
            ThingDef product = (exactDeposit?.resourceDef ?? preview?.resourceDef)?.building?.mineableThing;
            if (product == null)
            {
                return "矿种未识别 —— 这一处没有登记矿脉信息。";
            }
            int yieldPerCell = exactDeposit?.yieldPerUnit ?? preview?.yieldPerUnit ?? 0;
            FloatRange range = LumpValueRange();
            float average = (range.min + range.max) * 0.5f;

            if (exactDeposit != null)
            {
                return string.Format("{0} · 剩余 {1}/{2} 格（已确定）· 每格基础 {3} 单位",
                    product.LabelCap, exactDeposit.UnitsRemaining, exactDeposit.totalUnits, yieldPerCell);
            }

            string s = string.Format("{0} · 预计 {1}–{2} 格（抵达后才能确定实际规模）· 每格基础 {3} 单位",
                product.LabelCap, preview.minUnits, preview.maxUnits, yieldPerCell);
            s += string.Format("\n矿点价值区间 {0:0}-{1:0} 银（均值 {2:0}）", range.min, range.max, average);
            return s;
        }

        /// <summary>
        /// 「预期获得」列表（S6）：矿点只有**一种产物**，而规模在抵达前只能给区间 ——
        /// 所以这一行刻意把"区间"写在脸上（uncertain = true ⇒ UI 画成灰字），
        /// 抵达后（exactDeposit 非空）换成精确单位数。
        /// </summary>
        public override List<DelegationPreviewItem> PreviewItems(Site site, DelegationPreview preview,
            DelegationDeposit exactDeposit)
        {
            ThingDef mineable = exactDeposit?.resourceDef ?? preview?.resourceDef;
            ThingDef product = mineable?.building?.mineableThing;
            if (product == null)
            {
                return null;
            }
            int yieldPerCell = Mathf.Max(1, exactDeposit?.yieldPerUnit ?? preview?.yieldPerUnit ?? 0);

            DelegationPreviewItem item = new DelegationPreviewItem
            {
                thingDef = product,
                label = product.LabelCap.ToString()
            };

            if (exactDeposit != null)
            {
                int units = exactDeposit.UnitsRemaining * yieldPerCell;
                item.detail = string.Format("×{0} 单位 · 约 {1:0.#} kg · 市价约 {2:0} 银（已确定）",
                    units, units * product.BaseMass, units * product.BaseMarketValue);
                item.value = units * product.BaseMarketValue;
                item.count = units;
                item.unitLabel = "单位";
                item.mass = units * product.BaseMass;
            }
            else
            {
                int min = preview.MinYieldUnits;
                int max = preview.MaxYieldUnits;
                item.uncertain = true;
                item.count = max;
                item.unitLabel = "单位";
                item.detail = string.Format("×{0}–{1} 单位 · 约 {2:0.#}–{3:0.#} kg（抵达后才能确定）",
                    min, max, min * product.BaseMass, max * product.BaseMass);
            }
            return new List<DelegationPreviewItem> { item };
        }

        /// <summary>
        /// 作业进行中"现场还剩什么"（S7）：矿点只有一种产物，所以就是一行 ——
        /// 剩余格数 × 每格产出 = 还没搬走的单位数。让页签的「本次进度」能说清"在挖什么、还剩多少"。
        ///
        /// S9 起把**机器可读的数量**（count / unitLabel / mass / value）也填上：
        ///   ① UI 要用它当分子算「已获取 X/Y 单位」（分母是开工时的台账）；
        ///   ② 有台账时明细里不再重复"约 N 单位"（前缀已经说了），没台账时仍然写全 ——
        ///      两种存档下都不丢信息。
        /// </summary>
        public override List<DelegationPreviewItem> ProgressItems(Delegation d, Site site)
        {
            ThingDef product = d?.resourceDef?.building?.mineableThing;
            if (product == null)
            {
                return null;
            }
            int remainingCells = Mathf.Max(0, d.totalCells - Mathf.FloorToInt(d.cellsMined));
            int units = remainingCells * Mathf.Max(1, d.yieldPerCell);
            float mass = units * product.BaseMass;
            float value = units * product.BaseMarketValue;
            DelegationPreviewItem item = new DelegationPreviewItem
            {
                thingDef = product,
                label = product.LabelCap.ToString(),
                count = units,
                unitLabel = "单位",
                mass = mass,
                value = value
            };
            item.detail = d.LedgerFor(product) != null
                ? string.Format("剩余 {0} 格 · {1:0.#} kg · 市价约 {2:0} 银", remainingCells, mass, value)
                : string.Format("剩余 {0} 格 · 约 {1} 单位 · {2:0.#} kg · 市价约 {3:0} 银",
                    remainingCells, units, mass, value);
            return new List<DelegationPreviewItem> { item };
        }

        public override string PawnDetail(Pawn p)
        {
            if (p == null)
            {
                return null;
            }
            int level = p.skills?.GetSkill(SkillDefOf.Mining)?.Level ?? 0;
            float speed = p.GetStatValue(StatDefOf.MiningSpeed);
            return string.Format("采矿 {0} · 速度 ×{1:0.##} · {2:0.##} 格/作业小时",
                level, speed, CellsPerWorkHour(p));
        }

        public override string ProgressLabel(Delegation d)
        {
            if (d.resourceDef?.building?.mineableThing == null)
            {
                return "矿种未识别";
            }
            float percent = d.cellsMined / Mathf.Max(1, d.totalCells) * 100f;
            int estimated = Mathf.RoundToInt(d.totalCells * d.yieldPerCell * (d.cellsMined / Mathf.Max(1, d.totalCells)));
            return string.Format("{0}：{1:0.#}/{2} 格（{3:0.#}%）· 约 {4} 单位",
                d.resourceDef.building.mineableThing.LabelCap,
                d.cellsMined, d.totalCells, percent, estimated);
        }

        /// <summary>
        /// 把"存量是独立推算的"这件事摊到检视面板上。
        ///
        /// 不这么做的话，玩家会遇到一个说不清的现象：委派说还剩 12 格，进图一看是 47 格（或反过来）。
        /// 根因是原版的矿格数**只在生成地图时才掷**（`GenStep_PreciousLump` 用
        /// `totalValueRange.RandomInRange` 现掷），而委派按同一个公式自己算了一遍 ——
        /// 全局 RNG 的状态不同，两次结果必然只是**同分布**、不是同一个数（DESIGN §7.1 自认未堵）。
        ///
        /// 配套的堵漏是 Def 上的 `blockMapEntryAfterWorked`：动过存量的地点会被
        /// 原版 `EnterCooldownComp` 封禁进入，所以"两边各拿一份"不成立。
        /// </summary>
        public override string InspectWarning(Delegation d)
        {
            if (d == null)
            {
                return null;
            }
            bool blocked = d.def != null && d.def.blockMapEntryAfterWorked;
            // S20 文案：原来那句写的是"按 GenStep 的公式独立推算 / 原版生成地图时掷格数"——
            // 那是引擎口吻，玩家看到的应该是"两处各算了一次账，数字可能对不上"。
            return "⚠ 这里的矿藏规模是委派按矿脉的分布规律另行推算的，" +
                   "而实地开挖时还会按地图上那一份来算 —— 两处各算一次，数字可能对不上。" +
                   (blocked
                       ? "该地点在委派动过之后会禁止进入，所以不会出现两边各拿一份。"
                       : "（当前设置没开「动过之后禁止进入」，所以还可能出现两边各拿一份。）");
        }
    }
}
