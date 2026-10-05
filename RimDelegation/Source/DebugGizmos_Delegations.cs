using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 开发者模式下的验证工具。
    ///
    /// 最重要的一条：验证 DESIGN §7.2 的"零 Harmony 暂停原版 30 天计时"是否真的停表。
    /// 已知事实（反编译确认）：
    ///   QuestPart_Delay.TicksLeft = enableTick + delayTicks - TicksGame
    ///   且 if (State != Enabled) return 0;   ← 所以暂停期间 TicksLeft 恒为 0，这是正常的，不是失败
    ///   暂停靠发 inSignalDisable 信号（public 路径）；
    ///   恢复必须反射写 private state，并用公开字段 delayTicks 补偿被暂停掉的时长。
    /// </summary>
    public static class DebugGizmos_Delegations
    {
        public static IEnumerable<Gizmo> For(WorldObjectComp_Delegations comp)
        {
            Site site = comp.Site;
            if (site == null)
            {
                yield break;
            }

            Command_Action log = new Command_Action();
            log.defaultLabel = "DEV: 打印超时计时器";
            log.defaultDesc = "把 State / delayTicks / TicksLeft / 暂停记录写进 Player.log";
            log.action = () => LogTimeout(site, comp);
            yield return log;

            Command_Action pause = new Command_Action();
            pause.defaultLabel = "DEV: 发信号暂停超时计时";
            pause.defaultDesc = "读一次剩余时间，再发该 QuestPart 的 inSignalDisable 信号，然后打印前后状态";
            pause.action = delegate
            {
                LogTimeout(site, comp);
                bool ok = DelegationUtility.TryPauseTimeout(site, out int remaining, out string report);
                if (ok)
                {
                    comp.timeoutPaused = true;
                    comp.pausedTimeoutRemaining = remaining;
                    comp.pausedAtTick = Find.TickManager.TicksGame;
                }
                Log.Message($"[RimDelegation][DEV] 暂停结果 = {ok} | {report}");
                LogTimeout(site, comp);
            };
            yield return pause;

            Command_Action resume = new Command_Action();
            resume.defaultLabel = "DEV: 恢复超时计时";
            resume.defaultDesc = "反射把 state 写回 Enabled，并用 delayTicks 补回暂停掉的时长";
            resume.action = delegate
            {
                if (!comp.timeoutPaused)
                {
                    Log.Message("[RimDelegation][DEV] 该地点当前不处于暂停状态，无需恢复");
                    return;
                }
                LogTimeout(site, comp);
                comp.DevResumeTimeout();
                LogTimeout(site, comp);
            };
            yield return resume;

            Command_Action advance = new Command_Action();
            advance.defaultLabel = "DEV: 委派推进 1 天";
            advance.defaultDesc = "按 60000 ticks 调一次委派结算（验证工时门控、速率、结束条件与心情挂载）";
            advance.action = () => comp.DevAdvance(60000);
            yield return advance;

            Command_Action finish = new Command_Action();
            finish.defaultLabel = "DEV: 立即完成委派";
            finish.action = () => comp.DevForceComplete();
            yield return finish;

            // ── 暂停 / 继续（§19.24）──
            Command_Action togglePause = new Command_Action();
            togglePause.defaultLabel = "DEV: 暂停/继续委派";
            togglePause.defaultDesc = "验证暂停：不开采、不产出、不消耗计划天数，且队员的休息条会回升";
            togglePause.action = delegate
            {
                if (comp.active == null)
                {
                    Log.Message("[RimDelegation][DEV] 当前没有进行中的委派");
                    return;
                }
                comp.TogglePause(comp.active);
                Log.Message($"[RimDelegation][DEV] paused={comp.active.paused} | pausedTicksTotal={comp.active.pausedTicksTotal}");
            };
            yield return togglePause;

            // ── 随机事件演示（§19.24）──
            List<DelegationEventDef> events = DefDatabase<DelegationEventDef>.AllDefsListForReading;
            for (int i = 0; i < events.Count; i++)
            {
                DelegationEventDef def = events[i];
                if (def == null) continue;
                Command_Action fire = new Command_Action();
                fire.defaultLabel = "DEV: 触发事件「" + def.LabelCap + "」";
                fire.defaultDesc = "跳过概率与冷却，直接触发一次，用来演示随机事件的实际效果。\n" +
                                   "MTB " + def.mtbDays + " 天 · 冷却 " + def.minDaysBetween + " 天";
                fire.action = delegate
                {
                    if (comp.active == null)
                    {
                        Log.Message("[RimDelegation][DEV] 当前没有进行中的委派，无法触发事件");
                        return;
                    }
                    comp.DevFireEvent(def);
                };
                yield return fire;
            }
        }

        public static void LogTimeout(Site site, WorldObjectComp_Delegations comp)
        {
            QuestPart_WorldObjectTimeout part = DelegationUtility.FindTimeoutPart(site);
            string partInfo = part == null
                ? "未找到 QuestPart_WorldObjectTimeout"
                : $"part.State={part.State} | inSignalDisable='{part.inSignalDisable}' | delayTicks={part.delayTicks} | TicksLeft={part.TicksLeft}";
            string pauseInfo = comp == null
                ? "comp=null"
                : $"comp.timeoutPaused={comp.timeoutPaused} | 暂停时剩余={comp.pausedTimeoutRemaining} ticks（{comp.pausedTimeoutRemaining / 60000f:0.#} 天）| 已在暂停中 {((Find.TickManager.TicksGame - comp.pausedAtTick) / 60000f):0.#} 天";
            Log.Message(string.Format(
                "[RimDelegation][DEV] {0} | HasWorldObjectTimeout={1} | WorldObjectTimeoutTicksLeft={2}\n   {3}\n   {4}\n   注意：TicksLeft 在 State=Disabled 时恒为 0（原版实现），不是故障。",
                site.Label, site.HasWorldObjectTimeout, site.WorldObjectTimeoutTicksLeft, pauseInfo, partInfo));
        }
    }
}
