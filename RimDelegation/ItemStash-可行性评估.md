# RimDelegation × ItemStash 可行性评估

> 问题：委派框架能否支持 `ItemStash`（物品藏匿点）？
>
> 结论：**能，但不是"只加 XML def"**（与 DESIGN §9 的框架验收标准不符）。
> 需要 **1 个新 `DelegationWorker` 子类 + 3 处小改**；主线（超时暂停 / 双入口 / 小时交付 / 心情 / 中断撤回 / 超重提醒）**零改动全部复用**。
>
> ✅ **已实施完成**（见文末 §7「实施记录」）：MSBuild Release 0 error / 0 warning，
> 纯 `Rebuild` 后元数据校验通过。
>
> 证据来源：`rimsearcher`（Def 真值）+ 1.6 Assembly-CSharp 反编译 + 本仓库现状源码。

---

## 1. 好消息：ItemStash 的内容在"建点时"就已存在（比矿点更干净）

`SitePartWorker_ItemStash.Notify_GeneratedByQuestGen` `[验证]`：

```csharp
ThingDef single = slate.Get<ThingDef>("itemStashSingleThing");
IEnumerable<ThingDef> many = slate.Get<IEnumerable<ThingDef>>("itemStashThings");
if (single != null)      list = { ThingMaker.MakeThing(single) };
else if (many != null)   list = many.Select(ThingMaker.MakeThing).ToList();
else {
    float x = slate.Get("points", 0f);
    list = ThingSetMakerDefOf.Reward_ItemsStandard.root.Generate(new ThingSetMakerParams {
        totalMarketValueRange = new FloatRange(0.7f, 1.3f)
                              * QuestTuning.PointsToRewardMarketValueCurve.Evaluate(x)
    });
}
part.things = new ThingOwner<Thing>(part, oneStackOnly: false);
part.things.dontTickContents = true;                       // 不 tick，纯静态库存
part.things.TryAddRangeOrTransfer(list, canMergeWithExistingStacks: false);
slate.Set("generatedItemStashThings", list);               // 任务描述里直接用这份清单
```

落地位置与持久化 `[验证]`：

| 事实 | 位置 |
|---|---|
| 实物清单存在 `SitePart.things`（`ThingOwner<Thing>`） | `SitePart` 字段 |
| **不解包地图也能存读档** | `SitePart.ExposeData` → `Scribe_Deep.Look(ref things, "things", this)` |
| 地图生成时优先用这份清单 | `GenStep_ItemStash.ScatterAt` → `if (parms.sitePart.things != null && parms.sitePart.things.Any) stockpileConcreteContents = parms.sitePart.things` |
| 为空才退到第二个容器 | `else if (map.Parent.GetComponent<ItemStashContentsComp>().contents.Any)` |
| 再为空才**重新掷** | `else thingSetMakerDef ?? ThingSetMakerDefOf.MapGen_DefaultStockpile` |

**这比矿点(PreciousLump)有利得多**：矿点只存了矿种（`SitePartParams.preciousLumpResources`），格数要靠委派复刻
`GenStep_PreciousLump.Generate` 的公式 —— 这正是 DESIGN §7.1 承认"仍未堵住"的那个两次独立掷骰的口子。
**ItemStash 存的是实物本身，0 复刻、0 二次掷骰。**

顺带天然解决双吃：

- 取走**一部分** → `part.things` 还有剩 → 以后真进图，BaseGen 用的就是剩下的 → **天然一致**
- 全部取走 → `part.things` 空 → 若还进图会重掷一份 → **必须销毁地点**（`destroyTargetOnComplete`，已有机制）

---

## 2. 坏消息：现有框架是"连续格数模型"

`DelegationDeposit` / `Delegation` 把"目标"抽象成**单一资源 + 统一单位产出 + 连续进度**：

| 字段 | 位置 | 问题 |
|---|---|---|
| `ThingDef resourceDef`（单值） | `DelegationDeposit.cs:19` | 装不下异构清单 |
| `int yieldPerUnit`（统一） | `DelegationDeposit.cs:28` | 每件物资产出不同 |
| `UnitsRemaining` / `IsDepleted` | `DelegationDeposit.cs:39,41` | 基于 `totalUnits` 整数 |
| `totalCells` / `cellsMined` / `yieldPerCell` | `Delegation.cs:28-29,32` | 全部以"格"为单位 |
| `TargetDepleted` / `IsComplete` / `Progress` | `Delegation.cs:226,228,230` | 硬编码 `cellsMined >= totalCells` |
| `EstimatedDaysLeft` | `Delegation.cs:240` | `(totalCells - cellsMined) / perDay` |

宿主 `WorldObjectComp_Delegations` 有 5 处直接读 cells：

| 行 | 用途 | 需要改吗 |
|---|---|---|
| `:196` | inspect `"本次可采 {d.totalCells} 格"` | ⚠️ 文案 |
| `:378` | 开始日志 `"目标 {active.totalCells} 格"` | ⚠️ 文案 |
| `:521` | `if (d.TargetDepleted) Complete(...)` | ✅ 可用 |
| `:678` / `:697` | `trulyDepleted` → `site.Destroy()` | ✅ 可用 |
| `:815` | DEV「立即完成委派」`d.cellsMined = d.totalCells` | ✅ 可用 |

### ★ 关键：不需要改宿主，只要 worker 自己填 cells

`DelegationWorker_Mining.OnStart` `[验证]` 就是这么做的：

```csharp
public override void OnStart(Delegation d, Site site) {
    DelegationDeposit dep = d.deposit;
    d.resourceDef   = dep.resourceDef;
    d.yieldPerCell  = dep.yieldPerUnit;
    d.totalCells    = dep.UnitsRemaining;
    dep.timesDelegated++;
}
```

⇒ **`totalCells` 是 worker 自己写的**。只要约定 `1 格 = 1 件物资`（或 1 单位市场价值），
`TargetDepleted` → `Complete` → `site.Destroy()` 这条链**原样可用**。

反之若 worker 不管 `totalCells`：`totalCells` 恒 0 ⇒ `TargetDepleted` 恒 false ⇒
`EndConditionReached` 对 `UntilDepleted` 返回 null（`Delegation.cs:220`）⇒ **委派永不结束，地点永不销毁，进图重掷 = 双吃**。

---

## 3. 最小改动清单

> ⚠️ 本节是**动工前的评估**。实际落地时比这里多做了三件事（`WorkerEndReason` 收工出口、
> 量纲 `UnitName/WorkVerb`、跨 tick 的 `haulCarryOverKg`）—— **以文末 §7「实施记录」为准**。
> 另外第 1 项的实现与当时的设想不同：没有"待交付缓冲"，而是**边搬边装车**（`d.oreUnits` 恒为 0），
> 这样中断天然等于"已搬走的都在车上"。

| # | 文件 | 改动 | 必要性 | 状态 |
|---|---|---|---|---|
| 1 | **新增** `Source/DelegationWorker_TakeItemStash.cs` | `RollDeposit` 读 `site.parts[].things` 定 `totalUnits`；`OnStart` 照抄 mining；`Tick` 推进 `cellsMined` 并把 Thing 移进待交付缓冲；`FlushDeliveries` 用 `caravan.AddPawnOrItem(thing, false)` 真交付；覆写 `GetGizmoIcon` / `PreviewLabel` / `DeliverySummary` / `ProgressLabel` / `PawnDetail` | **必须** | ✅ |
| 2 | **新增** XML def `RimDelegation_TakeItemStash` | `targetSitePartTags = [ItemStash]`，其余照抄矿点 def | **必须** | ✅ |
| 3 | `DelegationEventDef.cs:184` 富矿脉 | `d.totalCells += n` 对非采矿委派是**硬 bug**（见 §4.1），需按 worker/def 过滤 | **必须** | ✅ |
| 4 | `WorldObjectComp_Delegations.cs:196,378` | "格" 文案改成 worker 提供 | 建议 | ✅ |
| 5 | `Dialog_ChooseDelegation.cs:287` | `preview.resourceDef?.building?.mineableThing` 对物资点为 null ⇒ 选人框"产物"一行会空 | 建议 | ✅ |

`WorldObject.GetComponent<T>()` 存在 `[验证]`（`WorldObject` 有 2 个重载，找不到时静默返回 null）—— DESIGN 里那条 `[待实测]` 可以划掉。

---

## 4. 4 个 ItemStash 特有陷阱

### 4.1 ★ 富矿脉会让委派永不完成（硬 bug）

`DelegationEventDef_BonusYield.Apply` `[验证]`：

```csharp
if (d.totalCells > 0) d.totalCells += n;   // 凭空加格数
```

而 `RollEvents`（`WorldObjectComp_Delegations.cs:541-592`）**遍历全部 `DelegationEventDef`，没有按 `DelegationDef` 过滤**，只有 `CanFire(d, site)` 一道闸。
⇒ 物资点委派触发富矿脉 → `totalCells` 变大但**没有真的多出物资** → `cellsMined` 永远追不上 → 永不完成 + 永不销毁 + 进图重掷双吃。

修法（择一）：`CanFire` 里判 `d.Worker is DelegationWorker_Mining`；或给 `DelegationDef` 加 `allowedEventTags` / 事件倍率表。

### 4.2 塌方语义错

`DelegationEventDef_CaveIn.CanFire` 只看 `d.oreUnits >= 5f`，文案是"矿洞顶板塌了一块"。
地表物资点用它很怪；`oreUnits` 若被 stash worker 复用为"已搬出待运"缓冲，则**会真的埋掉玩家已到手的东西**。同 4.1 一起过滤。

### 4.3 85% 概率带敌人

`OpportunitySite_ItemStash` `[验证]`：`siteThreatChance = 0.85`，标签 `ItemStashQuestThreat` 的池是
`AmbushEdge` / `AmbushHidden` / `Manhunters` / `SleepingMechanoids` / `MechCluster` / `Turrets` / `Outpost`。
⇒ **绝大多数物资点有守军**。另外该任务用 `hiddenSitePartsPossible = true` 调用 `Util_GenerateSite`（矿点是 `false`）
⇒ 威胁件可能是 `hidden`，玩家在检视面板上看不到。
这正是 `WorldObjectComp_ThreatAssessment` + `CombatSimulator` 的用武之地 —— 建议委派入口强制走一次威胁评估。

### 4.4 超时是 12–28 天（不是 30），且 `HasWorldObjectTimeout` 会翻转

`OpportunitySite_ItemStash` `[验证]`：`delayTicks = $(randInt(12,28)*60000)`、`inSignalDisable = "site.MapGenerated"`。
机制与矿点同构 ⇒ `TryPauseTimeout` / `ResumeTimeout` **零改动可用**（它扫的是 `QuestPart_WorldObjectTimeout` 且 `worldObject == site`）。

但注意副作用：`SitePartWorker_ItemStash.GetPostProcessedThreatLabel` 读 `site.HasWorldObjectTimeout`，
而 `Site.WorldObjectTimeoutTicksLeft` 在计时被 Disabled 后返回 `-1` ⇒ **暂停期间地点的检视字符串会变**。功能无害，视觉上要知道。

---

## 5. 建议的 XML def 草案

```xml
<RimDelegation.DelegationDef>
  <defName>RimDelegation_TakeItemStash</defName>
  <label>远程搜刮</label>
  <description>把物资藏匿点委派给车队：他们会在原地把藏匿的物资搬上车，按天消耗营养，搬空后收工。</description>
  <workerClass>RimDelegation.DelegationWorker_TakeItemStash</workerClass>

  <targetSitePartTags><li>ItemStash</li></targetSitePartTags>

  <commandLabel>委派：搜刮 {0}</commandLabel>
  <planningLabel>前往委派搜刮</planningLabel>
  <planningReportString>正在前往 {0} 执行委派搜刮</planningReportString>

  <minPawns>1</minPawns>
  <maxPawns>12</maxPawns>
  <!-- skillDef 留空：搬运不吃技能；若要，用 Hauling 无效，建议用 Mining 之外的通用项或不发 XP -->

  <modes>
    <li>RimDelegation_Mode_Relaxed</li>
    <li>RimDelegation_Mode_Normal</li>
    <li>RimDelegation_Mode_Overtime</li>
    <li>RimDelegation_Mode_AroundTheClock</li>
  </modes>
  <defaultMode>RimDelegation_Mode_Normal</defaultMode>

  <handleTargetTimeout>Pause</handleTargetTimeout>
  <destroyTargetOnComplete>true</destroyTargetOnComplete>
  <abortIfCaravanLeavesTile>true</abortIfCaravanLeavesTile>

  <defaultEndCondition>UntilDepleted</defaultEndCondition>
  <maxDaysLimit>30</maxDaysLimit>

  <completeMoodThought>RimDelegation_Thought_DelegationCompleted</completeMoodThought>
  <abortMoodThought>RimDelegation_Thought_DelegationAborted</abortMoodThought>
</RimDelegation.DelegationDef>
```

> `destroyTargetOnComplete = true` 在这里不是可选项：`part.things` 空掉后若还进图，`GenStep_ItemStash` 会走
> `thingSetMakerDef` 分支**重掷一份新物资**。

---

## 6. 未验证项

- `DelegationEventDef_PawnAccident` 等其余事件对物资点委派是否同样需要过滤 —— 未逐个核对 `CanFire`。
  （Setback / Quarrel / Accident 三种与"挖矿"无关，刻意保持对所有委派生效。）
- `ThingSetMakerDefOf.Reward_ItemsStandard` 是否可能产出 Pawn —— 若可能，装车路径的 pawn 分支
  需要复核（`Caravan.AddPawnOrItem` 本身已有该分支，本实现统一走它）。
- **搬运速率的手感**：`HaulTripsPerWorkHour = 0.25` 是量纲推导出来的合理值，不是实测标定。
  需要一次真机存档验证（60 kg / 200 kg 藏匿点的实际天数）。

---

## 7. 实施记录（已完成）

### 7.1 Bug 修复：随机事件没有按 DelegationDef 过滤

| 文件 | 改动 |
|---|---|
| `Source/DelegationEventDef.cs` | 新增 `List<DelegationDef> onlyForDelegationDefs`（`[NoTranslate]`）+ `virtual bool AppliesTo(Delegation, Site)` |
| `DelegationEventDef_BonusYield.CanFire` | 追加不变量闸门 `d.Worker?.AllowsScaleIncrease ?? false` |
| `DelegationEventDef_BonusYield.Apply` | 同样的闸门（**DEV 按钮 `DevFireEvent` 会绕过 CanFire 直接调 Apply**，破坏性实现必须自己把关） |
| `WorldObjectComp_Delegations.RollEvents` | 在 `CanFire` **之前**先问 `def.AppliesTo(d, site)`，异常一并兜住 |
| `DelegationWorker` | 新增 `virtual bool AllowsScaleIncrease => false`（**默认 false = 安全侧**） |
| `DelegationWorker_Mining` | `AllowsScaleIncrease => true`（矿点规模是 `GenStep_PreciousLump` 掷出来的抽象格数） |
| `Defs/RimDelegation_DelegationEvents.xml` | `RimDelegation_Event_CaveIn` 与 `RimDelegation_Event_RichVein` 各加 `<onlyForDelegationDefs><li>RimDelegation_MinePreciousLump</li></onlyForDelegationDefs>` |

**两道闸的分工**：`onlyForDelegationDefs` 管"内容归属"（哪些事件属于哪种活），
`AllowsScaleIncrease` 管"结构不变量"（目标规模能不能被凭空放大）。漏配 XML 时后者兜底。

### 7.2 框架微改（为量纲与"装满即收工"让路）

| 文件 | 改动 | 理由 |
|---|---|---|
| `Source/Delegation.cs` | 新增 `float haulCarryOverKg` + Scribe | **离散物件必须跨 tick 攒预算**。否则只剩"每 tick 至少搬一件"，那是 60 件/秒，速率彻底失效 |
| `Source/DelegationWorker.cs` | 新增 `UnitName`（默认"格"）、`WorkVerb`（默认"采"）、`WorkerEndReason(d, site)`（默认 null）；`EstimateUnitsPerDayFor` 加可选参数 `Site site = null` | 宿主/对话框原本把"格"写死在 4 处；"车队装满"需要一个收工出口 |
| `WorldObjectComp_Delegations.cs` | `:196` / `:378` 改用 `d.Worker.UnitName/WorkVerb`；`Depleted` 分支改中立措辞；`TickDelegation` 在 `TargetDepleted` 之后插入 `WorkerEndReason` 钩子 | 量纲 + 收工出口 |
| `Source/DelegationPreview.cs` | 新增 `float massPerUnit` | 让"干完后车队多重"的预告对非采矿委派也能算 |
| `Source/Dialog_ChooseDelegation.cs` | `DrawMassLines` 改用 `massPerUnit`（退回旧算法的分支保留，行为不变）；估算行改用 `WorkVerb/UnitName`；`EstimateUnitsPerDayFor` 传 `site` | 量纲 + 负重预告 |

### 7.3 新增 `Source/DelegationWorker_TakeItemStash.cs`

**目标定位**：`LootOwner(site, def)` 按**原版 GenStep 的同一优先级**取
`SitePart.things`（标签匹配）→ `ItemStashContentsComp.contents` → null。
用 `Snapshot(ThingOwner)` 而不是 `InnerListForReading`（那个属性在泛型 `ThingOwner<T>` 上，
非泛型只保证 `IEnumerable<Thing>`；快照还顺带避免了 `Take` 期间改容器）。

**进度单位 = 1 件**（`totalCells` / `cellsMined` / `oreDelivered` 复用宿主的"采空 → 销毁"链路）。

**速率口径：按负重**

```
预算(kg) = Σ_可作业者 MassUtility.Capacity(pawn)   // = BodySize × 35f，人类 1.0 → 35 kg
         × HaulTripsPerWorkHour (0.25)             // 4 小时一趟
         × (delta / 2500)                          // 作业小时
         × mode.workRateMultiplier
```

然后把预算依次花在现场队首的物件上 —— **件数由质量决定，不由件数决定**；
预算不够一件就跨 tick 攒进 `haulCarryOverKg`。

| 场景（单人，35 kg/趟） | 量纲 |
|---|---|
| 正常模式 8h/天 | 35 × 0.25 × 8 = **70 kg/作业天** |
| 60 kg 小藏匿点 | ≈ 0.9 天 |
| 200 kg 大藏匿点 | ≈ 2.9 天 |
| 4 人 | ≈ 1/4 |

**车队负重是真实的收工理由**：装车真的增加 `Caravan.MassUsage`；剩余空间装不下任何一件时
`WorkerEndReason` 报告"车队已装满"→ 宿主收工 → **没取空 ⇒ 地点不销毁、失效计时恢复** ⇒
剩下的留原地，卸完货可以回来接着委派（`deposit` 跨委派累计，不重掷）。

**预览是精确值**（`minUnits == maxUnits`）：实物本来就在存档里，不用等 GenStep 掷。

**`AllowsScaleIncrease` 保持继承的 false** —— 这是 §7.1 那道不变量在物资点上的落点。

### 7.4 新增 XML def `RimDelegation_TakeItemStash`

追加在 `Defs/RimDelegation_Delegations.xml`：`targetSitePartTags = [ItemStash]`、
`workerClass = RimDelegation.DelegationWorker_TakeItemStash`、
`handleTargetTimeout = Pause`、`destroyTargetOnComplete = true`、`abortIfCaravanLeavesTile = true`、
四种模式与矿点一致、`skillDef` 刻意留空（原版搬运不给经验，想给再填）。

### 7.5 验证

```
MSBuild /t:Rebuild /p:Configuration=Release   →  0 error / 0 warning
输出：RimDelegation\Assemblies\RimDelegation.dll
```

元数据校验（DecompilerServer 加载构建产物）：

| 期望 | 结果 |
|---|---|
| `RimDelegation.DelegationWorker_TakeItemStash`（35 个成员） | ✅ |
| `Delegation.haulCarryOverKg` | ✅ |
| `DelegationEventDef.onlyForDelegationDefs` / `AppliesTo` | ✅ |
| `DelegationWorker.AllowsScaleIncrease` | ✅ |
| `DelegationWorker_TakeItemStash.UnitName` / `WorkVerb` / `WorkerEndReason` | ✅ |

XML 用 UTF-8 显式解析：`RimDelegation_Delegations.xml` 12 个顶层元素（含 2 个 `DelegationDef`）、
`RimDelegation_DelegationEvents.xml` 6 个顶层元素，均通过。

---

## 8. 修订（试玩反馈，S5 轮）：本文档 §1 的"好消息"有前提

> 反馈原话：「物品藏匿点的物品没有正常生成，处于搜刮的时候也显示开采中」+「现场没有可搬运的物资… => 添加判断，如果没有进入，则提示不确定有什么物资；搬完了才显示现场没有可搬运的物资」。

### 8.1 §1 结论的补丁：`part.things` 只在**任务生成路径**被填

§1 说"ItemStash 的内容在建点时就已经存在"——**只对 `QuestGen_Sites.GenerateSite` 那条路径成立**。
非任务生成的藏匿点（或任务生成时 `points = 0`、清单掷成空）时，清单由 `GenStep_ItemStash.ScatterAt`
在**生成地图时**才掷，落到 `SymbolResolver_Stockpile.Resolve` 的 else 分支：

```csharp
if (rp.stockpileConcreteContents != null) { ...摆进地图并 Clear()...; return; }   // ← 任务生成路径走这里
CalculateFreeCells(rp.rect, 0.45f);                                              // ★ 要地图才知道有几格
float num4 = rp.stockpileMarketValue ?? Mathf.Min(cells.Count * 130f, 1800f);
value = new ThingSetMakerParams { techLevel/faction..., totalMarketValueRange = (num4, num4),
                                  countRange = (cells.Count, cells.Count) };
thingSetMakerDef = rp.thingSetMakerDef ?? ThingSetMakerDefOf.MapGen_DefaultStockpile;
```

⇒ 「不进图就知道有什么」这件事**不成立**；原来的 `LootOwner == null ⇒ totalUnits = 0`
会把委派变成 `0/0` 空转（`TargetDepleted` 是 `totalCells > 0 && …`，恒假），
而且预览还把"**还没掷**"说成"**没有**"——用户看到的「物品没有正常生成」正是这句假话。

### 8.2 修法：抵达时按原版同一口径掷一份并**写回 `SitePart.things`**

`DelegationWorker_TakeItemStash.TryRollMissingContents`：

| 项 | 取法 |
|---|---|
| ThingSetMaker | 该 SitePartDef 的 `GenStep_ItemStash.thingSetMakerDef`（**`GenStepDef.linkWithSite` 是 `SitePartDef` 引用**，`SitePartDef.ExtraGenSteps` 就是它的索引）→ Core 为 null ⇒ `ThingSetMakerDefOf.MapGen_DefaultStockpile` |
| techLevel / makingFaction | 地点所属派系（与 `SymbolResolver_Stockpile` 一致） |
| totalMarketValueRange | `[近似]` **1800 银**（原式 `Min(cells×130, 1800)`，7×7 密室 `cells ≥ 14` 就撞上限 ⇒ 绝大多数情况就是这个数） |
| 写回 | `part.things ??= new ThingOwner<Thing>(part, false){ dontTickContents = true }` + `TryAddRangeOrTransfer(list, false)`（与 `Notify_GeneratedByQuestGen` 逐字同构） |

**写回才是关键**：之后 `GenStep_ItemStash` 的第一优先级（`parms.sitePart.things`）就命中了，
所以"委派看到的一份"与"真进图的另一份"是**同一份** —— 这一点比矿点干净（那边是两次独立掷骰，只能靠
`EnterCooldownComp` 封禁进图兜住）。近似的部分（市价上限）按本项目准则②**同时写在代码注释与玩家可见的
预览文案里**。

配套：

- `DelegationDeposit.workerRolledContents`（新字段，已 Scribe）标记"这份清单是委派替原版掷的"；
- `StartDelegation` 新增闸门 `dep.UnitsRemaining <= 0 ⇒ 拒绝开工`（0 存量的委派是必然空转的死局，
  营救点已经没人时同样会踩到）；
- 预览文案从**两态**改成**三态**：有实物（精确值）/ 还没清点（「现场内容未定」）/ 已清点且为空。

### 8.3 措辞与量纲：不再写死"采/格/挖"

`DelegationWorker` 新增 `ActivityName`（搜刮）/ `OutputUnitName`（件）/ `UntilDepletedLabel`（搬空为止）/
`StatusKeySuffix`（`Search`），结束条件文案收成 `DelegationUIUtility.EndConditionLabel` 一份、四处共用。
详见 `DESIGN.md` §15 的 S5 段。

### 8.4 顺带修的硬 bug

作业期间右键该地点仍会看到原版「接近XX」并能进图（`ApplyEntryBlock` 只在 `Complete`/`Abort` 里调）。
修法：进行中每 tick 续期 `EnterCooldownComp`（0.5 天窗口），收工/中断时按"动过存量 + Def 要求"决定保留
还是 `Stop()`。根因、证据与释放规则见 `DESIGN.md` §7.3。

