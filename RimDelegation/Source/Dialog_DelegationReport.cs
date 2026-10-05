using System.Collections.Generic;
using System.Text;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>报告窗口需要的数据（脱离 <see cref="Delegation" /> 也能画 —— 委派结束后对象可能已经不再被引用）。</summary>
    public class DelegationReportData
    {
        public string title;
        public string summary;
        public List<string> eventLines;
        public bool aborted;

        /// <summary>从一条刚结束的委派上抓数据（只在 Complete / Abort 那一刻调）。</summary>
        public static DelegationReportData Build(Delegation d, Site site, string reason, bool aborted)
        {
            if (d == null)
            {
                return null;
            }
            DelegationReportData data = new DelegationReportData
            {
                title = string.Format("{0}{1} @ {2}",
                    aborted ? "委派中断：" : "委派收工：",
                    d.def?.label ?? "?", site?.Label ?? "?"),
                aborted = aborted,
                eventLines = DelegationUIUtility.EventReportLines(d),
            };

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("结束理由：" + (reason.NullOrEmpty() ? "—" : reason));
            sb.AppendLine(string.Format("参与 {0} 人 · 模式 {1}",
                d.participants?.Count ?? 0, d.ModeLine()));
            sb.Append(string.Format("累计作业 {0:0.#} 小时 / 休息 {1:0.#} 小时",
                d.ticksWorked / Delegation.TicksPerHour, d.ticksResting / Delegation.TicksPerHour));
            if (d.ticksStalled > 0)
            {
                sb.Append(string.Format(" / 停摆 {0:0.#} 小时", d.ticksStalled / Delegation.TicksPerHour));
            }
            if (d.eventsFired > 0)
            {
                sb.Append(string.Format("\n期间发生 {0} 次随机事件", d.eventsFired));
            }
            string progress = d.Worker?.ProgressLabel(d);
            if (!progress.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append("进度：" + progress);
            }
            string delivery = d.Worker?.DeliverySummary(d);
            if (!delivery.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append("产出：" + delivery);
            }
            data.summary = sb.ToString();
            return data;
        }
    }

    /// <summary>
    /// 委派结束的**签核报告**窗口（S15）。
    ///
    /// 用户拍板的三个字段（打断版）：
    ///   · `forcePause = true`            → 弹出即暂停游戏，玩家必须先看；
    ///   · `closeOnClickedOutside = false` → 点外面不关，**必须点「确认」** —— 这才叫"签核"；
    ///   · `absorbInputAroundWindow = true` → 保持默认，别让点击穿透到世界地图
    ///     （否则会出现"游戏暂停着、但还能点到地图"的混乱状态）。
    ///
    /// 内容与信件**同一份来源**（`DelegationUIUtility.EventReportLines`）——
    /// 两处各写一份格式化，迟早出现"信里有、窗口里没有"。
    /// </summary>
    public class Dialog_DelegationReport : Window
    {
        private readonly DelegationReportData data;
        private Vector2 scroll = Vector2.zero;

        public Dialog_DelegationReport(DelegationReportData data)
        {
            this.data = data;
            forcePause = true;
            closeOnClickedOutside = false;
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            onlyOneOfTypeAllowed = true;
            draggable = true;
            resizeable = true;
            doWindowBackground = true;
            // S15 第三期：窗口出现时的提示音（与事件提示音共用同一个开关，默认关）
            if (RimDelegationMod.Settings?.eventSoundEnabled ?? false)
            {
                soundAppear = DefDatabase<SoundDef>.GetNamedSilentFail("Click");
            }
        }

        public override Vector2 InitialSize => new Vector2(560f, 460f);

        public override void DoWindowContents(Rect inRect)
        {
            // 画法与主控台右栏的**历史详情共用** DelegationReportUI —— 两处各画一份迟早分叉
            Rect content = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 46f);
            DelegationReportUI.Draw(content, data, ref scroll);

            // 签核：必须点这一颗（点外面关不掉）
            if (Widgets.ButtonText(new Rect(inRect.xMax - 130f, inRect.yMax - 36f, 130f, 32f), "确认"))
            {
                Close();
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            // 队列化：多条委派可能在同一 tick 前后完成，关掉一份再弹下一份
            DelegationReport.NotifyClosed();
        }
    }

    /// <summary>
    /// 报告窗口的**排队器**（S15）。
    ///
    /// 为什么需要它：`onlyOneOfTypeAllowed = true` 只会让第二个窗口被**拒绝**（报告直接丢掉），
    /// 而"同时收工两条委派"完全可能发生。所以这里自己排队：一次只开一个，关掉再弹下一个。
    /// </summary>
    public static class DelegationReport
    {
        private static readonly Queue<DelegationReportData> pending = new Queue<DelegationReportData>();
        private static bool showing;

        /// <summary>请求弹一份报告（若设置里没开，调用方不该走到这里）。</summary>
        public static void Enqueue(DelegationReportData data)
        {
            if (data == null)
            {
                return;
            }
            pending.Enqueue(data);
            ShowNext();
        }

        private static void ShowNext()
        {
            if (showing || pending.Count == 0)
            {
                return;
            }
            showing = true;
            Find.WindowStack.Add(new Dialog_DelegationReport(pending.Dequeue()));
        }

        internal static void NotifyClosed()
        {
            showing = false;
            ShowNext();
        }
    }
}
