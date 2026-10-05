# RimDelegation - Radius UI（试验）

把 **RimDelegation 的三块 UI** 换成 **Radius UI** 的绘制，用来低风险看清「Radius UI 这套皮」值不值得引入：

| 靶子 | RimDelegation 侧的方法 | 触发方式 |
|---|---|---|
| **委派对话框** | `Dialog_ChooseDelegation.DoWindowContents(Rect)` | 点「委派」gizmo / 浮菜单 |
| **远行队「委派」页签** | `WITab_Caravan_Delegation.FillTab()` | 世界地图选中正在委派的远行队 → 检视面板「委派」页签 |
| **委派主控台窗口**（S8-b） | `Window_Delegations.DoWindowContents(Rect)` | 屏幕底部「委派」按钮（没有在途委派时是灰的） |

这是**试验性 mod**，不打算上创意工坊。

---

## 目录

```
RimDelegation-RadiusUI\
  About\About.xml                      name = RimDelegation - Radius UI
                                       packageId = duskmelon.rimdelegation.radiusui
  Assemblies\RimDelegation.RadiusUI.dll    编译产物（Assemblies\ 里只应有这一个 dll）
  Source\
    RimDelegation.RadiusUI.csproj
    ModBoot.cs                         [StaticConstructorOnStartup]：代数声明 + 挂两块补丁
    RadiusUISkinMod.cs / .Settings.cs  Mod 入口与设置页（设置页本身用框架的 SettingsPage 画）
    SkinPatch.cs                       两块补丁点的管理 + 两个入口前缀（含独立失败保护）
    DialogBridge.cs                    对话框反射桥（18 个私有字段）
    DelegationDialogSkin.cs            对话框的 Radius UI 绘制
    CaravanTabBridge.cs                页签反射桥（4 个成员）
    CaravanTabSkin.cs                  页签的 Radius UI 绘制
    DelegationConsoleSkin.cs           主控台窗口的 Radius UI 绘制（左列表 / 右详情 / 底部操作条）
    ConsoleWindowBridge.cs             反射替换 Verse.Window.windowDrawing（圆角窗口底 + 框架关闭按钮）
    SkinButtons.cs                     两块皮肤共用的红"危险"按钮
    PawnRowRenderer.cs                 两处共用的人员行渲染
  build.ps1 / deploy.ps1
```

---

## 依赖与加载顺序

| | |
|---|---|
| RimWorld | 1.6 |
| Harmony | `brrainz.harmony`（必需） |
| Radius UI Framework | `astryl.RadiusUI.Framework`（必需，generation 32） |
| RimDelegation | `duskmelon.rimdelegation`（必需） |

加载顺序：**Harmony → Radius UI Framework → RimDelegation 边缘委派 → RimDelegation - Radius UI**。
前三项已写进 `About.xml` 的 `modDependencies` 与 `loadAfter`，游戏会自动排。

---

## 构建 / 部署

```powershell
& .\build.ps1     # 产物写到 .\Assemblies\RimDelegation.RadiusUI.dll
& .\deploy.ps1    # 部署到游戏 Mods 目录（或 -Link 做符号链接）
```

`build.ps1` 会先检查四个外部引用是否都在，缺哪个就直接报路径。

---

## 它到底改了什么（机制）

**三个补丁点，各管一块 UI，互不牵连：**

```
Harmony Prefix → Dialog_ChooseDelegation.DoWindowContents(Rect)   返回 false ⇒ 阻断原版绘制
Harmony Prefix → WITab_Caravan_Delegation.FillTab()               返回 false ⇒ 阻断原版绘制
Harmony Prefix → Window_Delegations.DoWindowContents(Rect)        返回 false ⇒ 阻断原版绘制
```

第三块的**窗口底**不是靠补丁换的：`Verse.Window.windowDrawing` 是私有字段，`ConsoleWindowBridge`
反射把它换成 `ConsoleWindowDrawing`（`CardChrome` 圆角面板 + `UIKit.Flat.CloseX`）。之所以能把底板
画满整个窗口，是因为反编译显示 `windowDrawing.DoWindowBackground(windowRect.AtZero())`
**发生在 `BeginGroup(inRect)` 之前** —— 所以这一块没有对话框那 18px 透明边的老毛病。

窗口/页签实例、候选名单、`onConfirm`/`onDefer` 回调、已掷定的存量（`DelegationDeposit`）、
当前委派对象、暂停/中止入口 —— **全部由 RimDelegation 自己造好**，本 mod 只借来画。

页签那边的数据**几乎全走公开 API**（`Delegation` / `WorldObjectComp_Delegations` /
`DelegationRegistry` 全是 public），文字与格式串沿用 RimDelegation 自己的 Keyed 键，
所以措辞不会漂移，改的只有排版、配色、字阶与控件外观。

因此下列行为与 RimDelegation 原版**逐字一致**（都是读它的状态或调它的方法）：

- 对话框：勾选人员 / 全选 / 全不选 / 三种排序；切换模式（直接调用它私有的 `OpenModeMenu()`）；
  结束条件三选一 + 天数/配额步进（上限用与 `QuotaCap()` 相同的算法）；「补给耗尽时中止」；
  「确认委派」的校验与 `DelegationRequest` 组装、`Close()` 与回调调用顺序；「延后决定」只在未抵达时出现
- 页签：进度与存量、参与者排序、暂停 / 继续、中止、超重与失效倒计时提示

**没有做的事**（刻意的）：

- 不动 `WorldObjectComp_Delegations` 与 `CaravanArrivalAction_StartDelegation` 里那三个
  `new Dialog_ChooseDelegation(...)` 调用点 —— 它们在 lambda 与 `Arrived()` 里，签名一变就崩。
- 不 patch `WindowStack.Add` —— 全局热路径。
- **不接管世界地图检视面板本身**（页签行 / 面板边框）。套件里的 `Radius UI - Inspector` 也做不到：
  它的 `InspectorCore.Classify()` 有 `!WorldRendererUtility.WorldRendered` 条件，
  且 `PaneMode` 只识别 `Thing`/`Pawn`/`Zone`，没有 `WorldObject` 分支。

---

## 与 RimDelegation 内部实现的耦合面（唯一风险点）

### 对话框：18 个私有字段 + 1 个可选私有方法

`caravan` `site` `def` `onConfirm` `onDefer` `candidates` `selected` `preview` `exactDeposit`
`dispMinCells` `dispMaxCells` `onTile` `mode` `endCondition` `daysLimit` `quotaUnits`
`abortWhenOutOfFood` `sortMode`；方法 `OpenModeMenu()`（拿不到时退化为本 mod 自带的等价实现）。

### 页签：4 个成员

| 成员 | 来源 | 用途 |
|---|---|---|
| `SelCaravan` | `RimWorld.Planet.WITab`（protected 属性） | 取当前远行队 |
| `size` | `Verse.InspectTabBase`（protected 字段） | 页签画布尺寸 |
| `sortMode` | `WITab_Caravan_Delegation`（private 字段） | 参与者排序 |
| `displayPawns` | `WITab_Caravan_Delegation`（private 字段） | 显示用副本（原版也用它） |

以上全部在**编译好的 `RimDelegation.dll` / `Assembly-CSharp.dll` 上逐项核对过**，并且 `RimDelegation.dll`
的 SHA256 与游戏 `Mods\RimDelegation` 里那一份一致 —— 反射目标就是游戏会加载的那一份。

### 失败策略（三层，两块独立）

1. **解析失败** → 这一块干脆不打补丁，游戏里就是 RimDelegation 原版；`Player.log` 写清缺了哪个成员。
   还带**类型校验**：字段名对但类型变了也会被挡下，避免 `as` 静默返回 null 变成"点了没反应"。
2. **绘制异常** → 当帧交还原版；连续 3 次则**本会话停用这一块**并解锁补丁，另一块不受影响。
3. **随时可关** → 设置页有总开关 + 两块各自的开关，立刻生效，不需要重启。

---

## 设置页

`选项 → Mod 设置 → RimDelegation - Radius UI`

- **总开关**
- **委派对话框** / **远行队「委派」页签** / **委派主控台窗口**（各自可关，便于 A/B 对照）
- **详细日志**
- **补丁状态** —— 三行分别告诉你每块补丁是否可用；不可用时说明「已回退原版」

---

## 已知限制（先看这几条再判断值不值）
1. **对话框第 1 帧会闪一下原版底**。原版 `Window.InnerWindowOnGUI` 是在调用 `DoWindowContents`
   **之前**就读掉 `doWindowBackground` / `doCloseX` 的，所以只能从第 2 帧起生效。单帧，肉眼不可辨。
2. **对话框四周有 18px 透明边**。原版 `Window.Margin` 默认 18，`DoWindowContents` 拿到的 `inRect`
   已被内缩且 GUI 组被裁剪到该范围，面板覆盖不过去。副作用是原版阴影也一起没了。
3. **页签布局仍是"固定画布 + y 累加"**。RimDelegation 源码里明确写了这里不能用
   `Widgets.BeginScrollView`（时机不可靠，曾导致面板全空白），所以本 mod 沿用同一策略。
   S8 起底部预留不再是常量，而是与原版同一条**纯函数**（流程/加班/疲劳/伙食/状态/按钮的
   高度全部先算出来），所以放不下的参与者会先截断并给出「…还有 N 人未显示」，
   必要时还会**自动折叠现场物资**——两端的判据公式相同，只有行高不同。
4. **页签不铺 Radius 底板**。原版 `InspectTabBase.DoTabGUI()` 把页签画在一个
   `ImmediateWindow(doBackground: true)` 里，并且在调用 `FillTab()` **之前**就先画了右上角的
   关闭方块（`Widgets.CloseButtonFor`，固定在 `(width-22, 4)` 的 18×18 位置）。任何铺满画布的
   底板都会把它盖住，玩家就看不见关闭按钮了 —— 所以页签保留原版窗口底，只换内容排印。
   想要整块 Radius 卡片的话，需要让卡片从 `y=26` 以下开始（避开那条 18px 的按钮带）。
5. **人员行的头像 / 心情角标 / 受伤角标 / 信息卡按钮**仍是 RimDelegation 的实现
   （`DelegationUIUtility` 的那套公开子件）。只有底纹、勾选框与文字换成了 Radius UI。
6. **纵向空间来自 RimDelegation 的 `UpdateSize()`**（宽 620，高 `Clamp(PaneTopY-130, 260, 620)`）。
   框架的 `SectionHeaderH` / `RailEntryH` 一变高，参与者列表就会少显示几行。
7. **框架只有英文 Keyed**，所以 `选项 → Mod 设置 → Radius UI`（框架自己的设置页）在中文环境下
   显示英文。本 mod 的界面文字硬编码中文，与 RimDelegation 自身做法一致。
8. **皮肤与 RimDelegation 的程序集签名是硬绑定的** —— 本 mod 引用的是工作区里那份
   `..\RimDelegation\Assemblies\RimDelegation.dll`，所以 **RimDelegation 改了公开签名就必须重编本 mod**。
   2026-09-26 那次事故就是它：RimDelegation 把 `DelegationWorker.EstimateUnitsPerDayFor` 从 3 参加到 4 参
   （补 `Site`，物资点的速率取决于现场物件质量），旧皮肤在 `DelegationDialogSkin` 第 226 行抛
   `MissingMethodException` → 连炸 3 次 → `SkinPatch.TryDraw` **停用整场会话**、退回原版对话框。
   `Player.log` 的判据：`[RimDelegation-RadiusUI] 委派对话框 绘制异常（第 N/3 次）：System.MissingMethodException`。
   编译顺序因此固定：**先 RimDelegation，后本 mod**。
9. **主控台的窗口底是靠反射换的**（`Verse.Window.windowDrawing`）。原版把那个字段改名或改类型时，
   这一块会**只退掉窗口底**（内容仍然是 Radius 排版，`InstallChrome()` 返回 false 时自己铺一张卡片兜住），
   并在 `Player.log` 写一行 `换主控台窗口底失败…`；补丁状态那一行则始终显示"已启用"。
   顺带：装上窗口底时会把该窗口的 `drawShadow` 关掉（圆角面板配方形阴影会毛边）。
10. **主控台窗口的位置/尺寸记忆由 RimDelegation 自己存**（`RimDelegationSettings`），皮肤不参与；
    所以开关皮肤不影响"窗口记得上次拖到哪"。

---

## 怎么验（进游戏后 5 分钟）

**A. 委派对话框**

1. 开发者模式打开，世界地图选一个有 `PreciousLump` 的矿点，对车队点「委派」gizmo。
2. 逐项对照行为，任何一项和以前不一样就是 bug：
   - 勾选 / 全选 / 全不选 → 底部「已选 N 人」当帧就变
   - 排序按钮 → 列表顺序变
   - 点「委派模式」行 → 弹出的是**原版 FloatMenu**（这是对的，菜单仍归 RimDelegation）
   - **有姿态轴的委派**（囚犯营救：强攻 / 潜入）→ 模式行右侧应多出「姿态：X」一格，
     点它弹出原版姿态菜单；底部出现「姿态成算」一行；`确认委派` 后**结算用的是选中的姿态**
     （潜入成功则无战斗、失败则守军先手）
   - 物资藏匿点（搜刮）→ 结束条件写「搬空为止」、量纲是**件**、勾选框是「饿着也继续搜刮」
   - 结束条件点「按天数」/「按产出」→ 步进器出现，`-` / `+` 到边界时按钮变灰
   - 未在目标格时才有「延后决定」
   - 「确认委派」在人不够时只弹红字、不关窗

**B. 委派页签**

3. 让委派跑起来，世界地图选中该远行队 → 检视面板点「委派」页签。
4. 对照：
   - 进度条数值与「本次进度：X / Y 格（Z）」一致
   - 「排序」按钮点了参与者顺序会变
   - 「暂停委派 / 继续委派」文案随状态切换，点了真的停/续
   - 「中止委派」会走原版中止流程（会来一封信）
   - 参与者多于画布容量时，末尾出现「…还有 N 人未显示」

**C. 委派主控台窗口（S8-b）**

6. 底部「委派」按钮点开窗口，对照：
   - 窗口底是**圆角深色面板**（不是原版方形灰底），右上角是框架画的关闭 ✕；
   - 左栏每行：`车队名 @ 地点名` / `活动 · 状态` + 右端百分比 / 一条细进度条；选中行有**强调色竖条**；
   - 右栏按分区排（状态 / 总进度 / 现场物资 / 参与者 / 流程 / 车队），**能滚动**，
     滚到底不会缺行（滚动条拖到最下应能看到「车队」区最后一行）；
   - 底部操作条：`暂停委派` / `紧急加班 +4h`（红）/ `结束加班`（仅加班中）/ `中止委派`（红）/ `去页签看这队`；
   - 四栏列头（左栏 / 流程 / 主信息 / 概览）各有一颗 `－`（收起）/`＋`（展开），**它左边是图钉 PIN**：
     钉住 = 这一栏固定展开（不吃悬浮焦点），点 `－` 会顺带拔钉；
   - 悬浮焦点：鼠标在**流程栏** ⇒ 展开流程、收起左栏；在**左栏** ⇒ 只展开左栏、**不收起流程**
     （两栏宽度挤不下时流程栏仍会回落进主列）；鼠标离开两栏 ⇒ 回到你自己的折叠设置；
   - 关掉窗口再开 → 位置与大小仍在（这是 RimDelegation 存的，换皮肤不影响）。

**D. 开关与回退**

5. `选项 → Mod 设置 → RimDelegation - Radius UI`，分别取消两块勾选，再各开一次 —— 应当立刻变回原版。
6. 设置页底部两行「补丁状态」应当都是「已启用（Radius UI 皮肤接管）」。

日志：`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`
搜 `[RimDelegation-RadiusUI]`。启动时会有一行：

```
[RimDelegation-RadiusUI] 已加载 | 委派对话框：启用（…） | 委派页签：启用（…）
```

---

## 回滚

- 想临时看原版：设置页关掉对应开关（或总开关）。
- 想彻底不要：从 Mods 目录删掉 `RimDelegation-RadiusUI` 文件夹，并取消勾选。
- 本 mod **不写任何存档数据**，只写自己的 Mod 配置（开关 / 折叠 / 图钉 / 悬浮焦点等阅读偏好 + 一个置顶站点 ID 列表），
  增删都不影响存档。

---

## 许可与出处

- 本 mod 的源码是你自己的（duskmelon）。
- 它引用并调用 **Radius UI Framework**（作者 astryl，MIT License）。MIT 允许这样引用；
  若以后要把框架代码内联进本工程，随附其 LICENSE 即可。
- 框架的「代数」契约：本 mod 在 `ModBoot` 里调用 `FrameworkVersion.Require("RimDelegation - Radius UI", 32)`。
  框架升级到更高代数、而本 mod 从源码重建时，必须和框架一起重建。
