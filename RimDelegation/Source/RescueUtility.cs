using System;
using System.Collections.Generic;
using System.Text;
using RimDelegation.Combat;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「救人」类委派（难员 / 囚犯）的共用引擎：找目标、清场、抬人、入伙。
    ///
    /// ── 为什么能"不进图" ─────────────────────────────────────────────────
    /// 两类任务的目标 Pawn 都在 **`SitePart.things[0]`** 里，而 `SitePart.ExposeData` 用
    /// `Scribe_Deep` 存它 ⇒ 不解地图就能读到，也能存读档。地图生成时
    /// `GenStep_DownedRefugee` / `GenStep_PrisonerWillingToJoin` 的取值优先级同样是
    /// `part.things` → `ImportantPawnComp.pawn` → 现生成，与本类取的是**同一份**。
    ///
    /// ── 原版"救下即加入"的实际路径（反编译确认）────────────────────────────
    /// 不是那个看起来像奖励的 `QuestNode_AddPawnReward`：它只把 `Reward_Pawn` 塞进
    /// `QuestPart_Choice`，而 `Reward_Pawn` **没有覆写** `Notify_Used`（基类只置
    /// `usedOrCleanedUp = true`）⇒ `site.MapGenerated` 信号其实**什么都不做**。
    ///
    /// 真正的加入路径是 **`JobDriver_TakeToBed.CheckMakeTakeeGuest`**：
    ///     `Takee.guest.SetGuestStatus(Faction.OfPlayer)` + 发 `"Rescued"` 任务信号
    /// 随后 `Toils_Bed.TuckIntoBed(..., rescued: true)` 完成入伙判定。
    /// 而 `RecruitUtility.Recruit(pawn, faction, recruiter)` 正是把这一串做完的正统入口：
    ///     `guest.SetGuestStatus(null)` → `SetFaction(faction, recruiter)` → `guest.Notify_PawnRecruited()`
    /// 它唯一的 `Spawned` 分支是地图注册表刷新，未 spawn 时自动跳过 ⇒ **地图无关**。
    ///
    /// ── 倒地目标能不能进车队（反编译确认）────────────────────────────────
    /// `Caravan.AddPawn` 只拒绝 `null` 与 `Dead`，**没有 `Downed` 守卫**，
    /// 而且它自带 `ShouldAutoCapture(p)` 分支 —— 说明车队系统本来就考虑了"加入需要收押的人"。
    /// 另有 `CaravanFormingUtility.IsFormingCaravanOrDownedPawnToBeTakenByCaravan(pawn)`
    /// 这个明确为"倒地者由车队带走"准备的工具。
    /// ⚠️ 顺序很重要：**先 `Recruit` 再上车**，否则 `ShouldAutoCapture` 会把刚救出的人
    /// 当俘虏收押。
    /// </summary>
    public static class RescueUtility
    {
        /// <summary>进度单位 = 百分比。整条救援用 0..100 表示。</summary>
        public const float TotalWork = 100f;

        /// <summary>`Delegation.workerStage` 的取值（语义由本类定义）。</summary>
        public const int StagePending = 0;      // 还没结算清场
        public const int StageStealthOk = 1;    // 潜入成功，未交战
        public const int StageAssaultWon = 2;   // 强攻（或潜入失败转强攻）获胜
        public const int StageAssaultLost = 3;  // 清场失败，应中断
        public const int StageExtracted = 4;    // 人已抬进车队

        // ── 目标 ────────────────────────────────────────────────────────
        //
        // ⚠️ 原版读"这个点上的人"是**两级**优先级（rw16 反编译确认，缺一不可）：
        //     GenStep_DownedRefugee.ScatterAt / GenStep_PrisonerWillingToJoin.ScatterAt：
        //       ① if (parms.sitePart.things != null && parms.sitePart.things.Any)
        //              pawn = (Pawn)parms.sitePart.things.Take(parms.sitePart.things[0]);
        //       ② else { comp = map.Parent.GetComponent<XXXComp>();
        //                pawn = comp.pawn.Any ? comp.pawn.Take(comp.pawn[0]) : 现生成; }
        //     （DownedRefugeeComp / PrisonerWillingToJoinComp 都是 ImportantPawnComp 的子类）
        //
        // S6 之前我们只读第 ① 级，于是"人在第 ② 级（或压根还没生成）"的地点会被判成
        // **"事件点上已经没有可救的人了"** —— 那是句假话：人只是还没被写进我们能读的那一格。
        // 这与 S5-b 物资藏匿点那个 bug 是同一个病（把"读不到"说成"没有"）。

        /// <summary>这条 def 声明的那个 SitePart（按 tag/def 匹配，**不管**里面有没有人）。</summary>
        public static SitePart MatchingPart(Site site, DelegationDef def)
        {
            if (site?.parts == null)
            {
                return null;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (part?.def != null && PartMatches(def, part))
                {
                    return part;
                }
            }
            return null;
        }

        /// <summary>目标所在的 SitePart（要求 `part.things` 里**真的**有 Pawn）。</summary>
        public static SitePart TargetPart(Site site, DelegationDef def)
        {
            SitePart part = MatchingPart(site, def);
            if (part?.things != null && part.things.Any && part.things[0] is Pawn)
            {
                return part;
            }
            return null;
        }

        /// <summary>原版第 ② 级：Site 上的 <see cref="ImportantPawnComp" />（子类见类注释）。</summary>
        public static ThingOwner CompOwner(Site site)
        {
            ImportantPawnComp comp = site?.GetComponent<ImportantPawnComp>();
            return comp?.pawn;
        }

        private static Pawn CompPawn(Site site)
        {
            ThingOwner owner = CompOwner(site);
            if (owner == null || !owner.Any)
            {
                return null;
            }
            return owner[0] as Pawn;
        }

        /// <summary>
        /// 目标**实际所在的容器**（原版两级里的哪一级）。
        /// 抬人时必须用它，不能写死 `part.things` —— 人在第 ② 级时那是另一个 owner。
        /// </summary>
        public static ThingOwner TargetOwner(Site site, DelegationDef def, out Pawn pawn)
        {
            pawn = null;
            SitePart part = MatchingPart(site, def);
            if (part?.things != null && part.things.Any && part.things[0] is Pawn inPart)
            {
                pawn = inPart;
                return part.things;
            }
            ThingOwner compOwner = CompOwner(site);
            if (compOwner != null && compOwner.Any && compOwner[0] is Pawn inComp)
            {
                pawn = inComp;
                return compOwner;
            }
            return null;
        }

        public static Pawn TargetPawn(Site site, DelegationDef def)
        {
            TargetOwner(site, def, out Pawn pawn);
            return pawn;
        }

        /// <summary>
        /// 抵达时把"这个点上的人"**归一到原版第 ① 级**（`SitePart.things`），必要时按原版同一口径现生成。
        /// 幂等：已经在 `part.things` 里时什么都不做。
        ///
        /// ── 为什么必须搬（人在 comp 里时）──────────────────────────────────
        /// `ImportantPawnComp.PostDestroy` → `RemovePawnOnWorldObjectRemoved()` 会把
        /// comp 里**剩下的人** Destroy 或 PassToWorld。我们把人救出来（抬进车队）之后
        /// 如果 comp 还攥着这引用，收工销毁地点的那一刀会削到活人身上 —— 白救。
        /// 原版 GenStep 也是 `Take()` 走的，所以"搬过来"与"进图时用的那一份"完全一致，不双吃。
        ///
        /// ── 为什么"哪儿都没有"时可以现生成 ─────────────────────────────
        /// 那不是凭空造人：原版对同样的地点（`part.things` 空、comp 也空）**本来就会**
        /// 在生成地图时 `GenerateRefugee` / `GeneratePrisoner` 生成一个。
        /// 我们只是把这一步提前到"委派抵达"并**写回 part.things**，
        /// 于是进图时原版会取走我们生成的这一个人 —— 掷出来是谁，进图也是谁。
        /// </summary>
        public static bool TryEnsureTarget(Site site, DelegationDef def, out string report)
        {
            return TryEnsureTarget(site, def, out report, out _);
        }

        /// <param name="generated">
        /// true = 这次调用**真的造了一个人**（原版两级来源都空，按 GenStep 口径现生成）。
        /// 调用方据此决定要不要打上"这份目标是委派替原版掷的"标记。
        /// </param>
        public static bool TryEnsureTarget(Site site, DelegationDef def, out string report, out bool generated)
        {
            report = null;
            generated = false;
            if (site == null)
            {
                report = "地点不存在";
                return false;
            }
            SitePart part = MatchingPart(site, def);
            if (part == null)
            {
                report = "这个地点上没有匹配的 SitePart";
                return false;
            }

            // ① 已经在第一优先级里：不动它（读得到就是原有目标，绝不重掷）
            if (part.things != null && part.things.Any && part.things[0] is Pawn existing)
            {
                report = "原有目标：" + existing.LabelShortCap;
                return true;
            }

            // ② 人在 ImportantPawnComp 里 → 搬到 part.things（原因见方法注释）
            ThingOwner compOwner = CompOwner(site);
            if (compOwner != null && compOwner.Any && compOwner[0] is Pawn inComp)
            {
                part.things = part.things ?? NewOwner(part);
                Thing taken = compOwner.Take(inComp);
                if (taken != null)
                {
                    part.things.TryAdd(taken);
                    if (part.things.Any)
                    {
                        report = "原版把这位挂在地点组件上（进图时才取出），已按同一口径搬到事件点上：" + inComp.LabelShortCap;
                        return true;
                    }
                    compOwner.TryAdd(taken);   // 放回去，绝不能把人弄丢
                    report = "从地点组件搬人失败（已放回原处）";
                    return false;
                }
                report = "从地点组件取人失败";
                return false;
            }

            // ③ 哪儿都没有 → 按原版 GenStep 的同一口径现生成并写回
            Pawn created = GenerateTargetLikeVanilla(site, def, out string how);
            if (created == null)
            {
                report = "按原版口径生成失败（" + how + "）";
                return false;
            }
            part.things = part.things ?? NewOwner(part);
            part.things.TryAdd(created);
            if (!part.things.Any)
            {
                report = "生成了目标但没能放进 SitePart.things";
                return false;
            }
            generated = true;
            report = "这个点上原本没有既存目标，已按原版同一口径掷定并写回：" + created.LabelShortCap + "（" + how + "）";
            return true;
        }

        /// <summary>与原版 `SitePartWorker_*` 逐字同构的容器：`ThingOwner&lt;Pawn&gt;(part, oneStackOnly: true)`。</summary>
        private static ThingOwner NewOwner(SitePart part)
        {
            return new ThingOwner<Pawn>(part, oneStackOnly: true) { dontTickContents = true };
        }

        /// <summary>
        /// 复刻原版 GenStep 的"现生成"口径（两个 GenStep 各自的那一行）。
        ///
        /// 囚犯：`GeneratePrisoner(map.Tile, faction)`，faction 取
        ///       `ParentFaction != null &amp;&amp; != 玩家 ? ParentFaction : RandomEnemyFaction()`；
        /// 难员：`GenerateRefugee(map.Tile)`（默认 chanceForFaction = 0.6，含倒地 + 双腿失能），
        ///       另外 GenStep 会把 `WillJoinColonyIfRescued` 置真，这里照做。
        /// </summary>
        private static Pawn GenerateTargetLikeVanilla(Site site, DelegationDef def, out string how)
        {
            how = null;
            if (IsPrisonerTarget(def, site))
            {
                Faction host = site.Faction;
                if (host == null || host == Faction.OfPlayer)
                {
                    host = Find.FactionManager.RandomEnemyFaction();
                }
                Pawn prisoner = PrisonerWillingToJoinQuestUtility.GeneratePrisoner(site.Tile, host);
                how = "GenStep_PrisonerWillingToJoin 口径 / GeneratePrisoner";
                return prisoner;
            }
            Pawn refugee = DownedRefugeeQuestUtility.GenerateRefugee(site.Tile);
            if (refugee?.mindState != null)
            {
                refugee.mindState.WillJoinColonyIfRescued = true;
            }
            how = "GenStep_DownedRefugee 口径 / GenerateRefugee（已倒地且双腿失能）";
            return refugee;
        }

        /// <summary>这条委派的目标是不是"囚犯"（tag 或 SitePartDef 名字）。</summary>
        public static bool IsPrisonerTarget(DelegationDef def, Site site)
        {
            if (def != null && !def.targetSitePartTags.NullOrEmpty()
                && def.targetSitePartTags.Contains("PrisonerWillingToJoin"))
            {
                return true;
            }
            SitePart part = MatchingPart(site, def);
            return part?.def != null && part.def.defName == "PrisonerWillingToJoin";
        }

        private static bool PartMatches(DelegationDef def, SitePart part)
        {
            if (def == null)
            {
                return true;
            }
            if (!def.targetSitePartDefs.NullOrEmpty() && def.targetSitePartDefs.Contains(part.def))
            {
                return true;
            }
            if (!def.targetSitePartTags.NullOrEmpty() && !part.def.tags.NullOrEmpty())
            {
                for (int i = 0; i < part.def.tags.Count; i++)
                {
                    if (def.targetSitePartTags.Contains(part.def.tags[i]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ── 守军与潜入 ──────────────────────────────────────────────────

        /// <summary>
        /// 守军的**单位数估算**，只用于潜入暴露概率。
        ///
        /// ⚠️ 刻意**不调用** `ThreatRosterFactory.Build`：那个会真的生成 pawn / 炮塔实例
        /// （炮塔最多 11 座 + 11 把枪），而本方法会被委派对话框**每帧**调用一次
        /// （`ApproachForecast` → `StealthExposure` → 这里）。每帧造 20 个 Thing 会直接把帧数打穿。
        ///
        /// 所以这里只读存档里的数据：
        ///   · 炮塔 —— **精确**：`SitePartParams.turretsCount` 是任务生成时就算好的整数
        ///     （公式 `Clamp(round(威胁点数 / 45), 2, 11)`，与 `SitePartWorker_Turrets` 一致）
        ///   · 其它 —— **估算**：按 `威胁点数 / 等效单位战力`。这只影响"该躲还是该冲"的手感，
        ///     不影响任何结算（真正的编队仍由 `ThreatRosterFactory` 精确复现）。
        /// </summary>
        public static int GuardUnitCount(Site site)
        {
            if (site?.parts == null)
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (!ThreatAssessmentEntry.IsThreatPart(part))
                {
                    continue;
                }
                n += CheapGuardCount(part);
            }
            return n;
        }

        /// <summary>等效单位战力（用于把威胁点数折成"大概几个人"）。`[建议]` 值：约等于一个中档人类守军。</summary>
        public const float EquivalentCombatPowerPerUnit = 75f;

        private static int CheapGuardCount(SitePart part)
        {
            float points = part.parms.threatPoints;
            if (part.def != null && part.def.defName == "Turrets")
            {
                // 与 SitePartWorker_Turrets.GenerateDefaultParams 同一个公式；老存档没写就现算
                int combatPower = Mathf.Max(1, Mathf.RoundToInt(MiniTurretCombatPower));
                return part.parms.turretsCount > 0
                    ? part.parms.turretsCount + part.parms.mortarsCount
                    : Mathf.Clamp(Mathf.RoundToInt(points / combatPower), 2, 11) + part.parms.mortarsCount;
            }
            return Mathf.Max(1, Mathf.RoundToInt(points / EquivalentCombatPowerPerUnit));
        }

        /// <summary>迷你炮塔的战力（`SitePartWorker_Turrets` 用它把点数折成座数）。</summary>
        public static float MiniTurretCombatPower
        {
            get
            {
                ThingDef turret = CombatSnapshotFactory.TurretDefOfChoice;
                return turret?.building != null ? Mathf.Max(1f, turret.building.combatPower) : 45f;
            }
        }

        /// <summary>有没有<b>无法无地图抽象</b>的守军（有则强攻/潜入都不可评估）。</summary>
        public static bool HasUnassessableGuard(Site site)
        {
            return ThreatAssessmentEntry.UnassessablePartCount(site) > 0;
        }

        /// <summary>
        /// 潜入暴露概率（0..1）。
        ///
        /// 口径（按既定决策）：**专业度（参与者里最高的潜行/射击/格斗）× 人数 vs 守军数量** ——
        ///     暴露 = baseExposure + exposurePerGuard × 守卫数
        ///                      - exposureReductionPerSkillLevel × 专业度
        ///                      - exposureReductionPerExtraPawn × (人数 - 1)
        /// 再 clamp 到 [minExposure, maxExposure]。
        /// </summary>
        public static float StealthExposure(Site site, List<Pawn> pawns, DelegationApproachDef approach)
        {
            if (approach == null || !approach.stealth)
            {
                return 0f;
            }
            int guards = GuardUnitCount(site);
            int men = CountUsable(pawns);
            int skill = BestSkillLevel(pawns, approach.skillDefs);

            float exposure = approach.baseExposure
                             + approach.exposurePerGuard * guards
                             - approach.exposureReductionPerSkillLevel * skill
                             - approach.exposureReductionPerExtraPawn * Mathf.Max(0, men - 1);

            return Mathf.Clamp(exposure, approach.minExposure, approach.maxExposure);
        }

        /// <summary>参与者在指定技能里的最高等级（没有列表时返回 0）。</summary>
        public static int BestSkillLevel(List<Pawn> pawns, List<SkillDef> skillDefs)
        {
            if (pawns == null || skillDefs.NullOrEmpty())
            {
                return 0;
            }
            int best = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p?.skills == null || p.Dead) continue;
                for (int j = 0; j < skillDefs.Count; j++)
                {
                    SkillDef def = skillDefs[j];
                    if (def == null) continue;
                    int lvl = p.skills.GetSkill(def)?.Level ?? 0;
                    if (lvl > best) best = lvl;
                }
            }
            return best;
        }

        private static int CountUsable(List<Pawn> pawns)
        {
            if (pawns == null) return 0;
            int n = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p != null && !p.Dead && !p.Downed) n++;
            }
            return n;
        }

        // ── 清场结算 ────────────────────────────────────────────────────

        /// <summary>
        /// 结算"清场"。
        ///
        /// 返回结果同时写进 `d.workerStage` 与 `d.workerAbortReason`，
        /// 并把伤亡**真的施加到参与者身上**（见 <see cref="ApplyCasualties"/>）。
        /// 战斗用 `Rand.RangeInclusive` 取的种子存进 `d.combatSeed`（RIM-30 起的独立字段），
        /// 保证读档后叙述与数字自洽。
        /// </summary>
        public static void ResolveClearance(Delegation d, Site site, Caravan caravan,
                                            DelegationApproachDef approach, bool casualtiesArePermanent,
                                            StringBuilder report)
        {
            if (d.workerStage != StagePending)
            {
                return;   // 已经结算过（读档后再次 Tick）
            }

            bool stealthAttempt = approach != null && approach.stealth;
            bool fighting = true;

            if (stealthAttempt)
            {
                float exposure = StealthExposure(site, d.participants, approach);
                bool detected = Rand.Chance(exposure);
                report?.AppendLine(string.Format("潜入判定：暴露概率 {0} → {1}",
                    exposure.ToStringPercent(), detected ? "**被发现**" : "未被发现"));

                if (!detected)
                {
                    d.workerStage = StageStealthOk;
                    fighting = false;
                    stopStealth(d, approach);
                }
                else
                {
                    stopStealth(d, approach);
                }
            }

            if (!fighting)
            {
                return;
            }

            // ── 强攻（或潜入失败转强攻）──
            // RIM-27：先手折扣走**唯一**判据（DelegationApproachDef.FirstStrikePenalty）；
            // 「不参战」名单（拍板 2A）也必须真的生效 —— 旧写法这里硬写 `null`，
            // 玩家勾掉的"不参战"对营救清场完全无效（与主列/交战段口径不同源）。
            float firstStrike = DelegationApproachDef.FirstStrikePenalty(approach);

            CombatSetup setup = CombatSceneFactory.Build(caravan, site, firstStrike, d.noCombatPawns, keepPawns: true);
            if (!setup.CanAssess)
            {
                d.workerStage = StageAssaultLost;
                d.workerAbortReason = "此地存在无法无地图评估的守军（需进入地图清剿）：" + setup.BlockReason();
                setup.DestroyUnusedPawns();   // S31：保留的真 pawn 必须收尾
                return;
            }

            int seed = Rand.RangeInclusive(1, 999999);
            d.combatSeed = seed;   // RIM-30：种子不再占用 rolledValue（那格只留给 worker 的"已搬 kg"语义）

            CombatResult result;
            try
            {
                using (RandRng rng = new RandRng(seed))
                {
                    result = CombatSimulator.Simulate(setup.Scene, rng);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 委派清场战斗结算失败：" + ex);
                setup.DestroyUnusedPawns();
                d.workerStage = StageAssaultLost;
                d.workerAbortReason = "清场战斗结算失败（" + ex.GetType().Name + "）";
                return;
            }

            // S31：清场同样**打扫战场**（就地屠宰阵亡者 / 收押倒地的守卫）——
            // 营救没有流程段，所以屠宰产物在这里**直接**结算（连同 S30 的缴获一起）。
            try
            {
                RimDelegationSettings cfg = RimDelegationMod.Settings;
                DelegationUtility.CleanupBattlefield(d, setup, result,
                    cfg?.corpseCleanup ?? CorpseCleanupMode.ButcherHere,
                    cfg == null || cfg.cleanupCapturePrisoners);
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 清场打扫战场失败：" + ex);
            }
            finally
            {
                setup.DestroyUnusedPawns();
            }

            report?.AppendLine("强攻结算（种子 " + seed + "）：" + result.Summary());
            ApplyCasualties(result, d.participants, casualtiesArePermanent, report);

            if (result.ThreatCleared)
            {
                d.workerStage = StageAssaultWon;
                // S30：**打赢就地缴获** —— 营救没有流程段，挂不上「搜集战利品」段（那一段只配给有流程的委派），
                // 所以这里直接结算，用的是同一份"能装多少装多少"（`DelegationUtility.TakeLoot`）。
                // S31：装备清单也并进同一份账 —— `CleanupBattlefield` 已经把就地屠宰的肉/皮记进 `d.lootBag`。
                if (d.lootBag == null)
                {
                    d.lootBag = new List<DelegationLootItem>();
                }
                RimDelegationSettings cfg2 = RimDelegationMod.Settings;
                if (cfg2 == null || cfg2.cleanupTakeEquipment)
                {
                    d.lootBag.AddRange(DelegationUtility.CaptureLoot(setup, result));
                }
                string lootNote = DelegationUtility.TakeLoot(caravan, d.lootBag, d.takenRows);
                d.lootBag.Clear();   // 搬完就清空（与搜集段同一条规矩：别让读档重搬）
                // S32「带走」档：尸骸也在这里装车（营救没有流程段，所以就地结算）
                int hauled;
                float hauledMass;
                DelegationUtility.TakeCorpses(caravan, d.pendingCorpses, d, out hauled, out hauledMass);
                if (hauled > 0)
                {
                    string corpseLine = string.Format("另带回尸骸 ×{0}（合计 {1:0.#} kg，回家可自行屠宰）", hauled, hauledMass);
                    lootNote = lootNote.NullOrEmpty() ? corpseLine : lootNote + "\n" + corpseLine;
                }
                if (!lootNote.NullOrEmpty())
                {
                    report?.AppendLine(lootNote);
                }
            }
            else
            {
                d.workerStage = StageAssaultLost;
                d.workerAbortReason = "清场失败（" + result.Outcome + "），远行队带着伤员撤了";
            }
        }

        /// <summary>潜入成功后把"未交战"记进说明文本。</summary>
        private static void stopStealth(Delegation d, DelegationApproachDef approach)
        {
            if (d.workerStage == StageStealthOk)
            {
                d.workerAbortReason = null;
            }
        }

        /// <summary>
        /// 把抽象战斗的伤亡**真的**施加到参与者身上。
        ///
        /// 映射靠 `UnitReport.Name`（= `CombatSnapshotFactory` 写入的 `LabelShortCap`），
        /// 同名时按未匹配的先后顺序取 —— 车队里短名通常唯一，这是可接受的近似。
        ///
        /// 伤害量按本模型自己的尺子折算：`(当前耐久比例 - 目标耐久比例) × 100`，
        /// 与 `CombatTuning.HealthPoolPerScale = 100` 同一个量纲，不引入第二套换算。
        /// </summary>
        public static void ApplyCasualties(CombatResult result, List<Pawn> ours, bool lethal, StringBuilder report)
        {
            if (result?.Units == null || ours == null)
            {
                return;
            }
            List<Pawn> pool = new List<Pawn>();
            for (int i = 0; i < ours.Count; i++)
            {
                if (ours[i] != null) pool.Add(ours[i]);
            }

            int killed = 0, downed = 0, hurt = 0;
            for (int i = 0; i < result.Units.Count; i++)
            {
                UnitReport u = result.Units[i];
                if (u == null || !u.IsMine) continue;
                Pawn p = TakeByName(pool, u.Name);
                if (p == null || p.Dead) continue;

                try
                {
                    if (u.Dead)
                    {
                        if (lethal)
                        {
                            p.Kill(new DamageInfo(DamageDefOf.Bullet, 9999f), null);
                            killed++;
                        }
                        else
                        {
                            HealthUtility.DamageUntilDowned(p, false);
                            downed++;
                        }
                    }
                    else if (u.Downed)
                    {
                        HealthUtility.DamageUntilDowned(p, false);
                        downed++;
                    }
                    else if (u.HealthFractionEnd < 0.995f)
                    {
                        float cur = p.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
                        float amount = (cur - u.HealthFractionEnd) * 100f;
                        if (amount >= 1f)
                        {
                            p.TakeDamage(new DamageInfo(DamageDefOf.Blunt, amount));
                            hurt++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ⚠️ 与 DelegationEventDef_PawnAccident 同一个未验证点（DESIGN §19.14 第 1 项）：
                    //    "对未 spawn 的 pawn 施加伤害"是否安全尚无定论，所以必须兜住异常，
                    //    绝不能让一次委派结算把游戏打崩。
                    Log.WarningOnce("[RimDelegation] 无地图施加伤亡失败（§19.14①）：" + ex, 0x5E0CA);
                }
            }

            if (report != null && (killed + downed + hurt) > 0)
            {
                report.AppendLine(string.Format("伤亡落实：阵亡 {0} · 倒地 {1} · 轻伤 {2}{3}",
                    killed, downed, hurt, lethal ? "" : "（本次委派设定为非致命）"));
            }
        }

        private static Pawn TakeByName(List<Pawn> pool, string name)
        {
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i] != null && pool[i].LabelShortCap.ToString() == name)
                {
                    Pawn p = pool[i];
                    pool.RemoveAt(i);
                    return p;
                }
            }
            return null;
        }

        // ── 抬人 + 入伙 ─────────────────────────────────────────────────

        /// <summary>
        /// 把目标从事件点上取下来，入伙，然后抬进车队。
        ///
        /// ⚠️ 顺序不能反：`Caravan.AddPawn` 里有 `ShouldAutoCapture(p)` 分支
        ///    —— 先把人变成玩家的人，再上车，否则会被当俘虏收押。
        /// </summary>
        public static bool ExtractAndRecruit(Site site, DelegationDef def, Caravan caravan, Pawn recruiter,
                                             out string report)
        {
            report = null;
            // 目标可能还在原版第 ② 级（ImportantPawnComp）里 —— 那里也是合法的"人在点上"，
            // 所以取人必须问 TargetOwner，不能写死 part.things（S6）
            ThingOwner owner = TargetOwner(site, def, out Pawn target);
            if (target == null || owner == null)
            {
                report = "目标已经不在了";
                return false;
            }
            if (target.Dead)
            {
                report = target.LabelShortCap + " 已经死亡";
                return false;
            }

            // ① 先入伙（原版正统入口，见类注释）
            try
            {
                RecruitUtility.Recruit(target, Faction.OfPlayer, recruiter);
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 目标入伙失败：" + ex);
                report = "入伙失败（" + ex.GetType().Name + "）";
                return false;
            }

            // 与 JobDriver_TakeToBed.CheckMakeTakeeGuest 一致的任务信号
            try
            {
                QuestUtility.SendQuestTargetSignals(target.questTags, "Rescued", target.Named("SUBJECT"));
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 发送 Rescued 任务信号失败：" + ex.Message, 0x5E0CB);
            }

            // ② 从事件点上取下来
            Thing taken = null;
            try { taken = owner.Take(target); }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 从事件点取人失败：" + ex);
            }
            if (taken == null)
            {
                report = "从事件点取人失败（人已入伙，但没能抬走）";
                return false;
            }

            // ③ 抬进车队（倒地者可以进车队：Caravan.AddPawn 只拒绝 Dead）
            try
            {
                caravan.AddPawnOrItem(taken, false);
            }
            catch (Exception ex)
            {
                Log.Error("[RimDelegation] 抬进远行队失败：" + ex);
                owner.TryAdd(taken);   // 放回原地，别把人弄丢
                report = "抬进远行队失败（" + ex.GetType().Name + "）";
                return false;
            }

            if (!caravan.ContainsPawn(target))
            {
                owner.TryAdd(taken);
                report = "远行队没有接收" + target.LabelShortCap + "（已放回原地）";
                return false;
            }

            report = target.LabelShortCap + " 已入伙并抬上远行队";
            return true;
        }

        /// <summary>
        /// 救援速率：每人每作业小时推进的百分点。
        ///
        /// `rateFactor`（RIM-5）= 满意度给出的作业速率系数：作业模式不再提供效率（用户拍板 1A），
        /// 乘数改由调用方按满意度传进来（在途传 <see cref="Delegation.SatisfactionRateFactor" />）。
        /// </summary>
        public static float PercentPerWorkHour(int pawnCount, int bestSkillLevel, DelegationModeDef mode,
            float rateFactor = 1f)
        {
            float men = Mathf.Max(1, pawnCount);
            float skillFactor = 0.6f + 0.04f * Mathf.Clamp(bestSkillLevel, 0, 20);   // 0 级 0.6 → 10 级 1.0 → 20 级 1.4
            return PercentPerHourPerPawn * men * skillFactor * rateFactor;
        }

        /// <summary>
        /// 单人每作业小时的救援进度（百分点）。
        /// 取 12 ⇒ 单人正常工作（8h/天）约 **1.04 天**干完 100 点。
        /// `[建议]` 值：现实里"把人稳定住再抬上车"是几小时级的活，这里刻意放慢到"一天"，
        /// 好让"派几个人 / 用哪个模式"有意义（与采矿、搜刮的手感对齐）。
        /// </summary>
        public const float PercentPerHourPerPawn = 12f;
    }
}
