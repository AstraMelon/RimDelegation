using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 地点威胁 → 敌方编队快照。
    ///
    /// **用的是 vanilla 自己的确定性生成路径**（DESIGN.md §19.7）：
    /// 站点生成时就已经把 <c>SitePartParams.randomValue</c> 与 <c>threatPoints</c> 掷好存进存档，
    /// 进图时 <c>GenStep_SitePawns</c> 只是拿同一个种子去生成。
    /// 所以我们在这里用**同一个种子**调 <c>PawnGroupMakerUtility.GeneratePawns</c>，
    /// 得到的编制与真实入侵逐人一致 —— 不需要地图。
    ///
    /// **生成完立刻折算成快照并销毁 pawn**，因此不存在"未 spawn 的 pawn 被写进存档"的风险
    /// （§19.14 第 4 项）。
    /// </summary>
    public static class ThreatRosterFactory
    {
        public sealed class Result
        {
            public readonly List<CombatUnitSnapshot> Enemies = new List<CombatUnitSnapshot>();
            public readonly List<string> Notes = new List<string>();
            /// <summary>无法抽象处理的威胁（炮塔/机械集群/伏击）—— 按 §18.3 应当进图。</summary>
            public readonly List<string> Unresolved = new List<string>();
            public float TotalThreatPoints;

            /// <summary>
            /// S30：这些敌人**身上有什么**（缴获，见 <see cref="DelegationLootItem" />）。
            ///
            /// 必须在这里抄：pawn 抄完数值立刻 `Destroy()`，晚了就什么都没了。
            /// ⚠️ 抄的是**全部**敌人的装备，谁被打掉由战斗结果决定（搜集战利品时才筛）。
            /// index 用的是"成功进编队的第几个敌人"，与 `CombatScene` 的敌方顺序一致。
            /// </summary>
            public readonly List<DelegationLootItem> EnemyLoot = new List<DelegationLootItem>();

            /// <summary>
            /// S31：**保留下来的真 pawn**（下标 = `Enemies` 的下标；炮塔这类没有 pawn 的位是 null）。
            ///
            /// 只有"真结算"（交战段 / 营救清场）才要它：尸体要宰、倒地的要收押，都离不开真 pawn；
            /// UI 的预告/评估一律不留（`keepPawns = false`），照旧生成完就销毁。
            /// 调用方**必须**在 finally 里把没被收编的那些销毁掉（见 `DiscardPawn`）。
            /// </summary>
            public readonly List<Pawn> EnemyPawns = new List<Pawn>();

            /// <summary>S31：这一趟要不要把真 pawn 留下来（真结算 = true；UI 预告 = false）。</summary>
            public bool keepPawns;

            /// <summary>把一个 pawn 放到指定敌人下标上（缺位补 null，保证与 `Enemies` 对齐）。</summary>
            internal void SetPawnAt(int enemyIndex, Pawn p)
            {
                while (EnemyPawns.Count <= enemyIndex)
                {
                    EnemyPawns.Add(null);
                }
                EnemyPawns[enemyIndex] = p;
            }
        }

        /// <summary>
        /// 一个威胁部件该怎么处理。
        ///
        /// **这是唯一的事实来源**：<see cref="Build"/> 的生成分支与 UI 的"有几个无法抽象"
        /// （`ThreatAssessmentEntry.UnassessablePartCount`）都走 <see cref="SupportOf"/>，
        /// 免得摘要说"可以评估"、点进去却是"需进图"。
        /// </summary>
        public enum RosterSupport
        {
            /// <summary>可抽象：有确定性的无地图生成路径。</summary>
            Abstractable,
            /// <summary>§18.3 分流表明确判为 ForbidAbstract（固定火力 / 布局 / 触发时机无法抽象）。</summary>
            ForbidAbstract,
            /// <summary>还没有实现无地图生成路径。</summary>
            Unsupported,
        }

        public static RosterSupport SupportOf(SitePartDef def)
        {
            switch (def?.defName)
            {
                case "Outpost":
                case "SleepingMechanoids":
                case "Manhunters":
                // ★ 炮塔从 ForbidAbstract 改成可抽象：编制（turretsCount / mortarsCount）在任务生成时
                //   就算好并存进 SitePartParams，是**确定性整数**；无法抽象的"布局/触发时机"
                //   在本模型里由 HasTerrainAdvantage 与 EnemyOutputFactor 吸收。
                //   这一改同时解锁：囚犯营救（守卫 100% 是炮塔）、物资点 1/7、难员 1/7、矿点 1/7。
                case "Turrets":
                    return RosterSupport.Abstractable;

                // ★ 2026-09-30（用户拍板「工作站＝乙档：清场 + 搬运」）：
                //   Ideology 工作站点。它与上面那些的**结构不同** —— 一个部件里塞了**两批**人
                //   （劳工半额 + 战斗员半额，同一 group-maker 种子），所以生成走 GenerateWorkSiteGuards。
                //   在加这四行之前，它们是 `Unsupported` ⇒ CanAssess = false ⇒ 一进交战段就 Abort
                //   （文案「此地存在无法无地图评估的守军」）。判据见 ThreatAssessmentEntry.IsThreatPart：
                //   工作站的**主件自己**就有威胁点数（minThreatPoints 350）。
                case "WorkSite_Logging":
                case "WorkSite_Hunting":
                case "WorkSite_Farming":
                case "WorkSite_Mining":
                    return RosterSupport.Abstractable;

                case "MechCluster":
                case "AmbushEdge":
                case "AmbushHidden":
                    return RosterSupport.ForbidAbstract;

                default:
                    return RosterSupport.Unsupported;
            }
        }

        /// <summary>有威胁点的 SitePart 才算威胁部件（PreciousLump / ItemStash 主件的 wantsThreatPoints 是 false）。</summary>
        private static bool IsThreatPart(SitePart part)
        {
            // 与 UI 侧共用同一个判据（ThreatAssessmentEntry 在 RimDelegation 命名空间）
            return ThreatAssessmentEntry.IsThreatPart(part);
        }

        public static Result Build(Site site, bool keepPawns = false)
        {
            Result r = new Result();
            r.keepPawns = keepPawns;
            if (site == null) return r;

            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (!IsThreatPart(part)) continue;

                r.TotalThreatPoints += part.parms.threatPoints;
                string defName = part.def.defName;

                switch (SupportOf(part.def))
                {
                    // §18.3 分流表：固定火力 / 布局 / 触发时机无法抽象 —— 只能进图
                    case RosterSupport.ForbidAbstract:
                        r.Unresolved.Add(part.def.LabelCap + "（" + defName + "）");
                        break;

                    case RosterSupport.Unsupported:
                        r.Unresolved.Add(part.def.LabelCap + "（" + defName + "，尚未支持）");
                        break;

                    default:
                        switch (defName)
                        {
                            case "Outpost":
                                GeneratePawnGroup(r, site, part, PawnGroupKindDefOf.Settlement,
                                                  site.Faction, inhabitants: true,
                                                  seed: OutpostSitePartUtility.GetPawnGroupMakerSeed(part.parms),
                                                  label: "哨所守卫");
                                break;

                            case "SleepingMechanoids":
                                GeneratePawnGroup(r, site, part, PawnGroupKindDefOf.Combat,
                                                  Faction.OfMechanoids, inhabitants: false,
                                                  seed: SleepingMechanoidsSitePartUtility.GetPawnGroupMakerSeed(part.parms),
                                                  label: "休眠机械族");
                                break;

                            case "Turrets":
                                GenerateTurrets(r, site, part);
                                break;

                            // ★ 2026-09-30：工作站点（两批守军）
                            case "WorkSite_Logging":
                            case "WorkSite_Hunting":
                            case "WorkSite_Farming":
                            case "WorkSite_Mining":
                                GenerateWorkSiteGuards(r, site, part);
                                break;

                            default:
                                // SupportOf 说它可抽象，而这里只有 Manhunters 这一条路可走
                                GenerateManhunters(r, part);
                                break;
                        }
                        break;
                }
            }

            return r;
        }

        /// <summary>
        /// 用原版种子生成**一批**守军，并折算成快照。
        ///
        /// <paramref name="points" /> &lt; 0 ⇒ 用该部件的威胁点数（旧的单批行为）；
        /// 工作站点要"劳工半额 + 战斗员半额"两批，所以必须能覆盖点数。
        /// <paramref name="fightersOnly" /> ⇒ 原版 `PawnGroupMakerParms.generateFightersOnly`
        /// （反编译 `GenStep_WorkSitePawns.GroupMakerParmsFighters` 的同一格）。
        /// </summary>
        private static void GeneratePawnGroup(Result r, Site site, SitePart part, PawnGroupKindDef groupKind,
                                              Faction faction, bool inhabitants, int seed, string label,
                                              float points = -1f, bool fightersOnly = false)
        {
            if (faction == null)
            {
                r.Unresolved.Add(label + "：没有可用派系");
                return;
            }

            float pointsUsed = points < 0f ? part.parms.threatPoints : points;
            PawnGroupMakerParms parms = new PawnGroupMakerParms
            {
                tile = site.Tile,
                faction = faction,
                groupKind = groupKind,
                points = pointsUsed,
                inhabitants = inhabitants,
                seed = seed,
                generateFightersOnly = fightersOnly,
            };

            int count = 0;
            List<Pawn> generated = new List<Pawn>();
            // ★ S24：**自己再包一层种子**。
            //   反编译确认：vanilla 只在"挑 group maker"那一小段 push/pop 了种子
            //   （`PawnGroupMakerUtility.TryGetRandomPawnGroupMaker` 里 `Rand.PushState(parms.seed)` /
            //   `Rand.PopState`），而**真正生成 pawn 的那一段没有**。
            //   两个后果都得防：① 不包的话"同种子逐人一致"只是碰巧；② 从 UI 里调用它
            //   （威胁评估面板、主列的作战任务段）会吃掉世界随机数流。
            //   嵌套 push/pop 是安全的：vanilla 内部的 push/pop 依然成对。
            Rand.PushState(seed);
            try
            {
                foreach (Pawn p in PawnGroupMakerUtility.GeneratePawns(parms, true))
                {
                    if (p == null) continue;
                    generated.Add(p);
                }
            }
            catch (Exception ex)
            {
                r.Notes.Add(label + "：生成失败（" + ex.GetType().Name + "）");
            }
            finally
            {
                Rand.PopState();
            }

            for (int i = 0; i < generated.Count; i++)
            {
                Pawn p = generated[i];
                string problem;
                CombatUnitSnapshot snap = CombatSnapshotFactory.FromPawn(p, false, out problem);
                if (snap != null)
                {
                    // 守卫有工事 ⇒ 地形优势；具体由调用方按战场态势再覆盖
                    snap.HasTerrainAdvantage = true;
                    // ★ 2026-09-30 修：下标必须取**全局**的 `r.Enemies.Count`，不能用从 0 起的局部计数器。
                    //   旧写法的两个后果（审计 §12-N 已记录其中一条）：
                    //     ① 同一地点有**第二个**威胁部件时会覆盖 `EnemyPawns[0..n]`，
                    //        被覆盖的真 pawn 因 `keepPawns = true` 跳过 Discard ⇒ 永久泄漏；
                    //     ② 缴获的 `enemyIndex` 同样串台 ⇒ 战利品被记到错误的敌人头上。
                    //   工作站的"劳工 + 守卫"两批走同一条路，不加这一改第二批就会吃掉第一批。
                    int enemyIndex = r.Enemies.Count;
                    r.Enemies.Add(snap);
                    CaptureLoot(r, p, enemyIndex);   // S30：抄装备（马上要 Destroy 了）
                    if (r.keepPawns)
                    {
                        r.SetPawnAt(enemyIndex, p);  // S31：真结算才留人（尸体要宰、倒地的要收押）
                    }
                    count++;
                }
                else if (problem != null)
                {
                    r.Notes.Add(problem);
                }
                if (!r.keepPawns)
                {
                    Discard(p);   // S31：真结算时留着（战后要宰/要收押），由调用方负责销毁
                }
            }

            r.Notes.Add(label + "：威胁点数 " + pointsUsed.ToString("0") +
                        "（种子 " + seed + "）→ " + count + " 个可评估单位");
        }

        /// <summary>
        /// 工作站点（Ideology `WorkSite_*`）的**两批**守军 —— 照抄原版 `GenStep_WorkSitePawns`
        /// （2026-09-30 反编译，MVID 61e41735…）：
        ///
        ///   ① **劳工**：`groupKind = ((SitePartWorker_WorkSite)part.def.Worker).WorkerGroupKind`，
        ///      点数 = 威胁点数 ÷ 2，`inhabitants = true`；
        ///   ② **战斗员**：`groupKind = Combat`（该派系没配 Combat 编制就退回 `Settlement`），
        ///      点数同上，`generateFightersOnly = true`。
        ///
        /// 两批**共用同一个** `OutpostSitePartUtility.GetPawnGroupMakerSeed(parms)` —— 原版就是这么写的，
        /// 换成别的种子就复现不出同一个营地（"同种子逐人一致"这条承诺会当场失效）。
        /// 点数下限同样照抄：`Mathf.Max(半额, 该派系生成这种编制的最低点数)`。
        /// </summary>
        private static void GenerateWorkSiteGuards(Result r, Site site, SitePart part)
        {
            SitePartWorker_WorkSite worker = part?.def?.Worker as SitePartWorker_WorkSite;
            if (worker == null)
            {
                // 注意：`LabelCap` 是 TaggedString，不能直接与 string 相加（CS0034），先转成 string。
                string workLabel = part?.def != null ? part.def.LabelCap.ToString() : "工作站点";
                r.Unresolved.Add(workLabel + "：workerClass 不是 SitePartWorker_WorkSite，" +
                                 "拿不到它用的编制种类（WorkerGroupKind），无法复现守军");
                return;
            }

            Faction faction = site.Faction;
            if (faction == null)
            {
                r.Unresolved.Add(part.def.LabelCap + "：没有可用派系");
                return;
            }

            int seed = OutpostSitePartUtility.GetPawnGroupMakerSeed(part.parms);
            float half = Mathf.Max(1f, part.parms.threatPoints / 2f);

            PawnGroupKindDef workerKind = worker.WorkerGroupKind;
            if (workerKind == null)
            {
                r.Unresolved.Add(part.def.LabelCap + "：WorkerGroupKind 为空，复现不出劳工那批");
                return;
            }

            float workerPoints = Mathf.Max(half, faction.def.MinPointsToGeneratePawnGroup(
                workerKind, new PawnGroupMakerParms
                {
                    tile = site.Tile, faction = faction, groupKind = workerKind,
                    inhabitants = true, seed = seed,
                }));
            GeneratePawnGroup(r, site, part, workerKind, faction, inhabitants: true, seed: seed,
                              label: part.def.LabelCap + "·劳工", points: workerPoints);

            PawnGroupKindDef fightKind = PawnGroupKindDefOf.Combat;
            if (!HasGroupMaker(faction, fightKind))
            {
                fightKind = PawnGroupKindDefOf.Settlement;
            }
            float fightPoints = Mathf.Max(half, faction.def.MinPointsToGeneratePawnGroup(
                fightKind, new PawnGroupMakerParms
                {
                    tile = site.Tile, faction = faction, groupKind = fightKind,
                    inhabitants = true, seed = seed,
                }));
            GeneratePawnGroup(r, site, part, fightKind, faction, inhabitants: true, seed: seed,
                              label: part.def.LabelCap + "·守卫", points: fightPoints, fightersOnly: true);
        }

        /// <summary>这个派系配了这种编制吗（原版 `faction.def.pawnGroupMakers.Any(m =&gt; m.kindDef == kind)`）。</summary>
        private static bool HasGroupMaker(Faction faction, PawnGroupKindDef kind)
        {
            if (faction?.def?.pawnGroupMakers == null || kind == null)
            {
                return false;
            }
            for (int i = 0; i < faction.def.pawnGroupMakers.Count; i++)
            {
                if (faction.def.pawnGroupMakers[i]?.kindDef == kind)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 炮塔守军。
        ///
        /// 数量来自任务的确定性掷骰（`SitePartWorker_Turrets.GenerateDefaultParams`）：
        ///     turretsCount = Clamp(round(威胁点数 / Turret_MiniTurret.building.combatPower), 2, 11)
        ///     mortarsCount = Rand.RangeInclusive(0, 1)
        /// 老存档若没写 turretsCount，就按**同一个公式**从 threatPoints 反推，保证与进图一致。
        ///
        /// **RIM-23(1A)**：这一件在原版进图时还恒定附带 **1 名边缘守卫**，所以本方法先补那一人
        /// （见 <see cref="GenerateTurretGuard" />）再产炮塔 —— 顺序也照抄原版（守卫先于炮塔）。
        /// </summary>
        private static void GenerateTurrets(Result r, Site site, SitePart part)
        {
            ThingDef turretDef = CombatSnapshotFactory.TurretDefOfChoice;
            if (turretDef?.building == null)
            {
                r.Unresolved.Add("炮塔：找不到 " + (turretDef?.defName ?? "Turret_MiniTurret"));
                return;
            }

            // ★ RIM-23(1A)：原版 `GenStep_Turrets.guardsCountRange = new IntRange(1, 1)`
            //   （另有 `DefaultGuardsCount = 1`）⇒ 恒定 1 名，且与 turretsCount 无关。
            //   用户拍板 1A：补上它（会让现有 4 条可能出炮塔的委派手感变化，这是预期内的）。
            GenerateTurretGuard(r, site, part);

            int combatPower = Mathf.Max(1, Mathf.RoundToInt(turretDef.building.combatPower));
            int count = part.parms.turretsCount > 0
                ? part.parms.turretsCount
                : Mathf.Clamp(Mathf.RoundToInt(part.parms.threatPoints / combatPower), 2, 11);

            if (count <= 0)
            {
                r.Unresolved.Add("炮塔：数量为 0");
                return;
            }

            ThingDef gunDef = turretDef.building.turretGunDef;
            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                string problem;
                // 同名 ⇒ 预告面板的编队列表会把它们合并成一行"迷你炮塔 ×N"
                CombatUnitSnapshot snap = CombatSnapshotFactory.FromTurret(
                    turretDef, gunDef, turretDef.LabelCap.ToString(), out problem);
                if (snap != null)
                {
                    snap.HasTerrainAdvantage = true;
                    r.Enemies.Add(snap);
                    ok++;
                }
                else if (problem != null)
                {
                    r.Notes.Add(problem);
                    break;
                }
            }

            r.Notes.Add("炮塔：" + count + " 座" + turretDef.LabelCap +
                        "（turretsCount = " + part.parms.turretsCount + "，威胁点数 " +
                        part.parms.threatPoints.ToString("0") + " / 战力 " + combatPower +
                        "）→ " + ok + " 个可评估单位");

            if (part.parms.mortarsCount > 0)
            {
                r.Notes.Add("迫击炮 ×" + part.parms.mortarsCount +
                            "：最小射程 29.9 格 > 本模型的交战距离，不产生输出（已忽略，见 DESIGN §18.3）");
            }
        }

        /// <summary>
        /// RIM-23(1A)：`Turrets` 威胁件在原版进图时恒定附带的 **1 名「边缘守卫」**。
        ///
        /// 全流程照抄原版（反编译 rw16b / MVID `61e41735…`）：
        ///   · `GenStep_Turrets.Generate` 把 `guardsCountRange.RandomInRange`（= **1**）传成
        ///     `edgeDefenseGuardsCount`；
        ///   · `SymbolResolver_EdgeDefense.Resolve` 对每名守卫构造
        ///     `new PawnGenerationRequest(faction.RandomPawnKind(), faction, NonPlayer, map.Tile,
        ///       forceGenerateNewPawn: false, allowDead: false, allowDowned: false,
        ///       canGeneratePawnRelations: true, mustBeCapableOfViolence: true)`；
        ///   · `Faction.RandomPawnKind()` = 该派系 `pawnGroupMakers` 里所有 **humanlike** 项 `RandomElement()`
        ///     （一项都没有时退回 `def.basicMemberKind`）。
        ///
        /// 原版这条路径**没有** `Rand.PushState`。若照字面"无种子"地生成，会同时踩两个坑：
        ///   ① 每打开一次威胁评估面板/主列就重掷一次种类 ⇒ **评估与结算不是同一份编队**，
        ///      恰好违反本议题自己的「五、验收标准」第 2 条；
        ///   ② 威胁摘要缓存每帧重建，会**吃掉世界随机数流**（与 RIM-25 修掉的那个坑同类）。
        /// 所以这里采用与 RIM-25(1A/2A) **完全同款**的输入同源做法：种子取 `part.parms.randomValue`
        /// （原版建点那一刻 `Rand.Int` 掷定、随存档）；兵种仍由 `faction.RandomPawnKind()` 决定
        /// —— 这是对拍板 1A "无种子"字面的一处**有意偏离**，已在交付评论里显式标注待你确认。
        /// 2B：不在 `InspectWarning` 里写明"这是推演值"。
        /// </summary>
        private static void GenerateTurretGuard(Result r, Site site, SitePart part)
        {
            Faction faction = site?.Faction;
            if (faction == null)
            {
                r.Notes.Add("炮塔·边缘守卫：没有可用派系，未生成（与原版同为 0 人）");
                return;
            }

            int seed = part.parms.randomValue;
            Pawn p = null;
            Rand.PushState(seed);
            try
            {
                PawnGenerationRequest req = new PawnGenerationRequest(
                    faction.RandomPawnKind(), faction, PawnGenerationContext.NonPlayer, site.Tile,
                    forceGenerateNewPawn: false, allowDead: false, allowDowned: false,
                    canGeneratePawnRelations: true, mustBeCapableOfViolence: true);
                p = PawnGenerator.GeneratePawn(req);
            }
            catch (Exception ex)
            {
                r.Notes.Add("炮塔·边缘守卫：生成失败（" + ex.GetType().Name + "）");
                return;
            }
            finally
            {
                Rand.PopState();
            }

            if (p == null)
            {
                r.Notes.Add("炮塔·边缘守卫：生成为空");
                return;
            }

            string problem;
            CombatUnitSnapshot snap = CombatSnapshotFactory.FromPawn(p, false, out problem);
            if (snap == null)
            {
                if (problem != null) r.Notes.Add(problem);
                if (!r.keepPawns) Discard(p);
                return;
            }

            snap.HasTerrainAdvantage = true;    // 与炮塔同为工事里的守军
            int enemyIndex = r.Enemies.Count;   // 与 GeneratePawnGroup 同款：下标取全局计数，不用局部计数
            r.Enemies.Add(snap);
            CaptureLoot(r, p, enemyIndex);
            if (r.keepPawns)
            {
                r.SetPawnAt(enemyIndex, p);
            }
            else
            {
                Discard(p);
            }

            r.Notes.Add("炮塔·边缘守卫：1 名 " + p.kindDef.LabelCap +
                        "（原版 guardsCountRange = (1,1)；本 mod 按输入同源用种子 " + seed + "）");
        }

        private static void GenerateManhunters(Result r, SitePart part)
        {
            PawnKindDef kind = part.parms.animalKind;
            if (kind == null)
            {
                r.Unresolved.Add("猎杀人类：站点未记录动物种类");
                return;
            }

            int n = AggressiveAnimalIncidentUtility.GetAnimalsCount(kind, part.parms.threatPoints);
            int count = 0;

            // ★ RIM-25(1A/2A)：这是**唯一**没有隔离随机数流的生成路径。
            //   `PawnGenerator.GeneratePawn` 内部处处用 `Rand`（年龄 / 性别 / 健康 / 装备），
            //   不包种子的两个后果（与 `GeneratePawnGroup` 里那段注释同源）：
            //     ① 每打开一次威胁评估面板 / 主列就重掷一份名册 ⇒ 面板算的名册 ≠ 结算用的名册，
            //        违背"预告即契约"（§19.12）的输入同源前提；
            //     ② 从 UI（每帧缓存重建、对话框）调用会**吃掉世界随机数流**，之后的世界事件序列被静默推进。
            //   种子取 `part.parms.randomValue`（拍板 2A）：反编译确认原版
            //   `SitePartWorker.GenerateDefaultParams` 就是 `randomValue = Rand.Int`，
            //   并由 `SitePartParams.ExposeData` 的 `Scribe_Values.Look(ref randomValue, "randomValue", 0)`
            //   随存档 —— 建点那一刻掷定，是"与原版同源"的那颗种子（`site.ID` 虽稳定但与建点掷骰无关）。
            //   3B：不在 `InspectWarning` 里写"野兽编队为推演值"。
            int seed = part.parms.randomValue;
            Rand.PushState(seed);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    Pawn p = null;
                    try { p = PawnGenerator.GeneratePawn(kind, null); }
                    catch (Exception ex) { r.Notes.Add("猎杀人类：生成失败（" + ex.GetType().Name + "）"); break; }

                    if (p == null) continue;
                    string problem;
                    CombatUnitSnapshot snap = CombatSnapshotFactory.FromPawn(p, false, out problem);
                    if (snap != null)
                    {
                        snap.HasTerrainAdvantage = false;
                        // ★ 下标必须取**全局**的 `r.Enemies.Count`，不能用从 0 起的局部计数器：
                        //   同一地点可能有第二个威胁部件 ⇒ 否则缴获会记到错误的敌人头上，
                        //   被覆盖的那只真 pawn 还会逃过收尾销毁（永久泄漏）。
                        int enemyIndex = r.Enemies.Count;
                        r.Enemies.Add(snap);
                        CaptureLoot(r, p, enemyIndex);
                        if (r.keepPawns)
                        {
                            r.SetPawnAt(enemyIndex, p);
                        }
                        count++;
                    }
                    if (!r.keepPawns)
                    {
                        Discard(p);
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }

            r.Notes.Add("猎杀人类：" + kind.LabelCap + " × " + count +
                        "（威胁点数 " + part.parms.threatPoints.ToString("0") + "，种子 " + seed + "）");
        }

        /// <summary>
        /// S30：把一名敌人**身上带的东西**抄成缴获条目（用户口径：「作战任务的主要是缴获敌人装备」）。
        ///
        /// 抄三处：手里的（`equipment`）、穿着的（`apparel`）、背包里的（`inventory`）。
        /// 品质要抄 —— 缴获回来的枪是"良好"还是"极佳"就是它的价值所在；
        /// 耐久**刻意不抄**（近似，见 <see cref="DelegationLootItem" /> 的类注释）。
        /// 同样的东西**可堆叠时合并**（弹药/食物），不可堆叠时各自一条（枪、甲）。
        /// </summary>
        private static void CaptureLoot(Result r, Pawn p, int enemyIndex)
        {
            if (r == null || p == null)
            {
                return;
            }
            try
            {
                if (p.equipment?.AllEquipmentListForReading != null)
                {
                    CaptureList(r, p.equipment.AllEquipmentListForReading, enemyIndex);
                }
                if (p.apparel?.WornApparel != null)
                {
                    CaptureList(r, p.apparel.WornApparel, enemyIndex);
                }
                if (p.inventory?.innerContainer != null)
                {
                    CaptureList(r, p.inventory.innerContainer.InnerListForReading, enemyIndex);
                }
            }
            catch (Exception ex)
            {
                // 抄不到就少一份战利品，绝不能因此把一次委派结算打断
                r.Notes.Add("缴获清点失败：" + ex.GetType().Name);
            }
        }

        private static void CaptureList(Result r, System.Collections.IEnumerable things, int enemyIndex)
        {
            foreach (object o in things)
            {
                Thing t = o as Thing;
                if (t?.def == null || t.stackCount <= 0)
                {
                    continue;
                }
                // 尸体/任务物品之类不该当战利品（它们不是"装备"）
                if (t.def.IsCorpse || t.def.destroyOnDrop)
                {
                    continue;
                }
                CompQuality cq = (t as ThingWithComps)?.TryGetComp<CompQuality>();
                int quality = cq != null ? (int)cq.Quality : -1;
                int count = Mathf.Max(1, t.stackCount);

                // RIM-32：**生物编码的原主名**与**武器特性**也要抄下来 —— 实物化时这两样都会丢，
                // 而 `ThingMaker.MakeThing` 对独特武器还会自己随机掷一套特性（反编译
                // `CompUniqueWeapon.PostPostMake → InitializeTraits`）⇒ 不抄就等于"缴获回来的
                // 不是同一件东西，而是另一件随机独特武器"。还原见
                // `DelegationUtility.ApplyBiocode` / `ApplyWeaponTraits`。
                string codedLabel = CaptureCodedLabel(t);
                List<string> traits = CaptureWeaponTraits(t);

                // 可堆叠的合并成一条，不可堆叠的各算一条（一条 = 一件实物）
                // （带编码 / 带特性的东西一律不参与合并 —— 它们本来就不可堆叠）
                if (t.def.stackLimit > 1 && codedLabel.NullOrEmpty() && traits.NullOrEmpty())
                {
                    DelegationLootItem same = null;
                    for (int j = 0; j < r.EnemyLoot.Count; j++)
                    {
                        DelegationLootItem e = r.EnemyLoot[j];
                        if (e != null && e.enemyIndex == enemyIndex && e.def == t.def
                            && e.stuff == t.Stuff && e.quality == quality)
                        {
                            same = e;
                            break;
                        }
                    }
                    if (same != null)
                    {
                        same.count += count;
                        continue;
                    }
                }
                r.EnemyLoot.Add(new DelegationLootItem
                {
                    def = t.def,
                    stuff = t.Stuff,
                    count = t.def.stackLimit > 1 ? count : 1,
                    quality = quality,
                    enemyIndex = enemyIndex,
                    codedPawnLabel = codedLabel,
                    weaponTraits = traits,
                });
                if (t.def.stackLimit <= 1 && count > 1)
                {
                    // 不可堆叠却真的带了多件（罕见）：按件数各补一条
                    for (int k = 1; k < count; k++)
                    {
                        r.EnemyLoot.Add(new DelegationLootItem
                        {
                            def = t.def,
                            stuff = t.Stuff,
                            count = 1,
                            quality = quality,
                            enemyIndex = enemyIndex,
                            codedPawnLabel = codedLabel,
                            weaponTraits = traits,
                        });
                    }
                }
            }
        }

        /// <summary>RIM-32：抄下这件东西的"生物编码原主名"（没被编码 = null）。</summary>
        private static string CaptureCodedLabel(Thing t)
        {
            try
            {
                CompBiocodable bc = (t as ThingWithComps)?.TryGetComp<CompBiocodable>();
                return bc != null && bc.Biocoded ? bc.CodedPawnLabel : null;
            }
            catch (Exception)
            {
                return null;   // 抄不到就少一份忠实度，绝不能因此打断一次委派结算
            }
        }

        /// <summary>RIM-32：抄下这件东西的武器特性 defName 表（没有 = null）。</summary>
        private static List<string> CaptureWeaponTraits(Thing t)
        {
            try
            {
                CompUniqueWeapon uw = (t as ThingWithComps)?.TryGetComp<CompUniqueWeapon>();
                if (uw == null || uw.TraitsListForReading.NullOrEmpty())
                {
                    return null;
                }
                List<string> list = new List<string>();
                for (int i = 0; i < uw.TraitsListForReading.Count; i++)
                {
                    WeaponTraitDef d = uw.TraitsListForReading[i];
                    if (d != null)
                    {
                        list.Add(d.defName);
                    }
                }
                return list.Count > 0 ? list : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 销毁一个"只为取数值/只为结算"而生成的临时 pawn（S31 起对外公开：
        /// 真结算保留的那批人由调用方在 finally 里收尾，见 `CombatSetup.EnemyPawns`）。
        ///
        /// ⚠️ **不要** 调 `Find.WorldPawns.RemovePawn(p)`（S26 修）：这些 pawn 由
        /// `GeneratePawns` 造出来但从没 spawn、也从未登记进 `WorldPawns`，
        /// 而 vanilla 的 `RemovePawn` 在找不到它时会**逐次 `Log.Error`**
        /// 「Tried to remove pawn … but it's not here.」—— 用户 S26 的调试日志就是这么被刷屏的
        /// （它记的是 error 而不是抛异常，所以 try/catch 挡不住）。真正的清理只需要 Destroy。
        /// </summary>
        public static void DiscardPawn(Pawn p)
        {
            Discard(p);
        }

        private static void Discard(Pawn p)
        {
            if (p == null) return;
            try
            {
                if (!p.Destroyed) p.Destroy();
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 清理生成的临时 pawn 失败：" + ex.Message, 0x5E0C1);
            }
        }
    }
}
