using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一条**已结束委派**的记录（S15 第二期）。
    ///
    /// 只存"事后还看得懂"的字段：是谁、在哪、什么时候、为什么结束、干了多久、产出多少、期间出了什么事。
    /// 不引用 <see cref="Delegation" />（它随委派结束就没人持有了），但能转成
    /// <see cref="DelegationReportData" /> 供报告窗口复用 —— 报告文本同样只有一份来源。
    /// </summary>
    public class DelegationRecord : IExposable
    {
        public DelegationDef def;

        /// <summary>地点名（**必须在 site.Destroy() 之前抓**）。</summary>
        public string siteName;

        /// <summary>地点所在格（显示绝对时间要用它算经度/时区）；-1 = 未知。</summary>
        public int tile = -1;

        /// <summary>起止时刻（TicksAbs）。⚠️ 原版 `Delegation.startedTickAbs` 与 `GenTicks.TicksAbs` 都是 **long**，别收窄成 int。</summary>
        public long startTickAbs;
        public long endTickAbs;
        public bool aborted;
        public string reason;
        public string progressText;
        public string deliveryText;
        public float workedHours;
        public float stalledHours;

        /// <summary>事件明细行（与信件/报告窗口同一份格式化：`EventReportLines`）。</summary>
        public List<string> eventLines;

        public string Title => string.Format("{0}{1} @ {2}",
            aborted ? "委派中断：" : "委派收工：", def?.label ?? "?", siteName ?? "?");

        /// <summary>列表里那一行的副标题（结束时刻 · 时长 · 产出）。</summary>
        public string SubLine
        {
            get
            {
                string when = tile >= 0
                    ? GenDate.DateFullStringWithHourAt(endTickAbs, Find.WorldGrid.LongLatOf(tile))
                    : null;
                StringBuilder sb = new StringBuilder();
                if (!when.NullOrEmpty())
                {
                    sb.Append(when);
                }
                sb.Append(sb.Length > 0 ? " · " : "");
                sb.Append(string.Format("作业 {0:0.#}h", workedHours));
                if (stalledHours > 0f)
                {
                    sb.Append(string.Format(" · 停摆 {0:0.#}h", stalledHours));
                }
                if (!(eventLines?.Count > 0))
                {
                    return sb.ToString();
                }
                sb.Append(string.Format(" · 事件 {0} 条", eventLines.Count));
                return sb.ToString();
            }
        }

        public static DelegationRecord From(Delegation d, Site site, string reason, bool aborted)
        {
            if (d == null)
            {
                return null;
            }
            return new DelegationRecord
            {
                def = d.def,
                siteName = site?.Label ?? "?",
                tile = site != null ? site.Tile.tileId : -1,
                startTickAbs = d.startedTickAbs,
                endTickAbs = GenTicks.TicksAbs,
                aborted = aborted,
                reason = reason,
                progressText = d.Worker?.ProgressLabel(d),
                deliveryText = d.Worker?.DeliverySummary(d),
                workedHours = d.ticksWorked / Delegation.TicksPerHour,
                stalledHours = d.ticksStalled / Delegation.TicksPerHour,
                eventLines = DelegationUIUtility.EventReportLines(d),
            };
        }

        /// <summary>转成报告窗口能画的数据（与"结束时弹出的那份"同一结构、同一份文本来源）。</summary>
        public DelegationReportData ToReportData()
        {
            DelegationReportData data = new DelegationReportData
            {
                title = Title,
                aborted = aborted,
                eventLines = eventLines,
            };
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("结束理由：" + (reason.NullOrEmpty() ? "—" : reason));
            sb.Append(string.Format("累计作业 {0:0.#} 小时", workedHours));
            if (stalledHours > 0f)
            {
                sb.Append(string.Format(" / 停摆 {0:0.#} 小时", stalledHours));
            }
            if (!progressText.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append("进度：" + progressText);
            }
            if (!deliveryText.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append("产出：" + deliveryText);
            }
            data.summary = sb.ToString();
            return data;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref siteName, "siteName");
            Scribe_Values.Look(ref tile, "tile", -1);
            Scribe_Values.Look(ref startTickAbs, "startTick", 0);
            Scribe_Values.Look(ref endTickAbs, "endTick", 0);
            Scribe_Values.Look(ref aborted, "aborted", false);
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref progressText, "progress");
            Scribe_Values.Look(ref deliveryText, "delivery");
            Scribe_Values.Look(ref workedHours, "workedHours", 0f);
            Scribe_Values.Look(ref stalledHours, "stalledHours", 0f);
            Scribe_Collections.Look(ref eventLines, "eventLines", LookMode.Value);
        }
    }

    /// <summary>
    /// 委派历史（S15 第二期）：**世界级**的已完成/已中断记录，跨委派、跨地点留存。
    ///
    /// 为什么用 `GameComponent` 而不是 `WorldComponent`：
    ///   · `Game.FillComponents()` 用 `typeof(GameComponent).InstantiableDescendantsAndSelf()` **反射自动收集**
    ///     所有子类 ⇒ 本项目**零 Harmony** 的约束下这是唯一省事的挂点；
    ///   · `WorldComponent` 是在 `World..ctor()` 里逐个 `new` 出来的，mod 想挂上去得 patch 构造函数。
    ///
    /// 老存档没有这个组件怎么办：`Get()` 里**懒补建**（找不着就 new 一个塞进 `Current.Game.components`）。
    /// 档案里已有的记录由 `ExposeData` 的 `LookMode.Deep` 自动读写。
    /// </summary>
    public class RimDelegationHistory : GameComponent
    {
        /// <summary>记录上限（FIFO）—— 防存档膨胀。</summary>
        public const int MaxRecords = 60;

        public List<DelegationRecord> records = new List<DelegationRecord>();

        /// <summary>
        /// ⚠️ `GameComponent` 的构造函数是 **protected 无参** —— 原版 `Game.FillComponents()`
        /// 用 `Activator.CreateInstance` 造实例，读档时也从无参构造恢复。
        /// 所以这里**不能**写 `: base(game)`（会 CS1729）；组件自己的 `game` 字段由框架填。
        /// </summary>
        public RimDelegationHistory()
        {
        }

        public static RimDelegationHistory Get(bool createIfMissing = true)
        {
            if (Current.Game == null)
            {
                return null;
            }
            List<GameComponent> comps = Current.Game.components;
            if (comps != null)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    if (comps[i] is RimDelegationHistory found)
                    {
                        return found;
                    }
                }
            }
            if (!createIfMissing)
            {
                return null;
            }
            RimDelegationHistory created = new RimDelegationHistory();
            Current.Game.components.Add(created);
            DelegationUtility.LogVerbose("补建委派历史组件（RimDelegationHistory）");
            return created;
        }

        /// <summary>记一条（最新的排在最前，超出上限丢最旧的）。</summary>
        public void Add(DelegationRecord rec)
        {
            if (rec == null)
            {
                return;
            }
            if (records == null)
            {
                records = new List<DelegationRecord>();
            }
            records.Insert(0, rec);
            while (records.Count > MaxRecords)
            {
                records.RemoveAt(records.Count - 1);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && records == null)
            {
                records = new List<DelegationRecord>();
            }
        }
    }
}
