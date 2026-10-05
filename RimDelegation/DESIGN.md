# RimDelegation 设计草案 v0.2（世界地图「委派」框架）

> **v0.2 概念变更**：v0.1 的"远程作业（Remote Operation）"改为**「委派（Delegation）」**。核心动作是：玩家在世界地图上把**一件事**委派给**一支车队**，由车队自己完成，**不进地图**。两种下达方式：
> - **逻辑 1 · 就地委派**：车队已停在事件点上 → 直接下委派 → 立刻开工
> - **逻辑 2 · 预先委派**：车队还没到 → 先计划好"去哪、委派什么、什么模式" → 车队走过去 → **抵达即开工，不再二次确认**
>
> 命名约定 `[建议]`：Mod 目录 / 程序集 / packageId 仍沿用 **`RimDelegation`**（不做无谓改名）；**游戏内概念与 C# 类型统一用「委派 / Delegation」**（`DelegationDef` / `DelegationWorker` / `Delegation` / `WorldObjectComp_Delegations` / `CaravanArrivalAction_StartDelegation`）。若想连 mod 名一起换成 `RimDelegation` 之类，改 packageId + 三处引用即可。
>
> 本文的每一条"引擎事实"都来自本机实测（rimsearcher Def 数据 + 1.6 Assembly-CSharp 反编译），标注 `[验证]`；我提出的数值/结构标注 `[建议]`；未验证的假设标注 `[待实测]` / `[UNVERIFIED]`。
>
> **首版目标**：远距离矿物扫描仪产出的矿点（`PreciousLump`）。
> **终局目标**：数据驱动的委派框架，一个 XML def 覆盖一类世界事件（矿点 / 物品藏匿点 / 工作站点 / 废弃据点 / 小行星）。
>
> **已定稿决策**：① 采用**真实车队**（不瞬移），**抵达才开工**；② 直接做 `DelegationDef` **框架**，矿点只是第一条 def；③ 委派开始时**暂停/重置**矿点的 30 天超时计时；④ 支持**就地委派**与**预先委派**两条下达路径。

---

## 0. 一句话定位

```
现在：扫描 → 出矿点 → 组车队走过去 → 切图(生成地图) → 手动点挖/搬 → 组车队 → 走回来

委派后：
  逻辑 1（已经在点上）：选中车队 → 点 caravan 上的「委派：远程开采」gizmo → 立刻开工
  逻辑 2（还没到）    ：选中车队 → 右键矿点 → 「预先委派：远程开采…」→ 车队走过去 → 抵达自动开工
                                          ↓
                        按天消耗营养产出 → 采空即收工（或带已采部分撤回）
```

---

## 1. 原版基线：从扫描到矿点的完整数据链（全部 `[验证]`）

### 1.1 事件产生

| # | 环节 | 引擎事实 |
|---|---|---|
| 1 | 扫描仪 | `ThingDef LongRangeMineralScanner` 带 `RimWorld.CompLongRangeMineralScanner`（+ `CompPowerTrader` / `CompBreakdownable` / `CompFlickable` / `CompForbiddable`）；其 gizmo 从 `GenStepDefOf.PreciousLump.genStep` 的 `mineables` 取可选矿种 |
| 2 | 可选矿种（7 种） | `MineableGold` / `MineableSilver` / `MineableSteel` / `MineablePlasteel` / `MineableComponentsIndustrial` / `MineableUranium` / `MineableJade` |
| 3 | 出结果 | `CompLongRangeMineralScanner.DoFind(Pawn worker)`：建 `Slate` 设 `map` / `targetMineable` / `targetMineableThing` / `worker`，跑 `QuestScriptDefOf.LongRangeMineralScannerLump`，发 `LetterDefOf.PositiveEvent` |
| 4 | 任务脚本 | `LongRangeMineralScannerLump`：`autoAccept = true`；`siteThreatChance = 0.5`（`QuestNode_ViolentQuestsAllowed` 为假则 0） |
| 5 | 站点部件 | `QuestNode_GetSitePartDefsByTagsAndFaction`：标签 `PreciousLump`，外加按 `$siteThreatChance` 概率取 `MineralScannerPreciousLumpThreat` |
| 6 | 威胁池 | `MineralScannerPreciousLumpThreat` 这个 token 只出现在这 7 个 `SitePartDef`：`AmbushEdge` / `AmbushHidden` / `Turrets` / `Manhunters` / `Outpost` / `SleepingMechanoids` / `MechCluster` |
| 7 | **矿种已存盘** | `QuestNode_GetDefaultSitePartsParams.SetVars`：若 part 是 `PreciousLump` → `parms.preciousLumpResources = slate.Get<ThingDef>("targetMineable")`。该字段在 `SitePartParams` 内且已 `Scribe_Defs` ⇒ **不解包地图就能读到矿种** |
| 8 | 超时 | `timeoutTicks = $(30*60000)`（**30 天**）；`QuestNode_WorldObjectTimeout` 的 `inSignalDisable = site.MapGenerated`（**一旦生成地图就停表**）；到期发信「XX 已被人捷足先登」→ `QuestNode_End outcome = Fail`；另有 `QuestNode_NoWorldObject` 兜底结束任务 |

### 1.2 矿点定义与生成

| 项 | 值 `[验证]` |
|---|---|
| `SitePartDef PreciousLump` | `workerClass = RimWorld.Planet.SitePartWorker_PreciousLump`；`forceExitAndRemoveMapCountdownDurationDays = 11`（进图后 11 天强制撤离）；`considerEnteringAsAttack = true`；`requiresFaction = false`；`gravShipsCanLandOn = true`；`selectionWeight = 0`；`tags = ["PreciousLump"]` |
| `GenStepDef PreciousLump` | `linkWithSite = PreciousLump`；`order = 900`；`genStep = GenStep_PreciousLump` |
| `GenStep_PreciousLump` | `mineables` = 上述 7 种；`totalValueRange = {min:3500, max:5000}`；`count = 1` |
| 生成公式 | `forcedDefToScatter = parms.sitePart.parms.preciousLumpResources ?? mineables.RandomElement()`；`V = totalValueRange.RandomInRange`；**`forcedLumpSize = max(round(V / (mineableYield × mineableThing.BaseMarketValue)), 1)`** |

### 1.3 采矿产出公式链（逐条反编译）

```csharp
// BuildingProperties
public int EffectiveMineableYield => Mathf.RoundToInt(mineableYield * Find.Storyteller.difficulty.mineYieldFactor);

// Mineable
public void Notify_TookMiningDamage(int amount, Pawn miner)
{
    float num  = (float)Mathf.Min(amount, HitPoints) / (float)MaxHitPoints;
    float num2 = (miner != null) ? miner.GetStatValue(StatDefOf.MiningYield) : 1f;
    yieldPct += num * num2;
}
// TrySpawnYield：先过 def.building.mineableDropChance
int num = Mathf.Max(1, def.building.EffectiveMineableYield);
if (def.building.mineableYieldWasteable) num = Mathf.Max(1, GenMath.RoundRandom((float)num * yieldPct));
```

| 项 | 值 `[验证]` |
|---|---|
| `StatDef MiningYield.skillNeedFactors` | `SkillNeed_Direct`，`skill = Mining`，`valuesPerLevel[0..20]` = **0.60 / 0.70 / 0.80 / 0.85 / 0.90 / 0.925 / 0.95 / 0.975 / 1.00 / 1.01 / 1.02 / 1.03 / 1.04 / 1.05 / 1.06 / 1.07 / 1.08 / 1.09 / 1.10 / 1.12 / 1.13** |
| `StatDef MiningSpeed.skillNeedFactors` | `SkillNeed_BaseBonus`，`baseValue 0.04`，`bonusPerLevel 0.12`，`skill = Mining` |
| `MineableSteel` | `mineableYield 40`；`mineableThing Steel`；`mineableDropChance 1`；`mineableYieldWasteable true`；`MaxHitPoints 1500`；`mineableScatterCommonality 1`；`mineableScatterLumpSizeRange {30,40}`；`veinMineable true` |
| `MineableGold` | `mineableYield 40`；`mineableThing Gold`；`MaxHitPoints 1500`；`mineableScatterCommonality 0.07`；`mineableScatterLumpSizeRange {2,8}` |
| 市价（`statBases[].MarketValue`） | **Steel 1.9**；**Gold 10** |

### 1.4 由此推出的矿点真实规模 `[建议]`（按公式推算）

`矿格数 = max(round(V / (40 × 市价)), 1)`，`单格满采产出 = EffectiveMineableYield × MiningYield(技能)`：

| 矿种 | 矿格数 | 满采总产出（技能 8，系数 1.00） | 技能 0（0.60） | 技能 20（1.13） |
|---|---|---|---|---|
| Steel | V/76 → **46–66 格** | 1840–2640 钢铁 | 1104–1584 | 2079–2983 |
| Gold | V/400 → **9–13 格** | 360–520 黄金 | 216–312 | 407–588 |

> **设计不变式**：无论什么矿，矿点总价值恒为 **3500–5000 银**；技能只影响「拿到多少 × 花多久」（0.6×～1.13×，约 ±40%）。这是委派数值的天然锚点。

### 1.5 玩家当前被迫走的路径（痛点）

```
组车队 → 走路 → 抵达矿点 → 点「进入地点」→ CaravanArrivalAction_VisitSite.DoEnter()
  → GetOrGenerateMapUtility.GetOrGenerateMap(...)         ← 生成地图 = 停掉 30 天计时
  → CaravanEnterMapUtility.Enter(...)                     ← 人进图
  → 手动 designate 挖矿 / 搬运 → 组车队 → 走回家
```

`Site.HasMap` 为 false 时，切图是唯一获取矿石的路径 —— **这就是本 mod 要拆掉的那一步。**

---

## 2. 双入口机制（两条路都**零 Harmony**）

### 2.1 逻辑 1 · 就地委派：挂在**车队**上的 gizmo

```
选中车队 → Caravan.GetGizmos()
  if (IsPlayerControlled)
    foreach (WorldObject o in Find.WorldObjects.ObjectsAt(caravan.Tile))   // 车队所在格上的所有世界对象
      foreach (Gizmo g in o.GetCaravanGizmos(this))
        yield return g;
              └─ WorldObject.GetCaravanGizmos:  foreach (comps[i].GetCaravanGizmos(caravan)) yield return ...
                                                                        ↑ ★我们的 comp 在这里出「委派：远程开采」
```

⇒ 只要车队**停在事件点格上**，caravan 的 gizmo 栏里就会出现委派按钮。整条链 `[验证]`：`Caravan.GetGizmos` → `WorldObject.GetCaravanGizmos` → `WorldObjectComp.GetCaravanGizmos(Caravan)`。
注意：这一段在 `Caravan.GetGizmos` 里**不在** `SingleSelectedObject == this` 判断内 ⇒ 多选车队时也会显示。

### 2.2 逻辑 2 · 预先委派：任务菜单 + 抵达回调

```
选中车队 → 在世界地图上给矿点下指令
  └─ Site.GetFloatMenuOptions(Caravan caravan)
       ├─ base = WorldObject.GetFloatMenuOptions(caravan)
       │        └─ foreach (comps[i].GetFloatMenuOptions(caravan)) yield return ...   ← ★我们的 comp 在这里出「预先委派：…」
       ├─ if (base.HasMap) yield break;        ← 进过图就再也没有委派入口
       └─ CaravanArrivalAction_VisitSite.GetFloatMenuOptions(caravan, this)  → 「进入地点 / 进攻」
                └─ action = caravan.pather.StartPath(site.Tile, arrivalAction, repathImmediately: true)
```

`FloatMenuAcceptanceReport` 的三种语义（`CaravanArrivalActionUtility` 里 `[验证]`）：

| 返回 | 菜单表现 |
|---|---|
| 不 Accepted，且 `FailReason` / `FailMessage` 都空 | **该项完全不显示**（"不适用 / 未解锁"） |
| `FailReason` 非空 | 显示成 `label (原因)`，灰掉不可点（"技能不够 / 人不够"） |
| `FailMessage` 非空 | 可点，点了弹 `Messages.Message(..., MessageTypeDefOf.RejectInput)`（"点击时才发现的问题"） |

计划期间玩家能看到进度：`CaravanArrivalAction.Label` / `ReportString` 会显示在车队信息里（原版例：`CaravanArrivalAction_VisitSite.Label => site.ApproachOrderString`、`ReportString => site.ApproachingReportString`）`[验证]`。
**中途存读档安全** `[验证]`：`Caravan_PathFollower.ExposeData` 里 `Scribe_Deep.Look(ref arrivalAction, "arrivalAction")`，读档时还会 `StartPath(destTile, arrivalAction, ...)` 重新挂上。

抵达时的回调 `[验证]`：

```csharp
private void PatherArrived()
{
    CaravanArrivalAction a = arrivalAction;
    StopDead();
    if (a != null && (bool)a.StillValid(caravan, caravan.Tile)) a.Arrived(caravan);
    else if (caravan.IsPlayerControlled && !caravan.VisibleToCameraNow())
        Messages.Message("MessageCaravanArrivedAtDestination".Translate(caravan.Label), caravan, MessageTypeDefOf.TaskCompletion);
}
```

⇒ **`StillValid` 是抵达时的守门员**：计划失效（矿点没了 / 被人占了 / 已有别的委派）时，原版会优雅退化成普通的"已抵达"提示，不会崩。

### 2.3 ★ 两条路统一到同一个调用（关键发现）

```csharp
public bool StartPath(PlanetTile destTile, CaravanArrivalAction arrivalAction, bool repathImmediately = false, bool resetPauseStatus = true)
{
    caravan.autoJoinable = false;
    if (arrivalAction != null && !arrivalAction.StillValid(caravan, destTile)) return false;
    ...
    this.destTile = destTile;
    this.arrivalAction = arrivalAction;
    ...
    if (AtDestinationPosition()) { PatherArrived(); return true; }    // ← 关键
    ...
}
private bool AtDestinationPosition() => caravan.Tile == destTile;     // ← 关键
```

⇒ **`StartPath(车队当前所在格, action)` 会立刻走完"抵达"流程并触发 `action.Arrived(caravan)`** `[验证]`。

所以逻辑 1 与逻辑 2 **共用同一段启动代码**，只是 UI 入口不同：

```csharp
// 两个入口最终都调这一行
caravan.pather.StartPath(site.Tile, new CaravanArrivalAction_StartDelegation(site, delegationDef, mode), repathImmediately: true);
//   车队已在格上 → AtDestinationPosition() == true → 立刻 Arrived() → 开工（逻辑 1）
//   车队不在格上 → 正常走位 → 抵达时 PatherArrived() → Arrived() → 开工（逻辑 2）
```

顺带把 v0.1 的待实测项 #2（"StartPath 到当前格会怎样"）**直接解决了**。

### 2.4 两个入口的代码骨架

```csharp
// WorldObjectComp_Delegations —— 挂在 WorldObjectDef[Site] 上（XML patch，零 Harmony）

// 逻辑 1：车队已停在事件点上 → 出 gizmo
public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
{
    var site = parent as Site;
    if (site == null || site.HasMap || site.Tile != caravan.Tile) yield break;
    if (ActiveDelegation != null) { yield return CancelDelegationGizmo(caravan); yield break; }

    foreach (var def in DelegationUtility.MatchingDefs(site))          // 按 site.parts[].def.tags 匹配
    {
        var localDef = def;                                            // 闭包捕获
        yield return new Command_Action
        {
            defaultLabel = "RimDelegation.CommandDelegate".Translate(localDef.LabelCap),
            defaultDesc  = localDef.description,
            icon         = localDef.Icon,
            action       = () => Find.WindowStack.Add(new Dialog_ChooseDelegation(caravan, site, localDef))
        };
    }
}

// 逻辑 2：车队还没到 → 出"预先委派"菜单项
public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
{
    var site = parent as Site;
    if (site == null || site.HasMap || ActiveDelegation != null) yield break;

    foreach (var def in DelegationUtility.MatchingDefs(site))
    {
        var localDef = def;
        foreach (var opt in CaravanArrivalActionUtility.GetFloatMenuOptions(
            () => DelegationUtility.CanStart(caravan, site, localDef),          // → FloatMenuAcceptanceReport
            () => new CaravanArrivalAction_StartDelegation(site, localDef, mode),
            "RimDelegation.PlanDelegation".Translate(localDef.LabelCap),
            caravan, site.Tile, site))
        {
            yield return opt;
        }
    }
}
```

```csharp
// CaravanArrivalAction_StartDelegation —— 两条路的共同落点
public class CaravanArrivalAction_StartDelegation : CaravanArrivalAction
{
    private Site site;
    private DelegationDef def;
    private DelegationModeDef mode;          // 见 §4

    public override string Label        => "RimDelegation.Delegating".Translate(def.LabelCap);
    public override string ReportString => "RimDelegation.TravelingToDelegate".Translate(def.LabelCap, site.Label);

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        => DelegationUtility.CanStart(caravan, site, def);

    public override void Arrived(Caravan caravan)
    {
        var comp = site.GetComponent<WorldObjectComp_Delegations>();
        comp?.StartDelegation(caravan, def, mode);      // ← 就地委派与预先委派在这里汇合
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref site, "site");
        Scribe_Defs.Look(ref def, "def");
        Scribe_Defs.Look(ref mode, "mode");
    }
}
```

> `WorldObject.GetComponent<T>()` 是否存在未验证 `[待实测]`；若无则用 `parent.comps.OfType<WorldObjectComp_Delegations>().FirstOrDefault()`（`WorldObject.comps` 是 public 字段 `[验证]`）。

---

## 3. 类结构

| 类型 | 角色 |
|---|---|
| `RimDelegation.DelegationDef : Def` | 数据：委派能给谁、要几人、什么技能、多久、产什么、什么风险、怎么收尾 |
| `RimDelegation.DelegationWorker` | 行为：`CanStart` / `DurationTicks` / `YieldPerTick` / `OnStart` / `OnTick` / `OnComplete` / `OnAbort` / `GetInspectString`。矿点是第一个实现 |
| `RimDelegation.DelegationModeDef : Def` | 委派模式（见 §4） |
| `RimDelegation.Delegation` | `IExposable` 实例状态：`def` / `mode` / `caravan` / `pawns` / `ticksDone` / `progress` / `accumulatedYield` / `startedTick` |
| `RimDelegation.WorldObjectComp_Delegations : WorldObjectComp` | 挂在 `Site` def；持有活动委派；`CompTickInterval` 跑结算；`GetCaravanGizmos` / `GetGizmos` / `CompInspectStringExtra` 做 UI；`PostExposeData` 存读档 |
| `RimDelegation.CaravanArrivalAction_StartDelegation : CaravanArrivalAction` | 预先委派的抵达落点；也是就地委派的统一入口 |
| `RimDelegation.Dialog_ChooseDelegation` | 选人 + 选模式（就地委派时；预先委派时它是"计划"对话框） |
| `RimWorld.StatDef RimDelegation_WorkRatePerDay`（+ `StatWorker` / `StatPart`）`[建议]` | 走原版范式（`ForagedNutritionPerDay` + `StatWorker_ForagedNutritionPerDay` + `ForagedFoodPerDayCalculator`），inspect 里能给"为什么是这个数" |

**委派状态挂在哪？** `[建议]` 挂在 **Site**（`WorldObjectComp_Delegations`）而不是 Caravan，理由：
- 被消耗的稀缺资源是**事件点**，它的状态应该跟着事件点走；
- 暂停 30 天计时、完成即销毁（防双吃）都是 site 侧事务；
- 车队中途消失 / 被合并 / 被摧毁时，site 侧一检测 `caravan == null || caravan.Destroyed` 就能转 Aborted；
- 玩家用**另一支**车队来查看/接管时，状态仍在。

`DelegationDef` 字段草案 `[建议]`：

```
targetSitePartTags : List<string>      // 匹配 Site.parts[].def.tags，如 PreciousLump / WorkSite / ItemStash
targetSitePartDefs : List<SitePartDef> // 或精确指定
workerClass        : Type              // DelegationWorker
label / description: string
gizmoIcon / texPath: string
minPawns / maxPawns: int
skillDef           : SkillDef          // Mining / Plants / Animals / Construction
workRateStat       : StatDef
modes              : List<DelegationModeDef>   // 见 §4；默认取 defaultMode
defaultMode        : DelegationModeDef
yieldMode          : enum { MineableLump, ThingSetMaker, LootTable }
yieldThingDef      : ThingDef
yieldPerDayRange   : FloatRange
yieldTable         : List<ThingDefWeight>      // 结构可照抄 SitePartDef.WorkSiteLootThing { thing, weight }
handleTargetTimeout: enum { Ignore, Pause, ExtendBy }   // ← 已定决策③ = Pause
destroyTargetOnComplete  : bool        // ← 防双吃，见 §7.1
abortIfCaravanLeavesTile : bool
abortOnThreat      : bool
riskTable          : List<RiskEntry>
letters            : start / progress / complete / abort / fail
```

---

## 4. 委派模式（`DelegationModeDef`）

`[建议]` 把"选择委派模式"做成数据驱动，`DelegationDef.modes` 列可选模式，`defaultMode` 是预先委派时的默认值。

| 轴 | 候选模式 | 影响 |
|---|---|---|
| **强度** | 标准 / **急采** / 保守 | 日产 ×1.0 / ×1.5–2.0 / ×0.7；急采额外扣心情、加受伤几率；保守几乎无事故 |
| **归属** | **随队带回** / 空投回城 | 产出进车队库存需自己运回 / 每天或完成时用运输舱送回（耗化合燃料，需 `CompLaunchable` 或 Odyssey 穿梭机） |
| **结束条件** | **采空为止** / 按配额 / 按天数 | 采空 / 例如"采满 400 钢铁就收工" / "干 5 天就撤" |
| **风险姿态** | **遇袭即撤** / 坚守 | `abortOnThreat` 的开关 |

v1 建议只实现**强度 × 结束条件**两轴、各 2–3 个模式，其余留空字段。预先委派时在 `Dialog_ChooseDelegation` 里选完模式即锁定，抵达后不再弹窗（符合"到地方了直接开工"）；可留一个 ModSettings 开关"抵达后仍需确认"。

---

## 5. 生命周期状态机

```
                                        ┌── 逻辑 1：车队已在点上 → StartPath 立即 Arrived
菜单确认 / gizmo 点击 ──▶ [Planned] ────┤
                       (只在逻辑 2 存在) └── 逻辑 2：车队走位 ──▶ 抵达 → Arrived
                                                                    ↓
                                                                 [Active] ──进度满──▶ [Completed]
                                                                    │                  ├─ 产出交付
                                        ┌───────────────────────────┤                  ├─ site.Destroy()（防双吃）
                                        │                           │                  └─ 完成信
                        全员倒地/死亡 ───┤                           │
                        车队离开目标格 ──┤                           │
                        断粮 ────────────┤                           │
                        遇袭（若设为撤）─┤                           │
                        矿点计时到期 ────┘                           ↓
                                                              [Aborted]（交付已采部分）
```

要求：**每个分支都必须有信（Letter）+ `CompInspectStringExtra` / `GetCaravanGizmos` 可见反馈**，否则玩家会认为"卡住了"。

---

## 6. 结算与人物状态

### 6.1 营养 —— 原版白送 `[验证]`

```
Caravan.TickInterval(delta)
  ├─ pather.PatherTickInterval / tweener / forage / carryTracker / beds / babies
  ├─ needs.NeedsTrackerTickInterval(delta)
  │    └─ TrySatisfyPawnsNeeds(delta) → TrySatisfyPawnNeeds(pawn, delta)   ← 自动从车队库存进食/休息/服药/治疗
  ├─ CaravanDrugPolicyUtility.CheckTakeScheduledDrugs
  ├─ CaravanTendUtility.CheckTend
  └─ CaravanPollutionUtility.CheckDamageFromPollution（Biotech）
```

⇒ 只要人留在车队里，"消耗对应营养 / 心情 / 医疗 / 疾病 / 精神崩溃"**全部复用原版**。
食物耗尽时原版 `Caravan_NeedsTracker.AnyPawnOutOfFood` 会给断粮预警 —— 天然就是委派的 deadline。`nutritionPerPawnPerDay` 只用于"还要几天 / 预计总产出"的显示。

> **反面教材（已排除）**：把人塞进 `Find.WorldPawns` 自己持有 —— `WorldPawns.WorldPawnsTick()` 会对 `pawnsAlive` 逐个 `pawn.DoTick()` → `Thing.DoTick()`（`tickerType == Normal` 时调 `Tick()`），而 `Pawn_NeedsTracker.NeedsTrackerTickInterval` 里**没有 `Spawned` 判空** ⇒ 需求照掉，**但没人喂饭**（进食靠 `JobGiver_GetFood`，离图不跑），几天后全员营养不良；另有每 15000 tick 一次的 `DoMothballProcessing` 会休眠无人引用的世界 pawn。

### 6.2 产出 —— 原版车队库存 `[验证]`

```csharp
public void AddPawnOrItem(Thing thing, bool addCarriedPawnToWorldPawnsIfAny)
{
    if (thing == null) Log.Warning(...);
    else if (thing is Pawn p) AddPawn(p, addCarriedPawnToWorldPawnsIfAny);
    else CaravanInventoryUtility.GiveThing(this, thing);      // ← 物品走这里（含负重/人份分配）
}
```

⇒ `caravan.AddPawnOrItem(ThingMaker.MakeThing(def), false)`，回家自动倒出；车队库存 / 负重 UI 全复用。

**S2 已实装的交付节奏**：每 1 小时（2500 ticks）flush 一次 + 完成/中断各 flush 一次 → 中断天然等于"带已采部分撤回"。细节见 §15。

> **必须留意的副作用**：产出进车队库存 = 真实负重。原版车队超重（`Caravan.ImmobilizedByMass`）会**无法移动**。S2 会在首次超重时发一次提醒并在 inspect 常驻警告；玩家要么派驮兽、要么就地卸货、要么用运输舱运回。这也是 DESIGN §4「归属」轴（随队带回 / 空投回城）存在的理由。

### 6.3 速率 —— 原版公式已完整还原 `[验证]`

**原版采矿速率 = 固定的"击数 × 击间隔"；技能只改间隔，不改单次伤害。**

```csharp
// RimWorld.JobDriver_Mine
public const int BaseTicksBetweenPickHits = 100;
private const int BaseDamagePerPickHit_NaturalRock = 80;
private const int BaseDamagePerPickHit_NotNaturalRock = 40;
private const float MinMiningSpeedFactorForNPCs = 0.6f;

private void ResetTicksToPickHit()                       // 每击之间的间隔
{
    float num = pawn.GetStatValue(StatDefOf.MiningSpeed);
    if (num < 0.6f && pawn.Faction != Faction.OfPlayer) num = 0.6f;   // NPC 下限
    ticksToPickHit = (int)Math.Round(100f / num);
}

private void DoDamage(Thing target, Toil mine, Pawn actor, IntVec3 mineablePos)
{
    int num = target.def.building.isNaturalRock ? 80 : 40;            // ★与技能无关
    if (!(target is Mineable mineable) || target.HitPoints > num)
    {
        target.TakeDamage(new DamageInfo(DamageDefOf.Mining, num, 0f, -1f, mine.actor));
        return;
    }
    mineable.Notify_TookMiningDamage(target.HitPoints, mine.actor);   // ★最后一击用"剩余 HP"
    mineable.HitPoints = 0;
    mineable.DestroyMined(actor);                                     // → TrySpawnYield(yieldPct)
}
```

`MiningSpeed` 的合成链 `[验证]`（`StatWorker.GetValueUnfinalized` + `FinalizeValue`）：

```
MiningSpeed = defaultBaseValue(= 1)
            × skillNeedFactors[Mining].ValueFor(pawn)      // SkillNeed_BaseBonus = 0.04 + 0.12 × 等级  ← 乘法！
            × (traits / hediffs / genes / ideo 的 statFactors)
            + (gear 的 statOffsets)                        // 装备可以给加成
            × capacityFactors 惩罚                          // Manipulation(w=1)、Sight(w=0.5，仅在 <1 时生效)
            → Mathf.Clamp(val, minValue = 0.1, maxValue)     // ★技能 0 的 0.04 会被抬回 0.1
```

`skillNeedFactors` 是**乘法**、`skillNeedOffsets` 才是加法（`StatDef` 上两者都存在，不要搞混）。

⇒ 单格（`MineableSteel`：`isNaturalRock = true`、`MaxHitPoints = 1500`）恰好 **19 击**（18×80 = 1440 + 第 19 击打剩余 60），并且 `yieldPct` 的总累计**恰好等于 `MiningYield(技能)`**（因为总伤害 = MaxHitPoints）。

**委派可直接使用的闭式公式（无需任何经验常数）：**

```
ticksPerCell = 19 × round(100 / MiningSpeed(pawn))
orePerCell   = round(mineableYield × difficulty.mineYieldFactor) × MiningYield(pawn)
日产          = (60000 / ticksPerCell) × workFraction × Σ_矿工 orePerCell
```

`difficulty.mineYieldFactor` `[验证]`：和平休闲 / 开拓建设 = **1.2**；孤星探险 / 荒野求生 / 自定义 = **1.0**；冷酷无情 = **0.8**。

单人工时基准 `[验证]`（连续作业，1 天 = 60000 ticks）：

| 采矿等级 | MiningSpeed | ticks/击 | ticks/格 | 秒/格 | **格/作业小时** | 天/格 |
|---|---|---|---|---|---|---|
| 0 | **0.10**（被 minValue 夹回） | 1000 | 19000 | 317 | **0.13** | 0.317 |
| 2 | 0.28 | 357 | 6783 | 113 | **0.37** | 0.113 |
| 4 | 0.52 | 192 | 3648 | 61 | **0.69** | 0.061 |
| 6 | 0.76 | 132 | 2508 | 42 | **1.00** | 0.042 |
| **8** | **1.00** | 100 | 1900 | 32 | **1.32** | 0.032 |
| 12 | 1.48 | 68 | 1292 | 22 | **1.93** | 0.022 |
| 16 | 1.96 | 51 | 969 | 16 | **2.58** | 0.016 |
| 20 | 2.44 | 41 | 779 | 13 | **3.21** | 0.013 |

> 加粗那列是**现在 UI 用的单位**（`2500 / ticks每格`，1 小时 = 2500 ticks）。
> `秒/格` 保留在这里只为说明推导过程 —— 它在界面上已经被替换掉了，理由见 §19.25.8。

整点耗时（单人、连续）：

| 矿点 | 格数 | 技能 0 | 技能 8 | 技能 20 |
|---|---|---|---|---|
| Steel | 46 | 14.6 天 | 1.46 天 | 0.60 天 |
| Steel | 66 | 20.9 天 | 2.09 天 | 0.86 天 |
| Gold | 9 | 2.85 天 | 0.29 天 | 0.12 天 |
| Gold | 13 | 4.12 天 | 0.41 天 | 0.17 天 |

**由此得到的五条设计结论 `[建议]`**：

1. **技能的价值是不对称的**：产出只差 0.60→1.13（1.9×），速度差 0.10→2.44（**24×**）⇒「派谁去」比「派几个人」重要得多。
2. **现实系数**：原版地图上挖矿含走路/吃饭/睡觉，有效工时约 55–60%；委派模式最优雅的做法是直接用 `Caravan.NightResting` / `LeftNonRestTicks` 门控进度（原版 `ForagedFoodPerDayCalculator` 就是拿 `caravanNightResting` 当系数）⇒ 技能 8 挖完 46 格钢铁 ≈ **2.4–2.6 天**。
3. **30 天超时是天然门槛**：技能 0 单人挖 46 格钢铁（含现实系数）≈ 25 天，技能 8 只要 2.4 天 ⇒ 派错人真的会失败，不需要额外编惩罚机制。
4. **"带已采部分撤回"有原版语义可直接用**：`Mineable.PreApplyDamage` 每一击都按 `伤害/MaxHitPoints × MiningYield` 累计 `yieldPct`，且只有 `mineableYieldWasteable = true` 时才按比例产出 ⇒ 中断时的产出 = `EffectiveMineableYield × yieldPct`，**不需要自创公式**。
5. **多人协作要建模为"分格并行"**：`JobDriver_Mine` 没有"多人同格加速"的机制（一格只能一个人挖）⇒ 多劳力的委派应当线性叠加到"每天挖掉几格"，而不是缩短单格耗时。

### 6.4 风险

- **免费的压力**：车队停在野外 → `Caravan` 的 `WorldObjectDef.IncidentTargetTags = ["Caravan"]` `[验证]` ⇒ 原版袭击 / 疾病 / 动物事件自动生效，不用写新系统。
- **矿点专属**：50% 概率带 `MineralScannerPreciousLumpThreat`（§1.1 第 6 条）。三选一：(a) 需护送/清场才允许委派；(b) 按天 roll 危险（`riskTable`）；(c) 强制进图打（v1 不做）。
- **"遇袭即撤"模式**：遇袭即中断并交付已采部分，是 v1 最省事也最有戏剧性的规则。

### 6.5 委派期间，车队里的人到底在做什么 `[验证]`

车队的 tick 顺序（`Caravan.TickInterval`）：

```
pather → tweener → forage → carryTracker → beds → needs → babies
      → CaravanDrugPolicyUtility.CheckTakeScheduledDrugs
      → CaravanTendUtility.CheckTend
      → CaravanPollutionUtility.CheckDamageFromPollution（Biotech）
```

车队成员同时是**世界 pawn**（`CaravanMaker.MakeCaravan(..., addToWorldPawnsIfNotAlready: true)` → `Find.WorldPawns.PassToWorld`），会走 `WorldPawns.WorldPawnsTick` → `pawn.DoTick()` → `Pawn.TickInterval`：

```csharp
bool suspended = Suspended;
if (!suspended)
{
    if (!this.IsWorldPawn()) jobs?.JobTrackerTickInterval(delta);   // ★世界 pawn 不跑 Job
    health.HealthTickInterval(delta);                               // 伤病 / 感染照常
    if (!Dead)
    {
        mindState.MindStateTickInterval(delta);                     // ★精神崩溃 / 激励照常
        carryTracker.CarryHandsTickInterval(delta);
        infectionVectors?.InfectionTickInterval(delta);
    }
}
```

| 行为 | 车队里会不会发生 | 证据 |
|---|---|---|
| 吃饭（从车队库存取最好的食物） | ✅ | `TrySatisfyFoodNeed` → `CaravanInventoryUtility.TryGetBestFood` → `food.CurLevel += food2.Ingested(pawn, food.NutritionWanted)`；断粮发 `MessageCaravanRanOutOfFood`（`ThreatBig`） |
| 睡觉 / 恢复休息 | ✅ **只要车队没在移动** | `TrySatisfyRestNeed`：`if (!caravan.pather.MovingNow \|\| pawn.InCaravanBed() \|\| pawn.CarriedByCaravan()) rest.TickResting(bedRestEff)` |
| 娱乐 | ⚠️ 仅**停车**时，且只有 **Meditative（冥想）+ Social（社交，需 ≥2 名可行动人类）** | `GetCurrentJoyGainPerTick`：移动中返回 `0f`，停车返回 `4E-05f`；`GetAvailableJoyKindsFor` 只加这两种 |
| 床铺舒适 | ✅ | `Caravan_BedsTracker.RecalculateUsedBeds`（库存里的 `bed_caravansCanUse` 折叠床）→ `PawnUtility.GainComfortFromThingIfPossible` |
| 觅食 | ✅ | `Caravan_ForageTracker.ForageTrackerTickInterval` → `ForagedFoodPerDayCalculator` |
| 伤病进展 / 感染 | ✅ | `health.HealthTickInterval` |
| 精神崩溃 / 激励 / 阈值 | ✅ | `MindStateTickInterval` → `mentalStateHandler` / `mentalBreaker` / `inspirationHandler` |
| 医疗（自动包扎 / 用药） | ✅ | `CaravanTendUtility.CheckTend` |
| 按毒品政策服药 | ✅ | `CaravanDrugPolicyUtility.CheckTakeScheduledDrugs` |
| 血原质 / 灵能焦点 / 化学需求 | ✅ | `TrySatisfyHemogenNeed` / `TryGainPsyfocus` / `TrySatisfyChemicalDependency` |
| 污染伤害（Biotech） | ✅ | `CaravanPollutionUtility.CheckDamageFromPollution` |
| 婴儿 / 哺乳 | ✅ | `babies?.TickInterval`、`ChildcareUtility` |
| **心情想法：地上睡觉 / 睡在室外 / 无桌吃饭** | ❌ **不会发生** | `TrySatisfyRestNeed` 只调 `TickResting`、`Caravan_BedsTracker` 只给舒适度；`SleptOnGround` 是地图侧 `Toils_LayDown` 给的 |
| 地形 / 天气心情想法 | ❌ | `MindStateTickInterval` 里那一段以 `this.pawn.Spawned` 为前提 |
| **技能经验** | ❌ | Job 不跑（世界 pawn 跳过 `JobTrackerTickInterval`）⇒ **委派必须自己发 XP**，见 §6.7 |
| 需求驱动的心情 | ⚠️ **分情况，见下表** | 需求本身照掉（`Pawn_NeedsTracker.NeedsTrackerTickInterval` 没有 `Spawned` 判空），但**情境型想法**要过 `ThoughtUtility.CanGetThought` 的 `validWhileDespawned` 关 |

**⚠️ 上表最后一行的展开：车队成员的"心情想法"到底哪些真的生效（§19.25 核实）**

判据是 `ThoughtUtility.CanGetThought` 里的这一行：

```csharp
if (!def.validWhileDespawned && !pawn.Spawned && !def.IsMemory) return false;
```

车队成员 `Spawned == false`，所以**没有写 `validWhileDespawned` 的情境型想法全部作废**：

| 想法 | 值域 | 车队里生效 | 依据 |
|---|---|---|---|
| `NeedFood` 饥饿 | 0 / -6 / -12 / -20 / -26 / -32 / -38 / -44 | ✅ | `validWhileDespawned=true` |
| `NeedJoy` 娱乐 | -20 / -10 / -5 / +5 / +10 | ✅ | 同上；停车时靠冥想 / 社交慢慢涨 |
| `NeedComfort` 舒适 | -3 / +4 / +6 / +8 / +10 | ✅ | 同上；来自 `Caravan_BedsTracker` 的折叠床 |
| `EnvironmentCold` / `EnvironmentHot` | 0 / -4 / -8 / -12 / -16 | ✅ | 同上；`AmbientTemperature` 对未 spawn 的 thing 会退回 `GenTemperature.GetTemperatureAtTile(Tile)`，而车队的 `Tile` 有效 ⇒ **矿点所在地的气候真的扣心情** |
| 餐食 `AteLavishMeal` / `AteFineMeal` / `AteRawFood` / `AteKibble` | +12 / +5 / -7 / -12 | ✅ | 记忆型（`durationDays>0` ⇒ `IsMemory`），由 `Thing.Ingested` 授予；`stackLimit` 默认 1 ⇒ **只刷新不叠加** |
| `Expectations` 财富期望 | +30 … -12 | ⚠️ 未决 | `validWhileDespawned=true`，但代码里另一处 `minExpectation` 分支要求 `pawn.Spawned`，实际生效到哪档没核实 |
| **`NeedRest` 疲劳** | **-6 / -12 / -18** | **❌ 不生效** | **没写 `validWhileDespawned`**，而且是情境型 ⇒ 直接 return false |
| `NeedBeauty` | — | ❌ | 同上 |

> **这条纠正很重要**：早先的设计笔记写过"全天轮班一路掉到 0.1 的地板硬撑 ⇒「连轴转 -6」的心情代价有了机制支撑" —— **那是错的**。
> 疲劳在车队里**不产生任何心情惩罚**，0.1 的地板只影响休息条本身。
> 所以长工时模式的生理代价必须由我们自己补，做法见 §19.25（疲劳 → 工伤倍率）。

**结论**：委派期间的人不是"冻结"的 —— 吃、睡、病、崩、激励、成瘾、污染全都在跑。"派出去受苦"是真实存在的，而且 mod 有完整挂载点去加码。

### 6.6 工作时长：可以在委派 UI 上设 `[建议]`

**原版已经有"车队的一天"这个概念**，直接复用：

```csharp
public static class CaravanNightRestUtility
{
    public const float WakeUpHour    = 6f;
    public const float RestStartHour = 22f;
    public static bool RestingNowAt(PlanetTile tile) => WouldBeRestingAt(tile, GenTicks.TicksAbs);
    public static bool WouldBeRestingAt(PlanetTile tile, long ticksAbs)
    {
        float h = GenDate.HourFloat(ticksAbs, Find.WorldGrid.LongLatOf(tile).x);   // 经度修正的地方时
        return h < 6f || h > 22f;
    }
    public static int LeftRestTicksAt(PlanetTile tile);      // 1 小时 = 2500 ticks
    public static int LeftNonRestTicksAt(PlanetTile tile);
}
```

而原版**已经用这个东西当离图生产的进度系数**（照抄即可）：

```csharp
// ForagedFoodPerDayCalculator
private const float BaseProgressPerTick = 0.0001f;
public const float NotMovingProgressFactor = 2f;
GetProgressPerTick(movingNow, nightResting): if (!movingNow && !nightResting) num *= 2f;
```

⇒ 委派速率写成 `进度 += rate × delta × f(当前时段)`。

**别踩的坑**：`Caravan.NightResting` 是**真的时钟**，不是"累了才睡"：

```csharp
public bool NightResting => base.Spawned
    && needs.AnyPawnsNeedRest                              // ← 只要队员"有 rest 需求"就为真
    && !(最后冲刺中)
    && CaravanNightRestUtility.RestingNowAt(base.Tile);
// AnyPawnsNeedRest: 遍历 PawnListForReading，只要 needs?.rest != null 就 return true
```

所以它可以安全地当"现在是夜里"用；反过来，别指望它反映疲劳程度。

UI 提案（`Dialog_ChooseDelegation` 里的一个下拉）：

| 模式 | 判定 | 日有效工时 | 代价 |
|---|---|---|---|
| **只白天（默认）** | `!CaravanNightRestUtility.WouldBeRestingAt(tile, GenTicks.TicksAbs)` | 16 h（6:00–22:00） | 无 |
| **早出晚归** | 自定义 `HourFloat ∈ [5, 23)` | 18 h | 轻微心情惩罚 |
| **全天轮班** | 恒 1（24 h） | 24 h | 心情 debuff + 事故/受伤几率，见 §6.7 |

两个实现细节：
- 用 `GenTicks.TicksAbs` + `GenDate.HourFloat(ticksAbs, Find.WorldGrid.LongLatOf(tile).x)` 自己算，`WouldBeRestingAt` 是 public static 可直接调。
- 委派 inspect 面板显示「今日已作业 9.4 / 16 h」，让工时可见。

### 6.7 心情增益 / 减益 与 技能经验 `[建议]`

**心情用 `ThoughtDef`，不要用 Hediff。** 原版 `moodOffset` 实际只出现在 Anomaly 的一个 hediff comp 上（`comps[0].moodOffset`），不是通用字段。

`ThoughtDef` 有两条路：

| 类型 | 做法 | 适合 |
|---|---|---|
| **记忆型 memory** | `workerClass = null`，由代码显式授予 `pawn.needs.mood.thoughts.memories.TryGainMemory(def)` | "这次委派把我累坏了 / 收获不错"，会自然消退 |
| **情境型 situational** | `workerClass` 指向自写 `ThoughtWorker`，实时计算 | "我正在急采"这种**进行中**状态，不需要每天重复授予 |

记忆型模板（原版 `SleptOnGround` `[验证]`）：`stages[0].baseMoodEffect = -4`、`durationDays = 1`、`stackLimit = 1`、`stackedEffectMultiplier = 0.75`、`nullifyingPrecepts = ["RoughLiving_Welcomed"]`。

建议的委派心情表：

> ⚠️ **下表是 S4 时期的草案，已被 §19.25 取代。** 关键差异：草案里的"野外扎营 −3 ~ −5"
> 会把基线做成负收益（心情从 32% 起步），最终**没有采用**；模式心情也不是这张表的数字。
> 以 §19.25.2 的四个模式数值为准。

| 触发 | 类型 | 心情 | 说明 |
|---|---|---|---|
| 野外扎营（每天） | memory ×1/天 | −3 ~ −5 | 原版车队**不会**给"地上睡觉"，所以纯新增，不会双重扣分 |
| 全天轮班（每天） | memory ×1/天 | −6 | 对应 §6.6 的代价；`stackLimit 1` + `durationDays 1`，不叠加 |
| 急采模式（进行中） | situational | −4 | `ThoughtWorker` 读当前模式；停机/撤离即消失 |
| 采空完成 | memory ×1 | +5，`durationDays 2~3` | 一次性正反馈；价值越高给越多（可复用 `MineStrikeManager.MineableIsValuable` 的分级） |
| 中途失败 / 超时撤离 | memory ×1 | −6 | 让"派错人"有情绪后果 |
| 队友在委派中死亡 | memory ×1 | −8 ~ −10 | 原版队友死亡已有 thoughts，注意别重复授予 |

**顺带提醒：委派必须自己发技能经验。** 原版挖矿的 XP 在 `JobDriver_Mine` 里：

```csharp
if (actor.skills != null && (mineTarget.Faction != actor.Faction || actor.Faction == null))
    actor.skills.Learn(SkillDefOf.Mining, 0.07f * (float)delta);
```

世界 pawn 不跑 Job ⇒ 委派期间**一分 XP 都不涨**。照抄这个系数即可零标定：`pawn.skills.Learn(SkillDefOf.Mining, 0.07f × 实际挥镐 tick 数)`（`SkillRecord.Learn` 自带热情/学习率修正）。不给的话，玩家会发现"派出去干十天回来一点没长"，是明显的负反馈 bug。

---

## 7. 三个必做的边界

### 7.1 防"双吃"（已实装：存量挂在地点上 + 采空即销毁）

委派采完后 `GenStep_PreciousLump` 仍会用 `parms.preciousLumpResources` 生成真矿脉；**把它置 null 也没用**（fallback 到 `mineables.RandomElement()`，照样出矿）。所以做了两层：

**① 存量（deposit）挂在地点上，跨多次委派累计扣减** —— `DelegationDeposit` 是 `WorldObjectComp_Delegations` 的字段：

```csharp
public class DelegationDeposit : IExposable   // 属于事件点，不属于某一次委派
{
    public DelegationDef def;
    public ThingDef resourceDef;    // 矿种
    public float rolledUnits;       // 原始掷值（价值 3500–5000）
    public int totalUnits;          // 总格数
    public int yieldPerUnit;        // 每格 EffectiveMineableYield
    public float unitsMined;        // ★ 累计已采（跨多次委派，保留小数）
    public int unitsDelivered;      // 累计已交付
    public int timesDelegated;
    public int UnitsRemaining => totalUnits - FloorToInt(unitsMined);
    public bool IsDepleted => unitsMined >= totalUnits - 0.0001f;
}
```

- `EnsureDeposit(def)` 只在第一次需要时掷定，之后**预览与开工都用同一份**（对话框不再"预览掷一次、开工再掷一次"）
- 每次 `Tick` 里 `dep.unitsMined += 本 tick 实际采掉的格数`（受 `UnitsRemaining` 封顶）
- ⇒ 中止后重新委派**接着采**，不会重掷；`timesDelegated` / `unitsDelivered` 也可显示"这个点被采过"
- 早期版本的坑（已修）：`rolledValue/totalCells` 放在 `Delegation` 上 ⇒ "挖一点 → 中止 → 重委派"可以无限刷矿

**② 采空即销毁**：`destroyTargetOnComplete = true` → 真的采空时 `site.Destroy()`；地点消失会顺带让原版 `QuestNode_NoWorldObject` 结束任务。
按天数/配额收工但**没**采空 → 地点保留，并把原版失效计时恢复回去（S14 起同时**封禁进入**，见 §7.3）。

**③ 天然利好**：`Site.GetFloatMenuOptions` / `WorldObject.GetFloatMenuOptions` 在 `HasMap == true` 时不产出委派入口。

> ~~仍未堵住的一处（需要 GenStep patch，P2）~~ **S5 已堵住作业期间那一半**：存量算出来的总格数与"如果玩家真去生成地图，原版会掷出的格数"是**两次独立掷骰**；采空路径由销毁解决，未采空就进图的话两边数字可能不一致 —— 所以作业期间与动过之后都禁止进入（§7.3）。

### 7.3 作业期间禁止进入（S5 实装）

**症状**（试玩反馈）：车队正在某个地点委派作业时，右键该地点仍然能看到原版的「接近XX」，点进去就进了地图。

**根因** `[验证]`：
- 进图入口是原版 `CaravanArrivalAction_VisitSite`：`Label => site.ApproachOrderString`（中文即「接近XX」）、`CanVisit` 只检查 `site.EnterCooldownBlocksEntering()`。
- 我们此前**只在 `Complete()` / `Abort()` 里**调 `ApplyEntryBlock()` —— 也就是"干完之后才封"，作业期间一次都没封。
- 后果不只是"少了一层保护"：`GenStep_PreciousLump` 会按原版公式**再掷一份**矿（双吃），而且 `TickDelegation` 会因为"队员已被 spawn 到地图上"误报「车队被事件拉进了地图（多半是遇袭）」——明明是自己走进去的。

**修法**（仍然零 Harmony，复用原版组件）：

| 时机 | 动作 |
|---|---|
| `StartDelegation` + 每 tick | `EnsureEntryBlockedWhileActive()`：`EnterCooldownComp.Start(0.5 天)`，剩余不足一半就续期 ⇒ 进行中 `BlocksEntering` 恒为真 |
| `Complete` / `Abort` | 原有的 `ApplyEntryBlock()`（动过存量 + Def 要求 ⇒ `Start(blockMapEntryDays)`）之后，再跑 `ReleaseEntryBlockIfUnwarranted()` |
| 释放规则 | **没动过存量**（`deposit.unitsMined <= 0`）⇒ 把**我们起的**那次冷却 `Stop()` 掉；真的动过存量就把封禁留着 |

**S14 扩展（用户口径：事件点被委派动过之后只走委派路线）** `[验证]`：

- 触发问题（用户原话）：「**当一个事件点进行过委派任务后，这个事件点是否还能够进入？如果还能够，请尝试禁止进入地图。**」
- 逐 Def 核对下来，S14 之前**只有采矿会封** —— `blockMapEntryAfterWorked` 全仓库只有矿点写了 `true`；物资藏匿点与两条营救只靠"动过就删地点"这一层。于是**装过车但没取空**（`WorkerEndReason` = "车队已装满" ⇒ 地点保留）时，右键那个地点的「接近XX」照旧可用 = 绕过委派的第二条路。
- 改法：`DelegationDef.blockMapEntryAfterWorked` 默认值 `false` → **`true`**（漏配置倒向安全侧，见字段注释），并在四条 Def 上显式写 `<blockMapEntryAfterWorked>true</blockMapEntryAfterWorked>` 让数据侧可见。判据不变，仍然是**真的动过存量**（`deposit.unitsMined > 0`）⇒ "点开委派看一眼就取消"不封。
- 营救两条**刻意不放宽"动过"的判据**：救到人（`d.deposit.unitsMined += 1`）才算动过，而那时 `TargetDepleted` 成立 ⇒ 地点直接销毁，开关实际几乎不生效。理由：委派失败时人还押在里面，玩家自己进图强攻是合法退路，不该被一次失败的委派堵死。
- 封禁只挡**原版**的「接近XX」，**不挡我们自己的委派菜单**（`WorldObjectComp.GetFloatMenuOptions` 不看这个冷却）⇒ 没取空的地点，剩下的目标仍然可以再派一次委派去取。

- 为什么"进行中"固定用 0.5 天而不是 Def 的 `blockMapEntryDays`：那个值是"干完之后"的封禁时长（采矿 = 9999 天），拿它当进行中的剩余时间，玩家会看到原版提示"还需 9999 天才能进入"。
- 为什么必须记 `entryBlockSelfStarted`（新字段，已 Scribe）：`EnterCooldownComp` 是**共享**的（原版失效计时、别的 mod 都可能用），我们只被允许撤销自己起的那一次。
- `EnterCooldownComp.BlocksEntering` 在 `ParentHasMap` 时恒为 false，`PostMapGenerate` 还会自己 `Stop()` ⇒ 已经有地图的地点天然不用封，`EnsureEntryBlockedWhileActive` 里也直接跳过 `site.HasMap`。

### 7.2 暂停 30 天计时（决策③）

**坑**：矿点的 30 天计时**不是** Site def 上那个 `TimeoutComp`（`WorldObjectCompProperties_Timeout`，那是给别的任务用的），而是任务里的 **`QuestPart_WorldObjectTimeout`** `[验证]` —— `Site.WorldObjectTimeoutTicksLeft` 的实现就是遍历 `Find.QuestManager.QuestsListForReading`，找 `quest.State == Ongoing` 且 part 为 `QuestPart_WorldObjectTimeout { State: Enabled }` 且 `worldObject == this` 的那个 part，返回其 `TicksLeft`；找不到返回 `-1`。

**⚠️ v0.2 修正（S0 期间核实）**：`QuestPartActivable.State` 是**只读**的 ——

```csharp
public abstract class QuestPartActivable : QuestPart
{
    private QuestPartState state;                 // 私有
    public QuestPartState State => state;         // 只读
    protected virtual void Disable() { ... }      // protected
    protected virtual void Enable(SignalArgs a) { ... }
    public override void Notify_QuestSignalReceived(Signal signal)   // ★public
    {
        if (signal.tag == inSignalEnable && (state == QuestPartState.NeverEnabled || (state == QuestPartState.Disabled && reactivatable)))
            Enable(signal.args);
        else if (state == QuestPartState.Enabled)
        {
            if (signal.tag == inSignalDisable) Disable();             // ★原版的停表路径
            else ProcessQuestSignal(signal);
        }
    }
}
```

所以 `questPart.State = QuestPartState.Disabled` **编译不过**。正确的零 Harmony / 零反射做法是**发原版自己用的那个信号**：

```csharp
part.Notify_QuestSignalReceived(new Signal(part.inSignalDisable, false));   // 扫描仪任务里 inSignalDisable = "site.MapGenerated"
```

- **暂停 ✅ 零 Harmony / 零反射**（S3 已实装）：`DelegationUtility.TryPauseTimeout(site, out remaining, out report)`
  ⚠️ **剩余时间必须在发信号之前读** —— `QuestPart_Delay.TicksLeft` 的源码是
  ```csharp
  public int TicksLeft => (base.State != QuestPartState.Enabled) ? 0 : enableTick + delayTicks - Find.TickManager.TicksGame;
  ```
  禁用后恒返回 0。所以 DEV gizmo 在暂停状态下打印出 `TicksLeft=0` 是**正常的**，不是故障。
- **恢复 ⚠️ 必须反射一次**（S3 已实装）：该任务节点 `inSignalEnable` 为 null 且 `reactivatable = false`，信号无法重新 Enable。实现是
  ```csharp
  FieldInfo f = AccessTools.Field(typeof(QuestPartActivable), "state");   // 唯一要反射的东西
  f.SetValue(part, QuestPartState.Enabled);
  part.delayTicks += pausedTicks;      // TicksLeft = enableTick + delayTicks - now
                                       // ⇒ 把"被暂停掉的时长"补进公开字段就行，不必碰 enableTick
  ```
  反射失败时只打日志、不影响委派本身（最坏结果是地点按原版时间照常到期）。
- **完成路径根本不需要恢复**：采空即 `site.Destroy()`，地点连同计时部件一起消失。只有"中断"与"按天数/配额收工但没采空"才恢复。

**DEV 验证工具**（选中地点，开发者模式）：`打印超时计时器` / `发信号暂停超时计时` / **`恢复超时计时`（S3 新增）** / `委派推进 1 天` / `立即完成委派`。

> 顺带记录：Site def 上确实有一个 `EnterCooldownComp`（`autoStartOnMapRemoved = true`、`durationDays = 1`），控制"离开地点后 1 天内不能再进入"。`CaravanArrivalAction_VisitSite.CanVisit` 会检查 `site.EnterCooldownBlocksEntering()`。我们的 `StillValid` 应照抄这个检查模式。
> 另一条 `SitePartDef.forceExitAndRemoveMapCountdownDurationDays = 11`（PreciousLump）只在进图后生效，委派路线天然不触发（利好）。

---

## 8. 第一条 def：矿点委派

`[建议]` XML 草案：

```xml
<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <RimDelegation.DelegationDef>
    <defName>RimDelegation_MinePreciousLump</defName>
    <label>开采</label>
    <description>把矿点委派给车队：他们会在原地扎营开采，按天消耗营养，采空后收工。</description>
    <workerClass>RimDelegation.DelegationWorker_Mining</workerClass>

    <targetSitePartTags>
      <li>PreciousLump</li>
    </targetSitePartTags>

    <minPawns>1</minPawns>
    <maxPawns>6</maxPawns>
    <skillDef>Mining</skillDef>
    <workRateStat>RimDelegation_WorkRatePerDay</workRateStat>

    <!-- worker 读 SitePart.parms.preciousLumpResources 推矿种与格数，不需要生成地图 -->
    <yieldMode>MineableLump</yieldMode>

    <defaultMode>RimDelegation_Mode_Standard</defaultMode>
    <modes>
      <li>RimDelegation_Mode_Standard</li>
      <li>RimDelegation_Mode_Rush</li>
      <li>RimDelegation_Mode_Careful</li>
    </modes>

    <handleTargetTimeout>Pause</handleTargetTimeout>
    <destroyTargetOnComplete>true</destroyTargetOnComplete>
    <abortIfCaravanLeavesTile>true</abortIfCaravanLeavesTile>

    <letters>
      <start>…</start>
      <complete>…</complete>
      <abort>…</abort>
    </letters>
  </RimDelegation.DelegationDef>
</Defs>
```

矿种与总量获取路径 `[验证]`：

| 需要什么 | 从哪拿 |
|---|---|
| 矿种 | `site.parts` 里 `def == SitePartDefOf.PreciousLump` 的那个 part → `part.parms.preciousLumpResources`（`ThingDef`） |
| 矿点价值区间 | `GenStepDefOf.PreciousLump.genStep` → cast 成 `GenStep_PreciousLump` → `totalValueRange`（3500–5000） |
| 单格产出 | `preciousLumpResources.building.mineableYield` × `EffectiveMineableYield` 逻辑（`mineYieldFactor`） |
| 矿格数 | `max(round(V / (mineableYield × mineableThing.BaseMarketValue)), 1)`（复刻 `GenStep_PreciousLump.Generate`） |

---

## 9. 泛化路线图

| 阶段 | def | 目标站点 / 部件 | 关键素材 `[验证]` |
|---|---|---|---|
| P0 | `RimDelegation_MinePreciousLump` | `PreciousLump` | 本文 §1 |
| P1 | `RimDelegation_TakeItemStash` | `ItemStash` | `Site` def 已挂 `Planet.ItemStashContentsComp` |
| P1 | `RimDelegation_Logging` / `_Hunting` / `_Farming` | Ideology `WorkSite_Logging` / `_Hunting` / `_Farming`（tag `WorkSite`） | `SitePartWorker_WorkSite` 的 `lootTable` 已给权重（Mining 版：Steel .8 / Plasteel .3 / Uranium .3 / Gold .15 / Silver .15 / Jade .15 / ComponentIndustrial .15）、`PointsMarketValue`、`forceExitAndRemoveMapCountdownDurationDays = 4`、`minThreatPoints = 350`、`requiresFaction = true` |
| P2 | `RimDelegation_SurveyRuins` | `AbandonedSettlement` / `AncientStockpile` / `OpportunitySite_*` | Odyssey 系列 SitePartDef |
| P2 | `RimDelegation_MineAsteroid` | Odyssey `AsteroidMiningSite`（`ResourceAsteroidMapParent`） | `SitePartWorker_Asteroid` 也读 `preciousLumpResources` ⇒ 同一条 worker 逻辑可复用 |
| P2 | `RimDelegation_RaidWorkSite` | Ideology `WorkSite_*` | 复用 `SitePartDef.lootTable` |
| P3 | 与 RimOre 联动 | 委派采回的矿石按 RimOre 成分配比表进产线 | `RimOre_LowGradeOre` 只有星际线能吃，正好当"远程大矿点"的产物 |

**框架验收标准**：P1 的每一类事件，**只加 XML def、不加 C# 代码**就能跑通。

---

## 10. 兼容与踩坑清单

| 风险 | 处理 |
|---|---|
| 30 天计时在 `QuestPart_WorldObjectTimeout` 而不是 `TimeoutComp` | §7.2；先实测 `State = Disabled` 是否真停表 |
| 逻辑 1 的 gizmo 在车队**路过**事件点那一 tick 也会出现 | `GetCaravanGizmos` 里加 `!caravan.pather.MovingNow` 或"仅在车队停住时显示"的判定 `[建议]` |
| 预先委派计划失效 | `StillValid` 必须真的判（矿点没了 / 已进图 / 已有别的委派 / `EnterCooldownBlocksEntering()`），原版 `PatherArrived` 会优雅退化 |
| 读档丢委派 | `Caravan_PathFollower` 已自带 `Scribe_Deep(arrivalAction)` `[验证]`；`WorldObjectComp.PostExposeData` + `Scribe_References`（`caravan` / `site`）也要写全 |
| 车队合并 / 拆分 / 被吞 | `Caravan.Notify_Merged` 是 `virtual` `[验证]`；委派绑的是 `caravan` 引用，必须能检测到车队消失并转 Aborted |
| 别的 mod 改过 `PreciousLump` 的 GenStep | 只读 `SitePartParams.preciousLumpResources`，**不要自己 roll 矿种** |
| 车队停在矿点上被原版事件打 | 这是特性不是 bug；用"遇袭即撤 / 坚守"模式暴露给玩家 |
| 委派中人被移出车队 / 死亡 / 倒地 | 每次 tick 重算人力，速率随之变化；全灭则 Aborted |
| 玩家点「进入地点」 | 与委派入口并存，不冲突；一旦 `HasMap` 则委派入口自动消失 |
| 多人同步 / 存档兼容 | v1 明确不做；改委派语义时记得迁移旧存档字段 |

---

## 11. 实施顺序（结合本机工程）

| 阶段 | 内容 | 验收点 |
|---|---|---|
| **S0** ✅ | 复制 `DllModTemplate` → `RimDelegation`，改 `About.xml` 的 packageId、`ModBoot.HarmonyId`、`csproj` 的 AssemblyName | **已完成**，见 §15 |
| **S1** ✅ | 空框架：`DelegationDef` + `WorldObjectComp_Delegations` + `CaravanArrivalAction_StartDelegation` + `Dialog_ChooseDelegation`，**只发信不产出** | **已完成**，见 §15 的验收步骤 |
| **S2** ✅ | 矿点真实结算：速率 / 产出 / `AddPawnOrItem` 交付 | **已完成**，见 §15 |
| **S3** ✅ | 暂停计时（§7.2）+ 完成即 `site.Destroy()` | **已完成**，见 §15 |
| **S4** ✅ | 委派模式（§4 的强度 × 结束条件）+ 心情 + 异常状态机（车队离开 / 死人 / 断粮 / 遇袭） | **已完成**，见 §15 |
| **S5** ✅ | 第二条委派 `ItemStash`（搜刮）+ 营救两条（囚犯/难员）+ 威胁评估；本轮再补：作业期间封锁进图、藏匿点清单口径、措辞/量纲交回 worker | **已完成**，见 §15 的 S5 段 |
| **S6** | 框架继续泛化：`WorkSite_*` → 废弃据点 | **加 XML 不加代码** |

`[建议]`工程约定（沿用本机已验证环境）：
- 经典 csproj / `net472` / 无 NuGet（本机 `api.nuget.org` 不可达）
- Harmony 从 `brrainz.harmony` Mod 取 `0Harmony.dll`（1.6 游戏 `Managed` 目录里没有）
- `[StaticConstructorOnStartup]` 里 `Harmony.PatchAll`；HarmonyId 用 packageId
- 源码必须 UTF-8；`<CodePage>65001</CodePage>`
- **S1–S2 目标为零 Harmony**（全部靠 `WorldObjectComp` + `CaravanArrivalAction`）；只有 S3 路径 1 实测失败才引入 Harmony

---

## 12. 待实测 / 待验证清单

| # | 待验证的事 | 影响 | 怎么验 |
|---|---|---|---|
| 1 | 发 `inSignalDisable` 信号停表后，30 天计时**是否真的不再倒数**；以及反射恢复是否精确续上 | 决定 S3 的暂停/恢复是否真的可用 | 已内置 3 个 DEV gizmo：「打印超时计时器」→「发信号暂停」→ 隔几天再打印（`TicksLeft` 会显示 0，这是原版实现，看 `State` 与地点是否存活）；再点「恢复超时计时」看 `TicksLeft` 是否回到暂停时的剩余量 |
| 2 | ~~`StartPath` 到当前所在格的行为~~ | ~~决定逻辑 1 能否复用逻辑 2 的代码~~ | **已解决 `[验证]`**：`AtDestinationPosition() => caravan.Tile == destTile` ⇒ 立即 `PatherArrived()` |
| 3 | `WorldObject.GetComponent<T>()` 是否存在 | 取 comp 的写法 | 查成员表；无则 `parent.comps.OfType<T>()` |
| 4 | ~~每秒基础 Mining 伤害 / 单格实际耗时~~ | ~~决定委派时长模型~~ | **已解决 `[验证]`**：见 §6.3 —— `19 × round(100 / MiningSpeed)` ticks/格，无需经验常数 |
| 5 | 人日均营养消耗精确值 | 只影响预计天数显示 | 读 `RaceProps.baseHungerRate`(人类 1.0) × `HungerRate` |
| 6 | 车队停在 `Site` 格上时原版事件的命中频率 | 平衡 | 长跑实测 |
| 7 | 逻辑 1 gizmo 在"路过"时的表现 | UX | 实测是否闪烁；按 §10 加 `MovingNow` 判定 |
| 8 | 长时间停在矿点上时 `Need_Rest` 是否稳定在满值 | 影响"车队里的人会不会疲劳"与心情 debuff 设计 | 实测：停在矿点跑几天，看休息条与 `TrySatisfyRestNeed` 的净效果 |

---

## 13. 关键 API / memberId 速查

> memberId 前缀是当前 `rw16` 上下文的 MVID `61e4173561894da49d210260257b5097`（游戏更新后会变；变了就按类型名重新 `search_symbols`）。

### 13.1 事件与数据

| 类型 / 成员 | memberId | 用途 |
|---|---|---|
| `RimWorld.CompLongRangeMineralScanner` | `02002F2F:T` | 事件源头 |
| `RimWorld.CompLongRangeMineralScanner.DoFind` | `06011129:M` | 任务生成 |
| `RimWorld.GenStep_PreciousLump` | `0200208B:T` | 生成公式 |
| `RimWorld.Planet.SitePartWorker_PreciousLump` | `02003E1D:T` | 部件行为 |
| `RimWorld.Planet.SitePartParams.preciousLumpResources` | `0400F149:F` | 矿种（已存盘） |
| `RimWorld.QuestGen.QuestNode_GetDefaultSitePartsParams.SetVars` | `060147A5:M` | 矿种写入点 |
| `RimWorld.Mineable` | `02002985:T` | 产出公式、`yieldPct`（部分开采语义） |
| `RimWorld.BuildingProperties.EffectiveMineableYield` | `17001670:P` | 产出公式 |
| `RimWorld.JobDriver_Mine` | `020012D0:T` | ★采矿速率全部常量与公式（100 ticks / 80 伤害 / NPC 下限 0.6） |
| `RimWorld.StatWorker.GetValueUnfinalized` | `06013AAA:M` | ★`skillNeedFactors` 是乘法 |
| `RimWorld.StatWorker.FinalizeValue` | `06013AAD:M` | ★`Mathf.Clamp(val, minValue, maxValue)` |
| `RimWorld.SkillNeed_BaseBonus` | `020019D5:T` | `0.04 + 0.12 × 等级` |

### 13.2 两个入口（本文核心）

| 类型 / 成员 | memberId | 用途 |
|---|---|---|
| `RimWorld.Planet.Caravan.GetGizmos` | `060156B6:M` | 逻辑 1 的上游：`ObjectsAt(Tile)` → `GetCaravanGizmos` |
| `RimWorld.Planet.WorldObject.GetCaravanGizmos` | `06015CA3:M` | 逻辑 1 的注入点（聚合 comps） |
| `RimWorld.Planet.WorldObject.GetFloatMenuOptions` | `06015CA4:M` | 逻辑 2 的注入点（聚合 comps） |
| `RimWorld.Planet.Site.GetFloatMenuOptions` | `06015AE4:M` | 逻辑 2 的上游（`HasMap` 时 yield break） |
| `RimWorld.Planet.WorldObjectComp` | `02003E65:T` | 虚方法全表 |
| `RimWorld.Planet.CaravanArrivalAction_VisitSite` | `02003D56:T` | 照抄模板（`Label`/`ReportString`/`StillValid`/`Arrived`） |
| `RimWorld.Planet.CaravanArrivalActionUtility` | `02003D42:T` | 菜单构造 + `StartPath` |
| `RimWorld.Planet.Caravan_PathFollower.StartPath` | `0601581B:M` | ★`AtDestinationPosition()` → `PatherArrived()` 统一入口 |
| `RimWorld.Planet.Caravan_PathFollower.PatherArrived` | `06015822:M` | ★抵达回调 + `StillValid` 守门 |
| `RimWorld.Planet.Caravan_PathFollower.AtDestinationPosition` | `0601582D:M` | ★`caravan.Tile == destTile` |
| `RimWorld.Planet.Caravan_PathFollower.ExposeData` | `0601581A:M` | 预先委派读档安全 |

### 13.3 车队与结算

| 类型 / 成员 | memberId | 用途 |
|---|---|---|
| `RimWorld.Planet.Caravan.AddPawnOrItem` | `060156B0:M` | 产出交付 |
| `RimWorld.Planet.Caravan.TickInterval` | `060156AC:M` | 离图 tick 顺序 |
| `RimWorld.Planet.Caravan_NeedsTracker.TrySatisfyPawnsNeeds` | `060158E7:M` | 自动吃饭 |
| `RimWorld.Planet.CaravanMaker.MakeCaravan` | `02003D87:T` | 造车队 |
| `RimWorld.Planet.ForagedFoodPerDayCalculator` | `02003DA5:T` | "技能→离图日产"范式（含夜休系数） |
| `RimWorld.Planet.Caravan_NeedsTracker.TrySatisfyPawnNeeds` | `060158E8:M` | ★车队里每种需求的分发 |
| `RimWorld.Planet.Caravan_NeedsTracker.TrySatisfyRestNeed` | `060158E9:M` | 停车即回休息 |
| `RimWorld.Planet.Caravan_NeedsTracker.TrySatisfyFoodNeed` | `060158EA:M` | 从车队库存进食 / 断粮 `ThreatBig` |
| `RimWorld.Planet.Caravan_NeedsTracker.TrySatisfyJoyNeed` | `060158EF:M` | 娱乐（仅冥想 / 社交） |
| `RimWorld.Planet.Caravan_NeedsTracker.GetAvailableJoyKindsFor` | `060158F3:M` | 可用娱乐种类 |
| `RimWorld.Planet.Caravan_NeedsTracker.GetCurrentJoyGainPerTick` | `060158F0:M` | ★移动中娱乐收益 = 0 |
| `RimWorld.Planet.Caravan.NightResting` | `1700356E:P` | ★夜幕判定（可复用为工时门控） |
| `RimWorld.Planet.CaravanNightRestUtility` | `02003D8B:T` | ★6:00 起床 / 22:00 休息 / 1 小时 = 2500 ticks |
| `RimWorld.Planet.Caravan_BedsTracker` | `02003DB7:T` | 车队床铺 → 舒适度（不给心情想法） |
| `Verse.Pawn.TickInterval` | `06003098:M` | ★世界 pawn 的 tick 面（健康 + 精神，无 Job） |
| `Verse.AI.Pawn_MindState.MindStateTickInterval` | `06006405:M` | 精神崩溃在车队里照常发生 |
| `RimWorld.Planet.Caravan_NeedsTracker.TrySatisfyRestNeed` | `060158E9:M` | ★车队"停车即补休息"的元凶（本 mod 唯一 Harmony 补丁的目标） |
| `RimWorld.Need_Rest.TickResting` | `0600CBAF:M` | 只打标记、不推进数值 |
| `RimWorld.Need_Rest.Resting` | `17001F13:P` | `TicksGame < lastRestTick + pawn.UpdateRateTicks` |
| `RimWorld.Planet.WorldObjectsHolder.AllWorldObjects` | `1700369F:P` | `DelegationRegistry` 每 tick 重建反向索引用 |

### 13.5 远行队页签（UI）

| 类型 / 成员 | memberId | 用途 |
|---|---|---|
| `RimWorld.Planet.WITab` | `02003E86:T` | 页签基类（已提供 `StillValid` / `PaneTopY` / `CloseTab` / `SelCaravan`） |
| `Verse.InspectTabBase` | `020001AF:T` | `labelKey` / `IsVisible` / `FillTab` / `UpdateSize` 的来源 |
| `RimWorld.InspectPaneUtility.DoTabs` | `060129C6:M` | ★`if (tab.IsVisible)` 同时管住按钮与面板 |
| `RimWorld.Planet.WorldObject.GetInspectTabs` | `06015CA7:M` | 返回 `def.inspectorTabsResolved` |
| `RimWorld.Planet.WorldObjectDef.inspectorTabs` | `04006C5F:F` | 要 patch 的那个字段 |
| `RimWorld.Planet.WITab_Caravan_Needs` | `02003E93:T` | 照抄模板（ctor 里设 `labelKey`） |
| `RimWorld.Need_Mood.MoodString` | `17001F07:P` | 心情角标 tooltip 的心情档位文字 |
| `RimWorld.HediffSet.PainTotal` | `1700088E:P` | 受伤角标 |
| `Verse.Widgets.DrawBoxSolidWithOutline` | `06004F33:M` | 角标底色（不依赖任何贴图路径） |
| `Verse.Widgets.InfoCardButton(Single, Single, WorldObject)` | `06004FB0:M` | 地点本体的信息卡按钮 |
| `Verse.Widgets.FillableBar(Rect, Single)` | — | 页签里的进度条 |

### 13.4 站点、计时与运输

| 类型 / 成员 | memberId | 用途 |
|---|---|---|
| `RimWorld.Planet.Site` | `02003DFF:T` | `parts` / `HasMap` / `Destroy` / `WorldObjectTimeoutTicksLeft` |
| `RimWorld.Planet.WorldObject` | `02003E42:T` | `comps` 字段 |
| `RimWorld.Planet.WorldObject.TickInterval` | `06015C94:M` | comp tick 证据 |
| `RimWorld.Planet.WorldObject.GetGizmos` | `06015CA2:M` | comp gizmo 聚合 |
| `RimWorld.Planet.TimeoutComp` | `02003E5F:T` | Site def 上的另一个计时器（别搞混） |
| `RimWorld.QuestPart_WorldObjectTimeout` | `02001BF6:T` | 真正的 30 天计时 |
| `RimWorld.QuestPart_Delay.delayTicks` | `04006D89:F` | 延长计时的落点 |
| `RimWorld.Planet.TransportersArrivalAction` | `02003E26:T` | v2「运输舱直达但不进图」的扩展点 |
| `RimWorld.Planet.TransportersArrivalAction_FormCaravan` | `02003E2E:T` | `GeneratesMap => false` 的原版范式 |

---

## 14. 与现有工程的关系

- **`RimOre`**（`D:\Game\Rimworld Dev\RimOre`）：选矿冶金链，**互补不冲突**。接口建议用 `DefModExtension` 单向挂钩，零耦合：委派产出可以是 `RimOre_*Ore`；反之 RimOre 的"低品位矿"可作为远程大矿点委派的奖励池。
- **`DllModTemplate`**（`D:\Game\Rimworld Dev\DllModTemplate`）：S0 的脚手架来源，含 `build.ps1` / `deploy.ps1` / ModSettings / Harmony 引导。About.xml 需声明 `modDependencies: brrainz.harmony`（为 S3 兜底；若 S3 走通零 Harmony 可去掉）。
- 本文档的写法（`[验证]` / `[建议]` 标注）沿用 `RimOre/DESIGN.md` 的约定。

---

## 15. 实施状态

### S0 —— 已完成 `[验证]`

| 项 | 结果 |
|---|---|
| 工程 | `RimDelegation/`（About / Source / Defs / Patches / build.ps1 / deploy.ps1 / DESIGN.md），经典 csproj + net472，无 NuGet 还原 |
| 编译 | `build.ps1` → `Assemblies\RimDelegation.dll` **39936 字节**；引用 mscorlib / Assembly-CSharp / System.Core / UnityEngine.CoreModule / UnityEngine.TextRenderingModule / 0Harmony |
| 部署 | `deploy.ps1` → `<Steam>\steamapps\common\RimWorld\Mods\RimDelegation`（robocopy /MIR，排除 `Source\obj`、`Source\bin`） |
| 入口 | `RimDelegationMod`（Mod + 设置界面）、`RimDelegationSettings`（verboseLogging / requireConfirmOnArrival）、`ModBoot`（Harmony 引导；**S1 期间补丁数 = 0**） |

> 编译期踩到的两个坑（已修）：① 游戏类型的命名空间是 `RimWorld.Planet`（`Site` / `Caravan` / `PlanetTile` / `WorldObjectComp`）与 `RimWorld`（`SitePartDef` / `SkillDef` / `GenDate` / `FloatMenuAcceptanceReport`），每个文件都要显式 `using`；② `string.Formatted(params object[])` 不可用（会解析到 `NamedArgument` 重载），传数组时改用 `string.Format`。

### S1 —— 已完成（只发信不产出）

| 文件 | 作用 |
|---|---|
| `Defs/RimDelegation_Delegations.xml` | 3 个 `DelegationModeDef`（只白天 6–22 / 早出晚归 5–23 / 全天轮班 24h）+ 第 1 条 `DelegationDef`（`PreciousLump`） |
| `Patches/RimDelegation_SiteComps.xml` | 给 `WorldObjectDef[Site]/comps` 追加 `WorldObjectCompProperties_Delegations` |
| `WorldObjectComp_Delegations` | 逻辑 1 `GetCaravanGizmos` + 逻辑 2 `GetFloatMenuOptions` + `CompTickInterval` 结算 + inspect + 存读档 + 完成/中断 |
| `CaravanArrivalAction_StartDelegation` | 两条入口的共同落点；`StillValid` 守门；`ExposeData` 让预先委派读档安全 |
| `Dialog_ChooseDelegation` | 选人 + 选模式（工时）+ 预计天数；**预计总量只掷一次并缓存**，避免每帧重掷导致数字跳动 |
| `DelegationWorker_Mining` | 复刻 `JobDriver_Mine`：`19 × round(100 / MiningSpeed)` 格耗时 + `0.07 × delta` 技能 XP + 复刻 `GenStep_PreciousLump` 的矿格数公式 |
| `DelegationUtility` | def 匹配 / 资格判定 / 参与者求交集（车队被拆分合并的兜底）/ `TryPauseTimeout` |
| `DebugGizmos_Delegations` | 4 个 DEV gizmo：打印计时器 / 发信号暂停计时 / 推进 1 天 / 立即完成 |

**尚未实装**（后续阶段）：`site.Destroy()` 防双吃（S3）、心情 thoughts 与 mode 的心情代价（S4）、中断后恢复原版计时（S3）。

### S2 —— 已完成（真实产出交付）`[验证]`

产出模型完全照抄原版两条公式，没有自创常数：

```csharp
// 单格产出（复刻 Mineable.Notify_TookMiningDamage：总伤害 = MaxHitPoints ⇒ yieldPct = MiningYield）
oreThisDelta += cells * d.yieldPerCell * p.GetStatValue(StatDefOf.MiningYield) * dropChance;
//   cells           = delta / TicksPerCell(p) * mode.workRateMultiplier   （每人挖自己那一格 → 并行）
//   yieldPerCell    = BuildingProperties.EffectiveMineableYield = round(mineableYield × difficulty.mineYieldFactor)
//   dropChance      = building.mineableDropChance（原版矿物 = 1）
```

| 决策 | 做法 | 理由 |
|---|---|---|
| **交付节奏** | 每 **2500 ticks（1 小时）** 把已采出的部分 `FlushDeliveries` 进车队库存；完成与中断时再各 flush 一次 | ① 中断天然就是"带已采部分撤回"，不需要额外的部分开采公式；② 玩家能看着负重涨，而不是干到最后才发现搬不动 |
| **交付接口** | `caravan.AddPawnOrItem(thing, false)` → `CaravanInventoryUtility.GiveThing` | 复用原版车队库存的负重分摊与 UI |
| **拆堆** | 按 `ThingDef.stackLimit` 拆成多堆 | 避免生成超大堆叠 |
| **越权开采** | `mineableYieldWasteable = false` 的矿物改成"整格采完才出货"（按整格数差折算） | 原版只有 wasteable = true 才按 yieldPct 比例出货；原版矿点全是 true，这段是给非原版矿物兜底 |
| **超重保护** | `caravan.ImmobilizedByMass` 首次为真时发一次 `ThreatSmall` 提醒 + inspect 里常驻"车队已超重" | 原版超重会让车队**无法移动**，不能默默把玩家坑在荒野 |
| **信件** | `completeLetterText` / `abortLetterText` 新增 `{2}` = 交付汇总（"已交付 1840 × 钢铁（约 3496 银）"） | 让结果可核对 |

新增字段（全部 Scribe）：`Delegation.oreUnits`（待交付，保留小数余数）/ `oreDelivered`（已交付总数）/ `ticksSinceFlush` / `massWarned`。

### S3 —— 已完成（防双吃 + 计时收尾）

| 项 | 做法 |
|---|---|
| **采空即销毁** | `Complete()` 里 `trulyDepleted = d.TargetDepleted`，只有**真的采空**才 `depleted = true` 并 `site.Destroy()`。地点名先读进局部变量再销毁（信件不能引用已销毁对象） |
| **按计划收工不销毁** | 按天数/配额收工但没采空 → 地点保留，并把原版失效计时**恢复**回去 |
| **暂停计时** | 发 `inSignalDisable`（零 Harmony）；剩余时间在发信号**前**读 |
| **恢复计时** | 反射 `QuestPartActivable.state` 写回 Enabled + 公开字段 `delayTicks += 暂停时长`（精确续上，不碰 `enableTick`）；反射失败只打日志 |
| **销毁的连带效果** | 地点消失会让原版 `QuestNode_NoWorldObject` 结束整个任务 —— 不需要我们手动收尾任务 |

### S4 —— 已完成（结束条件 / 风险姿态 / 心情 / 异常状态机）

| 轴 | 实装 |
|---|---|
| **强度 × 工时** | 3 个 `DelegationModeDef`（只白天 / 早出晚归 / 全天轮班 ×1.1），对话框里循环切换 |
| **结束条件** | 三个单选 + 数字步进：`采空为止` / `按天数 1–maxDaysLimit` / `按产出配额 1–预估总量`（配额上限由 `EstimateYieldUnits` 算出） |
| **风险姿态** | `补给耗尽时中止` 勾选框（正向语义，默认勾选 = `abortWhenOutOfFood = true`，从模式的 `abortWhenOutOfFood` 取初值；取消 = 饿着也继续挖） |
| **心情** | 5 条记忆型 `ThoughtDef`；进行中**每天**给车队里每个人挂一次（`def.dailyMoodThought` + `mode.dailyMoodThought`），收工挂 `completeMoodThought`(+5/2 天)，中断挂 `abortMoodThought`(-6/1 天)。全走 `TryGainMemory`，**不需要 ThoughtWorker，也不需要全局查询谁在委派里** |
| **异常状态机** | ① 车队消失 ② 车队离开目标格 ③ **人员被生成到地图上**（遇袭被拉进图；`Caravan` 是 `WorldObject` 不是 `MapParent`，没有 `HasMap`，所以用"有没有人 `Spawned`"判定）④ 全员死亡/离队 ⑤ 断粮（按风险姿态撤或硬扛 + 提醒一次） |
| **倒地不再永久除名** | S1/S2 曾是 `RemoveAll(p => p.Downed)`，倒地一次就再也回不来 —— S4 改成只在 **死亡 / 离队** 时除名，工作计算时跳过倒地者 ⇒ 治好自动复工 |

**参数传递重构**：新增 `DelegationRequest`（IExposable）打包「模式 + 选人 + 结束条件 + 天数 + 配额 + 风险姿态」，随 `CaravanArrivalAction` 一起 Scribe。轴再多也只改这一个类，不动各处签名。

### 修复记录（S4 之后，来自实际试玩反馈）

#### 修 1：事件点需要计数，避免多次采集

**症状**：中止后重新委派能反复刷出全新的矿藏，且已交付的产出不扣减。
**根因**：`rolledValue / totalCells / cellsMined` 挂在 `Delegation`（一次委派的实例）上，而不是事件点上。
**修法**：新增 `DelegationDeposit`（§7.1），作为 `WorldObjectComp_Delegations` 的字段持久化；`Delegation` 只持有引用（不重复 Scribe，读档时在 `PostLoadInit` 重新挂上）。同时顺带修掉"对话框预览与实际开工不是同一次掷骰"。
**新增 UI**：inspect 里多一行「事件点存量：全点剩余 N/M 格 · 本次可采 K 格 · 已被委派 T 次」，闲置时也显示剩余与累计交付。

#### 修 2：采矿进行时人物一直在休息，休息条不减

**症状**：委派作业期间队员的休息条只涨不跌。
**根因**（已反编译确认）——`Need_Rest` 的两个方法：

```csharp
public void TickResting(float restEffectiveness)          // 只打标记，不推进数值
{ lastRestTick = Find.TickManager.TicksGame; lastRestEffectiveness = restEffectiveness; }

public bool Resting => Find.TickManager.TicksGame < lastRestTick + pawn.UpdateRateTicks;

public override void NeedInterval()                        // 每 150 ticks
{
    if (Resting) CurLevel += 0.005714286f * lastRestEffectiveness * RestRateMultiplier;   // 涨
    else         CurLevel -= RestFallPerTick * 150f * RestFallRateFactor;                // 跌
}
```

而 `Caravan_NeedsTracker.TrySatisfyRestNeed` 在车队**没在移动**时每 tick 都戳一次 `TickResting`：

```csharp
if (!caravan.pather.MovingNow || pawn.InCaravanBed() || pawn.CarriedByCaravan())
    rest.TickResting(pawn.CurrentCaravanBed()?.GetStatValue(StatDefOf.BedRestEffectiveness) ?? ...);
```

⇒ 停车作业期间 `Resting` 恒为真 ⇒ **休息条只涨不跌**。这是原版车队行为的必然结果，不是我们的 bug，但委派"停车干活"正好踩满这个坑。

**修法**：Harmony Prefix 挂在 `Caravan_NeedsTracker.TrySatisfyRestNeed`（private 方法，Harmony 可直接补），**工时段直接跳过**，让 `Resting` 自然转假 → 休息条开始正常下降；收工时段照常调用 → 夜里睡回来。

```csharp
[HarmonyPatch(typeof(Caravan_NeedsTracker), "TrySatisfyRestNeed")]
public static class Patch_CaravanNeedsTracker_Rest
{
    private const float ExhaustionFloor = 0.1f;   // 低于这个比例就不再拦，允许累垮后自己睡
    public static bool Prefix(Pawn pawn, Need_Rest rest, int delta) { ... }
}
```

配套 `DelegationRegistry`：每 tick 重建一次的"谁正在干活"反向索引（不保存跨存档状态，所以没有静态注册表残留问题）。

**数值自检**：`NeedInterval` 每 150 ticks 跑一次 ⇒ 一天 400 次。
- 掉落 = `1.5833333E-05 × 150 × 400` ≈ **0.95 / 天**（≈ 满休息条撑一天）
- 回收 = `0.005714286 × eff × 400` ≈ **2.29 × eff / 天** ⇒ 睡满约 0.44 天 ≈ **10.5 小时**，正好等于原版 `Need_Rest.FullSleepHours = 10.5f` ✓

⇒ 只白天模式 ≈ 正常作息（白天掉、夜里补）；全天轮班则一路掉到 0.1 的地板硬撑。
> ⚠️ **但"硬撑"并没有心情代价** —— `NeedRest` 的疲劳想法对未 spawn 的车队成员不生效（§5.5 展开表）。
> 疲劳必须自己接到工伤率上才有意义，见 §19.25。

> 说明：这也是本 mod 的**第一个 Harmony 补丁**（此前 S1–S4 全是零 Harmony）。补丁只做一件事：工时段不补休息；其余原版逻辑不变。

#### 新增：远行队 inspect 页签「委派」

原来的状态只有"选中矿点"才看得到。现在**选中正在委派的远行队**也有一个独立页签，内容与实现见 §16.8。注册方式同样是 XML patch（`WorldObjectDef[Caravan]/inspectorTabs`），**零 Harmony**；靠 `IsVisible` 做到"只有真正在委派的远行队才出现这个页签"。

#### 新增：四项列表 UI 打磨

| 项 | 实现 |
|---|---|
| **心情 / 受伤小角标** | 头像右侧固定两格 16×16（左心情、右受伤）。心情按 `need.mood.CurLevelPercentage` 四级配色（≥70 绿 / ≥45 黄 / ≥25 橙 / 其余红）；受伤角标只在"有 `Hediff_Injury` 或疼痛>0 或已倒地"时才画（颜色更深）。tooltip 分别给 `心情 62%（心情愉快）`、`受伤 3 处 · 疼痛 24%` |
| **按技能排序** | 「排序：技能（高→低）」循环按钮：技能↓ / 名字 / 心情（差→好）。对话框与页签共用 `DelegationUIUtility.SortPawns`；页签排的是**显示用副本**，不动 `Delegation.participants` 的真实顺序 |
| **全选 / 全不选** | 对话框工具条两个按钮，右侧实时显示 `已勾选 N/M` |
| **地点本体信息卡按钮** | 对话框与页签的标题行右侧各一个（`Widgets.InfoCardButton(x, y, WorldObject)`） |

四项全部抽在 `DelegationUIUtility` 里，对话框与远行队页签共用同一段绘制，不会"改了一处忘了另一处"。

> 关于**角标为什么不用原版的"心情脸"图标**：那套图集在 `ColonistBarColonistDrawer.MoodAtlas` 里（私有字段，索引规则也在内部实现里），引用它等于抄私有实现、且换版本就可能崩。改用 `Widgets.DrawBoxSolidWithOutline(rect, color, Color.black, 1)` + tooltip：零贴图路径猜测、零私有 API 依赖。

#### 新增：五项 UI 增强

| 项 | 关键实现 |
|---|---|
| **As-Is / To-Be 负重** | `Caravan.MassUsage` / `Caravan.MassCapacity` + 产物 `ThingDef.BaseMass`；**只算产物**（不算途中吃掉的补给）。超容量时整行变红并追加 `← 会超重` |
| **结束条件改下拉框** | `[ 当前项 ▼ ]` 按钮 + `FloatMenu` 弹层（当前项带 `✓` 前缀）。**没有直接调 `Widgets.Dropdown`** —— 它的重载签名在反编译里是 `Dropdown(Rect, Target, Func, Func, String, Texture2D, String, Texture2D, Action, Boolean)`，泛型实参（`Target` / `Func<T>`）无法确定，硬猜有编译或运行期风险；改用与原版 dropdown **同款的"按钮 + ▼ + FloatMenu 弹层"交互**，观感与行为一致 |
| **抵达前只给区间** | 新增 `DelegationPreview`（worker 的 `MakePreview(site)`）：由 `totalValueRange` 的 min/max 两端各套一次 `TotalCellsFor` 得到格数区间，ETA 同步变成区间。**关键改动：存量改为抵达时才掷定** —— `EnsureDeposit` 从对话框移到 `StartDelegation` / `Arrived`，所以计划阶段根本拿不到精确值，而且"真值必定落在区间内" |
| **延后决定** | 对话框第三个出口。`onDefer` 回调 → 用 `request == null` 的 `CaravanArrivalAction_StartDelegation` 出发；`Arrived` 里判定 `request == null` → 先 `EnsureDeposit` 再弹框（此时显示精确规模）。**已经站在目标格上时不显示该按钮**（等于取消，没有意义） |
| **补给耗尽时中止** | 勾选框改成正向语义并直接映射 `abortWhenOutOfFood`（勾选 = 中止，默认勾选）；同步改了中断原因（`车队补给耗尽`）、提醒文案与页签 Keyed 串 |

> 顺带的好处：**「延后决定」+「抵达后才掷存量」天然配套** —— 玩家可以先花路上时间，到了地方看着精确规模再决定派谁、采多少、要不要冒超重的风险。这也让"派车队先去探一下"成为一种真实可用的策略。

#### 修 3（严重）：存档标签撞名导致解析异常

**症状**：`Player.log` 出现
```
Exception parsing node <active><def>RimDelegation_MinePreciousLump</def>…</active> into a System.Boolean:
System.FormatException: String was not recognized as a valid Boolean.
  at Verse.ParseHelper.ParseBool → Verse.ScribeExtractor.ValueFromNode[T]
```
**根因（这次踩得很深，值得记死）**：原版把 `WorldObject` 上**所有 comp 的字段平铺写在同一个 XML 层**里。存档里能看到 `<timeoutEndTick>`（TimeoutComp）、`<refugee>`、`<prisoner>`、`<contents>`（各 comp）以及我的 `<active>` 都是 `<li Class="Site">` 的**直接子节点**。

⇒ **同一个世界对象上，所有 comp 的 Scribe 标签共享一个命名空间**。我用 `active` 当标签，与同层另一个 comp 的 bool 字段撞名 → 对方的 `ParseBool` 找到我的 Delegation 节点 → 抛异常（对方的字段被丢弃，我的节点虽然还在但同层已经乱了）。

**修法**：给本 mod 的所有 comp 级标签加 `ro` 前缀：
```csharp
Scribe_Deep.Look(ref active, "roActive");
Scribe_Deep.Look(ref deposit, "roDeposit");
Scribe_Values.Look(ref timeoutPaused, "roTimeoutPaused", false);
Scribe_Values.Look(ref pausedTimeoutRemaining, "roPausedRemaining", 0);
Scribe_Values.Look(ref pausedAtTick, "roPausedAtTick", 0);
```
（`Delegation` / `DelegationDeposit` 内部的字段是嵌套在自己节点里的，各有独立作用域，不需要前缀。）

> **给以后加字段的规矩**：往 `Site`/`Caravan`/`Settlement` 等原版世界对象挂 comp 时，标签一律带 mod 前缀。

#### 修 4：委派页签全空白

**症状**：页签按钮在、面板也画出来了、标题也显示，但**除标题外一片空白**，而且日志里**没有** `Exception filling tab`（说明 `FillTab` 没抛异常）。

**根因**：`Widgets.BeginScrollView` 转发到 Unity 的 `GUI.BeginScrollView`，它会把内容**裁剪到 `viewRect`**。而 `scrollViewHeight` 只在 `Event.current.type == EventType.Layout` 时更新：
```csharp
if (Event.current.type == EventType.Layout) scrollViewHeight = ls.CurHeight + 8f;
```
在 `ImmediateWindow` 里这个时机不可靠 ⇒ 一直按初始值（100px）裁剪，后面的内容全被切掉。

**修法**：页签**彻底不用滚动视图**，改为固定画布 + 显式 `Rect` + 手写 y 累加；面板高度 `Mathf.Clamp(PaneTopY - 130f, 260f, 620f)` 自适应且不顶出屏幕；参与者列表按剩余空间截断并显示「…还有 N 人未显示」（完整列表在对话框里）。同时加了 `OnOpen()` 的 verbose 日志，便于确认页签生命周期确实在跑。

### S5 —— 已完成（作业期间封锁进图 / 藏匿点清单口径 / 措辞与量纲交回 worker）

> 触发原因：一轮试玩反馈（2 个 bug + 4 项增强）。这一轮**没有新玩法**，全是"框架契约"的补漏 —— 但其中两处是硬 bug。

#### S5-a 硬 bug：作业期间能进图（根因与修法见 §7.3）

#### S5-b 硬 bug：物品藏匿点的清单"读不到"时委派 0/0 空转

**症状**：`委派：远程搜刮` 开起来后显示「本次进度 已搬走 0/0 件（0%）」永不前进；对话框写着「现场没有可搬运的物资（SitePart.things 与 ItemStashContentsComp 都是空的）。」

**根因** `[验证]`：

1. `SitePart.things` 只在**任务生成路径**被填：`QuestGen_Sites.GenerateSite` → `SitePartWorker_ItemStash.Notify_GeneratedByQuestGen`。
2. 其它来源（或任务生成时 `points = 0` 掷出空清单）的点，清单由 `GenStep_ItemStash.ScatterAt` **进图时**才掷，落到 `SymbolResolver_Stockpile.Resolve`：
   ```csharp
   if (rp.stockpileConcreteContents != null) { …把 part.things 摆进地图并 Clear()… return; }
   CalculateFreeCells(rp.rect, 0.45f);                                  // 地图生成完才知道有几格可站立
   num4 = rp.stockpileMarketValue ?? Mathf.Min(cells.Count * 130f, 1800f);
   value = new ThingSetMakerParams { techLevel/makingFaction…, totalMarketValueRange = (num4, num4), countRange = (cells.Count, cells.Count) };
   thingSetMakerDef = rp.thingSetMakerDef ?? ThingSetMakerDefOf.MapGen_DefaultStockpile;
   ```
   ⇒ **地图外读不到**（尤其 `cells.Count`）。
3. 而我们的 `RollDeposit` 读不到容器就把 `totalUnits` 记成 0 ⇒ `totalCells = 0` ⇒ `TargetDepleted` 恒假（它是 `totalCells > 0 && …`）⇒ 委派永不完成、地点永不销毁；同时预览还把"**还没掷**"说成"**没有**"。

**修法（用户决策 = 方案 A）**：抵达时**按原版同一口径**掷一份，并**写回 `SitePart.things`**（`DelegationWorker_TakeItemStash.TryRollMissingContents`）：

| 要素 | 取法 |
|---|---|
| ThingSetMaker | 该 SitePartDef 自己的 `GenStep_ItemStash.thingSetMakerDef`（`GenStepDef.linkWithSite` 是 **SitePartDef 引用**，`SitePartDef.ExtraGenSteps` 就是它的索引）→ Core 为 null ⇒ 回退 `ThingSetMakerDefOf.MapGen_DefaultStockpile` |
| techLevel / makingFaction | 地点所属派系（与 `SymbolResolver_Stockpile` 的 else 分支一致） |
| totalMarketValueRange | `[近似]` 取上限 **1800 银**。原式是 `Min(cells.Count × 130, 1800)`，而 7×7 密室的格数在 25–49 之间、`cells ≥ 14` 就已撞上限 ⇒ 绝大多数情况就是 1800。**这条近似同时在代码注释与玩家可见的预览文案里写明**（本项目准则②） |
| 写回 | `part.things ??= new ThingOwner<Thing>(part, oneStackOnly: false){ dontTickContents = true }` + `TryAddRangeOrTransfer(list, false)` —— 与 `Notify_GeneratedByQuestGen` 逐字同构 |

⇒ 写回之后 `GenStep_ItemStash` 的第一优先级就命中了：**掷出来的是什么，进图也是什么**，不存在"委派一份、进图再掷一份"（这一点比矿点干净 —— 那边是两次独立掷骰，只能靠封禁进图兜住）。

配套：

- `DelegationDeposit.workerRolledContents`（新字段，已 Scribe）：标记"这份清单是委派替原版掷的"，供预览如实标注。
- `WorldObjectComp_Delegations.StartDelegation` 新增闸门：`dep.UnitsRemaining <= 0` ⇒ 拒绝开工并提示。**`totalCells = 0` 的委派是必然空转的死局**，对话框底部的估算只是提示，不能当闸门（营救点已经没人时同样会踩到）。
- 现场清单文案从**两态**改成**三态**：有实物（精确值）/ 还没清点（「现场内容未定」）/ 已清点且为空（「现场没有可搬运的物资」）。

#### S5-c 措辞与量纲：写死的"采/格/挖"全部交回 worker

**症状**：搜刮物资藏匿点时，远行队信息栏写「委派**开采**中。」、页签写「本次进度 … **格**」、结束条件写「**采**空为止」、勾选框写「饿着也继续**挖**」。

**根因**：这些字面量散在 5 个文件里，而且**全是采矿口径**（`Patch_CaravanInspect` + Keyed 串 + `Dialog_ChooseDelegation` + `DelegationRequest.EndConditionLabel` + 宿主 `EndConditionLabelOf`）。

**修法**：`DelegationWorker` 上新增三个维度，UI 一律问 worker：

| 新成员 | 采矿 | 物资点 | 营救 | 用途 |
|---|---|---|---|---|
| `ActivityName` | 开采 | 搜刮 | 营救 | 句子（"委派{0}中"/"恢复{0}"/"饿着也继续{0}"） |
| `OutputUnitName` | 单位 | 件 | 人 | **产出**量纲（配额、累计交付）——与进度单位 `UnitName` 是两件事：采矿进度是"格"、产出是"单位" |
| `UntilDepletedLabel` | 采空为止 | 搬空为止 | 救出为止 | "取尽"那一档的整句（"救空为止"不是中文） |
| `StatusKeySuffix` | Mining | Search | Rescue | Keyed 键后缀（`RimDelegationCaravanDelegating_Search` …），**键不存在自动退回基础键**（`Translator.CanTranslate`），漏配翻译不会显示裸键名 |

- 结束条件文案收成**一份**：`DelegationUIUtility.EndConditionLabel(cond, days, quota, worker)` —— 对话框 / 页签 / 检视 / RadiusUI 皮肤四处共用，不再各写一遍（原来四处各写一份，改一处漏三处）。
- `EstimateUnavailableReason(...)`（新虚方法）：默认那句「无法估算（模式或人员缺失）」在物资点上是**误导**（人跟模式都在，缺的是清单）。
- Keyed 串 `RimDelegationTabRunProgress` / `RimDelegationTabDeposit` / `RimDelegationFoodPressOn` 增加量纲占位符。英文串刻意**不**插入代码给的中文词（只有键值本身是语言相关的），所以英文侧省略该占位符。

#### S5-d Radius UI 皮肤：崩溃修复 + 姿态轴补齐

- **崩溃**：`Player.log` 里 `DelegationDialogSkin.cs:226` 还在调**旧的 3 参** `EstimateUnitsPerDayFor`，而 S4 已把它改成 4 参（加 `Site`）⇒ `MissingMethodException` → 皮肤连炸 3 次后整场停用、退回原版对话框。这就是"物品藏匿点没有 Radius UI"的真正原因。修法：补 4 参 + 措辞/量纲全部改用 worker 供给。
- **姿态轴（补上本来就欠的账）**：皮肤是用 Harmony 前缀**整个掐掉**原版绘制的，所以原版新加的 UI 在皮肤下不可见。S4 新增的「作战姿态」（强攻/潜入）此前没有桥过去 —— 皮肤一旦能跑起来，囚犯营救就**选不了潜入**（而它的默认姿态恰恰是潜入）。本轮补齐：`DialogBridge` 桥 `approach` 字段 + `OpenApproachMenu()`（含兜底实现）、`Confirm()` 组装 `DelegationRequest` 时带上 `approach`、皮肤里加「姿态：X（掷暴露/战斗结算）」 rail entry 与底部「姿态成算」一行。

#### S5 验收步骤

1. 开着委派选中目标地点右键 → 原版菜单项应显示为**不可点**（`MessageEnterCooldownBlocksEntering`：还需若干小时）。中止委派且**一点没动过** → 右键应恢复可进入。
2. 委派采过之后收工/中止 → 地点仍保留时，右键仍应被封禁（`blockMapEntryAfterWorked`，采矿 9999 天）。
3. 找一个"读不到清单"的物资藏匿点（非任务生成，或任务生成时清单为空）：
   - 计划阶段对话框：「现场**内容未定**…」+ 底部「无法估算：这份清单要等生成地图时才掷定」，**不再**说"没有可搬运的物资"；
   - 抵达后：清单已按原版口径掷定并写回，对话框显示精确件数/kg/市价；
   - 随后真进图（先把委派中止）→ 地图里的物资应与委派看到的**是同一份**（`Player.log` 有 `物资点现场清单读不到 ⇒ 委派按原版口径掷定并写回：…` 的 verbose 行）。
4. 搜刮委派进行中：远行队信息栏应写「委派**搜刮**中。」、每人「XX **搜刮**中。」；页签进度单位是**件**；结束条件是「**搬空**为止」「**搬满** N 件」；勾选框是「饿着也继续**搜刮**」。
5. 开 Radius UI：对话框皮肤应正常渲染（不再有 `委派对话框 绘制异常`），且有姿态轴的两条委派能切换强攻/潜入，底部出现「姿态成算」。

### S1 验收步骤


1. Mod 列表启用 **RimDelegation 边缘委派**（会自动带上 Harmony 依赖）
2. `Player.log` 应出现 `[RimDelegation] 已加载 | Harmony … | DelegationDef 1 个 | …`
3. 造一个矿点：研究「远距离矿物扫描仪」→ 造扫描仪 → 选矿种 → 等它扫出来
4. **逻辑 2（预先委派）**：选中一支车队 → 右键矿点 → 「委派：开采 XX」→ 选人/选模式 → 确认 → 车队走过去 → 抵达应收到「委派开始：开采」
5. **逻辑 1（就地委派）**：车队停在矿点格上 → 选中车队 → gizmo 栏出现「委派：开采 XX」→ 点击立刻开工
6. 选中矿点看 inspect：模式、参与人数、进度、作业/休息小时数、预计剩余天数
7. 开发者模式选中矿点 → 4 个 DEV gizmo。先点「打印超时计时器」，再点「发信号暂停超时计时」，再点「打印」→ 应看到 `part.State` 从 `Enabled` 变 `Disabled`
8. **要验证的核心问题（DK-1）**：暂停后隔几天再看该地点 —— 若矿点活过原本的 30 天，则"零 Harmony 暂停计时"成立，S3 不必引 Harmony

### S2 验收步骤（可延后）

1. 按上面第 4/5 步开出一个委派
2. 地点 inspect 面板应**每游戏内 1 小时**跳一次「已交付 N × 钢铁」
3. 选中车队 → 物品页应真的出现钢铁堆，且**负重上升**
4. 半途点「中止委派」→ 中断信里应带「已交付 …」，且库存里保留这部分（= 带已采部分撤回）
5. 让它自然采完 → 完成信与 inspect 的交付总量应 ≈ 矿点总价值 **3500–5000 银**（矿点总价值不变式）
6. 把负重塞爆 → 应出现一次超重提醒，且 inspect 常驻「车队已超重，无法移动」

### S3 验收步骤（防双吃 + 计时）

1. 开一个委派 → 选中矿点 → DEV「打印超时计时器」→ 记下 `State=Enabled` 与剩余天数
2. 再点一次「打印」（委派开始时应已自动暂停）→ 应看到 `State=Disabled`、`comp.timeoutPaused=True`
3. 点几次「推进 1 天」后再打印 → 矿点**仍然存在**；`WorldObjectTimeoutTicksLeft` 会显示 `-1`（部件被禁用，这是原版的读取逻辑）
4. 点「恢复超时计时」→ `State` 回到 `Enabled`，`TicksLeft` ≈ 暂停时记录的剩余量（不该凭空多出或少掉）
5. 走「采空」路径 → 收工信应写「目标已采空」，**地点从世界地图上消失**，相关任务随之结束（不会再收到「XX 已被人捷足先登」）
6. 走「中断」路径（把车队开走）→ 地点**保留**，且计时已恢复
7. 选「按天数 1 天」+ 推进若干天 → 收工信应写「按计划干满 1 天」，且**地点保留**（因为没采空）

### S4 验收步骤（模式 / 心情 / 异常）

1. 对话框应能循环切换 3 个模式，且模式标签尾部显示该模式的心情代价（只白天「心情 -3/天」、全天轮班「心情 -6/天」）
2. 结束条件三个单选 + 步进可用；点 `+` / `-` 会自动把结束条件切到对应项
3. 选「按产出」配额 → inspect 出现「产出配额进度：N / M」，达到后自动收工（收工信写「已达到产出配额 N 单位」）
4. 选「按天数」→ 天数一到自动收工
5. 不勾「补给耗尽时中止」→ 车队断粮应只提醒一次然后继续挖；保持勾选 → 补给耗尽立刻中断并带回已采部分
6. 用 DEV「Down random pawn」打倒一个参与者 → 产能下降但**委派不中断**；治好后应自动复工（S2 之前是永久除名）
7. 心情：每次「推进 1 天」应给车队里的人挂一次「野外扎营干活」；全天轮班额外挂「连轴转」；收工挂「满载而归」；中断挂「白跑一趟」（在人物需求页可见）
8. 遇袭：让车队被事件拉进地图 → 委派应中断，原因写明"被事件拉进了地图"

---

## 16. UI 控件清单（实装即此，文案为程序字面量）

### 16.1 车队 gizmo（逻辑 1）

```
[矿物 uiIcon]  委派：开采 <地点名>
```
- 提示：`派车队在这个矿点上扎营开采，不需要进入地图。`
- 只在该车队**停住**且地点无委派时出现（`MovingNow` 不显示，避免路过闪烁）
- 已有委派 → 换成 `中止委派`；已采空 → 不显示；人数不够/被占用 → 灰掉并在标签后带原因

### 16.2 右键地点浮空菜单（逻辑 2）

`委派：开采 <地点名>` 与原版的 `进入地点` / `进攻 XX` 并列。点它先弹 16.3 的对话框，确认后才出发。

行进中车队信息栏：`Label = 前往委派开采`，`ReportString = 正在前往 <地点名> 执行委派开采`。

### 16.3 委派对话框（640 × 780，模态）

标题行（右侧是**地点本体**的信息卡按钮）/ 顶部块 / 结束条件 / 风险姿态 / 工具条 / 滚动列表 / 底部按钮：

```
委派：开采 <地点名>                                              (i)←地点信息卡
<def.description>
钢铁 · 预计 46–66 格（抵达后才能确定实际规模）· 每格基础 40 单位
矿点价值区间 3500-5000 银（均值 4250）
[ 委派模式：只白天 · 6:00 - 22:00 · 速率 ×1 · 心情 -3/天   （点击切换） ]
人数下限 1，上限 12，共 5 人可选
结束条件：[ 采空为止 ▼ ]      一直采到采空
[✓] 补给耗尽时中止（取消勾选 = 饿着也继续挖）
[ 排序：技能（高→低） ] [ 全选 ] [ 全不选 ]   已勾选 3/5
┌ 滚动列表，每行 44px：───────────────────────────────────────────┐
│ ☑ ┌──────┐ 名字   采矿 8 · 速度 ×1 · 1.32 格/作业小时  ■ ■  (i)  │
│   │ 36×36│                                      ↑ ↑           │
│   │ 头像 │                                    心情 受伤        │
│   └──────┘                                                    │
└────────────────────────────────────────────────────────────────┘
已选 N 人 · 结束条件：采空为止 · 每天心情 -3
预计 1.0–1.4 天采完（约 47 格/天）
车队负重：现在 320 / 525 kg
采完后预计 1,632–2,640 / 525 kg（只算产物） ← 会超重
                        [ 确认委派 ]  [ 延后决定 ]  [ 取消 ]
```

**抵达之后**（走「延后决定」或设置里开启了抵达确认）再弹同一个框时，规模从区间变成精确值、且不再提供「延后决定」：

```
钢铁 · 剩余 34/52 格（已确定）· 每格基础 40 单位
此事件点已被委派 2 次，累计采出 18 格、累计交付 736 单位
…
预计 0.7–0.7 天采完（约 47 格/天）
                        [ 确认委派 ]                 [ 取消 ]
```

（窗口 `InitialSize` 640 × 780；`Window.Margin = 18` ⇒ `DoWindowContents` 收到的 `inRect` 是 **604 × 744**
且 GUI 组被裁剪到该范围。标题行 28px、顶部块由 `Listing_Standard.CurHeight` 决定（`TopHeight = 214`
只约束 `Begin` 的矩形）、结束条件 30px、补给勾选 30px、工具条 28px、滚动列表自适应、
底部 `BottomHeight = 180px`。）

**五个增强点**：

| 项 | 行为 |
|---|---|
| **负重 As-Is / To-Be** | 两行：`车队负重：现在 MassUsage / MassCapacity kg`、`采完后预计 现在+范围 / 容量 kg（只算产物）`。**只算产物**（不算途中吃掉的补给）。超容量时该行变红并追加 `← 会超重`。每单位重量取 `ThingDef.BaseMass`（Steel = 0.5） |
| **结束条件下拉框** | 从三个单选改成下拉：`[ 采空为止 ▼ ]` + `FloatMenu` 弹层（当前项带 `✓`）。选中「按天数」「按产出」时右侧才出现 `[-] N [+]` 步进 |
| **抵达前只给区间** | 计划阶段显示 `预计 46–66 格（抵达后才能确定实际规模）`，ETA 也变成区间 `1.0–1.4 天`；抵达后（存量已掷定）才显示 `剩余 34/52 格（已确定）`。范围由 `GenStepDef.PreciousLump.totalValueRange` 的 min/max 两头各套一次 `TotalCellsFor` 得到，**保证真值一定落在区间内** |
| **延后决定** | 三个出口：`确认委派`（带选择出发，抵达即开工）/ `延后决定`（**不带选择**先让车队过去，抵达后再弹这个框，此时显示精确规模）/ `取消`。已站在目标格上时不显示「延后决定」（等于取消，没有意义） |
| **补给耗尽时中止** | 勾选框措辞与语义都改成正向：勾上 = `abortWhenOutOfFood = true`（默认），取消 = 饿着也继续挖。同步改了中断原因（`车队补给耗尽`）、提醒文案与页签 Keyed 串 |

人员行的交互：

| 操作 | 行为 |
|---|---|
| 点小方框 | 勾选 / 取消 |
| 点头像或姓名那一片 | 同样切换勾选（label 热区） |
| 悬停某行 | 整行高亮 |
| 悬停头像 | 姓名 tooltip |
| 悬停**心情角标** | `心情 62%（心情愉快）` |
| 悬停**受伤角标** | `受伤 3 处 · 疼痛 24%`（倒地时追加「已倒地」，且颜色更深） |
| 点头像右侧的 `(i)` | 打开原版 Pawn 信息卡（外观 / 需求 / 健康 / 技能 / 社交全套） |
| 点标题右侧的 `(i)` | 打开**地点本体**的信息卡（`Widgets.InfoCardButton(x, y, WorldObject)`） |
| 点「排序：…」 | 循环 技能（高→低）/ 名字 / 心情（差→好）；**只对候选列表排序，不改车队本身** |
| 点「全选」「全不选」 | 批量勾选；右侧实时显示 `已勾选 N/M` |

**心情 / 受伤角标**（`DelegationUIUtility`，对话框与页签共用）：

| 项 | 实现 |
|---|---|
| 位置 | 贴在信息卡按钮左边，固定两格（16×16，间距 3px）：左心情、右受伤 |
| 心情颜色 | 由 `pawn.needs.mood.CurLevelPercentage` 分级：≥70% 绿 / ≥45% 黄 / ≥25% 橙 / 其余红 |
| 心情 tooltip | `心情 {pct}（{Need_Mood.MoodString}）` |
| 受伤角标 | 有 `Hediff_Injury` 或 `hediffSet.PainTotal > 0` 或已倒地才画；tooltip `受伤 n 处 · 疼痛 x%` |
| 为什么不用图标 | 原版的"心情脸"在 `ColonistBarColonistDrawer.MoodAtlas`（私有图集，索引逻辑在内部），直接引用等于抄私有实现。改用 `Widgets.DrawBoxSolidWithOutline(rect, color, Color.black, 1)` + tooltip：零贴图路径猜测、不会因版本换图而崩 |

实现要点 `[验证]`：

| 需求 | 用的 API |
|---|---|
| 外貌头像 | `GUI.DrawTexture(rect, PortraitsCache.Get(pawn, new Vector2(w, h), Rot4.South))` —— 与原版 `TransferableUIUtility.DrawOverseerIcon` 完全同款写法；`PortraitsCache.Get` 返回 `RenderTexture`，带缓存（按 size+params 分桶），滚动不卡 |
| 信息卡按钮 | `Widgets.InfoCardButton(float x, float y, Thing)`（返回 bool）+ `Widgets.InfoCardButtonSize` 取尺寸 |
| 勾选框 | `Widgets.Checkbox(float x, float y, ref bool checkOn, float size, bool disabled, bool paintable, Texture2D texChecked, Texture2D texUnchecked)` |
| 单选 | `Widgets.RadioButtonLabeled(Rect, string, bool chosen, bool disabled)`（返回"被点了"） |
| 整行热区 | `Widgets.ButtonInvisible(Rect, bool)`；**点击区要避开信息按钮**，否则 IMGUI 里先画的控件会先吃掉事件 |
| 悬停高亮 | `Widgets.DrawHighlight(Rect)` + `Mouse.IsOver(Rect)` |
| tooltip | `TooltipHandler.TipRegion(Rect, TipSignal)`（string 隐式转 TipSignal） |
| 混排布局 | `Listing_Standard.CurHeight` 读顶部块实际占用高度，再用显式 Rect 接下面的单选/步进行 |

> 已实装：心情/受伤小角标、按技能排序、全选/全不选、地点本体的信息卡按钮（见上表）。

### 16.4 地点 inspect 面板（选中矿点）

```
委派：开采（只白天 · 6:00 - 22:00 · 速率 ×1）
参与者 3 人 · 结束条件：采空为止 · 补给耗尽时中止
事件点存量：全点剩余 34/52 格 · 本次可采 34 格 · 已被委派 2 次
每天心情：-3（已挂 2 天）
钢铁：17.4/52 格（33.5%）· 约 696 单位
已交付 680 × 钢铁（约 1292 银）
待交付：16（每小时结算一次）
产出配额进度：696 / 800              ← 仅"按产出"时
当前：作业中              ← 或 "休息中（不在工时段）"
预计剩余：0.98 天
原版失效倒计时：已暂停（暂停时剩余 29.8 天；采空后地点直接销毁，其他情况收工即恢复）
累计作业：5.2 小时 / 休息 1.1 小时（1 小时 = 2500 ticks）
[注意：车队已超重，无法移动]        ← 仅超重时
```

### 16.5 开发者 gizmo（选中矿点，`DebugSettings.ShowDevGizmos`）

`DEV: 打印超时计时器` / `DEV: 发信号暂停超时计时` / `DEV: 恢复超时计时` / `DEV: 委派推进 1 天` / `DEV: 立即完成委派`

（打印会把 `State` / `delayTicks` / `TicksLeft` / 暂停记录一起写进 Player.log，并注明"TicksLeft 在 Disabled 时恒为 0 是原版实现"。）

### 16.6 三封信

| 类型 | 标题 | 正文模板 |
|---|---|---|
| Neutral | 委派开始：开采 | `{0} 的委派开采已开始。\n\n模式：{1}\n人员：{2} 人` |
| Positive | 委派收工：开采 | `{0} 的委派开采已收工：{1}\n\n{2}\n\n{3}`（{1}=收工原因，{2}=进度，{3}=交付汇总） |
| Negative | 委派中断 | `{0} 的委派开采已中断：{1}\n\n{2}`（{1}=原因，{2}=交付汇总） |

### 16.7 Mod 设置面板

`详细日志` / `抵达后仍需确认` 两个勾选框 + 两行说明。



### 16.8 远行队 inspect 页签「委派」

选中一支**正在委派**的远行队 → 世界地图 inspect 面板底部出现一个独立页签「委派」
（620 × `clamp(PaneTopY − 130, 260, 620)`，**没有滚动视图**）：

> 布局是 **S8 重排**（§19.28，用户给的草图）后的样子：状态词叠在模式行右端、ETA 并进总进度行、
> 现场物资可折叠、参与者用 30px 头像的紧凑行并且表头有「查看全部 N 人」。
> 下图按"搜刮物品藏匿点 · 加班工作中"举例子。

```
委派：搜刮 @ 物品藏匿点                                   (i)←地点信息卡
[模式：加班工作 · 06:00 - 22:00 · 速率 ×1.1 · 每天心情 -4　（点击切换模式）]      作业中
结束条件：搬空为止 · 补给耗尽时中止
总进度 [████████░░░░░░░░░░] 21% · 8.4/40 件 · 剩余 1.24 天   ←悬停=进度明细(worker 进度/事件点存量/交付汇总/待交付)
累计作业 9.4 小时 / 休息 1.1 小时（1 小时 = 2500 ticks）
产出配额进度：696 / 800                                    ←仅"按产出"模式
注意：车队已超重，无法移动                                 ←仅超重时
现场物资（3 类）· 剩余 40/40 件                                        ▾←点击折叠
[图标] 钢铁   ×12 件 · 60 kg · 市价约 24 银
[图标] 零部件 ×6 件 · 6 kg · 市价约 130 银
[图标] 医药   ×4 件 · 2 kg · 市价约 80 银
参与者（3 人）                    [ 排序：技能（高→低） ]  [ 查看全部 3 人 ]
[头像30] 小明   搜刮 8 · 速度 ×1 · 12.4 kg/作业小时        ■ ■      (i)
[头像30] 阿花   搜刮 14 · 速度 ×1.72 · 21.2 kg/作业小时    ■ ■      (i)
[头像30] 老王   搜刮 2 · 速度 ×0.28 · 3.1 kg/作业小时      ■ ■      (i)
──────────────────────────────────────────────
流程：
　侦察完成（1h）
　已抵达目标地点（2h）
　正在破门…（0.4h/1h）
　撤离（0h/2h）
紧急加班中：剩余额度 2.6 小时 —— 无视工时窗口，且队员不会补休息（工伤风险上升）
疲劳 · 全队平均休息 62% → 工伤倍率 ×1.4
伙食（库存最好）· 包装生存食品 · 原版 +2 · 委派 +4 · 全部补给约可维持 1.2 天
[ 暂停委派 ] [ 紧急加班 +4h ] [ 结束加班 ] [ 中止委派 ]
```

放不下时：参与者列表**先截断**并写一行「…还有 N 人未显示」（点「查看全部 N 人」开 FloatMenu 看全名单）；
若连一行都放不下、而折叠现场物资能放下一行 ⇒ **本帧自动折叠现场物资**（并在标题后注明"空间不足，已折叠"）。


实现要点 `[验证]`：

| 需求 | 用的 API |
|---|---|
| 注册页签 | XML patch 往 `Defs/WorldObjectDef[defName="Caravan"]/inspectorTabs` 追加 `<li>RimDelegation.WITab_Caravan_Delegation</li>` |
| 页签基类 | `RimWorld.Planet.WITab : Verse.InspectTabBase`；WITab 已提供 `StillValid` / `PaneTopY` / `CloseTab` / `SelCaravan`，子类只需 `labelKey` + `IsVisible` + `UpdateSize` + `FillTab` |
| **只在有委派时出现** | `public override bool IsVisible => DelegationRegistry.For(SelCaravan) != null;` —— 原版 `InspectPaneUtility.DoTabs` 里的 `if (tab.IsVisible)` **同时管住页签按钮与面板绘制**，所以其它远行队完全看不到这个页签 |
| 页签标题 | `labelKey = "RimDelegationTabDelegation"`，配合 `Languages/{ChineseSimplified,English}/Keyed/RimDelegation.xml`（缺失键会刷"Translation not found"日志） |
| 进度条 | `Widgets.FillableBar(Rect, float)`；S8 起用 `总进度 [条] 21% · 8.4/40 件 · 剩余 1.24 天` 一行（尾注宽度由 `Text.CalcSize` 反推条宽，量纲换了也不串位） |
| **底部预留高度** | S8 起是**纯函数** `BottomReserve(width, flowLines, overtimeLine, fatigueLine, mealLine, metaCount)`，与绘制共用同一批入参。旧实现是常量 `y + RowHeight > size.y - 250` —— 与真实内容脱钩，最重状态下参与者一行都画不出来 |
| 折叠现场物资 | 原版**没有** `Widgets.Foldout`；用 `TexUI.ArrowTexRight` + `Widgets.DrawTextureRotated(rect, tex, 90f)` 自绘三角，整行 `Widgets.ButtonInvisible` 收点击；状态存 `RimDelegationSettings.stashItemsExpanded`（原版与皮肤共用） |
| 行内自绘 | `Listing_Standard.GetRect(height)` 先占位，再往这个矩形里画头像/文字/信息卡按钮 |
| 滚动高度 | `if (Event.current.type == EventType.Layout) scrollViewHeight = ls.CurHeight + 8f;`（原版同类页签的标准写法，避免"本帧测高、下帧用"）——**本页签不用**，见下一条 |
| 查询"这队有没有委派" | `DelegationRegistry.For(Caravan)`（每 tick 重建的反向索引，与休息补丁共用一次扫描） |
| 心情/受伤角标 | 与对话框共用 `DelegationUIUtility.DrawPawnLine` / `DrawMoodBadge` / `DrawInjuryBadge` |
| 参与者排序 | 同 `DelegationUIUtility.SortPawns`，排的是**显示用副本**，不动 `Delegation.participants` |
| 标题旁地点信息卡 | `Widgets.InfoCardButton(x, y, WorldObject)` |
| **不要用 `Widgets.BeginScrollView`** | 它转发到 Unity 的 `GUI.BeginScrollView`，会把内容**裁剪到 `viewRect`**；而滚动高度只能靠 `Event.current.type == EventType.Layout` 更新，在 `ImmediateWindow` 里那个时机不可靠 ⇒ 页面永远按初始高度裁剪（实测导致整页只剩标题）。改用固定画布 + 显式 `Rect` + 手写 y 累加，并把高度 `Mathf.Clamp(PaneTopY - 130, 260, 620)` 适配屏幕 |

> 顺带把"头像 + 文字 + 信息卡按钮"抽成 `DelegationUIUtility.DrawPawnLine(...)`，对话框与页签共用，避免两处长相漂移。

### 16.9 远行队信息栏（委派期间改写）

选中正在委派的远行队时，原版信息栏里的「等待中。」会被换成「委派开采中。」，并且**商队级的「休息中。（使用N个睡袋）」被换成每个人的工作状态**：

```
Chisa的远行队
2 殖民者
委派开采中。
Chisa 开采中。
Denia 休息中。（使用0个睡袋）
```

实现：Harmony **Postfix** 挂在 `RimWorld.Planet.Caravan.GetInspectString`（第二个补丁，仍只做这一件事）：

| 原版片段（已反编译） | 处理 |
|---|---|
| `pather.Moving ? ArrivalAction.ReportString : (visiting ? CaravanVisiting : "CaravanWaiting")` | 命中 `"CaravanWaiting".Translate()` 时整行替换成 `RimDelegationCaravanDelegating`，并在其后插入每个参与者一行 |
| `if (!pather.MovingNow) { AppendLine(); Append(AppendUsingBedsLabel("CaravanResting", beds.GetUsedBedCount())) }` | 丢掉这一行（由逐人行取代），逐人用 `caravan.beds.GetBedUsedBy(p) != null` 判断是否在睡袋里，产出 `{0} 休息中。（使用{1}个睡袋）` |
| 其余所有行（旅行中 / 正在前往 / 访问中 / 超重 / 补给耗尽 / 全员倒地…） | **原样保留** |

判定"开采中"用的是：`!p.Downed && !p.Dead && mode.IsWorkingNow(tile, TicksAbs)` —— 和产能结算用的是同一个工时判定，所以显示与实际一致（夜里不会说"开采中"却不出货）。

新增 Keyed：`RimDelegationCaravanDelegating` / `RimDelegationPawnWorking` / `RimDelegationPawnResting`。

---

## 17. 威胁：「这片区域存在敌人活动的迹象。」

### 17.1 这句话是什么 `[验证]`

「这片区域存在敌人活动的迹象。」= `SitePartDef[PreciousLump].mainPartAllThreatsLabel`（`Data/Core/Languages/ChineseSimplified/DefInjected/SitePartDef/PreciousLump.xml`）。

站点检视栏的实现 `RimWorld.Planet.Site.GetInspectString`（`06015AEB:M`）：

```csharp
stringBuilder.Append(base.GetInspectString());
for (int i = 0; i < parts.Count; i++) {
    if (parts[i].hidden || !parts[i].def.displayOnInspectPane) continue;
    if (MainSitePart == parts[i] && !parts[i].def.mainPartAllThreatsLabel.NullOrEmpty() && ActualThreatPoints > 0f) {
        stringBuilder.Length = 0;                         // ← 整段清空
        stringBuilder.Append(parts[i].def.mainPartAllThreatsLabel.CapitalizeFirst());
        break;
    }
    ... // 其它 part 走 Worker.GetPostProcessedThreatLabel
}
```

`Site.ActualThreatPoints` = `Σ parts[i].parms.threatPoints`（`1700360E:P`）。

### 17.2 威胁从哪来 `[验证]`

| 环节 | 事实 | 证据 |
|---|---|---|
| 谁生成矿点 | `QuestScriptDef LongRangeMineralScannerLump`（远距离矿物扫描仪） | `rimsearcher get LongRangeMineralScannerLump --type QuestScriptDef` |
| 站点部件 | `sitePartsTags = [PreciousLump]`（必出）+ `[MineralScannerPreciousLumpThreat, chance=$siteThreatChance]` | 同上 |
| 概率 | `siteThreatChance = 0.5`（`QuestNode_ViolentQuestsAllowed` 为真时），否则 `0` | 同上 |
| 掷骰 | `Rand.Chance(item.chance)` 决定该 tag 是否进入候选 | `QuestNode_GetSitePartDefsByTagsAndFaction.TrySetVars` `06014893:M` |
| 威胁池（7 个） | `AmbushEdge` `AmbushHidden` `Manhunters` `Turrets` `SleepingMechanoids` `Outpost` `MechCluster`(Royalty) | `rimsearcher find "tags[0]" MineralScannerPreciousLumpThreat --type SitePartDef`（`Outpost` 在 `tags[1]`） |
| 威胁点数 | `GenerateDefaultParams`：`threatPoints = def.wantsThreatPoints ? myThreatPoints : 0f`；7 个威胁全是 `wantsThreatPoints: true`；`Ambush*` 再 ×0.8 | `06009A86:M` / `02003E12:T` |
| 提前告知 | 任务信里 `lumpThreatDescription` = 「扫描仪检测到矿区附近的活动，它可能是敌对的。」 | `SitePartWorker_PreciousLump.Notify_GeneratedByQuestGen` `02003E1D:T` + Keyed `PreciousLumpHostileThreat` |
| 进图即视为进攻 | `PreciousLump` 与 7 个威胁全部 `considerEnteringAsAttack: true` | `rimsearcher get <def> --type SitePartDef`；消费点 `CaravanArrivalAction_VisitSite.DoEnter` |
| 进图后的撤离倒计时 | 威胁 part `forceExitAndRemoveMapCountdownDurationDays = 4`；`PreciousLump` = 11 | 同上 |

⇒ **50% 的扫描矿点是"有敌人活动的迹象"**。

### 17.3 威胁什么时候真正出现 `[验证]`（这条决定整个设计）

| 事实 | 证据 |
|---|---|
| 威胁内容在地图生成时才落地：`SitePartWorker.PostMapGenerate(Map)` 是空虚方法，各威胁 worker 覆写它 | `06009A82:M` + 各 worker |
| 伏击更进一步，走 `extraGenSteps`（`GenStep_Ambush_Edge` / `GenStep_Ambush_Hidden`：在地图边缘/某矩形放触发器，踩到才刷人） | `GenStep_Ambush_*` |
| **没有地图 ⇒ 没有任何威胁。** 车队停在矿点格上、不进图，站点威胁永远不会碰它 | 由上两条推出 |

这就是当前的洞：委派 =「到点即开工，不进图」⇒ **100% 无视守卫**，机械集群、哨所、休眠机械族一样白拿。而 vanilla 已经用任务信 + 站点检视两次明示了风险，等于这条信息被完全浪费。

### 17.4 已实装的一个连带缺陷 `[验证]`

`WorldObject.GetInspectString()` 里**已经追加了所有 comp 的 `CompInspectStringExtra()`**（`06015C91:M`），而 `Site.GetInspectString()` 开头正是 `Append(base.GetInspectString())` 再 `Length = 0` ⇒

> **在有威胁的矿点上，站点检视栏里我们的委派状态行会被整段抹掉，只剩那句"迹象"。**

⇒ 委派状态必须有一个不经过 `GetInspectString` 的出口：**给 Site 加 `inspectorTabs`**（`WorldObject.GetInspectTabs() => def.inspectorTabsResolved`，`06015CA7:M`；`WITab.SelObject` 可用）。与车队页签同一套做法。

### 17.5 设计原则

1. 不删除 vanilla 威胁，也不替玩家打赢：**不做"战力 vs 威胁点数"的抽象自动结算**。RimWorld 的战斗是地图内的战术行为（掩体、炮塔、地形），抽象化必然失真，且与 mod 既有的"真实"基调（真实行军、真实营养、真实负重）冲突。
2. 判定只用玩家已经能看到的信息（站点检视 + 任务信都在明说），不做隐藏信息透视。
3. 数据驱动：威胁分类 → 处理策略的映射放进 Def，后续 `ItemStash` / 遇险信标一类事件可复用。
4. 默认安全：宁可多要求一次"进图清剿"，也不让"迹象"变成废话。

### 17.6 方案 A（默认）：清剿闸门

- 闸门条件：`site.ActualThreatPoints > 0f && !comp.roThreatCleared`。
- 表现（两条入口共用同一判定）：
  - 右键浮空菜单「开始开采：XX」→ `FloatMenuAcceptanceReport.WithFailReason("RimDelegationNeedsClearance")` ⇒ **灰显带理由**，看得到、点不了。
  - 紧随其后补一条 **「前往清剿（进入地图）」**，直接复用 vanilla `CaravanArrivalAction_VisitSite`（`02003D56:T`）——原样保留它的全部副作用：`GetOrGenerateMap`、进图信、`SettlementUtility.AffectRelationsOnAttacked`（因为 `considerEnteringAsAttack = true`）。
- 清剿判定（关键）：
  - 钩子：`Game.DeinitAndRemoveMap` 在 `MapDeiniter.Deinit` **之前**调用 `map.Parent.Notify_MyMapAboutToBeRemoved()`（`060014C9:M`）⇒ 那一刻地图和 pawn 都还在，可可靠判定。
  - 判定：`!map.mapPawns.AllPawnsSpawned.Any(p => !p.Dead && p.HostileTo(Faction.OfPlayer))` ⇒ `roThreatCleared = true`。
  - **注意**：杀光敌人**不会**清掉威胁 part。`parms.threatPoints` 是生成期数据，没有运行时清除逻辑（`ActualThreatPoints` 永远读到原值）⇒ 必须自己记 flag。
  - 该钩子拿不到 comp 上（`Site.Notify_MyMapAboutToBeRemoved` 只转发给 `SitePartWorker`，`WorldObjectComp` 没有对应虚方法）⇒ 需要**第 3 个 Harmony 补丁**（postfix on `Site.Notify_MyMapAboutToBeRemoved`）。退路是在 `CompTickInterval` 里跟踪 `site.HasMap` 边沿 + 缓存"最后已知敌人数"，不推荐（边界不可靠，且世界对象在玩家位于地图内时是否照常 tick 未验证）。
- 清剿之后：vanilla 检视栏仍然只有那句"迹象"（见 §17.4），所以**只能靠我们的页签**告诉玩家「威胁：已清剿，可委派」。

### 17.7 方案 B（可选开关，默认关）：风险开采

允许在未清剿的矿点上委派，用真实代价换"不回图"：

| 代价 | 实现 |
|---|---|
| 产能折损 | `DelegationDef.threatWorkFactor`（建议 0.65）乘进 `EstimatedUnitsPerDay` 与结算；UI 明示「警戒作业 −35%」 |
| 真实战斗 | 抵达时 `CaravanIncidentUtility.SetupCaravanAttackMap(caravan, enemies, sendLetterIfRelatedPawns: false)` + 按 `site.ActualThreatPoints` 用 `PawnGroupMaker` 生成敌人 ⇒ 打一场 **vanilla 战斗**，胜负由游戏本身决定，不掷骰子 |
| 战后 | 赢 → 回世界地图仍在矿点格，`roThreatCleared = true`，自动继续委派；输/撤 → 委派中止，已采部分随人带走 |

`[建议]` 需实测：`SetupCaravanAttackMap` 在"车队已停在站点格子上"时生成的是遭遇战地图还是站点地图（`Site.HasMap` 会不会被绕开）。

### 17.8 方案 C（不推荐，仅记录）：只加提示

对话框加一行红字 + 复选框。改动最小，但漏洞仍在（白拿 50% 的守卫矿点），且让 vanilla 专门为这件事写的威胁池彻底失效。

### 17.9 UI 变更点

| 位置 | 变更 |
|---|---|
| **新增 `WITab_Site_Delegation`** | XML patch 往 `WorldObjectDef[defName="Site"]/inspectorTabs` 追加；`IsVisible => SelObject is Site s && (DelegationRegistry.For(s) != null \|\| s.ActualThreatPoints > 0f)`；内容 = 委派状态 + 威胁行 |
| 威胁行的粒度 | **只显示 vanilla 已公开的信息**：有/无威胁、是否已清剿、强度分级（`ActualThreatPoints` / 本队战力估计 → 低/中/高）。**不要列具体 part defName**：`AmbushEdge`/`AmbushHidden` 是 `defaultHidden = true`，列出来等于剧透 |
| `Dialog_ChooseDelegation` | 闸门关闭时整块替换为「⚠ 这片区域存在敌人活动的迹象。」+ 原因 + 「前往清剿」按钮（已在格子上才可点）；方案 B 打开时换成「警戒作业 −35% 产能」+ 复选框 |
| 车队页签 | 加一行威胁状态（同粒度） |

### 17.10 数据结构

- `DelegationDef` 增：`requiresClearedSite`（bool，默认 true）、`threatWorkFactor`（float，默认 0.65）、`threatPolicy`（`RequireClear` / `Hazardous` / `Ignore`）。
  `[建议]` 更好的做法是单独一个 `DelegationThreatPolicyDef`，便于按"威胁 defName → 策略"分别配置（例如 `Manhunters` 允许风险开采、`MechCluster` 强制清剿）。
- `WorldObjectComp_Delegations` 增：`roThreatCleared`（bool）、`roThreatClearedTick`（int）。
  **遵守既有规则：comp 级 Scribe 标签一律 `ro` 前缀**（同一 WorldObject 上所有 comp 共享一个扁平 XML 作用域，见 §15 修复记录）。
- 新 Keyed：`RimDelegationNeedsClearance` / `RimDelegationThreatSigns` / `RimDelegationThreatCleared` / `RimDelegationGoClear` / `RimDelegationHazardousWork` / `RimDelegationThreatTier_Low|Mid|High`。
- 新 XML patch：`Patches/RimDelegation_SiteTabs.xml`。

### 17.11 边界与未决

| 项 | 说明 |
|---|---|
| 30 天计时器 `[建议]` | `QuestNode_WorldObjectTimeout.inSignalDisable = site.MapGenerated`：玩家一进图，vanilla 自己就停了计时。我们现有的"派发时暂停"要和它对位，避免重复计数 / 出图后不恢复，需实测 |
| 4 天撤离倒计时 `[建议]` | 威胁 part `forceExitAndRemoveMapCountdownDurationDays = 4`（`PreciousLump` 是 11）。清剿进图后只有 4 天，UI 应提示 |
| 派系好感 `[建议]` | `considerEnteringAsAttack = true` ⇒ 进图会掉关系；对 `Outpost`/`Turrets` 合理，需确认对无派系的 `Manhunters` 无副作用 |
| **误判"已清剿"** `[未决]` | `AmbushHidden` 的敌人是踩到矩形才刷的。玩家进图没触发就离开会被误判为已清剿 ⇒ 判定应加触发/时限条件（`GenStep_Ambush_Hidden.MakeRectTrigger` 的触发条件需实测） |
| 旧档兼容 `[未决]` | 已在进行中的委派若落在有威胁的矿点上：不追溯中止（宽限），只在下次派发时生效 |
| "部分清剿" | 不建议支持（把威胁点数压到 0 以下再委派），判定太脆 |

### 17.12 实施顺序

1. **§17.4 的 UI 缺陷**（site 页签）—— 独立于玩法，且是已存在的 bug（有威胁的矿点上委派信息不可见）。
2. **方案 A 闸门**：`ActualThreatPoints` 判定 + 灰显理由 + 「前往清剿」。
3. **清剿判定**：`Site.Notify_MyMapAboutToBeRemoved` postfix + `roThreatCleared`。
4. **方案 B**（设置开关 + `SetupCaravanAttackMap`）。

---

## 18. 战斗委派（世界地图抽象结算）

### 18.1 决策记录

| 问题 | 决定 | 备注 |
|---|---|---|
| 战斗在框架里的位置 | **两者都要** | ① 开采委派的前置阶段 ② 独立战斗委派目标 |
| 战场形态 | **世界地图抽象结算** | 不进图、不生成地图、不手动操作 |
| 参与者 | **车队全员** | 包括动物与驮兽；不做"部分人留守" |
| 与 §17 的关系 | §17 的清剿闸门降级为**兜底** | 只有"不适合抽象"的威胁才强制进图 |

### 18.2 vanilla 能借的与不能借的 `[验证]`

**能借（白捡的）：**

| 能力 | API | 证据 |
|---|---|---|
| 战报容器 | `Verse.Battle`（`IExposable`，含 `Create()` / `Add(LogEntry)` / `GetName()` / `Absorb`） | `02000321:T` |
| 战报入口 | `Find.BattleLog.Add(LogEntry)`，`BattleLog` 自身 `ExposeData` 存档 | `06001401:M` / `06001403:M` |
| 战报句子 | `BattleLogEntry_Event(Thing subject, RulePackDef eventDef, Thing initiator)` —— 句子由 `RulePackDef` 的语法生成 | `02000328:T` |
| 战报命名 | `Battle.GetName()` 走 `RulePackDefOf.Battle_Solo / Battle_Duel / Battle_Internal / Battle_War / Battle_Brawl` | `02000321:T` |
| 护甲减免 | `StatDefOf.ArmorRating_Sharp / _Blunt / _Heat`（1.6 存在） | `rimsearcher get ArmorRating_Sharp --type StatDef` |
| 命中相关 | `StatDefOf.ShootingAccuracyPawn` / `MeleeHitChance` / `MeleeDamageFactor` | 同上 |
| 近战输出 | `StatDefOf.MeleeDPS` 直接可读 | 同上 |
| 战利品 | `SitePart.lootThings` / `SitePartDef.lootTable` | `02003E0F:T` / `SitePartDef` 字段表 |
| 派系好感 | `Faction.TryAffectGoodwillWith`（`CaravanArrivalAction_VisitSite` 走 `SettlementUtility.AffectRelationsOnAttacked`） | `02003D56:T` |
| 敌人规模 | `SitePart.expectedEnemyCount`、`Site.ActualThreatPoints` | `02003E0F:T` / `1700360E:P` |

**不能借的：** vanilla **没有**世界地图级别的抽象战斗结算。`IncidentWorker_Ambush.DoExecute` 在目标不是地图时会调 `CaravanIncidentUtility.SetupCaravanAttackMap` **生成真地图**，所以伏击也不是抽象结算。⇒ **回合引擎必须自己写**，这是本功能最大的成本。

**`RangedDPS` 这个 StatDef 不存在**（`rimsearcher get RangedDPS --type StatDef` → MISS）⇒ 远程期望输出必须从 `VerbProperties` 现算（`burstShotCount` / `ticksBetweenBurstShots` / `accuracyMedium` / `defaultProjectile.projectile.damageAmountBase` / `armorPenetration`）。近战可以直接读 `StatDefOf.MeleeDPS`。

### 18.3 威胁分流表（`DelegationThreatPolicyDef`）`[建议]`

抽象结算不是"万能降级"。每个威胁 defName 一条策略：

| 字段 | 含义 |
|---|---|
| `resolution` | `Abstract`（可抽象） / `ForbidAbstract`（回落到 §17 闸门，必须进图） |
| `powerFactor` | 威胁点数 → 敌方战力的换算系数（补偿"抹平了敌方构成差异"） |
| `abstractPenalty` | 抽象修正（工事、射界等空间优势无法体现时的补偿性惩罚） |
| `preferredMode` | 默认 ROE |

| 威胁 | `resolution` | 理由 |
|---|---|---|
| `Manhunters` | `Abstract`（`powerFactor` 基准） | 纯近战对冲，空间因素极小 |
| `AmbushEdge` / `AmbushHidden` | `Abstract` | 遭遇战；vanilla 自己也有世界地图级别的伏击概念 |
| `SleepingMechanoids` | `Abstract`（`abstractPenalty` **为负**，即更有利） | 它们本来是"休眠"的，偷袭/绕过本来就是合理选项。进图反而变成面对一整张图的机械族 —— **抽象在这里语义上比进图更准确** |
| `Outpost` | `Abstract` + 重罚（v1 可先设 `ForbidAbstract`） | 有工事与守卫优势，空间因素开始主导 |
| `Turrets` | `ForbidAbstract` | 固定火力 + 射界，空间因素主导，抽象必错 |
| `MechCluster` | `ForbidAbstract` | 同上，还有迫击炮与支援建筑 |

⇒ `ForbidAbstract` 的威胁走 §17 的清剿闸门（灰显 + 「前往清剿」）。**§17 与 §18 拼起来才是完整设计。**

新增 Def 文件：`Defs/RimDelegation_ThreatPolicies.xml`。

### 18.4 结算引擎：分段掷骰 `[建议]`

**不写"战力比公式"。** 公式会忽略动力甲、护盾腰带、机械族护甲、灵能，而且每加一个装备 mod 就错一次。改为把战斗切成固定时长的回合，每回合用 vanilla 的真实数值掷骰：

```
回合长度 = 250 ticks（6 秒，与现有 tick / 营养模型对齐）

每回合：
  我方每个可战斗单位（!Downed && !Dead && 有攻击能力）：
      选目标 → 命中判定
        远程：verbProps 的 accuracyMedium 档位 × ShootingAccuracyPawn 因子
        近战：MeleeHitChance
      命中 → 伤害 = DamageDef 基础伤害 × 伤害因子，再走护甲减免
              （三档 ArmorRating_Sharp/_Blunt/_Heat 按 DamageDef.armorCategory 取）
      未命中 → 无事
  敌方（v1：战力档案）同样反击
  结算：HP 扣减 → 疼痛休克判定 → 倒地 / 死亡
  检查撤退阈值
未结束 → 下一回合
```

**明确声明抽象掉了什么**（写进 UI 与文档，不藏）：掩体、地形、走位与风筝、炮塔射界、灵能、技能冷却、爆炸物溅射、火势。分流表（§18.3）就是对这份损失清单的补偿。

**两个承重未决项 `[未决]`（最高优先级，必须先做原型验证）：**

| 项 | 问题 | 退路 |
|---|---|---|
| 无地图施加伤害 | `Pawn.TakeDamage` / `Pawn.Kill` 作用在未生成地图的车队成员上是否安全（部分路径会摸 `Map`：`FilthMaker` / `MoteMaker` / 尸体生成） | 绕开 `TakeDamage`，直接加 `Hediff_Injury`（`HediffDef` + `Severity`，是既有惯例） |
| 敌方侧怎么来 | (i) `ActualThreatPoints × powerFactor` 换算成"敌方战力档案"（稳、零清理负担） (ii) `PawnGroupMakerUtility.GeneratePawns` 真实生成敌人取数值后丢弃（更真，但要处理 world pawn 清理与存档膨胀） | **v1 用 (i)**，(ii) 留 v2 |

**随机源 `[建议]`**：回合引擎一律用局部 `System.Random`（种子从存档里的 `roCombatSeed` 取），**不要动全局 `Rand`** —— 否则预告用的蒙特卡洛会推进全局随机序列，造成同一 tick 内其它系统的随机性被污染，且战报不可复现。

### 18.5 预测即契约 `[建议]`

派发前的预告面板必须给确定数字，且**结算结果必须落在预告区间内**。实现方式不是调参，而是：

1. **预告与结算是同一个函数**：`Simulate(seed, scene) → CombatResult`。
   预告 = 用不同种子跑 N 次（建议 200）取分位：P50 给"预计"，P90 给"最坏情况"；结算 = 再跑一次（真实种子）。
   ⇒ 预告不是"另一个近似公式"，而是同一个引擎的采样统计，**结构上不可能出现"预告 95% 却全灭"**。
2. **方差靠回合数摊平**：一次性大掷骰必然出现极端结果；切成几十上百个回合后总结果自然向预测收敛。这是数学保证，不是数值调参。
3. **成本控制**：预告结果按 `(site, 参与者集合 + 装备指纹, policy)` 缓存，仅在名单/装备/策略变化时重算。单次迭代是几百次算术运算，200 次迭代可接受。

预告面板字段：

```
威胁：这片区域存在敌人活动的迹象
敌方：约 12 名敌人（威胁点数 850）          抽象修正：-15%（工事）
我方：5 人可战斗 · 期望输出 42.3 DPS · 平均护甲 0.31
────────────────────────────────
预计用时：4–6 小时
预计伤员：1–2 人（最坏 3 人）
预计阵亡：0 人（最坏 1 人）      ← 按 P90 给，不按均值
成功率：88%
补给消耗：营养 ×5 · 药品 ×4
────────────────────────────────
[ 开始清剿 ]   [ 放弃 ]     交战规则：标准 ▾   撤退阈值：任一死亡 ▾
```

### 18.6 代价与产出

| 维度 | 内容 | 实现 |
|---|---|---|
| 时间 | 每回合 250 ticks | 复用现有 tick 模型 |
| 营养 | 与开采共用消耗 | 已有 |
| 伤员 | 真实 `Hediff_Injury`，养伤期间不能开采 | 直接加 `Hediff`；`Tended` 质量按药品档位设置 |
| 死亡 | **真死，不掩盖** | 预告里红字 + P90 阵亡数 |
| 药品 | 治疗消耗库存 `Medicine` | `caravan` 物品栏查询与扣除 |
| 心情 | 战斗胜利 / 失利 / 战友阵亡 | 新增 `ThoughtDef`（已有 5 个的先例） |
| 派系好感 | 战斗/摧毁哨所 | `Faction.TryAffectGoodwillWith` |
| 战利品 | `SitePart.lootThings` / `SitePartDef.lootTable` | 现有逐时交付管线 |
| 摧毁目标 | 独立战斗委派可选"摧毁" | `site.Destroy()` 已实装 |

### 18.7 与开采委派的组合：阶段机

`DelegationWorker` 增加阶段枚举：

```
DelegationStage { Approach → Clear → Mine → Return }
```

- **开采前置**：`DelegationDef.combatRequired`（或按 §18.3 分流表自动判定）
- **独立战斗委派**：新 `DelegationDef_Assault`（目标 = 清剿，结束条件 = 威胁解除 / 天数上限 / 战利品配额）
- 两者**共用同一个引擎、同一套预告 UI、同一套战报**，差别只在 `DelegationDef` 的结束条件与产出声明

`ProgressLabel` / 逐人状态行 / 页签全部复用，新增 `Chisa 交战中。` / `Denia 倒地，正在撤离。` 两行文案。

### 18.8 UI 变更清单

| 位置 | 变更 | 备注 |
|---|---|---|
| **`WITab_Site_Delegation`（新增）** | 站点页签：委派状态 + 阶段 + 威胁行 + 「开始清剿」「查看战报」 | **同时修掉 §17.4 的信息抹除缺陷**（`Site.GetInspectString` 在有威胁时清空 builder，comp 的 `CompInspectStringExtra` 一起阵亡）⇒ 这个页签**必须先做** |
| `Dialog_CombatForecast`（新增） | §18.5 的预告面板 + ROE + 撤退阈值 | |
| 战报展示 | 写入 vanilla `Find.BattleLog` ⇒ 玩家在游戏原本的战斗日志 UI 里读；再给信件一个超链接 | 白捡 |
| `Dialog_ChooseDelegation` | 增加"目标"段（开采 / 清剿 / 清剿+开采）与相应控件 | 复用现有数字框与下拉 |
| 车队页签 + 车队信息栏 | 增加阶段行与逐人战斗状态 | 复用 §16.9 的逐人行机制 |
| 新 Keyed | `RimDelegationCombatForecast*` / `RimDelegationStage_*` / `RimDelegationROE_*` / `RimDelegationRetreat_*` / `RimDelegationBattleVictory|Defeat` / `RimDelegationThreatTier_*` | 中英双份 |

### 18.9 数据结构

- `DelegationThreatPolicyDef`：`resolution` / `powerFactor` / `abstractPenalty` / `preferredMode`（§18.3）
- `DelegationDef` 增：`combatRequired`（bool）、`combatMode`（`PrePhase` / `Standalone`）、`retreatPolicy`（Def 引用）
- `Delegation` 增：`stage`（枚举）、`roCombatSeed`（int）、`roCombatOutcome`（可 `IExposable` 的结果摘要）
- `DelegationWorker_Assault`
- `AbstractCombatSimulator`（静态、无状态，输入 `CombatScene`、输出 `CombatResult`）
- **Scribe 标签一律 `ro` 前缀**（同一 WorldObject 上所有 comp 共享一个扁平 XML 作用域，见 §15 修复记录）

### 18.10 边界与未决

| 项 | 说明 |
|---|---|
| 无地图伤害 `[未决]` | §18.4，最高优先级，先做原型 |
| 敌方侧数据来源 `[未决]` | §18.4，(i) 先行 |
| 撤退的物理含义 `[建议]` | 撤退 = 委派中止 + 回到世界地图仍在矿点格 + 威胁未解除；是否需要"脱离接触"额外回合（被追击伤亡）待定 |
| 全员倒地/死亡 | 接 vanilla `Caravan` 全灭语义（人员被俘/遗弃），不要自造结局 |
| 玩家中途手动干预 | 抽象路线下玩家无法中途接管（没有地图）。所以预告与撤退阈值是**唯一**的控制面，必须做扎实 |
| 时间跳跃与事件 | 抽象战斗会快进若干小时，期间 vanilla 的袭击/天气/疾病是否照常触发（取决于我们是 tick 推进还是一次性结算）⇒ `[建议]` 用 **tick 推进**（每回合 250 ticks 真实流逝），换取"世界在动"的一致性 |
| 与 `ForbidAbstract` 的分工 | 分流表必须可在 XML 里改，方便玩家/mod 调整口味 |
| 平衡上限 | 老练战斗队刷低级威胁点是否过强 ⇒ 用 `powerFactor` 与 `abstractPenalty` 控制，并在验收步骤里记录实测数据 |
| 兼容性 | 抽象路线不模拟 stat，天然兼容装备/基因/灵能 mod；但若某 mod 改了 `VerbProperties` 的语义（如 Combat Extended 的弹药/压制），引擎读到的是改后的值，结果会偏 —— 属可接受，需在 README 标注 |

### 18.11 实施顺序（MVP 切法）

1. **`WITab_Site_Delegation` + `Patches/RimDelegation_SiteTabs.xml`** —— 同时修 §17.4，是战斗与开采共用的状态出口，**先做**
2. **`AbstractCombatSimulator` 最小可用版** + 原型验证两个 `[未决]`（无地图伤害 / 敌方战力档案）
3. **`DelegationThreatPolicyDef` + 分流表**：v1 只放行 `Manhunters` / `Ambush*` / `SleepingMechanoids`；`Turrets` / `MechCluster` / `Outpost` 先设 `ForbidAbstract` → 走 §17 闸门
4. **`Dialog_CombatForecast`（蒙特卡洛预告）**
5. **阶段机 `DelegationStage` + 开采前置组合**
6. **战报写入 `BattleLog`**
7. **独立战斗委派 `DelegationWorker_Assault`**（战利品 / 好感 / `site.Destroy()`）
8. 心情 / 药品 / 交付细节打磨

**为什么这样切**：第 1 步独立可验证且修既有 bug；第 2 步先验证两个可能推翻设计的技术前提（如果无地图伤害不可行，整个抽象路线要重新评估）；第 3 步让功能"能玩但保守"，避免炮塔类算不准导致整个功能失信；第 7 步才是纯增量。

### 18.12 风险登记

| 风险 | 等级 | 缓解 |
|---|---|---|
| 无地图施加伤害不可行 | **高** | 第 2 步原型先验证；退路是直接写 `Hediff_Injury` |
| 结算结果与玩家直觉不符（"我 5 个海军甲怎么会输"） | 高 | 预告面板把引擎读到的数值**摊开给玩家看**（DPS/护甲/命中），让分歧可归因，而不是黑箱 |
| 抽象掉的正是某些威胁的核心 | 中 | §18.3 分流表；`ForbidAbstract` 兜底 |
| 预告性能 | 低 | 缓存 + 200 次迭代上限 |
| 快进时间引发的连锁事件 | 中 | 用 tick 推进而非瞬结；并在战报里显示经过时间 |
| 与其它战斗 mod 的语义差异 | 低 | 读的是它们改后的 stat 与 `VerbProperties`；README 标注 |

---

## 19. 委托：战斗 —— 逻辑规格

### 19.1 本节的位置与形态

§18 是**设计与决策**（为什么这样做、能借什么、风险在哪）；本节是**逻辑规格**（状态机、每回合做什么、数值从哪读、结局怎么判）。

**关于是否单独拆成 `COMBAT.md`：现在不拆。** 理由与 §14/§19 的代码分层口径一致：

1. 战斗的**契约面**是框架级的 —— 威胁分流表（§18.3）、阶段机、存档字段、预告即契约，都跨框架与战斗两侧。剪出去会让框架文档缺一块。
2. 单一真相源在本项目已经验证过价值（§15 修复记录里的事故都是靠"一处记全"才没复发）。
3. 拆分成本不是"多一个文件"，而是"以后每处改动都要改两个地方"。
4. **触发条件**（客观，免得凭感觉吵）：本节自身超过 ~600 行，或战斗引擎开始有独立的迭代/调参节奏（像 §15 那样出现独立增长的修复记录）时，才剪出 `COMBAT.md`；届时 DESIGN.md 只保留一节**契约摘要 + 链接**，绝不复制内容。

为保证将来"纯剪切、零改写"，本节自包含：不依赖 §18 的上下文也能读懂。

### 19.2 名词

| 名词 | 类型 | 含义 |
|---|---|---|
| 委派 | `Delegation` | 一个被 scribe 的状态对象，挂在 `WorldObjectComp_Delegations` |
| 阶段 | `DelegationStage` | `Approach` / `Clear` / `Mine` / `Return` |
| 战斗场景 | `CombatScene` | 一次结算的全部输入快照（我方单位 + 敌方档案 + 策略） |
| 战斗结果 | `CombatResult` | 结局 + 逐回合事件列表 + 伤亡/消耗统计 |
| 敌方名册 | `EnemyRoster` | 用 vanilla 种子生成的真 `Pawn` 列表（未 `Spawn`，§19.7） |
| 情报等级 | `IntelLevel` | `Vague` / `Scouted` / `Detailed`（§19.7 末） |

### 19.3 三条触发路径

| 路径 | 入口 | 结果 |
|---|---|---|
| **A. 开采前置** | 开采委派抵达矿点 → 检测威胁 → 分流表 `Abstract` | 插入 `Clear` 阶段，打完自动进 `Mine` |
| **B. 独立战斗委派** | 世界地图右键敌对站点 →「委派：清剿」 | 只有 `Clear` 阶段 |
| **C. 兜底（§17 闸门）** | 分流表 `ForbidAbstract` | **不进引擎**：灰显 + 「前往清剿」进图 |

### 19.4 状态机

```
Planned ──> Approach ──> Arrived ──> Forecast ──> Resolving ──> Settling ──> Mine? ──> Done
                            │            │             │             │
                            │            │             └── 撤退 ─────> Aborted
                            │            └── 玩家取消 ──> Aborted
                            └── 无威胁 / 无战斗目标 ─────> Mine
```

| 状态 | 进入条件 | 做什么 |
|---|---|---|
| `Planned` | 委派创建（预先委派） | 等车队抵达 |
| `Approach` | 车队出发 | 现有逻辑（行军 / 营养 / 休息补丁） |
| `Arrived` | `caravan.Tile == site.Tile` | `EnsureDeposit`；按 §18.3 判定威胁与分流 |
| `Forecast` | 有可抽象威胁 | 算 `CombatScene` → 蒙特卡洛预告 → 按设置决定是否弹 `Dialog_CombatForecast` |
| `Resolving` | 玩家确认（或自动） | 回合引擎推进，**每回合 250 ticks 真实流逝** |
| `Settling` | 引擎返回结局 | 施加伤害/死亡/战利品/好感/心情；置 `roThreatCleared`；写战报收尾条 |
| `Mine` | 结局为胜利且目标含开采 | 现有开采逻辑 |
| `Done` / `Aborted` | 结束条件 | 信件 + 清理 |

**关键选择：用 tick 推进而非一次性结算。** 每回合消耗 250 ticks 真实时间，意味着失血、疼痛、休克、倒地、感染、营养、心情、天气、袭击全部由 vanilla 各系统**自己照常运行** —— 我们只负责"施加伤害"和"判定结束"。这一条把工作量砍掉一大半，也让"世界在动"的一致性自动成立。

### 19.5 每回合逻辑

```
RunRound():                                  // 由 CompTickInterval 驱动，每 250 ticks 一次
  1. 我方行动
     foreach (unit in 可战斗单位，按随机顺序)
        if (unit 不能行动) continue          // Downed / Dead / 精神崩溃
        target = PickEnemyTarget(unit)
        ResolveAttack(unit, target)
  2. 敌方行动
     重复 enemyCount 次（存活敌人）
        target = PickOurTarget()
        ResolveEnemyAttack(target)
  3. 持续性：交给 vanilla（hediff / 失血 / 疼痛 / 倒地 都由它们自己 tick）
  4. 结束判定（顺序重要）
     a. 敌方兵力 <= 0                → Victory / PyrrhicVictory
     b. 我方无可战斗单位              → Defeat
     c. 撤退策略触发                  → Retreat（先跑一轮"脱离接触"）
     d. TicksGame - 开打 tick > 上限   → Timeout（按 Retreat 处理）
  5. 战报：只写"有意义的事件"（首次接火 / 我方倒地或阵亡 / 每 N 回合伤亡摘要 / 结局）
```

#### 19.5.1 索敌规格 `[验证]`（与 `CombatSimulator.PickTarget` 逐条对齐）

**双方共用同一个函数**，只换策略参数（`scene.OurPriority` / `scene.EnemyPriority`）。

```
候选集 = 敌方阵营 且 未退出战斗（!Out，即未倒地、未阵亡）
打分   = ScoreTarget(priority, 候选)
取值   = 最高分；**同分用蓄水池抽样随机决出**
```

| 策略 | 分值 | 语义 |
|---|---|---|
| `Strongest`（我方默认） | `ThreatWeight` = 命中率 × 射速 × 单发伤害 | "泛用输出最高"。**不看破甲能否打穿目标护甲，也不看目标还剩多少血** |
| `Weakest`（敌方默认） | `-HealthFraction` | 按**剩余耐久比例**而非绝对耐久 —— 半血的 400 血机械蜈蚣会优先于六成血的 100 血殖民者 |
| `Random` | 恒 `0` | 所有候选同分 ⇒ 由蓄水池抽样给出**等概率**选择，不需要单独分支 |

**粒度**：每次索敌发生在**单位出手前、每回合一次**，整轮连发（`burstShotCount`）全打同一个目标；目标在连发中途倒下时**不转火**（剩余子弹浪费）—— 与 vanilla 的一次 burst 承诺一个目标一致。

**抽象掉的**：没有距离、没有视线、没有逐目标掩体、没有"最近的敌人"。索敌是**全局名册**上的选择，不是空间上的选择。这条要在预告面板/README 里讲清楚，否则玩家会以为"最先冲上来的"会被优先打。

**`[未决]` 两个已知的模型缺口**（都由原型实测暴露）：

1. **没有目标黏性（target stickiness）**。真实 AI 会记住当前目标并持续打，只在目标失效时换人。本引擎每回合独立重选，于是：
   - 修复同分偏置前：`Strongest` 因名册顺序而**完美协同集火**，伤亡数字被系统性低估；
   - 修复后：目标相同时**各自独立随机**，火力分散，伤亡数字偏高。
   真实值在两者之间。补上黏性（`UnitState` 存一个 `currentTarget`，仅在其 `Out` 时重选）是下一步最值得做的模型改进。
2. **`ThreatWeight` 是"泛用输出"，不是"对我的威胁"**。真正的威胁应当考虑破甲 vs 我方护甲、以及"再打几下就能打掉它"。当前实现会让一个打不穿重甲的冲锋枪手与一个反装甲狙击手在排序上失真。

**撤退的"脱离接触"轮**：触发撤退后敌方获得一次额外攻击机会，输出乘以 `disengageFactor`（默认 0.5，另乘 `EnemyOutputFactor`）。这让"撤退"不是一个免费按钮，同时不至于惩罚过重。

**`OutputMultiplier`**：敌方每次出手都乘 `scene.EnemyOutputFactor` —— 这是 §18.3 分流表 `abstractPenalty`（工事优势 / 被偷袭）在引擎里的**唯一落点**。原型早期漏了这一步，导致该字段只影响预告的数字展示、不影响模拟结果（见 §19.17.3②）。

### 19.6 我方单位数值来源 `[验证]`

我方是**真 pawn**，所以只读、不抽象：

| 数值 | 来源 |
|---|---|
| 近战输出 | `StatDefOf.MeleeDPS`（该 StatDef 存在） |
| 远战输出 | **`RangedDPS` 这个 StatDef 不存在**，必须从 `pawn.equipment.PrimaryEq.PrimaryVerb.verbProps` 现算：`burstShotCount` / `ticksBetweenBurstShots` / `defaultProjectile.projectile.damageAmountBase` / `armorPenetration` |
| 远程命中 | `StatDefOf.ShootingAccuracyPawn` × `verbProps.accuracyMedium`（固定按中距档位） |
| 近战命中 | `StatDefOf.MeleeHitChance` |
| 护甲 | `StatDefOf.ArmorRating_Sharp / _Blunt / _Heat`（按 `DamageDef.armorCategory` 取对应档） |
| 耐久 | **不抽象** —— 走 vanilla `Pawn.TakeDamage`，HP / 伤痕 / 失血 / 感染 / 倒地 / 死亡全部由游戏处理 |

⇒ 武器、护甲、基因、仿生体、灵能装备的差异**自动正确**，因为读的是游戏自己算出来的 stat。

### 19.7 敌方编制：用 vanilla 的种子精确复现，不做系数估算 `[验证]`

> **本节修正了本设计早期的一版错误方案。** 原方案是"`threatPoints × powerFactor` 造一个聚合档案"，理由是"生成真敌人会导致存档膨胀 / 需要地图"。挖完生成链后确认这个理由**是错的**：编制在站点生成时就已经掷好并存进存档，而且生成**不需要地图**。系数估算等于把一个免费可得的事实换成了猜测。

#### 三段时间线

```
① 站点生成时（QuestGen / SiteMaker）
     SitePartParams 就在这里被填好
       randomValue  = Rand.Int                       ← 确定性种子
       threatPoints = 站点威胁点数
       animalKind                          （仅 Manhunters）
       turretsCount / mortarsCount         （仅 Turrets）

② 进图时（地图生成 → GenStep）
     GenStep_SitePawns.Generate 用 ① 存好的种子生成并 spawn

③ vanilla 自己算"有几个敌人"给玩家看
     SitePartWorker_Outpost.GetEnemiesCount()
     SitePartWorker_SleepingMechanoids.GetMechanoidsCount()
       → PawnGroupMakerUtility.GeneratePawnKindsExample(..., seed = ①的种子).Count()
       → 拼进 "KnownSiteThreatEnemyCountAppend" 显示在检视栏
```

#### 各威胁的编制来源

| 威胁 | 编制在哪决定 | 能否复现 |
|---|---|---|
| `Manhunters` | `parms.animalKind`（**存量数据**）+ `AggressiveAnimalIncidentUtility.GetAnimalsCount(kind, threatPoints)`（**纯函数**） | ✅ 完全确定，连采样都不需要 |
| `Turrets` | `parms.turretsCount = Mathf.Clamp(Mathf.RoundToInt(threatPoints / ThingDefOf.Turret_MiniTurret.building.combatPower), 2, 11)`、`parms.mortarsCount = Rand.RangeInclusive(0, 1)`（**都是存量数据**） | ✅ 完全确定 |
| `Outpost` | `GeneratePawns(seed = OutpostSitePartUtility.GetPawnGroupMakerSeed(parms) = parms.randomValue, groupKind = PawnGroupKindDefOf.Settlement, points = threatPoints, inhabitants = true)` | ✅ 确定性种子 |
| `SleepingMechanoids` | 同上，`seed = SleepingMechanoidsSitePartUtility.GetPawnGroupMakerSeed(parms)`、`groupKind = Combat`、`faction = Faction.OfMechanoids` | ✅ 确定性种子 |
| `AmbushEdge` / `AmbushHidden` | 地图生成时由 `GenStep_Ambush_Edge/Hidden` 放触发器，踩到才刷人 | ⚠️ 时机不确定，编制需另查 |
| `MechCluster` | 地图生成时由 GenStep 生成 `MechClusterSketch` | ❌ 不可预知（已 `ForbidAbstract`，无妨） |

#### 真实生成代码（`GenStep_SitePawns.Generate`）

```csharp
int pawnGroupMakerSeed = OutpostSitePartUtility.GetPawnGroupMakerSeed(parms.sitePart.parms);
PawnGroupMakerParms parms2 = GroupMakerParms(map.Tile, faction, parms.sitePart.parms.threatPoints, pawnGroupMakerSeed);
foreach (Pawn item in PawnGroupMakerUtility.GeneratePawns(parms2)) {
    GenSpawn.Spawn(item, cell, map);      // ← 只有这一行需要地图
    lord.AddPawn(item);
}
```

**`PawnGroupMakerParms` 没有 `map` 字段**（成员表只有 `tile` / `faction` / `groupKind` / `points` / `seed` / `inhabitants` / `traderKind` / `raidStrategy` / `generateFightersOnly` / `ideo` / `dontUseSingleUseRocketLaunchers` / `forceOneDowned` / `raidAgeRestriction` / `ignoreGroupCommonality`）。⇒ **`PawnGroupMakerUtility.GeneratePawns` 不需要地图**，只有 `GenSpawn.Spawn` 需要。

#### 结论：v1 直接用真敌人，不造档案

```csharp
EnemyRoster = PawnGroupMakerUtility.GeneratePawns(new PawnGroupMakerParms {
    tile        = site.Tile,
    faction     = policy.faction(site),   // 普通 = site.Faction；机械族 = Faction.OfMechanoids
    groupKind   = policy.groupKind,       // Settlement / Combat
    points      = sitePart.parms.threatPoints,
    inhabitants = policy.inhabitants,
    seed        = policy.GetSeed(sitePart.parms)   // = parms.randomValue
}).ToList();
```

拿到的是**一群真 `Pawn`**：真武器、真护甲、真血量、真技能，只是不在任何地图上。于是 §19.8 两侧走**同一条** `ResolveAttack` 路径 —— 不再有"我方走 vanilla、敌方用自己算的护甲"这种不对称近似。连带好处：

- 战利品 = 直接读敌方 pawn 的 `equipment` / `inventory`，不必绕 `lootThings`
- 预告面板可以显示真实构成（"3 名海盗 · 2 名重装 · 1 名狙击手"），而不是编造的"威胁等级"
- `PickEnemyTarget`（§19.5）从"只影响叙事"变成真选择 —— 集火、斩首、优先打狙击手才有意义
- 分流表里的 `powerFactor` / `abstractPenalty` 不再是"伪造数值"的旋钮，回归为**只表达空间优势损失**（工事、射界）的修正

**必须付的代价**（见 §19.14）：这些 pawn 从未 `Spawn`，战斗结束后必须显式清理，不能让 world pawn 系统收养它们。

#### 情报分级：算得出来 ≠ 应该直接显示 `[建议]`

vanilla 在**珍贵资源矿点上故意隐瞒了编制**：`Site.GetInspectString` 一旦命中 `mainPartAllThreatsLabel` 就 `break`（§17.1），所以 `Outpost` / `SleepingMechanoids` / `Turrets` / `Manhunters` 各自 `GetPostProcessedThreatLabel` 里那句"哨所：12 名敌人"在扫描矿点上**从不显示**；同一批 worker 在别的站点类型上却会显示。

按 §17.5"不做隐藏信息透视"原则，我们算得出来不等于应该白给。设计成情报等级：

| `roIntelLevel` | 怎么获得 | 预告给什么 |
|---|---|---|
| `Vague`（默认） | 抵达即有 | 只知道"有敌人活动" + 威胁强度档（低/中/高）；预告给**宽区间** |
| `Scouted` | 花一次**侦察**（耗时 N 小时，有遭伏击概率；可在对话框里选） | 精确编制（种类/数量/护甲档）；预告给**窄区间** |
| `Detailed` | 侦察 + 队伍里有高智识/高社交成员 | 同上，侦察更快、伏击概率更低 |

**关键区分：隐瞒的是"显示"，不是"计算"。** 引擎在任何情报等级下都在对**真实编制**做蒙特卡洛 —— 所以预告的分布始终是真实分布；`Vague` 只是把分布粗化后呈现。这样玩家的手感与"预告即契约"（§19.12）都不受损。

### 19.8 对称的命中与伤害施加 `[验证]`

两侧都是真 `Pawn`（§19.7），所以只有一条路径：

```csharp
void ResolveAttack(Pawn attacker, Pawn target, float hitChance, DamageDef def,
                   float amount, float armorPen, ThingDef weaponDef) {
    if (!Rand.Chance(hitChance)) { RecordMiss(attacker, target); return; }

    target.TakeDamage(new DamageInfo(
        def:                 def,               // 近战 / 远程 各取一次（§19.6）
        amount:              amount,
        armorPenetration:    armorPen,
        angle:               -1f,
        instigator:          attacker,
        hitPart:             null,              // 让 vanilla 自己选部位
        weapon:              weaponDef,
        category:            DamageInfo.SourceCategory.ThingOrUnknown,
        intendedTarget:      null,
        instigatorGuilty:    true,
        spawnFilth:          false,             // ★ 无地图必须关
        weaponQuality:       QualityCategory.Normal,
        checkForJobOverride: true,
        preventCascade:      false));
}
```

⇒ 护甲减免、身体部位选择、失血、感染、疼痛、休克、倒地、死亡**全部由 vanilla 处理**，我们一行都不写。

**`spawnFilth: false` 是必须的。** `DamageInfo` 的构造函数带 `spawnFilth` 参数，而泼溅物需要一张地图 —— 在"没有地图"的抽象战斗里必须关掉，否则会摸 `Map` 报错。这是 §18.4 那个"无地图施加伤害"高风险项的第一道防线。

**新代价：敌方 pawn 的生命周期。** 它们从未 `Spawn`，因此：

| 事项 | 处理 |
|---|---|
| 战后清理 | 显式 `Find.WorldPawns.RemovePawn(p)` + `p.Destroy()`，防止 world pawn 系统收养造成存档膨胀 |
| 战死 | `Pawn.Kill` 在 `Spawned == false` 时不应生成尸体（vanilla 行为，**需实测确认**） |
| 战斗中存档 | 未 spawn 的 pawn 会不会被 scribe、写多大 ⇒ **新增高风险未决项**，见 §19.14 |
| 战利品 | 战斗结束前先读 `equipment` / `inventory`，再销毁 |

### 19.9 撤退策略 `[建议]`

`RetreatPolicyDef`：

| 选项 | 触发 |
|---|---|
| `从不` | 打到全灭 |
| `任一倒地` | 有单位 `Downed` |
| `任一死亡` | 有单位被打死 |
| `健康低于 N%` | 任一单位 `health.summaryHealth.SummaryHealthPercent < N` |
| **`伤亡比例超过 N%`（默认，N≈0.34）** | 伤亡数 / 参战数 ≥ N |

**默认值是原型跑出来的结论，前后改过两次，两次都是原型推翻的**（详见 §19.17 修正记录）：
- 「任一死亡」在这套模型里**等价于永不撤退** —— 倒地单位会被目标选择跳过（与 vanilla AI 一致），而 vanilla 的失血致死是小时级尺度，几十秒的短促战斗只产生倒地。
- 改成「任一倒地」又走到另一个极端：开局倒一个就跑，4 人小队在稳赢局里胜率只剩 8%。
- 二元触发器（有/没有）在两个方向上都失真，故默认改为**比例式**。

`[未决]` 4 人小队的比例粒度太粗（1/4=25% 不触发，2/4=50% 才触发）。建议 Def 同时暴露**绝对人数阈值**与**兵力比**（我方可战 : 敌方可战）两种表述，让玩家按队伍规模选。

预告面板里可选、可存档（写在 `Delegation` 上，不写在方案上，因为每队承受力不同）。

### 19.10 结局表

| 结局 | 触发 | 后果 |
|---|---|---|
| `Victory` | 敌方兵力 ≤ 0 | `roThreatCleared = true`；战利品；好感；心情`胜利`；进下一阶段 |
| `PyrrhicVictory` | 胜但我方有死亡 | 同上 + 更重的负心情；信件语气不同 |
| `Retreat` | 撤退策略触发 | 威胁**未**解除；委派中止；伤员随队；心情`撤退` |
| `Defeat` | 我方无可战斗单位 | 接 **vanilla `Caravan` 全灭语义**（人员被俘/遗弃），不自造结局 |
| `Timeout` | 超天数上限 | 按 `Retreat` 处理 |

**只有 `Victory` / `PyrrhicVictory` 才置 `roThreatCleared`。** 撤退或超时都不置 —— 威胁还在。

### 19.11 战报（vanilla `BattleLog` 自动归组）`[验证]`

`Find.BattleLog.Add(LogEntry)` 的行为（反编译确认）：

```
1. 遍历 entry.GetConcerns()，取每个 pawn 的 records.BattleActive
2. 都没有 → Battle.Create() 并插入 battles 列表
3. 有 → 取 Importance 更大者，并把其余 Absorb 进来
4. 对每个 concern 调 pawn.records.EnterBattle(battle)
5. battle.Add(entry)
```

⇒ **只要把 `BattleLogEntry_Event(subject, RulePackDef, initiator)` 交给 `Find.BattleLog.Add`，战斗分组与命名（`Battle.GetName()` 走 `RulePackDefOf.Battle_Solo/Duel/Internal/War/Brawl` 语法）全部自动完成**，玩家在原版战斗日志 UI 里就能读，还能点人名跳转。这块是白捡的。

`[未决]`：若参与者身上仍挂着**上一场**战斗（`records.BattleActive` 未过期），新战报会被并入旧战斗。可能需要在开打前先清空该状态（`EnterBattle(null)` 或等价手段），需实测确认。

### 19.12 预告 = 同一个引擎的采样 `[建议]`

```csharp
CombatResult Simulate(int seed, CombatScene scene);   // 唯一入口

// 预告：跑 200 次不同种子
forecast = MonteCarlo(Simulate, scene, n: 200)
   → P50 给"预计"，P90 给"最坏情况"

// 结算：再跑一次，种子 = delegation.roCombatSeed（已 scribe，可复现）
result = Simulate(delegation.roCombatSeed, scene)
```

**预告不是另一个近似公式，而是同一引擎的采样统计** ⇒ 结构上不可能出现"预告 95% 却全灭"。方差由回合数摊平（几十到上百回合，单回合小方差），这是数学保证，不是数值调参。

随机源：**用 `Rand.PushState(seed)` / `Rand.PopState()` 把全局随机序列隔离起来**（两者都是 `public static`），而不是自建 `System.Random`。

原因：我们要调用的一批 vanilla 函数**内部就在用 `Rand`**（例如 `ArmorUtility.ApplyArmor` 里的 `Rand.Value`）。自己用 `System.Random` 并不能阻止它们动全局序列 —— 200 次采样照样污染同一 tick 内其它系统。`PushState/PopState` 是 vanilla 自己的隔离机制：每次迭代 `PushState(seed_i)` → 跑完 → `PopState()`，全局序列分毫不动，且每次迭代可复现。

> 这条**修正了本设计早期的一版规则**（原写"一律局部 `System.Random`，绝不动全局 `Rand`"）。那个规则在"不调用任何使用 `Rand` 的 vanilla 函数"的前提下成立，但 §19.16 的计算方案恰恰要调用它们。

缓存键：`(site, 参与者集合 + 装备指纹, policy, roe, retreatPolicy)`，变化时才重算。

### 19.13 存档字段

`Delegation` 增（**全部 `ro` 前缀**，同一 WorldObject 上所有 comp 共享一个扁平 XML 作用域，见 §15）：

| 字段 | 类型 | 用途 |
|---|---|---|
| `roStage` | `DelegationStage` | 当前阶段 |
| `roCombatSeed` | `int` | 结算种子（可复现） |
| `roCombatRound` | `int` | 已进行的回合数 |
| `roCombatStartTick` | `int` | 开打 tick（算用时与超时） |
| `roEnemySeed` | `int` | 敌方编制种子（= `parms.randomValue`，用于复现与读档重建） |
| `roIntelLevel` | `IntelLevel` | 情报等级（§19.7） |
| `roRoe` / `roRetreatPolicy` | `Def` | 玩家选择 |
| `roThreatCleared` / `roThreatClearedTick` | `bool` / `int` | §17.6 的清剿标记 |

**敌方名册怎么过存档**（三选一，取决于 §19.14 第 4 项的实测结果）：

| 方案 | 做法 | 代价 |
|---|---|---|
| A. 深度 scribe | `Scribe_Collections.Look(ref enemies, "roEnemies", LookMode.Deep)` | 正确但存档变大；Pawn 深写是重操作 |
| B. 种子重建（配合 `roEnemySeed`） | 只存种子 + 每个敌人的血量比例数组；读档时重新 `GeneratePawns` 再套用血量 | 轻，但要求生成是**逐人确定性**的（§19.14 第 5 项） |
| C. 两段式快照 | 生成后立刻抽出 `CombatUnitProfile` 并销毁 pawn，只 scribe 档案 | 最轻，但失去真目标选择与"直接读装备当战利品" |

**v1 取 B**（最省存档、且顺便验证第 5 项）；若第 5 项不成立则退 C。

### 19.14 未决项与原型验证清单

原型阶段**必须先验证下列各项**，其中第 1–3 条任一条不成立都要回到设计：

| # | 待验证 | 不成立的后果 | 退路 |
|---|---|---|---|
| 1 | 无地图时 `Pawn.TakeDamage` 是否安全（已用 `spawnFilth: false` 挡掉已知的一条） | 抽象路线整体重估 | 绕开 `TakeDamage`，直接加 `Hediff_Injury` |
| 2 | 无地图时 `Pawn.Kill` 是否安全（尸体生成需要地图？`Spawned` 为 false 时 vanilla 是否跳过） | 死亡无法表现 → 改用"失踪/被俘"语义 | 让 `Caravan` 自行处理减员（`caravan.RemovePawn`） |
| 3 | `BattleLog.Add` 是否会被参与者身上的旧 `BattleActive` 误并 | 战报混进上一场战斗 | 开打前清空 `BattleActive` |
| 4 | **未 spawn 的敌方 pawn 会不会被 scribe 进存档、写多大** | 存档膨胀 / 读档时 pawn 状态不一致 | 战斗期间禁止存档？或改用"只取数值不留对象"的两段式（见下） |
| 5 | `GeneratePawnKindsExample` 与真实 `GeneratePawns` 的构成是否逐人一致 | 预告里的"3 名海盗 · 2 名重装"与实际不符 | 预告直接用 `GeneratePawns` 的结果（即 §19.7 的方案），只在极少场合退回 example |
| 6 | `GeneratePawns` 传 `inhabitants: true` 而无 `map` 是否会在某些 group kind 上摸 `Map` | 生成失败 / 报错 | 按 group kind 分支：失败者退回 `GeneratePawnKindsExample` + 系数 |
| 7 | `AmbushEdge` / `AmbushHidden` 的伏击编制（`GenStep_Ambush_Edge/Hidden` 的触发与生成） | 伏击类无法进入抽象引擎 | 把 `Ambush*` 移入 `ForbidAbstract`（回落到 §17 闸门） |

其余未决：分流表 `abstractPenalty` 的取值标定（回归为"仅空间优势损失"后需重测）、`PickOurTarget` 默认策略的体感、脱离接触轮系数、快进期间 vanilla 事件的连锁影响、侦察阶段的耗时与伏击概率。

**第 4 项的设计预案**（若 scribe 风险不可接受）：把 §19.7 拆成两段 —— 开打时用 `GeneratePawns` 生成真 pawn 并**立即快照**出 `CombatUnitProfile`（DPS / 命中 / 护甲 / HP / 装备清单），然后立刻销毁 pawn，战斗只在档案上进行。这样回到"档案"模型但**数值仍然来自真实生成**，只是不再持有对象。代价是失去"集火具体目标"与"直接读装备当战利品"，需要把装备清单存进档案。

### 19.15 伪代码汇总

```csharp
// ── 抵达 ────────────────────────────────────────────
void OnArrived() {
    comp.EnsureDeposit(def);
    var threat = ThreatAnalyzer.Analyze(site);            // 读 parts / ActualThreatPoints
    if (threat == null || roe == Roe.Skip) { BeginMine(); return; }

    var policy = ThreatPolicyFor(threat);                  // §18.3 分流表
    if (policy.resolution == Resolution.ForbidAbstract) {  // §17 兜底
        deleg.EndWith(EndReason.NeedsManualClear); return; // → 灰显 + 「前往清剿」
    }
    scene = BuildScene(caravan, threat, policy, roe, retreatPolicy);
    if (Settings.requireConfirmOnCombat) OpenForecastDialog(scene);   // §19.12
    else BeginResolve(scene);
}

// ── 每 250 ticks ────────────────────────────────────
protected override void Tick() {
    if (roStage != DelegationStage.Clear) { base.Tick(); return; }
    if (TicksGame - roCombatStartTick < (roCombatRound + 1) * TicksPerRound) return;
    roCombatRound++;

    foreach (var u in Combatants().OrderByRandom()) ResolveAttack(u, PickEnemyTarget(u));
    for (int i = 0; i < scene.enemies.alive; i++) ResolveEnemyAttack(PickOurTarget());

    var end = CheckEnd(out CombatOutcome outcome);
    WriteRoundLog(end);
    if (end) Settle(outcome);                              // §19.10
}

// ── 结算 ────────────────────────────────────────────
void Settle(CombatOutcome outcome) {
    ApplyLoot(); ApplyGoodwill(); ApplyThoughts();
    if (outcome is Victory or PyrrhicVictory) {
        comp.roThreatCleared = true; comp.roThreatClearedTick = TicksGame;
        if (def.targets.Contains(Mine)) roStage = DelegationStage.Mine;
        else Finish();
    } else {
        Abort(outcome);                                    // 威胁仍在，委派中止
    }
}
```

---

### 19.16 战斗计算规格（命中 · 伤害 · 节奏 · 随机源）`[验证]`

#### 19.16.1 总原则

**能复用 vanilla 的就复用，只建模"必须建模的"。** 逐项审计 `ShotReport.HitReportFor` 的合成链（`02000D6C:T`），把每个因子分成三类：

| 因子 | 处置 | 依据 |
|---|---|---|
| 射手技能 × 距离 | ✅ **精确复用** | `ShotReport.HitFactorFromShooter(Thing, float, float?)` 是 **public static 且零地图依赖** |
| 武器精度 | ✅ **精确复用** | `VerbProperties.GetHitChanceFactor(Thing equipment, float dist)` **public** |
| 目标体积 | ✅ **精确复用** | vanilla 原式 `Mathf.Clamp(pawn.BodySize, 0.5f, 2f)` —— 因为敌方也是真 pawn（§19.7），`BodySize` 直接读 |
| 护甲结算 | ✅ **精确复用** | `ArmorUtility.GetPostArmorDamage(Pawn, float, float, BodyPartRecord, ref DamageDef, out bool, out bool)` **public static**，直接吃 `Pawn` |
| 近战伤害 / 破甲 / 冷却 | ✅ **精确复用** | `VerbProperties.AdjustedMeleeDamageAmount / AdjustedArmorPenetration / AdjustedCooldownTicks` 全 **public**，签名取 `(Verb, Pawn)` |
| 掩体 | ⚠️ **必须建模** | `CoverUtility.CalculateOverallBlockChance` 要地图格与位置 → 用 Def 常数替代 `PassCoverChance` |
| 天气 / 屋顶 / 黑暗 / 烟雾 | ❌ **放弃** | 无地图 ⇒ 因子取 1（Def 可覆盖） |
| 姿态（卧倒、处决） | ❌ **放弃** | `FactorFromPosture` / `FactorFromExecution` 取 1（§19.5 的"倒地即退场"已覆盖大部分语义） |
| 走位 / 射界 / 灵能 / 溅射 | ❌ **放弃** | 由分流表 `abstractPenalty` 统一补偿（§18.3） |

**注意 `ShotReport.HitReportFor` 本身不能用** —— 它读 `caster.Position` / `caster.Map` / `CoverUtility` / `caster.Map.weatherManager`。我们只借它那两个 public static 的分解因子。

#### 19.16.2 命中率

```csharp
float dist = policy.assumedEngagementDistance;              // Def，默认 12（短/中档交界）

float p = ShotReport.HitFactorFromShooter(attacker, dist)          // 技能^距离 × 四档距离曲线
        * verb.verbProps.GetHitChanceFactor(verb.EquipmentSource, dist)   // 武器精度
        * Mathf.Clamp(target.BodySize, 0.5f, 2f)                   // 目标体积（vanilla 原式）
        * policy.coverPassFactor;                                  // ← 唯一自建项，替代 PassCoverChance
p = Mathf.Clamp01(p);
```

`HitFactorFromShooter` 内部（反编译确认）：

```
f   = caster.GetStatValue(StatDefOf.ShootingAccuracyPawn)     // 或 ShootingAccuracyTurret
num = Mathf.Pow(f, distance)
× ShootingAccuracyFactor_Touch / _Short / _Medium / _Long（按 3 / 12 / 25 / 40 分段线性插值）
return Mathf.Max(num, 0.0201f)
```

⇒ **技能与距离的效果与地图内战斗逐位一致**。`StatDefOf.ShootingAccuracyFactor_*` 这四个 stat 也顺带被覆盖，改装 mod 加的技能加成自动生效。

#### 19.16.2.1 命中率的因子归属：vanilla 是"射手 × 目标 × 环境"三组 `[未决]`

把 vanilla `ShotReport` 的合成链按**因子归属**重排，会看到一个本模型尚未处理的结构问题：

| 归属 | 因子 | vanilla 里对谁求值 |
|---|---|---|
| **射手侧** | `factorFromShooterAndDist`（技能^距离 × 四档曲线） | 射手 |
| **射手侧** | `factorFromEquipment`（`GetHitChanceFactor`） | 射手的武器 |
| **目标侧** | `factorFromTargetSize`（`Clamp(BodySize, 0.5, 2)`） | **目标** |
| **目标侧** | `FactorFromPosture`（目标卧倒且距离 ≥ 4.5 ⇒ ×0.5） | **目标** |
| **目标侧** | `PassCoverChance` = `1 - coversOverallBlockChance`（射手→目标之间的掩体） | **目标相对射手** |
| **环境侧** | `factorFromWeather` / `factorFromCoveringGas` / `offsetFromDarkness` | 场景 |

⇒ **五个因子里有三个是目标侧的**，而我们的 `CombatUnitSnapshot.HitChance` 是一个**每攻击者一个**的标量，等于把目标侧那三个都吞进了攻击者自身。

在"所有目标体型/姿态/掩体都相同"的场合（即当前原型与 CombatLab 的全部场景）**结果完全正确**；但一旦目标之间存在差异就会失真，例如：

- §19.7 里敌方是**真 `Pawn`** —— 机械蜈蚣与人类的 `BodySize` 差一个量级，同一个射手打它们的命中率本不该相同；
- vanilla 的 `MeleeHitChance` / 掩体也都带目标侧成分。

**修法（尚未实施）**：把快照字段从 `HitChance` 拆成
`AccuracyFactor`（射手侧，=`HitFactorFromShooter × GetHitChanceFactor`）
与目标侧的 `BodySize` / `CoverPass` / `PostureFactor`，在**结算时**逐目标相乘：

```
hit = AttackersAccuracy × Clamp(target.BodySize, 0.5, 2) × target.CoverPass × target.PostureFactor × sceneEnvFactor
```

这同时会把当前那个"场景级掩体通过率"升级成**逐目标掩体**，掩体才真正影响相对强弱（见 §19.16.8）。

#### 19.16.3 节奏：一回合打几轮

不逐 tick 模拟开火，而是用 vanilla 自己的周期函数折算：

```csharp
const int TicksPerRound = 250;                                   // 6 秒

float cycleSeconds = verb.verbProps.AdjustedFullCycleTime(verb, attacker);   // public
int bursts = Mathf.Max(1, Mathf.RoundToInt(TicksPerRound / (cycleSeconds * 60f)));
int shots  = bursts * verb.verbProps.burstShotCount;
```

`AdjustedFullCycleTime` 已经把 `StatDefOf.RangedCooldownFactor` / `AimingDelayFactor` / 瞄准延迟等算进去了，所以"每回合几次射击"不是估算。近战同理，用 `AdjustedCooldownTicks(verb, attacker)`。

#### 19.16.4 伤害：两条通道，同源公式

**通道 A —— 实际结算：直接 `TakeDamage`。**
不自己算护甲，让 vanilla 走完整条链（护甲、部位、失血、感染、疼痛、休克、倒地、死亡）。见 §19.8。

**通道 B —— 预告与 AI 决策：解析期望，不碰 pawn。**
vanilla 的护甲结算本体（`ArmorUtility.ApplyArmor` 反编译）：

```csharp
float num   = Mathf.Max(armorRating - armorPenetration, 0f);
float value = Rand.Value;                       // ← 均匀分布
if      (value < num * 0.5f)  damAmount = 0f;                          // 完全弹开
else if (value < num)         damAmount = GenMath.RoundRandom(damAmount / 2f);  // 减半
                                                                        // 且锐器变钝击
```

因为 `Rand.Value` 是均匀分布，这个分布的**概率是解析可得的**：

```
P(弹开)  = Clamp01(num * 0.5)
P(减半)  = Clamp01(num - num * 0.5)
E[伤害]  = amount * (1 - P(弹开) - P(减半)) + (amount / 2) * P(减半)
```

⇒ **预告通道可以做到"分布精确"，而不是"伤害估算"。** 每个虚拟单位的 `health` 用一个浮点表示，每次射击照常掷命中与护甲档（用精确分布），但**不触碰任何真 pawn**。

`armorRating` 取 `pawn.GetStatValue(StatDefOf.ArmorRating_Sharp / _Blunt / _Heat)`（按 `DamageDef.armorCategory`）—— 这个 stat 本身就是 vanilla 对身体各部位护甲的加权平均，所以"用聚合值代替逐部位"是**语义一致**的近似，不是随手拍。

#### 19.16.5 随机源与可复现性

```csharp
Rand.PushState(seed);      // public static，隔离全局序列
try   { /* 跑一次完整模拟 */ }
finally { Rand.PopState(); }
```

- **必须 `try/finally`**：模拟中途抛异常而不 `PopState` 会让全局随机栈泄漏，之后所有游戏内随机都错位。
- 预告：`for (i in 0..200) { Rand.PushState(hash(scene, i)); Simulate(); Rand.PopState(); }`
- 实际结算：`Rand.PushState(delegation.roCombatSeed); Simulate(); Rand.PopState();`
- 战报与结算结果因此**完全可复现**（调试时能用同一个种子重演任何一场战斗）。

#### 19.16.6 预报与结算为什么不冲突

| | 预告通道（B） | 结算通道（A） |
|---|---|---|
| 命中率 | 同一个 `HitFactorFromShooter` × `GetHitChanceFactor` | 同 |
| 伤害 | vanilla 护甲公式的**解析分布** | vanilla 护甲公式的**实现**（`ApplyArmor`） |
| 部位 | 用 `ArmorRating_*` 聚合值 | 逐部位随机 |
| 单位状态 | 虚拟 `health` 浮点 | 真 `Hediff` / `Pawn` |

两通道的**系统性偏差只来自"聚合护甲 vs 逐部位"**，而这正是 vanilla 自己 `ArmorRating_*` 的定义口径。剩余差异全是随机性 —— 而随机性由 §19.12 的"同一引擎采样"处理。⇒ **预告给出的 P50/P90 是可信区间，不是乐观估计。**

#### 19.16.7 待标定的 Def 参数

| 参数 | 位置 | 默认 | 说明 |
|---|---|---|---|
| `assumedEngagementDistance` | `DelegationThreatPolicyDef` | 12 | 抽象交战距离（短/中档交界），决定命中率基准。**`[未决]` 当前实现里还没有这个字段** —— 见 §19.19.3① |
| `coverPassFactor` | 同上 | 见下 | 替代 `PassCoverChance`（**不是难度旋钮**，语义见 §19.16.8） |
| `abstractPenalty` | 同上 | 0 | 空间优势损失的统一修正（§18.3），引擎里的落点是 `EnemyOutputFactor` |
| `TicksPerRound` | 常量 | 250 | 6 秒 |
| `forecastIterations` | Mod 设置 | 200 | 预告蒙特卡洛次数 |

这五项是**全部**需要人工标定的数值 —— 其余每一个数字都来自 vanilla 的 stat、`VerbProperties` 或 `ArmorUtility`。这是本方案相对"自造战力公式"的核心优势。

> ⚠️ **上面这句话目前不准确。** 完整的未知量清单（20 项，其中 12 项是当前实现里客观存在但 UI 没暴露的）见 **§19.19**。真实情况是：当前实现只有 8 个可见旋钮，但被静默固定的假设有 9 项，另有 3 项属于"换了模型"而非"少一个数"。

**关于 `coverPassFactor` 的两个默认值（曾不一致，现说明清楚）**：

| 场合 | 取值 | 为什么 |
|---|---|---|
| 原型 / CombatLab 的基线 | **1.00** | 控制台场景没有掩体概念，取 1.00 才能与"无掩体基准"对齐，交叉验证才有意义 |
| 游戏内接入时的建议标定起点 | 0.75 | 野外遭遇战里双方通常都有一定掩体；`Outpost`（工事）应更低 |

**这两者不矛盾**：前者是"实验室基线"，后者是"游戏内待标定值"。`[未决]` 游戏内接入后应当用真实地图战斗数据反推，而不是照抄 0.75。

#### 19.16.8 「掩体通过率」到底是什么 `[验证]`

**定义**：一次射击**穿过目标掩体**的概率。它就是 vanilla `ShotReport.PassCoverChance` 的同名量：

```
vanilla:  PassCoverChance = 1f - coversOverallBlockChance
          由 CoverUtility.CalculateOverallBlockChance(target, caster.Position, caster.Map) 从地图几何算出
本模型:    取一个标量代替（因为没有地图）
```

它进入公式的位置与 vanilla **完全一致**：

```
vanilla:  TotalEstimatedHitChance = Clamp01( AimOnTargetChance × PassCoverChance )
本模型:   HitChance               = … × coverPassFactor
```

⇒ **`1.00` = 完全没有掩体；`0.75` = 平均每 4 发有 1 发被掩体挡下；`0.30` = 大部分射击都被挡住。**

##### 但它是**双方对称**的，所以它不是难度旋钮 `[验证]`

CombatLab 的参数扫描（`--sweep cover`，预设「猎杀人类（8 野兽）」，300 次蒙特卡洛）：

| 掩体通过率 | 成功率 | 伤员 P50 | 回合 P50 | 用时 |
|---|---|---|---|---|
| 1.00 | 54% | 1 | 9 | 0.9 小时 |
| 0.80 | 57% | 1 | 10 | 1.0 小时 |
| 0.60 | 59% | 1 | 13 | 1.3 小时 |
| 0.50 | 58% | 1 | 16 | 1.6 小时 |
| **0.30** | **60%** | 1 | **26** | 2.6 小时 |

对照 `--sweep enemyfactor`（非对称修正）：

| 敌方输出修正 | 成功率 | 回合 P50 |
|---|---|---|
| 0.20 | **100%** | 8 |
| 1.00 | 54% | 9 |
| 1.50 | **8%** | 6 |
| 2.00 | **1%** | 4 |

**结论**：

- ✅ **对称折损一定显著拉长战斗**（任意场景都成立）：上表 9 → 26 回合，约 3 倍。
- ✅ **在双方兵力结构对称时，它才是胜负中性的** —— 这是它"不是难度旋钮"这个说法唯一站得住的版本。
- ❌ **兵力结构不对称时，它会实质改变胜负。**

> ⚠️ **这条是本设计被实测推翻并更正的结论。**
>
> 我最初根据上面那张旧表（54% → 60%）写下"胜负几乎不变"，并据此断言"掩体通过率不是难度旋钮"。
> 加入空间模型后重测同一个猎杀人类场景：**88% → 69%，19 个百分点**。
>
> 原因是**对称的输入 ≠ 对称的结果**：命中率减半等于战斗时长翻倍，而长战斗对
> "总耐久 × 持续输出"更强的一方有利。猎杀人类局里敌方是 8 只野兽（640 总耐久、8 次攻击/回合），
> 我方是 4 人（400 总耐久），双方兵力结构根本不对称，所以这个旋钮会改变胜负。
>
> 现在的断言拆成三条，把更正后的认识钉死（`CoverFactorIsSymmetricAndLengthensFight`）：
> ① 对称折损显著拉长战斗；② **镜像对局**下胜负中性；③ 兵力不对称时**会**改变胜负（>8 个百分点）。
> 第 ③ 条是"记录的失败"—— 它防止以后有人把这条结论又改回过度简化的版本。
>
> 教训：**"双方都乘同一个系数"不等于"结果不变"。** 只要动力学里有非线性或吸收态
> （这里是"耐久耗尽"与"撤退阈值"），乘性扰动就会改变竞赛的胜负概率。

- **想调难度首选 `EnemyOutputFactor`（非对称修正）**，它在 0.20→2.00 之间能把成功率从 100% 拉到 1%，
  非常灵敏且方向明确。三者的分工：

| 旋钮 | 对称性 | 直接作用 | 适合用来调 |
|---|---|---|---|
| `掩体通过率` | 对称 | 双方命中率 | **时间尺度**（拉长/缩短交火） |
| `天气` | 对称 | 双方命中率 | 同上，但**逐场随机**（影响的是分布宽度） |
| `无优势射程系数` | **非对称** | 一方的**射程** | **谁先开火 / 抢到多少轮先手**（§19.20） |
| `EnemyOutputFactor` | 非对称 | 敌方的**输出** | **难度** |

##### `[未决]` 与 vanilla 的结构差异

vanilla 的掩体是**目标侧**的量（射手→目标之间的几何），而本模型是**场景级标量**。
所以当前的「掩体通过率」只能表达"整片战场都更难打中"，无法表达
"敌人躲在掩体后、我方暴露在开阔地"这种**非对称**态势 —— 后者恰恰是掩体在真实战斗里最有意义的部分。

彻底修法见 §19.16.2.1：把掩体降为**逐目标**属性，在结算时对每一个 (射手 → 目标) 单独相乘。
在那之前，非对称的地形优势只能用 `EnemyOutputFactor` 粗略替代（这正是 §18.3 分流表里
`Outpost` 的 `abstractPenalty` 设为 1.15 的原因）。

---

### 19.17 原型：独立可测的战斗计算核心 `[验证]`

#### 19.17.1 位置与运行方式

**刻意放在 `Source/` 之外**，因此完全不影响现有 mod 编译（`Source/RimDelegation.csproj` 的 `<Compile Include="**\*.cs">` 只覆盖 `Source/`）。

```
RimDelegation/Prototype/
  RimDelegation.Prototype.csproj       net10.0 控制台；**不引用 RimWorld / Unity**
  Program.cs                       selftest | demo | dump [场景] | battle <场景> [种子] | replay <文件>
  Combat/
    Core/                          纯计算核心 —— 与控制台、桌面 UI（§19.18）共用同一批源文件
      IRng.cs                      随机源接口（核心与"用哪个 RNG"解耦）
      XorShiftRng.cs               纯确定性 RNG（离线自测与复演）
      CombatMath.cs                护甲分布的**解析形式** + vanilla 参照实现
      CombatUnitSnapshot.cs        单位数值快照（游戏侧与核心之间的唯一契约）
      CombatScene.cs               场景 + 撤退策略 + 目标选择策略
      CombatResult.cs              结局 / 逐人终局 / 可复现指纹
      CombatSimulator.cs           回合引擎（预告与结算共用同一个函数）
      Forecast.cs                  蒙特卡洛 P50/P90
      SnapshotCodec.cs             快照文本编解码（游戏内抓数据 → 离线复演）
    Tests/                         只给控制台与 UI 用，不进游戏
      Scenarios.cs                 标定过的样例场景
      SelfTest.cs                  17 项断言（输出可重定向，见 §19.18.3）
```

```powershell
cd RimDelegation\Prototype
dotnet run -- selftest              # 17 项断言，退出码 = 失败数是否为 0
dotnet run -- demo                  # 各场景预告 + 一场样战斗报
dotnet run -- battle sleepingmechs 4242
dotnet run -- dump outpost > s.txt; dotnet run -- replay s.txt
```

#### 19.17.2 断言清单（当前 17/17 通过）

| 类别 | 断言 |
|---|---|
| 数学 | 解析护甲分布 == vanilla `ApplyArmor` 分支链（6 组 × 40 万次采样，逐档概率在 3σ 内） |
| 数学 | 解析期望伤害 == 参照实现采样均值（5 组 × 40 万次） |
| 不变量 | 同种子 × 50 次 ⇒ 结果指纹完全一致 |
| 不变量 | 不同种子 × 200 次 ⇒ 结果有分布（非退化） |
| 不变量 | `Simulate` 不修改输入场景（20 次后快照逐字节一致） |
| 不变量 | 随机性只经由注入的 `IRng`，且调用次数可复现 |
| 行为 | 护甲单调性：0.00→0.60 我方平均伤亡严格下降 |
| 行为 | 火力单调性：伤害 ×2 ⇒ 敌方残存减少 |
| 行为 | `EnemyOutputFactor` 真的进了模拟（回归测试，见 19.17.3②） |
| 行为 | 撤退策略生效 + 三档激进度排序 |
| 行为 | 模型性质：短促战斗产生倒地而非阵亡 |
| 行为 | 护甲可完全弹开且敌方无输出 ⇒ Timeout |
| 行为 | 压倒性优势 ⇒ Victory、零伤亡、威胁解除 |
| 行为 | 撤退结局包含「脱离接触」轮并写入战报 |
| 行为 | 预告 P90 包络成立（**自洽性**检查，非保真度） |
| 行为 | 快照编解码往返后战斗结果一致（离线复演可用） |

#### 19.17.3 原型推翻设计的三条记录

**① 撤退策略的默认值错了两次。** 见 §19.9。这是纯设计问题，靠推理发现不了 —— 只有把引擎跑起来，看到"胜率 0%"和"胜率 8%"两个极端才会暴露。

**② `EnemyOutputFactor` 漏进模拟（真实 bug）。** 该字段是 §18.3 分流表 `abstractPenalty`（工事/射界/被偷袭）在引擎里的**唯一落点**，但原型初版只在预告面板的数字展示里用到它，模拟循环里传的是固定的 `1f`。症状极具误导性：偷袭机械族的场景胜率只有 8%，而单独跑一场却是 9 回合胜利 —— 因为展示的数字变了、实际结果没变。已修，并加断言 `EnemyOutputFactorAffectsOutcome` 钉住。

> 教训记下来：**新增一个"配置字段"时，必须同时加一条"改这个字段会改变结果"的断言**。否则它会静默地变成纯装饰。

**②-b 索敌的 `Random` 策略根本不是随机（真实 bug，读代码时发现）。** `PickTarget` 用严格 `score > bestScore` 比较，而 `Random` 给所有候选打的分恒为 `0f` ⇒ 第一个候选被选中后再也不会被替换。于是：

- `TargetPriority.Random` 退化成"**永远打名册里的第一个**"；
- `Strongest` / `Weakest` 在**同分**时同样永远偏向名册顺序 —— 敌人开局全部满血时 `Weakest` 的所有分值都是 `-1`，于是永远集火我方名册里的第一个单位。

修法：同分改用**蓄水池抽样**（`ties++; if (rng.Range(0, ties) == 0) best = c;`）。这样 `Random` 自然成为等概率选择，`Strongest`/`Weakest` 的同分偏置也一并消除，不需要为 `Random` 写单独分支。

回归测试 `TargetingTieBreakIsRandom`（3 条）：构造"1 个一击倒一个的我方 + 5 个**完全相同**的靶子"的探针场景，用 200 个种子统计"第一个倒地的敌人"有几个不同取值。修复前恒为 1 个，修复后 5 个都出现。

**这个修复显著改变了标定数值，方向必须记下来：**

| 场景 | 修复前成功率 | 修复后成功率 |
|---|---|---|
| manhunters | 93% | 55% |
| outpost | 66% | 29% |
| sleepingmechs | 100% | 100% |
| turrets | 0% | 0% |

原因：修复前的 `Strongest` 因名册顺序而**完美协同集火**（全队按同一顺序逐个点掉敌人），这是不真实的；修复后目标相同时每个单位**各自独立随机**，火力分散。**旧数值是被这个人为偏置系统性高估的。** 真实值应当落在两者之间 —— 见 §19.5.1 的「目标黏性」缺口。

> 交叉验证不受影响：控制台 55% 与 CombatLab 56% 仍然吻合（迭代次数 400 vs 200 的采样差异）。

**③ .NET 自定义数字格式串陷阱。** `0.1` **不是**"一位小数"—— 自定义格式串里只有 `0` 和 `#` 是数字占位符，`1` 是**字面量字符**。实测：

```
0f.ToString("0.1", 不变文化)     = "01"
69.85f.ToString("0.1", 不变文化) = "701"
4f.ToString("0.000", 不变文化)   = "4.000"
```

原因：`"0.1"` = 整数位 `0` + 小数点（后面没有小数占位符，被丢弃）+ 字面量 `1`。
**`0.1` 因此把 69.85 舍入成 70 再拼上一个 `1`。** 已把该诊断常驻在 `selftest` 输出里，避免以后重复踩。
已用正则 `:\s*[0#][0#]*\.[0#]*[1-9]` 扫过现有 `Source/`，**无此模式**（现有代码用的是 `0.#` / `0.##` / `0`，均合法）。

#### 19.17.4 标定后的场景数据（可作为回归基线）

| 场景 | 我方 | 敌方 | 输出/回合 | 成功率 | 伤员 P50/P90 | 结局多为 |
|---|---|---|---|---|---|---|
| manhunters | 4 | 8 野兽 | 69.9 vs 28.8 | 93% | 1 / 1 | Victory |
| outpost | 4 | 5 海盗（工事 ×1.15） | 69.9 vs 41.4 | 66% | 1 / 2 | Victory |
| sleepingmechs | 4 | 3 机械族（偷袭 ×0.20） | 69.9 vs 10.8 | 100% | 0 / 0 | Victory |
| turrets | 4 | 4 迷你炮塔（×1.4） | 69.9 vs 121.0 | 0% | 3 / 3 | Retreat |
| invulnerable | 1 | 1 不可击穿 | 27.0 vs 0 | 0% | 0 / 0 | Timeout |
| overwhelming | 8 | 1 野狗 | 576.0 vs 2.0 | 100% | 0 / 0 | Victory |

主题上是自洽的：偷袭机械族最轻松、炮塔最致命（与 §18.3 判它 `ForbidAbstract` 一致）。

#### 19.17.5 诚实边界：原型**没有**覆盖什么

| 未覆盖 | 说明 |
|---|---|
| 游戏侧工厂 | `CombatSnapshotFactory`（真 pawn → 快照）还没写，所以 `HitFactorFromShooter` / `GetHitChanceFactor` / `AdjustedFullCycleTime` 的实际取值**未经验证** |
| 保真度 | 预告 P90 包络是**引擎自洽性**检查（同一引擎采样 vs 结算），不是"与真实地图战斗一致"的检查。后者要用同批 pawn 跑 N 场真地图战斗来标定 |
| 部位 / 疼痛 / 失血 | 引擎用耐久池近似。vanilla 的倒地/死亡更多由疼痛休克、部位破坏、失血决定 ⇒ **这是残余系统性偏差的主要来源** |
| 暖机回合 | "偷袭休眠机械族"目前用单一输出折扣近似。更忠实的做法是给敌方一个**未接敌的暖机回合数**，记为待办 |
| 护甲聚合 | 用 `ArmorRating_*` 聚合值代替逐部位，语义与 vanilla 一致但非逐部位随机 |
| 天气 / 黑暗 / 烟雾 / 姿态 | 全部取 1（无地图） |

#### 19.17.6 下一步

1. **游戏侧工厂 + 开发 gizmo**：真 pawn → 快照，把 `HitFactorFromShooter` 等 vanilla 数值打印出来，与手算对照；再 dump 成快照喂给本原型离线复演。
2. 用一个开发者 gizmo 验证 §19.14 的第 1–3 项（无地图 `TakeDamage` / `Pawn.Kill` / `BattleLog.Add` 旧战斗误并）。
3. 保真度标定：同批 pawn 跑 N 场真实地图战斗，比对伤亡分布。
4. §18.11 第 1 步（`WITab_Site_Delegation`）仍未被做 —— 它同时修 §17.4 的既有 bug。

**提升路径**：`Prototype/Combat/*.cs` 里除 `XorShiftRng` 外全部可以原样搬进 `Source/Combat/`（都是纯 C#，已用 `LangVersion latest` + 无 record/无 `init` 的保守语法写，能在 net472 下编译）。届时补 `RandRng`（`Rand.PushState/PopState` 包装）与 `CombatSnapshotFactory` 两个游戏侧文件即可。

---

### 19.18 CombatLab：游戏外的战斗计算验证器 `[验证]`

#### 19.18.1 位置与运行

同样是**游戏外**工具，放在 `Source/` 之外，对 mod 本体零影响。

```
RimDelegation/CombatLab/
  RimDelegation.CombatLab.csproj      net10.0-windows, WinForms
  Program.cs                      入口 + --smoke / --selftest / --render
  MainForm.cs                     主窗口（全部显式布局）
  LabModel.cs                     模板 / 编队单位 / 场景参数 + SceneBuilder
  LabPresets.cs                   模板库与 7 套预设对局
  CombatLab.cmd                   启动器（双击即开）
  README.md                       使用说明
  dist/                           已发布产物（net10.0-windows，约 250 KB）
```

```powershell
cd RimDelegation\CombatLab
dotnet run                                  # 界面
dotnet run -- --selftest                    # 无头跑 17 项断言
dotnet run -- --smoke                       # 无头跑全部预设
dotnet run -- --render preview.png          # 离屏渲染界面成 PNG
```

**计算核心不复制**，两个工程链接同一批源文件：

```xml
<Compile Include="..\Prototype\Combat\Core\*.cs"  LinkBase="Core" />
<Compile Include="..\Prototype\Combat\Tests\*.cs" LinkBase="Tests" />
```

⇒ 控制台（`Prototype/`）与桌面 UI（`CombatLab/`）跑的是同一份 `.cs`，不存在漂移。

#### 19.18.2 界面构成

| 区域 | 能力 |
|---|---|
| 预设对局 | 7 套现成编队（含与控制台同名同参数的前 4 套） |
| 我方 / 敌方 | 选模板 → 添加 / +5 / 删除选中 / 清空；选中一行后逐项改 耐久·命中率·射速·伤害·破甲·护甲，改动即时生效 |
| 场景参数 | 掩体通过率、敌方输出修正、脱离接触折扣、倒地阈值、每回合 ticks、回合上限、撤退策略与阈值、双方目标选择策略 |
| 计算预告（200 次） | 蒙特卡洛 P50/P90：成功率、撤退率、失败率、用时、伤员、阵亡、回合数、结局众数 |
| 跑 1000 场统计 | 结局分布 / 伤员分布 / 阵亡分布 / 用时（平均·最短·最长），并**在现场校验预告 P90 包络**（实际超出比例应接近名义 10%） |
| 打一场 | 指定种子跑一次，看战报 + 逐人终局（同种子必然同结果） |
| 导出 / 导入快照 | 与控制台 `--dump` / `--replay` 互通，可用于跨工具比对 |

#### 19.18.3 交叉验证证据 `[验证]`

预设「猎杀人类（8 野兽）」与控制台 `Scenarios.Manhunters()` 同参数，两边独立跑蒙特卡洛：

| 来源 | 成功率 | 伤员 P50/P90 |
|---|---|---|
| 控制台 `dotnet run -- demo`（400 次） | 93% | 1 / 1 |
| CombatLab 预设（200 次） | 93% | 1 / 1 |
| CombatLab `--smoke` | 93% | 1 / 1 |

⇒ 两个独立程序共用同一核心这件事得到实测确认，而不是靠"引用了同一个文件"的推论。

`--selftest` 在 UI 里也能一键跑（结果写进「统计详情」页）。为此把 `SelfTest.RunAll()` 重构成
`RunAll(TextWriter)`，原来的无参版本转调 `Console.Out` —— 控制台行为不变，UI 用 `StringWriter` 捕获后显示。

#### 19.18.4 实现期踩到的三个坑（都已修，记下来防复发）

| # | 症状 | 根因 | 修法 |
|---|---|---|---|
| ① | 选中单位后，改动的是**敌方**那套数值 | 两侧各自 `hpNum = MakeField(...)` 但字段是**同一批成员变量**，建敌方那一侧时把己方的控件引用覆盖了 | 引入 `EditorFields` 内部类，每侧一套；`ApplyEditor(mine, f)` 显式带上目标 |
| ② | 载入预设后编辑器显示一排 `0`，像坏了 | 列表填完后没有任何选中项，而编辑器只在 `SelectedIndexChanged` 时填充 | `FillList` 末尾在无选中时自动选第一行 |
| ③ | 中文注释的 `.cmd` 启动器乱码 | `cmd.exe` 按 **OEM 代码页**（本机 936）读 `.cmd`，而文件是 UTF-8 | 启动器注释改用纯 ASCII；中文说明放进 `README.md` |

> ③ 与 §19.17.3③ 是同一类问题的两面：**给"非 UTF-8 读者"（.NET 自定义格式串 / cmd.exe）写文本时，
> 编码假设必须先验证**。

#### 19.18.5 边界

与 §19.17.5 相同 —— 本工具验证的是**引擎内部一致性**，不是与真实地图战斗的保真度。
模板数值是标定用的占位值，不是从真 pawn 读出来的；正式接入游戏后由 `CombatSnapshotFactory`
折算真实数值再喂进同一套核心。

#### 19.18.6 部署

```powershell
cd RimDelegation\CombatLab
dotnet publish -c Release -o dist
```

产物 `dist\RimDelegation.CombatLab.exe`（framework-dependent，5 个文件约 250 KB）需要
**.NET 10 桌面运行时**。已实测：发布产物 `--selftest` 17/17、`--smoke` 全部预设通过。

若要在没有运行时的机器上跑，改用 `dotnet publish -c Release -r win-x64 --self-contained -o dist-standalone`
（体积约 150 MB）。

### 19.19 无地图战斗的未知量清单 `[验证]`

#### 19.19.1 判据与三档分类

一个量属于**"必须设定"**，当且仅当它**既不能从真 pawn / Def 读出，也不能从世界地图（tile / biome / 时刻）推出**。

| 档 | 含义 | 处置 |
|---|---|---|
| 🟢 **可确定** | 真 pawn 的 stat、`VerbProperties`、`DamageDef`、`BiomeDef`、tile 经纬度 | 直接读，不要再造旋钮 |
| 🟡 **可用更好的模型降级** | 不是"缺一个数"，而是"模型缺一个维度" | 拆字段 / 加状态，见 §19.16.2.1 |
| 🔴 **本质不可知** | 需要地图上的具体空间/瞬时状态 | 只能设定，或把相关威胁排除出抽象路线 |

**结论先摆**：当前 UI 暴露 **8 个场景旋钮 + 10 个单位字段**；但客观存在的未知量有 **20 项** ——
差额集中在"被静默固定为 1 或烘进别的字段"的那 9 项（§19.19.3）。

#### 19.19.2 场景级旋钮（当前 UI 已暴露）

| # | 字段 | 为什么无地图定不了 | 当前默认 | 档 |
|---|---|---|---|---|
| 1 | `CoverFactor` | 真实值来自 `CoverUtility.CalculateOverallBlockChance(target, caster.Position, caster.Map)` —— 需要两者的格子 | 1.00（实验室基线） | 🔴 但见 §19.19.6② |
| 2 | `EnemyOutputFactor` | 真实对应的是走位、掩体利用、射界、协同 —— 全都是地图上的行为 | 1.00；按威胁类型 0.20 / 1.15 / 1.40 | 🔴 |
| 3 | `DownHealthFraction` | vanilla 的倒地是**疼痛休克 + 部位破坏 + 失血**的函数，不是一个耐久比例 | 0.25 | 🟡 替代模型 |
| 4 | `DisengageFactor` | "脱离接触时被打多少"没有 vanilla 对应物（vanilla 里撤退就是走人） | 0.50 | 🔴 纯约定 |
| 5 | `TicksPerRound` | 回合长度是抽象层的约定，vanilla 是逐 tick | 250（6 秒） | 🔴 纯约定 |
| 6 | `MaxRounds` | 同上 | 240（1 天） | 🔴 纯约定 |
| 7 | `OurPriority` / `EnemyPriority` | 真实 AI 的目标选择含空间因素（威胁最大 / 最近 / 打得动） | `Strongest` / `Weakest` | 🔴 偏好 + 默认值 |
| 8 | `Retreat.Kind` / `CasualtyFraction` | 玩家偏好，但默认值需要标定 | 比例 0.34 | 🔴 偏好 + 默认值 |

#### 19.19.3 ★ 被藏起来的未知量（UI 里没有，但客观存在）

这一节是重点：**它们全都是 `ShotReport.HitReportFor` 的因子，当前被静默取 1，或烘进了单位自己的 `HitChance` 里。**

| # | 未知量 | vanilla 里怎么来的 | 当前处置 | 档 |
|---|---|---|---|---|
| ① | **交战距离** | `distance = (target.Cell - caster.Position).LengthHorizontal`，再送进 `HitFactorFromShooter(caster, distance)` | **烘进 `HitChance`**，没有独立字段（§19.16.7 列了 `assumedEngagementDistance`，但代码里不存在） | 🔴 |
| ② | **视线 / 射界** | `verb.TryFindShootLineFromTo(...)` —— 打不到就换目标或移动 | 假设永远打得到 | 🔴 |
| ③ | **屋顶 / 露天** | `caster.Position.Roofed(caster.Map)` / `target.Cell.Roofed(...)`，决定天气是否生效 | 取 1 | 🔴 |
| ④ | **天气** | `caster.Map.weatherManager.CurWeatherAccuracyMultiplier` | 取 1 —— **但见 §19.19.6③，这一项其实可以推算** | 🟢 |
| ⑤ | **烟雾 / 致盲气体** | 沿射击线 `item.AnyGas(caster.Map, GasType.BlindSmoke)` ⇒ ×0.7 | 取 1 | 🔴 |
| ⑥ | **目标姿态** | `FactorFromPosture`：距离 ≥ 4.5 且非站立 ⇒ ×0.5 | 取 1 | 🔴 |
| ⑦ | **处决加成** | `FactorFromExecution`：距离 ≤ 3.9 且非站立 ⇒ **×7.5** | 取 1（我们的模型里没有"近身且倒地"这个状态） | 🔴 |
| ⑧ | **目标体积** | 对 pawn：`Clamp(BodySize, 0.5, 2)`；对**建筑**：`fillPercent × size.x × size.z × 2.5` 再 clamp | 烘进**攻击者**的 `HitChance`，且**没有建筑那条通道**（炮塔是建筑） | 🟡 |
| ⑨ | **爆炸 / 溅射 / 散布** | `GenExplosion.DoExplosion(IntVec3 center, Map map, …)` —— **同时要格子和地图**；散布武器走 `forcedMissRadius` + `GenRadial.NumCellsInRadius` | **完全不建模**：榴弹、火箭、迫击炮被当成单体直射武器 | 🔴 |

**⑨ 是最该先处理的一项**：它意味着带爆炸武器的威胁类型（`MechCluster` 有迫击炮、`Turrets` 有 `mortarsCount`）在抽象引擎里会被**系统性低估**。§18.3 已经把这两个判为 `ForbidAbstract` —— 现在有了更硬的理由，应当把"含爆炸武器"写成分流的显式判据，而不是靠巧合。

#### 19.19.4 结构性替代模型（不是缺参数，是换了模型）

| # | 真实机制 | 本模型的替代 | 后果 |
|---|---|---|---|
| ⑩ | 耐久分布在身体部位上，死亡来自部位破坏 / 失血 / 疼痛 | 单一**耐久池** + 比例阈值倒地 | 倒地判定与 vanilla 有系统性偏差（§19.17.5）；"短促战斗只倒地不阵亡"这条性质就是它的产物 |
| ⑪ | 失血致死是**小时级**过程 | 战斗内不建模 | 长战斗（>1 小时）会低估阵亡 |
| ⑫ | 目标黏性：AI 记住目标持续打 | 每回合独立重选（§19.5.1） | 火力要么过度分散、要么（修复前）完美协同，两个极端都不对 |
| ⑬ | 技能 / 灵能 / 能力（跳跃、护盾、EMP、火箭） | 完全不建模 | 高档装备与灵能者的价值被低估 |
| ⑭ | 爆炸溅射、火焰蔓延、有毒气体 | 完全不建模 | 与 ⑨ 同 |

#### 19.19.5 最小必要设定集

如果只允许设定 5 个数，应该是：

| 优先级 | 量 | 理由 |
|---|---|---|
| 1 | **交战距离** | 它乘进命中率的底数（`Pow(ShootingAccuracyPawn, distance)`），影响最大且当前完全不可见 |
| 2 | **掩体通过率** | 决定时间尺度，且是"双方都更难打中"的唯一表达 |
| 3 | **`EnemyOutputFactor`** | 唯一能表达非对称地形优势的旋钮（难度旋钮） |
| 4 | **倒地位例阈值** | 决定撤退触发与伤亡统计，直接决定"值不值得打" |
| 5 | **回合长度** | 把抽象回合换算成游戏内时间的唯一锚点 |

**其余 15 项**按 §19.19.6 的手段逐个消除或标定。

#### 19.19.6 每一项的标定手段（可操作）

| # | 量 | 怎么定 | 可行性 |
|---|---|---|---|
| ① | 交战距离 | 在真实站点地图上采样：对每一对 (我方, 敌方) 记录 `LengthHorizontal` 的分布，取中位数/众数 | **可测**，一个开发 gizmo 即可 |
| 1 | 掩体通过率 | 在同一张地图上对每一对 (射手 → 目标) 调 `CoverUtility.CalculateOverallBlockChance(...)`，取分布 | **可测**，同上 |
| ④ | 天气 | **不需要地图**：`BiomeDef.baseWeatherCommonalities` 给 {天气, 权重}，`WeatherDef.accuracyMultiplier` 给乘子，加权求期望。实测：温带森林 = **0.9029**（Clear 权重 18、Fog 0.5、SnowHard 0.8 …，总权重 35） | **已可算**，见下方公式 |
| ③⑤⑥⑦ | 屋顶 / 烟雾 / 姿态 / 处决 | 不可知。建议：③⑦ 取 1；⑤ 取 1（除非引入"烟幕弹"道具）；⑥ 若将来加"卧倒"姿态再建模 | 只能设定 |
| ⑨ | 爆炸武器 | 不标定 —— **把含爆炸武器的单位/威胁类型排除出抽象路线**（`ForbidAbstract`），或给该单位一个显式的"溅射折损"标记 | 分流解决 |
| ⑧ | 目标体积 | 拆到目标侧后：pawn 用 `BodySize`，**建筑**用 `fillPercent × size.x × size.z × 2.5`（vanilla 原式） | 可确定（API 已有） |
| 3 | 倒地阈值 | 真实地图战斗标定：记录"倒地瞬间 `health.summaryHealth.SummaryHealthPercent`"的分布，取中位数 | **可测** |
| 2 | `EnemyOutputFactor` | 按威胁类型用真实地图战斗的胜负/伤亡反推（例如 `Outpost` 应让预告胜率与实战胜率吻合） | 可标定，但需要真图数据 |
| 5/6 | 回合长度 / 上限 | 纯约定，只要与 mod 的 tick / 营养模型一致即可，不需要标定 | 不必标定 |

**天气的推算公式**（§19.19.3④，已实测成立）：

```
E[accuracyMultiplier | biome] = Σ_i ( commonality_i / Σ commonality ) × WeatherDef_i.accuracyMultiplier
```

只依赖 `Find.WorldGrid[site.Tile].Biome`，**不需要生成地图**。局限：这是**长期平均**，不是"战斗那一刻的天气"；
若想更精确，可以只统计当前季节可能出现的天气。

#### 19.19.7 这份清单的用途

1. **决定下一步做什么**：① 与 ⑧ 是"改字段就能消除"，⑫ 是"加状态就能改善"，⑨ 是"分流即可规避" —— 都不需要新数据。
2. **给预告面板一个诚实的"假设"区**：把这 20 项里被取值的项**摊开给玩家看**（哪些是量出来的、哪些是假设的），比藏起来更有说服力。
3. **避免"旋钮幻觉"**：UI 上有 8 个旋钮不代表只有 8 个假设。**没暴露的假设最危险** —— 玩家调不动、也看不见，却真实影响结果。

> **本轮（§19.20）已处理其中 4 项**：① 交战距离（成了独立字段 `StartDistance`）、④ 天气（逐场采样实现）、
> 1 掩体通过率（保留并明确了语义）、⑧ 目标体积（仍未处理，见 §19.20.8）。
> 另新增两个**非对称**旋钮：地形优势的射程惩罚、移动策略。剩余项见 §19.20.9。

---

### 19.20 空间模型：距离 · 射程 · 移动 · 地形优势 · 天气 `[验证]`

#### 19.20.1 为什么必须加空间

在此之前，战斗是一个**零维**过程：所有单位同时互相射击，命中率是一个固定的数。这带来三个说不通的地方：

1. **射程完全不存在** —— 狙击枪和霰弹枪没有区别，只有"命中率 × 射速 × 伤害"的乘积不同；
2. **先手不存在** —— 谁射程远、谁先发现对方，对结果毫无影响；
3. **接近过程不存在** —— 近战单位凭空砍人，冲锋途中的挨打完全不计。

§19.20 给战场加**一维距离**：双方从 `StartDistance` 相互接近，进入各自射程后才开火。

#### 19.20.2 新增字段

**单位级（`CombatUnitSnapshot`）**

| 字段 | 含义 | vanilla 来源 |
|---|---|---|
| `Range` | 武器射程（格） | `VerbProperties.AdjustedRange(verb, attacker)` |
| `MoveSpeed` | 每回合接近格数 | `StatDefOf.MoveSpeed`（折算成"格/回合"） |
| `IsMelee` | 近战单位：射程恒为 `MeleeRange`，**且不吃地形射程惩罚** | `verbProps.IsMeleeAttack` |
| `HasTerrainAdvantage` | 地形优势 | 由战斗环境决定（守方/工事 ⇒ 有） |
| `AccuracyNear` / `AccuracyFar` | 距离两端点的命中率 | 由 vanilla 四段精度曲线取两点近似（§19.20.6） |

`HitChance`（旧的单一命中率）**已被这两个字段取代**。

**场景级（`CombatScene`）**

| 字段 | 默认 | 含义 |
|---|---|---|
| `StartDistance` | **40** | 初始交战距离。**大于常见射程 25 ⇒ 开局双方都够不着** |
| `MeleeRange` | 1.5 | 近战的有效射程 |
| `NearBand` | 8 | 命中率两点插值的近端距离 |
| `NoAdvantageRangeFactor` | **0.75** | 无地形优势者的射程乘子（近战除外） |
| `Closing` | `UntilShortestEngaged` | 距离收缩到哪为止（§19.20.3） |
| `CoverFactor` | 1.00 | 场景级掩体通过率（对称，§19.16.8） |
| `WeatherTable` | 空 | 天气表；**空 = 无天气影响**（§19.20.5） |

#### 19.20.3 每回合的三段结构

```
每回合：
  1. 交火 —— 只有「距离 ≤ 自己的有效射程」的单位能出手；够不着的按兵不动
  2. 结束判定（§19.10，顺序不变）
  3. 移动 —— 距离向平衡点收缩
```

因为 `StartDistance(40) > 常见射程(25)`，**第一回合双方都开不了火**，只接近 —— 这正是需求里
"为正常交战设置一个双方都无法射击的初始距离"。

**有效射程**

```
EffectiveRange(u) = u.IsMelee          ? scene.MeleeRange
                  : u.HasTerrainAdvantage ? u.Range
                                          : u.Range × scene.NoAdvantageRangeFactor
```

**平衡点（距离收缩到哪里）**

```
EquilibriumDistance() = min over 参与单位 of EffectiveRange
```

`Closing` 三种模式：

| 模式 | 平衡点 | 语义 |
|---|---|---|
| `UntilShortestEngaged`（默认） | **全体**最短射程 | 近战会一路冲上去，交火距离被拉到近身 —— 真实（野兽冲锋） |
| `UntilRangedEngaged` | 只算**远程**单位 | 近战永远够不着 —— 模拟"在开阔地冲不到人" |
| `Never` | —— | 全程停在初始距离 |

**推进速度**：每回合距离减少量 = 我方推进 + 敌方推进；每方取"**还没进射程的**单位中最慢的那个"
（与远行队行军按最慢者一致）。已进入射程的单位停下开火，不再推进。

#### 19.20.4 地形优势：无优势方射程缩短（近战除外）

这是需求里明确要的机制，语义是"**你暴露在开阔地，对方在工事里，他们的枪先够得着你**"。

关键在于：它不是"命中率降低"，而是**射程降低** —— 于是产生一个**先手窗口**：

```
我方无优势（射程 25×0.75 = 18.75）、敌方有优势（射程 24）
开局 40 格：
  → 双方都够不着，互相接近
  → 距离降到 24：敌方开火，我方仍够不着，**必须顶着火力继续接近**
  → 距离降到 18.75：我方终于能还击
```

实测（`--sweep noadv`，预设「海盗哨所（我方无掩体）」，300 次蒙特卡洛）：

| 无优势射程系数 | 成功率 | 回合 P50 |
|---|---|---|
| 1.00（无惩罚） | **31%** | 9 |
| 0.90 | 20% | 8 |
| 0.80 | 13% | 8 |
| **0.75（默认）** | **8%** | 8 |
| 0.60 | 5% | 8 |
| 0.40 | **2%** | 8 |

⇒ 这个旋钮**极其灵敏**：0.40 到 1.00 之间成功率变化 29 个百分点。
失败方式几乎全是 `Retreat`（被打到撤退阈值），不是全灭 —— 符合"顶着火力接近"的直觉。

> **反直觉的观察**：表里"我方输出/回合"随着惩罚加重反而**上升**（68.0 → 87.3）。
> 因为射程被压缩后，平衡距离也随之变近，而距离越近命中率越高（§19.20.6）。
> **所以"输出数字变好"和"胜率变差"可以同时发生** —— 决定胜负的是接近阶段谁在挨打，
> 而不是稳定交火阶段的对射。这条也说明为什么单看"期望输出"评估强弱是不够的。

#### 19.20.5 天气：逐场采样，而不是取期望

`WeatherTable` 每项 = `{天气名, 命中率乘子, 权重}`，数值直接取自 vanilla：

- 乘子 ← `WeatherDef.accuracyMultiplier`（实测：Clear/Windy/Overcast/DryThunderstorm = 1.0，
  Rain/RainyThunderstorm/SnowGentle/SnowHard/GrayPall/Sandstorm = 0.8，
  TorrentialRain = 0.75，Blizzard = 0.7，**Fog/FoggyRain = 0.5**）
- 权重 ← `BiomeDef.baseWeatherCommonalities`（例如温带森林：Clear 权重 18、SnowHard 4、Fog 1…）

**关键设计：每场模拟开始时按权重采样一次，整场沿用。**

```
若不采样而用期望值：温带森林恒为 ×0.9029 —— 没有任何一场真实战斗是这个数
若逐场采样：        多数场次是 Clear（×1.0），偶尔抽到雾（×0.5）
```

于是蒙特卡洛的**分布**里自然包含了"今天起雾了"这种坏运气 —— **P90 才有意义**。
这与"预告即契约"（§19.12）是同一个思路：不是把随机性抹平成均值，而是让玩家看到它的形状。

CombatLab 内置 7 套天气预设（无 / 温带森林 / 沙漠 / 苔原 / 冰原 / 恒定浓雾 / 恒定暴风雪）。
`[建议]` 游戏内接入时应当用 `Find.WorldGrid[site.Tile].Biome` 的天气表自动填充 —— 见 §19.19.6④。

#### 19.20.6 命中率的距离衰减：两点插值

vanilla 用四段曲线（`accuracyTouch/Short/Medium/Long` @ 3/12/25/40 格做线性插值）。
本模型把它压成**两点**：

```
t  = Clamp01( (distance − NearBand) / (EffectiveRange − NearBand) )
acc = Lerp(AccuracyNear, AccuracyFar, t)        // distance ≤ NearBand 时 t=0
hit = Clamp01(acc × 掩体通过率 × 天气乘子)
```

校准规则（把旧的单一命中率 `H` 拆成两点）：`AccuracyNear = 1.25H`、`AccuracyFar = 0.55H`。
于是**距离 ≈15 格时命中率 ≈ H** —— 与旧标定值可比。

**已知偏差 `[未决]`**：vanilla 的部分武器在**贴脸**（touch）时精度**低于**近距离（short），
例如突击步枪 0.8 / 0.9 / 0.65 / 0.5。两点插值没有这个非单调性，所以**近身阶段的命中率被高估**。
这也是"猎杀人类"场景胜率偏高（89%）的原因之一。修法是把 `AccuracyNear` 拆成 touch/short 两点。

#### 19.20.7 实测：各场景（含空间模型）

| 场景 | 我方 | 敌方 | 成功率 | 终距 | 结局多为 |
|---|---|---|---|---|---|
| 猎杀人类（8 野兽冲锋） | 4（无优势） | 8 近战（无优势） | 89% | 1.5 | Victory |
| 海盗哨所（我方无掩体） | 4（**无优势**） | 5（优势） | **8%** | 7.2 | Retreat |
| 休眠机械族（偷袭 ×0.20） | 4（优势） | 3（优势） | 100% | 12 | Victory |
| 炮塔阵地 | 4（无优势） | 4 炮塔（优势） | 0% | 12.4 | Retreat |
| 同等兵力 4 vs 4 | 4（无优势） | 4（优势） | 48% | 7.2 | Victory |
| 地形对比：我方也有掩体 | 4（**有优势**） | 5（优势） | 32% | 12 | Retreat |

"无掩体 8%" vs "有掩体 32%"这一对是专门为演示地形优势做的对照 —— **同一套编队，只翻一个复选框**。

#### 19.20.8 已知局限

| # | 局限 | 后果 | 修法 |
|---|---|---|---|
| ① | **单标量距离** —— 全队共用一个 `distance` | 短射程单位（霰弹枪 12 格、近战）会把**全队**拖到近身，远程单位失去距离优势 | 改成逐单位距离带，或至少逐方距离；`UntilRangedEngaged` 是部分缓解 |
| ② | 两点插值，无 touch 段 | 贴脸命中率被高估（§19.20.6） | 拆成三段 |
| ③ | 没有视线/地形遮挡 | 射程够得着就一定能打中 | 需要地图，抽象层解决不了 |
| ④ | 爆炸武器仍按单体直射处理 | 榴弹/迫击炮被低估 | §19.19.3⑨：分流为 `ForbidAbstract` |
| ⑤ | 目标体积仍未按目标取（§19.19.3⑧） | 机械蜈蚣与人类对同一射手"一样好打" | 把 `BodySize` 拆到目标侧 |
| ⑥ | 移动速度是"每回合格数"，与 `TicksPerRound` 耦合 | 改回合长度会连带改移动节奏 | 存成"格/秒"，在结算时换算 |

#### 19.20.9 本轮新增断言（`SelfTest`）

| 断言 | 钉住的性质 |
|---|---|
| 空间模型：开局双方都够不着，第一回合无人开火 | `StartDistance` 确实大于射程，且射程门真的生效 |
| 地形优势：占优方先开火（我方占优 ⇒ 零伤亡解决对手） | 射程差 ⇒ **先手** |
| 地形优势：无优势方顶着火力接近（敌方占优 ⇒ 我方被打倒，对手无伤） | 反方向也成立，不是只验证了一侧 |
| 空间模型：近战把交战距离拉到 `MeleeRange` | `UntilShortestEngaged` 的语义 |
| 空间模型：`Closing=Never` ⇒ 全程停在初始距离 | 移动关闭时的退化行为 |
| 天气：逐场采样（60 个种子下出现 ≥2 种天气） | 天气是采样而非取期望 |
| 天气：差天气显著拉长战斗（雾 ×0.5 ⇒ 回合数上升） | 天气乘子确实接进了模拟 |
| 掩体通过率 ×3 条（拉长 / 镜像中性 / 不对称时会改变胜负） | §19.16.8 更正后的结论 |

**一个踩过的坑**：CombatLab 的天气预设最初用**下标**引用（`WeatherPreset = 4` 指"恒定浓雾"），
而 4 实际是"冰原"，于是那一档静默地跑成了雪天（SnowBad×0.8），标签与行为不符。
现在改用 `LabWeather.IndexOf("恒定浓雾")` **按名字查找**。
教训与 §19.17.3② 同类：**用下标/魔法值引用一份可增删的列表，必然在某次增删后静默错位。**

---

### 19.21 逐单位距离带：纵深模型 `[验证]`

#### 19.21.1 要解决的问题

§19.20 的**单标量距离**有一个致命失真：全队共用一个 `gap`，于是

> 带一把霰弹枪（有效射程 12）= 把**全队**拖到 12 格，狙击手也只能在 12 格开火。

这让"射程"这个属性只对最短射程的那个单位有意义。§19.21 给每个单位加一层**纵深**。

#### 19.21.2 模型

```
                 ┌── 我方纵深 depth[i] ──→ 单位 i 距敌方前线 = gap + depth[i]
战线间距 gap ────┤
                 └── 敌方纵深 depth[j] ──→ 单位 j 距我方前线 = gap + depth[j]

攻击距离(i, j) = gap + depth[i] + depth[j]        （近战忽略 depth[j]）
期望纵深(i)    = Clamp(有效射程(i) − gap, 0, MaxStandoff)
```

`depth` 是**运行时状态**（不进快照），含义是"该单位站在己方前线之后多少格"。

**每回合的移动（`Advance`）**

1. 每个单位把自己的纵深向期望值调整：**前压全速、后撤半速**（`FallbackFactor`，倒着走更慢）；
2. 战线间距只由"**站在前线上（depth≈0）且仍够不着**"的单位压低，取其中最慢者 —— 与远行队行军按最慢者一致。

#### 19.21.3 效果实测

`manhunters` 局（我方射程 12~30，野兽是近战 ⇒ 战线被拉到 1.5 格）：

| 单位 | 射程 | 终局纵深 |
|---|---|---|
| 阿花（狙击） | 40×0.75 = 30 | **12.0**（上限） |
| Chisa（突击步枪） | 25.9×0.75 = 19.4 | **12.0**（上限） |
| 老王（冲锋枪） | 20×0.75 = 15 | **12.0**（上限） |
| Denia（霰弹） | 12×0.75 = 9 | **4.6** |
| 8 只野兽 | 近战 1.5 | **0.0**（全在前线） |

⇒ 纵深**按射程自然分层**：远程在后、近战在前。移动日志能直接看到这个过程：

```
第3回合：阿花 后撤 2.3 格 → 纵深 2.4（距敌前线 22.2）
第5回合：Denia 后撤 2.3 格 → 纵深 2.3（距敌前线 3.8）
第7回合：阿花 后撤 2.3 格 → 纵深 9.3（距敌前线 10.8）
```

#### 19.21.4 纵深策略：**后撤只有在"撤得出去"时才有意义** `[验证]`

这是被参数扫描**推翻了两次**的默认值，值得完整记下来。

**第一次**：无条件"把自己拉到最大射程"（`ToMaxRange`）。扫描 `纵深上限`：

| 纵深上限 | 0（单标量） | 2 | 5 | 8 | **12** | 16 | 24 |
|---|---|---|---|---|---|---|---|
| 哨所局成功率 | 8% | 5% | 1% | 0% | **0%** | 0% | 0% |
| 我方输出/回合 | 73.3 | 66.2 | 55.4 | 48.1 | **42.8** | 40.9 | 38.4 |

**后撤让结果单调变差。** 原因：哨所局里我方被压制射程（步枪 19.4 &lt; 海盗 24），
后撤只是把自己推到命中率最低的位置（`AccuracyFar`），而对手照样够得着。

**第二次**：改成 `OnlyIfOutranging`（只有自己射程更长时才后撤）。仍然变差 —— 因为
判据不完整：狙击手射程 30 &gt; 24 **看起来**占优，但 `gap(9) + MaxStandoff(12) = 21 ≤ 24`，
**它根本撤不出对手的射程**，于是后撤还是纯亏。

**最终判据**（两个条件缺一不可）：

```
① 自己的有效射程 > 敌方最短有效射程          （否则谈不上压制）
② gap + MaxStandoff > 敌方最短有效射程      （否则撤不出去）
```

加上 ② 之后，扫描在 `纵深上限` 小的时候保持平坦（不后撤），超过"能撤出去"的阈值才开始生效：

| 纵深上限 | 0 | 2 | 5 | 8 | 12 | 16 | 24 |
|---|---|---|---|---|---|---|---|
| 我方输出/回合 | 73.3 | 73.3 | 73.3 | 73.3 | 73.3 | **65.5** | **63.0** |

（73.3 段 = 判据 ② 拦住了无谓后撤；16 起 = 狙击手开始真正撤出 24 格外。）

> **教训**：一个"战术"能不能成立，取决于它**有没有达到目的**，而不只是方向对不对。
> "拉开距离"这件事，拉开得不够等于白拉 —— 这类"阈值型策略"必须把**结果**写进判据，
> 而不是只写**意图**。这正是参数扫描（`--sweep standoff`）能发现、而读代码发现不了的东西。

三种策略：

| 策略 | 语义 |
|---|---|
| `OnlyIfOutranging`（默认） | 满足 ①② 才后撤 |
| `ToMaxRange` | 无条件维持最大射程（"风筝"战术；被压制时是自残） |
| `Never` | 不前压也不后撤（等价 `MaxStandoff = 0`，退化成 §19.20 的单标量模型） |

#### 19.21.5 新增字段

| 字段 | 默认 | 含义 |
|---|---|---|
| `MaxStandoff` | 12 | 后排最多能站多远（格）。0 = 退化 |
| `FallbackFactor` | 0.5 | 后撤速度系数（前压为 1.0） |
| `Standoff` | `OnlyIfOutranging` | 纵深策略 |

#### 19.21.6 已知局限 `[未决]`

| # | 局限 | 后果 |
|---|---|---|
| ① | **近战忽略目标纵深** —— 会绕过前线直取后排 | 模型不模拟"拦截"（前线挡住冲过去的人）。近战一旦贴上来就够得着所有人，所以"用近战挡住对方近战"这条战术无效 |
| ② | 纵深是**一维标量**，没有左右 | 无法表达"侧翼包抄""被包围" |
| ③ | 后撤没有代价（除了速度减半） | 现实中后撤会丢失阵地、暴露侧翼；本模型里后撤是免费的 |
| ④ | `MaxStandoff` 是全局值 | 现实中它取决于地形与战场纵深，应当逐战场/逐 tile 取值 |

#### 19.21.7 新增断言

| 断言 | 钉住的性质 |
|---|---|
| 逐单位纵深：长射程单位后撤保住自己的交战距离 | 狙击手纵深 &gt;8、霰弹手 &lt;1、战线停在 12 |
| 纵深策略：被对手压制射程的单位不后撤 | `OnlyIfOutranging` 下步枪手纵深 0，`ToMaxRange` 下 &gt;0.5 |
| 纵深策略：能压制对手射程时照常后撤 | 对近战野兽（射程 1.5）我方照常拉开 |
| 逐单位纵深：`MaxStandoff=0` ⇒ 退化为单标量距离 | 全体纵深为 0（保证旧行为可复现） |

---

### 19.22 结构化战斗日志与过滤 `[验证]`

#### 19.22.1 从"一串字符串"改为"带类别的条目"

```csharp
public enum CombatLogKind { Setup, Move, Attack, Hit, Status, Outcome }

[Flags] public enum CombatLogFilter { None, Setup, Move, Attack, Hit, Status, Outcome, Default, All }

public sealed class CombatLogEntry { public CombatLogKind Kind; public int Round; public string Text; }
```

`CombatResult.Entries` 是 `List<CombatLogEntry>`；取文本用 `LogLines(filter)` / `LogText(filter)`。

#### 19.22.2 六类各自的记录内容

| 类别 | 粒度 | 内容 |
|---|---|---|
| `Setup` | 一次 | 开局：战线间距、天气（含乘子）、纵深上限；以及"开始交火"的回合与距离 |
| `Move` | 每单位每次有效位移（≥0.5 格） | `阿花 后撤 2.3 格 → 纵深 2.4（距敌前线 22.2）`；以及 `战线间距 21.8 → 17.2 格`、`战线间距稳定在 1.5 格` |
| `Attack` | **每次出手一行**（出手方视角汇总） | `Chisa → 海盗2 @19.4 格 · 命中率 34% · 射击 3 · 命中 1 · 甲弹对抗 弹开 0 / 减半 0 / 全额 1 · 合计 12 伤害` |
| `Hit` | **每次命中一行**（目标视角明细） | `海盗2 受击（Chisa）@19.4 格 · 甲 0.35 − 破甲 0.16 = 净 0.19 → 全额 · 本发 12 伤害 · 耐久 90 → 78/90` |
| `Status` | 状态变化时 | 倒地（含耐久比例与阈值）、阵亡（含凶手）、撤退触发、脱离接触 |
| `Outcome` | 一次 | 结局摘要 |

**`Attack` 与 `Hit` 的分工**是刻意的：`Attack` 是"我这一轮打得怎么样"（含甲弹分布），
`Hit` 是"这一发具体发生了什么"（甲弹判定的档位与耐久前后）。前者每回合每单位一行，
后者每一次命中一行 —— 量大时用过滤关掉 `Hit` 即可。

#### 19.22.3 过滤是**写入时**生效，不只是显示时

```csharp
// Runner 里：
private void Log(CombatLogKind kind, int round, string text)
{
    if (!scene.LogFilter.Allows(kind)) return;   // ← 不满足就根本不拼字符串
    result.Entries.Add(new CombatLogEntry(kind, round, text));
}
```

理由：**预告要跑 200~1000 次**，每次都拼几百条字符串是纯浪费。
`Forecast.Run` 因此先 `scene.Clone()` 并把克隆体的 `LogFilter` 设为 `None`
（克隆只做一次，不是每次迭代）。

> ⚠️ **由此产生一个必须记住的约定**：日志类别是**写入时**决定的，
> 所以"我想看受击明细"必须**在模拟之前**把 `LogFilter` 装到场景上。
> 原型早期版本只在打印时过滤，结果 `hit` 类别根本没被记录，日志里怎么也找不到 ——
> 已由断言 `LogFilterIsRespected` 钉住。

`Forecast.Run` 用克隆体而不是就地修改 `scene.LogFilter`，是为了不污染调用方 ——
由断言 `ForecastLeavesCallerSceneUntouched` 钉住（跑完预告后调用方的场景仍逐字节不变、
自己再跑一次仍然拿得到日志）。

#### 19.22.4 用法

```
控制台：dotnet run -- battle outpost 4242 all
        dotnet run -- battle outpost 4242 hit,status      # 只看受击与状态
        dotnet run -- battle outpost 4242 move            # 只看移动
        dotnet run -- battle outpost 4242 none            # 只看逐人终局
UI：    「战报」页顶部 6 个复选框（开局/移动/攻击/受击/状态/结局）+ 全选/全不选/默认，
        切换即刻重渲染（不用重打），并显示「共 N 条（攻击 x / 受击 y / 移动 z / 状态 w）· 当前显示 …」
```

#### 19.22.5 新增断言

| 断言 | 钉住的性质 |
|---|---|
| 日志：All 时 6 类都记到 | 每一类都真的会产生条目（含逐发受击） |
| 日志：None 时一条都不写 | 预告省时间的机制真的生效 |
| 日志：只开 Move 时不含其它类别 | 过滤器逐类精确 |
| 日志：攻击行含甲弹对抗汇总 | 命中率 / 弹开·减半·全额 / 合计伤害 |
| 日志：受击行含逐发甲弹判定 | 净护甲 / 档位 / 耐久前后 |
| 预告不污染调用方场景 | `LogFilter` 未被改写、场景逐字节不变、之后仍能拿到日志 |

#### 19.22.6 顺带修掉的一个误导

受击行最初在 `ApplyDamage` **之前**写，于是"剩余耐久"显示的是命中**前**的值 ——
`本发 9 伤害 · 剩余 100/100` 这种自相矛盾的输出。
现在先算好前后值再写：`耐久 100 → 91/100`。

---

### 19.23 接进游戏 Mod `[验证]`

#### 19.23.1 结构：核心进 mod，工具链接同一份

```
Source/Combat/Core/      ← 计算核心（10 个文件，**进 mod 的 DLL**）
    CombatLog / CombatMath / CombatResult / CombatScene / CombatSimulator
    CombatUnitSnapshot / Forecast / IRng / SnapshotCodec / XorShiftRng
Source/Combat/Game/      ← 游戏侧适配层（**只有 mod 编**）
    CombatTuning            全部标定量 + 给玩家看的"已知假设"清单
    RandRng                 Rand.PushState/PopState 包装（§19.16.5）
    CombatSnapshotFactory   真 Pawn → 快照（复用 vanilla 的两个命中函数）
    ThreatRosterFactory     地点威胁 → 敌方编队（确定性种子，§19.7）
    CombatSceneFactory      车队 + 地点 → CombatScene（含立场与天气）
    Dialog_ThreatAssessment 评估对话框（预告 + 战报 + 过滤）
Source/WorldObjectComp_ThreatAssessment.cs        入口（车队 gizmo / 右键菜单）
Source/WorldObjectCompProperties_ThreatAssessment.cs
Patches/RimDelegation_SiteComps.xml                   把组件挂到 Site 上
Prototype/Combat/Tests/  ← 离线自测（只有两个工具用，不进 mod）
```

两个工具的 csproj 改为链接 `..\Source\Combat\Core\*.cs`。
⇒ **mod 跑的就是原型跑的那个核心**，不存在复制粘贴漂移。

mod DLL：66048 → **112128 字节**。

#### 19.23.2 游戏侧只做三件事：读、换算、下单

`CombatSnapshotFactory` 的设计原则是**能读就读，读不到才换算**：

| 数值 | 来源 | 性质 |
|---|---|---|
| 射程 | `VerbProperties.AdjustedRange(verb, pawn)` | 读 |
| 射速 | `AdjustedFullCycleTime` + `burstShotCount` | 读 |
| 伤害 | `ProjectileProperties.GetDamageAmount` / `AdjustedMeleeDamageAmount` | 读 |
| 破甲 | `AdjustedArmorPenetration` | 读 |
| 护甲 | `StatDefOf.ArmorRating_Sharp/_Blunt/_Heat` | 读 |
| 伤害类别 | `DamageDef.armorCategory.defName` | 读 |
| **命中率** | `ShotReport.HitFactorFromShooter` × `GetHitChanceFactor` | 读（在近端与远端各取一次） |
| 耐久池 | `baseHealthScale × 100 × summaryHealthPercent` | **换算** |
| 移动 | `StatDefOf.MoveSpeed × (TicksPerRound/60) × AdvanceCautionFactor` | **换算** |

**移动速度的单位换算是必须记下来的一条**：vanilla `MoveSpeed` 是**格/秒**，
全速 4.6 格/秒 = 每 6 秒回合 **27.6 格** —— 那是飞奔穿越空地，不是交火中的推进。
所以引入 `AdvanceCautionFactor = 0.15`（火力下的谨慎推进），得 4.14 格/回合，
**与原型里手写的 4.5 格/回合吻合** —— 也就是说原型当初的标定值本就相当于这个谨慎度。

#### 19.23.3 敌方编队：确定性种子 + 立刻销毁

`ThreatRosterFactory` 对每个**有威胁点的 SitePart**（`parms.threatPoints > 0`；`PreciousLump`
的 `wantsThreatPoints` 是 false 所以天然排除）分派：

| 威胁 | 处理 |
|---|---|
| `Outpost` | `GeneratePawns(groupKind=Settlement, faction=site.Faction, seed=parms.randomValue, inhabitants:true)` |
| `SleepingMechanoids` | `GeneratePawns(groupKind=Combat, faction=Faction.OfMechanoids, seed=SleepingMechanoidsSitePartUtility.GetPawnGroupMakerSeed(parms))` |
| `Manhunters` | `parms.animalKind` × `AggressiveAnimalIncidentUtility.GetAnimalsCount(kind, points)` |
| `Turrets` / `MechCluster` / `Ambush*` | **进 `Unresolved`**，UI 提示"需进入地图清剿"（即 §18.3 的 `ForbidAbstract`） |

**生成 → 折算快照 → 立刻 `Destroy()`**。因为只需要数值，销毁掉就彻底绕开了
"未 spawn 的 pawn 被写进存档"这个 §19.14 第 4 项的高风险未决 —— 编制只剩纯数据。
`[未决]` 将来要做"战场缴获"（读敌人装备当战利品）时，才需要保留真 pawn 并处理生命周期。

#### 19.23.4 立场与天气

- **车队 = 进攻方（`HasTerrainAdvantage = false`）**，守军 = 防守方（`true`）。
  与 §19.20.4 配合 ⇒ 进攻方射程 ×0.75，必须顶着火力接近。
- **天气从 `<c>Tile.PrimaryBiome.baseWeatherCommonalities</c>` 读**，逐场随机抽取（§19.20.5）。
  实测温带森林期望命中 ×0.9029。

  > 踩坑：`Tile.biome` 是**私有**字段，1.6 的公开入口是 `Tile.PrimaryBiome`。
  > 另一个：`DamageArmorCategoryDefOf` **只暴露了 `Sharp`**，Blunt/Heat 只能按 defName 比对。

#### 19.23.5 一个必须在核心兜住的资源管理

游戏侧的 `RandRng` 是 `IDisposable`（构造时 `PushState`、Dispose 时 `PopState`）。
预告要跑 200 次 = 200 个 `RandRng` —— 若调用方忘了 `using`，随机栈每迭代涨一层，
之后所有游戏内随机全部错位。

**修法放在核心而不是调用方**：`Forecast.Run` 在 `finally` 里检查 `rng as IDisposable` 并释放。
理由：这是"用 Rand 还是自建 RNG"的必然配套（§19.16.5），属于核心契约的一部分，
不该指望每个调用点都记得。

#### 19.23.6 入口与界面

- **车队停在有威胁的地点上** → 车队 gizmo 栏出现「威胁评估」；
- **车队还没到** → 右键地点出现「威胁评估（车队尚未抵达）」；
- 对话框：双方编队（真数值）→「计算预告（200 次）」/「打一场（种子 N）」/「换个种子」，
  日志过滤 6 个复选框，结果区含预告、分类战报、逐人终局、**评估假设清单**、
  敌方编队推算过程、被排除的我方成员。

**假设清单是刻意放在结果里的**（§19.19.7 第 2 条）：无地图战斗里有 20 项量只能设定，
把它们摊给玩家看比藏起来更有说服力。

#### 19.23.7 仍需游戏内验证 `[未决]`

| # | 待验证 | 失败后果 | 退路 |
|---|---|---|---|
| 1 | `GeneratePawns(inhabitants:true)` 无地图时是否安全（§19.14⑥） | 敌方编队推不出来 | 退回 `GeneratePawnKindsExample` + 系数 |
| 2 | `Find.WorldPawns.RemovePawn` + `Destroy()` 对未 spawn 的 pawn 是否安全 | 日志警告 / 残留 | 只丢引用不销毁（靠 GC） |
| 3 | `Rand.PushState/PopState` 在 200 次迭代下是否平衡 | 全局随机错位（症状极隐蔽） | 改成一次性 Push + 手动 seed |
| 4 | 预告耗时（200 次 × 数十回合）是否可接受 | 打开对话框卡顿 | 降到 50 次或异步 |
| 5 | 真实数据下的胜率是否讲得通 | 标定问题 | 用 `--sweep` 逐参数标定 |

#### 19.23.8 尚未做的部分

| 缺口 | 说明 |
|---|---|
| **Resolving 阶段** | 现在只到 Forecast；真的开打（`TakeDamage` 施加伤害、写 `BattleLog`、阶段机）见 §19.4 / §19.8 |
| 阶段机 `DelegationStage` | `Approach → Clear → Mine → Return` 尚未实装 |
| `DelegationWorker_Assault` | 独立战斗委派尚未实装 |
| 战场缴获 | 需要保留真 pawn（与 §19.23.3 的"立刻销毁"冲突，见那里的说明） |
| `DelegationThreatPolicyDef` | 威胁分派目前是代码里的 switch，§18.3 要求数据化 |
| 逐目标掩体/体积 | §19.16.2.1 的因子归属拆分尚未做 |

---

### 19.24 开采委派：暂停 与 随机事件 `[验证]`

#### 19.24.1 暂停委派

**语义**：停开采、停产出、**人员就地休息**，而且**暂停的时间不计入计划天数**。

实现落在三个地方，缺一个都会"看起来暂停了、其实没暂停"：

| # | 落点 | 做什么 | 不做会怎样 |
|---|---|---|---|
| ① | `WorldObjectComp_Delegations.TickDelegation` | 暂停时直接 `return`，不产出、不交付、不判结束条件 | 照旧开采 |
| ② | `Delegation.ElapsedDays` | 减去 `pausedTicksTotal` | "按天数收工"的预算照跑 —— 暂停 3 天就把 5 天的计划耗掉 |
| ③ | **`DelegationRegistry`** | 把暂停中的委派**从 `workingPawns` 里排除** | 休息补丁继续拦 `TrySatisfyRestNeed`，**休息条不会回升** —— "让人休息"落空 |

第 ③ 条是关键：休息机制在 §15 修 2 里是"工时段禁止自动补休息"，
所以"暂停"必须在**同一层**把它解开，否则只是不挖矿、人照样睡不着。

**入口**：车队 gizmo「暂停委派 / 继续委派」、远行队页签上的同名按钮、
矿点检视里的状态行（`当前：**已暂停** —— 队员就地休息（不产出、不计入计划天数）`）、
开发者 gizmo。

#### 19.24.2 暂停 vs 停摆：**同样是"不产出"，时间代价相反**

随机事件会造成"停摆"（`stalledUntilTickAbs`）。它与暂停共用"不产出"的外观，
但**天数预算的处理正好相反**：

| | 谁造成 | 产出 | 天数预算 | 人员 |
|---|---|---|---|---|
| **暂停** | 玩家 | 停 | **冻结**（`ElapsedDays` 扣除） | 真正休息 |
| **停摆** | 随机事件 | 停 | **照常消耗** | 也是休息（registry 同样排除） |

这条对照是刻意的：**玩家主动暂停不该有代价，事故造成的中断必须有时代价。**
`DelegationRegistry` 对两者都排除（因为两者都不是在干活），
而 `ElapsedDays` 只扣除暂停 —— 两个判断分别落在不同的地方，正是为了让这个差异成立。

#### 19.24.3 随机事件（demo）

**Def 框架**：`DelegationEventDef` 是基类，5 个具体事件各自是一个子类 Def，
XML 元素名用**全限定名**（`<RimDelegation.DelegationEventDef_CaveIn>` —— 与既有的
`<RimDelegation.DelegationModeDef>` 同一套写法）。

```
DelegationEventDef            公共字段：mtbDays / weight / minDaysBetween / letterDef / letterLabel / letterText
                              / consumesDayBudget
                              虚方法：CanFire(d, site) / Apply(d, site) → 结果描述
├─ _CaveIn          塌方        lostFraction / stallHours
├─ _BonusYield      发现富矿脉   bonusFraction / minBonusUnits / maxBonusValue
│                              / rarityReferenceValue / minRarityScale / immediateFraction
├─ _Setback         作业受挫     stallHours
├─ _Mood            队伍闹别扭   thought / minParticipants
└─ _PawnAccident    工伤        damageDef / damageAmount / victims
```

**判定**（`WorldObjectComp_Delegations.RollEvents`）：

```
每游戏内 1 小时（2500 ticks）掷一次
  对每个事件：
    冷却未过（minDaysBetween）        → 跳过
    CanFire(d, site) 为假             → 跳过（例如"没有已采矿石"时塌方没意义）
    Rand.Chance(1 / (mtbDays × 24) × weight) → 命中则触发并结束本小时
```

**每小时最多一件事** —— 否则同一时刻可能刷出一堆信件。

| Def | MTB | 效果 | 备注 |
|---|---|---|---|
| `RimDelegation_Event_CaveIn` | 4 天 | 损失 20%~50% **已采未交付**的矿石 + 停摆 1~4 小时 | `CanFire` 要求 `oreUnits ≥ 5`，否则没意义 |
| `RimDelegation_Event_RichVein` | 6 天 | 事件点规模 **+n 格**（n 见 §19.24.7），并顺手采出 25% | 加成**必须看矿种**，固定格数会把黄金/零部件矿灌爆 |
| `RimDelegation_Event_Setback` | 5 天 | 停摆 3~8 小时 | 纯时间损失 |
| `RimDelegation_Event_Quarrel` | 8 天 | 全员挂 `RimDelegation_Thought_EventSetback`（-4 心情） | 复用已有 `Thought_Memory` 机制；**要求队伍 > 3 人**（§19.24.7） |
| `RimDelegation_Event_Accident` | 10 天 | 1 人受 6~12 点 Blunt 伤害 | **唯一真正改动 pawn 的事件**；概率与伤害都随疲劳放大，见 §19.25.3 |

MTB 合计约 0.84 次/天 —— 大约一天一次，便于演示。
（3 人及以下的车队少了「闹别扭」，约 0.71 次/天。见 §19.24.7 ③。）

**演示路径**：开发者模式下选中矿点，会看到一排「DEV: 触发事件「XX」」按钮，
跳过概率与冷却直接触发；另有「DEV: 暂停/继续委派」。
启动日志也会报 `DelegationEventDef N 个`，数量为 0 时额外 `Log.Warning`
—— XML 写坏了的症状是"一个事件都不触发"，静默失败最难查。

#### 19.24.4 一处刻意的防御

`_PawnAccident.Apply` 里对 `pawn.TakeDamage` 包了 try/catch。
原因不是"随手加的保险"，而是这条路径**正是 §19.14 第 1 项那个未验证的高风险项**：
车队成员不在任何地图上，`TakeDamage` 是否安全尚无定论。
一次随机事件不该把游戏打崩，所以失败时降级成"受伤失败（异常类型）"写进信件，
并 `Log.WarningOnce` 打出 §19.14① 的标记 —— **让这个未决项在实际运行时自己暴露出来**。

#### 19.24.5 待验证 `[未决]`

| # | 待验证 | 失败后果 | 退路 |
|---|---|---|---|
| 1 | 暂停后休息条是否真的回升 | 暂停退化成"只是不挖矿" | 说明 registry 排除没生效，需查 `IsWorkingNow` 与排除顺序 |
| 2 | 暂停/继续的按钮在两个入口都出现且状态同步 | UI 不一致 | —— |
| 3 | 5 个事件 Def 是否都加载（看启动日志） | 静默无事件 | XML 元素名/字段名对照 |
| 4 | 工伤事件能否真的造成伤害（§19.14① 的实测） | 信件显示"受伤失败" | 改用 `Hediff_Injury` |
| 5 | 塌方/停摆的时长在 UI 上是否正确显示 | 显示错位 | —— |
| 6 | 暂停期间原版 30 天计时器（已暂停态）与我们的暂停是否冲突 | 计时器语义混乱 | §7.2 的暂停逻辑独立，理论上无冲突，需实测 |
| 7 | Mod 设置里取消勾选后，随机事件是否真的不再触发 | 设置无效 | 确认 `RimDelegationMod.Settings` 在游戏内非 null（构造函数已赋值），必要时改读 `LoadedModManager.GetMod<RimDelegationMod>().GetSettings<RimDelegationSettings>()` |
| 8 | 富矿脉在真实存档里的加成格数与 §19.24.7 的表是否同量级 | 经济被个别矿种打穿 | 调 `maxBonusValue` / `minRarityScale` / `bonusFraction` |

#### 19.24.6 这个 demo 没有做的部分

| 缺口 | 说明 |
|---|---|
| 事件与威胁系统联动 | "守军来袭"这类事件应当走 §19.23 的战场评估 + 真正的 Resolving 阶段，而不是简单扣血 |
| 事件只发生在工时段 | 休息时段与暂停时段不掷骰（设计如此，但"夜里被野兽骚扰"这类事件因此缺失） |
| 事件权重按模式/地形调整 | 目前 `weight` 是固定值；理想情况应随生物群系、威胁等级变化 |
| 事件历史 | 只留了"最近一次"，没有完整事件日志（可复用 §19.22 的日志分类思路） |
| `DelegationEventDef` 不影响委派结果以外的系统 | 例如派系好感、地点派系敌意 |

#### 19.24.7 三处修订：事件开关 · 富矿脉看矿种 · 闹别扭要足够多人 `[验证]`

##### ① 随机事件可在 Mod 设置里关掉（默认开）

新增 `RimDelegationSettings.randomEventsEnabled = true`，接在 `DoSettingsWindowContents` 的
「开采期间的随机事件（默认开）」勾选框上；`RollEvents` 的**第一行**就检查它：

```csharp
RimDelegationSettings cfg = RimDelegationMod.Settings;
if (cfg != null && !cfg.randomEventsEnabled) return;   // 关掉后连骰子都不掷
```

放在 `RollEvents` 而不是 `FireEvent`，是为了**保留开发者按钮**
（`DevFireEvent` → `FireEvent` 仍然直通）—— 关掉随机事件是做内容验证的常见需求，
此时更不能把调试入口一起关掉。

**关掉之后必须"看得见"**：否则玩家（或我们自己）会把它当成"事件一次都没触发"的 bug。
两处补了提示：

| 位置 | 文案 |
|---|---|
| 矿点检视（`WorldObjectComp_Delegations.InspectText`） | `（随机事件已在 Mod 设置中关闭）` |
| 远行队页签状态行（键 `RimDelegationTabEventsOff`） | 同上（英文 `(random events are disabled in the mod settings)`） |

##### ② 「发现富矿脉」必须看矿种

**问题**：原来固定 `+20~60` 格。但 vanilla 矿点规模是
`V / (mineableYield × 矿石单价)`（`V ∈ [3500, 5000]`，见 §19.10），
稀有矿的格数**天生就少**。固定加 20 格会往黄金矿里凭空灌 800 单位黄金（8000 银）——
一封信件直接把经济打穿。

**做法**：加成经过四道闸，全部是 Def 字段（可调，无硬编码）：

```
① 按比例   n = round(矿点总格数 × bonusFraction)              bonusFraction ∈ [0.15, 0.35]
② 按稀有度 n = round(n × Clamp(rarityReferenceValue / 矿石单价, minRarityScale, 1))
                                                             rarityReferenceValue = 1.9（钢铁）
                                                             minRarityScale = 0.25
③ 按价值封顶 n = min(n, floor(maxBonusValue / (每格产出 × 矿石单价)))   maxBonusValue = 1200 银
④ 兜底     n = max(minBonusUnits, n)                          minBonusUnits = 1
```

**实测**（所有 `Mineable*` 的 `mineableYield` = 40；每格产出 = `EffectiveMineableYield`
= 40 × 难度 `mineYieldFactor`，中/普通难度为 1.0，下表取 40；`bonusFraction` 取中值 0.25）：

| 矿种 | 矿石单价 | 矿点格数 | 本事件加成 | 折算价值 |
|---|---|---|---|---|
| 钢铁 | 1.9 | 46–60 | **+12 ~ +15 格** | ~900–1100 银 |
| 白银 | 1.0 | 88–113 | **+22 ~ +30 格** | ~880–1200 银 |
| 翡翠 | 5 | 18–23 | **+2 格** | ~400 银 |
| 铀 | 6 | 15–19 | **+1 ~ +2 格** | ~240–480 银 |
| 玻璃钢 | 9 | 10–13 | **+1 格** | ~360 银 |
| 黄金 | 10 | 9–12 | **+1 格** | ~400 银 |
| 零部件 | 32 | 3–4 | **+1 格** | ~1280 银 |

三点要留意：

* **难度会整体缩放产出**（和平/简单 1.2、中/普通 1.0、困难 0.95、极端 0.8），
  所以格数是近似值 —— 难度越高，同样的银两需要更多格，事件的"格数加成"会略多。
* **零部件的价值封顶算出 0 格**，但被第 ④ 道闸兜到 1 格。这是**有意的**：
  「发现富矿脉」却一格不加，玩家的第一反应是"事件坏了"。1 格零部件 ≈ 1280 银，
  和封顶值同量级，不算失控。
* 表里的"折算价值"按 **每格产出 = 40** 算，即 `MiningYield = 100%` 的理想情况。
  实际每格还要乘矿工自己的 `MiningYield`（0.60–1.13）和 `mineableDropChance`，
  所以信件里的银两数字是**上限**，技能低的队会略少 —— 这个偏差方向是安全的
  （高估风险，不低估收益）。

**还修了一个会让事件"看着有效、实际无效"的坑**：`d.totalCells` 是本次委派**开工时**
从 `deposit.UnitsRemaining` 抓的快照（§19.10），光加 `deposit.totalUnits` 不会让
**当前这支队**多采一格 —— 新矿脉要等到下次委派才吃得到，信件里"要挖更久"就成了空话。
所以 `Apply` 里必须同时推进三处：

```csharp
d.deposit.totalUnits += n;
d.deposit.rolledUnits += n;              // 不同步 → UI 的"剩余/总数"对不上
if (d.totalCells > 0) d.totalCells += n; // 不同步 → 当前委派采不到新矿脉
```

##### ③ 「队伍闹别扭」要求队伍 > 3 人

原来的 `CanFire` 只要求 `participants.Count > 0`，于是**两个人出工也会"为分工吵一架"**，
读起来很假。改为人数门槛，并且写成 Def 字段而不是硬编码：

```csharp
public int minParticipants = 3;                     // 可被 XML 覆盖
public override bool CanFire(Delegation d, Site site)
    => thought != null && d != null && CountAlive(d) > minParticipants;
```

`CountAlive` 只数 `!Dead` 的人（倒地仍算 —— 躺着吵架不影响这条事件的合理性）。
默认 3 ⇒ **至少 4 人**。顺带删掉了原来没人用的 `extraTargets` 字段。

**代价**：`RimDelegation_Event_Quarrel` 在 3 人及以下的车队里**永远不会触发**，
MTB 合计随之下降（3 人队 ≈ 0.71 次/天）。这是刻意的取舍 —— 宁可少一件事，
也不要一件明显不合理的事。想改回来只需调 XML 里的 `<minParticipants>`。

##### 本轮验证到什么程度 `[验证]`

* 编译产物字段名已用 DecompilerServer 直接读 `Assemblies\RimDelegation.dll` 核对：
  `_BonusYield` 有 `bonusFraction / minBonusUnits / maxBonusValue / rarityReferenceValue /
  minRarityScale / immediateFraction`，**没有**残留 `bonusUnits` / `alsoGrantImmediately`；
  `_Mood` 有 `thought / minParticipants`，**没有** `extraTargets`。
  XML 节点名与这 8 个字段逐一对应（未知字段会被 vanilla 记成 XML error）。
* 反编译 `_BonusYield.Apply` 与 `WorldObjectComp_Delegations.RollEvents`，确认四道闸的顺序、
  `d.totalCells += n` 与设置守卫**都真的进了 DLL**（不是只改了源文件）。
* 7 个 XML 全部按 UTF-8 解析通过；两份语言文件各 31 个键、无重复键、无单侧缺失。
* 已部署到 `…\RimWorld\Mods\RimDelegation`，DLL / pdb / 2 个 Defs / 2 个语言文件的 SHA256 与源目录**逐一 MATCH**。
* **未验证**（本机无法启动 RimWorld）：§19.24.5 的第 7、8 项。

---

### 19.25 委派模式重新定价 · 疲劳 → 工伤 · 野外伙食 `[验证]`

> ⚠️ **19.25.2（四个模式的速率 / 心情定价）已被 RIM-5 取代**（2026-10-05，见 §S37）：
> 模式不再提供效率、也不再直接挂心情；下面的表**只保留作历史定价推导**，现行数值以 §19.109 为准。
> 19.25.3（疲劳 → 工伤）与 19.25.4（野外伙食）**仍然有效**。

这一节是"四个工作模式该怎么定价"的结论，以及为它补上的两个机制。

#### 19.25.1 定价基准：心情从 32% 起步，不是 50% `[验证]`

推翻早先一切"心情大概 50%"的直觉估算：

```
心情 = Clamp01(0.32 + 难度 colonistMoodOffset/100 + 想法合计/100)      // Need_Mood.CurInstantLevel
MentalBreakThreshold 默认 0.35  ⇒  轻微 35% / 重度 20% / 极端 5%
```

| 难度 | 和平/简单 | 中等 | 普通（默认） | 困难 | 极端 |
|---|---|---|---|---|---|
| `colonistMoodOffset` | +10 | +5 | 0 | -5 | -10 |

一个**什么想法都没有**的殖民者在默认难度就是 **32%**，已经低于轻微崩溃线。
⇒ 结论：**正常工作是基线，必须记 0；只有偏离基线的模式才该有心情值。**
原来 Mining def 上那条"野外扎营干活 -3"会把正常工作压到 29%，是明确的定价错误，已摘掉
（`RimDelegation_Thought_DelegationCamp` 保留定义只为让旧存档里的这条记忆还能解析）。

#### 19.25.2 四个模式（最终数值）`[验证]` —— ⚠️ RIM-5 起作废，见 §19.109

| 模式 | 工时窗口 | 速率 | 日 h-eq | 心情 | 最低休息 | 倍率均值 | 等效 MTB |
|---|---|---|---|---|---|---|---|
| 轻松工作 | 10:00–16:00（6h） | ×0.9 | 5.4 | **+3** | 76% | ×1.00 | 10.0 天 |
| 正常工作 | 08:00–16:00（8h） | ×1.0 | 8.0 | **0** | 68% | ×1.00 | 10.0 天 |
| 加班工作 | 06:00–22:00（16h） | ×1.1 | 17.6 | **-4** | 37% | ×1.36 | 7.4 天 |
| 全天候工作 | 00:00–24:00（24h） | ×1.1 | 26.4 | **-6** | 10% | ×3.52 | 2.8 天 |

"等效 MTB" = 工伤事件 `mtbDays ÷ 倍率均值`，也就是**工伤平均多久出一次**。

**工时窗口保持单段（`startHour`/`endHour`），这是刻意的取舍** `[未决]`：

设计稿里的"轻松工作 10-12 + 14-18""正常工作 08-12 + 14-18"是**两段**窗口（含午休），
现有 `DelegationModeDef` 表达不了。本轮**按现状保留单段**，把两段折叠成等效连续窗口
（总工时不变：6h / 8h）：

* 午休对**原始心情**毫无影响 —— 车队成员的疲劳想法本来就不生效（§5.5）；
* 午休唯一影响的是**疲劳 → 工伤倍率**，而那已经是二阶效应。

要真做午休，需要把 `DelegationModeDef` 改成窗口列表（`workWindows`），属于独立改动，未做。

#### 19.25.3 疲劳 → 工伤倍率 `[验证]`

**为什么必须自己补这条账**：`NeedRest`（-6/-12/-18）对未 spawn 的车队成员**不生效**（§5.5），
而 `DelegationWorker_Mining.Tick` 也从不读 `rest`。
⇒ 不接这条的话，24 小时连轴转除了一个固定心情数字以外**毫无后果**，休息条是"死"的。

**做法**：给 `DelegationEventDef` 加一个虚方法，把"动态倍率"开放给子类（数据驱动，不是 switch）：

```csharp
public virtual float ChanceMultiplier(Delegation d) => 1f;
```

`RollEvents` 里：

```csharp
chance = hoursPerRoll / mtbHours * weight;
chance *= Mathf.Clamp(def.ChanceMultiplier(d), 0f, 50f);   // 子类抛异常则退回 1
```

`_PawnAccident` 覆写它，参数全在 Def 上：

```
倍率 = 1 + (maxFatigueMultiplier - 1) × Clamp01((fatigueOnsetRest - 全队平均休息) / fatigueOnsetRest)
伤害 = damageAmount.RandomInRange × (1 + damageFatigueScale × 疲劳系数)
```

实测（40 天稳态模拟，`fatigueOnsetRest` = 0.7 / `maxFatigueMultiplier` = 4 / `damageFatigueScale` = 0.5）：

| 模式 | 最低休息 | 倍率均值 | 倍率峰值 | 等效 MTB | 伤害均值 |
|---|---|---|---|---|---|
| 轻松工作 | 76% | ×1.00 | ×1.00 | 10.0 天 | ×1.00 |
| 正常工作 | 68% | ×1.00 | ×1.07 | 10.0 天 | ×1.00 |
| 加班工作 | 37% | ×1.36 | ×2.43 | 7.4 天 | ×1.06 |
| 全天候 | 10% | ×3.52 | ×3.57 | 2.8 天 | ×1.42 |

**一个必须写下来的估算陷阱**：倍率是**每小时掷骰那一刻**算的，不是按"平均休息"算的。
`1 + (max-1) × factor` 是分段线性（凸）函数，按小时算再平均，**比按平均休息算大得多** ——
加班模式按平均休息算得 ×1.00（平均休息 70% ≥ onset 70%），按小时平均却是 ×1.36。
我第一版文档就写错了这一条，后来用 40 天模拟纠正。

`DelegationRegistry` 为此新增了第三个索引 `activePawns`（参与者 → 委派，**不看工时、不看暂停**），
因为吃饭 / 受伤这类事随时都会发生，`workingPawns` 回答的是另一个问题。
另外给 `RefreshIfNeeded` 加了 `Current.ProgramState != Playing` 守卫 ——
它现在会被 `Thing.Ingested` 的后缀调用，那条路径在世界生成阶段也可能被触发。

#### 19.25.4 野外伙食：原版已经给了半条，我们补另外半条 `[验证]`

**先纠正一个常见误解**：原版在车队里**已经**会按餐食给心情：

```csharp
// Thing.Ingested
List<FoodUtility.ThoughtFromIngesting> list = FoodUtility.ThoughtsFromIngesting(ingester, this, def);
… ingester.needs.mood.thoughts.memories.TryGainMemory(thought_Memory);
```

`Caravan_NeedsTracker.TrySatisfyFoodNeed` 走的就是 `food2.Ingested(...)`，所以
**奢华餐 +12 / 精致餐 +5 / 生食 -7 / 干粮 -12 在车队里照常生效**，
而且 `ThoughtDef.stackLimit` 默认 1 ⇒ `TryMergeWithExistingMemory` 只 `Renew()`，
**只刷新不叠加**（"每天吃奢华餐" = 常驻 +12，不是 +24）。

所以"食物提供额外心情加成"做成了**第二层**（`RimDelegation_Thought_FieldMeal`，4 阶段）：

| 阶段 | 标签 | 心情 | 覆盖 |
|---|---|---|---|
| 0 | 又啃干粮 | **-2** | 生食 / 干粮 / 营养膏（`MealAwful`）/ 动物饲料 |
| 1 | 凑合一顿 | 0 | 简单餐 / 包装食品 / 干肉饼（挂了但 0 心情的记忆不会显示，所以**不挂**） |
| 2 | 野外也吃得不错 | **+2** | 精致餐 |
| 3 | 野外也吃得好 | **+4** | 奢华餐 |

分类**不硬编码食物清单**，走原版自己的两级信息（`DelegationFoodMoodDef.StageFor`）：

1. `IngestibleProperties.tasteThought` —— 与原版心情**同源**，不会出现"原版说好吃、我们说难吃"；
2. `IngestibleProperties.preferability` —— 兜住没有 tasteThought 的（营养膏 = `MealAwful`、简单餐 = `MealSimple`、肉干 = `MealSimple`）。

**挂载点**：`[HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]` 的 Postfix。
为什么不打 `Caravan_NeedsTracker.TrySatisfyFoodNeed`：那个方法里吃的东西是**局部变量**
（`CaravanInventoryUtility.TryGetBestFood(caravan, pawn, out var food2, out _)`），后缀拿不到。
`Thing.Ingested` 是唯一能同时拿到"谁吃的 + 吃了什么"的公共入口 —— 原版自己也在这里挂 `tasteThought`。
该补丁在**全地图吃饭**时也会被调用，所以第一件事就是 `DelegationRegistry.AnyActiveFor` 过滤。

**UI 上可规划**（三处共用 `DelegationUIUtility.MealLine`）：

```
伙食（库存最好）· 奢华餐 · 原版 +12 · 委派 +4
疲劳 · 全队平均休息 37% → 工伤倍率 ×1.36
```

分开显示"原版"和"委派"是刻意的：让玩家看懂委派这条是**额外**的，不是替代。
对话框底部、远行队页签状态区、矿点检视文本三处都有；对话框里那两行用**已勾选的人**算，
所以勾人时会实时变。

这样"奢华餐把加班的心情买回来"就成了一个真实、可读的物流决定 —— 也是这套模式系统里唯一
能把长工时模式的代价对冲掉的手段。

#### 19.25.5 本轮刻意没做的 `[未决]`

| 缺口 | 说明 |
|---|---|
| 两段工时窗口（午休） | 见 §19.25.2；需要 `DelegationModeDef.workWindows` 列表 + `IsWorkingNow` 遍历 |
| 疲劳折损**产出** | 只有"疲劳 → 事故率"这一条。也可以让 `rest` 直接压 `workRateMultiplier`，但那会毁掉全天候的产出优势（24h × 1.1 × 0.75 ≈ 加班），所以没做 |
| 每个模式自己的 `restFloor` | 全天候现在靠全局 `Patch_CaravanNeedsTracker_Rest.ExhaustionFloor = 0.1`。若要"轮班 = 有人睡着"，需要按模式配 floor |
| 伙食标准（像毒品政策那样限定吃哪种饭） | 现在一律走原版 `CaravanInventoryUtility.TryGetBestFood`（自动吃最好的）。"只在加班时喂奢华餐"需要拦截食物选择 |
| 气候 / 折叠床进入模式平衡 | `EnvironmentCold/Hot`（-4…-16）与 `NeedComfort`（+4…+10）都已核实对车队生效，但还没进任何 UI 提示或模式推荐 |
| 心情的"期望"档位 | `Expectations` 车队里实际生效到哪一档未核实 |

#### 19.25.6 本轮验证到什么程度 `[验证]`

* 编译产物字段名用 DecompilerServer 直接读 `Assemblies\RimDelegation.dll` 核对：
  `DelegationFoodMoodDef` = `thought / entries / defaultStage`，
  `FoodMoodEntry` = `tasteThought / preferability / stage`，
  `_PawnAccident` 新增 `fatigueOnsetRest / maxFatigueMultiplier / damageFatigueScale`
  —— 与 XML 子节点名**逐一对应**。
* 反编译 `_PawnAccident.ChanceMultiplier`，确认疲劳公式真的进了 DLL。
* 8 个 XML 按 UTF-8 解析通过；两份语言文件各 31 键、无重复、无单侧缺失。
* 格式串守卫通过（`0.1` 那类坏格式串在 `Source/` 里没有）。
* Prototype 自测 **41/41**（回归，未受本轮改动影响）。
* 已部署并逐文件 SHA256 `MATCH`。DLL 127488 字节。
* **未验证**（本机无法启动 RimWorld）：见下表。

#### 19.25.7 需要游戏内确认（追加到 §19.24.5）`[未决]`

| # | 待验证 | 失败后果 | 退路 |
|---|---|---|---|
| 9 | 启动日志是否报 `Harmony … 已打补丁方法 4 个` 且 `DelegationFoodMoodDef 1 个` | 野外伙食补丁没打上 | 检查 `Patch_Thing_Ingested_FieldMeal` 的目标签名 |
| 10 | 读旧存档时，因为 `RimDelegation_Mode_DayOnly` / `_Extended` 已被删除，`mode` 是否退回默认（日志会打 `委派模式在 Def 里找不到了`） | 旧存档里委派静止不动 | 兜底已写在 `Delegation.PostLoadInit`；若仍卡死就补回两个别名 Def |
| 11 | 车队吃饭时是否真的挂上「野外也吃得好」 | 野外伙食不生效 | 确认 `Thing.Ingested` 的后缀被调用（可在 `GrantFieldMeal` 里开 verbose 日志） |
| 12 | 工伤倍率在界面上显示的数字是否与实际事故频率相符 | 数值标定偏了 | 调 `fatigueOnsetRest` / `maxFatigueMultiplier` |
| 13 | 对话框底部加了两行后，720 高的窗口里人员列表是否仍够用 | 列表被挤得太短 | `BottomHeight` 180 ↔ `TopHeight` 214 之间重新分配 |

#### 19.25.8 人员行的速率单位：秒/格 → 格/作业小时 `[验证]`

**改的是什么**：`DelegationWorker_Mining.PawnDetail` 里最后一个数字。

```
改前：采矿 8 · 速度 ×1 · 32 秒/格
改后：采矿 8 · 速度 ×1 · 1.32 格/作业小时
```

**为什么改**：`秒/格` 回答的是"挖一格要挥多久镐"，跨技能等级从 13 秒一路到 317 秒，
和对话框底部的「约 X 格/天」**不同量纲**，玩家要自己做除法才能对上。
`格/作业小时`（`2500 / ticks每格`）和「格/天」只差"工时占比 × 模式系数"一层，是同一把尺子。

| 采矿等级 | 0 | 2 | 4 | 6 | **8** | 12 | 16 | 20 |
|---|---|---|---|---|---|---|---|---|
| MiningSpeed | 0.10 | 0.28 | 0.52 | 0.76 | **1.00** | 1.48 | 1.96 | 2.44 |
| 秒/格 | 317 | 113 | 61 | 42 | **32** | 22 | 16 | 13 |
| **格/作业小时** | **0.13** | **0.37** | **0.69** | **1.00** | **1.32** | **1.93** | **2.58** | **3.21** |

**实现**：新增 `public static float DelegationWorker_Mining.CellsPerWorkHour(Pawn)`，
`PawnDetail` 调它。两个刻意的选择：

* **不含 `workRateMultiplier`**。模式系数（×0.9 / ×1.0 / ×1.1）对队里每个人一视同仁，
  折进去只会让"横向比较谁更快"这件事变脏；而且模式自己的系数就写在紧邻的模式行上
  （`轻松工作 · 10:00 - 16:00 · 速率 ×0.9`）。所以这里的口径是"**1 小时实际作业**的产出"。
* **保留 `速度 ×{MiningSpeed}`**。玩家在游戏里认得这个 stat；而且
  `TicksPerCell = 19 × round(100 / MiningSpeed)` 里有取整，两者并非严格等价，
  留一个"原版口径"便于核对。

**Radius UI 不需要改** `[验证]`：伴随 mod `RimDelegation-RadiusUI` 的人员行**不自己拼字符串**，
三处渲染（`PawnRowRenderer.cs:93`、`DelegationDialogSkin.cs:320`、`CaravanTabSkin.cs:168`）
全都走同一个入口：

```csharp
DelegationUIUtility.PawnLine(p, worker, def)   // → worker.PawnDetail(p)
```

所以只改 `PawnDetail` 一处，**原版皮肤与 Radius UI 皮肤同时生效** ——
这正是那个伴随 mod 的设计意图（`PawnRowRenderer` 的类注释就写着"复用 RimDelegation 的公开子件，
不重写它们的逻辑"）。已用 grep 确认 `RimDelegation-RadiusUI/` 全目录里**没有任何 `秒` / `速度`
的硬编码副本**：唯一相关的 `格` 只出现在它自己的「约 X 格/天」工期行上，而那本来就是格为单位。

**未做**：`格/天` 那个汇总行没动（本来就是格为单位），
`开采` 的 `PreviewLabel`（每格基础产出）也没动 —— 那是"格→单位"的换算，不是速率。

---

## S6 轮：文案、救援目标归一、固定流程、预期物品列表、紧急加班

> 用户本轮的两条原话（保留措辞）：
> 「委派：远程搜刮 和远程开采 这一系列删除"远程"的描述，因为实际上车队还是到了Site上面才进行的」
> 「在暂停委派和中止委派这部分，添加紧急加班（应用场景：任务马上就快要干完了，但是进入了休息时间，
> 让人物加班赶紧完成任务，请设计心情减益和实际功能）」
> 「搜刮物品藏匿点需要添加固定时间，添加描述性的流程显示：地图侦察 1hrs, 移动到目标区域 2hrs, 破门 1hrs， 撤离2hrs」
> 「预期获得的物品是否可以提供列表显示，包括物品的icon和信息」
> 「Bugfix 委派：救援 在未出发的时候显示"事件点上没有可救的人了" 这个显示有问题」

### 19.26 紧急加班：用心情买时间 `[实现]`

**形态（用户拍板：限时额度）**：每次点「紧急加班」买 **4 小时**"无视工时窗口"的额度，可续；
额度用完自动回到正常作息；随时可「结束加班」作废剩余额度（**心情不退**）。

三段机制，缺一条就变成免费加班：

| # | 机制 | 落点 |
|---|---|---|
| ① | **额度**：`Delegation.overtimeTicksRemaining`，只在**休息时段**被消耗（工时段照常按窗口走，不扣额度） | `WorldObjectComp_Delegations.TickDelegation` |
| ② | **心情**：同一地点第 n 次加班挂 `ThoughtDef` 的第 n 级 | `RimDelegation_Thought_EmergencyOvertime`（多 stage）+ `comp.emergencyOvertimeUses` |
| ③ | **生理**：加班期间 `IsWorkTime` 为真 ⇒ 不补休息 ⇒ 休息条下降 ⇒ 既有「疲劳 → 工伤倍率」自己收账 | `Patch_CaravanNeedsTracker_Rest` + `DelegationRegistry` |

**心情递进（用户给的数值）**：同一个地点第 1/2/3/4/5+ 次 = **-4 / -8 / -12 / -16 / -20**。
实现方式是多 stage 的 `Thought_Memory` + `stackLimit = 1`：
`MemoryThoughtHandler.TryGainMemory`（`0600D0E1:M` 反编译确认）在同 Def 且合并失败时
`memories.Add(newThought)`，随后 `while (NumMemoriesOfDef(def) > stackLimit) RemoveMemory(OldestMemoryOfDef(def))`
⇒ **新的一级自动挤掉旧的**，正好是"第 n 次覆盖第 n-1 次"。

**为什么按地点累计而不是按委派**：玩家会"中止 → 重新委派"接着干，
记在委派上等于每次都能把惩罚重置回第一级（可刷）。计数器落在 `WorldObjectComp_Delegations`（随地点存档，
标签 `roOvertimeUses`）。

**唯一工时判据**：`Delegation.IsWorkTime(site, nowAbs) = EmergencyOvertimeActive || mode.IsWorkingNow(...)`。
改之前"此刻算不算在干活"这个问题散在**四处**（`TickDelegation` / `DelegationRegistry` /
`Patch_CaravanNeedsTracker_Rest` / 两个 UI），加班只要漏掉一处就会出现
"界面说在加班、休息条却还在回升"这类半真半假的状态。全部收成一处之后，
`Patch_CaravanInspect` 的逐人行也顺带跟着加班走。

**入口**：车队 gizmo（`GetCaravanGizmos`）与「委派」页签各一个按钮；暂停中禁用（`cmd.Disable`）。
派生的「结束加班」gizmo 只在加班中显示。

### 19.27 固定流程：描述性阶段 `[实现]`

**形态（用户拍板：前置 4h + 收尾 2h）**：
`地图侦察 1h → 移动到目标区域 2h → 破门 1h`（**前置**，走完才开装车）→ 装车 → 任何收工条件成立后走
`撤离 2h`（**收尾**，走完才真正收工）。合计 6 小时，**与速率无关**（加人不加快）。

**数据驱动（用户拍板：Def）**：新增 `DelegationPhaseDef`（`label` / `hours` / `afterWork`）
+ `DelegationDef.flowPhases` 列表。采矿与营救的 `flowPhases` 为空 ⇒ 行为与加这个字段之前逐字一致。

**收工条件的唯一出口**：`TryBeginCompletion(d, site, flow, reason)`。
收工条件有四处（目标取尽 / worker 喊停 / 按天数 / 按配额），收尾流程必须挡在**全部**之前 ——
漏一处就会出现"写着撤离 2h、但地点当场没了"。进入收尾时把理由记进
`DelegationFlowState.pendingEndReason`（存档），因为收尾期间再问一次可能已经问不出同一句话
（例：撤离途中车队被卸了货，就不再"装满"了）。

**中断不走走尾**：`Abort` 直接结束 —— 撤不出来是因为被打断了，本来就该立刻结束。

**ETA 要把固定时间加上**：`Delegation.EstimatedDaysLeft` 里 `days += FlowRemainingTicks()/60000f`，
否则前置 4h 的活会被系统性低估。收尾只在**已经进入收尾**之后才计入 ETA。

**显示**：`DelegationFlow.Line()` → `✔ 地图侦察 1h → ▶ 移动到目标区域 2h（0.4/2h） → · 破门 1h → · 撤离 2h`，
落在地点检视面板与「委派」页签（原版 + RadiusUI 皮肤）。页签用 `Text.CalcHeight`/
`RadiusFont.HeightAt` 量真实高度 —— `Widgets.Label` 会换行但不裁剪，按固定 22px 画会盖到下一行。

### 19.28 委派页签重排（S8 阶段一）`[实现]`

**用户给的草图**（截图当规格）：信息按"玩家做决定"的顺序排 —— 什么活 → 什么状态 → 干到哪了 →
现场还剩什么 → 谁在干 → 干到哪一步 → 能干什么。三条结构性改动：

1. **状态词并进模式行右端、ETA 并进总进度行**：`作业中 / 休息中 / 已暂停 / 停摆中` 叠在模式行右端
   （`Widgets.Label` + `TextAnchor.MiddleRight`；Label 不吃事件，点击仍是切换模式），
   `剩余 1.24 天` 接到进度条尾注后面 ⇒ 原「当前/预计/累计」三行收成一行（省 44px）。
2. **现场物资可折叠，且空间不足时自动折叠**：页签没有滚动视图（§16.8），折叠是唯一的兜底；
   判据是"展开会让参与者一行都画不出来、而折叠能画出来" ⇒ **先保人**。
   折叠状态存 `RimDelegationSettings.stashItemsExpanded`，**原版与皮肤共用一份**（否则切皮肤忽开忽合）。
3. **参与者用 30px 头像的紧凑行（38px）+ 表头「查看全部 N 人」**：头像尺寸参数化
   （`DelegationUIUtility.DrawPawnLine(row, p, text, portraitSize)`），对话框仍是 36px/44px ——
   行画法仍然只有一处；「查看全部」走 `DelegationUIUtility.OpenParticipantsMenu`（FloatMenu + 信息卡）。

**这次修的真正硬伤是底部预留**：旧代码的闸门是常量 `y + RowHeight > size.y - 250`，与真实底部内容
脱钩 ⇒ "搜刮 + 现场物资 + 4 段流程"这种最重状态下**参与者一行都画不出来**。现在两侧各有一份
**纯函数** `BottomReserve(...)`（公式相同、行高各自），并与绘制**共用同一批入参**，想分叉都难。

**顺手接的原版现成件**（不自己发明公式，规则②）：`Caravan.DaysWorthOfFood`（**带 3000 tick 缓存**，
每帧调也安全）→「全部补给约可维持 N 天」，并入「伙食」行（`DelegationUIUtility.FoodDaysLine`）。

**阶段二（未做，等阶段一验收）**：
- 负重条 `(+X, X = 剩余将装入的质量)` —— 语义对齐原版 `CaravanUIUtility.CaravanInfo.extraMassUsage`，
  需要新 worker 虚方法 `EstimatedRemainingMass(d)`（矿点未掷定时给区间）；
  画法用 `Widgets.FillableBar` + `Widgets.FillableBarChangeArrows(barRect, changeRate)`。
- 「执行流程」横向链（`✅侦察完成(1h) → ⏳正在破门(0.4/1h) → ⏸撤离`）—— 需要
  `DelegationUIUtility.FlowChain(d)` 返回**结构化段**（label/state/hours）；
  绝不能让皮肤去解析 `FlowLines()` 的字符串，那等于把逻辑抄进皮肤。

### S6-a 硬 bug：救援把"读不到"说成"没有" `[验证 + 实现]`

**症状**（用户原话）：「委派：救援 在未出发的时候显示"事件点上没有可救的人了"」，
且补充「没进去过，和到地点了都是」。

**根因**：`RimDelegation_RescuePawn.PreviewLabel` 在 `Target(site,def) == null` 时直接断言"没有"，
而 `RescueUtility` 只读了原版两级来源里的**第一级**。反编译（rw16）逐字确认原版读的是两级：

```
GenStep_DownedRefugee.ScatterAt / GenStep_PrisonerWillingToJoin.ScatterAt：
  ① if (parms.sitePart.things != null && parms.sitePart.things.Any)
         pawn = (Pawn)parms.sitePart.things.Take(parms.sitePart.things[0]);
  ② else { comp = map.Parent.GetComponent<XXXComp>();
           pawn = comp.pawn.Any ? comp.pawn.Take(comp.pawn[0]) : 现生成; }
```
`DownedRefugeeComp` / `PrisonerWillingToJoinComp` 都是 `ImportantPawnComp`（`02003E56:T`）的子类，
而 `WorldObject.GetComponent<T>()` 走的是 `type.IsInstanceOfType`（`06015CA1:M`）⇒ 基类查询能拿到子类实例。

**修法**（与 S5-b 物资藏匿点同一套路，用户当时选的正是这一支）：

1. `RescueUtility` 补齐两级读取：`MatchingPart` / `TargetOwner` / `CompOwner`，
   取人一律问 `TargetOwner`（不再写死 `part.things`）；
2. 抵达时 `TryEnsureTarget` **归一**到第一优先级：
   * 已在 `part.things` → 不动（原有目标绝不重掷）；
   * 只在 `ImportantPawnComp` → **搬**进 `part.things`。必须搬而不是只读原处：
     `ImportantPawnComp.PostDestroy` → `RemovePawnOnWorldObjectRemoved()` 会把 comp 里**剩下的人**
     Destroy/PassToWorld，我们救出的人还在 comp 手里的那一刀会削到活人身上；原版 GenStep 也是 `Take()` 走的；
   * 两级都空 → 按 **GenStep 的同一口径**现生成并**写回** `part.things`
     （囚犯 `PrisonerWillingToJoinQuestUtility.GeneratePrisoner(tile, faction)`；
     难员 `DownedRefugeeQuestUtility.GenerateRefugee(tile)` + `WillJoinColonyIfRescued = true`）。
     这不是凭空造人：原版对同样的地点本来就会在生成地图时生成一个，我们只是把这一步提前并写回，
     于是"掷出来是谁，进图也是谁"（不双吃）。
3. `PreviewLabel` 改**三态**：读得到 → 详情；读不到且未抵达 → 「**现场目标未定**」（不再是"没有"）；
   已抵达/已清点却仍为空 → 这才是"没有可救的人了"。
   `DelegationDeposit.workerRolledContents` 复用为"这份目标是委派替原版掷的"（物资点/营救点共用）。

### S6-b 文案：删掉「远程」`[实现]`

车队是真的开到 Site 上作业，不是隔空遥控。4 条 `DelegationDef.label` 改为
**开采 / 搜刮 / 营救 / 救援**；`commandLabel` 本来就是「委派：YYY {0}」，不动。
连带影响面（都是 `label` 生成的）：`委派：{label}`（检视面板）、`可委派：{LabelCap}`、
页签标题「委派：{0} @ {1}」、`CaravanArrivalAction.Label` 的兜底分支。

### S6-c 「预期获得」列表 `[实现]`

新增 `DelegationWorker.PreviewItems(site, preview, exactDeposit)` + `DelegationPreviewItem`
（`thingDef` / `pawn` / `label` / `detail` / `value` / `uncertain`）。
措辞由 worker 给，UI 只负责"图标 + 标题 + 副标题" —— 三类目标的量纲完全不同：

* 搜刮：按 `ThingDef` **归并**（7×7 密室里常常几十堆同类物资，逐堆画就是噪音），按市价降序；
* 矿点：一种产物 + **区间**（`uncertain = true` ⇒ 画灰），抵达后换精确单位数；
* 营救：一行，图标用头像。

**两处都要画**（用户拍板）：原版对话框（`Dialog_ChooseDelegation`）与 Radius UI 皮肤
（`DelegationDialogSkin`）—— 用户开着皮肤，只做原版等于看不到。两处都只画前 3 行、其余滚动。

### S6 验收步骤

1. 启动游戏，`Player.log` 里应有 `DelegationPhaseDef 4 个`，且无
   `紧急加班…没有配置 emergencyOvertimeMoodThought` / `一条 DelegationPhaseDef 都没加载到` 的报错；
2. 右键任意地点：菜单与检视面板里**不再出现「远程」**；页签标题为「委派：搜刮 @ XX」；
3. 委派对话框：物资藏匿点应出现「预期获得（N 项）」列表（图标 + 名称 + 件数/kg/银）；
   矿点是一行区间、营救是一个人；
4. 搜刮委派开工后：页签/检视应显示 `流程：▶ 地图侦察 1h（x/1h） → · 移动到目标区域 2h → · 破门 1h → · 撤离 2h`，
   进度在前置 4 小时内**保持 0**（这是正常的，不是卡住）；装完车后应看到收尾「撤离」再发完成信；
5. 紧急加班：点一次应出现"紧急加班中：剩余额度 4 小时"，且**不在工时段**时进度照涨、
   队员休息条**下降**；同一地点第 2 次加班后记忆应变成 -8（第 3 次 -12）；
6. 救援 bugfix：
   * 预先委派一个（开发者工具生成的）囚犯/难员点：对话框应写「**现场目标未定**…」而不是"没有可救的人了"；
   * 抵达后应在日志看到 `救援点目标归一：…`，且 `SitePart.things` 里出现了人；委派能正常开工并救出。

### S6 未决 `[未决]`

* 紧急加班的**递进惩罚没有上限**（第 5 次之后恒为 -20）——数值若要收敛，改 `ThoughtDef` 的 stage 数即可；
* 固定流程目前只配给物资藏匿点；矿点/营救要不要各配一段（如"扎营 2h"）尚未定；
* `flowPhases` 的**中途存读档**已随 `DelegationFlowState` 存盘（标签 `flow`），但没有在游戏内实测过读档续跑。

---

## S7 轮：上机反馈（截图 + 三条增强）

> 用户原话（保留措辞）：
> 「bugfix: 本次进度中看不到物资具体是什么」
> 「enhancement:委派过程中支持委派模式切换，[紧急加班 +h]应该显示[紧急加班+4h] 建议配色为红色 [中止委派] 建议配色为红色；
>   流程显示enhance：正在侦察环境中 … (0h/1h)　侦察完成后变为:　侦察完成 (1h)　正在移动到目标地点… (0h/2h)」

### S7-a 硬 bug：`"键".Translate(参数)` 会吃掉格式说明符 `[验证 + 实现]`

**症状**：紧急加班按钮显示「紧急加班 **+h**」——`{0:0.#}` 整段不见了。

**根因（反编译确认，链条完整）**：

```
"RimDelegationTabOvertime".Translate(4f)          // 注意：带参数
  → 绑定到 TranslatorFormattedStringExtensions.Translate(this string, NamedArgument)
     （float → NamedArgument 是隐式转换，**普通形参**比 Translator.Translate 那条
       `params object[]` 的**展开形参**优先，所以走的是这条）
  → TaggedString.Formatted(NamedArgument)
  → GrammarResolverSimple.Formatted → TryResolveInner(...)
       它按 **token 名**解析 `{...}`，不是 string.Format
  ⇒ `{0:0.#}` 这个名字在参数表里查不到 → 整段替换成空串 ⇒ "+h"
```

**正确写法（本项目的既有惯例）**：先 `.Translate()` 拿**原文**，再用 `string.Format` 填参：

```csharp
string.Format("RimDelegationTabOvertime".Translate(), d.def.emergencyOvertimeHoursPerUse)
```

这也解释了为什么这个 mod 里 20+ 处文案全是 `.Translate()` + `string.Format(...)` 的啰嗦写法 ——
那不是风格，是绕坑。**S6 我在两处新文案上偷懒用了 `.Translate(参数)`，就成了唯一的漏网之鱼。**

### S7-b 「本次进度」看不到物资具体是什么 `[实现]`

新增 `DelegationWorker.ProgressItems(d, site)`（默认 null）：
* 搜刮 → 现场容器按 `ThingDef` 归并（与 `PreviewItems` 共用 `GroupOwner`），搬走一件就少一件，
  所以页签看到的天然与进度条一致，不需要另记一份账；
* 矿点 → 一行"剩余 N 格 · 约 M 单位"。

原版页签与 RadiusUI 皮肤都在「本次进度」下面画最多 3 行（图标 + 名称 + 件数/kg/银），
行画法收在 `DelegationUIUtility.DrawItemRow` 一处（否则两种皮肤会长得不一样）。

### S7-c 作业期间切换委派模式 `[实现]`

`WorldObjectComp_Delegations.OpenModeMenu(d)` / `SetMode(d, mode)`（gizmo、原版页签、皮肤三处共用）：
* gizmo「委派模式：{ModeLine}」（放在暂停之前）、页签的模式行**本身就是按钮**（`（点击切换模式）`）、
  皮肤把模式行换成 `UIKit.Flat.RailEntry(IconSet.Action.Swap, …)`；
* **换班不罚心情**：模式就是作息表，换一班不该罚款；代价由模式自己的 `dailyMoodThought` 承担
  —— 换过去之后下一次每日心情挂载才用新 Def，旧记忆按 `durationDays` 自然过期。
  ⚠️ 因此**不要**重置 `ticksSinceMoodTick`（那会变成"频繁换模式白拿心情"）。

### S7-d 破坏性动作染红 `[实现]`

* 原版页签：`Widgets.ButtonText` 没有"危险"档，靠 `GUI.color` 染（`new Color(1f, 0.55f, 0.5f)`）；
* RadiusUI 皮肤：`ButtonStyle` 只有 `Primary / Solid / Ghost` 三档（反编译确认，**没有 Danger**），
  所以照 `UIKit.Button` 的内部画法自绘 `DangerButton`：`CardChrome.Rounded` 铺深红底 →
  `Hover` → `CardChrome.Outline` → `RadiusFont.Label` 居中 → `Widgets.ButtonInvisible` 收点击。

### S7-e 流程逐段文案 `[实现]`

用户要的格式（已照做）：

```
流程：
　正在侦察环境中…（0.4h/1h）
　移动到目标区域（0h/2h）
　破门（0h/1h）
　撤离（0h/2h）        ← 侦察走完后，第一行变「侦察完成（1h）」
```

实现：`DelegationPhaseDef` 新增 **`progressLabel`**（进行中措辞）与 **`doneLabel`**（完成措辞），
两者留空时兜底为 `"正在" + label + "…"` / `label + "完成"`；
`DelegationFlow.Lines()` 逐段输出，落在地点检视面板、原版页签、皮肤三处。
（S6 那版的一行箭头格式 `✔ … → ▶ …` 已废弃。）

### S7 验收步骤

1. 页签（皮肤与原版都看一遍）：
   * 模式行可点 → 弹出模式菜单，切到「全天候工作」后 `ModeLine` 与每日心情立即变化；
   * 「现场物资（N 类）：」下面能看到具体物资名 + 件数/kg/银（搜刮点）；
   * 流程显示为逐段多行：`正在侦察环境中…（0.4h/1h）`、走完后 `侦察完成（1h）`；
   * 「紧急加班 +4h」**数字必须在**，且按钮与「中止委派」是**红色**。
2. 车队 gizmo：应多出「委派模式：…」按钮；点开能切模式，切换后有消息提示。

### S7 未决 `[未决]`

* 加班的递进上限、固定流程是否推广到矿点/营救（同 S6）；
* 「现场物资」只画前 3 类（超出显示"…还有 N 类未显示"）—— 类别多的藏匿点要不要做滚动列表尚未定。

---

## S9 轮：作业期编辑、现场物资台账、绝对完成时间、全局文案

用户给的需求（原话照录，做过的部分逐条对账）：

```
Enhancement:
  矿点描述 → "委派矿点任务给远行队，远行队的殖民者会在矿点扎营并且进行开采任务，完成任务后自行撤离并待命。"
  载重描述 → "载重 6.8 (+3.3)kg / 119kg" + "委派结束后预计车队载重 10.1kg"
  委派UI 支持编辑 (参与者，暂停委派，中止委派，紧急加班，切换委派模式，采空为止)，参与者支持排序
  现场物资 显示已经获取的数量/预期总数量 市场总价值 支持排序
  总进度显示 预期于 XX时间完成
  车队描述 → 车队改远行队 / 工伤倍率改「工作遭受事故伤害倍率」/ 伙食改补给品 /
             "原版 0 委派-2" 改 "标准心情加成 0，委派工作时的额外心情加成 -2"
BugFix: 从 RadiusUI 切换成原版 UI 后，原版 UI 没有重新切回 RadiusUI 的按钮
全局改动: 检查哪些需要全局改动（车队→远行队、伙食→补给品…）
```

拍板（选项式提问，五项都取"推荐档"）：A 全套操作台（原版 + 皮肤两端，页签补入口）/
A 物资行「已获取 X/Y + kg + 市价」+三档排序 / A 保留剩余天数并追加绝对时间（页签收进 tooltip）/
A 只改玩家可见文案（注释与 DESIGN 保留「车队」作内部术语）/ A 描述只改文案，不新增"自动撤离"行为。

### 19.29 「流程」块：统一阶段序列 + 阶段旁白 `[实现]`

**用户 S14 的草图**（照录）：`流程 ／ 阶段0: 🔵Chisa (随机一个人) 正在侦察环境 … ／ ↳ "躲在暗处观察是否有奇怪的物体…" ／ ████████░░░░░░ 40% | 预计还需 XX h`；阶段 1–4 = 移动到目标 / 打开通道 / 所有人正在搬运物资 / 撤离。四项拍板：**主作业段进入列表**、**未开始阶段完全隐藏**、皮肤用 `UIKit.Flat.Bar`、旁白池放 Def。

**统一阶段序列**（`DelegationStageList.Build`）= 前置段（`DelegationPhaseDef`，`afterWork=false`）+ **主作业段** + 收尾段（`afterWork=true`）：

- 主作业段**不是** flowPhases —— 它是 worker 按速率推进的主体工作（开采/搬运/营救），所以它的旁白只能挂在 `DelegationDef.workAmbientLines` 上；
- 只输出「已完成 + 当前」：`preludeIndex` 之后的阶段一个字都不给（"完全隐藏"）；
- 老存档 `flow == null` 时**不把前置段当成已完成**（否则凭空长出一串没发生过的阶段），直接显示主作业段。

**旁白与说话人**（`DelegationAmbient`）：

- 每阶段一个池（`DelegationPhaseDef.ambientLines` / `DelegationDef.workAmbientLines`）；换阶段**必掷**一条，同阶段内每 `ambientRerollHours`（默认 0.5h）换一条且不重复上一条；
- 掷定结果**存档**（`DelegationFlowState.ambientStageKey / ambientVariant / ambientPickedTick`）—— 在 `OnGUI` 里当场随机会每帧换词（用户明确要求"不要让句子频繁跳动"）；
- **暂停期间不换词**（S14 追加要求）：`DelegationAmbient.Tick` 在 `d.paused` 时直接返回，台词与条一起冻住；恢复后不需要补偿 —— 那时距上次掷定早已超过间隔，下一 tick 自然重掷。页签上"已暂停"仍由原有的状态行说明，这里不重复表达；
- 说话人 = **确定性哈希**（FNV-1a over `defName + "|" + stageKey`；**不能用 `string.GetHashCode()`** —— 它跨运行不稳定，会变成"每次开游戏换一个人"）；`speakerMode` = `RandomPawn` / `All` / `None`，池里的 `{0}` 替换成说话人；
- 台词池：`Defs/RimDelegation_Delegations.xml`（每阶段 8 条）+ `Doc/流程显示-示例与旁白池.md`（含采矿/营救的基础池与第二步要做的条件层 L1/L2/L3）。

**双端画法**（判据一份、画法各自）：

| 端 | 画法 |
|---|---|
| 原版页签 / 原版主控台 / 检视 tooltip | `DelegationUIUtility.StageRows` + `DrawStageRows`（`Widgets.FillableBar` 8px + `·`/`●` 字形 + 压暗 62%） |
| RadiusUI 页签 | `CaravanTabSkin.DrawStageRows`（`UIKit.Flat.Bar` 16px + `RadiusFont`） |
| RadiusUI 主控台 | 直接复用 `Cursor.Progress`（内部就是 `UIKit.Flat.Bar`，与「总进度」同一行同一件） |

- **字形约定**：`·`（U+00B7）与 `●`（U+25CF）已核实字体里有；**不用** `✓`（只在原版 Debug 工具里出现）与任何彩色 emoji（§19.37）。
- **高度**：`StageRowsHeight` 是与绘制逐行对应的纯函数（页签 `BottomReserve` 改用它）；页签已完成 > 3 段、主控台 > 6 段时压成一行汇总（折叠的是行数，不是信息）。

**S14 追加修正**（用户实机反馈"检查一下流程描述的逻辑问题"）：

| 问题 | 修法 |
|---|---|
| 物资点/矿点**没有守军**，台词却说"正在观察守卫 / 记下了守军换岗的规律" | 旁白池按 `ThreatAssessmentEntry.HasThreat(site)` 分流到 `ambientLinesHostile`（判据读的是存档里的 `SitePartParams.threatPoints`，不需要生成地图）；默认池只留"有没有敌人都成立"的句子。难民那条的"守卫/岗哨"同样挪进 `workAmbientLinesHostile`（它的威胁池是 7 选 1，可能一个敌人也没有）；囚犯那条守卫 **100% 是炮塔**，默认池本来就是敌情语境，不拆 |
| 队伍超过 1 人时"XXX 正在移动到目标地点…"像他一个人在走 | `DelegationPhaseDef.progressLabelGroup`（"正在带领远行队移动…"/"正在带队破门…"）：**多人 + `speakerMode = RandomPawn`** 时用，单人时仍用 `progressLabel` |
| 单人时"全队踩着…""有人说…"不成立 | `ambientLinesSolo` / `workAmbientLinesSolo`：选池顺序 **单人池 > 驮兽池 > 有敌情池 > 默认池**（单人优先：把"人"说对更重要）；单人时还会把说话人模式强制成 `RandomPawn`（单人语境写"全队"自相矛盾） |
| 囚犯营救的台词写"伤者"（囚犯是健康人） | 改成"把人从地上扶起来" |
| 休息时段进度条与 `phaseTicks` 都不动，玩家以为卡住了 | `DelegationModeDef.HoursUntilStart(tile, nowAbs)` + `DelegationUIUtility.WorkStartLine`：原版页签 / 原版主控台 / 皮肤页签各占一行（`BottomReserve` 已计入），皮肤主控台进「远行队」卡，检视 tooltip 的"当前：休息中"也带上小时数。判据走 `IsWorkTime` ⇒ **紧急加班时不显示**（那时人在干活）；暂停时也不显示（暂停另有状态行） |

**S14 收尾**（用户逐条勾选 a / b / d / e / f；**c 明确不改**）：

| 项 | 结果 |
|---|---|
| a 池里提到驮兽但队伍可能没有 | 新增 `ambientLinesPacked` / `workAmbientLinesPacked`（判据 `RaceProps.packAnimal`，只扫车队的活体动物）；选池顺序变成 **单人 > 驮兽 > 有敌情 > 默认** |
| b 完成行口径 / `doneLabel` 无处显示 | `label` 由"地图侦察"改为**"侦察环境"**（与 S14 草图 `✅ 侦察环境（Chisa 已完成，耗时 XX h）` 一致）；`doneLabel` 字段随之删除（没有任何显示位置）。完成行 = `侦察环境（Chisa · 耗时 1h）` |
| c 进度括号空格 | **不改**（保持 `（0.4h / 1h）`） |
| d 休息行把工时又写一遍 | 只留 `距离开工还有 X 小时`（工时在模式行里已有） |
| e 旧流程文案兼容层 | 删掉 `DelegationFlow.Lines / CurrentPhaseLabel / AppendLine` 与 `DelegationUIUtility.FlowLines / FlowLine`（S14 起无调用者的死代码）。⚠️ `HasPhases` **保留** —— `Delegation.FlowRemainingTicks()` 还在用它 |
| f 撤离首句假设搬了东西 | "然后想起了那箱货"改成中性句（"确认没落下什么。"） |

**S14 验收步骤**：① 搜刮一开工，流程块显示 `● <某人> 正在侦察环境中…（0.0h / 1h）` + 一句旁白 + 条 + 预计还需；② 满 0.5h 台词换一条（且不与上一条重复）；③ 侦察走完 → `· 地图侦察（<某人> · 耗时 1h）`，破门成为当前段且**换了一个人**；④ 前置走完出现 `● 全队 搜刮中（0/N 件）`（主作业段）；⑤ 车队装满后出现主作业段的完成行 + `● 全队 正在撤离…`；⑥ 存读档后台词与说话人不变；⑦ 原版页签与皮肤页签是"同一份文字、两套画法"，高度都不会把参与者挤出面板。

### 19.30 作业期编辑：改结束条件 · 改参与者 `[实现]`

* **为什么允许改**：结束条件与名单决定的是"干到什么时候、谁在干"，而玩家的判断随现场情况变；
  原版只在下达那一刻能选，改主意就得中止重开 —— 而中止会挂「白跑一趟」-6 心情，等于变相罚款。
* **账本不重置**（必须在 UI 上写明）：`ElapsedDays` / `MinedUnitsTotal` 都从开工算起，
  所以把已经干了 2 天的活改成"干满 1 天"会在下一 tick 立刻收工。这是有意的：
  改的是**目标**，不是**账本**。
* 新增公开入口（两端 + 页签共用，判定只有一份）：
  `WorldObjectComp_Delegations.OpenEndConditionEditor / SetEndCondition / QuotaCapOf`
  `WorldObjectComp_Delegations.OpenParticipantEditor / SetParticipants`
* 两个编辑窗口：`Dialog_SetDelegationEndCondition`（单选 + 步进器，量纲问 worker）、
  `Dialog_EditDelegationParticipants`（名单口径同对话框：`DelegationUtility.EligiblePawns`）。
* `SetParticipants` 以"车队里此刻真的能干活的人"为准（计划期选的人可能已经不在队里了），
  并以 `def.minPawns / maxPawns` 为闸门，越界只报错不改动。

### 19.31 「现场物资」台账：已获取 X/Y 的分母 `[实现]`

* 症状：`ProgressItems` 读的是**现场实时内容**（搬走一件就少一件），分母天生不存在 ⇒
  最多只能说"还剩 9 件"，说不出"已经拿了 3 件"。
* 做法：开工后立刻抄一份台账 `Delegation.itemLedger`（`DelegationItemLedgerEntry`：
  thingDef / unitLabel / expectedCount / expectedMass / expectedValue），
  由 `WorldObjectComp_Delegations.StartDelegation` 在 `new Delegation(...)`（worker.OnStart 已跑完）
  **之后**调 `active.CaptureItemLedger(site)`。
* 为什么写进存档：委派可存读档，台账丢了就会出现"读档后已获取变 0"这种最容易被察觉的不一致。
  **旧存档在途的委派 `itemLedger` 为 null ⇒ UI 自动退回"只显示剩余"**（绝不凭空画一个 0 当分母）。
* `DelegationPreviewItem` 新增机器可读的 `count / unitLabel / mass`（`detail` 是给人看的一整句话，
  UI 从里面抠不出数字）+ `detailPrefix`（由 `DelegationUIUtility.ProgressItemRows` 填「已获取 X/Y 单位 · 」）。
* `DrawItemRow` 顺带修正：明细从"只在 tooltip"改成**右对齐画在行内**
  （数字看不见等于没显示），并给 label 保底 40px 以免与明细叠字。

### 19.32 绝对完成时间：`GenDate.DateFullStringWithHourAt` `[反编译 + 实现]`

* 反编译依据（`Assembly-CSharp`，rw16）：
  `GenDate.DateFullStringWithHourAt(long absTicks, Vector2 location)`
  = `DateFullStringAt(...)` + `", "` + `HourInteger(...)` + `"LetterHour".Translate()`
  ⇒ 中文形如 `5501年 春 3日, 14时`；`location` 取 `Find.WorldGrid.LongLatOf(tile)`，**按经度算时区**，
  所以"几点完成"是真的当地时间。汉化由原版 Keyed（`FullDate` / `LetterHour`）负责，我们不维护日期措辞。
* 估算口径：`DelegationUIUtility.EstimatedFinishDays` = `Delegation.EstimatedDaysLeft`（含固定流程），
  并在「干满 N 天」这一档取 `min(工作量, 天数预算 − 已用)` —— 否则 3 天的活会被说成 5 天。
  刻意**不**为配额档单独折算：那样"预期完成"会早于同一行的"剩余 N 天"，两个数字打架比不精确更糟。
* 落地位置：主控台（原版 + 皮肤）画在总进度尾注；页签收进 tooltip（那一行已排满）；
  右栏「概览」的时间行副标题换成它，原口径「含固定流程在内的预计时间」改挂该行 tooltip。

### 19.33 载重与文案的单一来源 `[实现]`

* 用户给的版式：`载重 6.8 (+3.3)kg / 119kg` + `委派结束后预计远行队载重 10.1kg`，
  其中 `(+3.3)` = **还没装车的产物质量** = 剩余进度 × `worker.MakePreview().massPerUnit`。
* 公式收进 `DelegationUIUtility.TryMassForecast / MassMain / MassSub`（含对话框的区间版），
  主控台右栏、原版主控台、委派对话框、皮肤对话框四处共用 ⇒ 括号里的数永远等于"结束后 − 现在"。
* 换词（用户字面指定）：`工伤倍率 → 工作遭受事故伤害倍率`、`伙食 → 补给品`、
  `原版 {x} · 委派 {y} → 标准心情加成 {x}，委派工作时的额外心情加成 {y}`、`车队 → 远行队`。
  连带把 UI 里另外两处「工伤」口径统一成「事故伤害」（`工伤概率 → 事故伤害概率`、`工伤风险 → 事故伤害风险`），
  但**随机事件 Def 自己的名字仍是「工伤」**（那是事件名，不是倍率标签）。
* 全局排查范围（用户拍板 A）：只改**玩家可见**文案 —— Def 的 label/description/commandDesc/
  planningReportString/信件、Keyed、所有 .cs 里的 UI 字符串与玩家消息/信件理由；
  代码注释、`DESIGN.md`、`Doc/*.svg` 里的「车队」保留为内部术语。
  英文 Keyed 不动（英文里 `caravan` 就是原版术语）。

### 19.34 BugFix：回退到原版后没有"回皮肤"的入口 `[实现]`

* 根因：皮肤标题栏那颗「切回原版」把 `skinConsole` 置 false ⇒ `ApplyAll()` 把本块的 **prefix 摘掉**
  ⇒ 皮肤整段不再运行，"回皮肤"的入口只剩 Mod 设置里的复选框。
  同类路径还有一条：皮肤连续异常 3 次会自动停用该块（`TryDraw`）—— 玩家同样会卡在原版里。
* 修法（只在皮肤侧，RimDelegation 依然零依赖）：`SkinPatch.Slot` 新增 `Postfix`，
  `SetEnabled` 在**摘 prefix 的同一个动作里**挂上 `ConsoleRestorePostfix` ⇒
  原版照原样画，左下角叠一颗「切回 RadiusUI 皮肤」按钮（含半透明底 + tooltip），
  点击即 `skinConsole = true; enabled = true; ApplyAll()`。
* 幂等性：`ApplyAll()` 会被设置页每帧调用，所以用 `AppliedPostfix` 状态位保证 Patch/Unpatch 不重入
  （Harmony 重复 Unpatch 会抛"未找到补丁"）。

### S9 验收步骤

1. **矿点委派**（原版主控台与皮肤主控台各看一遍）：
   * 描述文本 = 用户给的新句子；
   * 总进度尾注出现 `预期于 5501年 春 X日, XX时 完成`（与「剩余 N 天」并存）；
   * 现场物资一行 = `黄金 · 已获取 3/24 单位 · 剩余 2 格 · xx kg · 市价约 xx 银`；
   * 载重两行 = `载重 6.8 (+3.3)kg / 119kg` + `委派结束后预计远行队载重 10.1kg`；
   * 「远行队」区里的两行措辞：`疲劳 · 全队平均休息 55% → 工作遭受事故伤害倍率 ×1.63`、
     `补给品（库存最好）· 莓果 · 标准心情加成 +0，委派工作时的额外心情加成 -2 · 全部补给约可维持 0.4 天`。
2. **操作台**：暂停/继续、紧急加班+4h、结束加班、切换委派模式、结束条件（改成「干满 3 天」→ 提示账本不重置）、
   编辑参与者（增删人 → 消息 + 名单与产能立即变化）、中止委派 —— 全部可用；
   参与者与现场物资两个列表的「排序：…」按钮每点一下换一档。
3. **存读档**：委派进行中存档 → 读档后「已获取 X/Y」不归零（台账随存档），
   `剩余 N 天` 与 `预期于 …` 保持一致。
4. **BugFix**：皮肤主控台点「切回原版」→ 原版窗口左下角应出现「切回 RadiusUI 皮肤」→ 点它回到皮肤。
5. **页签**：结束条件行可点（弹出编辑器）、参与者表头有「编辑参与者」、现场物资表头有排序按钮、
   总进度行 tooltip 里能看到预计完成时刻。
6. **旧存档兼容**：读一个 S9 之前存的"委派进行中"存档 ⇒ 现场物资不显示"已获取"前缀、其余一切正常。

### S9 未决 `[未决]`

* 「完成任务后自行撤离并待命」只是描述文案：实机行为仍是收工后就地待命（用户选择不改行为）；
* 结束条件切到「按天数/按配额」时若账本已越过新目标，会在下一 tick 立即收工（有意，但尚无二次确认弹窗）；
* 参与者在"抵达前的预先委派"阶段不可编辑（那时 `active` 还不存在，名单由 `ResolveParticipants` 在抵达时取交集）；
* 副标题式的命名遗留：`DelegationUIUtility.FatigueRiskLine` 里的 `AccidentRiskMultiplier` 仍是原版"工伤"命名，
  只改了玩家可见措辞，代码标识符未动。

---
## S10 轮：主控台可用性修补（截图逐条）

用户在截图里标了红/绿/蓝框并逐条点名，全部落在 RadiusUI 皮肤主控台（原版同步）。

### 19.35 原版 `MainButtonWorker.DoButton` 在 Disabled 时**不画图标** `[反编译 + 实现]`

* 用户报的 bug："没有委派的时候，下面的委派 UI 直接不显示了，需要显示。"
* 根因（`Assembly-CSharp` 反编译）：`DoButton(Rect)` 的第一段是
  `if (Disabled) { Widgets.DrawAtlas(rect, Widgets.ButtonSubtleAtlas); ...; return; }`
  —— 提前返回发生在**画图标与文字之前**（图标那行在方法尾部：`GUI.DrawTexture(..., def.Icon)`）。
  而我们的按钮有 `iconPath` ⇒ `label = (def.Icon == null) ? text : ""` ⇒ 文字也不画。
  于是 S8 写的"没有在途委派就 `Disabled`"实际效果是**按钮整块消失**，不是"变灰"。
* 修法：`Disabled` 只保留原版语义（没地图 / 正在规划路线 / 正在选点）；
  "没有在途委派"改为重写 `DoButton` —— 照原版画（图标、进度条、tooltip 一个不少），
  外面套一层 `GUI.color = (1,1,1,0.55)` 压暗。原版 `DoButton` 里的
  `Widgets.ButtonTextSubtle` / `GUI.DrawTexture` 都吃 `GUI.color`，所以一个乘算就够。
* Def 描述同步改成"按钮会暗下去（仍然可以点开看空态提示）"。

### 19.36 截图逐条修补 `[实现]`

| 用户原话 | 落点 |
|---|---|
| 「删除 1小时=2500ticks」 | Keyed `RimDelegationTabHours` 中英同步删掉括号里那句（=标红处其一） |
| 「标红的有两个显示不全」 | ① 总进度尾注不再拼绝对时刻，**另起一行**画「预期于 …完成」；② 排序按钮改用短标签（见下） |
| 「绿框里面的…可以点击的是否可以换个显示配色」 | 皮肤里的表头动作按钮一律 `ButtonStyle.Solid`（Ghost 长得像纯文字）；可点的概览行铺 `TableRow` 底 + 悬停高亮 + 右端"切换"提示 |
| 「编辑参与者是否可以直接在中间的参与者列表里改，如果可以，移除中间和右上角的按钮」 | 新增公开入口 `OpenParticipantRowMenu`（点人名 → 移出 / 打开信息卡）与 `OpenAddParticipantMenu`（加人，末尾常驻"一次改多人…"进窗口）；主控台与页签的「编辑参与者」按钮全部移除 |
| 「切换委派模式是否可以直接在概览里面调整」 | 概览「模式」行变成可点行（`GlanceEntry.Clickable/OnClick`），操作卡里那颗按钮移除 |
| 「参与者里面后面太长开不见了，直接移除->工作遭受的…」 | 概览「参与者」行的副标题缩短为「疲劳 · 全队平均休息 55%」，完整那句改挂该行 tooltip |
| 「地图是否可以拉满显示」 | 位置卡缩略图 `ScaleMode.ScaleToFit` → `ScaleAndCrop`（铺满不拉伸） |
| 「左上角的排序显示有问题」 | 左栏搜索框宽度改成按排序按钮反推（原来两者相加 336 > 栏宽 320 ⇒ 排序按钮被裁）；排序标签去掉"排序："前缀 |
| 「暂停委派和紧急加班调整到最下面，顺序你决定」 | 操作卡改成 [结束条件] → [暂停委派] → [紧急加班] → [结束加班]；原版主控台对应改成"上一行=改设定 / 下一行=动状态+中止" |

* 短排序标签：新增 `DelegationUIUtility.ItemSortShort` / `PawnSortShort`（`市价↓` / `数量↓` / `名称`；
  `技能↓` / `名字` / `心情↑`）。长文案留给宽度充足的委派对话框工具条 ——
  「排序：市价（高→低）」在 92~138px 的按钮里会被自己裁掉。
* 现场物资表头去掉「约」：`现场物资（N 类）· 市价合计 X 银`（要与排序按钮共处一行）。

### S10 验收步骤

1. 底部「委派」按钮：**没有在途委派时仍然看得见**（暗一半），点开是空态提示；有在途时恢复正常亮度 + 底部进度条。
2. 主控台：累计作业行不再有 tick 换算；总进度下面是独立一行的「预期于 …完成」；
   现场物资/参与者表头的排序按钮文字完整、一按换档；点人名出「移出 / 打开信息卡」；
   「添加人员」能加人（满了会说明）；概览的模式行点一下弹出模式菜单；位置卡地图铺满整卡。
3. 页签（原版 + 皮肤）：表头排序标签完整、点人名可改名单、「添加人员」可用。

---
## S11 轮：动作沉到底栏，「操作」卡整张删除

用户的红色箭头（截图）把上一轮的"最下面"指明白了：不是"操作卡的最下面"，而是**窗口底栏**
（与「固定 / 中止委派」同一排）。所以：

* **删除** RadiusUI 皮肤右栏那张「操作」卡 —— 它上面的按钮已经全部有更合适的家：
  切换模式 → 概览的模式行（S10）、编辑参与者 → 参与者名单里点人名（S10）、
  结束条件 → 概览的结束条件行（本轮改成可点行，与模式行同一套 `Clickable/OnClick` + 右端"切换"）；
* **暂停委派 / 紧急加班 / 结束加班搬到皮肤窗口底栏**，从右往左排：
  `中止委派（红）| 固定 | [结束加班] | 紧急加班（红）| 暂停委派`，左侧仍是状态摘要 + 快捷键提示
  （宽度不够时**先让提示**，绝不叠到按钮上）。
  这一排的心智变成"立刻改变现场状态的动作都在这儿"。
* 原版主控台对称收紧：正文里的**模式行与结束条件行本身变成可点按钮**（"（点击切换模式）"/"（点击修改）"），
  于是操作区不再需要那两颗按钮，只剩一排 `暂停 / 紧急加班 / 结束加班 → 查看页签 / 中止委派`；
  详情栏窄于 700px 时把「查看页签 / 中止」挪到第二行。

### S11 验收步骤

1. 皮肤主控台右栏只有 概览 / 位置 / 导航（**没有「操作」卡**）；概览里的模式行与结束条件行都带"切换"提示、点得动。
2. 窗口底栏：从右往左是 `中止委派（红）/ 固定 / [结束加班] / 紧急加班（红）/ 暂停委派`，点得动、不叠字。
3. 原版主控台：模式行/结束条件行可点，底部一排只有状态与破坏性动作。

---
## S12 轮：图标换矢量图 + 「前往中」（在途计划）上主控台

### 19.37 emoji 字形画不出来，但等效矢量图标可以 `[实现]`

* 用户给了一张"每个分区配哪个 emoji"的建议表。**字形 emoji（🔧⏳😫🎒）不能直接写进 UI**：
  游戏字体没有那些码位，`Widgets.Label` 只会画豆腐块（§S8 已有先例，见 lesson）。
* 可行做法：RadiusUI 框架自带 23 组矢量图标，清单**直接查磁盘最快** ——
  `steamapps\workshop\content\294100\3786107692\Textures\RadiusUI\<组>\<名>.png`。
  本轮给 `GlanceEntry` 加了 `SubIcon`（副标题行也画一枚 14px 图标；`IconSet.Get` 取不到就不画，绝不画豆腐）。
* 映射（主图标 + 副图标）：作业中 `Policy/Work` + `Stat/BarGraph`／剩余 `Common/Clock` + `Common/Cal`／
  模式 `Action/Snooze` + 心情<0 `Stat/TrendDown` 否则 `Stat/Mood`／结束条件 `Action/Check` +
  中止 `Alert/Warning` 否则 `Stat/Food`／参与者 `Status/Focus` + `Status/Zzz`／载重 `Stat/Weight` + `Slot/Pack`／
  补给 `Stat/Food` + `Occasion/Caravan`。

### 19.38 在途计划：「前往中」这一组 `[反编译 + 实现]`

* 用户问："委派是否可以记录在途的委派（远行队还在路上，但已经做了决定或推迟决定）"。
* 反编译事实：计划**本来就被原版持久化** —— `CaravanArrivalAction_StartDelegation`（site/def/request）
  挂在远行队的 pather 上，`Caravan_PathFollower.ExposeData` 里有
  `Scribe_Deep.Look(ref arrivalAction, "arrivalAction")` + `Scribe_Values.Look(ref destTile, …)`；
  `request == null` 就是「延后决定」。**但 `arrivalAction` / `destTile` 都是 private**
  （只有 `curPath` / `nextTile` 是 public）⇒ 想列出来要么反射私有字段、要么自记一份。
* 用户拍板 **方案 A + 语义②**：站点 comp 自记（零反射零 Harmony），"取消计划"= 取消抵达动作但让远行队继续走到那里。
* 实现：
  * `WorldObjectComp_Delegations` 新增 `plannedCaravan / plannedDef / plannedRequest / plannedAtTick`
    （Scribe 存 `roPlanned*`，读档后校验）+ `HasPlan / PlanDecided / RecordPlan / ClearPlan / CancelPlan / ValidatePlan`；
  * 记录点只有一处（两条下达路径的最后一步）：`GetFloatMenuOptions` 里 dialog 的 `onConfirm` 与 `onDefer` 两个 lambda；
    开工时由 `StartDelegation` 清掉（`ClearPlan("已抵达并开工")`）；
  * `ValidatePlan()` 每 tick 在 `CompTickInterval` 最前面跑（那时 `active` 还是 null，正是计划期）：
    远行队没了 / 已改道（`pather.Moving && Destination != site.Tile`）/ 没在走又不在格子上 ⇒ **只丢记录**，
    绝不反向改原版行为（最坏情况只是列表少一行）；
  * `CancelPlan()`：人已在格子上 → `pather.StopDead()`；否则 `pather.StartPath(pather.Destination, null, repathImmediately: true)`
    ⇒ 抵达时 `arrivalAction == null`，原版什么都不做；
  * `DelegationRegistry.AllPlanned()`（与 `AllActive` 同一次遍历、同一个 tick 缓存；两者互斥）；
  * 文案与详情共用件：`DelegationUIUtility.PlanStatusWord` / `PlanLines`（两端同一份，双端准则）。
* UI：原版与皮肤主控台左栏都多一组 **「前往中」**（行 = `远行队 → 地点` / `委派 · 已决定|延后决定`，tooltip 是完整计划），
  选中后右侧（皮肤主列 / 原版详情区）显示计划摘要 + `取消计划` / `选中该远行队`；
  皮肤标题栏计数加了 `前往中 {3} 项`（Keyed `RimDelegationConsoleCounts` 加第 4 个参数，中英同步）。
  计划期刻意**不给**暂停/加班/改模式 —— 那些属于已开工的委派（要改计划就取消重下）。

### S12 验收步骤

1. 选中一支远行队 → 右键远处的矿点 → 「委派：开采 XX」→ 对话框点「确认委派」：主控台左栏立刻出现 **前往中 1**，
   行显示"远行队 → 地点 · 开采 · 已决定"，右侧能看到模式/结束条件/参与者。
2. 同样流程点「延后决定」：那一行显示 **延后决定**，摘要写"抵达后再选…"。
3. 「取消计划」：远行队**继续走向该地点**，抵达后不会开工（Player.log 有 `丢弃在途计划…玩家取消`）；
   重新右键仍能下达委派。
4. 下达计划后手动把远行队改道去别处：那一行自己消失（日志 `远行队已改道`）。
5. 存读档：计划仍在（`roPlannedCaravan/roPlannedDef/roPlannedRequest` 进了存档）；抵达开工后自动从
   「前往中」挪到「进行中」，计数同步。

---
## S13 轮：底栏布局 · 远行队补给品列表 · i 按钮 · 参与者 V/X · 补给不足标红

用户的截图逐条（箭头从「补给品」那行画到窗口底栏，两个红框圈住主列下方的空白）：

* **底栏布局**（用户："固定移动到暂停委派的左边，然后调整这几个按钮的布局"）：
  左→右变成 `固定 | 暂停委派 | 紧急加班 | [结束加班] | 中止委派` ——
  左边是阅读/作业状态类的轻动作，越往右越破坏性；原来「固定」被夹在加班与中止之间，容易误点。
* **远行队补给品列表**（用户："远行队的下方，添加一个远行队补给品的列表（类似现场物资），
  显示图标，名称，数量，总计营养，重量，市场价值"）：新增公开件
  `DelegationUIUtility.CaravanFoodItems(caravan)` / `FoodItemsHeader` / `NutritionOf`。
  数据源是原版 `CaravanInventoryUtility.AllInventoryItems`，判据 `def.IsNutritionGivingIngestible`
  ——与右上角那个"可维持 N 天"**同一把尺子**，所以列表与天数不会互相矛盾。
  营养取 `ingestible.CachedNutrition × 数量`（未 spawn 的裸 Thing 没有 StatDef 上下文），
  质量 `BaseMass × 数量`（与物资点 worker 同口径），市价 `MarketValue × 数量`；排序 = 总营养降序。
  列表放在主列「远行队」那一段下方（原版与皮肤两端都有；页签因为空间已经被"搜刮 + 物资 + 4 段流程"占满，不放）。
* **两个列表加 i**（用户："现场物资和补给品列表添加 i 键（查看信息）"）：`DrawItemRow` 新增
  `withInfoButton` 参数 —— 行尾画原版 `Widgets.InfoCardButton(thingDef)`（人名那条走的是头像+信息卡，
  不需要第二颗）。主控台与页签的现场物资、以及两端的补给品列表都开了。
* **参与者 V/X**（用户："参与者的最前面添加 V（Yes）或 X（No）来控制殖民者是否参与"）：
  参与者那一段从"只列参与者"改成**当前参与者（✓）+ 队里还能参加的人（×）**两批
  （`DelegationUIUtility.BuildParticipantRoster`），行最前面是开关
  （`DrawParticipantToggle`：字形 `✓` / `×` + 绿/红描边，**不用图形 emoji**——会渲染成豆腐块），
  点一下走 `comp.ToggleParticipant` → 复用 `AddParticipant` / `RemoveParticipant` 的上下限判定。
  没参加的人整行压暗 62%，一眼能分清状态。
* **补给不足标红**（用户："右上角的全部补给可维持X天，如果X小于2，标红警告"）：概览卡补给行的判据
  用 `DelegationUIUtility.FoodDaysLeft(caravan) < 2f`（与那行文字同源），满足时主标题/图标/副标题全红，
  副标题补一句"（不足 2 天）"。为此 `GlanceEntry` 新增 `MainColor`（alpha == 0 = 默认 Ink）。

### S13 验收步骤

1. 底栏从左到右是 `固定 / 暂停委派 / 紧急加班 +4h / [结束加班] / 中止委派`，窄窗口时左侧提示先让位。
2. 主列「远行队」下方出现「远行队补给品（N 类）· 营养合计 X · 市价合计 Y 银」+ 每行
   `图标 名称 ×N 份 · 营养 X · Y kg · 市价 Z 银` + 行尾 i；远行队没有食物时显示红色的空态提示。
3. 现场物资每行行尾也有 i，点开是该 ThingDef 的信息卡。
4. 参与者那一段：参与者行前面是绿色 ✓、队里没参加的人是红色 ×（整行压暗），点一下即加入/移出；
   人数已到下限时（`def.minPawns`）✓ 不再可点。
5. 补给不足 2 天时，右上角「全部补给约可维持 0.4 天」整行标红并注明"（不足 2 天）"。

---
## S14 轮：补给品的腐烂时间（照抄原版渲染口径）

用户要求："远行队补给品里面添加显示物品的腐烂时间 例如在 x 38分后添加 (2.4)；同样的，右上角的补给时间也添加 例如 7.8 (2.4)"。

反编译核实到的**原版口径**（照抄，不自创）：

* `RimWorld.Planet.Caravan.DaysWorthOfFood` 的类型是 **`(float days, float tillRot)` 元组**
  （getter 里 `DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood` + `DaysUntilRotCalculator.ApproxDaysUntilRot`，
  带 3000 tick 缓存）；`tillRot` 本身是"每件食物的剩余腐烂天数 × 营养"的**加权中位数**（`GenMath.WeightedMedian`，
  不腐烂的按 600 天哨兵值参与）。
* `CaravanUIUtility.GetDaysWorthOfFoodLabel`（private，抄格式不抄方法）：
  `days.ToString("0.#")`，且**仅当 `tillRot < 600 && tillRot < days`** 时追加
  `" " + "(" + "DaysWorthOfFoodInfoRot".Translate($"{tillRot:0.#})")`。
  中文 Keyed `DaysWorthOfFoodInfoRot` = `{0}之后腐烂`，而**右括号是参数的一部分** ⇒
  渲染出来就是用户举的例子：**`7.8 (2.4)之后腐烂`**。
* 逐件的腐烂：原版 `DaysUntilRotCalculator` 里是 `TryGetComp<CompRottable>()`，`!Active` 视为不会烂；
  公开 API 拿天数用 `CompRottable.ApproxTicksUntilRotWhenAtTempOfTile(tile, ticksAbs)`（按**当前格子的季节温度**）。

实现（都在公开件里，两端 + 页签共用）：

* `DelegationUIUtility.FoodDaysLine`：改成 `days` + 满足条件时追加 `RotPhrase(tillRot)`
  —— 与右上角那行、与原版车队面板是同一个数、同一句话。
* 新增 `DelegationUIUtility.RotPhrase(float)`：`"(" + "DaysWorthOfFoodInfoRot".Translate("2.4)" )`，
  **先把参数 Format 成带右括号的字符串**再交给 Keyed（S7 的坑：`"key".Translate(参数)` 吃格式说明符），
  并用 `Translator.CanTranslate` 兜底自拼中文，绝不显示裸键名。
* `CaravanFoodItems`：每类食物额外记"**最先腐烂的那一摞**"的剩余 ticks（`min`），
  明细尾巴接上同一句 `(2.4)之后腐烂`；≥ 600 天按"不会烂"不显示。
  温度只看脚下那一格（原版算车队总账时会沿路径逐格估温度）⇒ 属 `[近似]`，已在代码注释里写明。

### S14 验收步骤

1. 远行队里带会腐烂的食物（莓果 / 生食）：主列「远行队补给品」每行明细尾巴出现 `(2.4)之后腐烂`；
   带包装生存食品（不会烂）的那一行**没有**这一句。
2. 右上角「全部补给约可维持 X 天」在"先烂后吃完"时同样出现 `(2.4)之后腐烂`；
   `tillRot ≥ days`（能吃到吃完才烂）时**不显示** —— 与原版车队面板的行为一致。
3. 与游戏内原版车队信息栏对照：同一时刻的 `days` 与 `tillRot` 两个数字应当完全一致。

---

### 19.39 随机事件 UI：零弹出 · 事件留痕 · 结束报告 `[实现]`

**用户 S15 的裁决**（原话）：「是否不按游戏的弹信事件处理，只按委派-流程里面的添加叙事趣味的事件处理？**没有 Major Crises**」，随后四项拍板：**零弹出** / 历史记录用世界级组件（第二期）/ 勾选项放全局偏好 / 报告窗口做**打断版**。

**为什么必须动**：事件原本**每件都发 Letter**，而 `Apply()` 返回的那句话**不存档** —— 事件一过就再也查不到，也没法在流程块里显示。
另核实：`LetterDef.pauseMode`（1.6 的字段，旧的 `pauseIfUrgent` 已不存在）在 `NeutralEvent/PositiveEvent/NegativeEvent` 上都是 `AnyLetter`，而玩家 `Prefs.automaticPauseMode` 默认 `AnyThreat` ⇒ **这些信默认并不暂停游戏**；真正的打扰是信件区堆积与左上角图标。

| 件 | 说明 |
|---|---|
| `DelegationEventLogEntry` | `def` / `firedTickAbs` / `detail` / `seen`；挂在 `Delegation.eventLog`（`Scribe_Collections`，FIFO ≤ `MaxEventLog` = 50） |
| `DelegationEventDef.severity` | 两档 `Flavor` / `Effect`（默认 Effect）；配套 `flavorLines` —— 纯叙事事件**只写 XML，不碰代码** |
| 零弹出 | `FireEvent` 不再发 Letter。三条知情路径：流程块事件行 / 结束报告 / 底部按钮角标 |
| 流程块事件行 | 新行类型 `Event`（橙色 `!` 前缀），四端共用同一份排版模型（`DelegationStageList.Rows` 的新参数 `lastEvent`） |
| 停摆呈现 | **不新插 stall 阶段**，而是把停摆标记挂在"当前段"上：条冻结 + ETA 改写成 `停摆中 · 剩余 Xh`。理由：阶段序列的"当前段"必须唯一（旁白掷定与 Rows 排版都依赖它），插一段立刻变两个 Active |
| 结束报告 | `DelegationUIUtility.EventReportLines` 为**唯一来源**：完成 / 中断信件正文与报告窗口共用 |
| `Dialog_DelegationReport` | 打断版：`forcePause = true` / `closeOnClickedOutside = false`（必须点「确认」）/ `absorbInputAroundWindow = true` / `onlyOneOfTypeAllowed = true`；`DelegationReport` 静态队列负责**排队**（多条同时完成不叠窗） |
| 设置 | `RimDelegationSettings.reportOnComplete`（默认关）+ Mod 设置界面 + 皮肤主控台底栏「固定」左边的 checkbox |
| 角标 | `MainButtonWorker_Delegations.DrawUnseenBadge`（红方块 + 数字）；`DelegationRegistry.UnseenEventCount / MarkAllEventsSeen`，打开主控台（`Activate`）即清零 |
| 示例内容 | 两条 Flavor 事件：闲聊 / 沿途风景（各 4 句台词，`flavorLines`） |

**S15 验收步骤**：① DEV 触发塌方 ⇒ **零弹窗零信件**，流程块出现 `! 塌方：…`、条变 `停摆中 · 剩余 Xh`、底部按钮右上角出现红色角标；② 打开主控台 ⇒ 角标清零；③ 收工 / 中断的信件里是**逐条明细**（不再是"期间发生 N 次随机事件"）；④ 勾上「结束报告」后收工 ⇒ 弹**暂停**窗口、点「确认」才关；⑤ 两条委派同时完成 ⇒ 报告排队不叠；⑥ 存读档后事件留痕与"已查看"状态都保留。

**第二期（已实装）**：`RimDelegationHistory : GameComponent`（`List<DelegationRecord>`，FIFO ≤ `MaxRecords`=60；**懒补建** —— 老存档没有这个组件时 `Get()` 自动补一个，因为 `Game.FillComponents()` 是反射自动收集 `GameComponent` 子类，本项目零 Harmony 只能挂这里）+ `DelegationRecord`（def / 地点名 / tile / 起止 / 理由 / 进度 / 产出 / 作业·停摆时长 / 事件明细，并能 `ToReportData()`）。
左栏最下方加「历史（N）」（原版主控台与 RadiusUI 皮肤各一套，最多列 20 行），点一行 → 右栏显示完整报告；报告画法只有一份 `DelegationReportUI.Draw`（签核窗口与主控台历史详情共用），皮肤侧另有 `RecordMain`（RadiusFont 版，同一份数据）。

### 19.40 S16：gizmo 图标 · 车队锁定 · 休息置顶 `[实现 + 反编译]`

**① 那排洋红叉的根因** `[反编译]`：`Verse.Command.DrawIcon` 里写着
```csharp
Texture badTex = icon;
if (badTex == null) { badTex = BaseContent.BadTex; }   // ← 没设图标的 Command 一律画洋红叉
```
⇒ 凡是没设 `icon` 的 `Command_Action`，在原版界面上就是**洋红色叉**（用户 S16 截图里那四个）。
本轮补上了原版现成纹理（`RimWorld.TexCommand.*`，零美术成本）：

| gizmo | 图标 |
|---|---|
| 暂停 / 继续委派 | `TexCommand.PauseCaravan` |
| 委派模式 | `TexCommand.Replant` |
| 紧急加班 | `TexCommand.FireAtWill` |
| 结束加班 | `TexCommand.ClearPrioritizedWork` |
| 中止委派 | `TexCommand.RemoveRoutePlannerWaypoint` |
| 锁定车队（锁 / 解） | `TexCommand.ForbidOn` / `TexCommand.ForbidOff` |
| 威胁评估 | `TexCommand.SquadAttack` |

**② 车队锁定（S16 用户要求："和定居 拆分这些按钮一起；锁定后右击其他地图块不会移动，以免误操作取消委派；默认锁定"）**

- `RimDelegationCaravanState : GameComponent`：存**被显式解锁**的车队 ID（`unlockedCaravanIds`）⇒ 名单外的 = 锁定，**默认锁定**成立；懒补建同上。
- `WorldObjectComp_CaravanLock`：挂在 **Caravan 自己的 WorldObjectDef** 上（`Patches/RimDelegation_CaravanComps.xml`），所以**每一支车队**都有这颗按钮（与「定居 / 拆分」同排）。⚠️ 该 def 原本**没有 `<comps>` 节点**，补丁是**追加**一个 `<comps>` 子节点（不是往已有节点里 Add）。
- `Patch_CaravanMoveLock`：**第 4 个** Harmony 补丁，`Caravan_PathFollower.StartPath` 前缀（`___caravan` 注入私有字段）。拦住的条件 = **锁定 + 有在途委派 + 目的地不是它委派的那个地点**；「前往中」的计划**不拦**（否则「取消计划」自己都走不动）。拦下时给一句 `Message`。

**③ 休息置顶（S16 用户要求："流程休息中的时候，把休息置顶，显示休息中的内容（睡觉或者睡前聊天）"）**

- `DelegationDef.restAmbientLines`：休息内容池（四个 def 各一组：睡觉 / 睡前聊天…）。
- 新行类型 `RestTitle` / `RestAmbient`，由 `DelegationStageList.Rows(..., restTitle, restAmbient)` 插在**最上面**（Header 之后、已完成段之前）—— 做成"两行"而不是新阶段，因为阶段序列的"当前段"必须唯一。
- `DelegationAmbient.Tick` 在休息时段改用 `"rest"` 键 + `restAmbientLines` 掷定（每 0.5h 换一句）；判据 `!paused && !IsStalled && !IsWorkTime`（与「距离开工还有 X 小时」同源）。

---

## S18 轮：全入口收敛到「委派」主控台（待下达草稿 · 页签引导页 · 继续决定）`[实现 + 反编译]`

**用户原话（2026-09-27）**：「RimDelegation 功能集成：所有页面是否可以都使用综合的委派UI处理？例如远行队页签的委派点击直接跳转到综合委派UI，在世界地图上右键Site进行委派任务，跳转的也是综合委派UI，进行Brainstorm」
用户随后拍板：**下单本身就在主控台里完成**（草稿 → 前往中连成一条线）；页签**变成引导页**（点开就打开主控台，面板只显示一句提示）；**抵达决策也进主控台**；**草稿不进存档**；主控台「查看页签」改成「选中该远行队」。

### 19.41 这轮先立起来的五条引擎事实 `[反编译]`

| # | 事实 | 影响 |
|---|---|---|
| ① | `Verse.FloatMenuWorld.DoWindowContents` 开头：`if (!(Find.WorldSelector.SingleSelectedObject is Caravan caravan)) { Find.WindowStack.TryRemove(this); return; }` | 世界地图右键菜单**天然**是"已选中一支车队"的语境 ⇒ 委派入口永远有 `caravan` 可传 |
| ② | `CaravanArrivalActionUtility.GetFloatMenuOptions(..., confirmActionProxy)` 的代理是在**菜单被点的那一刻**调用的，确认后再调 `startAction()`（内部是 `caravan.pather.StartPath`） | 换入口只需换这一处代理，`StartPath` 语义一字不动 |
| ③ | `Verse.WindowStack.Add` 第一句是 `RemoveWindowsOfType(window.GetType())` | **同类窗口全局唯一** ⇒ 多张草稿只能共存在一张表里，不能靠多开窗 |
| ④ | `WindowStack.WindowsForcePause` = **任一**窗口 `forcePause` 为真，逐帧求值 | 主控台可以按"有没有待下达"动态决定要不要暂停游戏 |
| ⑤ | `InspectPaneUtility.DoTabs` → `InterfaceToggleTab` → `ToggleTab`：先 `tab.OnOpen()`，**然后**无条件 `pane.OpenTabType = tab.GetType()` | 「点页签只跳窗、不展开面板」做不到（零 Harmony）⇒ 页签只能变成"96px 引导条 + 打开主控台" |
| ⑥ | `Caravan_PathFollower.StopDead()` 里 `arrivalAction = null` | 抵达后抵达动作已被原版清掉 ⇒ "抵达后决定"的回调直接走 `comp.StartDelegation`，且**关掉窗口就再也点不回来**（本轮的洞，见 §19.44） |

### 19.42 状态与画法一次抽干净

- **`Source/DelegationDraft.cs`（新）** —— 一次"还没下达"的表单。身份（`caravan/site/def/onConfirm/onDefer`）+ 12 个选择字段 + 只读派生（`preview/exactDeposit/dispMinCells/dispMaxCells/onTile`）+ 三个下拉菜单 + `QuotaCap/ChosenList/MakeRequest/CanConfirm/Confirm/Defer/Discard/IsStale`。
  ⚠️ **字段一律 public**：皮肤要直接读写。对话框时代皮肤只能反射读 18 个私有字段（旧 `DialogBridge`），草稿是新类，没有历史包袱就不该再复制一份反射。
  `RefreshSiteData()` 在构造时与"复用已有草稿"时都跑一遍（存量、预览区间、可参加人员都会变）。
  **同 (车队, 地点, 委派) 只留一张草稿**：来回点同一个地点期望的是"回到那张没写完的表单"，不是叠出几张空表；复用时以最新一次的 `onConfirm/onDefer` 为准（不同入口"确认之后做什么"不同）。
- **`Source/DelegationDraftUI.cs`（新）** —— 表单正文的**唯一一份**画法，从原 `Dialog_ChooseDelegation`（727 行）整段搬来并做了三处改造：① 状态改问 `DelegationDraft`；② 三个出口不在这里画（`Layout/ButtonY` 把按钮行留给宿主）；③ **窄列自适应**（主控台右栏最窄 ~370px，而原版是 640 固定宽 —— 结束条件行/工具条/数值微调都按可用宽度分配）。
  汇总区改成"先算行文案与行高、再落笔"（`BuildSummary` + `MeasuredHeight`），而不是固定预留 180 —— 窄列里自动换行会让固定高度互相压住。
- **`Source/RimDelegationDrafts.cs`（新）** —— `GameComponent`（`Game.FillComponents()` 反射自动收集，零 Harmony 的挂点），持有 `List<DelegationDraft>`、`Find/Add/Remove/Prune/Count`。
  **刻意不 Scribe**：草稿就是"玩家正在填的表单"，与"关掉对话框就没了"同一个心智；组件在读档时由原版新建，不写 `ExposeData` 就等于"读档即清空"，也不会留下指向上一局 Pawn/Site 的悬空引用。「前往中 / 进行中 / 历史」本来就有各自的存档。
- **`Dialog_ChooseDelegation.cs`** —— 727 行 → **~130 行薄壳**（标题 + `DelegationDraftUI.Draw` + 右下角三颗出口）。**保留**，由 Mod 设置里新加的「使用旧版委派对话框」（`legacyDelegationDialog`，默认关）驱动，是**不用改 dll 的逃生门**。

### 19.43 主控台的第四组：「待下达」

左栏在「进行中」之前多一组 **「待下达（N）」**（`DrawDraftRow`：车队 → 地点 / 委派名 · 未下达 · 就地开工），右栏选中它就是那张表单 + 底栏三颗出口（`确认下达 / 延后决定 / 取消`，措辞是 `DelegationDraftUI` 的公开常量，皮肤同一份）。

- **只在有草稿时画**（与「历史」常驻的理由相反：草稿一定是刚被玩家点出来的，而且会被自动聚焦；空占位只会挤掉别人的位置）。
- `EnsureSelection` 的优先级重排为：**入口指定焦点 > 当前选择是否仍成立 > 自动挑（待下达 > 进行中 > 前往中）**。`EnsureOpen(draft)` / `EnsureOpen(null, comp)` 是新加的聚焦入口。
- **动态 `forcePause`**：`forcePause = drafts.Count > 0` —— 有待下达的表单就是"必须现在决定"的场合（事实 ④）。
- **「查看页签」→「选中该远行队」**，并且**不再顺手关窗**（页签已退化，那个名字会误导；主控台是可拖拽浮动窗口，玩家想看一眼车队在哪再回来接着操作）。
- 底部按钮：有待下达时不再压暗，左下角多一颗**琥珀色小方块**（与右上角"有事件没看"的红色角标**语义分开**）。

### 19.44 顺手补掉的那个洞：「继续决定」

`ValidatePlan()` 在"人已经站在目标格上"时**故意保留** `planned*` 记录（原注释："玩家看到的『人到了但没开工』正是实情"）。但抵达动作已经被 `StopDead()` 清掉（事实 ⑥）⇒ **界面上没有任何入口能把那次选择做完**，玩家只能在地点上重新走一次"就地委派"（那是另一条路径，原选择全丢）。
⇒ `Window_Delegations.CanResumeDecision(comp)`（人已到位 + 未开工 + 地点还有得取）+ `ResumeDecision(comp)`（`EnsureDeposit` + 按 `(车队, 地点, 委派)` 重建草稿，`preRequest = comp.plannedRequest`）。原版与皮肤两端都在「前往中」的详情/主列长出这颗 `继续决定`。

### 19.45 页签退化成引导页

`WITab_Caravan_Delegation`：`OnOpen()` 里 `Window_Delegations.EnsureOpen(null, comp)`；`UpdateSize()` 压到 `620 × 96`；`FillTab()` 只画一句 `RimDelegationTabMoved` + 一颗「打开委派主控台」+ 一句 `RimDelegationTabMovedHint`。
⚠️ 面板**一定会展开**（事实 ⑤），所以策略是"压到最矮、只放一句说明"，而不是试图阻止它。
**净删**：原版侧 ~530 行绘制；皮肤侧 `CaravanTabSkin.cs`(36KB) + `CaravanTabBridge.cs` + 一个补丁点。同一排动作按钮从"四处各画一遍"降到两处。

### 19.46 皮肤侧：补丁点 3 → 1

| 动作 | 对象 |
|---|---|
| 删 | `CaravanTabSkin.cs` / `CaravanTabBridge.cs`（页签退化 ⇒ 无用） |
| 删 | `DelegationDialogSkin.cs` / `DialogBridge.cs` / `PawnRowRenderer.cs`（对话框退化成逃生门 ⇒ 按定义就该用 RimDelegation 原版样式，免得玩家分不清自己在哪个壳里） |
| 删 | `SkinPatch` 的 `Dialog` / `CaravanTab` 两个槽、`RadiusUISkinSettings.skinDialog` / `skinCaravanTab`（老配置里的键被原版 Scribe 忽略） |
| 加 | 左栏「待下达」组 + `DrawDraftRow`、主列 `DrawDraftMain`、底栏 `DrawDraftBottomBar`（`Primary/Solid/Ghost` 三档，**不用红**：表单还没发生任何事） |
| 加 | `Window_Delegations.ConsumePendingDraft()` / `ConsumePendingComp()` —— 皮肤整段拦掉 `DoWindowContents`，原版 `EnsureSelection` 不跑，入口焦点与 `forcePause` 必须由皮肤自己消费 |
| 已知取舍 | 皮肤里的**草稿表体**暂时调 RimDelegation 原版的 `DelegationDraftUI.Draw`（观感偏原版，但数据/校验/交互只有一份实现）。Radius 风格的草稿页是后续精修项：数据全在 public 字段上，照 Radius 画法重写一份即可，**不需要反射桥** |

### S18 验收步骤

① 车队停在事件点上点委派 gizmo ⇒ **底部主控台自动打开并聚焦**到一张「待下达」表单（游戏被暂停），左栏顶部出现「待下达（1）」；
② 表单里改模式/姿态/结束条件/勾人 ⇒ 与旧对话框一致；点「确认下达」⇒ 表单消失、条目录入「进行中」或「前往中」；
③ 右键远处的事件点 ⇒ 同样进主控台；点「延后决定」⇒ 车队出发，左栏变「前往中」；
④ 抵达后（`requireConfirmOnArrival` 或延后决定）⇒ **不弹模态框**，主控台里出现待下达表单、游戏暂停；
⑤ 把主控台关掉再打开 ⇒ 表单还在（草稿活在本次游戏会话里）；**存读档一次** ⇒ 草稿清空（刻意）；
⑥ 「前往中」那条人已到位却没开工 ⇒ 详情/主列出现「继续决定」，点它把表单调回来，确认后开工；
⑦ 远行队「委派」页签 ⇒ 点开只出现一条 96px 的引导提示，同时主控台被打开并聚焦到这支车队那条委派；
⑧ Mod 设置勾「使用旧版委派对话框」⇒ 四条路径全部回到 S17 那个一窗到底的模态框，且样式是 RimDelegation 原版；
⑨ 皮肤侧：确认 `Player.log` 只有一行 `委派主控台：启用（Radius UI 皮肤接管）`，主控台左栏的「待下达」组、主列草稿页、底栏三颗按钮都在。

### S18 未决

① ~~Radius 风格的草稿页~~ —— **已在 S19 做掉**（见 §19.48）；
② 草稿是否要在「待下达」为 0 时也常驻一行引导（当前不常驻，靠"点出来就会被聚焦"）；
③ 窄窗口（< 700px 高）下草稿表单会挤（打开草稿时会把窗口自动撑到 700，但玩家仍可再拖矮）；
④ 「待下达」组压在「进行中」上面是否符合长期手感（一句话就能调换顺序）。

---

## S19 轮：皮肤侧跟上「待下达」，右栏抽成三上下文共用 `[实现]`

**用户原话（2026-09-27，配三张截图）**：「关于待下达的UI（图一），是否可以直接用图二的，另外的，图三是前往中的UI，是否也可以显示图二的信息？」
三张图分别是：图一 = 皮肤主控台里的草稿页（当时表体直接调 RimDelegation 原版的 `DelegationDraftUI.Draw` ⇒ 观感就是"Radius 窗口里贴了一张原版表单"）；图二 = 皮肤的在途详情（三栏 + 右栏「概览 / 位置 / 导航」）；图三 = 皮肤的「前往中」详情（右栏整片空白、按钮重复）。

### 19.47 右栏：从"只有在途才有"到"三种上下文共用"

**根因（图三右侧那片空白）** `[源码]`：`DelegationConsoleSkin.DrawRail()` 第一句是
`if (d == null || site == null) { return; }` —— 计划与草稿都**没有 `Delegation` 实例**，于是整条右栏（含位置缩略图与导航）根本不画。
修法：抽出 `RailRun(r, pass)`（两趟同一份代码 + `FlatScroll`）与 `RailCards(c, glance, site, caravan)`，
「概览」的行按上下文换一个构造函数：

| 上下文 | 概览行来源 | 行内容 |
|---|---|---|
| 在途 | `BuildGlanceRows(d, site)` | 原样（进度 / 剩余 X 天 / 累计作业 / 载重 / 补给…） |
| 待下达（草稿） | `BuildDraftGlanceRows(draft)` | 未下达 / 模式 / 姿态 / 结束条件 / 预计 X–Y 天 / 已选人数 / 载重 / 补给 |
| 前往中（计划） | `BuildPlanGlanceRows(comp)` | 未开工 / 模式 / 结束条件 / 姿态 / 参与者 / 补给；取值全来自 `plannedDef` / `plannedRequest`（null = 延后决定） |

**铁律**：草稿与计划**只列算得出来的行** —— 进度条、累计作业小时、"剩余 X 天"都以 `Delegation` 实例为前提，宁可不显示，也不显示一行假的 0（与 §19.31「旧存档没有台账就不显示假 0」同一条口径）。
卡片顺序、位置缩略图（`WorldSnapshot` 只要 `Site`）、导航按钮（只要 `Site` + `Caravan`）**一行都没改** ⇒ 草稿/计划拿到的位置与导航与在途完全一致。补给行抽成 `AppendFoodRow`（三处共用，不足 2 天标红）。

### 19.48 草稿页：表体也换成 Radius 画法

`DrawDraftMain` 从"调 `DelegationDraftUI.Draw`"改成两趟 + `FlatScroll` + 新的 `DraftPass`：
描述 / `PreviewLabel` / **可点行**（模式 · 作战姿态 · 结束条件 —— 与右栏概览同一套"主行 + 副行 + 右端切换"版式）/ 数值步进 `Stepper`（按天数与产出配额两档）/ 补给中止勾选 / 「预期获得」列表 / 参与者列表（自绘勾选块 + 头像 + `DelegationUIUtility.PawnLine` 那一句）/ 汇总（已选 · 预计天数 · 姿态成算 · 疲劳 · 补给 · 载重）。

- **数据与校验一行都不重写**：全部走 `DelegationDraft` 的公开方法；原版主控台仍用 `DelegationDraftUI` ⇒ "同一份数据、两套壳"（双端准则），新增字段时两处都要看。
- 勾选仍用原版 `Widgets.CheckboxLabeled` —— 皮肤框架里没有勾选件（S15 那张「结束报告」开关同样是这么处理的、同样是这个理由）。
- 新增三个**共用件**（避免皮肤复刻公式）：`DelegationUIUtility.ModeLine(DelegationModeDef)`（草稿/在途/计划三处同一个模式文案）、`DelegationUIUtility.FatigueRiskLineOf(List<Pawn>)`（给"还没下单的一批人"算疲劳倍率，与在途那条同源）、`DelegationDraft.TryMassForecast(...)`（草稿的负重预测，原版 `DelegationDraftUI` 也改调它了）。
- 滚动：整列跟着主列一起滚（`draftScroll` 独立于 `mainScroll`），**不再需要内嵌滚动视图** —— 原版那份有内嵌滚动，是因为原版详情区只能靠它自己滚。

### 19.49 图三暴露的按钮重复

`DrawPlanMain` 里那一对（取消计划 / 选中该远行队）与全局底栏 `DrawPlanBottomBar` 完全重复 ⇒ 同一件事 4 颗按钮。
修法：**主列只负责"读"**，动作全在底栏（与在途详情同一个分工）；「继续决定」也从主列搬到 `DrawPlanBottomBar`（底栏按 `Window_Delegations.CanResumeDecision` 动态给 2 或 3 颗）。

### S19 验收步骤

① 皮肤里下单一笔委派 ⇒ 「待下达」页整页都是 Radius 件（模式/姿态/结束条件都是"点行即改"，右端有「切换」提示）；
② 同一页右栏出现「概览 / 位置 / 导航」三张卡；
③ 选中「前往中」⇒ 右栏同样三张卡（含位置缩略图），底栏只有一排按钮（最多 3 颗）；
④ 人已到位却没开工的计划 ⇒ 底栏出现「继续决定」，点它把待下达表单接回来；
⑤ 概览里点「模式」「结束条件」两行 ⇒ 直接弹出切换菜单（与在途那边同一手感）。
（②原本还写着"概览里没有进度条与累计作业"——**S20 已推翻**，见下。）

---

## S20 轮：条目一个不少（未知就写未知）+ 三个页面的分块对齐 + 文案回到世界观内 `[实现]`

**用户原话（2026-09-27，配待下达/在途两张对照截图）**：
①「前往中和待下达右边的概览每个项目都显示。数据不知道就提示无法估算。」
②「待下达和休息中/作业中的委派保持一致，显示总进度（算不出来就提示未知），现场物资（提示未知），参与者用和休息中/作业中的一样的，下面远行队和补给品内容也一致（不知道就提示未知）」
③「前往中也适用UI，不知道的信息就填未知；另外调查然后变更显示文案，需要保持在游戏的lore框架内；例如：物资在任务生成时就已确定，不需要生成地图。—— 不要提及生成和地图，这块可以直接删除」

### 19.50 「条目在不在」与「值可不可信」分开办

S19 立的是"宁缺不显示假 0"⇒ 草稿/计划的概览**少了几行**（进度、累计作业、剩余天数）。用户这轮否掉了那个做法：
**每一行都要在**（版面一致、玩家不用在两个页面之间重新找位置），**值拿不到就明写「未知」**。
⇒ 铁律更新为：**条目必须齐，值可以是"未知"** —— 仍然不许出现假的 0 或编出来的数字。

- `BuildDraftGlanceRows` / `BuildPlanGlanceRows` 现在与在途那 8 行**一一对应**（状态 / 剩余 / 模式 / 结束条件 / 参与者 / 载重 / 补给，草稿多一行姿态）。
  计划态拿不到的：剩余天数 =「未知（要等队伍抵达后才清楚）」；产物的增量 =「未知」；但**车队现在的占用/上限照给**（`载重 X / Y kg`）。
  草稿态拿不到的（说不出产物质量）也只是那一行写「未知」。
- 占位词统一为**「未知」**（S19 写的"无法估算"只剩 worker 自己给原因的那一处保留）。

### 19.51 三个页面同一套分块

| 页面 | 分块 |
|---|---|
| 在途（`MainPass`） | 描述 / **总进度** / **现场物资** / **参与者** / 流程 / **远行队** / **远行队补给品** |
| 待下达（`DraftPass`） | 描述 + 表单（模式/姿态/结束条件/步进/勾选）/ **总进度** / **现场物资**（未知）/ 预期获得 / **参与者** / **远行队** / **远行队补给品** / 姿态成算 |
| 前往中（`PlanPass`，S20 新增） | 描述 + 计划摘要 / **总进度**（未知）/ **现场物资**（未知）/ **参与者** / **远行队** / **远行队补给品** |

- **「前往中」原本只有 `PlanLines` 几行字**（图三那一片）⇒ 现在改成与在途同一个两趟 + `FlatScroll`（`planScroll`）+ 同款分块；参与者行直接调在途那一批件（`DrawPawnLine` + `PawnLine`），计划期**只读**（改人是"取消计划再下一条"）。
- **待下达的参与者行也换成在途那一批件**（`DrawDraftRosterRows` → `DelegationUIUtility.DrawPawnLine` / `DrawParticipantToggle`）：用户原话「参与者用和休息中/作业中的一样的」，而"一样"的唯一判据是**两处调同一批函数** —— 自绘一套只会"长得像、细节不一样"。
- 草稿页新增 **总进度**（`c.Progress`，分母用预览区间 ⇒ `0/40 件` 不是假数字；尾注给"预计 X–Y 天"或"未知"）、**现场物资**（未知，带一句为什么）、**远行队**（距离开工未知 / 疲劳 / 补给品）、**远行队补给品**（同款列表）。载重与补给从主列移到右栏概览（与在途同一个分工）。

### 19.52 文案回到世界观内（本轮清单）

用户口径：**不要在玩家可见文案里提"生成""地图"这类引擎词**。逐条改掉（内部注释/DESIGN 不受此限）：

| 位置 | 改前 → 改后 |
|---|---|
| `Defs/RimDelegation_Delegations.xml`（搜刮 description） | 删掉整句「物资在任务生成时就已确定，不需要生成地图。」 |
| `DelegationWorker_TakeItemStash.PreviewLabel` | 「…要等生成地图时才会由原版掷出，不进图读不到…按原版同一口径（MapGen_DefaultStockpile…）掷定并写回该地点」→「…还没人到现场清点过…都要等队伍到了才知道…抵达时会按市价上限 N 银清点并登记」 |
| 同上（空态那两态） | 「已按原版同一口径掷过这份清单，结果为空」「（SitePart.things 与 ItemStashContentsComp 都是空的）」→「已经清点过这一处，确实是空的」「这一处本来就是空的」 |
| 同上 `EstimateUnavailableReason` | 「这份清单要等生成地图时才掷定（…按原版同一口径掷定）」→「这里到底藏了多少东西，要等队伍到了现场清点才知道」 |
| `DelegationWorker_RescuePawn.PreviewLabel` | 「要等生成地图时才由原版生成…按原版同一口径把这个人掷定并写回该地点」→「还没人到现场确认过…抵达时会确认这个人的身份并记住他」 |
| 同上（空态那两态） | 「已按原版同一口径掷定过这份目标」「（站点数据里既没有既存目标，地点组件上也没有挂人）」→「已经清点过这一处，确实是空的」「这一处本来就是空的」 |
| `DelegationWorker_Mining.InspectWarning` | 「存量由委派按 GenStep 的公式独立推算；原版只在生成地图时才掷格数，两者是同分布的两次独立掷骰」→「委派按矿脉的分布规律另行推算…实地开挖时还会按地图上那一份来算，两处各算一次」；「当前 Def 未开启 blockMapEntryAfterWorked」→「当前设置没开「动过之后禁止进入」」 |
| 同上 `PreviewLabel` | 「矿种未识别（SitePartParams.preciousLumpResources 为空）」→「矿种未识别 —— 这一处没有登记矿脉信息。」 |
| `DelegationEventDef.Apply` | 「该委派的目标规模不是掷定出来的量」→「这一处的规模本来就不是能变多的东西」 |
| `DelegationDraftUI` | 「worker 类找不到了（Def 被改名或删除？）」→「这条委派的数据不完整 —— 可能是相关模组被移除或改名了。」 |
| `Window_Delegations` / 皮肤左栏 | 「（本存档还没有委派结束过）」→「（还没有已结束的委派）」（两处） |
| 皮肤（草稿页总进度 tooltip） | 「存量已经掷定 ⇒ 精确规模」/「要等生成地图时才掷定」→「这一处的底细已经摸清了 ⇒ 按实际规模算的」/「要等队伍到了才清楚，所以分母给的是上限」 |
| 皮肤（草稿页 现场物资） | 「要等生成地图时才由原版掷出」→「抵达现场后才能真正清点（下面是这一趟预计能取到的产出）」 |
| 皮肤（概览/底栏 tooltip） | 「固定…不影响存档」→「只影响本机的显示偏好」；「不携带选择…存量已掷定」→「先不定人、模式、结束条件…看到的是实际规模」；「抵达后会生成一张表单」→「抵达后会出现一张表单」 |

### S20 验收步骤

① 待下达页：总进度（`0/N` + 预计天数或"未知"）/ 现场物资（未知）/ 参与者（与在途同款 V/X 行）/ 远行队 / 远行队补给品 **五块都在**；
② 前往中页：同样五块都在，且进度与现场物资写「未知」、参与者按已定的人列出（未定则写"抵达后再选人"）；
③ 右栏概览：三个页面的行数**一样多**，只差值（拿不到的一律「未知」）；
④ 全屏扫一遍玩家可见文案：没有「生成」「地图」（"进图/不需要进入地图"这类玩家语例外）、没有"掷定/口径/GenStep/组件名"；
⑤ 搜刮委派的描述里只剩"破门、清点、装车…即收工"那一句。

---

## S21 轮：书面语化清单（用户逐条给出"当前文案 → 建议文案"）`[实现]`

用户直接贴了一张逐条对照表（主信息面板 + 右侧面板），本轮**照字面实现**，一处没自作主张；表里没提的一律不动。

| 位置 | 改后 |
|---|---|
| 标题 | 不动（用户标注"已相对标准"） |
| 搜刮·描述（Def） | 「将物资藏匿点委派给远行队：队伍将在目标区域执行破拆、清点与装载作业，按日消耗补给；待物资清空或运力满载后结束任务。」 |
| 搜刮·现场情报 | 「现场情报未知：目标区域尚未勘察，藏匿点内物资的种类与数量需待队伍抵达后确认。」＋「委派下达后，队伍抵达时将按市价上限（1800银）进行估值与登记——清点结果即为实际获取物资。」 |
| 搜刮·概览未知 | 「未知：藏匿点物资需待队伍侦察确认。」 |
| 工作安排 | 模式 Def label「正常工作」→「**常规作业**」；心情 0 的一律改称「**不受心情影响**」（新共用件 `DelegationUIUtility.MoodLine`，四处同源）。乘号仍是 `×`（不是 `x`）——用户表里写的是 `x1`，但 `×` 是全项目既定写法，先保留，要他一句话就换。 |
| 勾选项 | **删掉**「无需设定：一直干到 X 为止」这一行（用户批注「！删除这个描述！」）——它与其上面那行「结束条件：搬空为止」重复。两端的结束条件行都只是不再画这句，档位本身不变。 |
| 进度 | Keyed `RimDelegationTabTotalProgress` 加冒号「总进度：」；草稿尾注改成「· 预计完成时间：X–Y 天」/「· 预计完成时间：无法估算」 |
| 现场物资 | 「未知——需抵达现场后方可确认；以下为本趟预计可获取的产出物。」 |
| 参与人员 | 「参与者」→「**参与人员**」：Keyed `RimDelegationTabParticipants`、两处皮肤概览行、`PlanLines` 里的那一行、编辑参与人员窗口标题、地点检视行 |
| 远行队 | 「距离开工：未知（**委派尚未下达**/委派尚未开工）」「**疲劳状态**：休息充足，事故伤害概率无额外加成」（`FatigueRiskLine` / `FatigueRiskLineOf`）；概览里的短行改成「疲劳：已选人员平均休息 100%」 |
| 概览·未下达 | 「尚未开工 · 无累计作业（**等待确认**）」 |
| 概览·载重未知 | 「未知：现场详情需待队伍确认」 |
| 补给行 | 「全部补给约可维持6.8天（1.8）之后腐烂」→「全部补给约可维持 **6.8（1.8）天**」（`FoodDaysLine` 自己拼，不再借原版 `DaysWorthOfFoodInfoRot` 的"之后腐烂"；单位"天"落最后） |

连带新增的共用件：`DelegationUIUtility.MoodLine(float)`（心情那句原本在 4 处各写一遍 —— 这张清单正是这么漏掉的）。

### S21 验收步骤

① 待下达页从上到下逐行对一遍上表（描述/现场情报/工作安排/结束条件/进度/现场物资/参与人员/远行队）；
② 结束条件为「取尽」时，那一行**不再**出现"无需设定：一直干到…"；
③ 概览里 5 行分别是「未下达（等待确认）」「未知（藏匿点物资需待队伍侦察确认）」「疲劳：已选人员平均休息 X%」「未知（现场详情需待队伍确认）」「全部补给约可维持 X（Y）天」；
④ 模式行显示「常规作业 · … · 速率 ×1」＋「不受心情影响」。

---

## S22 轮：主控台第四栏「流程」（皮肤侧四栏布局）`[实现]`

用户口径：「是否可以在左边的过滤器搜索，和中间的主体信息栏之间拆分一栏出来（可以扩大整体UI的大小），然后把流程放到新的栏位来？」

本轮只做**布局与搬家**（那半个没有开放决策的部分）：把「流程」块从主列搬进新栏。技能挂钩、采矿两岔流程、模拟战斗集成仍在提案里，见 `Doc/流程栏与技能挂钩-方案评估.md`，下一轮实装。

### 19.53 四栏布局：宽度账与"放不下就回三栏"

- 内容宽 `C = winW − 60`（`Verse.Window.Margin = 18` ×2 ＋ 皮肤 `Pad = 12` ×2）；左栏 `min(340, C×0.30)`、右栏 `min(320, C×0.28)`。
- `flowW = clamp(C − 左 − 右 − 2 − 440, 0, 300)`；**`flowW < 240` 就不画流程栏**，流程块回落到主列（＝S17 及以前的现状）—— 窄屏与老存档几何永远不会出现"挤到读不出来"的栏。
- 主列下限 440 的由来：参与者表头三个按钮 `86+92+100+12 = 290` 加上「参与人员（N）」标签约 80；现场物资行（名称 ＋ 右对齐明细）更长。
- 流程栏只在**选中的是一条在途委派**时出现（待下达草稿 / 历史记录 / 前往中计划 / 空态都没有流程可讲 ⇒ `flowW = 0`，主列拿回全部宽度，不留空栏）。
- `Window_Delegations` 默认尺寸 1180×700 → **1490×760**；`RimDelegationSettings.CurrentGeomVersion` 1 → 2 —— 不作废旧几何的话，老玩家存档里的 1180 会让流程栏**永远走回落分支**、根本看不到新栏。

### 19.54 内容只有一份：`FlowPass`

流程块（`c.Section("流程")` ＋ 各 `DelegationStageRowKind` 的画法）抽成 `DelegationConsoleSkin.FlowPass(Cursor, Delegation, Site)`：

- 流程栏（`DrawFlowColumn`）画它；主列在 `flowShown == false` 时画它。**两处互斥**，`flowShown` 是唯一开关 ⇒ "同一条信息画两遍"在结构上不可能发生（重排 ≠ 删信息）。
- 流程栏自带滚动（`flowScroll`，两趟量高 ＋ `FlatScroll`，与主列 / 右栏同构）；换选中目标（`flowOwner` 变化）时滚回顶部。
- 条仍复用 `Cursor.Progress` → `UIKit.Flat.Bar`（用户要求"用同页面有的进度条"），本轮**没有新造控件**。
- 原版主控台**不动**（S8-c 拍板：三栏/四栏是 Radius 皮肤专属）；双端准则仍由数据源（`DelegationUIUtility.StageRows`）保证。

### S22 验收步骤

① 打开主控台（选中的是一条在途委派）：从左到右应是 **左栏 / 流程 / 主列 / 右栏** 四栏，流程栏约 300 宽、主列约 468；
② 同一时刻**主列里不再有「流程」块**（只有流程栏里有），且流程栏能独立滚动；
③ 把窗口拖窄到约 1380 以下：流程栏整栏消失、流程块回到主列原来的位置（信息一条不少）；
④ 切到「待下达」草稿页 / 「历史」记录页 / 「前往中」计划页：流程栏消失、主列变宽（不留空栏）；
⑤ 老存档首次打开：窗口尺寸变成 1490×760（`geomVersion` 作废旧几何生效）。

### S22 未决

① 技能挂钩（`DelegationPhaseDef.skillDef` ＋ 段耗时 ÷ `skillFactor`）与「最佳技能选人」；
② 采矿两岔流程（「有敌情」阶段级条件：甲 `requireThreat` / 乙 双流程 `flowPhasesHostile`）；
③ 模拟战斗集成（含"搜集战利品"的数据源，用户已明确留白）；
④ 窄屏下的**窄条时间轴**（只画节点、文字进 tooltip）—— 本轮先做成"整栏消失"，S2 方案待拍；
⑤ 流程栏拖拽分隔条（原版与 RadiusUI 都无现成控件，要自绘）。

### 19.55 不可打断的固定段 · 小队位置 · 「还没开始」的将来时

用户原话：「有部分固定流程需要设定为无法打断，例如 正在侦察环境的时候，不会因为时段进入了休息而打断…」「流程上方添加一个虚拟的小队位置（外围，矿点营地，矿点/藏匿点）」「如果抵达的时候不在工作时间，则小队在外围休息。这种情况下，下面的侦察环境应该修改成 Aemeath（射击 无火 10）将带队侦察环境 (0h / 1h)，下面的随机描述暂时不显示」。

- `DelegationPhaseDef.uninterruptible`（XML：侦察 / 移动 / 破门 = true）：`TickDelegation` 的工时门控为它让路 —— 出了窗口也**照常推进**，并且照计 `ticksWorked`（醒着干活 ≠ 休息）。判据 `DelegationFlow.UninterruptibleActive`。
- `DelegationPhaseDef.squadPosition` ＋ `DelegationDef.workSquadPosition`：小队此刻的虚拟位置（外围 / 藏匿点内部），由 `DelegationStageList.SquadPosition(d)` 取"最后一个走过（含当前）的段"的位置；一个都没写 ⇒ 不显示这一行。
- 新行类型 `DelegationStageRowKind.Position`：画在「流程」标题**之上**，两端共用（原版 `DelegationUIUtility.DrawStageRows` / 皮肤 `FlowPass`）。
- `DelegationPhaseDef.skillDef` ＋ `DelegationStage.executor / executorSkill`：配了技能的段，执行者 = **技能最高者**（`PickExecutor`，并列取名单里靠前者），标题行组成 `名字（技能 无火/火/双火 等级）`。激情**用文字**而不是原版火苗贴图 —— 两端各画一遍贴图迟早分叉，而这三个词玩家在人物面板上早就见过。⚠️ 技能当前只决定"谁去做 ＋ 怎么显示"，**不决定耗时**（耗时挂钩是下一期）。
- `DelegationStage.pendingStart / pendingText`：小队在休息、这一段一点都还没走 ⇒ 标题改将来时（`将带队侦察环境`）并且**不显示旁白**（没发生的事不编话）。

### 19.56 清单门控 · 位置卡时间 · 四个区域可折叠

- `DelegationDef.hideItemsUntilPhase`（搜刮 = `RimDelegation_Phase_StashBreach`）＋ `DelegationFlow.PhaseDone`：**破门之前不给看清单** —— `TakeItemStash.PreviewItems`（开工前的「预期获得」）与 `ProgressItems`（作业期的「现场物资」）都返回 null。UI 本来就是"读不到就不画"，所以草稿页的说明行随之改成「未知——要等「破门」之后才能确认现场有什么。」（措辞从 Def 的段名拼，改 XML 会跟着变）。
- 右栏「位置」卡新增两行：原版 `GenDate.DateFullStringWithHourAt(TicksAbs, Find.WorldGrid.LongLatOf(tile))` 的游戏内时刻 ＋ 「距天黑 / 距天亮 X 小时」（昼夜边界照原版 `ThoughtWorker_IsNightForNightOwl` 的 23:00 / 6:00 —— 世界地图上没有地图，`GenCelestial` / `WeatherManager` 都用不了）。
- 四个区域（左栏 / 流程 / 主信息 / 概览）各自可折叠：开关统一画在顶端 22px 的**列头**里（四列内部标题行结构完全不同，逐个改造会四处分叉），折叠后收成 26px 竖条；状态存本地 Mod 配置 `RadiusUISkinSettings.collapsed*`（不进存档）。主列折叠时把它让出的宽度给流程栏（没有流程栏就给左栏，避免中间留一块空白）；**流程栏折叠 = 流程块回到主列**（与"宽度不够自动回落"是同一条路径）。

### S22 第二批验收步骤

① 抵达时不在工时窗口：流程栏顶部显示「位置：外围」，当前段写成「Aemeath（射击 无火 10）将带队侦察环境（0h / 1h）」，且**没有**旁白那一行；
② 侦察 / 移动 / 破门走到一半跨过收工时刻：进度**继续走**（不再停在 0% 等第二天），「休息中」不出现；
③ 搜刮：草稿页不再有「预期获得」列表，现场物资说明行写「未知——要等「破门」之后…」；破门完成后作业期的现场物资列表正常出现；
④ 右栏位置卡多出「5501年 春 3日, 14时」与「距天黑 X 小时」两行；
⑤ 列头四个「－」可把四列分别收成竖条，再点「＋」展开；流程栏收起后流程块回到主列。

### 19.57 S22 两处截图 bugfix

用户报告：「只有待下达的时候，左边有显示 bug（…请搜索关键词）」「流程的折叠按钮，按下之后进行了折叠，然后没有展开的按钮了」。

| # | 根因 | 修法 |
|---|---|---|
| 1 | 左栏"没有匹配"那一行的判据是 `visible == 0 && planned == 0 && history == 0` —— **漏了草稿**，于是一张待下达的表单就会命中；而且它画在**写死的 `y = 4`**（正是组标题的位置）⇒ 一命中必然压在「待下达」标题上叠字；措辞也不对（没有搜索词时说"请调整搜索关键词"） | 判据把草稿算进去（`drafts.Count > 0` 也算有内容）、**只在真有搜索词时**才出现、并且画在内容之后（当前 `y`）；`contentH` 相应 +22 |
| 2 | 流程栏折叠后 `flowRect.width == 0`，而列头开关画在 `flowRect` 里 ⇒ 收起来就再也点不到 | 手动折叠时额外留一个 26px 的**列头槽位**（左栏右边）专门放「＋」；只有"因窗口太窄自动回落"才不给开关（那种情况给了也展不开） |
| 3 | 顺手修：选中**待下达表单**时状态行显示「待下达 1 · 共 0 项 · 当前第 1 项」—— 草稿不在 `visible` 里，`SelectedIndex()` 于是返回 0 | 选中草稿时按草稿自己的数量与序号报（`DraftList().IndexOf(selectedDraft)`） |

验收：① 只有一张待下达表单时，左栏不再出现「…请调整搜索关键词」，标题行干净；② 输入一个匹配不到的搜索词时才出现那一行，且位置在各组之下；③ 流程栏点「－」收成竖条后，左栏右边仍有一个「＋」能展开它。

### 19.58 清单被门控时保留块头：「现场物资（? 类）」

用户口径：「当现场物资还没有透露给玩家的时候，左边应该显示一个问号」「是否可以也添加现场物资（? 类），然后下面是空的」。

`hideItemsUntilPhase` 生效时 worker 返回 `null`，UI 原本"读不到就不画"⇒ 那一块**整块消失**，玩家会以为"这条委派根本没有物资这回事"。现在：

- 新增单一判据 `DelegationUIUtility.ItemsHidden(Delegation d)`（＝ `hideItemsUntilPhase` 那一段还没走完），两端共用。
- `ProgressItemsHeader` 在门控期间返回 **`现场物资（? 类）`** —— 市价合计与"该地点存量"**一概不写**：它们是同一条信息的其余部分，只把数字换成"?"会读成"已经知道了、只是值未知"。
- 皮肤主列（`MainPass`）与原版主控台详情（`Window_Delegations`）都改成"**照画块头、下面留空**"；排序按钮也不画（没有行可排）。

验收：破门之前，主信息面板里有「现场物资（? 类）」一行、下面是空的（原版主控台同样）；破门完成后那一行变回「现场物资（N 类）· 市价合计 …」并列出清单。

### 19.59 折叠时的宽度归属：左栏 → 流程栏

用户口径：「左栏收起的时候，空间是否可以给到流程」。

原实现里左栏一折叠，让出的宽度被**主列**吃掉（主列总是拿剩下的全部）。现在：左栏折叠时把 `listBaseW − StripW` 加进流程栏的宽度上限（`flowCap = FlowMaxW + 让出的宽度`）⇒ 放宽的那部分**给流程**（旁白折行更少），主列回到它的标准宽度（约 468）。

- 右栏折叠**不**这么做（右栏的场合是"看大图/点操作"，宽度留给主列更合理）。
- 主列折叠仍按原规则把它让出的宽度给流程栏（没有流程栏就给左栏）。
- 只在"流程栏本来就显示"时才生效（`flowW` 仍受"放不下就回落到主列"那条约束）。

验收：选中一条在途委派 → 点左栏列头的「－」→ 流程栏明显变宽（约 614）、主信息回到原宽；再点「＋」恢复原样。

### 19.60 休息期间"被暂停的那一段"怎么显示（用户截图 + 措辞普查）

用户口径：「休息的时候，被暂停的行为需要修改文案（例如这个场景需要修改为等待搜刮中），并且不显示下面的随机描述。请帮忙检查其他的是否合适」。

**根因**：`pendingStart` 的判据原来写的是 `elapsedHours <= 0`，而**主作业段的 `elapsedHours` 是累计作业时长**（截图里是 2h，来自前面三段固定流程）⇒ 判据失效，休息时照样写「● 全队 搜刮中（0/1 件）」并把工作旁白也画出来。改为新增 `DelegationStage.untouched`：**前置/收尾段看 `phaseTicks`、主作业段看 `d.Progress`**。

**改法**：

| 情形 | 现在显示 |
|---|---|
| 休息 + 该段未开始（主作业段） | `● 全队 等待搜刮中（0/1 件）`（进行中措辞加"等待"前缀，用户给的原话） |
| 休息 + 该段未开始（固定段） | `● Aemeath 将带队侦察环境（0h / 1h）` |
| 休息 + 该段已走了一部分 | 标题保持进行中措辞，**旁白一律不显示**（`resting` 标记，无论有没有进度） |
| 休息 + 条尾 | `预计还需` → **`开工后还需 Xh`**（普查发现：`etaHours` 算的是"还要多少**作业**时间"，写"预计还需 0.2h"会被读成"还要等 0.2 小时"，实际要等到明天） |

**普查其余各项，确认合适、不动**：休息置顶行「休息中（距开工 X 小时）」+ 休息旁白（S16 拍板）；停摆 `停摆中 · 剩余 Xh`（条染红，S15 三期）；玩家暂停期间整个流程块**一个字节不变**（S14 §12 拍板）；事件留痕行；位置行。

验收：抵达时不在工时窗口（或中途进入休息）→ 当前段写「等待搜刮中」，**下面没有旁白**，条尾写「开工后还需 Xh」；开工后恢复「搜刮中（…）」+ 旁白 +「预计还需 Xh」。

### 19.61 腐烂半句加单位：「(2)天之后腐烂」

用户口径：「Enh: 这个修改为 (2)天之后腐烂」（截图里补给品行尾巴写着 `银 (2)之后腐烂` / `银 (11.8)之后腐烂` —— 光一个"2"读不出是几天）。

原版 `DaysWorthOfFoodInfoRot` 的中文键是 `{0}之后腐烂`，而调用方传进去的参数**自带右括号**（`$"{tillRot:0.#})"`），所以那个 `)` 是参数的一部分、键里没有。修法：**把单位塞进参数**——`RotPhrase` 现在 Format 出 `"2)天"` 再交给键 ⇒ 成品 `(2)天之后腐烂`。好处是既不用改原版 Keyed、也不用把整句重拼一遍（汉化照旧从原版拿）。取不到汉化时的兜底自拼句也同步成 `({0:0.#})天之后腐烂`。

⚠️ 注意这条只作用于**逐件明细**（`CaravanFoodItems` 行尾）；`FoodDaysLine` 那一行是 S21 用户书面语清单定的「全部补给约可维持 6.8（1.8）天」——单位本来就在最后，不受影响。

验收：远行队补给品明细行尾巴显示 `银 (2)天之后腐烂`。


---

## S23 轮：「有敌情」两岔流程 + 事件原语化 `[实现]`

用户口径：「关于 有敌情分支怎么实现」。四项拍板（2026-09-27）：**甲B（段级条件 + 开局冻结）**、
**布尔两档**、**交战失败走 Abort**、**本期含「战斗评估段搬进流程」**（「搜集战利品」仍留白）。

### 19.62 事件原语化：`DelegationEffectDef` + `DelegationConditionDef`

- **动机**：在此之前每条有机制的事件都是一个 C# 子类（`DelegationEventDef_CaveIn` /
  `_BonusYield` / `_Setback` / `_Mood` / `_PawnAccident`，各写一套 `CanFire` / `ChanceMultiplier` / `Apply`）。
  而写 C# 的代价不是多写几行，是**退游戏 → 编译 → 部署 → 重启**（Hot Reload mod 只热重载 Def，
  不重载程序集）⇒ 拆成原语之后，改数值/改文案只动 XML、存盘即生效。
- **两者都是普通类、不是 Def**：它们以 `List<T>` 的内联元素出现
  （`<li Class="RimDelegation.DelegationEffectDef_Stall"><hours>1~4</hours></li>`）。
  这个写法本项目已经在用（`Patches/RimDelegation_SiteComps.xml` 给 Site 追加
  `<li Class="RimDelegation.WorldObjectCompProperties_Delegations" />`），而 **Def 类型的列表元素走的是
  "按 defName 查表"那条路** —— 混用会出现"内联出来的 Def 不进 DefDatabase"。所以原语与 `CompProperties` 同类。
- **三个挂点**（`DelegationEventDef` 上）：`conditions`（全满足才触发）/ `blockers`（任一满足即否，
  写否定条件用）/ `chanceMultipliers`（相乘成动态概率倍率）。
  ⚠️ `blockers` 在 `Apply` 里**再查一次** —— DEV 按钮会绕过静态闸门（老类在自己 `Apply` 里复检是同一个道理）。
- **已迁移的 5 条事件**（旧 C# 类**全部保留**，可随时切回去）：
  | 事件 | 条件 | 效果 |
  |---|---|---|
  | 塌方 | `OreUnitsAtLeast(5)` | `LoseOre(0.2~0.5)` + `Stall(1~4)` |
  | 发现富矿脉 | `DepositNotDepleted` + `ScaleIncreaseAllowed` | `GainScale(…原公式逐字保留…)` |
  | 作业受挫 | — | `Stall(3~8)` |
  | 队伍闹别扭 | `ParticipantsAliveGreaterThan(3)` | `Thought(EventSetback)` |
  | 工伤 | `AnyAbleParticipant` | `ChanceMultiplier: FatigueMultiplier(0.7→×4)` + `Damage(Blunt 6~12)` |
- 文案：每条效果自带 `resultText` 模板（`{0}` 等占位符含义见各子类注释），宿主按顺序拼；
  塌方因此仍然是"一句话 + 一句话"，工伤仍然带伤亡明细与疲劳注脚。
- `ModBoot` 自检（S23）：① 事件既无 `effects` 又无 `flavorLines` ⇒ 警告（触发了也什么都不会发生）；
  ② `hideItemsUntilPhase` 指向带 `requireThreat` 的段 ⇒ 报错（没守军的地点永远走不到那一段，清单会永久锁住）。

### 19.63 段级条件 + 开局冻结（甲B）

- `DelegationPhaseDef.requireThreat` / `requireNoThreat`（**布尔两档**，互斥时 `ConfigErrors` 报错）。
- `DelegationFlow.Freeze(def, st, site)`：在 `StartDelegation` 里紧跟 `captureItemLedger` 之后跑一次，
  判据 `ThreatAssessmentEntry.HasThreat(site)`（读存档里的 `SitePartParams.threatPoints`，不需要生成地图）。
- **为什么必须冻结**（两条结构约束，不是风格选择）：
  ① 段表缓存挂在 **Def** 上（`DelegationFlow.For` 的 `def.cachedFlow`），而敌情是**每地点**的
     ⇒ 条件一旦进段表，段表就得"每条委派一份"；
  ② 进度只记**下标**（`preludeIndex`），存档里没有"当时是哪张表" ⇒ 段表随状态变化会指到别的段。
- 实装：`DelegationFlowState.activePrelude / activeSuffix`（defName 清单；**null = 这些段没有任何条件**，
  省一份存档数据、老存档语义也逐字不变）+ `DelegationFlow.PreludeFor(st) / SuffixFor(st)`（**唯一读表入口**）
  + `DelegationFlowState.Resolve`（**故意不缓存**：热重载会重建 Def 实例，缓存住旧实例等于"改 XML 没反应"）。
- 读表点全部改造：`Delegation.cs` 的 `FlowRemainingTicks`（ETA / 计划天数 / 超时都靠它）、
  `DelegationFlow.cs` 的推进与判定、`DelegationStageList.cs` 的 `Build` 与 `SquadPosition`。

### 19.64 段进入钩子与「交战」段

- `DelegationPhaseDef.onEnter`（效果原语清单）+ 宿主 `WorldObjectComp_Delegations.RunPhaseEnter`。
  钩子放在宿主里而不是 `DelegationFlow` 里：它要动的东西（留痕、中止、跑战斗）全是宿主的职责。
- 记号 `flow.enteredPhase`（**会存档**）：交战钩子会真的打一场，读档后重跑等于白送一场战斗并再施一次伤亡。
  钩子抛异常也**不会**变成"每 tick 重试"（先记后跑）。
- 「交战」= 进入该段的那一 tick 跑一次 `CombatSimulator.Simulate`（与营救的清场同款；
  这 1 小时是**叙事耗时**，不是"战斗模型跑了一小时"）。战场与威胁评估面板**同源**
  （同一个 `CombatSceneFactory.Build`）⇒ 预告即契约（§19.12）仍然成立。
- 失败 ⇒ `d.flowAbortReason` ⇒ 宿主立刻 `Abort`（用户拍板；与营救"带伤员撤"同一条路）。
  结果摘要存 `d.flowCombatResult`（用户留白的「搜集战利品」段将来要读它）。
- 段钩子的文字走**同一条**事件留痕通道（`Delegation.LogPhaseNote` + `DelegationEventLogEntry.phaseLabel`）
  ⇒ 流程块的事件行、结束报告、历史记录三处自动可见，不必为"段的结果"再开第二套显示。

### 19.65 采矿的两岔流程（纯 XML）

| 段 | 类型 | 小时 | 技能 | 小队位置 | 条件 |
|---|---|---|---|---|---|
| 环境侦察 | 前置 | 1 | 射击 | 外围 | `requireNoThreat` |
| 战术侦察 | 前置 | 1 | 射击 | 外围 | `requireThreat` |
| 战斗评估 | 前置 | 1 | 智识 | 外围 | `requireThreat` |
| 交战 | 前置 | 1（叙事耗时） | 不挂钩 | 矿点 | `requireThreat` + `onEnter: ResolveCombat` |
| 建立营地 | 前置 | 0.5 | 建造 | 矿点营地 | —（两岔都有） |
| 〔主作业〕开采 | 主作业 | — | 采矿 | 矿点 | — |
| 撤离 | 收尾 | 2 | 无 | 外围 | — |

- **无威胁** = 环境侦察 → 建立营地 → 开采 → 撤离，固定 **3.5h**；
  **有敌情** = 战术侦察 → 战斗评估 → 交战 → 建立营地 → 开采 → 撤离，固定 **5.5h**。
- 旁白池沿用老规矩（**单人 > 驮兽 > 有敌情 > 默认**）。战术侦察/战斗评估/交战三段**天然只在有守军时出现**，
  所以正文写在 `ambientLinesHostile`；环境侦察另留一组"有守军池"当安全网
  （老存档没有冻结清单时它也会在有守军的情况下跑到 —— 池里写了没发生的事就是 bug，S14 的教训）。
- ⚠️ **加流程 = 加时间**：前置段期间 `worker.Tick` 完全不跑，并经 `FlowRemainingTicks` 计入 ETA /
  计划天数 / 超时，所以这两条流程会直接把整单工程往后推。

### S23 验收步骤

① **无威胁矿点**：开工后流程栏显示「位置：外围」「环境侦察（0h / 1h）」，**没有**战术侦察/战斗评估/交战；
   固定 3.5h 后开始开采；
② **有威胁矿点**：流程栏依次出现 战术侦察 → 战斗评估 → 交战 → 建立营地 → 开采；
   进入「交战」的那一刻，留痕里出现一条以「交战」为名的记录（含战斗结果与伤亡）；
③ **交战打输** ⇒ 委派**立刻中断**（不是"完成"），信件/报告里能看到"交火失利…带着伤员撤了"；
④ **存读档**：冻结出来的段表不丢；已经跑过的「交战」段**不会重跑**（读档后不会出现第二条「交战」留痕）；
⑤ **老存档**在途委派（没有冻结清单）行为与 S22 逐字一致；
⑥ **事件侧**：塌方/富矿脉/作业受挫/闹别扭/工伤五条行为不变（现在由 XML 组合驱动）。
   改 XML 里的数值或文案，**存盘即生效、不用退游戏**。

### S23 未决

| # | 项 | 状态 |
|---|---|---|
| 1 | 「战斗评估」段的**段内快捷入口**（当下评估面板仍只有两个入口：委派草稿页按钮、地点右键/gizmo）与「我自己来（瞬间完成、0 XP）」 | 需要**双端 UI**，下一期 |
| 2 | 「搜集战利品」段与其数据源 | 用户明确留白（等模拟战斗的数据源一起定）；`flowCombatResult` 已先存下来 |
| 3 | 段耗时 × `skillFactor` 的技能挂钩（现在 `skillDef` 只决定"谁去做 + 怎么显示"） | 下一期，见 `Doc/流程栏与技能挂钩-方案评估.md` §4 |
| 4 | `DelegationWorker_DataDriven`（"想快速配置别的 mod 的事件点"） | 下一期 |
| 5 | 窄条时间轴、拖拽分隔条 | S22 遗留 |

---

## S24 轮：主信息拆三段（作战任务 / 收集任务 / 远行队信息）`[实现]`

用户口径（原话）：「委派UI 主信息内部拆分一下，拆分为作战任务（最上面） - 收集任务（中间）（开采/搜刮）
- 远行队信息（最下面）；Brainstorm一下作战任务需要展示的信息。（下达委派里面也要应用相同的）」
四项拍板：「**1 恒常、2 乙、3 不含、4 原话**」。

### 19.66 三段是「按信息分类」，不是「给委派分类」

- 采矿（有敌情那一趟）与物资藏匿点（`OpportunitySite_ItemStash` 的 `siteThreatChance = 0.85`）
  都**同时**有作战内容与收集内容 ⇒ 三段不是三选一，而是各自按内容出现。
- 因此「作战任务」**恒常**（用户拍板）：没有守军时写一行「此地没有守军 —— 本趟不涉及作战。」
  —— 整段消失会被读成"这条委派漏做了作战评估"（S17 准则同源）。

### 19.67 作战任务段：带键缓存 + 手动「重新推算」（方案乙）

- `DelegationThreatSummary`：**事实**（威胁点数 / 部件数 / 是否含未探明 / 能否无地图评估 /
  敌方编队摘要 / 我方可战与被排除人数）+ **成算**（`Forecast.Run` 200 次蒙特卡洛）。
- 缓存键 = 地点 + 车队 + 姿态 + 参与者名单（`thingIDNumber` + 每人**健康粗档**）。
  键一变下一帧自动重建 —— 人受伤/倒地之后成算本来就该跟着变。
- ⚠️ 两条都贵：编队要真的 `GeneratePawns` 一遍、成算是 200 次模拟，而主列是**每帧重绘**的
  ⇒ 成算只认「重新推算」按钮（用户拍板方案乙）；主列与草稿页每帧只读缓存。
- **单一来源**：编队分组画法搬到 `DelegationThreatSummary.RosterLines(scene, mine, detailed)`
  （面板 `detailed = true`、主列 `false`），`Dialog_ThreatAssessment` 改调它 ——
  它自己那份 `SameRosterLine` / `AppendRosterLine` 已删除，免得出现"面板 ×7、主列 7 行"。
- **顺手堵的一个隐患**（反编译依据）：vanilla 只在"挑 group maker"那一小段 push/pop 种子
  （`PawnGroupMakerUtility.TryGetRandomPawnGroupMaker` 里 `Rand.PushState(parms.seed)` / `PopState`），
  **真正生成 pawn 的那一段没有** ⇒ 在 `ThreatRosterFactory.GeneratePawnGroup` 外层自己再包一层
  `Rand.PushState(seed)` / `Rand.PopState()`。不包的话两个后果：①"同种子逐人一致"只是碰巧；
  ② 从 UI 调它（现在首次构建战场就可能由**绘制**触发）会吃掉世界随机数流。

### 19.68 四个绘制点 + 流程的归属

- 重排的绘制点：原版主控台详情、原版草稿页（下达委派）、皮肤主控台主列、
  皮肤草稿主列，外加皮肤的「前往中」主列（S20 就要求三个页面同一套分块）。
- 段名**只有一份**：`DelegationUIUtility.SectionCombat / SectionCollect / SectionCaravan`
  （用户给的原话），两端的段头**画法**各自一份（原版 `DrawSectionHeader`，皮肤 `Cursor.Section`）。
- 归属规则（两端一致）：**「模式」= 作业作息 ⇒ 收集任务；「姿态」= 作战 ⇒ 作战任务**。
  「结束条件 / 补给耗尽中止」是"这次收集怎么收工" ⇒ 收集任务。
- 「流程」**不归三段**（用户拍板）：皮肤仍画在第四栏（窄屏回落时画在主列**末尾**）；
  **原版**没有那一栏，所以移到主列末尾当独立块 —— 重排 ≠ 删信息。
- 「前往中」只给**事实**、不给成算：参与者选了"延后决定"时还没定下来，
  这时候给成功率等于给一个假承诺（§19.12 同一条理）。

### S24 验收步骤

① **无威胁矿点**：主信息第一段是「作战任务」+「此地没有守军 —— 本趟不涉及作战。」；
   第二段「收集任务」（模式 / 结束条件 / 总进度 / 现场物资）；第三段「远行队信息」（参与者 / 远行队 / 补给品）；
② **有威胁矿点或物资点**：作战任务段显示 威胁点数 · 威胁部件数 ·（含未探明的威胁）· 编队（`海盗 ×7`）·
   我方可战 N 人 · 「成算：尚未推算（点「重新推算」）」；
③ 点「**重新推算**」⇒ 多出 成功率 / 撤退率 / 失败率 + 用时 P50·P90 + 伤员与阵亡的 P50·P90；
   不点则一直写"尚未推算"（切选中、开合窗口都不会偷偷重算）；
④ 「**查看评估**」打开既有面板，里面编队那一块与主列的分组口径一致（同一个 `RosterLines`）；
⑤ **下达委派**（原版草稿弹窗与皮肤草稿页）：同样三段；「姿态」在作战任务段、「模式」在收集任务段；
⑥ 原版主列**末尾**能看到「流程」块；皮肤仍默认在第四栏，把窗口拖窄到流程栏回落时才出现在主列末尾；
⑦ 有敌情的在途委派：作战任务段多一行「本趟流程包含「交战」段（尚未走到）」；
   打过之后变成「本趟已交战：…」（数据来自 S23 的 `d.flowCombatResult`）。

### S24 未决

| # | 项 | 状态 |
|---|---|---|
| 1 | 「重新推算」的实际耗时（首次还要构建战场 = `GeneratePawns`） | **未实测**；若体感卡就退到方案甲（主列只给事实，成算只在面板里） |
| 2 | 作战任务段不常驻我方编队明细（只在面板里） | 有意（主列只有约 468px）；要不要给个 tooltip 待定 |
| 3 | 历史记录页仍是旧版单块，没拆三段 | 用户只点名"主信息 + 下达委派"，历史页待他发话 |

---

## S25 轮：情报渐进披露 · 编队图标 · 参战人员 · 待命决策 · 可折叠段头 `[实现]`

用户 Feat 单（2026-09-27，原话照录）：
「UI 改动 - 作战任务，收集任务，远行队的 TItle 更加凸显。需要可以折叠」
「作战任务这块，在侦察完成前，守军不会向玩家揭露」
「作战任务里面单独添加参与人员（或动物）」
「完成侦察任务后，向玩家揭露敌人编队」
「敌人编队的显示是否可以更加详细，比如有图标」
「完成侦察任务后，先自动进行战斗评估」
「然后远行队进入休息状态，作战任务 UI 里面添加 进行交战 和 撤退选项」

### 19.69 情报渐进披露：`revealsThreat` + `ThreatRevealed`

- 新增 `DelegationPhaseDef.revealsThreat`（采矿的「战术侦察」= true）。
  判据 `DelegationUIUtility.ThreatRevealed(d)` 只看**冻结后的段表**：那一段走完了才算揭露；
  一个流程里一个都没带（老存档、无侦察段、无威胁那一岔）⇒ 一律视为已揭露，行为逐字不变。
- 未揭露时 `DelegationThreatSummary.Lines()` 只给一行「守军情报未明 —— 完成侦察后揭晓。」
  ⇒ **不是**把整段藏掉（S24 的"恒常"仍然成立）。
- ⚠️ **下达委派（草稿页）不受门控**：那是"派不派单"的依据（物资点 85% 有守军，人到了才发现打不过就晚了）。
  门控只作用于**在途**的作战任务段。

### 19.70 编队图标 + 参战人员

- `CombatUnitSnapshot.IconDef`（`ThingDef`）：人族/动物/机械族取 `pawn.def`、炮塔取炮塔 Def
  ⇒ 两端都用现成的 `Widgets.ThingIcon` 画，**不需要新控件**。
  刻意只存 Def 不存 Pawn：敌方 pawn 生成完立刻销毁（存 Pawn = 存死对象 + 写进存档）。
- 分组画法升级为结构化：`DelegationThreatSummary.RosterEntries(scene, mine)`
  （`RosterEntry.Short` 给主列、`.Detail` 给面板、`.icon` 给两端画图标），
  `RosterLines` 变成它的文本包装 ⇒ 面板与主列仍然只有一份口径。
- **参战人员**：`CombatSetup.OurPawns`（能打的**真 Pawn**，在 `CombatSceneFactory.Build` 里顺手收集）
  ⇒ 作战任务段能用 `DelegationUIUtility.DrawPawnLine` 画**头像行**（动物也在里面，它们本来就参战）。
  详见"被排除的成员"仍走 `OurNotes`。

### 19.71 侦察完成 ⇒ 自动评估一次

- `DelegationThreatSummary.Ensure(..., requestForecast)`：主列/草稿页传
  `ts.revealed && ts.hasThreat` ⇒ 侦察一完成就自动跑一次 200 次蒙特卡洛；
  `forecastAttempted` 保证**只自动试一次**（失败也不每帧重试），「重新推算」仍可手动重采样。
- 未揭露时不跑 —— 没必要为一行"情报未明"白跑 200 次。

### 19.72 待命与两颗决策按钮（流程闸门）

- 新增 `DelegationPhaseDef.pauseUntilSignal`（采矿的「交战」= `engage`）+ `DelegationFlowState.signals`
  （**会存档**：读档不会把"已经下令"忘掉）。
- 语义：流程**停在它跟前**（不进入、不触发 `onEnter`）；`Delegation.IsWorkTime` 因此为假
  ⇒ 队伍就地待命（照常计休息），`StatusWord` 显示「等待指令」、流程置顶行显示「待命（等待你的作战指令）」。
  ⚠️ 挂在唯一工时判据上，否则会出现"界面说待命、流程却往前拱"。
- 两颗按钮（两端都有）：**「进行交战」**= `comp.GivePhaseSignal(d, signal)`；
  **「撤退」**= `comp.OrderRetreat(d)` ⇒ **Abort**（「主动撤退：队伍放弃了这一趟」）。
  为什么撤退走 Abort 而不是"跳过战斗继续挖"：与营救的"带伤员撤"、交战失败同一条路
  （人撤回去、地点不销毁、卸完货能再来）；守军在旁边继续挖矿在叙事上说不通。
- 自检：`ModBoot.CheckS25FlowGates` —— 有段在等信号、却没有任何段揭露情报 ⇒
  「进行交战」永远不会出现、委派**永久卡死**，启动就报错。

### 19.73 三段可折叠 + 标题更凸显

- `RimDelegationSettings.collapsedCombatSection / collapsedCollectSection / collapsedCaravanSection`
  （本地 Mod 配置、不进存档，**原版与皮肤共用** —— 与 S8 的 `stashItemsExpanded` 同一规矩）。
- 原版画法：`DelegationUIUtility.DrawSectionHeader(rect, label, draw, SectionId)` ——
  底色带 + 暖色文字 + 下划线（**不动字号**：一动，四处调用点的高度预算全要跟着改），
  右端 `－/＋` 按钮；皮肤画法：`SectionCollapsible`（`Cursor.Section` + 同一个按钮），
  状态读写共用 `DelegationUIUtility.SectionCollapsed/ToggleSection`。
- ⚠️ 开关画在**段头自己身上**，而段头永远在 —— S22 那次把开关画进被折叠的列里，收起来就再也点不到
  （用户报过两次的坑，见 lesson）。

### S25 验收步骤

① 有威胁矿点开工：作战任务段**只有**「守军情报未明 —— 完成侦察后揭晓。」（编队一个字都不给）；
② 战术侦察走完 ⇒ 编队逐行出现（**每行一个图标**：人形/动物/机械/炮塔）＋「参战人员（N）」带头像行
   ＋ 成算**自动**出现（不必点「重新推算」）；
③ 战斗评估段走完 ⇒ 队伍**停在那里**：状态词「等待指令」、流程置顶「待命（等待你的作战指令）」、
   进度不动、休息值照涨；作战任务段出现「**进行交战**」「**撤退**」两颗按钮；
④ 点「进行交战」⇒ 下一 tick 进入交战段并结算（留痕里出现「交战」那条）；点「撤退」⇒ 立刻中断，理由「主动撤退」；
⑤ 三段的段头变成带底色的标题，右端有 `－/＋`：收起后内容消失、标题仍在、再点能展开；
   切到原版主控台/草稿页，折叠状态**保持一致**（共用一份设置）；
⑥ 存读档：待命状态、已下达的信号、折叠状态都还在（前两者进存档，折叠进 Mod 配置）；
⑦ 无威胁矿点：作战任务段仍然只有「此地没有守军」那行（不受本轮的揭露逻辑影响）。

### S25 未决

| # | 项 | 状态 |
|---|---|---|
| 1 | 「撤退」的语义 | 本轮 = **Abort**（放弃这一趟）；若你要的是"跳过战斗直接撤离、地点留着继续采"，说一声就改 |
| 2 | 自动评估的耗时 | 首次含建战场（`GeneratePawns`）**未实测** |
| 3 | 侦察段本身要不要也画成"正在侦察（未知）"的进度提示 | 现在只有一行"未明"，可再补 |
| 4 | 历史记录页仍未拆三段（S24 遗留） | 待发话 |

---

## S26 轮：上机反馈 8 条（S25 的 bug 与精修）`[实现]`

用户原文（截图 + 8 条）：
「1. 现在完成战术评估后会自动交战 -- 需要玩家手动执行」「2. 交战流程完成前，结果就出来了 -- 需要等待交战结束再出结果」
「3. 待下达状态的时候，敌方编队和威胁点数就揭露了 -- 不要揭露」「4. 同样地，在作业中的UI的时候，完成战术评估前就揭露了敌人」
「5. 暂时关闭一下流程的随机描述，有些不符合逻辑」「6. 双火/火/无火 是否可以添加图标」
「7. 战术评估到一半进入休息了 -- 有的流程不应该被打断，请检查」「图一报错」

### 19.74 待命被 `uninterruptible` 绕过（第 1 条，真 bug）

- **根因**：`TickDelegation` 的工时门控是 `!d.IsWorkTime(...) && !flow.UninterruptibleActive(...)`。
  待命时 `IsWorkTime` 确实是假，但"当前段"是**交战段**（`uninterruptible = true`），
  于是 `UninterruptibleActive` 为真 ⇒ 照常往下走 ⇒ `RunPhaseEnter` 触发 `onEnter` ⇒ **战斗自己打完了**。
- **修法**：`UninterruptibleActive` 在 `IsGated(st)` 时直接返回 false —— 等待中的段"还没开始"，
  它的不可打断性当然不成立；再加一道 `RunPhaseEnter` 的 `if (flow.IsGated(...)) return false;`。
- 顺带确认了正确行为：点了「进行交战」之后，`IsGated` 变假 ⇒ 交战段恢复不可打断 ⇒ **夜里也会打**（合理）。

### 19.75 战报提前剧透（第 2 条）

- 交战是"进入该段那一刻一次性结算"（§19.64），但**结果**属于"打完了"。
- 新增 `DelegationPhaseDef.deferNoteUntilDone`（交战 = true）+ `Delegation.flowPendingNote/Phase/Label`（**会存档**）：
  段钩子的文字先入队，`FlushPendingNote` 在 `flow.PhaseDone(...)` 成立时才写进留痕；
  `Complete`/`Abort` 里另做一次强制落地（要结束了，结果不能再烂在字段里）。

### 19.76 情报揭露点搬到了「战术评估」（第 3、4 条）

- **第 4 条是真 bug**：主列的编队行**压根没门控** —— 我 S25 只写了句"roster 会是空的"的注释，
  而 `Build()` 其实照样填 `roster`，于是「情报未明」那行下面列着 `海盗 ×7`。现在两端都真门控。
- **第 3 条**：待下达（草稿页）**也**不再揭露 —— `s.revealed = false` 由 UI 侧强制，
  连「重新推算 / 威胁评估」两颗按钮一起收掉（那两颗会立刻把情报倒出来）。
  ⚠️ 覆盖了 S19/S24 的旧取舍（当时草稿页显示守军摘要，理由是"派不派单要有依据"）——
  用户现在要的是"情报要靠侦察换来"，所以**地点右键/gizmo 那两个老入口没动**，只是草稿页不再给。
- **揭露点从「战术侦察」搬到「战术评估」**：侦察负责看、评估负责给结论，两者都完事才揭晓
  （对应第 4 条"完成战术评估前就揭露了敌人"）。

### 19.77 旁白总开关（第 5 条）

- `RimDelegationSettings.flowAmbientEnabled`，**默认关**；判据只有一处
  （`DelegationUIUtility.AmbientEnabled`），消费点两处（段旁白 `DelegationStageList`、休息旁白 `StageRows`）。
  关掉时**连掷定都不做**，不留"关了但存档里还在换"的痕迹；Mod 设置里有勾选框。

### 19.78 激情火苗图标（第 6 条）

- 原版贴图是公开静态字段：`SkillUI.PassionMinorIcon` / `PassionMajorIcon`
  （反编译确认内部从 `UI/Icons/PassionMinor`、`UI/Icons/PassionMajor` 载入）。
- `DelegationUIUtility.PassionIconFor(stage)` 是**取图唯一来源**（S22 当初改用文字就是因为怕两端各查一次路径会分叉，
  这次把"取哪张"收起来，两端只负责 `GUI.DrawTexture`）；`ExecutorSkillText` 不再写"无火/火/双火"，
  只给「射击 10」+ 前面那团火苗。

### 19.79 战斗评估不可打断（第 7 条）

- 「战术评估」补 `uninterruptible = true`。**全流程审计**（用户"请检查"）：
  侦察 / 战术侦察 / 移动 / 破门 / **评估** / 交战 = 不可打断（一旦开始必须做完）；
  建立营地 / 开采 / 撤离 = 可以按作息停（这三段本来就没必要"硬撑"）。

### 19.80 `RemovePawn` 刷屏（第 8 条）

- **根因**：`ThreatRosterFactory.Discard` 里调了 `Find.WorldPawns.RemovePawn(p)`，
  而这些 pawn 由 `GeneratePawns` 造出来、从未 spawn、也从未登记进 `WorldPawns` ——
  vanilla 找不到时会**逐次 `Log.Error`**「Tried to remove pawn … but it's not here.」
  （它记 error 而不是抛异常，所以外面那层 try/catch 挡不住）。
  S25 起 UI 会按需构建编队 ⇒ 这条错误被放大成刷屏。
- **修法**：删掉那一次显式移除，真正的清理只需要 `Destroy()`。

### S26 验收步骤

① 有威胁矿点：战术侦察 → **战术评估**走完之前，作战任务段**只有**「守军情报未明」，
   编队一行都不出现；评估走完 ⇒ 编队（带图标）+ 参战人员 + **自动**成算同时出现；
② 评估走完 ⇒ 队伍停住待命（「等待指令」），**不会**自动开打；点「进行交战」才进入交战；
③ 交战段进行中：只有「正在交火…」与进度条，**没有**战报；这一段走完（1h）才出现「交战：…」留痕；
④ 待下达（草稿页）：作战任务段只有「情报未明」，**没有**编队/威胁点数，也没有「重新推算 / 威胁评估」按钮；
⑤ 流程块的随机描述默认**不显示**（Mod 设置里可勾回来）；
⑥ 执行者那一行：「Aemeath（射击 10）」+ 前面一团**火苗**（双火两张）；
⑦ 战术评估不再被休息打断（跨过收工时刻也一路走完）；
⑧ Player.log 不再出现「Tried to remove pawn … but it's not here.」。

### S26 未决

| # | 项 | 状态 |
|---|---|---|
| 1 | 「撤退」的语义（S25 遗留） | 现 = Abort；要改成"跳过战斗继续采"说一声 |
| 2 | 待下达连"此地没有守军"要不要也藏 | 现在：有守军 ⇒ 未明；**没守军 ⇒ 直说**（那不算泄露情报，是"不适用"）。要一并藏起来也是一行改动 |
| 3 | 地点右键/gizmo 那两个老入口仍会显示守军摘要 | 用户本轮只点名"待下达"与"作业中 UI"，那两个入口没动 |

---

## S27 轮：参战勾选框 · 参与人员归收集任务 · 休息值角标 · 段头大字号与底框 `[实现]`

用户原话（截图 + 三条）：
「参与人员放到收集任务里面（因为显示的是采矿作业相关的）」
「作战任务里面添加一个勾选框，决定人物或动物是参加还是不参加作战，这几个框除了心情以外，额外添加休息值的显示」
「'作战任务'和'收集任务'，'远行队信息'增大字号，他们是否可以像右边的概览一样，添加底框」

### 19.81 「参与人员」归「收集任务」

- 做法：**移动段头、不搬内容** —— 在途主列里把 `③ 远行队信息` 的段头下移到「远行队行 / 补给品」之前，
  于是 `参与人员（V/X 名单）` 自然落进 `② 收集任务`；草稿页同理（人数说明 + 工具栏 + 选人列表都归收集任务，
  远行队信息只留"这一趟的代价"）。
- 四处都改了：原版主控台详情、原版草稿页、皮肤主控台主列、皮肤草稿页。
- 理由（用户原话）：这一列显示的是「采矿 4 · 速度 ×0.55 · 0.73 格/作业小时」这类**作业**信息。

### 19.82 「作战任务」里的参战勾选框

- 新增 `Delegation.noCombatPawns`（`Scribe_Collections` + `LookMode.Reference`，**会存档**）
  + `Delegation.FightsInCombat(p)` + `WorldObjectComp_Delegations.ToggleCombatParticipant(d, p)`。
- `CombatSceneFactory.Build(..., excludeFromCombat)`：被排除的人**照常进 `setup.OurPawns`**
  （否则 UI 上就再也勾不回来）但不进 `scene`，并留一行 note「「X」按你的设置不参加作战」。
- `DelegationThreatSummary` 的**缓存键**把这张名单算进去 ⇒ 勾一下下一帧自动重建战场与成算。
- 「参战人员（N 人参战 / 共 M 人）」每行前面是 `DrawParticipantToggle`，点行或点方块即切换；
  不参战的整行压暗。一个参战的人都没有时，「进行交战」**禁用**并给 tooltip
  （不拦的话点下去只会是"没人打 ⇒ 判负"）。
- 与"参不参加委派"是**两条轴**：可以派他去挖矿、但不让他打仗（动物尤其常见）。

### 19.83 人员行加"休息值"角标

- `DelegationUIUtility.DrawPawnLine` 现在从右往左排 **三个**角标：`[受伤][休息][心情] (i)`；
  `LabelRectFor` 的预留同步改成 `3 × (BadgeSize + BadgeGap)`（⚠️ 两处必须一起改，否则文字被角标压住）。
- 新 `DrawRestBadge`：取 `Need_Rest.CurLevelPercentage`，颜色与心情同一把尺子（高=绿→低=红），
  tooltip 写「休息 62%（疲劳：工伤风险偏高）」—— 它与工伤倍率同源，玩家能直接看出"谁快撑不住了"。

### 19.84 段头：大字号 + 底框

- **原版**：段头字号提到 `GameFont.Medium`，`SectionHeaderH` 26 → 30（字号与高度必须一起改）。
- **皮肤**：三段改用**与右栏「概览/位置/导航」完全相同的卡片**（`CardChrome.Card` +
  `RadiusFont.Scale.Section`）⇒ 天然同观感。⚠️ 底框必须先画、内容后画（S22 的教训：反了会被盖住），
  而段高只有画完内容才知道 ⇒ 高度在**量高趟**记进静态字段、绘制趟开头铺底（第一帧没有底框是正常的）。
- 收起态单独画一张"只剩卡头"的底框 —— 否则标题会跟着内容一起消失（S22 那个"收起来就找不到"的老坑）。

### S27 验收步骤

① 主信息里「参与人员（N 人）」现在在**收集任务**段内，作战任务段里是「参战人员（N 人参战 / 共 M 人）」；
② 点参战人员行前的方块（或点该行）⇒ 勾/叉切换、整行压暗、**成算立刻重算**；
   全部取消勾选 ⇒「进行交战」变灰并给提示；
③ 人员行右侧现在有三个小方块：受伤 / 休息 / 心情，悬停各有 tooltip（休息那个会写疲劳警告）；
④ 三个段头字号明显大于正文；皮肤侧它们各自带一张与右侧「概览」同款的底框（收起的段落只剩卡头）；
⑤ 存读档后"谁不参战"的选择还在。

### S27 未决

| # | 项 | 状态 |
|---|---|---|
| 1 | 底框目前只做了**皮肤主控台的三个段**；原版（流式两趟）与草稿页/前往中仍是"大字 + 底色带" | 要铺开说一声 |
| 2 | 「撤退」语义（S25 遗留） | 现 = Abort |
| 3 | 待下达连"此地没有守军"是否也藏（S26 遗留） | 现在直说 |


---

## S28 轮：流程块卡片化 · 作战/收集分组 · 火苗挪到技能名后 `[实现]`

用户原话（2026-09-28）：
「字体字号参考右边的概览，底框直接要有空隙」
「流程里面的双火/火要显示在技能名后，例如 智识 双火」
「流程拆分一下，拆分显示为作战的和收集任务的」
拍板（选项式答复）：字体/底框改的是**流程块**（不是主列三段）；分组采用**流程栏内分两组**，
组标题就叫「作战任务」「收集任务」；火苗之后**保留等级**。

### 19.85 流程块 = 与「概览」同一张卡

- **皮肤**：`FlowPass` 整块改用 `CardChrome.Card`（与右栏 概览/位置/导航 同一张卡），
  内容四边缩进 8px（用户要的"空隙"），正文行 13 → 15px（`Scale.Body`，概览正文同档），
  副行（旁白 / 条尾 ETA）仍 13px（概览副行同档），卡头用 `Scale.Section` 加粗（概览卡头同档）。
  卡头文字取 `Header` 行的 text（"流程"）—— 文字仍只有 `Rows` 一份，皮肤只是把它从"一行内文"提升成卡标题。
- 流程栏不再铺深色底板（否则卡里套卡）；只有"确实没有任何行"时才退回旧底板。
- ⚠️ 卡片必须先铺底再画内容 ⇒ 高度在**量高趟**记进 `flowCardH`、绘制趟开头铺底（与 S27 的 `SectionChrome` 同一套）。
- **原版**：流程块套 `DelegationUIUtility.DrawFlowBox`（白 6% 底 + 18% 描边，与三段段头那条底带同口径），
  内容缩进 `FlowBoxPad = 8f`；高度必须用**缩进后的宽度**量（`StageRowsHeight(rows, w - 2*Pad)`），两趟同一个式子。

### 19.86 流程分组：作战任务 / 收集任务

- 新 `DelegationPhaseDef.flowCategory`（`Combat` / `Collect`；空 = 按条件推断：
  带 `requireThreat` 或 `pauseUntilSignal` ⇒ 作战，其余 ⇒ 收集，判据 `InCombatFlow`）。
  采矿那三段（战术侦察 / 战斗评估 / 交战）已在 XML 里显式标 `Combat`，其余靠推断（= 收集）。
- `DelegationStage.combat` 带上归类；主作业段（开采 / 搜刮 / 营救）一律算**收集**。
- `Rows()` 现在把**阶段行先分两束**（作战 / 收集，各保持原先后）再拼回主列表，组标题用
  `DelegationStageRowKind.GroupTitle` + 三段段名的**同一份常量**（`SectionCombat` / `SectionCollect`）。
  什么时候不分组：这条委派的流程里**一个段 Def 都没有**（例：两条营救）⇒ 一个组标题都不插，画面与 S28 前逐字一致。
  为什么分组不会打乱时间顺序：现实里的段表恰好是"作战在前、收集在后"（采矿有敌情那条 =
  战术侦察 / 战斗评估 / 交战 → 建立营地 → 开采 → 撤离）。
- 已完成压成一行（超过 6 段）时**不拆组**（它是"已完成多少"的计数），归属按其中最后一段算。

### 19.87 火苗挪到技能名之后

- 用户口径：「双火/火要显示在技能名后，例如 智识 双火」⇒ 图标位置由造文字的
  `DelegationStageList.ExecutorHead(stage, out int iconAt)` **一并给出**（= 人名 + `（` + 技能名之后、
  等级之前），两端只负责"切两段、中间画图"（`row.iconAt`）。**不许**两端自己去找技能名的位置
  —— 改一次文案（多一个空格）那种算法就会静默错位。
- 新共用件：`DelegationUIUtility.HasInlineIcon / InlineIconSlot / DrawInlineIconLine`（原版画法）、
  皮肤侧 `Cursor.InlineIconLine`（画法各自、测量口径一致：前缀宽 + 14px 槽位）。
  栏太窄放不下时退回"火苗在最前"的旧画法（宁可位置旧，也不要把字切掉）。
- 渲染结果：`Aemeath（采矿 🔥🔥 4）正在带队侦察环境…`（等级保留，顺序与原「射击 无火 10」一致）。

### S28 验收步骤

① 皮肤主控台流程栏：整块现在是**一张卡**（圆角 + 边框），四边都有 8px 空隙，正文明显比之前大一档；
② 流程里能同时看到「作战任务」「收集任务」两个小组标题（采矿有敌情那条；无威胁 / 物资点只有收集那一组）；
   两条营救没有流程段 ⇒ 一个组标题都不出现；
③ 当前段的火苗在**技能名后面**：`Aemeath（采矿 🔥🔥 4）正在…`（无火的人没有图标；等级还在）；
④ 原版主控台流程块同样有底框 + 8px 空隙 + 两个组标题（字号仍是原版 Small）；
⑤ 窗口拉窄到流程栏放不下时，流程块回落到主列，卡片 / 分组 / 火苗位置都一样。

---

## S29 轮：段间空隙 · 交战留痕归「作战任务」 · 悬浮焦点 `[实现]`

用户原话（2026-09-28，截图 + 三条）：
「Bugfix: 主信息的 作战任务/收集任务/远行队信息 之间没有空隙」
「Bugfix: 图一的信息应该在作战任务里面」
「Enh: 验证是否可以采取焦点的做法，如果鼠标悬浮在流程，则展开流程，折叠左栏；如果鼠标悬浮在左栏，则展开左栏」

### 19.88 三段之间留空隙（皮肤）

- 三段是**卡片**（S27）：卡高 = 内容高 ⇒ 相邻两张卡的边直接贴在一起。现在每段内容之后
  `c.Gap()`（8px），且**刻意加在"段高之外"** —— 若算进段高，底框会把这个间距一起吃进去，看上去还是贴着的。
- 三个页面（在途主列 / 待下达 / 前往中）的每两个段之间都加了，最后一段与「流程」块之间也加（两块都是卡）。

### 19.89 交战结算的留痕归「作战任务」组

- 病根：段钩子的留痕走的是**随机事件同一条通道**（S23 的设计，报告/历史/角标都靠它），
  而 `Rows()` 一律把"最近一条事件"吊在流程块**最末尾** ⇒ 交战结果那行看上去挂在「收集任务」下面
  （那一刻正在开采），用户截图报的正是这个。
- 做法：给留痕加一格 `DelegationEventLogEntry.combatPhase`（**会存档**），来源 = `phase.InCombatFlow`；
  `QueuePhaseNote`/`LogPhaseNote` 各加一个可选参数把标记带过去（待公布的段结果也要带，
  所以多一个 `Delegation.flowPendingNoteCombat`，同样存档）。
- `Rows()` 里：这条留痕若来自作战段 ⇒ **排进「作战任务」组的末尾**；其余随机事件仍吊在最末尾
  （它们是"整趟的事"，不属于任何一组）。

### 19.90 悬浮焦点（左栏 / 流程栏）

- 用户要求：鼠标在流程栏 ⇒ 展开流程、收起左栏；在左栏 ⇒ 反过来。只影响这一对（主列/右栏不参与）。
- 实现三条纪律（`DelegationConsoleSkin.UpdateColumnFocus`）：
  ① **粘滞**：判据用**上一帧的实际列矩形**，不用"两栏都展开"的理想几何 ——
     后者会在两栏交界处自激（展开 → 形状变 → 鼠标出界 → 收起 → 又落回旧区，一帧一个样）；
  ② 矩形**含列头那 22px** —— 否则"移上去点列头开关"的途中布局先弹回去，开关会从指头下面跑掉；
  ③ **只在两栏都是展开状态时生效** —— 用户特意收起某一栏之后，鼠标路过不该把它又弹开；
     鼠标离开两栏 ⇒ 回到 `RadiusUISkinSettings.collapsedLeft/collapsedFlow`（用户自己的设置）。
- 焦点状态是**每帧算出来的**，不写配置（否则鼠标一动就写一次文件）。
- 新设置项 `hoverFocus`（默认开，Mod 设置里可关，恢复默认也会带上它）。

### S29 验收步骤

① 皮肤主控台的「作战任务 / 收集任务 / 远行队信息」三张卡之间现在各有 8px 空隙（不再贴边）；
② 打完一场（或读一个已交战的档）：那条 `! 交战：…` 现在排在**「作战任务」组里面**，
   不再吊在「收集任务」下面；随机事件的 `!` 行仍在最末尾；
③ 鼠标移到流程栏 ⇒ 流程展开、左栏收成竖条；移到左栏 ⇒ 反过来；移到主列/概览 ⇒ 回到你原来的折叠设置；
④ 手动收起过某一栏（列头 `－`）之后，鼠标路过不再自动弹开；设置里关掉「悬浮焦点」则完全不动布局。

---

## S30 轮：缴获（战利品）第一期 `[实现]`

用户原话（2026-09-28，对 S28 后那份"交战结算 / 战利品收集"调查的拍板）：
「关于战利品这块，作战任务的主要是缴获敌人装备，哨所/工作站点/站点的应该放在收集任务里面；
「搜集战利品」段位置 —— A 单开一段，插在交战段之后、建立营地之前」

### 19.91 缴获从哪来：趁销毁前抄，按敌方下标配对

- 敌人生成路径（S23 起）是"`PawnGroupMakerUtility.GeneratePawns` 造真 pawn → 折算快照 → **立刻 `Destroy()`**"，
  所以装备清单**必须在那一步抄**：`ThreatRosterFactory.CaptureLoot` 抄三处
  `equipment` / `apparel` / `inventory`，抄成 `DelegationLootItem`（Def + 材质 + 品质 + 数量 + `enemyIndex`）。
  **品质抄、耐久不抄**（缴获回来的枪该是"极佳"就是"极佳"，但是崭新的 —— 刻意近似，写在类注释里）。
- 清单挂在 `ThreatRosterFactory.Result.EnemyLoot` → `CombatSetup.EnemyLoot`（与 `Scene.Units` 的敌方**同序**）。
- 战斗结束后 `DelegationUtility.CaptureLoot(setup, result)` 挑出**打掉的那些**（`UnitReport.Dead || Downed`）：
  判据用**敌方下标**对齐（`result.Units` 与 `scene.Units` 同序，场景敌方按编队顺序加入）——
  不按名字匹配，否则三个同名"海盗"会配错。跑掉的守军把东西带走了。
- 结果存 `Delegation.lootBag`（`Scribe_Collections` + `LookMode.Deep`）：交战段抄好，搜集段才搬，
  中间可能隔着存读档。

### 19.92 「搜集战利品」段（用户拍板 A：单开一段，交战之后 / 建立营地之前）

- 新 Def `RimDelegation_Phase_MineLoot`（`requireThreat` ⇒ 只有真打过仗那条岔路才有它、0.5h、
  `deferNoteUntilDone` ⇒ 结果等这一段走完再公布，与交战段同一条 S26 规矩），
  `onEnter` = 新效果原语 `RimDelegation.DelegationEffectDef_GainLoot`；
  在采矿的 `flowPhases` 里插在 `MineEngage` 与 `MineCamp` 之间。
- `DelegationEffectDef_GainLoot` 只做一件事：`DelegationUtility.TakeLoot(caravan, d.lootBag)`。
  **份量口径 = 载重闸门**（用户没另指定口径，取最小惊讶的一档）：车队剩多少装多少、值钱的先装、
  可堆叠物**部分装载**、装不下的留在原地并写进说明；品质/材质还原，`ThingMaker.MakeThing(def, stuff)`
  + `CompQuality.SetQuality`。
- 搬完**清空 `lootBag`** —— 否则读档后那一段被重跑会再搬一次（等于刷战利品）。
- 营救那两条没有流程段 ⇒ 挂不上段，于是**强攻清场获胜时就地结算**（`RescueUtility.ResolveClearance` 里
  调同一份 `TakeLoot`），报进 worker 报告；这样"打赢就有缴获"在两条路径上语义一致。

### 19.93 启动自检（§ModBoot）

- 新增一条：**带 `GainLoot` 的段必须排在带 `ResolveCombat` 的段之后**（同一条 `flowPhases` 里按下标比）。
  排反了的表现是"永远捡不到东西且一句报错都没有"（那时 `lootBag` 还是空的），所以必须喊在启动时。

### 19.94 「收集任务」侧的战利品（**本期未实装**，只记口径）

- 用户口径：「哨所/工作站点/站点的应该放在收集任务里面」⇒ 站点类战利品属于**收集**那一组，与"缴获装备"
  分开。现在还没有指向这些站点的委派 Def（四条委派 = 矿点 / 物资藏匿点 / 两条营救），所以只落口径。
- 数据源已查证（见 S28 后的调查报告）：哨所的战利品市值**就在存档里**
  （`SitePartWorker_Outpost.GenerateDefaultParams` → `SitePartParams.lootMarketValue`，
  进图时由 `GenStep_Outpost` 交给 BaseGen 的 `SymbolResolver_LootScatter` 撒出来）；
  工作点类的表是 `SitePartDef.lootTable`（唯一读取点 `SitePartWorker_WorkSite.LootThings`）；
  站点自带实物是 `SitePart.things`。

### S30 验收步骤

① 打一场**有敌情**的矿点：流程里「交战」之后多出「搜集战利品」段（0.5h），走完弹出一行
   「缴获 N 件（合计 X kg · 市价约 Y 银）：…」（列前 4 种），它排在流程块的**「作战任务」组**里；
② 车队背包装得下 ⇒ 战利品真的出现在远行队物品里（枪甲的**品质与材质**与原守军一致）；
   装不下的会写「还有 N 件装不下，留在原地」；
③ 读档后再走完这一段 ⇒ **不会**再缴获一次（清单已清空）；
④ 打输（或撤退）⇒ 直接 Abort，不会有缴获段；
⑤ 营救强攻获胜 ⇒ 完成信/报告里出现同一句缴获说明（营救没有流程段，所以就地结算）。

---

## S31 轮：打扫战场（就地屠宰 · 收押俘虏 · 战场清点表）`[实现]`

用户对 S30 后那份 Brainstorm 的拍板（2026-09-28，逐题短标签）：**1A / 2B / 3B / 4A / 5A / 6A**
1A 保留真 pawn · 2B 就地屠宰（不带尸体回家）· 3B 尸骸要进 UI 物资表 · 4A 无条件收押倒地者 ·
5A 打扫范围做成可配档 · 6A 不做毁尸灭迹。

### 19.95 保留真 pawn（只限真结算）+ 顺手修掉 S27 的一个漏改

- `ThreatRosterFactory.Build(site, keepPawns)`：`keepPawns = true` 时**不销毁**生成的真人，
  按敌方下标放进 `Result.EnemyPawns`（炮塔位是 null 占位）。
  `CombatSceneFactory.Build(..., keepPawns)` 再把它抬到 `CombatSetup.EnemyPawns`。
- **只有两处真结算**传 true：交战段（`DelegationEffectDef_ResolveCombat`）与营救清场
  （`RescueUtility.ResolveClearance`）；UI 的预告/评估一律 false（生成完即销毁，行为与 S30 前一致）。
- **绝不泄漏**：`CombatSetup.DestroyUnusedPawns()`（幂等；判据 = 已销毁或已有 ParentHolder）
  放在 `finally` 里；`CanAssess == false` 与推演异常的早退路径也各调一次。
- ⚠️ **顺手修掉 S27 的漏改**：`CombatSceneFactory.Build` 有 `excludeFromCombat`（"谁不参战"）这一格，
  但**实际交战结算那一处从来没传**（只有 UI 预告传了）⇒ S27 的参战勾选框过去只影响成算、不影响真打。
  本轮把 `d.noCombatPawns` 补进结算调用。

### 19.96 打扫战场：就地屠宰 + 原版收押（2B / 4A / 5A）

- 新 `DelegationUtility.CleanupBattlefield(d, setup, result, butcherCorpses, capturePrisoners)`，
  按敌方下标与 `result.Units` 对齐分流：
  - **阵亡** →（开启屠宰时）`Pawn.ButcherProducts(参与者一人, 1f)`（**原版口径**的肉/皮，
    humanlike 到皮为止不切肢体；人肉的食人/心情规则因此自动生效），产物**只记账**（并进 `d.lootBag`），
    仍由「搜集战利品」段按载重闸门搬；实物当场 `Destroy`（免得同一批东西拿两遍）。
    我们**不产生尸体实体**（2B：不带尸体回家），也不走 `Pawn.Kill`
    （那会给一次抽象交战带来一整套原版死亡记账：信件 / Tale / Ideo 通知，而这些人从未真的存在过）。
  - **倒地 + humanlike** → `Find.WorldPawns.PassToWorld(p)` 后 `caravan.AddPawn(p, false)`：
    `Caravan.AddPawn` 内部会按 `CaravanUtility.ShouldAutoCapture`（humanlike + 没死 + 阵营不同）
    自动 `guest.CapturedBy(玩家阵营)` ⇒ **原版收押路径**，原阵营会记一笔、Tale 也记。
    ⚠️ `PassToWorld` 不能省：这些 pawn 从没 spawn、也从没登记进 `WorldPawns`（见 §19.85 那次教训）。
  - **其余**（跑掉的 / 还站着的 / 炮塔）→ 什么都不做，由 `finally` 销毁。
    ⚠️ **非 humanlike 的倒地动物原本也归在这一档，S33 已改**：它们现在按"补刀"并入尸体三档
    （见 §19.101 / §19.102 —— 不然打赢一群猴子会什么都不掉，玩家实测反馈过）。
- 三件事各自受 Mod 设置管（5A）：`cleanupTakeEquipment` / `cleanupButcherCorpses` /
  `cleanupCapturePrisoners`，**默认全开**；关掉某一格 = 那件事**一步都不做**（不是"做了但不显示"）。
- ⚠️ 顺序不变量：先装备清单、后打扫战场 —— 打扫战场要往同一个 `lootBag` 里 `Add`，
  反过来写的话装备赋值会把屠宰产物整个盖掉（本轮自己踩过一次，代码里留了注释）。

### 19.97 「战场清点」表（3B：尸骸也要有一行）

- `Delegation` 新增三个存档字段：`takenRows`（实际装车的东西：装备 + 肉皮）、
  `corpsesButchered`（就地处理的尸骸数）、`prisonersTaken` + `capturedPrisoners`（俘虏，按引用存，
  他们已经是车队成员）。
- `DelegationUIUtility.CleanupRows/CleanupHeader` 生成行：`尸骸（已就地处理）×N 具`、每个缴获物
  （图标 + 件数 + kg + 约值）、每名俘虏（**真头像行**）；表头写「战场清点（N 类）· 市价合计约 X 银 · 收押 N 人」。
- 两端各画一块（原版主控台「收集任务」内、皮肤主列同段），行画法仍复用 `DrawItemRow`（一份实现）。

### S31 验收步骤

① 打一场有敌情的矿点：阵亡的守军会被**就地屠宰**，`搜集战利品`段走完后远行队物品里出现人肉/皮革
   （数量按原版 `MeatAmount`/`LeatherAmount`；装不下会写「还有 N 件装不下」）；
② 倒地的守军会**变成俘虏随队**（远行队成员里能看到他们，原阵营记了一笔）；他们照常吃补给；
③ 主控台「收集任务」里出现**「战场清点」**一块：`尸骸（已就地处理）×N 具` + 缴获物行 + 俘虏头像行；
④ Mod 设置里关掉「就地屠宰尸骸」⇒ 再打一场：没有尸骸行、没有肉皮，只有装备与俘虏；
⑤ 读档后这一块内容还在（三个字段都进了存档）；⑥ 预告/威胁评估面板反复开关 ⇒ 不留残留 pawn
   （可看 Player.log 有无 "Tried to remove pawn" 之类的报错）。

---

## S32 轮：尸体处置三档（带走 / 立刻处理 / 丢弃）`[实现]`

用户原话（2026-09-28，推翻 S31 那格固定的 2B）：**「尸体不带回家，应该要可以选择，带走或者立刻处理或者丢弃」**。

### 19.98 三档互斥，落在 Mod 设置

- 新枚举 `CorpseCleanupMode`（`RimDelegationSettings.cs`）：
  `HaulHome` 带走（造真尸体按载重装车，回家自己上屠宰台）· `ButcherHere` 立刻处理（**默认**＝S31 的行为）
  · `Discard` 丢弃（什么都不做，就地销毁）。
- 设置界面用**循环按钮**（`ButtonTextLabeled`）而不是三个 checkbox —— 三档互斥，用勾选会让人以为能多选。
  老配置里的 `cleanupButcherCorpses` 键被忽略（配置项不迁移，不影响存档）。

### 19.99 「带走」怎么造尸体（三条都有反编译依据）

1. `Pawn.MakeCorpse(null, false, 0f)` 是 **public**，且不需要地图/已 spawn：
   只校验"人不在容器里"与 `RaceProps.corpseDef != null` ⇒ 对"从没进过地图的临时守军"可以直接造。
2. **不调 `Pawn.Kill`**：反编译确认 `Kill` 对"未 spawn、非世界 pawn、不在容器"的人会走到最后一个分支
   `corpse = MakeCorpse(...)` —— 那具尸体**不给返回值**（白造一具泄漏，还可能把 `InnerPawn` 的归属搅乱）。
   改用 `p.health.SetDead()`（public，只改 `healthState`），干净且不会造第二具。
3. `corpse.timeOfDeath` 自己填（`MakeCorpse` 不管它）—— 它决定原版那 2.5 天开始腐烂。
- 尸体 def 由 `ThingDefGenerator_Corpses` 生成：自带 `StatDefOf.Mass`（＝该 pawn 的质量）、
  `CompRottable`、`alwaysHaulable`、`category = Item`、`ingestible.foodType = Corpse`
  ⇒ **载重闸门天然可用**，回家走原版屠宰台那一套（食人/心情规则也都跟着）。

### 19.100 装车与收尾

- 尸骸存放在 `Delegation.pendingCorpses`（`LookMode.Deep`：尸体里裹着那个 pawn 的完整状态）。
- `DelegationUtility.TakeCorpses` 在「搜集战利品」段（以及营救的就地结算）里按载重装车：
  **排在装备与肉皮之后**（尸骸估值最低，值钱的先装）；装不下的当场 `Destroy`
  （原版 `Corpse.Destroy` 会 `innerContainer.Clear()` + `PostCorpseDestroy` ⇒ 连内部 pawn 一起收尾），
  并且**无论成败都清空队列**（与 `lootBag` 同一条防读档重搬的规矩）。
- `DiscardPendingHaul(d)` 在 `Complete` / `Abort` 里调用：队伍没走到搜集段就散了的话，
  待搬的尸骸是真尸体，不能就那么随委派对象变成垃圾。
- UI：「战场清点」表新增一行 `尸骸（已带上）×N 具 · X kg · 可自行屠宰`（与"已就地处理"那一行互斥出现）。

### S32 验收步骤

① 设置里把「阵亡守军的尸体」切到**带走** ⇒ 打一场：搜集段说明里出现「另带回尸骸 ×N（合计 X kg，回家可自行屠宰）」，
   远行队物品里能摸到尸体（信息卡是原版那一套，可上屠宰台）；「战场清点」表显示「尸骸（已带上）」；
② 切到**立刻处理**（默认）⇒ 行为与 S31 一致：没有尸体实体，只有肉/皮 + 「尸骸（已就地处理）」那一行；
③ 切到**丢弃** ⇒ 没有尸体、没有肉皮、也没有尸骸行（只剩装备与俘虏）；
④ 三种档位下「载重不够」的表现一致：装不下的当场丢弃，说明里会写「还有 N 件装不下」；
⑤ 打到一半中止 ⇒ 待搬的尸骸被丢弃，读档后不会冒出一具幽灵尸体。

---

## S33 轮：倒地的动物也要有归宿（补刀 → 并入尸体三档）`[实现]`

用户实测反馈（2026-09-28）原话：**「这个打完两个猴子后，没有获得尸体，也没有屠宰提示」**
（截图：RadiusUI 皮肤主控台 · 矿点委派 · 有敌情 · 编队 = 公猴 × 2）。

### 19.101 根因：模型只会把它们打成「倒地」，而旧代码对倒地的动物什么都不做

两条叠在一起才出现"战果凭空蒸发"：

1. **模型注定把它们打成倒地。** `CombatSnapshotFactory.FromPawn` 给的耐久池是
   `max(5, baseHealthScale × HealthPoolPerScale(100) × 血量比)` —— 猴子 `baseHealthScale = 0.45`
   ⇒ 45 点；倒地阈值 `DownHealthFraction = 0.25` ⇒ 11.25；殖民者一支步枪每发约 11 点
   ⇒ 几乎必然在"打到 ≤25%"那一刻先倒地，而倒地即 `Out`（退出索敌）⇒ **再也不会被打死**。
   这正是 §19.17.5 记下的系统性偏差"短促战斗只产生倒地、不产生阵亡"。
   截图上的战报就是它：`Victory · 10 回合（0.04 天）· 我方 0 阵亡/0 倒地；敌方 0 阵亡/2 倒地 · 威胁解除=True`
   （*不是* 2 阵亡）。
2. **旧代码把这一类直接扔了。** §19.96 的分流表原文就是"非 humanlike 的倒地动物 → 什么都不做，
   由 `finally` 销毁" ⇒ 没有尸体（`u.Dead` 为假，走不到尸体三档）、没有屠宰（`ButcherInPlace` 只在阵亡分支）、
   也没有缴获（动物身上本来就没装备）⇒ `lootBag` 空着，搜集战利品段只能照实写
   「战场上没剩下能带走的东西」。

### 19.102 修法：倒地且**收押不了**的守军，按"补刀"并入 S32 的尸体三档

- `DelegationUtility.CleanupBattlefield` 新增一支 `u.Downed && !p.RaceProps.Humanlike`
  ⇒ 与阵亡者**走同一条** `DisposeEnemyCorpse(d, p, corpseMode)`：
  立刻处理（默认）→ `ButcherInPlace`（原版 `Pawn.ButcherProducts` 的屠宰口径）；
  带走 → `MakeCorpseForHaul`（`Pawn.MakeCorpse` + `health.SetDead()`，回家上屠宰台）；
  丢弃 → 什么都不做。
- 判据为什么用 **"非 humanlike"而不是 "Animal"**：同一种敌人"被打死"与"被打倒"必须得到同一种归宿
  —— `u.Dead` 分支本来就覆盖一切非人类（机械族也在内），补刀分支若只认 `RaceProps.Animal`，
  就会留下"打死机甲留尸骸、打倒地机甲什么都没"这种不对称。
  人的那一支**不受影响**：humanlike 倒地仍然只在**开启收押**时变成俘虏；关掉收押 = 留活口，不执行。
- 两条原版路径都**接受活体**（反编译依据）：`Pawn.ButcherProducts` 只校验
  `RaceProps.meatDef` / `leatherDef`（外加动物专属部位），**不校验死亡**；
  `Pawn.MakeCorpse` 只校验 `holdingOwner == null` 与 `RaceProps.corpseDef != null`；
  `Pawn_HealthTracker.SetDead()` 只有一句 `healthState = Dead` ⇒ 对倒地者调用是干净的。
- 新增存档字段 `Delegation.downedAnimalsDisposed`（Scribe `roDownedAnimalsDisposed`）：
  「战场清点」的尸骸行**行尾**会写「（含 N 只倒地的动物）」—— 否则同一块面板上
  「敌方 0 阵亡/2 倒地」与「尸骸 ×2 具」看着像互相打架。

### S33 验收步骤

① 打一场「猎杀人类」的敌情点（动物守军，如猴群）：战报写「敌方 0 阵亡/N 倒地」，
   而**搜集战利品段的说明里出现肉/皮**（立刻处理档），「战场清点」出现
   `尸骸（已就地处理）×N 具 · 肉与皮按载重装车（含 N 只倒地的动物）`；
② 设置切到**带走** ⇒ 重打一场：远行队物品里摸得到 N 具动物尸体（原版信息卡，能上屠宰台），
   尸骸行写「（含 N 只倒地的动物）」；
③ 切到**丢弃** ⇒ 没有肉皮、没有尸体、没有尸骸行（与阵亡者一致）；
④ 打一场**人形守军**（哨所 / 囚犯营）：倒地的守军照旧变俘虏（开收押）或留活口（关收押），
   **不会**因为这次改动被就地宰掉；
⑤ 读档一次：那一行连同 `downedAnimalsDisposed` 都还在。

---

## S34 轮：悬浮焦点改「不对称」· 四个列头加图钉（固定展开）`[实现]`

用户原话（2026-09-28）：**「关于RimDelegation焦点切换UI，目前是鼠标悬浮在流程的时候展开流程，然后折叠左栏。
鼠标悬浮在左栏的时候展开左栏，折叠流程。请修改成这样：鼠标悬浮在流程的时候展开流程，然后折叠左栏。
鼠标悬浮在左栏的时候展开左栏，但是不折叠流程。另外上面折叠展开的+-号按钮左边添加一个图钉PIN按钮，
点击后可以固定展开。」**

本轮**只动皮肤侧**（`RimDelegation-RadiusUI`：`DelegationConsoleSkin` / `RadiusUISkinSettings` / `RadiusUISkinMod`），
把 S29 的悬浮焦点从"二选一"改成不对称，并新增列头图钉。已 build + deploy
（`RimDelegation.RadiusUI.dll` SHA256 `EC7903CE…`，deploy.ps1 自检通过），待进游戏验收。

### 19.103 悬浮焦点改成不对称：悬左栏不再收流程

- 语义改动只有一处：`DelegationConsoleSkin.Draw` 里 `columnFocus == Left` 分支原来是 `colFlow = true`，
  现在只把左栏摆成展开态（`colLeft = false`，S29 门控要求它本来就没被手动收起），
  流程栏维持它自己的状态。`columnFocus == Flow` 分支不变（收左栏，顺带把让出的宽度抬进 `flowCap`）。
- 「不折叠流程」的观感分两种宽度（[源码] 推导，宽度账见 `DelegationConsoleSkin` 文件头的 FlowMaxW 注释）：
  - `body2.width ≥ 1342`（≈ 窗口 1402 宽）⇒ 左栏展开时流程栏仍能与主列 / 右栏共存，**两栏并存**；
  - 更窄 ⇒ 流程栏照 S22 的规则「挤不出宽度就回落进主列」（`flowW < FlowMinW(240) ⇒ 0`），
    流程块内容照旧画在主列里 —— 这是版面取舍，不是"被折叠"（旧版 `colFlow = true` 的可见结果与它相同）。
- 顺带把 S29 那条"粘滞"判据的成立条件核清楚（写进 `UpdateColumnFocus` 注释）：左栏收起时
  `flowCap` 加上 `listBaseW − StripW`，而可用宽度也正好多这么多 ⇒ 流程栏切入焦点态是
  **向左变宽、右边缘不变**（`27 + min(avail+314, 614) ≡ 341 + min(avail, 300)`），
  所以鼠标落在流程栏右半时不会因为这一栏变形而把自己挤出矩形（否则会一帧一个样地自激）。
- 门控现在吃的是**已把图钉算进去**的生效折叠态（`collapsed* && !pinned*`）：钉住左栏不会把整套悬浮焦点关掉
  （只是左栏不再被收起，见下）。

### 19.104 列头图钉 = 固定展开

- 每栏列头（左栏 / 流程 / 主信息 / 概览）在 `＋/－` **左边**多一颗 22px 图钉；贴图用 Radius UI Framework
  自带的 `RadiusUI/Action/Pin`（与 Quest Menu 的"钉住任务"同一张，[反编译]
  `RadiusUIQuestMenu.QuestDetail.DrawContent`）；钉住时强调色底 + 亮色图标，未钉住时与其它 IconButton 同款暗底。
- 语义只有一条：**钉住 = 这一栏固定保持展开**（`RadiusUISkinSettings.pinnedLeft/Flow/Main/Rail`，
  与 `collapsed*` 一样是**本地 Mod 配置、不进存档**）：
  ① 生效折叠态 = `collapsed* && !pinned*`；
  ② 悬浮焦点不收钉住的栏（`Draw` 里的 `!pinLeft`）—— **这是图钉的主用途**：
     钉住左栏之后，鼠标移到流程栏不会再把它收走；
  ③ 点 `－` ＝ 收起**并顺带拔钉**（按钮说的话必须算数，tooltip 也写明），
     所以不存在"钉住了却被收起来"或"钉住了却看不到图钉、拔不掉"的矛盾状态
     （钉住时只剩 `－` 可点、且钉住会把 `collapsed` 清掉）。
- 折叠成 26px 竖条时那一格放不下第二颗按钮 ⇒ 只画 `＋/－`；按 ③ 的口径，钉住的栏按定义就是展开的，
  所以不会出现拔不掉钉子的死角。
- 顺手把 Mod 设置里「悬浮焦点」的说明改成新规则；`README.md` 的验收清单补了列头 / 图钉 / 焦点三条，
  并把"本 mod 只写 4 个 bool"的旧描述改成"开关 / 折叠 / 图钉 / 悬浮焦点 + 置顶列表"。

### S34 验收步骤

① 皮肤主控台（选中一条在途委派）鼠标从左栏移到流程栏 ⇒ 左栏收成竖条、流程栏展开；
   再从流程栏移回左栏 ⇒ 左栏展开、**流程栏不再被收掉**（宽窗口下两栏并存；窄窗口下流程块回落进主列）；
② 鼠标移到主列 / 概览 ⇒ 两栏回到你自己的折叠设置（默认都展开）；
③ 点左栏列头的图钉 ⇒ 图标变强调色；此时鼠标移到流程栏 ⇒ **左栏仍在**（不再收成竖条）；
   再点一次图钉 ⇒ 恢复"移开就被收起"；
④ 点图钉旁边那颗 `－` ⇒ 该栏收起**且图钉一起灭掉**（再点 `＋` 只展开、不带图钉）；
⑤ 四栏列头都能看到 `＋/－` 与它左边的图钉；某一栏手动收起成 26px 竖条时只剩 `＋/－`；
⑥ 退出游戏重进 ⇒ 图钉状态还在（本地 Mod 配置；换存档也在，因为不进存档）。

---

## S35 · RIM-3：把 Radius UI 皮肤并入本体（2026-10-05）

**这是本日志里第一轮"合并 / 收敛"型改动**（前面 S0–S34 都是加功能）。追踪：任务板 **RIM-3**（依赖 RIM-2 的版本基线）。

### 起因

RimDelegation 与 RimDelegation - Radius UI 一直是**两个 mod**：本体出一个 dll、皮肤出第二个 dll；
皮肤用 **1 个 Harmony Prefix** 掐掉 `Window_Delegations.DoWindowContents` 再自己重画。副产品三条：
"同一份信息要维护两套画法"（双端准则）、两个工程必须**按顺序编译**、
以及一条只在"皮肤没装 / 被关掉"时才走的原版画法路径。

### 用户拍板（RIM-3 议题评论，五问五答，**原话**）

| # | 问题 | 用户答复（原话） |
|---|---|---|
| 1 | 合并形态 | 「合并形态：皮肤代码直接进本体 Source\\（同一个 dll）」 |
| 2 | 依赖处理 | 「合并后 astryl.RadiusUI.Framework 变成本体的硬依赖（modDependencies 里直接写死）」 |
| 3 | 一键回退开关 | 「**移除**」 |
| 4 | 皮肤目录 | 「**删除**」 |
| 5 | 双端 UI 准则 | 「**作废**」 |

### 改了什么

| 项 | 改动 |
|---|---|
| 目录 | `RimDelegation-RadiusUI\`（15 文件）**删除**；皮肤 3 个 .cs 移入 `RimDelegation\Source\Skin\`（`ConsoleWindowBridge.cs` / `DelegationConsoleSkin.cs` / `SkinButtons.cs`，命名空间仍是 `RimDelegationRadiusUI`） |
| 绘制入口 | `Window_Delegations.DoWindowContents` 顶部**直接调** `DelegationConsoleSkin.Draw(win, inRect)`，返回 true 就 `return`。**Harmony 补丁点 -1**（回到 4 个）；`SkinPatch.cs` / `RadiusUISkinMod.cs` / 皮肤 `ModBoot.cs` / 皮肤 `deploy.ps1` / `README.md` / `About.xml` / `RimDelegation.RadiusUI.csproj` 删除 |
| 兜底 | 皮肤不接管（反射拿不到 `Verse.Window.windowDrawing`）或最外层抛异常 ⇒ 落回旧画法（`DelegateToSkin` 里 try/catch + `Log.ErrorOnce`）。这是"界面绝不空窗"的最后一道，**不是给玩家切的开关** |
| 设置 | 皮肤设置并入 `RimDelegationSettings`（+10 键：`pinnedSites` / `collapsedLeft..Rail` / `pinnedLeft..Rail` / `hoverFocus`）；`enabled` / `skinConsole` 两个开关**删除**；标题栏那枚「切回原版」（`Action/SwapView`）**删除** |
| 依赖 | `About.xml` 加 `astryl.RadiusUI.Framework`（带 `steamWorkshopUrl`）+ `loadAfter`；`csproj` 加 `<RadiusUIDir>` 与 `RadiusUI.Framework` 引用（`<Private>False</Private>`）；`ModBoot` 加 `FrameworkVersion.Require("RimDelegation", 32)` |
| 日志 | 皮肤日志统一 `[RimDelegation] 皮肤：` 前缀；启动横幅尾部新增 `\| 主控台画法 Radius UI 皮肤`。**顺手修掉 4 组 `Log.WarningOnce/ErrorOnce` key 冲突**（皮肤 0x5E0E0/1/2/4 与本体 `DelegationThreatSummary` 的 0x5E0E1/2 撞车 ⇒ 合并前就存在、现在会互相吞日志；另两处既有冲突 0x5E0D1、0x5E0CF 一并唯一化）。修完 37 个 key 全唯一 |
| 文档 | 三份文档按"单端"改写；皮肤 README / About.xml 随目录删除 |

### 证据（2026-10-05，全部本机实测）

- 编译：单工程 MSBuild（`Source\RimDelegation.csproj`），**0 error / 0 warning**。
- 产物：`RimDelegation.dll` **346,112 B**（合并前 293,376 B）/ SHA256 `88BC5BDD657EF52243AD224D996E71E84CC6C5E53CB37B6B51DF80C56EFACE53`（20:42:02。20:34:15 那一版 `0A980D00…` 是修日志 key 冲突之前的，已被覆盖）；
  引用表含 `RadiusUI.Framework`；`DelegationConsoleSkin` / `ConsoleWindowDrawing` / `SkinButtons` 均在产物里。
- 部署：`deploy.ps1` 自检通过（游戏侧与工作区 dll 同哈希）；游戏 `Mods\RimDelegation` **148 文件 / 0 个 RadiusUI 残留**；`Mods\RimDelegation-RadiusUI\` 已删除。
- `ModsConfig.xml`：移除 `duskmelon.rimdelegation.radiusui`（备份 `_backup_ModsConfig_before_RIM3_20261005_203843.xml`）；`<li>` 154 → 153（activeMods 148 + knownExpansions 5）。
- 旧皮肤 dll（删除前记录存档）：75,776 B / SHA256 `6CFD130271C497522CDAC8A6A7AB702AB29745622C6F1ED9888E67F5ADF77FBE`。

### S35 验收步骤

① 从 Steam 启动（**只启用 RimDelegation 一个**——不再有皮肤 mod 可勾）；`Player.log` 应出现
   `[RimDelegation] 已加载 | … | 主控台画法 Radius UI 皮肤`，且**没有**任何 `[RimDelegation-RadiusUI]` 横幅；
② 底部「委派」按钮 → 主控台是 Radius UI 样式（圆角深色面板；标题栏两枚图标：设置 / 关闭，**没有**"切回原版"）；
③ Mod 设置里能看到「主控台（Radius UI 皮肤）」一段的**悬浮焦点**开关，**看不到**皮肤总开关 / 主控台开关；
④ 行为回归：左栏折叠与图钉、悬浮焦点、待下达草稿、中止委派、紧急加班按钮全都在。

### 遗留（未拍板，留给下一轮）

1. **旧画法（`Window_Delegations` 里那 1000 多行）要不要真删**：现状是"只在皮肤不可用时才走"。
   "只删主控台画法"还是"连草稿 + 报告画法一起删"，两条范围都没拍板（属皮肤合并调研清单第 ③ 问）。
2. 皮肤旧配置文件（`Config\ModSettings\..._RadiusUISkinMod.xml`）不再被读取：置顶 / 折叠 / 图钉需重新勾一次（纯观感，无副作用）。
3. **远程 GitHub 仓库仍未推送**（RIM-2 剩余项）。

---

## S36 · RIM-6 + RIM-9：列头折叠开关看不见（修）· 收工报告窗半径化（2026-10-05）

用户原话（2026-10-05，同一轮两条议题）：
**「左栏、流程、主信息、概览 上面添加了PIN按钮，但是折叠/展开按钮没了」**、
**「完成后的UI（图二）需要写RadiusUI」**；
拍板答复：**「修复RIM-6, A；RIM-9, A;」**（RIM-6 取 A = 换成矢量/自绘图标；RIM-9 取 A = 壳与内容一起半径化）。

本轮只动 **`Source/Skin/`** 与两个语言文件，另有一个**顺手的 P0 小修**（英文 Keyed 的非法注释，见 19.107）。
已 build + deploy（`RimDelegation.dll` 347,136 B，SHA256 `EAE62E48DBB3A578…`，deploy.ps1 自检通过），**待进游戏验收**。

### 19.105 RIM-6：列头折叠开关从"全角字形"改成"自绘几何 + 底"

- 现场（用户截图 + 逐像素复核）：四个列头**只有图钉**，图钉右边那 22px 一格是**纯背景** ——
  既没有按钮底、也没有任何字形笔画。而 `ildasm` 反汇编**已部署的 dll**
  确认 `DrawColumnToggle` 里那颗 `UIKit.Button(rect, "－"/"＋", ButtonStyle.Ghost, …)` **确实在画**、
  位置也正确（`Source/Skin/DelegationConsoleSkin.cs:1810-1876`，RIM-6 议题里有 IL 逐行表）。
- 结论：**代码没错，错在那一格的全部视觉都押在 `Ghost` 档 + 全角 `－`(U+FF0D) / `＋`(U+FF0B) 两个字形上**
  （`Ghost` 不铺底，字形不出来就等于没有按钮）；次要嫌疑是它右边缘正好压在列边界 `col.xMax - 2` 上被裁。
- 改法（两条一起，不依赖任何一条成立）：
  ① **自绘**：新增 `DrawCollapseToggle` —— `CardChrome.Rounded(Surface2, 6)` + `CardChrome.Hover` +
     `RadiusFont.LabelAt("－"/"＋", Scale.Section)` + `Widgets.ButtonInvisible`，
     与 `DrawPinToggle` **同源同款**；即使字形仍然不出来，也有底色保证"看得见、点得到"；
  ② **内收**：新增常量 `ColBtnW = 18f`（按钮方块，原 22×20）与 `BtnGap = 4f`，
     整组从 `col.xMax - 24` 收到 `col.xMax - BtnGap - ColBtnW`，右边缘不再贴列边界；
     列名让位宽度随之由写死的 `58` 改成**算式**（`PinBtnW + BtnGap + ColBtnW + BtnGap + 16`）。
- 为什么不用 `RadiusIcon`：框架图标集里**没有加减号**（`Textures/RadiusUI/Action/` 逐张看过：
  只有 `DevPlus` / `ChevronDown` / `Strip` / `StripArrow` 等），而"折叠/展开"最直观的仍是 `＋/－`。
- 保留：图钉语义、`－` 顺带拔钉、收成 26px 竖条时只画折叠开关 —— 全部照旧（`DrawColumnToggle` 的分支没动）。

### 19.106 RIM-9：收工签核窗口改走 Radius 画法

- 现场：图二那个窗口原本是**整个原版**（原版窗口底 + `Widgets.DrawMenuSection` + 两颗原版按钮），
  而主控台早就半径化了 ⇒ 同一界面两种质感。
- ① **窗口底**：`ConsoleWindowBridge` 的参数类型从 `Window_Delegations` **放宽到 `Window`**
  （`windowDrawing` 本来就是 `Verse.Window` 的字段，与具体窗口类无关）；
  `Dialog_DelegationReport.DoWindowContents` 开头 `new ConsoleWindowBridge(this).InstallChrome()`，
  于是两个窗口底都是同一个 `ConsoleWindowDrawing`（圆角 `Surface0` + `Border` 描边 + 关阴影）。
- ② **正文**：新增 `Source/Skin/ReportSkin.cs` —— 把原先长在 `DelegationConsoleSkin.RecordMain` 里的
  Radius 报告排版整块搬过来，**历史详情与签核窗口共用这一份**（`RecordMain` 现在只剩转交一行）。
  数据仍只来自 `DelegationReportData`，本类不碰逻辑。
- ③ **底栏**：新增 `ReportSkin.DrawConfirmBar`（`CardChrome.Rounded` + `RadiusFont.Label` + `ButtonInvisible`），
  右端一颗「确认」；**关窗动作交给窗口自己**（`PostClose()` 要触发报告排队器，皮肤不能绕过它）。
  同时把 `doCloseButton` 关掉（原版那颗"关闭"会和半径底栏打架），标题栏的 ✕（`doCloseX`）保留。
- ④ **兜底**：`ConsoleWindowBridge.Ready == false` 时**整窗落回原版画法**
  （`DelegationReportUI.Draw` + 原版按钮）——与主控台同一条"界面绝不空窗"的路径；
  原版画法的 `DelegationReportUI` **保留不删**。
- ⑤ 玩家可见文案进 Keyed：`RimDelegationReportEvents` / `_EventsNone` / `_NoEvents` / `_Confirm`（中英各 4 条）。
- 窗口 `InitialSize` 560×460 → **640×520**（给半径标题 + 底栏留位置）。

### 19.107 顺手修：英文 Keyed 里那处非法注释（RIM-1 遗留的 1 字符项）

- `Languages/English/Keyed/RimDelegation.xml:64` 的注释里写着 `register -- no developer voice`，
  XML 注释**不允许连续两个减号** ⇒ 整份文件解析失败、**59 个 key 全部作废**
  （中文那份一直正常，所以只在英文环境暴露）。
- 本轮因为要往这文件里加 RIM-9 的 4 条 key，顺手把 `--` 改成 `—`（em dash）。
  修后实测：中英两份都良构、**各 63 个 key、键集合完全相同**。
- 注：`tools/Check-DefsXml.ps1` **只扫 `Defs\`**，抓不到 `Languages/**` —— 这个缺口仍在（RIM-1 已记）。

### S36 验收步骤

① 进游戏打开「委派」主控台 ⇒ 四个列头（左栏 / 流程 / 主信息 / 概览）**每栏都能同时看到图钉与它左边的 `－`**，
   两颗都有底色、都能点；鼠标悬停各自出 tooltip（「收起左栏」/「固定展开左栏」）；
② 点某一栏的 `－` ⇒ 该栏收起成 26px 竖条（只剩折叠开关）；再点 `＋` ⇒ 展开；
③ 点图钉 ⇒ 变强调色且该栏不再随鼠标收走；点 `－` ⇒ 收起**且图钉一起灭掉**（S34 行为不变）；
④ 触发一次委派收工（Mod 设置里「结束报告」开着）⇒ 签核窗口是**圆角深色面板**、
   标题/概要/事件明细都是 Radius 排版、底栏右端一颗 Radius「确认」；
   点「确认」关窗后，若同时收工了两条，**第二条会接着弹**（排队器没被绕过）；
⑤ 主控台右栏选一条**历史记录** ⇒ 详情排版与④的窗口**完全一致**（同一份 `ReportSkin.Draw`）；
⑥ 英文语言下：`Player.log` 不应该再有 `RimDelegation.xml` 的解析错误，英文界面文案齐全。

### 遗留（未拍板）

1. **RIM-6 的根因未最终定性**：本轮用"自绘字形 + 底 + 内收"把两种嫌疑一次性都堵上了，
   但没有做"只留字形 / 只改位置"的对照实验 ⇒ 若将来又出现"开关看不见"，先按 RIM-6 议题的复核路径二分。
2. `Widgets.FillableBar` 仍留在旧对话框与**原版兜底**的报告画法里（`docs/设计文档.md §3` 已记）。
3. RIM-7（三段标题发虚）、RIM-8（收集任务段取空后空白）**未做**，仍是 backlog。

---

## S37 · RIM-5：作业模式剥离出独立「满意度」机制（2026-10-05）

> **本条取代 §19.25.2 的模式定价**：模式不再给效率、也不再直接挂心情 —— 两样都由「满意度」产出。
> §19.25.3（疲劳 → 工伤倍率）与 §19.25.4（野外伙食）**继续有效**，且分别成了满意度的"生理代价"邻居
> 与「最近一段时间的吃喝」来源的数据源。

### 19.108 用户需求与拍板 `[验证]`

**用户原话（2026-10-05）**：「现状：作业模式会提供效率和心情的增益减益／修改：作业模式不提供这些效果影响，
独立一套机制"满意度"出去，提供心情和效率增益减益，满意度来源：最近一段时间的吃喝，游戏难度，
远行时间（受文化影响），要让玩家可配置」

**议题 RIM-5 评论里的拍板**：

| # | 问题 | 拍板 | 落地含义 |
|---|---|---|---|
| 1 | 效率边界 | **1A** | 只摘 `workRateMultiplier`；**工时窗口留在模式**（它是作息定义，干得久自然日产高） |
| 2 | 模式那三条心情 | **2B** | 不删，折算成满意度来源「作业强度」（`workIntensity` = +3 / 0 / −4 / −6） |
| 3 | 载体粒度 | **3A** | **每支委派一个值**（不做逐人值，避开新加入 / 离队 / 伤员三种分叉） |
| 4 | 「远行时间」口径 | **4B → 修正为"远行队实际开始移动那一刻"** | `planDepartTickAbs`（`pather.MovingNow` 第一次为真）→ 开工交给 `Delegation.departTickAbs` |
| 5 | 「文化」 | **5C → 修正为"远行时间照做、只是不接文化"** | 本轮做满四来源，文化只留后续挂点（mod 内零 DLC 判定） |
| 6 | 心情兑现 | **6A** | 沿用 memory thought（每天挂一次、落地后兑现），不引 Hediff 路线 |
| 7 | 可配置粒度 | **7A** | 四来源各自**开关 + 权重**，另加效率幅度 / 心情幅度 |
| 8 | 数值落点 | **8A** | **双落点**：Def 给曲线与默认值，Mod 设置页是玩家实际生效值 |
| 9 | 效率落点 | **9A** | 仍乘在原来那四处（采矿 / 搜刮 / 工作站点 / 营救），**不**做 `StatDef` 化 |

### 19.109 数值公式（唯一收口 `Source/DelegationSatisfaction.cs`）`[验证]`

子分（0..1，0.5 = 中性）：

| 来源 | 公式 | 端点 |
|---|---|---|
| ① 最近一段时间的吃喝 | `clamp01((近 mealRecentDays 天每次吃饭的野外伙食心情均值 + 2) / 6)` | 干粮 −2 → 0；凑合 0 → 0.33；不错 +2 → 0.67；很好 +4 → 1；窗口内没吃 → 0.5 |
| ② 游戏难度 | `clamp01((Find.Storyteller.difficulty.colonistMoodOffset + 10) / 20)` | 和平 +10 → 1；常规 0 → 0.5；冷酷 −10 → 0 |
| ③ 远行时间 | `0.5 × (1 − clamp01((在外天数 − travelGraceDays) / travelFullPenaltyDays))` | 默认宽限 1 天、之后 9 天线性到 0 |
| ④ 作业强度 | `clamp01(0.5 + mode.workIntensity / intensitySpan)` | +3 → 0.75；0 → 0.5；−4 → 0.17；−6 → 0 |

```
满意度   = Σ(开关 × 权重 × 子分) ÷ Σ(开关 × 权重)        // 权重可负；权重和为 0 或全关 ⇒ 0.5
作业速率 = 1 + (满意度 − 0.5) × 2 × efficiencyRange        // 默认 0.15 ⇒ 满意度 0 → ×0.85、1 → ×1.15
每日心情 = 记忆 Def 第 clamp(floor(满意度 × 5), 0, 4) 档 baseMoodEffect × moodScale
           档位值 = −8 / −4 / 0 / +4 / +8（`RimDelegation_Thought_Satisfaction`，moodScale 默认 1）
```

⚠️ 两处**实测校正**（写代码时被编译器抓到，别再照文档表格写成整数）：
`DifficultyDef.colonistMoodOffset` 是 **float**（不是 int）；
`DelegationRegistry.AnyActiveFor(Pawn)` 返回的是**宿主组件** `WorldObjectComp_Delegations`，不是 `Delegation`。

### 19.110 落点与不变量 `[验证]`

* **新增** `Source/DelegationSatisfaction.cs`：`DelegationSatisfactionSource` / `DelegationSatisfactionSourceEntry` /
  `DelegationSatisfactionDef` / `DelegationMealRecord`（进存档）+ 公式收口 `DelegationSatisfaction`。
* **`DelegationModeDef`**：+`workIntensity`；`workRateMultiplier` 与 `dailyMoodThought` **标废弃但保留字段**
  （第三方 patch 不报未知字段错误），`ModBoot.CheckSatisfaction()` 对"还在写它们"喊 Warning（禁静默失效）。
* **`Delegation`**：+`departTickAbs` / `recentMeals` / `satisfaction`（三个新存档键 `roDepartTickAbs` /
  `roRecentMeals` / `roSatisfaction`）+ `RefreshSatisfaction()` **每 tick 刷一次**（UI/worker 只读缓存）。
* **心情仍是 memory thought**（用户拍板 6A）：`GrantDailyMood` 改挂满意度档位记忆；
  **0 心情那一档不挂**（0 心情记忆会被 `MoodOffset() != 0f` 滤掉，挂了只是白占内存）。
* **模式切换的语义变化**：满意度按新模式**立刻**重算（作业强度是输入），但**每日心情记忆要等下一次日结算**
  —— 所以「换班不白拿心情」这条老不变量仍然成立（`ticksSinceMoodTick` 绝不重置）。
* **可配置**：`RimDelegationSettings` +11 键 +设置页一整段（含公式说明与「恢复满意度默认值」）；
  设置页是**档位按钮不是滑条**（本项目从未用过滑条 API，不猜），要更细的浮点直接改 `Config\ModSettings` 的键。
* **原版三处 + 皮肤三处**文案统一走 `DelegationUIUtility.SatisfactionLine` / `SatisfactionLineEstimated`
  （"唯一一份措辞"，与 ModeLine/MoodLine 同规矩）。

### 19.110.1 RIM-5 追加：满意度独立成栏 + 悬浮四因素情报 + 模式块同步（2026-10-05 深夜）`[验证]`

用户原话：「RIM-5，把满意度单独拆成一栏，悬浮提供简要情报（显示影响的因素），工作模式那块的内容也要同步修改」。

**① 独立成栏（六处落点）**

| 位置 | 落法 |
|---|---|
| 皮肤概览栏 · 在途 | 模式行下面**新增一条独立 `GlanceEntry`**：主文字 `满意度 62%`、副文字 `干得挺顺 · 每天心情 +4 · 作业速率 ×1.036` |
| 皮肤概览栏 · 草稿 | 同款独立一条（**预计**口径，副文字尾标「（预计）」） |
| 皮肤概览栏 · 前往中 | 已决定时独立一条；在外天数按 4B 从 `planDepartTickAbs`（实际开始移动）起算 |
| 皮肤主列 · 草稿 | 模式行（可点）之后加一条**不可点**的满意度行 |
| 皮肤主列 · 在途 | 「收集任务」段首加一行满意度（明细在概览栏那条的悬浮情报里） |
| 原版页签那一行 | Keyed `RimDelegationTabMode` 由「模式：{0} · 每天心情 {1}」→「模式：{0} · **满意度 {1}**」，第二槽给 `SatisfactionShort` |

**② 悬浮情报**：线条来自 `DelegationSatisfaction.FactorLines` / `FactorLinesEstimated`
（每个来源一行：`· 名称 子分%（权重 w）：数据细节`；关掉的写「（已关闭）」）+ 末尾一行汇总
（`→ 满意度 x%（档位）：每天心情 ±n，作业速率 ×r`）。显示层唯一口径：
`DelegationUIUtility.SatisfactionMain` / `SatisfactionSub` / `SatisfactionSubOf` / `SatisfactionSubEstimated` /
`SatisfactionTip` / `SatisfactionTipEstimated` / `SatisfactionShort`。

**③ 模式块同步**：模式行的副文字改成 `作业窗口 X 小时/天 · 点它换班`，悬浮改成「作业强度是满意度的一个来源，
换班后满意度立刻重算，每日心情下一次日结算才换档」；全库清点后**模式/皮肤/原版里再无**「速率 ×N」或
「模式心情代价」字样（剩下的只属于满意度自己、紧急加班、以及注释）。

**验收（进游戏 1 分钟）**：在途委派右栏概览里「模式」下面多出独立一条「满意度 xx%」→ 悬停弹出四来源明细 + 汇总 →
点模式换班子分立刻变、当天心情记忆不变 → 草稿页模式行与满意度行分开两行 → 原版页签那行显示
「模式：… · 满意度 62%（心情 +4/天 · 速率 ×1.036）」。

**产物**：git `87e6f37`（只含本追加的 6 个源码/Keyed 文件 + 构建产物）；dll 382,464 B /
SHA256 `CAFF242666309F72F0BCF4665AD4CD92BDB965A01687A05A3064BC3A15DD9354`（`deploy.ps1` 自检通过）。

### S37 验收步骤

① 开局选**常规作业**：主控台/检视显示的"模式"行不再有 `速率 ×N`，改为 `作业强度 0` + 一行「满意度 …」；
② 让车队在委派里吃**干粮**（或生食）几顿 ⇒ 「满意度」下降（档位词变差、效率系数 < ×1.00）；
   换成**精致餐/奢华餐** ⇒ 回升（`durationDays 1` 的野外伙食记忆本身也在涨）；
③ 把难度换成**冷酷无情**（`colonistMoodOffset = −10`）⇒ 难度子分 → 0，满意度整体下移；
④ 让一趟委派在外待过 1 天（宽限）后继续 ⇒ 满意度随天数线性下降；同一次委派把模式切成**全天候** ⇒
   满意度**立刻**再降（作业强度 −6），但**当天已挂的心情记忆不变**，要等次日结算才换档；
⑤ Mod 设置里关掉「游戏难度」来源（或把权重切到 0）⇒ 难度不再影响满意度；
   关掉总开关 ⇒ 满意度恒为中性（每天心情 0、作业速率 ×1）；
⑥ `Player.log` 里应看到 `DelegationSatisfactionDef 1 个`；若有人把 `workRateMultiplier` 写回 XML，
   启动时会有一条 Warning 点名它**不生效**。

### 遗留（未拍板）

1. **文化这一层没做**（用户拍板 5C）：满意度来源里没有 Ideology 修饰；将来要接，挂点候选是
   `PreceptComp_*Thought` 系列 / `MemeDef`（`PreceptDef` **没有** `moodOffset` 字段 —— 实测 0 命中）。
2. **效率仍是"4 处乘法"**（用户拍板 9A）：没有 `StatDef` 化的作业速率，所以"满意度 ×0.85"不会出现在
   pawn 的信息卡里，只出现在委派 UI 与产出上。
3. 权重设置页只有 5 档（0 / 0.5 / 1 / 1.5 / 2）：更细的浮点要手改配置文件。
4. `DelegationUIUtility.MoodLine` 仍在被草稿页用作"每天心情"那一行（与满意度行并存），
   要不要合并成一行，等一轮 UI 反馈再定。

---

### 19.111 RIM-11 ~ RIM-16：流程数值可管理化 + 旁白逻辑化与随机度（S38）`[验证]`

本轮是**一次收口**：把 RIM-10（父议题，只做调研与拆分）下面的 6 条子议题一次做完。
用户原话（2026-10-05，本会话指令）：**「执行RIM-10 to RIM-16」**。

#### 用户需求与拍板（照录）

| 议题 | 用户原话（评论） | 拍板结果 |
|---|---|---|
| RIM-11 | 「RIM-11调查：撤离时间是否可以随机，一个固定值加减偏移值，brainstrom偏移值设计逻辑／启动自检那 2 条要」 | `3A` 三个「撤离」统一 **2h**；启动自检 2 条**做**；随机偏移按"固定值 ± 偏移值"实装（设计逻辑见下） |
| RIM-12 | 「使用推荐」 | `2A` 全局倍率 + 每段可选覆盖；`12A` 草稿页口径改为**含**固定流程；`4B` 技能因子单独立项；**`甲`** 倍率只对新开工生效（开工冻结） |
| RIM-13 | 「按照推荐来」 | `5A` 多标签集合 + 权重；`6A` 权重 + 不重复窗口 + 间隔区间全上 |
| RIM-14 | 「按照推荐来」 | `7A` L3 进度 + L2 特质 + L1 小时/季节/全球事件，**天气不做**；存档兼容走 `甲`（读档丢弃旧下标重掷）。⚠️ **落地时 L1 只做了"小时 + 全球事件"，季节没做** —— 引擎侧可用（`RimWorld.GenLocalDate.Season(PlanetTile)`），但 `AmbientLine` 里**没有**季节判据、也没有任何一条旁白用它。如实记为"未接线"，不冒充做了 |
| RIM-15 | 「按照推荐来」 | `9A` 18 段全补齐（敌情 + 驮兽，每池 ≥3 条）+ 逐字重复去重（**机器复算是 24 组**；RIM-10 调研稿记的 25 组是人工清点的口径差，以脚本为准）+ P-D11~P-D16 硬伤 + 休息池扩充 |
| RIM-16 | 「按照推荐来」 | `10A` 总开关恢复**默认开**；**不做**老玩家配置的一次性迁移；证据文件搬出 mod 目录 |

#### 机制定稿 A：流程数值（RIM-11 / RIM-12）

**A-1 唯一时长收口 = `DelegationPhaseDef.TicksWithScale(scale)`。**
体检时发现"四处显示口径"这个说法**只对了一半**：`DelegationPhaseDef.HoursLabel` 的读者是 **0**
（全库被读的 `HoursLabel` 都属于 `DelegationModeDef`），`DelegationStage.totalHours` 是**只写不读**。
真正需要同步的是 `DelegationStageList` 里**读 `phase.hours` 的那 8 处**（前置 4 + 收尾 4）+ 实际推进
（`AdvancePrelude` / `AdvanceSuffix`）+ 整条 ETA（`FlowRemainingTicks` → `EstimatedDaysLeft` → 那 9 处
"预期于 … 完成 / 剩余 X 天"）。所以本轮把"段时长"收敛成**两个入口**：
`st.HoursOf(phase, scale)`（显示）与 `st.TicksOf(phase, scale)`（推进），全部消费点只走它们。

**A-2 倍率（RIM-12）**：`RimDelegationSettings.flowHoursScale`，设置页 5 档
`0.5× / 0.75× / 1× / 1.5× / 2×`。**开工那一刻冻结**进 `DelegationFlowState.flowHoursScale`
（用户拍板 `甲`）—— 理由是 `flow.phaseTicks` 是绝对进度，实时读设置会让进行中的委派
"条与字在同一帧跳变"。老存档 / 没有 flow 状态 ⇒ `DelegationFlow.ScaleOf` 一律返回 `1f`，
行为与加倍率之前逐字一致。

**A-3 随机偏移（RIM-11）**：`DelegationPhaseDef.hoursJitter`（小时，空/0 = 不抖）。
掷法定稿（这就是用户要的"偏移值设计逻辑"）：

1. **掷点**：`DelegationFlow.Freeze` —— 与段表、倍率**同一处、同一时刻**，结果存进
   `DelegationFlowState.jitteredPhase` / `jitteredHours`（按 **defName** 存，不按下标，
   因为段表会被敌情过滤，按下标对不齐）。读档**不重掷**，UI 与推进同源。
2. **分布**：均匀 `U(base − j, base + j)`。
3. **量化**：四舍五入到 **0.25 小时**网格。UI 只显示一位小数（`1.8h`），不量化会写出
   `1.8347h` 这种既不可读、又让人误以为精确的数字。
4. **下限**：`0.25h`（再短就不像"做成了一件事"）。
5. **落点**：**只给三个「撤离」段**（各 ±0.5h ⇒ 1.5–2.5h），因为它们都是 `afterWork = true` 的
   **收尾段** —— 抖动不会让"开工那一刻给出的预计完工时刻"变成谎话；前置段抖动会。
6. **关掉的方式**：删掉那一行 `<hoursJitter>` 或写 `0`，行为立刻回到固定值。

#### 机制定稿 B：旁白选池与条件层（RIM-13 / RIM-14）

**B-1 XML 里仍是 `<li>纯文本</li>`，条件的载体是行首方括号指令。**
这是本轮**唯一一处偏离子议题原始描述**的技术选择：子议题写的是
`List<string>` → `List<AmbientLine>`（并因此需要"机器转换 260 条"或"兼容读取层"）。
实证后的选择是：**XML 形状保持不变**，在行首写一段可选的指令块，`ResolveReferences` 之后
按需解析成 `AmbientLine`。理由三条：

- 260 条旁白**一条都不用改写**（旧写法 = 无指令 = 永远可说）⇒ 改动面从"重排 1400 行 XML"
  降到"想加条件的那些行各加一个前缀"；
- RimWorld 的 `<li Class="...">` 逐字段写法会让这个文件膨胀一倍以上，且改一条文案要动五行；
- 解析在**纯 C# 层**（`AmbientLineSyntax`），可以用一个 net472 小工程离线跑用例
  （见"验收证据"），而 XML 结构改动没法离线验。

语法（唯一一份实现在 `Source/AmbientLine.cs` 的注释里）：
`[solo|group|packed|hostile|night|day|early|mid|late | w=N | p=A-B | h=A-B | t=TraitDef:degree | ts=… | c=GameConditionDef | once]`
后接正文。四个池名自带**隐式标签**（`ambientLinesSolo` → `solo`、`…Packed` → `packed`、
`…Hostile` → `hostile`、默认池无标签），所以旧池语义一字不变。

**B-2 选池：短路优先级链 → 多标签并集。**
原来 `Pick` 是"单人池 > 驮兽池 > 有敌情池 > 默认池"，选中前面那个就**再也不看后面** ——
于是"一个人去打有守军的矿点"永远听不到那 5 条敌情旁白，而"战斗评估 / 交战"这种只有敌情池的段
在单人时**一句旁白都没有**。现在四个池**合并成候选集**，每一条各自过 `Matches`：
标签（情境）+ 进度区间 + 当地小时区间 + 特质 + 全球事件 + `once`。

**B-3 权重与特异性**：有效权重 = `weight × (1 + 标签数)`（标签越多越"具体"，越该优先）。

**B-4 随机度三件套**（用户 `6A`）：
① 每条 `weight`（默认 1）；② **不重复窗口** = 最近 `min(候选数 − 1, 3)` 条不重复
（彻底消掉 `A-B-A-B`，`MineLoot`/`WorkLoot` 的 2 条单人池也一样）；
③ 换句间隔改**区间**：`ambientRerollHours` 从 `float` 变成**字符串**
（`"0.5"` = 固定，`"0.5~1.5"` = 每次抽完重掷下次间隔）。

**B-5 存档契约**：掷定结果从"池内下标"改成**稳定 id**（`来源池#池内下标` 的 FNV-1a）。
`AmbientLine.id` 是唯一身份，`Current()` 按 id 找回同一句 —— 候选集随情境变化时下标会指错，
id 不会。老存档 `ambientPickId == 0` ⇒ 走"确定性哈希"兜底（用户拍板 `甲`：读档换一句，不崩、不越界）。
新增 Scribe 键 4 个：`roAmbientPickId` / `roAmbientNextReroll` / `roAmbientRecent` / `roAmbientSeenOnce`。

#### 本轮推翻的三个过期前提（重要）

1. **`HoursLabel` / `totalHours` 不是显示口径**（见 A-1）—— RIM-10 的"四处口径"说法要按 A-1 修正。
2. **「注释 5.5h vs 实算 7.0h」里的 7.0 本身是错的。** 体检脚本按委派逐个复算，
   本机三条"有敌情"路径**全是 6.0h**（开采 = 战术侦察 1 + 战斗评估 1 + 交战 1 + 搜集战利品 0.5
   + 建立营地 0.5 + 撤离 2）。RIM-10 的调研稿把"开采 7 段"和"工作站 7 段"**混算**了 ——
   而工作站那条也有 `WorkScout`（`requireNoThreat`）不参与有敌情分支，实算同样是 6.0h。
   **凡是"某个数字看起来差了一截"的说法，先跑 `tools\Check-DefsXml.ps1` 对一遍再改注释。**
3. **英文 Keyed 的 `--` 早已修掉**（commit `b92bcd9`）。当前两份 Keyed 各 **65 key / 94 行**
   （收口时实测：键集两个方向差集全空、顺序也一致、两份都能被 `XmlDocument.Load` 解析）。
   RIM-1 / RIM-12 / RIM-16 里"59 个 key 作废"的说法全部过期，数字统一成 **65 key / 94 行**。
   ⚠️ 计数细节：这 65 个是**不含 `<LanguageData>` 根节点**的文案 key；`XmlDocument` 的
   `ChildNodes.Count` 是 74 / 76（含注释节点）。
   ⚠️ 其中 `RimDelegationReportCrew` / `RimDelegationReportProduced` 两个键是**并行会话**（RIM-9 跟进：
   报告窗口的参与人 / 产出小节标题）在同一工作树里加的，不是本轮加的 —— 本轮**没有新增任何 Keyed 键**
   （新增的两处设置页文案走的是 `RimDelegationMod.cs` 的既有中文硬编码风格，见遗留第 4 条）。
   本轮把脚本的扫描根从 `Defs\` 扩到 `Defs + Patches + Languages`，从机制上堵住这类"只扫 Defs"的缺口。

#### 落点与不变量 `[验证]`

- **新增文件**：`Source/AmbientLine.cs`（`AmbientLine` + `AmbientLineSyntax` + `AmbientPoolCache` +
  `AmbientPoolSet`）。
- **存档新增**：`DelegationFlowState` 5 个键（4 个旁白 + `roFlowHoursScale` + `roJitteredPhase` /
  `roJitteredHours`）；`RimDelegationSettings` 1 个键（`flowHoursScale`）。
  全部是"新键 + 老存档取默认值"，**不改任何既有键的语义**。
- **[不变量] 总开关关掉时行为与改动前逐字一致**：`DelegationUIUtility.AmbientEnabled` 为假时，
  `MarkActive` **连掷定都不做**（`DelegationAmbient.Tick` 也照旧在 UI 层被跳过），
  池的解析结果一个字都不影响画面。
- **[不变量] 段表冻结语义不变**：`hoursJitter` 与倍率都挂在"开工冻结"这条路线上，
  不新增第二个冻结点。
- **[不变量] `TicksPerHour` 绝不被倍率改**：它另有 12 个无关读者（StallFor、报告耗时、
  历史、事件冷却、旁白间隔），改它就是改"已发生时间"的折算口径。

#### S38 验收步骤

① `tools\Check-DefsXml.ps1` 退出码 **0**（10 个 XML 良构 + 18 段流程零 WARN）；
② `_probe_ambient` 的解析用例 **57/57 通过**（指令语法 / 隐式标签 / 稳定 id / 写错必报）；
③ 进游戏看启动横幅：`DelegationPhaseDef 18 个`，且**没有**新的 Warning（尤其是
   "同名流程段时长不一致"与"旁白池总共只有 ≤1 条"这两条 —— 数值改对之后不该亮）；
④ 委派一次搜刮：待下达草稿页应出现「固定流程：侦察环境 + 移动到目标区域 + 破门 + 撤离 共 6.0 小时
   （与人数/技能无关）」一行，且"预计 X–Y 天完"比改动前**多约 0.25 天**（= 6h）；
⑤ 设置页把倍率切到 `2×`，**新开**一条委派：流程条上写的是 `2h / 2h` 这一类翻倍值，
   而在途的老委派**不变**（冻结语义）；切回 `1×` 后新委派与改动前逐字一致；
⑥ 旁白：单人打一个**有守军**的矿点，在「战术侦察」段应能出现敌情句（改动前永远不会）；
   换句间隔不再是整齐的 0.5h；
⑦ 读一个改动前的存档：不报错、不越界，旁白会**重掷一条**（预期行为）；
⑧ 关掉「流程显示随机描述」⇒ 流程块里没有任何旁白，且与改动前逐字一致。

#### 遗留（未拍板 / 另立议题）

1. **`4B`「段耗时 × 技能」仍未做**（用户拍板单独立项）——`DelegationPhaseDef.skillDef`
   目前只决定"谁去做 + 怎么显示"，不决定耗时。
2. **`2C` 三档预设（写实 / 标准 / 快餐）**未做：等倍率落地后作为"打包层"另立。
3. **天气层仍然不做**（实证：`RimWorld.WeatherManager` 的唯一宿主是 `Verse.Map.weatherManager`，
   `WorldWeather` 在整个 Managed 目录 0 命中，`Planet.Tile` / `Site` 无任何天气成员）。
   将来若要，替代路径是"世界级 GameCondition → `GameConditionDef.weatherDef` → 某地图天气"，
   而不是"读世界某点的天气"。
4. **设置页文案仍是 C# 里的中文字面量**（`RimDelegationMod.cs`，全文件 1400+ 个 CJK 字符），
   本轮新增的两处也照同一风格写。要可翻译必须"加 Keyed 键 + 把字面量换成 `.Translate()`"两件事一起做。

