using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 一张战场报告：把"车队 + 地点"变成一个可运行的 <see cref="CombatScene"/>，
    /// 并留下**全部假设与降级说明**，供预告面板原样展示（DESIGN.md §19.19.7）。
    /// </summary>
    public sealed class CombatSetup
    {
        public CombatScene Scene;
        public Site Site;
        public Caravan Caravan;
        public readonly List<string> OurNotes = new List<string>();

        /// <summary>
        /// S25：**能打的自己人**（真 Pawn 引用）—— 作战任务段的"参战人员"列表要用它画头像行。
        /// 与 `OurNotes` 互补：进不了这里的都在 notes 里写明原因。
        /// </summary>
        public readonly List<Pawn> OurPawns = new List<Pawn>();
        public readonly List<string> EnemyNotes = new List<string>();
        public readonly List<string> Unresolved = new List<string>();
        public float TotalThreatPoints;

        /// <summary>
        /// S30：本次战场里**敌人身上带的东西**（缴获，来源 = `ThreatRosterFactory` 在销毁 pawn 之前抄的那份）。
        ///
        /// 与 `Scene.Units` 里的敌方**同序**（同一次编队生成的产物），所以战斗结束后
        /// 用 `UnitReport` 的下标就能知道"哪个敌人死了、留下了什么"。
        /// 预告面板调的也是同一个 Build ⇒ 不额外生成、不额外消耗随机数。
        /// </summary>
        public readonly List<DelegationLootItem> EnemyLoot = new List<DelegationLootItem>();

        /// <summary>
        /// S31：**保留下来的真 pawn**（下标与 <see cref="Scene" /> 的敌方同序；炮塔位是 null）。
        ///
        /// 只有"真结算"（`keepPawns = true`：交战段 / 营救清场）才非空 —— 战后要
        /// **就地屠宰阵亡者**、**收押倒地的**，都离不开真 pawn。UI 的预告/评估一律不留。
        /// ⚠️ 调用方**必须**把没用上的销毁掉：<see cref="DestroyUnusedPawns" />（放 finally）。
        /// </summary>
        public readonly List<Pawn> EnemyPawns = new List<Pawn>();

        /// <summary>
        /// S31：把还活着、也没被收编的保留 pawn 全部销毁（幂等）。
        /// "已被收编"的判据是**已经不在 holdingOwner 里**（囚犯上车后 ParentHolder 变成车队）。
        /// </summary>
        public void DestroyUnusedPawns()
        {
            for (int i = 0; i < EnemyPawns.Count; i++)
            {
                Pawn p = EnemyPawns[i];
                if (p == null || p.Destroyed || p.ParentHolder != null)
                {
                    continue;   // 已经销毁 / 已经上车（囚犯）⇒ 不碰
                }
                ThreatRosterFactory.DiscardPawn(p);
            }
        }

        /// <summary>能否给出可信的评估（我方有可战单位 + 敌方编队可抽象）。</summary>
        public bool CanAssess
        {
            get
            {
                return Scene != null && Scene.CountMine() > 0 && Scene.CountEnemies() > 0
                       && Unresolved.Count == 0;
            }
        }

        public string BlockReason()
        {
            if (Scene == null) return "无法构建战场";
            if (Scene.CountMine() == 0) return "远行队里没有可战斗的成员";
            if (Unresolved.Count > 0) return "此地存在无法抽象评估的威胁（需进入地图清剿）";
            if (Scene.CountEnemies() == 0) return "没能推算出敌方编队";
            return null;
        }
    }

    /// <summary>把游戏状态折算成战斗计算核心的输入（DESIGN.md §19.19 / §19.20 / §19.21）。</summary>
    public static class CombatSceneFactory
    {
        /// <summary>
        /// 建立战场。
        ///
        /// 立场假设：**车队是进攻方（无地形优势），守军是防守方（有地形优势）**。
        /// 这与 §19.20.4 的机制配合，产生"进攻方要顶着火力接近"的效果。
        ///
        /// <paramref name="ourFirstStrikePenalty"/> &gt; 0 时给敌方一次**先手一轮**的机会
        /// （潜入被发现转强攻的代价）：折算成我方开局的耐久折扣。
        /// 折扣作用在**快照**上，所以委派结算与对话框里的威胁评估看到的是同一个战场
        /// —— "预告即契约"（DESIGN §19.12）。
        ///
        /// <paramref name="ourRoster"/> = **这次委派的参与者名单**（RIM-26 拍板 1A）。
        /// 传 null 才退回"整个车队"的旧行为 —— 只有没经过参与者解析的调用方才该这么用。
        /// </summary>
        public static CombatSetup Build(Caravan caravan, Site site, float ourFirstStrikePenalty = 0f,
            List<Pawn> excludeFromCombat = null, bool keepPawns = false, List<Pawn> ourRoster = null)
        {
            CombatSetup setup = new CombatSetup { Caravan = caravan, Site = site };
            if (caravan == null || site == null) return setup;

            CombatScene scene = new CombatScene
            {
                OurPriority = TargetPriority.Strongest,
                EnemyPriority = TargetPriority.Weakest,
                Retreat = new RetreatPolicy
                {
                    Kind = RetreatPolicyKind.CasualtyFraction,
                    CasualtyFraction = 0.34f,
                },
                TicksPerRound = CombatTuning.TicksPerRound,
                MaxRounds = CombatTuning.MaxRounds,
                StartDistance = CombatTuning.StartDistance,
                MeleeRange = CombatTuning.MeleeRange,
                NearBand = CombatTuning.NearBand,
                NoAdvantageRangeFactor = CombatTuning.NoAdvantageRangeFactor,
                MaxStandoff = CombatTuning.MaxStandoff,
                FallbackFactor = CombatTuning.FallbackFactor,
                Standoff = StandoffPolicy.OnlyIfOutranging,
                DownHealthFraction = CombatTuning.DownHealthFraction,
                DisengageFactor = CombatTuning.DisengageFactor,
                EnemyOutputFactor = 1f,
                LogFilter = CombatLogFilter.Default,
            };

            // ── 我方：**只有这次委派的参与者**（RIM-26 拍板 1A）──
            // 旧写法取 `caravan.PawnsListForReading`＝整个车队（含驮兽、没参加这趟的人、还带着武器的囚犯），
            // 而伤亡只往 `d.participants` 身上落 ⇒ 战报说"我方 2 阵亡"而车队一个人都没少；
            // 多出来的人和动物还替参与者分担火力（这一仗比面板上算的更容易赢）。
            // 现在"谁进模型"与"谁承担伤亡"是**同一份名单**，第三种情况不存在。
            List<Pawn> ours = ourRoster;
            if (ours.NullOrEmpty())
            {
                // 存档自愈（准则⑥）：名单缺失或为空（异常数据）时退回整个车队 ——
                // 绝不让"我方零单位"的模型静默变成一场必败仗。这件事本身也要说出来（不静默）。
                if (ourRoster != null)
                {
                    setup.OurNotes.Add("参与者名单为空，已按整个车队推演");
                }
                ours = caravan.PawnsListForReading;
            }
            for (int i = 0; i < ours.Count; i++)
            {
                Pawn p = ours[i];
                if (p == null || p.Dead || p.Downed) continue;

                // 能不能打由工厂判定（取不到攻击 verb 的会被排除并留下原因）
                string problem;
                CombatUnitSnapshot snap = CombatSnapshotFactory.FromPawn(p, true, out problem);
                if (snap == null)
                {
                    if (problem != null) setup.OurNotes.Add(problem);
                    continue;
                }
                snap.HasTerrainAdvantage = false;   // 进攻方：暴露在开阔地
                // S27：玩家可以在「作战任务」段里逐个把人/动物排除出**这一场战斗**
                //      —— 他们仍然属于这次委派（照常开采/搬运），只是不参战。
                setup.OurPawns.Add(p);              // 能打的都进这张名单（UI 要靠它画出可切换的行）
                if (excludeFromCombat != null && excludeFromCombat.Contains(p))
                {
                    setup.OurNotes.Add("「" + p.LabelShort + "」按你的设置不参加作战");
                    continue;
                }
                scene.Add(snap);
            }

            // ── 敌方：按站点威胁部件的确定性种子复现 ──
            ThreatRosterFactory.Result roster = ThreatRosterFactory.Build(site, keepPawns);
            for (int i = 0; i < roster.Enemies.Count; i++) scene.Add(roster.Enemies[i]);
            setup.EnemyNotes.AddRange(roster.Notes);
            setup.Unresolved.AddRange(roster.Unresolved);
            setup.TotalThreatPoints = roster.TotalThreatPoints;
            setup.EnemyLoot.AddRange(roster.EnemyLoot);   // S30：缴获清单（与上面同一次生成）
            setup.EnemyPawns.AddRange(roster.EnemyPawns); // S31：保留的真 pawn（keepPawns 时非空）

            // ── 天气：按生物群系的天气表逐场随机 ──
            FillWeather(scene, site.Tile, setup.EnemyNotes);

            // ── 守军先手一轮（潜入被发现转强攻的代价）──
            if (ourFirstStrikePenalty > 0f)
            {
                ApplyFirstStrikePenalty(scene, ourFirstStrikePenalty, setup.OurNotes);
            }

            setup.Scene = scene;
            return setup;
        }

        /// <summary>
        /// 把"守军先手一轮"折算成我方开局的耐久折扣。
        ///
        /// 为什么用期望输出而不是真跑一轮：这里只是一个**场景级修正**，
        /// 目的是让结算与预告拿到同一个战场。真跑一轮敌方输出需要复制核心的回合逻辑，
        /// 那会让"预告"多出第二条代码路径 —— 正是 §19.12 要避免的事。
        /// </summary>
        private static void ApplyFirstStrikePenalty(CombatScene scene, float factor, List<string> notes)
        {
            float enemyAlpha = scene.ExpectedEnemyDamagePerRound();
            if (enemyAlpha <= 0f) return;

            int mine = 0;
            float pool = 0f;
            for (int i = 0; i < scene.Units.Count; i++)
            {
                CombatUnitSnapshot u = scene.Units[i];
                if (u == null || !u.IsMine) continue;
                mine++;
                pool += u.MaxHealth;
            }
            if (mine <= 0 || pool <= 0f) return;

            float loss = enemyAlpha * factor;
            // 上限 90%：先手一轮可以很痛，但不该一开局就全灭（那等于"潜入失败必输"，没有决策空间）
            float perUnit = Math.Min(loss / mine, pool / mine * 0.9f);
            if (perUnit <= 0f) return;

            for (int i = 0; i < scene.Units.Count; i++)
            {
                CombatUnitSnapshot u = scene.Units[i];
                if (u == null || !u.IsMine) continue;
                u.MaxHealth = Math.Max(1f, u.MaxHealth - perUnit);
            }

            notes.Add("守军先手一轮：我方每人开局耐久 -" + perUnit.ToString("0.#") +
                      "（敌方每回合期望输出 " + enemyAlpha.ToString("0.#") +
                      " × " + factor.ToString("0.##") + "）");
        }

        /// <summary>
        /// 从 tile 的生物群系取天气表（不需要地图）。
        /// 数值直接是 vanilla 的 <c>WeatherDef.accuracyMultiplier</c> 与
        /// <c>BiomeDef.baseWeatherCommonalities</c> 权重（§19.20.5）。
        /// </summary>
        private static void FillWeather(CombatScene scene, PlanetTile tile, List<string> notes)
        {
            try
            {
                Tile worldTile = Find.WorldGrid[tile];
                // 注意：Tile.biome 是私有字段，公开入口是 PrimaryBiome（1.6）
                BiomeDef biome = worldTile != null ? worldTile.PrimaryBiome : null;
                if (biome == null) return;

                List<WeatherCommonalityRecord> table = biome.baseWeatherCommonalities;
                if (table == null || table.Count == 0) return;

                for (int i = 0; i < table.Count; i++)
                {
                    WeatherCommonalityRecord rec = table[i];
                    if (rec.weather == null || rec.commonality <= 0f) continue;
                    scene.WeatherTable.Add(new WeatherSample(
                        rec.weather.defName, rec.weather.accuracyMultiplier, rec.commonality));
                }

                notes.Add("天气表取自生物群系「" + biome.LabelCap + "」，逐场随机抽取（期望命中 ×" +
                          scene.ExpectedWeatherMultiplier().ToString("0.00") + "）");
            }
            catch (Exception ex)
            {
                notes.Add("天气表读取失败，按无天气处理（" + ex.GetType().Name + "）");
            }
        }

        /// <summary>把战场假设摊成可读文本（预告面板显示）。</summary>
        public static string DescribeAssumptions()
        {
            string[] a = CombatTuning.Assumptions();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++) sb.AppendLine("  · " + a[i]);
            return sb.ToString();
        }
    }
}
