using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimDelegation
{
    /// <summary>
    /// 挂在 WorldObjectDef[Site] 上的委派宿主（XML patch 注入）。
    ///
    /// 双入口：
    ///   逻辑 1（就地委派）→ GetCaravanGizmos：车队停在事件点上时，车队 gizmo 栏出现委派按钮
    ///   逻辑 2（预先委派）→ GetFloatMenuOptions：车队还没到时，右键地点出现"预先委派"
    /// 两者最终都走 caravan.pather.StartPath(site.Tile, new CaravanArrivalAction_StartDelegation(...))，
    /// 而 AtDestinationPosition() == (caravan.Tile == destTile) 让"已在点上"也会立刻抵达并开工。
    /// </summary>
    public class WorldObjectComp_Delegations : WorldObjectComp
    {
        /// <summary>产出的交付节奏：每 2500 ticks（1 小时）把已采出的部分送进车队库存。</summary>
        public const int FlushIntervalTicks = 2500;

        /// <summary>随机事件的掷骰间隔：游戏内 1 小时（§19.24）。</summary>
        public const int EventRollIntervalTicks = 2500;

        /// <summary>心情记忆的挂载节奏：每 60000 ticks（1 天）。</summary>
        public const int MoodIntervalTicks = 60000;

        public Delegation active;

        /// <summary>
        /// 该事件点的存量。**属于地点、跨多次委派累计**（这是"避免多次采集"的核心）。
        /// 第一次需要时掷定一次，之后永久复用；已采格数记在它身上。
        /// </summary>
        public DelegationDeposit deposit;

        /// <summary>存量是否已被采空（由 deposit 推导，不再单独存一个 bool）。</summary>
        public bool Depleted => deposit != null && deposit.IsDepleted;

        /// <summary>
        /// S24：「前往中」计划那一页的「作战任务」缓存（运行期，**不进存档**）。
        /// 计划期还没有 Delegation 实例可挂，所以这一份跟着地点走；开工后主列用
        /// `Delegation.threatSummary`，两者互不影响。
        /// </summary>
        public DelegationThreatSummary threatSummary = new DelegationThreatSummary();

        // ---- 原版 30 天失效计时的暂停状态（S3）----
        public bool timeoutPaused;
        public int pausedTimeoutRemaining;
        public int pausedAtTick;

        // ---- "委派进行中禁止进入该地点"的记账（S5）----
        //
        // 为什么需要单独记一格：`EnterCooldownComp` 是**共享**的（原版自己的失效计时、
        // 别的 mod 都可能用它）。收工时我们只被允许 `Stop()` 掉**自己起的**那一次，
        // 否则会顺手把别人设的冷却清掉（例：`Props.autoStartOnMapRemoved`）。
        public bool entryBlockSelfStarted;

        /// <summary>
        /// 委派进行中封禁进入的续期窗口（天）。每 tick 检查，剩余不足一半就续到满。
        ///
        /// 用这么短的值而不是"一个够长的大数"：`EnterCooldownComp` 的原版行为是
        /// **地图生成后就自己 Stop()**（`PostMapGenerate`），所以只要保证
        /// "进行中始终 > 0"就够；而短窗口让原版那句"还需 X 才能进入"显示的是一段有意义的时长。
        /// </summary>
        private const float WhileActiveBlockDays = 0.5f;

        // ---- 紧急加班（§19.26）----

        /// <summary>
        /// 这个地点上**被用过几次紧急加班**（跨委派累计，和存量一样属于地点）。
        ///
        /// 为什么记在地点上而不是委派上：玩家会"中止 → 重新委派"接着干，
        /// 记在委派上等于每次都能把心情惩罚重置回第一级，递进惩罚就白设计了。
        /// 心情等级 = 这个计数（第 1 次挂第 1 级、第 2 次挂第 2 级……），
        /// 超出 ThoughtDef 的 stage 数时取最后一级（见 <see cref="EmergencyOvertimeStage"/>）。
        /// </summary>
        public int emergencyOvertimeUses;

        /// <summary>该地点的加班心情等级（按 Def 的 stage 数收敛）。</summary>
        public static int EmergencyOvertimeStage(DelegationDef def, int uses)
        {
            int stages = def?.emergencyOvertimeMoodThought?.stages?.Count ?? 0;
            if (stages <= 0)
            {
                return 0;
            }
            return Mathf.Clamp(uses - 1, 0, stages - 1);
        }

        public Site Site => parent as Site;

        // ================================================================ 在途计划（S12）
        //
        // 用户问："委派是否可以记录在途的委派（例如远行队还在路上，但已经做了决定或推迟决定）"。
        //
        // 事实（反编译 rw16 核实）：
        //   · 计划**本来就被原版持久化** —— `CaravanArrivalAction_StartDelegation`（site/def/request）
        //     挂在远行队的 `Caravan_PathFollower` 上，而它的 `ExposeData` 里有
        //     `Scribe_Deep.Look(ref arrivalAction, "arrivalAction")` + `Scribe_Values.Look(ref destTile, …)`；
        //     `request == null` 就是"延后决定"（`Arrived()` 据此再弹一次对话框）。
        //   · **但 `arrivalAction` / `destTile` 都是 private**（只有 `curPath` / `nextTile` 是 public）。
        //
        // 所以这里**自记一份**（用户拍板方案 A）：零反射、零 Harmony，与原版计划并存但**只读**它。
        // 代价说清楚：多一份状态要跟原版 pather 保持一致 —— 靠 `ValidatePlan()` 每 tick 兜底，
        // 一旦对不上就**丢弃记录**（绝不反向去改原版行为，所以最坏情况只是"列表少一行"）。

        /// <summary>正在前往这个地点、并已下达/延后决定的远行队；null = 没有在途计划。</summary>
        public Caravan plannedCaravan;

        /// <summary>在途计划用的是哪条委派 Def。</summary>
        public DelegationDef plannedDef;

        /// <summary>在途计划的内容（选人 / 模式 / 结束条件）；null = 玩家选了「延后决定」。</summary>
        public DelegationRequest plannedRequest;

        /// <summary>计划下达的时刻（显示"在路上多久了"用）。</summary>
        public int plannedAtTick;

        /// <summary>
        /// 计划里的远行队**实际开始移动**那一刻（`GenTicks.TicksAbs`）。
        ///
        /// 用户 2026-10-05 拍板 4B：满意度来源③「远行时间」从这一刻起算，而不是从"计划下达"起算。
        /// 0 = 还没动过（`Caravan_PathFollower.MovingNow` 一次都没为真 —— 例如原地待命等玩家）。
        /// 开工时这个值会交给 <see cref="Delegation.departTickAbs" />。
        /// </summary>
        public int planDepartTickAbs;

        /// <summary>有没有在途计划（远行队还在路上、抵达就会开工）。</summary>
        public bool HasPlan => plannedCaravan != null && !plannedCaravan.Destroyed && plannedDef != null;

        /// <summary>这个计划是不是"已经做了决定"（false = 延后决定，抵达后再选）。</summary>
        public bool PlanDecided => plannedRequest != null;

        /// <summary>
        /// 记下一份在途计划。两条下达路径最后都会经过这里（见 <see cref="GetFloatMenuOptions" />）。
        ///
        /// 不做任何校验：状态是否还成立由 <see cref="ValidatePlan" /> 每 tick 兜底 ——
        /// 下达那一刻玩家刚做的选择，没必要再问一遍原版。
        /// </summary>
        public void RecordPlan(Caravan caravan, DelegationDef def, DelegationRequest request, bool decided)
        {
            if (caravan == null || def == null)
            {
                return;
            }
            plannedCaravan = caravan;
            plannedDef = def;
            // 「延后决定」刻意存 null 而不是空 Request：Arrived() 与 UI 都用 "request == null" 判这一档
            plannedRequest = decided ? request : null;
            plannedAtTick = Find.TickManager?.TicksGame ?? 0;
            DelegationUtility.LogVerbose(
                $"记录在途计划：{def.defName} @ {Site?.Label}，{caravan.LabelCap}，{(decided ? "已决定" : "延后决定")}");
        }

        /// <summary>丢掉在途计划记录（不碰原版的路径与抵达动作）。</summary>
        public void ClearPlan(string reason = null)
        {
            if (plannedCaravan == null && plannedDef == null)
            {
                return;
            }
            if (!reason.NullOrEmpty())
            {
                DelegationUtility.LogVerbose(
                    $"丢弃在途计划：{plannedDef?.defName} @ {Site?.Label}，{plannedCaravan?.LabelCap ?? "?"} —— {reason}");
            }
            plannedCaravan = null;
            plannedDef = null;
            plannedRequest = null;
            plannedAtTick = 0;
            planDepartTickAbs = 0;
        }

        /// <summary>
        /// 在途计划还成立吗？（每 tick 调一次，便宜：只读几个公开字段）
        ///
        /// 失效的三种情况，都只是"丢掉记录"：
        ///   ① 远行队没了（被事件拉进地图 / 合并拆分 / 销毁）；
        ///   ② 正在走、但目的地已经不是这个地点（玩家改道了）；
        ///   ③ 没在走、人也不在这个格子上（路径被取消/失败）。
        /// </summary>
        private void ValidatePlan()
        {
            if (plannedCaravan == null)
            {
                return;
            }
            Caravan caravan = plannedCaravan;
            if (caravan.Destroyed || caravan.pather == null)
            {
                ClearPlan("远行队已不存在");
                return;
            }
            Site site = Site;
            if (site == null || site.Destroyed)
            {
                ClearPlan("地点已不存在");
                return;
            }
            // 4B（RIM-5）：记下"真的开始走了"那一刻 —— `MovingNow` 第一次为真。
            // 放在最前面：人一上路就该记，之后改道/抵达都不再改它（改道会让整条计划作废并一起丢掉）。
            if (planDepartTickAbs <= 0 && caravan.pather.MovingNow)
            {
                planDepartTickAbs = GenTicks.TicksAbs;
                DelegationUtility.LogVerbose(
                    $"在途计划开始移动：{caravan.LabelCap} → {site.LabelCap}（满意度「远行时间」从此计时）");
            }
            if (caravan.Tile == site.Tile)
            {
                // 人已经到格子上了：等原版这一 tick 走 Arrived() → StartDelegation 自己清；
                // 若它没开工（例如被「取消计划」改成无抵达动作），这条记录也留着 ——
                // 玩家看到的"人到了但没开工"正是实情，比悄悄消失好。
                return;
            }
            if (caravan.pather.Moving)
            {
                if (caravan.pather.Destination != site.Tile)
                {
                    ClearPlan("远行队已改道");
                }
                return;
            }
            ClearPlan("远行队已不在前往该地点的路上");
        }

        /// <summary>
        /// 「取消计划」（用户拍板语义 ②）：**取消抵达动作，但让远行队继续走到那里**。
        ///
        /// 做法是照原版公开 API 重新下一条"到同一格、不带抵达动作"的路径：
        /// `StartPath(dest, null)` ⇒ 抵达时 `arrivalAction == null`，原版什么都不做。
        /// 人已经站在格子上时改为 `StopDead()`：那会连抵达动作一起清掉（否则本 tick 就开工了）。
        /// </summary>
        public void CancelPlan()
        {
            Caravan caravan = plannedCaravan;
            Site site = Site;
            if (caravan == null || site == null)
            {
                ClearPlan();
                return;
            }
            try
            {
                if (caravan.Tile == site.Tile)
                {
                    caravan.pather.StopDead();
                }
                else
                {
                    PlanetTile dest = caravan.pather.Destination;
                    if (!caravan.pather.StartPath(dest, null, repathImmediately: true))
                    {
                        // 重新下令失败（地形变化 / 没路）：这条计划已经无法按原样执行，丢弃记录并说明
                        ClearPlan("重新下令失败");
                        Messages.Message("无法继续前往该地点，委派计划已取消", MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                }
                ClearPlan("玩家取消");
                Messages.Message(
                    string.Format("已取消委派计划：{0} 仍会前往 {1}，但抵达后不会开工",
                        caravan.LabelCap, site.LabelCap),
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception ex)
            {
                // 原版路径 API 抛异常绝不能让 UI 崩：丢记录 + 报一句
                ClearPlan("取消时异常：" + ex.GetType().Name);
                Log.WarningOnce("[RimDelegation] 取消委派计划失败：" + ex.Message, 0x5E0DA);
            }
        }

        public override void CompTickInterval(int delta)
        {
            // 在途计划的兜底校验必须放在委派 tick **之前**，而且与 active 无关：
            // 有计划的时刻恰恰是"还没开工"（active == null）的那段时间。
            ValidatePlan();
            TickDelegation(delta);
        }

        // ---------------------------------------------------------------- 逻辑 1
        public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
        {
            Site site = Site;
            if (site == null || site.Destroyed || site.HasMap)
            {
                yield break;
            }
            if (site.Tile != caravan.Tile)
            {
                yield break;
            }

            if (active != null)
            {
                yield return ModeGizmo(active);
                yield return PauseGizmo(active);
                if (active.def != null && active.def.AllowsEmergencyOvertime)
                {
                    yield return EmergencyOvertimeGizmo(active);
                    if (active.EmergencyOvertimeActive)
                    {
                        yield return CancelOvertimeGizmo(active);
                    }
                }
                yield return AbortGizmo(active);
                yield break;
            }
            if (deposit != null && deposit.IsDepleted)
            {
                yield break;
            }
            // 只在车队停住时提供"就地委派"；路过（MovingNow）不算到位
            if (caravan.pather.MovingNow)
            {
                yield break;
            }

            foreach (DelegationDef def in DelegationUtility.MatchingDefs(site))
            {
                DelegationDef local = def;
                FloatMenuAcceptanceReport report = DelegationUtility.CanStart(caravan, site, local);
                if (!report.Accepted && report.FailReason.NullOrEmpty() && report.FailMessage.NullOrEmpty())
                {
                    continue; // "不适用"→ 不显示
                }
                Command_Action cmd = new Command_Action();
                cmd.defaultLabel = (local.commandLabel ?? local.label).Formatted(site.Label);
                cmd.defaultDesc = local.commandDesc ?? local.description;
                // 图标优先取 worker 给的数据驱动图标（采矿 = 矿物的 uiIcon），否则退回 Def 里的贴图路径
                Texture2D icon = local.CreateWorker().GetGizmoIcon(site);
                if (icon == null)
                {
                    icon = LoadIcon(local.gizmoIconPath);
                }
                if (icon != null)
                {
                    cmd.icon = icon;
                }
                if (!report.Accepted)
                {
                    cmd.Disable(report.FailReason.ToString());
                }
                else
                {
                    cmd.action = () => DelegationDraft.BeginOrDialog(
                        caravan, site, local,
                        request => caravan.pather.StartPath(
                            site.Tile,
                            new CaravanArrivalAction_StartDelegation(site, local, request),
                            repathImmediately: true));
                }
                yield return cmd;
            }
        }

        // ---------------------------------------------------------------- 逻辑 2
        /// <summary>
        /// 右键地点时的委派菜单，分两种情况：
        ///   · 车队**就在这个格子上** → 直接给「就地委派」，点开对话框、确认即开工；
        ///   · 车队还在别处        → 走原版的"挂到 pather 上，抵达即开工"。
        ///
        /// 为什么就地那一支要单独写、不再走 <c>CaravanArrivalActionUtility</c>：
        /// 那个助手是给"**把车队派过去**"设计的（它把 arrivalAction 挂到 pather 上、
        /// 用 <c>pathDestination</c> 做校验）。车队已经在格子上时，它依赖"零长度路径也会
        /// 立刻 `PatherArrived()`"这条引擎细节才生效 —— 能用，但没必要在这里依赖它；
        /// 而且它还会经手 `FloatMenuOption` 的 `revalidateWorldClickTarget`，
        /// 少一层间接就少一个"选项为什么没出来"的可能。
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            Site site = Site;
            if (site == null || site.Destroyed || site.HasMap || active != null || Depleted)
            {
                yield break;
            }
            if (caravan == null || caravan.Destroyed)
            {
                yield break;
            }

            List<DelegationDef> defs = DelegationUtility.MatchingDefs(site);
            if (defs.Count == 0)
            {
                DiagnoseNoMatch(site, "右键菜单");
                yield break;
            }

            bool onTile = caravan.Tile == site.Tile;

            for (int i = 0; i < defs.Count; i++)
            {
                DelegationDef local = defs[i];

                if (onTile)
                {
                    foreach (FloatMenuOption opt in InPlaceOptions(caravan, site, local))
                    {
                        yield return opt;
                    }
                    continue;
                }

                // 选人/选模式/选结束条件在"下计划"时完成，抵达后直接开工
                // （Settings.requireConfirmOnArrival 可改为抵达后再确认一次）
                DelegationRequest chosenRequest = null;

                foreach (FloatMenuOption opt in CaravanArrivalActionUtility.GetFloatMenuOptions(
                    () => DelegationUtility.CanStart(caravan, site, local),
                    () => new CaravanArrivalAction_StartDelegation(site, local, chosenRequest),
                    (local.commandLabel ?? local.label).Formatted(site.Label),
                    caravan, site.Tile, site,
                    confirmActionProxy: startAction => DelegationDraft.BeginOrDialog(
                        caravan, site, local,
                        request =>
                        {
                            chosenRequest = request;
                            // S12：把"已决定"记成在途计划（抵达开工时由 StartDelegation 清掉）
                            RecordPlan(caravan, local, request, true);
                            startAction();
                        },
                        // 延后决定：不带选择先让远行队过去，抵达后再弹一次（那时存量已掷定、显示精确规模）
                        onDefer: () =>
                        {
                            RecordPlan(caravan, local, null, false);
                            startAction();
                        })))
                {
                    yield return opt;
                }
            }
        }

        /// <summary>
        /// 车队已经在目标格上时的「就地委派」菜单项。
        ///
        /// 三态语义照抄 DESIGN §2.2 的实测结论：
        ///   ① 不 Accepted 且 FailReason/FailMessage 都空 → 完全不适用，**不显示**
        ///   ② FailReason 非空                        → 显示并写明原因，**不可点**
        ///   ③ 只有 FailMessage                       → **可点**，点了弹 RejectInput 消息
        /// </summary>
        private IEnumerable<FloatMenuOption> InPlaceOptions(Caravan caravan, Site site, DelegationDef def)
        {
            FloatMenuAcceptanceReport report = DelegationUtility.CanStart(caravan, site, def);
            string label = (def.commandLabel ?? def.label).Formatted(site.Label);

            if (!report.Accepted)
            {
                if (report.FailReason.NullOrEmpty() && report.FailMessage.NullOrEmpty())
                {
                    yield break;   // ①
                }
                if (!report.FailReason.NullOrEmpty())
                {
                    yield return new FloatMenuOption(label + "（" + report.FailReason.ToString() + "）", null);   // ②
                    yield break;
                }
                string message = report.FailMessage.ToString();   // ③
                yield return new FloatMenuOption(label, delegate
                {
                    Messages.Message(message, MessageTypeDefOf.RejectInput, false);
                });
                yield break;
            }

            yield return new FloatMenuOption(label + "（就地开工）", delegate
            {
                DelegationDraft.BeginOrDialog(caravan, site, def,
                    request => caravan.pather.StartPath(site.Tile,
                        new CaravanArrivalAction_StartDelegation(site, def, request),
                        repathImmediately: true));
            });
        }

        /// <summary>已经报过"无匹配委派"的地点（避免每次右键都刷日志）。</summary>
        private static readonly HashSet<int> diagnosedSites = new HashSet<int>();

        /// <summary>「一条 DelegationDef 都没加载到」这种全局事故每次会话只弹一次提示（S26）。</summary>
        private static bool warnedNoDefs;

        /// <summary>
        /// 已知的"委派目标部件"。命中这些 defName 却没有任何 DelegationDef 匹配上，
        /// 一定是 bug（而不是"这个地点本来就没有委派"），所以要**无条件**留下现场证据。
        /// </summary>
        private static readonly string[] KnownTargetPartDefNames =
        {
            "PreciousLump", "ItemStash", "PrisonerWillingToJoin", "DownedRefugee",
        };

        /// <summary>
        /// 某个 Site 上没有任何 DelegationDef 匹配时，把它的**部件与标签**连同已加载的全部
        /// DelegationDef 一起写进日志（每个地点只报一次）。
        ///
        /// 为什么需要它："右键没有委派选项"这类问题，可能性散在
        /// 补丁没生效 / 标签不匹配 / 部件类型不符 / Def 没加载 好几处，
        /// 而这行日志能一次把它们区分开 —— 否则只能靠猜。
        ///
        /// 触发条件两档：
        ///   · 该地点含**已知目标部件**（`KnownTargetPartDefNames`）→ 无条件报（这一定是 bug，且不会刷屏：
        ///     普通遗迹/工作站点不在此列，也就不会因为路过右键而刷日志）；
        ///   · 其它地点 → 只在 `verboseLogging` 开启时报（那是"我想看看为什么这里没委派"的场景）。
        /// </summary>
        private static void DiagnoseNoMatch(Site site, string where)
        {
            if (site == null || !diagnosedSites.Add(site.ID))
            {
                return;
            }

            bool looksLikeTarget = HasKnownTargetPart(site);
            bool verbose = RimDelegationMod.Settings != null && RimDelegationMod.Settings.verboseLogging;

            // S26：**"一条 Def 都没加载到"必须当场喊出来**，别只写进日志。
            // 本轮实例：一份 XML 因为注释里出现连续两个减号而整份解析失败 ⇒ 症状是"右键没有委派选项"，
            // 而玩家不看 Player.log 就完全无从下手（日志里那句"没有加载到任何 DelegationDef"是唯一线索）。
            // 只在**一条 Def 都没有**时报（正常的"这个地点不匹配"仍只进日志），并且每次会话只弹一次。
            if (DefDatabase<DelegationDef>.DefCount == 0 && !warnedNoDefs)
            {
                warnedNoDefs = true;
                Messages.Message(
                    "RimDelegation：没有加载到任何委派数据 —— 委派功能整体不可用"
                    + "（多半是 Defs 里的 XML 解析失败，详见 Player.log 的 RimDelegation 加载行）。",
                    MessageTypeDefOf.RejectInput, false);
            }
            if (!looksLikeTarget && !verbose)
            {
                // 没报过但也不该报：把 ID 放回去，免得它占着"已诊断"名额
                diagnosedSites.Remove(site.ID);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("[RimDelegation] ").Append(where).Append("：地点「").Append(site.Label)
              .Append("」（def=").Append(site.def?.defName ?? "?").Append("，ID=").Append(site.ID)
              .Append("）没有匹配任何 DelegationDef。它的部件：");
            if (site.parts != null)
            {
                for (int i = 0; i < site.parts.Count; i++)
                {
                    SitePart part = site.parts[i];
                    sb.Append("\n  · ").Append(part?.def?.defName ?? "?")
                      .Append("  tags=[").Append(part?.def != null ? string.Join(",", part.def.tags) : "")
                      .Append("]  threatPoints=")
                      .Append(part?.parms != null ? part.parms.threatPoints.ToString("0") : "?");
                }
            }
            sb.Append("\n  已加载的 DelegationDef：");
            List<DelegationDef> all = DefDatabase<DelegationDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                DelegationDef d = all[i];
                sb.Append("\n  · ").Append(d.defName)
                  .Append("  tags=[").Append(d.targetSitePartTags != null ? string.Join(",", d.targetSitePartTags) : "")
                  .Append("]  defs=[");
                if (d.targetSitePartDefs != null)
                {
                    for (int j = 0; j < d.targetSitePartDefs.Count; j++)
                    {
                        if (j > 0) sb.Append(",");
                        sb.Append(d.targetSitePartDefs[j]?.defName ?? "?");
                    }
                }
                sb.Append("]");
            }
            sb.Append("\n  （若 DelegationDef 列表为空或缺少某条 ⇒ Patches/Defs 没生效；" +
                      "若列表齐全但标签对不上 ⇒ 是标签/部件的问题）");
            Log.Message(sb.ToString());
        }

        private static bool HasKnownTargetPart(Site site)
        {
            if (site?.parts == null)
            {
                return false;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                string name = site.parts[i]?.def?.defName;
                if (name == null)
                {
                    continue;
                }
                for (int j = 0; j < KnownTargetPartDefNames.Length; j++)
                {
                    if (KnownTargetPartDefNames[j] == name)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- 地点侧 UI
        public override IEnumerable<Gizmo> GetGizmos()
        {
            if (active != null)
            {
                yield return AbortGizmo(active);
            }
            if (DebugSettings.ShowDevGizmos)
            {
                foreach (Gizmo g in DebugGizmos_Delegations.For(this))
                {
                    yield return g;
                }
            }
        }

        public override string CompInspectStringExtra()
        {
            string s = InspectText();
            return s.NullOrEmpty() ? null : s;
        }

        public string InspectText()
        {
            Site site = Site;
            if (site == null)
            {
                return null;
            }
            Delegation d = active;
            if (d != null)
            {
                StringBuilder sb = new StringBuilder();
                string activity = d.Worker?.ActivityName ?? "开采";
                string unit = d.Worker?.UnitName ?? "格";
                sb.AppendLine($"委派：{d.def?.label ?? "?"}（{d.ModeLine()}）");
                sb.AppendLine($"参与人员 {d.participants.Count} 人 · 结束条件：{EndConditionLabelOf(d)} · 补给耗尽时{(d.abortOnOutOfFood ? "中止" : "继续" + activity)}");
                if (d.deposit != null)
                {
                    // 量纲交给 worker：采矿是"格"，物资藏匿点是"件"
                    string verb = d.Worker?.WorkVerb ?? "采";
                    sb.AppendLine($"事件点存量：全点剩余 {d.deposit.UnitsRemaining}/{d.deposit.totalUnits} {unit} · 本次可{verb} {d.totalCells} {unit} · 已被委派 {d.deposit.timesDelegated} 次");
                }
                float moodPerDay = DelegationUtility.DailyMoodOffset(d);
                if (moodPerDay != 0f)
                {
                    sb.AppendLine($"每天心情：{moodPerDay:+0.#;-0.#}（已挂 {d.moodTicksGranted} 天）");
                }
                // RIM-5：满意度是这一趟"心情 + 效率"的唯一来源，检视文本里必须能看到它
                sb.AppendLine(d.SatisfactionLine());
                string progress = d.Worker?.ProgressLabel(d);
                if (!progress.NullOrEmpty())
                {
                    sb.AppendLine(progress);
                }
                string delivery = d.Worker?.DeliverySummary(d);
                if (!delivery.NullOrEmpty())
                {
                    sb.AppendLine(delivery);
                }
                // worker 的"已知不精确之处"必须摊在面板上，而不是只写在文档里
                string warning = d.Worker?.InspectWarning(d);
                if (!warning.NullOrEmpty())
                {
                    sb.AppendLine(warning);
                }
                if (d.oreUnits >= 1f)
                {
                    sb.AppendLine($"待交付：{Mathf.FloorToInt(d.oreUnits)}（每小时结算一次）");
                }
                if (d.endCondition == DelegationEndCondition.Quota)
                {
                    sb.AppendLine($"产出配额进度：{d.MinedUnitsTotal} / {d.quotaUnits}");
                }
                if (d.caravan != null && d.caravan.ImmobilizedByMass)
                {
                    sb.AppendLine("注意：远行队已超重，无法移动");
                }
                long nowAbs = GenTicks.TicksAbs;
                bool overtime = d.EmergencyOvertimeActive;
                if (d.paused)
                {
                    sb.AppendLine("当前：**已暂停** —— 队员就地休息（不产出、不计入计划天数）");
                }
                else if (d.IsStalled(nowAbs))
                {
                    float left = (d.stalledUntilTickAbs - nowAbs) / 2500f;
                    sb.AppendLine($"当前：停摆中（剩余 {left:0.#} 小时）—— 时间照样计入计划天数");
                }
                else
                {
                    bool working = d.IsWorkTime(site, nowAbs);
                    if (working)
                    {
                        sb.AppendLine(overtime && !d.mode.IsWorkingNow(site.Tile, nowAbs)
                            ? $"当前：作业中（**紧急加班**，剩余额度 {d.overtimeTicksRemaining / 2500f:0.#} 小时）"
                            : "当前：作业中");
                    }
                    else
                    {
                        // S14：休息时把"还要等多久"一起说出来 —— 那段时间进度与 phaseTicks 都不动
                        float untilStart = d.mode?.HoursUntilStart(site.Tile, nowAbs) ?? 0f;
                        sb.AppendLine(untilStart > 0f
                            ? $"当前：休息中（不在工时段，距离开工还有 {untilStart:0.#} 小时）"
                            : "当前：休息中（不在工时段）");
                    }
                }
                // 流程（§19.29）：与 UI 同一个统一阶段序列（已完成累积 + 当前段 + 旁白），
                // 但这里是**纯文本 tooltip**，所以只取每行的 text（不画条、不上色）。
                List<DelegationStageRow> stageRows =
                    DelegationUIUtility.StageRows(d, site, 6, DelegationUIUtility.FlowHeader + "：");
                if (!stageRows.NullOrEmpty())
                {
                    for (int i = 0; i < stageRows.Count; i++)
                    {
                        DelegationStageRow row = stageRows[i];
                        if (row.kind == DelegationStageRowKind.ActiveBar)
                        {
                            continue;   // 条在文本里没有意义；ETA 已在别的行里
                        }
                        sb.AppendLine("　" + row.text);
                    }
                    if (!d.flow.pendingEndReason.NullOrEmpty())
                    {
                        sb.AppendLine($"收尾中（{d.flow.pendingEndReason}）—— 走完最后一段才会真正收工");
                    }
                }
                // 现场还剩什么（S7）：进度只说"0/2 件"，玩家不知道那 2 件是什么
                List<DelegationPreviewItem> remaining = DelegationUIUtility.SafeProgressItems(d, site);
                if (!remaining.NullOrEmpty())
                {
                    sb.AppendLine("现场物资：");
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        sb.AppendLine("　· " + (remaining[i].label ?? "?") + "　" + (remaining[i].detail ?? ""));
                    }
                }
                float days = d.EstimatedDaysLeft(site.Tile);
                if (days >= 0f)
                {
                    sb.AppendLine($"预计剩余：{days:0.##} 天");
                }
                if (timeoutPaused)
                {
                    sb.AppendLine($"事件点失效倒计时：已暂停（暂停时剩余 {pausedTimeoutRemaining / 60000f:0.#} 天；目标取尽后地点直接销毁，其他情况收工即恢复）");
                }
                sb.Append($"累计作业：{(d.ticksWorked / 2500f):0.#} 小时 / 休息 {(d.ticksResting / 2500f):0.#} 小时");
                if (d.ticksPaused > 0)
                {
                    sb.Append($" / 暂停 {(d.ticksPaused / 2500f):0.#} 小时");
                }
                if (d.ticksStalled > 0)
                {
                    sb.Append($" / 停摆 {(d.ticksStalled / 2500f):0.#} 小时");
                }
                sb.Append("（1 小时 = 2500 ticks）");
                if (d.eventsFired > 0)
                {
                    DelegationEventDef lastEvent = d.LastEventDef();
                    sb.AppendLine();
                    sb.Append($"期间发生 {d.eventsFired} 次随机事件" +
                              (lastEvent != null ? $"（最近：{lastEvent.LabelCap}）" : ""));
                }
                // 疲劳 → 工伤倍率（§19.25）。原版不会因为"累"给车队心情惩罚，
                // 所以这笔账必须在界面上看得见，否则玩家无法理解"加班 vs 全天候"的差别。
                string fatigue = DelegationUIUtility.FatigueRiskLine(d);
                if (!fatigue.NullOrEmpty())
                {
                    sb.AppendLine();
                    sb.Append(fatigue);
                }
                string meal = DelegationUIUtility.MealLine(d.caravan);
                if (!meal.NullOrEmpty())
                {
                    sb.AppendLine();
                    sb.Append(meal);
                }
                // 关掉事件后，检视里必须看得见 —— 否则"怎么一次事件都没有"会被当成 bug
                if (RimDelegationMod.Settings != null && !RimDelegationMod.Settings.randomEventsEnabled)
                {
                    sb.AppendLine();
                    sb.Append("（随机事件已在 Mod 设置中关闭）");
                }
                return sb.ToString().TrimEndNewlines();
            }
            if (Depleted)
            {
                // 中立措辞：挖矿与物资点共用这条分支
                return "委派：此事件点的目标已被取尽，地点已随之销毁。";
            }
            List<DelegationDef> defs = DelegationUtility.MatchingDefs(site);
            if (defs.Count > 0)
            {
                string extra = null;
                if (deposit != null && deposit.HasBeenWorked)
                {
                    // 量纲与措辞都从 worker 要 —— 这个分支采矿与物资点是共用的
                    DelegationWorker w = defs[0].CreateWorker();
                    extra = $"\n事件点存量：剩余 {deposit.UnitsRemaining}/{deposit.totalUnits} {w.UnitName} · 已被委派 {deposit.timesDelegated} 次 · 累计交付 {deposit.unitsDelivered} {w.OutputUnitName}";
                }
                return $"可委派：{defs[0].LabelCap}（远行队长按/右键该地点下达，或把远行队停在此处）{extra}";
            }
            return null;
        }

        /// <summary>
        /// S27：切换"这个人/动物参不参加**作战**"（用户：「作战任务里面添加一个勾选框，
        /// 决定人物或动物是参加还是不参加作战」）。
        ///
        /// 与"参不参加委派"是**两条轴**：可以派他去挖矿、但不让他打仗（动物尤其常见）。
        /// 状态存在 `d.noCombatPawns`（会存档），改完下一帧成算的缓存键就会变 ⇒ 自动重算。
        /// </summary>
        public void ToggleCombatParticipant(Delegation d, Pawn p)
        {
            if (d == null || p == null)
            {
                return;
            }
            if (d.noCombatPawns == null)
            {
                d.noCombatPawns = new List<Pawn>();
            }
            if (d.noCombatPawns.Contains(p))
            {
                d.noCombatPawns.Remove(p);
            }
            else
            {
                d.noCombatPawns.Add(p);
            }
            // 人换了一批 ⇒ 成算必须重算（缓存键里带了这张名单，下一帧自然会重建）
            d.threatSummary?.Ensure(d.caravan, Site, d.participants, d.approach, false, d.noCombatPawns);
        }

        /// <summary>
        /// S25：「进行交战」—— 给流程下达一个信号。
        ///
        /// 下完之后流程会在下一 tick 走进那个等指令的段，并触发它的 `onEnter`（交战结算就在那里）。
        /// 信号记在 `flow.signals` 里（**会存档**）：读档不会把"已经下令"这件事忘掉。
        /// </summary>
        public void GivePhaseSignal(Delegation d, string signal)
        {
            if (d?.flow == null || signal.NullOrEmpty())
            {
                return;
            }
            d.flow.GiveSignal(signal);
            DelegationUtility.LogVerbose(
                $"流程信号已下达：{signal} @ {Site?.Label}（{d.def?.defName}）");
        }

        /// <summary>
        /// S25：「撤退」—— 主动放弃这一趟作战，走**中止**。
        ///
        /// 为什么是 Abort 而不是"跳过交战继续开采"：与营救的"带伤员撤"、与交战失败同一条路
        /// （人撤回去、地点不销毁、原版失效计时恢复，卸完货还能再来）。旁边就是守军的时候继续挖矿，
        /// 在叙事上也说不通。用户口径：「作战任务UI里面添加 进行交战 和 撤退选项」。
        /// </summary>
        public void OrderRetreat(Delegation d)
        {
            Abort(d, "主动撤退：队伍放弃了这一趟");
        }

        /// <summary>
        /// 取该地点的存量；第一次调用时掷定并永久保存（之后每次委派、每次预览都用同一份）。
        /// 这就是"事件点计数、避免多次采集"的入口。
        /// </summary>
        public DelegationDeposit EnsureDeposit(DelegationDef def)
        {
            if (deposit == null)
            {
                deposit = new DelegationDeposit { def = def };
                def?.CreateWorker().RollDeposit(deposit, Site);
                DelegationUtility.LogVerbose(
                    $"掷定事件点存量：{def?.defName} @ {Site?.Label} → {deposit.totalUnits} 单位");
            }
            return deposit;
        }

        // ---------------------------------------------------------------- 生命周期
        public void StartDelegation(Caravan caravan, DelegationDef def, DelegationRequest request)        {
            Site site = Site;
            if (site == null || site.Destroyed || def == null)
            {
                return;
            }
            if (active != null)
            {
                Messages.Message("该地点已有委派在进行中", MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (Depleted)
            {
                Messages.Message("该地点已被委派采空", MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<Pawn> participants = DelegationUtility.ResolveParticipants(caravan, request?.pawns);
            int need = def.minPawns < 1 ? 1 : def.minPawns;
            if (participants.Count < need)
            {
                Messages.Message($"至少需要 {need} 名可行动人员，委派未开始", MessageTypeDefOf.RejectInput, false);
                return;
            }

            DelegationRequest resolved = request ?? new DelegationRequest();
            resolved.pawns = participants;
            resolved.mode = def.ResolveMode(resolved.mode);

            // 复用地点上那份存量（第一次会掷定）；本次只能采"剩余"的部分
            DelegationDeposit dep = EnsureDeposit(def);

            // 存量真的是 0 时不许开工。
            //
            // 为什么必须挡：`totalCells = 0` ⇒ `TargetDepleted` 恒为假（它是
            // `totalCells > 0 && ...`）⇒ `TickDelegation` 里那条"取空 → Complete"永远不成立，
            // 于是委派 0/0 空转、永不完成、永不销毁（物资点读不到清单时、营救点已经没人时都会踩到）。
            // 对话框底部的估算只是"提示"，不能当成闸门。
            if (dep.UnitsRemaining <= 0)
            {
                Messages.Message("这个事件点上当前没有可处理的目标（存量 0），委派未开始",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            active = new Delegation(def, resolved, caravan, site, dep);

            // RIM-5：把"实际开始移动那一刻"交给委派（满意度来源③「远行时间」按它起算）。
            // 没有在途旅程（就地委派 / 原地开工）时退回"开工这一刻" —— 语义就是"没有路程"。
            // ⚠️ 必须在下面的 ClearPlan 之前抄走：那一步会把 planDepartTickAbs 清 0。
            active.departTickAbs = planDepartTickAbs > 0 ? planDepartTickAbs : GenTicks.TicksAbs;
            active.RefreshSatisfaction();

            // S9：开工这一刻抄一份「现场物资」台账（"已获取 3/12 件"里的分母）。
            // 必须紧跟构造之后 —— worker.OnStart 已经跑完，现场还没被搬走一件。
            active.CaptureItemLedger(site);

            // S23：**开工这一刻冻结段表**（甲B 方案）—— 「有敌情」与「无威胁」两条流程就此定死。
            // 位置与上面那条同理：TickDelegation 的第一 tick、流程栏、ETA 全都读 d.flow，
            // 晚一格就会出现"先按 Def 全段显示一帧、下一帧才换岔"的抖动。
            DelegationFlow.Freeze(def, active.flow, site);

            // S12：已经开工 ⇒ 在途计划的那一行该从「前往中」挪进「进行中」了
            ClearPlan("已抵达并开工");

            // 委派一开工就把这个地点锁住：玩家右键时原版的「接近XX」会被灰掉。
            // 这是"进行中"的封禁（S5 补的洞）；干完之后的封禁见 ApplyEntryBlock。
            EnsureEntryBlockedWhileActive(def, site);

            // S3：暂停原版 30 天失效计时（剩余时间必须在发信号前读，禁用后 TicksLeft 恒为 0）
            timeoutPaused = false;
            if (def.handleTargetTimeout == DelegationTimeoutHandling.Pause)
            {
                bool ok = DelegationUtility.TryPauseTimeout(site, out int remaining, out string report);
                timeoutPaused = ok;
                pausedTimeoutRemaining = remaining;
                pausedAtTick = Find.TickManager.TicksGame;
                DelegationUtility.LogVerbose("暂停超时计时：" + report);
                if (!ok)
                {
                    Log.Message("[RimDelegation] 未能暂停该地点的超时计时：" + report);
                }
            }

            string label = def.startLetterLabel.NullOrEmpty() ? "委派开始" : def.startLetterLabel;
            string text = (def.startLetterText.NullOrEmpty()
                ? "{0} 的委派已开始。"
                : def.startLetterText).Formatted(site.Label, active.ModeLine(), participants.Count);
            Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent);

            DelegationUtility.LogVerbose(
                $"委派开始：{def.defName} @ {site.Label}，{participants.Count} 人，目标 {active.totalCells} {active.Worker?.UnitName ?? "格"}，结束条件 {resolved.EndConditionLabel(active.Worker)}");
        }

        private void TickDelegation(int delta)
        {
            Delegation d = active;
            if (d == null)
            {
                return;
            }
            Site site = Site;
            if (site == null || site.Destroyed)
            {
                active = null;
                return;
            }

            // 进行中的"禁止进入"要续期：`EnterCooldownComp` 是倒计时，短窗口会走到 0
            EnsureEntryBlockedWhileActive(d.def, site);

            // S14 阶段旁白（§19.29）：换阶段必掷一条、同一阶段内每 0.5h 换一条。
            // 放在 tick 里而不是 UI 里 —— OnGUI 当场随机会每帧换词，而且掷定结果必须随存档走。
            DelegationAmbient.Tick(d, site);

            Caravan caravan = d.caravan;
            if (caravan == null || caravan.Destroyed || !caravan.Spawned)
            {
                Abort(d, "远行队已不存在");
                return;
            }
            // 车队被事件拉进地图（遇袭、求救等）：人已经被生成到那张地图上，不在车队里了。
            // Caravan 是 WorldObject 而不是 MapParent，没有 HasMap，所以用"有没有人 Spawned"来判断。
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn participant = d.participants[i];
                if (participant != null && !participant.Dead && participant.Spawned)
                {
                    Abort(d, "远行队被事件拉进了地图（多半是遇袭），无法在世界地图上继续作业");
                    return;
                }
            }
            if (d.def.abortIfCaravanLeavesTile)
            {
                if (caravan.Tile != site.Tile)
                {
                    Abort(d, "远行队离开了目标地点");
                    return;
                }
                // ★ 一开始移动就中止 —— 不必等车队真的驶离当前格。
                //
                // 为什么需要这一条：`Caravan.Tile` 只在走完第一段路之后才变，
                // 在那之前 `Tile` 仍等于目标格，于是委派会继续"原地作业"，
                // 而玩家明明已经下令让车队去别处了。
                //
                // 判据用 `Moving && Destination != site.Tile`：
                //   · `Moving` = 有在途路径（= `moving && caravan.Spawned`）；
                //     它比 `MovingNow` 更合适 —— 后者还要求 `!Paused && !CantMove`，
                //     超重或暂停时就检测不到了，可玩家的"去别处"命令已经下达。
                //   · 目的地仍是本地点时不中止（玩家可能只是重新下令"来这里"）。
                if (caravan.pather != null && caravan.pather.Moving
                    && caravan.pather.Destination != site.Tile)
                {
                    Abort(d, "远行队已启程前往别处，委派中止");
                    return;
                }
            }

            // 补给耗尽：按风险姿态决定撤还是硬扛
            string malnutrition = null;
            bool outOfFood = false;
            if (caravan.needs != null)
            {
                outOfFood = caravan.needs.AnyPawnOutOfFood(out malnutrition);
            }
            if (outOfFood)
            {
                if (d.abortOnOutOfFood)
                {
                    Abort(d, "远行队补给耗尽");
                    return;
                }
                if (!d.outOfFoodWarned)
                {
                    d.outOfFoodWarned = true;
                    string activity = d.Worker?.ActivityName ?? "开采";
                    Messages.Message(
                        $"{caravan.LabelCap} 的补给已经耗尽（{malnutrition}），但委派设定为「补给耗尽时继续{activity}」——他们正在饿着干活。",
                        MessageTypeDefOf.ThreatSmall, false);
                }
            }

            // 重算人力：只剔除"死亡 / 离队"；倒地只是暂时不计入产能，恢复后会自动回来
            d.participants.RemoveAll(p => p == null || p.Dead || !caravan.ContainsPawn(p));
            if (d.participants.Count == 0)
            {
                Abort(d, "已无人可作业（全员死亡或离队）");
                return;
            }
            if (d.mode == null)
            {
                Abort(d, "委派模式丢失");
                return;
            }

            long nowAbs = GenTicks.TicksAbs;
            DelegationFlow flow = DelegationFlow.For(d.def);

            // worker 的"干不下去"判定先过：例：救援委派的清场战斗打输了。
            // 放在最前面 —— 已经打输了就不该再计入心情、随机事件与产出。
            string abortReason = d.Worker?.WorkerAbortReason(d, site);
            if (!abortReason.NullOrEmpty())
            {
                Abort(d, abortReason);
                return;
            }

            // ── 暂停（§19.24）────────────────────────────────────────────
            // 不开采、不产出；暂停的时间**不计入**计划天数（否则暂停 3 天就把 5 天的计划耗掉了）。
            // 人在这一刻是"在休息"而不是"在干活" —— 那件事由 DelegationRegistry 负责，
            // 它把暂停中的委派从 workingPawns 里排除，休息补丁因此不再拦休息条回升。
            if (d.paused)
            {
                d.pausedTicksTotal += delta;
                d.ticksPaused += delta;
                return;
            }

            // ── 停摆：随机事件造成的时间损失 ─────────────────────────────
            // 与暂停相反，停摆**照样消耗**计划天数 —— 那就是事件的时间代价。
            if (d.IsStalled(nowAbs))
            {
                d.ticksStalled += delta;
                return;
            }

            // 满意度（RIM-5）：每 tick 刷新一次缓存 —— 它要读难度、数在外天数、遍历吃饭记录，
            // 放在这里比放在 UI 每帧里便宜得多（UI 与 worker 只读 d.satisfaction / d.SatisfactionRateFactor）。
            d.RefreshSatisfaction();

            // 心情：按天给车队成员挂记忆（离图也照挂，回来还留着余味）
            d.ticksSinceMoodTick += delta;
            if (d.ticksSinceMoodTick >= MoodIntervalTicks)
            {
                d.ticksSinceMoodTick = 0;
                GrantDailyMood(d);
            }

            // ── 固定流程·收尾（§19.27）──────────────────────────────────────
            // 已经进入收尾就不再判任何收工条件 —— 收工理由在进收尾那一刻已经记下来了
            //（那之后 `TargetDepleted` / `WorkerEndReason` 可能已经问不出同一句话，
            //  例：撤离途中车队被卸了货，就不再"装满"了）。
            if (flow.InSuffix(d.flow))
            {
                d.ticksWorked += delta;
                // S23：收尾段也能带钩子（本轮没有配，但通路与前置段共用同一条）
                if (RunPhaseEnter(d, site, flow))
                {
                    Abort(d, d.flowAbortReason);
                    return;
                }
                if (flow.AdvanceSuffix(d.flow, delta))
                {
                    Complete(d, site, d.flow.pendingEndReason);
                }
                return;
            }

            // RIM-34(1A)：**待命等指令期间不推进任何收工判定**（也不累计"按天数"的工期）。
            //
            // 旧写法先判"按天数"（`EndConditionReached`）再看工时门控，而"待命"（`AwaitingOrder`）
            // 只让 `IsWorkTime` 变 false ⇒ 玩家还在看"要不要打"的界面，委派可能已经按计划天数
            // 进了收尾、直接收工，于是交战段 / 搜集战利品段 / 扎营段**全部被跳过**，
            // 还发一封"委派完成"的信（守军根本没处理）。
            // 判据只有一处：`Delegation.AwaitingOrder`（与 `IsWorkTime` 同源）。
            // 把 `startedTickAbs` 一起往后推 ⇒ 待命时间**不算工期**（拍板 1A 而非 1B），
            // UI 上"已用天数"在待命期间也不会走字。
            if (d.AwaitingOrder)
            {
                d.startedTickAbs += delta;
                d.ticksResting += delta;
                return;
            }

            // 结束条件里"按天数"与工时无关，先判一次
            string reached = d.EndConditionReached(GenTicks.TicksAbs);
            if (reached != null)
            {
                TryBeginCompletion(d, site, flow, reached);
                return;
            }

            // ★ 工时门控的唯一判据（含紧急加班，§19.26）
            //
            // S22 例外：**不可打断的固定段**（侦察 / 移动 / 破门）到了窗口之外也照常走完 ——
            // 用户原话：「正在侦察环境的时候，不会因为时段进入了休息而打断」。
            // 这几段一旦开工就必须做完，停下来等白天在叙事上说不通（人已经在门后面了）。
            // 这段时间照计 `ticksWorked`（醒着干活 ≠ 休息），所以直接落到下面的正常路径。
            if (!d.IsWorkTime(site, nowAbs) && !flow.UninterruptibleActive(d.flow))
            {
                d.ticksResting += delta;
                return;
            }

            // 加班额度只在**本来该休息**的时段被消耗：工时段照常按窗口走，不扣额度。
            // 所以「4 小时」= 额外的 4 小时，而不是"从此刻起 4 小时"。
            if (d.EmergencyOvertimeActive && d.mode != null && !d.mode.IsWorkingNow(site.Tile, nowAbs))
            {
                d.overtimeTicksRemaining = Mathf.Max(0, d.overtimeTicksRemaining - delta);
            }

            d.ticksWorked += delta;

            // ── 固定流程·前置（§19.27 / S23 段钩子）────────────────────────
            // 前置段（侦察/建立营地/评估/交战）没走完就不产出；随机事件与小时交付照旧 ——
            // 人在现场，塌方与装车本来就都会发生。
            //
            // S23：进入**新的一段**时先跑它的 `onEnter`（「交战」就是在这里结算一场模拟战斗）。
            // 钩子给出中止理由时立刻 Abort（用户 2026-09-27 拍板：交战失败走 Abort，不走收尾）。
            if (RunPhaseEnter(d, site, flow))
            {
                Abort(d, d.flowAbortReason);
                return;
            }

            bool preludeBusy = flow.AdvancePrelude(d.flow, delta);
            // S26：判"该公布的那段结果"到点了没有（例：交战打完 ⇒ 这时才贴出战报）
            FlushPendingNote(d, flow);
            if (!preludeBusy)
            {
                d.Worker?.Tick(d, site, delta);
            }

            // ── 随机事件（§19.24）：每游戏内 1 小时掷一次，每小时最多触发一个 ──
            d.ticksSinceEventRoll += delta;
            if (d.ticksSinceEventRoll >= EventRollIntervalTicks)
            {
                d.ticksSinceEventRoll = 0;
                RollEvents(d, site);
                if (d.IsStalled(nowAbs))
                {
                    return;   // 事件刚把作业打停，本 tick 不再产出
                }
            }

            // 按小时交付产出：让"带已采部分撤回"天然成立，也让玩家能看着负重涨
            d.ticksSinceFlush += delta;
            if (d.ticksSinceFlush >= FlushIntervalTicks)
            {
                d.ticksSinceFlush = 0;
                d.Worker?.FlushDeliveries(d, caravan);
                CheckMassCapacity(d, caravan);
            }

            // 前置段还没走完就谈不上"干完了"（这时进度必然还是 0，不该发完成信）
            if (preludeBusy)
            {
                return;
            }

            if (d.TargetDepleted)
            {
                // 完成理由交给 worker：`TargetDepleted` 是通用的"干完了"信号，
                // 但它的默认文案是采矿口径（"已采空"），救援类委派干完的是"人已救出"。
                // 这里一定要 return：TryBeginCompletion 要么已经 Complete（active 已清空），
                // 要么已经切进收尾 —— 两种情况本 tick 都不该再往下判。
                TryBeginCompletion(d, site, flow, d.Worker?.DepletedReason(d) ?? "目标已采空");
                return;
            }
            // worker 自己的收工判定：目标还给不出"采空"信号、但客观上已经干不下去了。
            // 例：物资藏匿点装车装到车队满载 —— 收工（不销毁地点、恢复失效计时），
            // 剩下的物资留在原地，玩家卸完货可以回来接着委派。
            string workerDone = d.Worker?.WorkerEndReason(d, site);
            if (!workerDone.NullOrEmpty())
            {
                TryBeginCompletion(d, site, flow, workerDone);
                return;
            }
            reached = d.EndConditionReached(GenTicks.TicksAbs);
            if (reached != null)
            {
                TryBeginCompletion(d, site, flow, reached);
            }
        }

        /// <summary>
        /// S23：**段进入钩子** —— 进入"当前这一固定段"的那一刻执行一次它的 `onEnter` 效果。
        ///
        /// 为什么放在宿主而不是 <see cref="DelegationFlow" /> 里：钩子要动的东西
        /// （事件留痕、中止委派、跑一场模拟战斗）全是宿主的职责；`DelegationFlow` 只管"下标走到哪了"。
        ///
        /// 记号存在 `flow.enteredPhase`（**会存档**）—— 交战段的钩子会真的打一场，
        /// 读档后重跑一次等于白送一场战斗（也会再施一次伤亡），所以"恰好一次"必须落盘。
        ///
        /// 返回 true = 钩子给出了中止理由，调用方应当立刻 `Abort`。
        /// </summary>
        private bool RunPhaseEnter(Delegation d, Site site, DelegationFlow flow)
        {
            // S26：在等玩家指令的时候**绝不能**跑段钩子 —— 否则"还没点进行交战，战斗就自己打完了"。
            if (flow.IsGated(d.flow))
            {
                return false;
            }
            DelegationPhaseDef phase = flow.ActivePhase(d.flow);
            if (phase == null)
            {
                return false;
            }

            string key = (flow.InPrelude(d.flow) ? "pre:" : "suf:") + phase.defName;
            if (d.flow.enteredPhase == key)
            {
                return false;
            }
            // 先记后跑：钩子抛异常也不能变成"每 tick 重试一次"
            d.flow.enteredPhase = key;

            if (phase.onEnter.NullOrEmpty())
            {
                return false;
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < phase.onEnter.Count; i++)
            {
                DelegationEffectDef effect = phase.onEnter[i];
                if (effect == null)
                {
                    continue;
                }
                string line;
                try
                {
                    line = effect.Apply(d, site);
                }
                catch (Exception ex)
                {
                    line = "（" + effect.GetType().Name + " 执行失败：" + ex.GetType().Name + "）";
                    Log.Error("[RimDelegation] 段 " + phase.defName + " 的 onEnter 效果抛异常：" + ex);
                    // RIM-34(2A)：**fail-closed**。旧写法只记一行留痕，而本方法最后是
                    // `return !d.flowAbortReason.NullOrEmpty()` ⇒ 异常时返回 false ⇒ 宿主认为
                    // "可以继续往下走"：守军从未结算、也没有战报，流程照常进入下一段。
                    // 同一段代码的注释自己写着「抽象模型兜不住…不装作打赢，也不静默通过 —— 直接中止」，
                    // 这条路径正好违反它。现在设了理由 ⇒ 宿主下一次判定就 Abort。
                    if (d.flowAbortReason.NullOrEmpty())
                    {
                        d.flowAbortReason = "段「" + phase.LabelCap + "」的结算没能完成（"
                            + ex.GetType().Name + "），为免弄虚作假，队伍按中断处理。";
                    }
                }
                if (line.NullOrEmpty())
                {
                    continue;
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }

            if (sb.Length > 0)
            {
                // S29：带上"出自作战段吗"—— 流程块据此把这条留痕排进「作战任务」组，
                // 而不是像以前那样一律吊在流程块最末尾（看着像挂在了「收集任务」下面）。
                bool combatPhase = phase.InCombatFlow;
                if (phase.deferNoteUntilDone)
                {
                    // S26：先存着，等这一段走完再公布（用户：「交战流程完成前，结果就出来了 -- 需要等待交战结束再出结果」）
                    d.QueuePhaseNote(phase.PendingLabel, phase.defName, sb.ToString(), combatPhase);
                }
                else
                {
                    // 与随机事件共用同一条留痕通道 ⇒ 流程块的事件行 / 结束报告 / 历史记录都能看到它
                    d.LogPhaseNote(phase.PendingLabel, sb.ToString(), combatPhase);
                }
            }
            return !d.flowAbortReason.NullOrEmpty();
        }

        /// <summary>
        /// S26：把"等这一段走完才公布"的段结果写进留痕（判据 = <see cref="DelegationFlow.PhaseDone" />）。
        ///
        /// 调用点有两处：每次推进前置流程之后，以及 Complete / Abort 之前
        /// （`flow` 传 null = 无条件公布 —— 委派都要结束了，结果不能再烂在字段里）。
        /// </summary>
        private void FlushPendingNote(Delegation d, DelegationFlow flow)
        {
            if (d?.HasPendingNote != true)
            {
                return;
            }
            if (flow != null && !flow.PhaseDone(d.flow, d.flowPendingNotePhase))
            {
                return;   // 这一段还没走完 —— 继续等
            }
            d.LogPhaseNote(d.flowPendingNoteLabel, d.flowPendingNote, d.flowPendingNoteCombat);
            d.flowPendingNote = null;
            d.flowPendingNotePhase = null;
            d.flowPendingNoteLabel = null;
            d.flowPendingNoteCombat = false;
        }

        /// <summary>
        /// **收工条件的唯一出口**（§19.27）：配了收尾流程就先走收尾，没有就直接 <see cref="Complete"/>。
        ///
        /// 为什么必须收成一个函数：收工条件有四处（目标取尽 / worker 自己喊停 / 按天数 / 按配额），
        /// 收尾流程要挡在它们**全部**之前 —— 只要漏掉一处，玩家就会看到
        /// "写着撤离 2h、但地点当场没了"的不一致（§19.27 的验收项之一）。
        /// </summary>
        private void TryBeginCompletion(Delegation d, Site site, DelegationFlow flow, string reason)
        {
            if (flow.NeedsSuffix(d.flow))
            {
                if (d.flow.pendingEndReason.NullOrEmpty())
                {
                    d.flow.pendingEndReason = reason;
                    d.flow.phaseTicks = 0f;
                    DelegationUtility.LogVerbose(
                        $"委派进入收尾流程：{d.def?.defName} @ {site?.Label}（收工理由：{reason}）");
                }
                return;   // 下一次 TickDelegation 走收尾分支，走完才 Complete
            }
            Complete(d, site, reason);
        }

        // ---------------------------------------------------------------- 随机事件（§19.24）

        /// <summary>
        /// 每游戏内 1 小时掷一次。
        /// 单项触发概率 ≈ <c>1 / (mtbDays × 24) × weight</c>，命中即发信并结束本小时
        /// （一小时最多一件事，避免同一时刻刷出一堆信件）。
        /// 每个事件还各有 <c>minDaysBetween</c> 冷却，记在 Delegation 上、会存档。
        /// </summary>
        private void RollEvents(Delegation d, Site site)
        {
            // 设置里可以整个关掉（默认开）。放在最前面 —— 关掉后连骰子都不掷。
            RimDelegationSettings cfg = RimDelegationMod.Settings;
            if (cfg != null && !cfg.randomEventsEnabled) return;

            List<DelegationEventDef> all = DefDatabase<DelegationEventDef>.AllDefsListForReading;
            if (all == null || all.Count == 0) return;

            int nowAbs = GenTicks.TicksAbs;
            float hoursPerRoll = EventRollIntervalTicks / 2500f;

            for (int i = 0; i < all.Count; i++)
            {
                DelegationEventDef def = all[i];
                if (def == null) continue;
                if (d.EventCooldownActive(def, nowAbs)) continue;

                bool canFire;
                try
                {
                    // ★ 静态闸门先过：这条事件是不是本来就不属于这门活。
                    //   缺了它，采矿专属的"塌方 / 富矿脉"会落到物资点、工作站点等任意委派上，
                    //   而富矿脉会凭空给 totalCells 加数 ⇒ cellsMined 永远追不上 ⇒ 委派死循环。
                    if (!def.AppliesTo(d, site))
                    {
                        continue;
                    }
                    canFire = def.CanFire(d, site);
                }
                catch (Exception ex)
                {
                    Log.Error("[RimDelegation] 随机事件 " + def.defName + " 的 AppliesTo/CanFire 抛异常：" + ex);
                    continue;
                }
                if (!canFire) continue;

                float mtbHours = Mathf.Max(0.1f, def.mtbDays * 24f);
                float chance = hoursPerRoll / mtbHours * Mathf.Max(0.01f, def.weight);

                // 动态倍率：目前只有「工伤」用它把疲劳接进来（§19.25）。
                // 子类抛异常不能连累整个掷骰流程，所以单独兜住并退回 1。
                float dynamic = 1f;
                try
                {
                    dynamic = def.ChanceMultiplier(d);
                }
                catch (Exception ex)
                {
                    Log.WarningOnce("[RimDelegation] 事件 " + def.defName + " 的 ChanceMultiplier 抛异常：" + ex, 0x5E0C6);
                }
                chance *= Mathf.Clamp(dynamic, 0f, 50f);

                if (!Rand.Chance(chance)) continue;

                FireEvent(d, site, def);
                return;
            }
        }

        /// <summary>
        /// 执行一次事件并发信。
        /// **异常必须兜住** —— 一次随机事件不该把游戏打崩（尤其是"对未 spawn 的 pawn 施加伤害"
        /// 这条尚未验证的路径，见 §19.14 第 1 项）。
        /// </summary>
        public void FireEvent(Delegation d, Site site, DelegationEventDef def)
        {
            if (d == null || def == null) return;

            string detail = null;
            try
            {
                detail = def.Apply(d, site);
            }
            catch (Exception ex)
            {
                detail = "事件执行失败：" + ex.Message;
                Log.Error("[RimDelegation] 随机事件 " + def.defName + " 执行失败：" + ex);
            }

            d.MarkEventFired(def, GenTicks.TicksAbs);
            d.eventsFired++;

            // S15：留痕。这是"玩家还能知道发生过什么"的唯一来源 ——
            // 流程块那一行、结束报告、历史记录全都读它。
            d.LogEvent(def, detail);

            // S15 第三期：提示音（**默认关** —— 声音也是一种打断，见 RimDelegationSettings.eventSoundEnabled）。
            // 只有"有实质影响"的事件响；纯叙事不响。
            if (def.severity == DelegationEventSeverity.Effect
                && (RimDelegationMod.Settings?.eventSoundEnabled ?? false))
            {
                SoundDef cue = DefDatabase<SoundDef>.GetNamedSilentFail("ClickReject");
                if (cue != null)
                {
                    // `Verse.Sound.SoundStarter` 的扩展方法（camera 位置播放，不需要 Map）
                    cue.PlayOneShotOnCamera();
                }
            }

            // S15 **零弹出**：事件不再发 Letter（用户要求：不按"游戏的弹信事件"处理）。
            // 玩家的三条知情路径：
            //   ① 进行中 = 流程块的「事件」一行（含停摆期间的 stall 行）；
            //   ② 结束时 = 完成/中断信件里的逐条明细（+ 可选的报告窗口）；
            //   ③ 随时   = 底部「委派」按钮的未查看角标 → 主控台。
            // Flavor 与 Effect 在"留痕"上完全一样，只影响报告里的配色与（将来的）提示策略。
            DelegationUtility.LogVerbose(
                $"随机事件：{def.defName}（{def.severity}）{detail}（不发信，S15 零弹出）");
        }

        /// <summary>开发者用：强制触发指定事件（跳过概率与冷却），用来演示/验证。</summary>
        public void DevFireEvent(DelegationEventDef def)
        {
            Delegation d = active;
            Site site = Site;
            if (d == null || def == null || site == null) return;
            FireEvent(d, site, def);
        }

        /// <summary>
        /// 每天给车队里的每个人挂心情记忆。
        ///
        /// RIM-5：**模式不再自己挂心情**（用户拍板 1A + 2B + 6A）——
        /// 模式的 +3/0/−4/−6 折算进满意度来源「作业强度」，这里统一挂**满意度那一档**的记忆。
        /// 0 心情的档位不挂：0 心情的记忆不会出现在需求列表里（`MoodOffset() != 0f` 会把它滤掉），
        /// 挂了只是白占内存 —— 与野外伙食同一条规矩。
        /// </summary>
        private void GrantDailyMood(Delegation d)
        {
            Caravan caravan = d.caravan;
            if (caravan == null)
            {
                return;
            }
            List<Pawn> list = caravan.PawnsListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == null || p.Dead || !p.RaceProps.Humanlike)
                {
                    continue;
                }
                DelegationUtility.GrantThought(p, d.def?.dailyMoodThought);
                float satisfactionMood = DelegationSatisfaction.Mood(d.satisfaction);
                if (satisfactionMood != 0f)
                {
                    DelegationUtility.GrantThought(p, DelegationSatisfaction.Def?.thought,
                        DelegationSatisfaction.Stage(d.satisfaction));
                }
            }
            d.moodTicksGranted++;
            DelegationUtility.LogVerbose(
                $"委派每日心情已挂载（第 {d.moodTicksGranted} 天，满意度 {d.satisfaction:0.00}"
                + $"（{DelegationSatisfaction.StageLabel(d.satisfaction) ?? "?"}）"
                + $"，每天心情 {DelegationSatisfaction.Mood(d.satisfaction):+0.#;-0.#;0}，{caravan.LabelCap}）");
        }

        /// <summary>产出让车队超重时提醒一次（原版超重会让车队无法移动，不能默默坑玩家）。</summary>
        private void CheckMassCapacity(Delegation d, Caravan caravan)
        {
            if (d.massWarned || caravan == null || !caravan.ImmobilizedByMass)
            {
                return;
            }
            d.massWarned = true;
            Messages.Message(
                $"委派产出已让 {caravan.LabelCap} 超重，远行队无法移动。派驮兽、就地卸货，或改用运输舱把货送回去。",
                MessageTypeDefOf.ThreatSmall, false);
        }

        /// <summary>
        /// 收工。reason = 为什么收工（"目标已采空" / "按计划干满 N 天" / "已达到产出配额"）。
        /// 只有真的采空才销毁地点；按天数/配额收工的地点会保留，并把原版失效计时恢复回去。
        /// </summary>
        public void Complete(Delegation d, Site site, string reason)
        {
            FlushPendingNote(d, null);   // S26：要结束了，等公布的结果先落地
            // S32：没搬走的东西（真尸体）不能留着；RIM-29(1A)：**丢了多少必须写进信件**（不许静默蒸发）
            DelegationUtility.DiscardedHaul discarded = DelegationUtility.DiscardPendingHaul(d);
            active = null;
            bool trulyDepleted = d?.TargetDepleted ?? false;

            // 交付剩余产出
            if (d != null && d.caravan != null && !d.caravan.Destroyed)
            {
                d.Worker?.FlushDeliveries(d, d.caravan);
            }
            d?.Worker?.OnComplete(d, site);
            GrantOutcomeThought(d, d?.def?.completeMoodThought);

            // 地点名要在销毁前读出来
            string siteLabel = site?.Label ?? "地点";
            string label = LetterLabel(d?.def?.completeLetterLabel, "委派完成");
            string progress = d?.Worker?.ProgressLabel(d);
            string delivery = d?.Worker?.DeliverySummary(d);
            string text = LetterText(d?.def?.completeLetterText, "{0} 的委派已完成：{1}\n\n{2}\n\n{3}",
                siteLabel, reason ?? "已收工", progress ?? "", delivery ?? "");
            // S15：事件不再单独弹信 ⇒ 结束时把逐条明细补进这封信（同一份格式化，见 EventReportLines）
            string eventReport = DelegationUIUtility.EventReportText(d);
            if (!eventReport.NullOrEmpty())
            {
                text += "\n\n" + eventReport;
            }
            // RIM-29(1A)：收尾丢掉的东西必须写进这封信（旧写法静默清空 lootBag / pendingCorpses）
            if (discarded.Any)
            {
                text += "\n\n" + discarded.Line();
            }
            Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.PositiveEvent);

            // S15 第二期：**无条件**记一条历史 —— "能不能回看"与"要不要弹窗"是两件事。
            // 放在 site.Destroy() 之前：记录里要用地点名与所在格。
            RimDelegationHistory.Get()?.Add(DelegationRecord.From(d, site, reason, false));

            // S15：开了"结束报告"就排队弹一份签核窗口（打断版：弹出即暂停、必须点确认）。
            // 放在 site.Destroy() **之前** —— 报告标题里要用地点名。
            if (RimDelegationMod.Settings?.reportOnComplete ?? false)
            {
                DelegationReport.Enqueue(DelegationReportData.Build(d, site, reason, false));
            }

            if (trulyDepleted && (d?.def?.destroyTargetOnComplete ?? true) && site != null && !site.Destroyed)
            {
                DelegationUtility.LogVerbose($"目标已采空，销毁地点 {siteLabel}（防双吃）");
                site.Destroy();
            }
            else if (timeoutPaused && site != null && !site.Destroyed)
            {
                ResumeTimeout(site);
            }

            // 没取空 ⇒ 地点保留 ⇒ 必须堵住"再进图拿另一份"的路
            ApplyEntryBlock(d, site);
            // 但"一点没动过"（或该 Def 不要求封禁）时，要把进行中那次临时冷却撤掉
            ReleaseEntryBlockIfUnwarranted(d, site);

            DelegationUtility.LogVerbose(
                $"委派完成（{reason}）：{d?.def?.defName} @ {siteLabel}，交付 {d?.oreDelivered} 单位");
        }

        public void Abort(Delegation d, string reason)
        {
            if (active == null)
            {
                return;
            }
            FlushPendingNote(d, null);   // S26：同 Complete —— 中断也要把等公布的结果落地
            // S32：同上（没搬走的尸骸就地丢弃）；RIM-29(1A/3A)：中断信里单列一行"丢了多少"，
            // 这条路径本来最容易被玩家误解（"交火失利"那封信以前一个字都不提那批已装箱的缴获）
            DelegationUtility.DiscardedHaul discarded = DelegationUtility.DiscardPendingHaul(d);
            active = null;
            Site site = Site;

            // 带已采部分撤回：把待交付的余数先送进车队库存
            if (d != null && d.caravan != null && !d.caravan.Destroyed)
            {
                d.Worker?.FlushDeliveries(d, d.caravan);
            }
            d?.Worker?.OnAbort(d, site);
            GrantOutcomeThought(d, d?.def?.abortMoodThought);

            string label = LetterLabel(d?.def?.abortLetterLabel, "委派中断");
            string delivery = d?.Worker?.DeliverySummary(d);
            string text = LetterText(d?.def?.abortLetterText, "{0} 的委派已中断：{1}\n\n{2}",
                site?.Label ?? "地点", reason, delivery ?? "");
            // S15：同上 —— 中断信里也补上事件明细（中断时事件留痕同样有意义：
            // "为什么中断"经常就写在事件里，例：停摆 + 补给耗尽）
            string eventReport = DelegationUIUtility.EventReportText(d);
            if (!eventReport.NullOrEmpty())
            {
                text += "\n\n" + eventReport;
            }
            // RIM-29(1A/3A)：失利/中止时**单列一行**"已缴获但没搬走"的部分
            //（旧写法：交火失利那封信只写"失利"，那批已经装进 lootBag 的缴获一个字都不提）
            if (discarded.Any)
            {
                text += "\n\n" + discarded.Line();
            }
            Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NegativeEvent);

            // S15 第二期：中断同样记一条历史（"为什么中断"经常就藏在事件明细里）
            RimDelegationHistory.Get()?.Add(DelegationRecord.From(d, site, reason, true));

            // S15：中断同样给报告
            if (RimDelegationMod.Settings?.reportOnComplete ?? false)
            {
                DelegationReport.Enqueue(DelegationReportData.Build(d, site, reason, true));
            }

            if (timeoutPaused && site != null && !site.Destroyed)
            {
                ResumeTimeout(site);
            }

            // 中断后地点一定还在（人/矿都留在原地）⇒ 同样要判断要不要封禁进图
            ApplyEntryBlock(d, site);
            ReleaseEntryBlockIfUnwarranted(d, site);

            DelegationUtility.LogVerbose($"委派中断：{reason}（{d?.def?.defName} @ {site?.Label}），已带回 {d?.oreDelivered} 单位");
        }

        /// <summary>
        /// 委派动过目标、地点却还留着时，**禁止再进图**（零 Harmony）。
        ///
        /// 走的是原版挂在 `Site` def 上的 `EnterCooldownComp` ——
        /// `CaravanArrivalAction_VisitSite.CanVisit` 本来就会检查
        /// `site.EnterCooldownBlocksEntering()`，所以"进入地点"菜单项会被原版自己灰掉，
        /// 提示文案也是原版自己的，不需要我们伪造。
        ///
        /// 为什么必须堵（两件事，S5 立的 + S14 扩的）：
        /// ① 采矿的存量是委派独立推算的，而原版只在生成地图时才掷格数 ——
        ///    不堵就会出现"委派挖一半 → 进图再挖一份"的双吃（DESIGN §7.1）；
        /// ② **玩法口径**（用户 S14 拍板）：事件点被委派动过之后只走委派路线 ——
        ///    物资藏匿点"车队先装满"收工时地点是保留的，原版那条"进图把剩下的搬走"
        ///    就是绕过委派的第二条路，一并关掉（`blockMapEntryAfterWorked` 默认 true）。
        /// </summary>
        private static void ApplyEntryBlock(Delegation d, Site site)
        {
            if (d?.def == null || site == null || site.Destroyed)
            {
                return;
            }
            if (!d.def.blockMapEntryAfterWorked)
            {
                return;
            }
            // 一点没动过就不堵：玩家只是来看了一眼、取消了委派，不该被罚
            if (d.deposit == null || d.deposit.unitsMined <= 0f)
            {
                return;
            }

            EnterCooldownComp comp = site.GetComponent<EnterCooldownComp>();
            if (comp == null)
            {
                Log.WarningOnce("[RimDelegation] 想禁止进入该地点，但它没有 EnterCooldownComp" +
                                "（Site def 上本应有），已跳过", 0x5E0CD);
                return;
            }
            comp.Start(d.def.blockMapEntryDays);
            DelegationUtility.LogVerbose(
                $"已禁止进入 {site.Label}（{d.def.blockMapEntryDays:0.#} 天）：委派动过该点的存量");
        }

        /// <summary>
        /// **委派进行中**禁止玩家进入这个地点（S5 修的那个洞）。
        ///
        /// 修之前只有 `Complete` / `Abort` 会调 <see cref="ApplyEntryBlock"/> ——
        /// 也就是"干完之后才封"，而**作业期间一直是敞开的**：
        /// 玩家右键这个地点仍然能看到原版的「接近XX」（= `CaravanArrivalAction_VisitSite`，
        /// 它只检查 `site.EnterCooldownBlocksEntering()`），进去以后 `GenStep_PreciousLump`
        /// 会按原版公式**再掷一份**矿 ⇒ 委派挖一半 + 进图再挖一份 = 双吃（DESIGN §7.1）。
        /// 顺带地，`TickDelegation` 会因为"队员已被 spawn 到地图上"而误报
        /// 「远行队被事件拉进了地图（多半是遇袭）」——明明是自己走进去的。
        ///
        /// 实现仍然走原版组件（零 Harmony）：`EnterCooldownComp.Start(days)`，
        /// 菜单项与提示都由原版自己灰/自己写。
        /// </summary>
        private void EnsureEntryBlockedWhileActive(DelegationDef def, Site site)
        {
            if (def == null || site == null || site.Destroyed)
            {
                return;
            }
            // 已经有地图就不必封了：`EnterCooldownComp.BlocksEntering` 在 ParentHasMap 时恒为 false，
            // 而这时候"进图"也不再是二次开采（那份矿早就在图里了）。
            if (site.HasMap)
            {
                return;
            }
            EnterCooldownComp comp = site.GetComponent<EnterCooldownComp>();
            if (comp == null)
            {
                Log.WarningOnce("[RimDelegation] 想封禁进入该地点，但它没有 EnterCooldownComp" +
                                "（Site def 上本应有），已跳过", 0x5E0CE);
                return;
            }
            // "进行中"一律用短窗口 + 续期，**不用** Def 的 blockMapEntryDays：
            // 那个值是"干完之后"的封禁时长（采矿 = 9999 天），拿来当"进行中"的剩余时间，
            // 玩家右键时会看到原版提示"还需 9999 天才能进入"这种莫名其妙的话。
            float days = WhileActiveBlockDays;
            // 剩余还有一半以上就不动它 —— 免得每 tick 都 Start()，把玩家的"还需多久"钉死在最大值
            if (comp.Active && comp.DaysLeft >= days * 0.5f)
            {
                return;
            }
            comp.Start(days);
            entryBlockSelfStarted = true;
        }

        /// <summary>
        /// 收工/中断后的收尾：**没动过存量**时，把"进行中"那次我们自己起的冷却撤掉。
        ///
        /// 不撤会怎样：`blockMapEntryAfterWorked` 现在默认 true（S14），
        /// 但"点开委派 → 立刻中止"这种一点没动过的情况**不该**被永久封禁
        /// （`ApplyEntryBlock` 里那条"一点没动过就不堵"的规则说的就是这件事）——
        /// 玩家只是看了一眼就走，地点上什么都没变，没有理由把原版那条路也关掉。
        ///
        /// S14 之前这里还有第二个理由"物资点/营救的 def 压根没开 blockMapEntryAfterWorked"，
        /// 那条已经按用户口径取消：四条玩法现在都封，判据统一成"有没有真的动过存量"。
        /// </summary>
        private void ReleaseEntryBlockIfUnwarranted(Delegation d, Site site)
        {
            if (!entryBlockSelfStarted || site == null || site.Destroyed)
            {
                return;
            }
            bool worked = d?.deposit != null && d.deposit.unitsMined > 0f;
            bool keep = d?.def != null && d.def.blockMapEntryAfterWorked && worked;
            if (keep)
            {
                return;   // 该留着的（动过 + Def 要求封禁）保持原样
            }
            site.GetComponent<EnterCooldownComp>()?.Stop();
            entryBlockSelfStarted = false;
            DelegationUtility.LogVerbose($"已解除进入封禁：{site.Label}（委派没动过存量，或该 Def 不要求封禁）");
        }

        private void GrantOutcomeThought(Delegation d, ThoughtDef thought)
        {
            if (d == null || thought == null)
            {
                return;
            }
            List<Pawn> list = d.caravan?.PawnsListForReading;
            if (list == null)
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                DelegationUtility.GrantThought(list[i], thought);
            }
        }

        /// <summary>把被暂停的原版失效计时恢复回去（补回暂停期间流逝的时长）。</summary>
        private void ResumeTimeout(Site site)
        {
            int pausedTicks = Find.TickManager.TicksGame - pausedAtTick;
            bool ok = DelegationUtility.TryResumeTimeout(site, pausedTicks, out string report);
            timeoutPaused = false;
            DelegationUtility.LogVerbose("恢复超时计时：" + report);
            if (!ok)
            {
                Log.Message($"[RimDelegation] 未能恢复该地点的超时计时：{report}（暂停时剩余 {pausedTimeoutRemaining} ticks，暂停了 {pausedTicks} ticks）");
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            // ⚠️ 所有标签都加 ro 前缀：原版把 WorldObject 上【所有 comp 的字段平铺在同一层 XML】，
            //    标签必须全局唯一，否则会与别的 comp 撞名（曾用 "active" 撞上别的 comp 的 bool → 解析异常）。
            Scribe_Deep.Look(ref active, "roActive");
            Scribe_Deep.Look(ref deposit, "roDeposit");
            Scribe_Values.Look(ref timeoutPaused, "roTimeoutPaused", false);
            Scribe_Values.Look(ref pausedTimeoutRemaining, "roPausedRemaining", 0);
            Scribe_Values.Look(ref pausedAtTick, "roPausedAtTick", 0);
            Scribe_Values.Look(ref entryBlockSelfStarted, "roEntryBlockSelfStarted", false);
            Scribe_Values.Look(ref emergencyOvertimeUses, "roOvertimeUses", 0);
            // S12 在途计划：自记一份（原版那份在 pather 的私有字段里，读不到）。
            // 存了它，"已决定/延后决定"在存读档中途也不会丢。
            Scribe_References.Look(ref plannedCaravan, "roPlannedCaravan");
            Scribe_Defs.Look(ref plannedDef, "roPlannedDef");
            Scribe_Deep.Look(ref plannedRequest, "roPlannedRequest");
            Scribe_Values.Look(ref plannedAtTick, "roPlannedAtTick", 0);
            // RIM-5：计划里"实际开始移动"的那一刻（4B 的计时起点）
            Scribe_Values.Look(ref planDepartTickAbs, "roPlanDepartTickAbs", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (active != null && active.def == null)
                {
                    active = null;
                }
                // 存量由地点持有、委派只引用 → 读档后重新挂上（各存一份会分裂成两套计数）
                if (active != null)
                {
                    active.deposit = deposit;
                }
                // 在途计划也要跟着重新校验：远行队可能已经在读档间隙解散/改道
                if (plannedCaravan == null || plannedDef == null)
                {
                    plannedCaravan = null;
                    plannedDef = null;
                    plannedRequest = null;
                }
                // S14：把"被委派动过的点进不去"这条不变量在读档时重新对齐一次
                EnsureEntryBlockedAfterWorked();
            }
        }

        /// <summary>
        /// 读档时补齐"干完之后禁止进入"（S14）。
        ///
        /// 为什么需要单独一步：`ApplyEntryBlock` 是**收工/中断那一刻跑一次**的动作，
        /// 而 S14 之前物资点/营救的 Def 没开 <see cref="DelegationDef.blockMapEntryAfterWorked"/>，
        /// 那些地点在收工当时就被 `ReleaseEntryBlockIfUnwarranted` 把冷却撤掉了 ——
        /// 光把 Def 改成 true **不会追溯**已经玩过的存档。这里补一次 `Start`，
        /// 让"动过存量 ⇒ 只能走委派路线"变成**任何时刻都成立的不变量**，而不是一次性调用。
        ///
        /// 只处理**已经收工**的地点（`active == null`）：进行中的地点由
        /// <see cref="EnsureEntryBlockedWhileActive"/> 用 0.5 天的短窗口续期，
        /// 两者都做的话，玩家会在委派还在跑时看到"还需 9999 天才能进入"。
        /// </summary>
        private void EnsureEntryBlockedAfterWorked()
        {
            if (active != null)
            {
                return;
            }
            Site site = Site;
            DelegationDef def = deposit?.def;
            if (def == null || site == null || site.Destroyed || !def.blockMapEntryAfterWorked)
            {
                return;
            }
            // 没动过存量（点开委派就取消）不封 —— 与 ApplyEntryBlock 同一把尺子
            if (deposit.unitsMined <= 0f)
            {
                return;
            }
            // 已经有地图就没意义：`BlocksEntering` 在 ParentHasMap 时恒为 false
            if (site.HasMap)
            {
                return;
            }
            EnterCooldownComp comp = site.GetComponent<EnterCooldownComp>();
            if (comp == null)
            {
                Log.WarningOnce("[RimDelegation] 想禁止进入该地点，但它没有 EnterCooldownComp" +
                                "（Site def 上本应有），已跳过", 0x5E0CF);
                return;
            }
            // 已经封着就别动它（免得每读一次档都把剩余时间续回最大值）
            if (comp.Active && comp.DaysLeft >= def.blockMapEntryDays * 0.5f)
            {
                return;
            }
            comp.Start(def.blockMapEntryDays);
            entryBlockSelfStarted = true;
            DelegationUtility.LogVerbose($"读档补齐进入封禁：{site.Label}（委派动过该点的存量）");
        }

        // ---------------------------------------------------------------- 工具
        public void DevAdvance(int ticks)
        {
            if (active == null)
            {
                Messages.Message("[DEV] 没有进行中的委派", MessageTypeDefOf.RejectInput, false);
                return;
            }
            TickDelegation(ticks);
        }

        public void DevForceComplete()
        {
            Delegation d = active;
            if (d == null)
            {
                Messages.Message("[DEV] 没有进行中的委派", MessageTypeDefOf.RejectInput, false);
                return;
            }
            d.cellsMined = d.totalCells;
            Complete(d, Site, "DEV: 强制收工（视为采空）");
        }

        /// <summary>DEV 用：手动恢复被暂停的超时计时。</summary>
        public void DevResumeTimeout()
        {
            if (!timeoutPaused)
            {
                return;
            }
            ResumeTimeout(Site);
        }

        private Gizmo AbortGizmo(Delegation d)
        {
            Command_Action cmd = new Command_Action();
            cmd.defaultLabel = "中止委派";
            cmd.defaultDesc = InspectText();
            // ⚠️ `Verse.Command.DrawIcon`：`icon == null` 时画 **`BaseContent.BadTex`（洋红色叉）** ——
            // 所以每个 Command_Action 都必须给图标，否则界面上就是一排红叉（用户 S16 截图）。
            // 这里借用原版现成纹理（零美术成本）。
            cmd.icon = TexCommand.RemoveRoutePlannerWaypoint;
            cmd.action = () => Abort(d, "玩家主动中止");
            return cmd;
        }

        /// <summary>
        /// 暂停 / 继续（§19.24）。
        /// 暂停期间：不开采、不产出、**不消耗天数预算**，而且队员**真的在休息**
        /// （见 DelegationRegistry 里把暂停中的委派从 workingPawns 排除掉）。
        /// </summary>
        private Gizmo PauseGizmo(Delegation d)
        {
            string activity = d.Worker?.ActivityName ?? "开采";
            Command_Action cmd = new Command_Action();
            cmd.defaultLabel = d.paused ? "继续委派" : "暂停委派";
            cmd.defaultDesc = d.paused
                ? $"恢复{activity}。暂停期间不产出，也不消耗计划天数。"
                : $"让队员就地休息：停止{activity}与产出，休息条会正常回升，暂停的时间不计入计划天数。";
            cmd.icon = TexCommand.PauseCaravan;
            cmd.action = () => TogglePause(d);
            return cmd;
        }

        public void TogglePause(Delegation d)
        {
            if (d == null) return;
            string activity = d.Worker?.ActivityName ?? "开采";
            d.paused = !d.paused;
            Caravan caravan = d.caravan;
            Messages.Message(
                d.paused
                    ? (caravan != null ? caravan.LabelCap + "：" : "") + $"委派已暂停 —— 队员就地休息，暂停期间不产出、不计入计划天数。"
                    : (caravan != null ? caravan.LabelCap + "：" : "") + $"委派已恢复，继续{activity}。",
                d.paused ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.PositiveEvent, false);
        }

        // ---------------------------------------------------------------- 模式切换（S7）

        /// <summary>
        /// **作业期间**切换委派模式（用户要求：委派过程中支持模式切换）。
        ///
        /// 为什么不设代价：模式本来就是"作息表"，换一班不该罚款。
        /// RIM-5 起模式的后果**只有一处**：它是满意度来源「作业强度」的输入，
        /// 换过去以后满意度立刻变（每 tick 重算），但**每日心情记忆**仍要等下一次日结算才换档
        /// —— 旧记忆按 `durationDays` 自然过期，所以"换班"不会立刻兑现心情。
        /// ⚠️ 因此**不要**在这里重置 `ticksSinceMoodTick`：那会让玩家靠频繁换模式白拿心情。
        /// </summary>
        public void OpenModeMenu(Delegation d)
        {
            if (d?.def == null)
            {
                return;
            }
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            if (d.def.modes.NullOrEmpty())
            {
                options.Add(new FloatMenuOption("（该委派没有定义模式）", null));
            }
            else
            {
                for (int i = 0; i < d.def.modes.Count; i++)
                {
                    DelegationModeDef local = d.def.modes[i];
                    if (local == null)
                    {
                        continue;
                    }
                    bool current = d.mode == local;
                    string label = string.Format("{0}{1} · {2} · 作业强度 {3:+0.#;-0.#;0}",
                        current ? "✓ " : "     ", local.LabelCap, local.HoursLabel, local.workIntensity);
                    // RIM-5：把"换到这个模式后大概是什么满意度、每天多少心情、速率多少"摊开，
                    // 否则玩家只看到作息窗口，完全不知道这一换代价在哪。
                    label += "\n" + DelegationUIUtility.SatisfactionLineEstimated(local, d.DaysAway);
                    if (current)
                    {
                        label += "（当前）";
                    }
                    if (!local.description.NullOrEmpty())
                    {
                        label += "\n" + local.description;
                    }
                    DelegationModeDef chosen = local;
                    options.Add(new FloatMenuOption(label, () => SetMode(d, chosen)));
                }
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>把已经开工的委派切到另一个模式（页签 / 皮肤 / gizmo 共用这一处）。</summary>
        public void SetMode(Delegation d, DelegationModeDef mode)
        {
            if (d == null || mode == null || d.mode == mode)
            {
                return;
            }
            DelegationModeDef old = d.mode;
            d.mode = mode;
            Messages.Message(
                string.Format("委派模式已切换：{0} → {1}（{2} · 作业强度 {3:+0.#;-0.#;0}；满意度已按新模式重算，每日心情下一次日结算兑现）",
                    old?.LabelCap.ToString() ?? "?", mode.LabelCap, mode.HoursLabel, mode.workIntensity),
                MessageTypeDefOf.NeutralEvent, false);
            DelegationUtility.LogVerbose(
                $"委派模式切换：{d.def?.defName} @ {Site?.Label} → {mode.defName}");
        }

        private Gizmo ModeGizmo(Delegation d)
        {
            Command_Action cmd = new Command_Action();
            cmd.defaultLabel = "委派模式：" + d.ModeLine();
            cmd.defaultDesc = "切换委派模式（作息窗口 × 作业强度）。\n" +
                              "换班本身不扣心情 —— 满意度会按新模式（作业强度那一项）立刻重算，"
                              + "每日心情记忆要等下一次日结算才换档。";
            cmd.icon = TexCommand.Replant;
            cmd.action = () => OpenModeMenu(d);
            return cmd;
        }

        // ---------------------------------------------------------------- 作业期编辑（S9）

        /// <summary>
        /// 作业期间修改**结束条件**（用户要求：委派 UI 支持编辑「采空为止」）。
        ///
        /// 为什么允许改：结束条件回答的是"干到什么时候收工"，而玩家对工期的判断会随现场情况
        /// （守军、补给、疲劳）变化；原版只在下达委派那一刻能选，想改就得中止重开，
        /// 而中止会挂「白跑一趟」-6 的心情 —— 等于变相罚款。所以这里给一个改的入口。
        ///
        /// ⚠️ 账本不重置：`ElapsedDays` / `MinedUnitsTotal` 都是**从开工算起**的累计值，
        ///    所以把条件改成"干满 1 天"时若已经干了 2 天，下一 tick 就会正常收工。
        ///    这是有意的（改的是目标，不是账本），但编辑器里必须把这句话写给玩家看。
        /// </summary>
        public void OpenEndConditionEditor(Delegation d)
        {
            if (d?.def == null)
            {
                return;
            }
            Find.WindowStack.Add(new Dialog_SetDelegationEndCondition(this, d));
        }

        /// <summary>应用结束条件（编辑器与将来的其它入口共用这一处，判定口径只有一份）。</summary>
        public void SetEndCondition(Delegation d, DelegationEndCondition condition, int days, int quota)
        {
            if (d?.def == null)
            {
                return;
            }
            int daysLimit = Mathf.Clamp(days, 1, Mathf.Max(1, d.def.maxDaysLimit));
            int quotaCap = QuotaCapOf(d);
            int quotaUnits = Mathf.Clamp(quota, 1, Mathf.Max(1, quotaCap));

            d.endCondition = condition;
            d.daysLimit = daysLimit;
            d.quotaUnits = quotaUnits;

            string label = DelegationUIUtility.EndConditionLabel(condition, daysLimit, quotaUnits, d.Worker);
            Messages.Message(
                (Site != null ? Site.LabelCap + "：" : "") + string.Format("委派结束条件已改为「{0}」", label),
                MessageTypeDefOf.NeutralEvent, false);
            DelegationUtility.LogVerbose(
                $"结束条件变更：{d.def.defName} @ {Site?.Label} → {condition}（天数 {daysLimit} / 配额 {quotaUnits}）");
        }

        /// <summary>
        /// 本趟"按产出配额"的**上限**：这一趟最多能产出多少个 <c>OutputUnitName</c>。
        ///
        /// 与对话框里的口径一致（对话框用 depot.UnitsRemaining × yieldPerUnit），
        /// 只是作业期没有"剩余存量"，改用本趟的 `totalCells × yieldPerCell`。
        /// </summary>
        public static int QuotaCapOf(Delegation d)
        {
            if (d == null)
            {
                return 1;
            }
            return Mathf.Max(1, Mathf.RoundToInt(d.totalCells * Mathf.Max(1, d.yieldPerCell)));
        }

        /// <summary>
        /// 作业期间**编辑参与者名单**（用户要求：委派 UI 支持编辑「参与者」+ 参与者支持排序）。
        ///
        /// 为什么允许改：车队途中可能合并/拆分（伤员、访客加入），而"谁在干活"直接决定产能与
        /// 负重上限。让玩家只能中止重开，代价是那一笔"白跑一趟"的心情 —— 与改结束条件同一个理由。
        /// </summary>
        public void OpenParticipantEditor(Delegation d)
        {
            if (d?.caravan == null || d.caravan.Destroyed)
            {
                Messages.Message("远行队已不存在，无法编辑参与者", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Find.WindowStack.Add(new Dialog_EditDelegationParticipants(this, d));
        }

        /// <summary>应用参与者名单（编辑器与将来的其它入口共用这一处）。</summary>
        public void SetParticipants(Delegation d, List<Pawn> list)
        {
            if (d?.def == null || d.caravan == null || d.caravan.Destroyed)
            {
                return;
            }
            // 名单以"车队里此刻真的能干活的人"为准：计划期选的人可能已经不在队里了
            List<Pawn> eligible = DelegationUtility.EligiblePawns(d.caravan);
            List<Pawn> result = new List<Pawn>();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Pawn p = list[i];
                    if (p != null && eligible.Contains(p) && !result.Contains(p))
                    {
                        result.Add(p);
                    }
                }
            }

            int need = Mathf.Max(1, d.def.minPawns);
            int cap = d.def.maxPawns > 0 ? d.def.maxPawns : int.MaxValue;
            if (result.Count < need)
            {
                Messages.Message(string.Format("至少需要 {0} 名可行动人员，参与者未改动", need),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (result.Count > cap)
            {
                Messages.Message(string.Format("这个委派最多 {0} 人，参与者未改动", cap),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            d.participants = result;
            Messages.Message(
                (Site != null ? Site.LabelCap + "：" : "") + string.Format("委派参与者已更新为 {0} 人", result.Count),
                MessageTypeDefOf.NeutralEvent, false);
            DelegationUtility.LogVerbose(
                $"参与者变更：{d.def.defName} @ {Site?.Label} → {result.Count} 人");
        }

        /// <summary>
        /// 点参与者列表里某个人时的菜单（S10 用户要求：名单直接在主控台的参与者列表里改）。
        ///
        /// 为什么不是"点一下就移出"：移除参与者会**当场改变产能与负重上限**，
        /// 误触的代价太高。所以走一层 FloatMenu（移出 / 打开信息卡），
        /// 与原版"点谁就弹出谁的操作"的习惯一致。
        /// </summary>
        public void OpenParticipantRowMenu(Delegation d, Pawn p)
        {
            if (d == null || p == null)
            {
                return;
            }
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            int need = Mathf.Max(1, d.def?.minPawns ?? 1);
            string removeLabel = string.Format("把 {0} 移出这次委派", p.LabelShortCap);
            if (d.participants != null && d.participants.Count <= need)
            {
                options.Add(new FloatMenuOption(
                    removeLabel + string.Format("（至少要留 {0} 人）", need), null));
            }
            else
            {
                options.Add(new FloatMenuOption(removeLabel, () => RemoveParticipant(d, p)));
            }
            options.Add(new FloatMenuOption("打开信息卡：{0}".Formatted(p.LabelShortCap),
                () => Find.WindowStack.Add(new Dialog_InfoCard(p))));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>把一个人加进这次委派（名单、上限、去重都由 <see cref="SetParticipants" /> 把关）。</summary>
        public void AddParticipant(Delegation d, Pawn p)
        {
            if (d == null || p == null || d.participants == null)
            {
                return;
            }
            if (d.participants.Contains(p))
            {
                return;
            }
            List<Pawn> list = new List<Pawn>(d.participants) { p };
            SetParticipants(d, list);
        }

        /// <summary>把一个人移出这次委派。</summary>
        public void RemoveParticipant(Delegation d, Pawn p)
        {
            if (d?.participants == null || p == null)
            {
                return;
            }
            List<Pawn> list = new List<Pawn>();
            for (int i = 0; i < d.participants.Count; i++)
            {
                if (d.participants[i] != null && d.participants[i] != p)
                {
                    list.Add(d.participants[i]);
                }
            }
            SetParticipants(d, list);
        }

        /// <summary>
        /// V/X 开关：这个人参不参加这次委派（S13 用户要求"参与者的最前面添加 V/X 控制是否参与"）。
        ///
        /// 参加 → 移出；没参加 → 加入。上下限与"队里还有没有这个人"的判定全部复用
        /// <see cref="AddParticipant" /> / <see cref="RemoveParticipant" />（只有一份口径）。
        /// </summary>
        public void ToggleParticipant(Delegation d, Pawn p)
        {
            if (d?.participants == null || p == null)
            {
                return;
            }
            if (d.participants.Contains(p))
            {
                RemoveParticipant(d, p);
            }
            else
            {
                AddParticipant(d, p);
            }
        }

        /// <summary>
        /// 「添加人员」菜单：列出**队里还能干活、但还没参加**的人（S10）。
        /// 口径与对话框同一条（<see cref="DelegationUtility.EligiblePawns" />），所以两处名单不会分叉。
        /// </summary>
        public void OpenAddParticipantMenu(Delegation d)
        {
            if (d?.caravan == null || d.caravan.Destroyed)
            {
                Messages.Message("远行队已不存在，无法添加参与者", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int cap = d.def != null && d.def.maxPawns > 0 ? d.def.maxPawns : int.MaxValue;
            if (d.participants != null && d.participants.Count >= cap)
            {
                Messages.Message(string.Format("这个委派最多 {0} 人，先移出一位再加", cap),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<Pawn> eligible = DelegationUtility.EligiblePawns(d.caravan);
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            for (int i = 0; i < eligible.Count; i++)
            {
                Pawn p = eligible[i];
                if (p == null || (d.participants != null && d.participants.Contains(p)))
                {
                    continue;
                }
                Pawn local = p;
                options.Add(new FloatMenuOption(
                    DelegationUIUtility.PawnLine(p, d.Worker, d.def), () => AddParticipant(d, local)));
            }
            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("队里没有可添加的人（都已参加，或没有可行动人员）", null));
            }
            // 常驻的"批量"出口：逐个人点适合微调，一次挑多人（含移出）仍然走那个窗口。
            // 两个入口共用 `SetParticipants`，所以判定只有一份。
            options.Add(new FloatMenuOption("一次改多人…（编辑参与者窗口）", () => OpenParticipantEditor(d)));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        // ---------------------------------------------------------------- 紧急加班（§19.26）

        /// <summary>
        /// 紧急加班：**用心情买时间**。
        ///
        /// 机制（三段都必要，缺一条就变成免费加班）：
        ///   ① 额度：每次点买 `def.emergencyOvertimeHoursPerUse` 小时的"无视工时窗口"额度，
        ///      且额度**只在休息时段被消耗**（工时段照常按窗口走）⇒ 4 小时是真的多干 4 小时；
        ///   ② 心情：同一个地点第 n 次加班挂 ThoughtDef 的第 n 级（-4/-8/-12/…，见 Def 注释）；
        ///   ③ 生理：加班期间 <see cref="Delegation.IsWorkTime"/> 为真 ⇒
        ///      `Patch_CaravanNeedsTracker_Rest` 不再补休息 ⇒ 休息条下降 ⇒ 既有的
        ///      「疲劳 → 工伤倍率」自己会把这笔账收上来（不需要第二套公式）。
        /// </summary>
        public void StartEmergencyOvertime(Delegation d)
        {
            if (d?.def == null || !d.def.AllowsEmergencyOvertime)
            {
                return;
            }
            if (d.paused)
            {
                Messages.Message("委派处于暂停中：先「继续委派」才能加班", MessageTypeDefOf.RejectInput, false);
                return;
            }

            int add = d.def.EmergencyOvertimeTicksPerUse;
            d.overtimeTicksRemaining += add;
            emergencyOvertimeUses++;
            GrantOvertimeThought(d);

            Caravan caravan = d.caravan;
            Messages.Message(
                (caravan != null ? caravan.LabelCap + "：" : "") +
                string.Format("紧急加班 {0:0.#} 小时 —— 期间无视工时窗口，但队员不会补休息（事故伤害风险上升）。", add / 2500f),
                MessageTypeDefOf.CautionInput, false);
            DelegationUtility.LogVerbose(
                $"紧急加班：{d.def.defName} @ {Site?.Label}，额度 +{add} ticks（累计剩余 {d.overtimeTicksRemaining}），本地点第 {emergencyOvertimeUses} 次");
        }

        /// <summary>取消加班：把剩余额度清零（**不退心情** —— 那笔账是"把人叫起来"的那一刻就产生的）。</summary>
        public void CancelEmergencyOvertime(Delegation d)
        {
            if (d == null || !d.EmergencyOvertimeActive)
            {
                return;
            }
            d.overtimeTicksRemaining = 0;
            Messages.Message("已取消紧急加班，队员回到正常作息。", MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>给全队挂"这个地点的第 n 次加班"记忆（等级 = 地点累计次数）。</summary>
        private void GrantOvertimeThought(Delegation d)
        {
            ThoughtDef thought = d?.def?.emergencyOvertimeMoodThought;
            if (thought == null)
            {
                // 静默失效：加班不扣心情 = 这机制变成免费。ModBoot 启动时已经报过一次，这里再提一句现场。
                Log.WarningOnce("[RimDelegation] 紧急加班没有配置心情 Def（DelegationDef.emergencyOvertimeMoodThought），本次加班不扣心情", 0x5E0DC);
                return;
            }
            int stage = EmergencyOvertimeStage(d.def, emergencyOvertimeUses);
            List<Pawn> list = d.caravan?.PawnsListForReading;
            if (list == null)
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == null || p.Dead || !p.RaceProps.Humanlike)
                {
                    continue;
                }
                try
                {
                    // stackLimit = 1 ⇒ 原版会把同 Def 的旧记忆挤掉 ⇒ 天然是"第 n 次覆盖第 n-1 次"
                    Thought_Memory memory = ThoughtMaker.MakeThought(thought, stage);
                    if (memory != null)
                    {
                        p.needs?.mood?.thoughts?.memories?.TryGainMemory(memory);
                    }
                }
                catch (Exception ex)
                {
                    Log.WarningOnce("[RimDelegation] 紧急加班心情挂载失败：" + ex.Message, 0x5E0D0);
                }
            }
        }

        private Gizmo EmergencyOvertimeGizmo(Delegation d)
        {
            Command_Action cmd = new Command_Action();
            float hours = d.def?.emergencyOvertimeHoursPerUse ?? 0f;
            string remain = d.EmergencyOvertimeActive
                ? string.Format("（当前剩余 {0:0.#} 小时）", d.overtimeTicksRemaining / 2500f)
                : "";
            cmd.defaultLabel = d.EmergencyOvertimeActive
                ? string.Format("紧急加班：续 +{0:0.#}h（剩 {1:0.#}h）", hours, d.overtimeTicksRemaining / 2500f)
                : string.Format("紧急加班 +{0:0.#}h", hours);
            cmd.defaultDesc =
                string.Format("买 {0:0.#} 小时加班额度：期间无视工时窗口继续作业，但**不补休息**（休息条下降 → 工作遭受事故伤害倍率上升）。{1}\n", hours, remain) +
                string.Format("心情代价按**本地点第几次加班**递进：第 1 次 -4、第 2 次 -8、第 3 次 -12……（当前将是第 {0} 次）\n",
                    emergencyOvertimeUses + 1) +
                "额度只在休息时段被消耗；工时段照常按窗口走，不扣额度。";
            cmd.icon = TexCommand.FireAtWill;
            cmd.action = () => StartEmergencyOvertime(d);
            if (d.paused)
            {
                cmd.Disable("委派处于暂停中：先「继续委派」");
            }
            return cmd;
        }

        private Gizmo CancelOvertimeGizmo(Delegation d)
        {
            Command_Action cmd = new Command_Action();
            cmd.defaultLabel = "结束紧急加班";
            cmd.defaultDesc = string.Format("把剩余 {0:0.#} 小时加班额度作废，队员回到正常作息。\n心情代价**不退还**（那笔账在把人叫起来的那一刻就已经产生）。",
                d.overtimeTicksRemaining / 2500f);
            cmd.icon = TexCommand.ClearPrioritizedWork;
            cmd.action = () => CancelEmergencyOvertime(d);
            return cmd;
        }

        private static Texture2D LoadIcon(string path)
        {
            if (path.NullOrEmpty())
            {
                return null;
            }
            return ContentFinder<Texture2D>.Get(path, false);
        }

        private static string LetterLabel(string raw, string fallback)
        {
            return raw.NullOrEmpty() ? fallback : raw;
        }

        public static string EndConditionLabelOf(Delegation d)
        {
            if (d == null)
            {
                return "采空为止";
            }
            return DelegationUIUtility.EndConditionLabel(d.endCondition, d.daysLimit, d.quotaUnits, d.Worker);
        }

        private static string LetterText(string raw, string fallback, params object[] args)
        {
            string s = raw.NullOrEmpty() ? fallback : raw;
            return string.Format(s, args);
        }
    }
}
