using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// Ideology 工作站点委派（`WorkSite_Logging` / `_Hunting` / `_Farming` / `_Mining`）：
    /// 不生成地图，**先清场、再搬空营地**。
    ///
    /// ── 为什么能整条复用搜刮 worker ────────────────────────────────────────
    /// `SitePartWorker_WorkSite.Init` 在**建点那一刻**（`Site.AddPart` 的最后一行就是
    /// `part.def.Worker.Init(this, part)`）就把实物掷好、用 `ThingMaker.MakeThing` 塞进
    /// `SitePart.things`，而 `SitePart.ExposeData` 用 `Scribe_Deep.Look(ref things, "things", this)`
    /// 把它存下来 ⇒ 不解图就能读到"现场有什么"。进图时 `GenStep_WorkSiteStash.ScatterAt` 又**优先拿
    /// 同一份** `sitePart.things` 当 `stockpileConcreteContents` ⇒ "委派搬走多少、进图就少多少"，
    /// 与物资藏匿点同构（这也正是它值得做的原因）。
    ///
    /// ── 与搜刮 worker 的三处差别（本类存在的全部理由）─────────────────────
    /// ① **速率单开一档 0.5 趟/作业小时**（用户 2026-09-30 拍板）：营地是开阔场地、储物堆铺在地上，
    ///    没有"找到／破门／清点／打包"那几道工序，而基类那个 0.25 是照 7×7 密室标定的。
    ///    实测：伐木点 603 kg 木头，沿用 0.25 时单人正常模式要 8.6 个作业天。
    /// ② **读不到实物 ≠ 可以现掷**：基类的兜底是"按 `GenStep_ItemStash` 的 maker（找不到就用
    ///    `MapGen_DefaultStockpile`）现掷一份并写回"。工作站根本没有"等进图才掷"这一步
    ///    （实物建点就有）⇒ 读不到就是**真的没有**，走兜底会凭空造出一批物资。
    ///    所以本类**不调**基类的 `RollDeposit`，只把读数抄进存量；存量 0 由宿主的
    ///    「0 存量闸门」拒绝开工（§1.2），而不是让委派 0/0 空转。
    /// ③ **文案口径**：营地／清剿，不是"藏匿点"。
    ///
    /// ── 清场那一段 ────────────────────────────────────────────────────────
    /// 威胁不在这里算：工作站的**主件自己**带威胁点数（`minThreatPoints` 350），走
    /// `ThreatRosterFactory`，守军是原版 `GenStep_WorkSitePawns` 的**两批**
    /// （劳工半额 + 战斗员半额，同一 group-maker 种子）—— 见 `GenerateWorkSiteGuards`。
    /// 流程里的「交战」段负责把它结算掉（与矿点/营救同一套）。
    /// </summary>
    public class DelegationWorker_WorkSite : DelegationWorker_TakeItemStash
    {
        /// <summary>
        /// 开阔营地档的"每人每作业小时趟数"（用户 2026-09-30 拍板 0.5 = 2 小时一趟）。
        ///
        /// 量纲：`MassUtility.Capacity(人) = BodySize × 35 = 35 kg/趟` ⇒ 单人 **17.5 kg/作业小时**、
        /// 正常模式 8h/天 ⇒ **140 kg/作业天**（基类那档是 70）。4 人的伐木点（603 kg）≈ 1.1 个作业天。
        /// 想调手感只改这一个常量，量纲不变。
        /// </summary>
        public const float CampHaulTripsPerWorkHour = 0.5f;

        public override float TripsPerWorkHour => CampHaulTripsPerWorkHour;

        /// <summary>营地里的活仍然叫"搜刮"（搬的是营地的仓储），量纲仍是"件"。</summary>
        public override string ActivityName => "搜刮";

        /// <summary>
        /// 存量登记：**只读现场，不掷骰、不兜底**（理由见类注释 ②）。
        /// </summary>
        public override void RollDeposit(DelegationDeposit dep, Site site)
        {
            if (dep == null)
            {
                return;
            }
            ThingOwner owner = LootOwner(site, def);
            if (owner == null)
            {
                // 这不是错误，是"这里确实没东西了"：营地的仓储在建点时就定好了，
                // 没有第二条"进图才生成"的来源，所以不补掷。
                DelegationUtility.LogVerbose(
                    "[RimDelegation] 工作站现场读不到实物（按空登记，不现掷）：" + site?.Label);
            }
            dep.resourceDef = HighestValueThing(site, def)?.def;
            dep.totalUnits = CountUsable(owner);
            dep.rolledUnits = TotalMass(owner);
            dep.yieldPerUnit = 1;
        }

        /// <summary>
        /// 「预期获得」的三态文案（营地口径）。
        ///
        /// 与搜刮点的差别只有一点：营地**不存在**"清单还没掷"这一态（建点就掷好了），
        /// 所以中间那一态在营地上只会出现在"存档来自更早的版本"这种边角情况，文案按实际情况写。
        /// </summary>
        public override string PreviewLabel(Site site, DelegationPreview preview, DelegationDeposit exactDeposit)
        {
            ThingOwner owner = LootOwner(site, def);
            if (owner != null && owner.Any)
            {
                string s = string.Format("营地内 {0} 件物资 · 合计 {1:0.#} kg · 市价约 {2:0} 银（精确值）",
                    CountUsable(owner), TotalMass(owner), TotalValue(owner));
                if (exactDeposit != null && exactDeposit.HasBeenWorked)
                {
                    s += string.Format("\n此营地已被委派 {0} 次，累计搬走 {1} 件（存量跨委派累计，不会重掷）",
                        exactDeposit.timesDelegated, exactDeposit.unitsDelivered);
                }
                return s;
            }

            return exactDeposit != null
                ? "营地内已无可搬运的物资：这一处的仓储已经被搬空了。"
                : "营地内没有可搬运的物资。";
        }

        /// <summary>算不出速率时给玩家的**真实**原因（营地口径）。</summary>
        public override string EstimateUnavailableReason(Site site, DelegationPreview preview, DelegationDeposit deposit)
        {
            if (deposit == null && LootOwner(site, def) == null)
            {
                return "未知：营地内的物资需待队伍实地清点。";
            }
            return null;
        }

        /// <summary>
        /// 把"这门活的已知不精确之处"摊给玩家看（准则②：不可避免的近似必须同时写在
        /// 代码注释与玩家可见处）。
        /// </summary>
        public override string InspectWarning(Delegation d)
        {
            return "这里的守军与仓储是按既有的营地记录推演的（不进入地图）；" +
                   "委派动过之后，这一处不再接受常规进入。";
        }
    }
}
