using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 两条入口的共同落点：
    ///   逻辑 2：选中车队 → 右键地点 → 「委派：…」→ 本 action 挂到 pather 上，抵达时 Arrived()
    ///   逻辑 1：车队已停在地点上 → StartPath(车队当前格, 本 action)
    ///           Caravan_PathFollower.AtDestinationPosition() == (caravan.Tile == destTile)
    ///           → 立刻 PatherArrived() → Arrived() → 开工，不需要第二套代码。
    ///
    /// 计划内容（选人 / 模式 / 结束条件 / 风险姿态）打包在 DelegationRequest 里一起 Scribe，
    /// 所以"预先委派"在存读档中途也安全（Caravan_PathFollower 自带 Scribe_Deep(arrivalAction)）。
    /// </summary>
    public class CaravanArrivalAction_StartDelegation : CaravanArrivalAction
    {
        private Site site;
        private DelegationDef def;
        private DelegationRequest request;

        public CaravanArrivalAction_StartDelegation()
        {
        }

        public CaravanArrivalAction_StartDelegation(Site site, DelegationDef def, DelegationRequest request)
        {
            this.site = site;
            this.def = def;
            this.request = request;
        }

        public override string Label
        {
            get
            {
                string raw = def?.planningLabel;
                if (!raw.NullOrEmpty())
                {
                    return raw;
                }
                return def != null ? def.label + "（委派中）" : "委派";
            }
        }

        public override string ReportString
        {
            get
            {
                string siteLabel = site?.Label ?? "目标地点";
                string raw = def?.planningReportString;
                if (!raw.NullOrEmpty())
                {
                    return raw.Formatted(siteLabel);
                }
                return "正在前往 " + siteLabel + " 执行委派";
            }
        }

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            FloatMenuAcceptanceReport report = base.StillValid(caravan, destinationTile);
            if (!report.Accepted)
            {
                return report;
            }
            if (site == null || site.Destroyed || site.Tile != destinationTile)
            {
                return false;
            }
            return DelegationUtility.CanStart(caravan, site, def);
        }

        public override void Arrived(Caravan caravan)
        {
            if (site == null || site.Destroyed)
            {
                return;
            }
            WorldObjectComp_Delegations comp = site.GetComponent<WorldObjectComp_Delegations>();
            if (comp == null)
            {
                Log.Error("[RimDelegation] 目标地点上没有 WorldObjectComp_Delegations —— Patches/RimDelegation_SiteComps.xml 可能没生效");
                return;
            }

            // 两条路都要在抵达后确认：
            //   · request == null          → 玩家在计划阶段点了「延后决定」
            //   · requireConfirmOnArrival  → 设置里要求抵达后再确认
            // 先把存量掷定，这样对话框显示的是精确规模而不是区间。
            bool needDecision = request == null
                || (RimDelegationMod.Settings != null && RimDelegationMod.Settings.requireConfirmOnArrival);
            if (needDecision)
            {
                comp.EnsureDeposit(def);
                // S18：不再弹模态框 —— 走主控台的「待下达」草稿（Mod 设置里可切回旧对话框）。
                // 玩家把主控台关掉也不会丢：草稿还在左栏，右栏「继续决定」随时能回去。
                DelegationDraft.BeginOrDialog(caravan, site, def,
                    confirmed => Start(caravan, confirmed),
                    preRequest: request);
                return;
            }
            Start(caravan, request);
        }

        private void Start(Caravan caravan, DelegationRequest resolvedRequest)
        {
            WorldObjectComp_Delegations comp = site.GetComponent<WorldObjectComp_Delegations>();
            if (comp == null)
            {
                Log.Error("[RimDelegation] 目标地点上没有 WorldObjectComp_Delegations —— Patches/RimDelegation_SiteComps.xml 可能没生效");
                return;
            }
            comp.StartDelegation(caravan, def, resolvedRequest);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref site, "site");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Deep.Look(ref request, "request");
        }
    }
}
