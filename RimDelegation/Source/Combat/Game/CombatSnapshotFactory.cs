using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 真 <see cref="Pawn"/> → <see cref="CombatUnitSnapshot"/>。
    ///
    /// **这里是"能复用 vanilla 的就复用"这条原则的落点**（DESIGN.md §19.16）：
    ///   • 命中率  ← <c>ShotReport.HitFactorFromShooter</c>（技能^距离 × 四档曲线）
    ///              × <c>VerbProperties.GetHitChanceFactor</c>（武器精度）
    ///   • 射程    ← <c>VerbProperties.AdjustedRange</c>
    ///   • 射速    ← <c>AdjustedFullCycleTime</c> + <c>burstShotCount</c>
    ///   • 伤害    ← <c>ProjectileProperties.GetDamageAmount</c> 或 <c>AdjustedMeleeDamageAmount</c>
    ///   • 破甲    ← <c>AdjustedArmorPenetration</c>
    ///   • 护甲    ← <c>StatDefOf.ArmorRating_*</c>
    /// 只有下面这几项是"换算"而非"读取"，全部集中在 <see cref="CombatTuning"/>。
    /// </summary>
    public static class CombatSnapshotFactory
    {
        /// <summary>
        /// 从真 pawn 折算快照。取不到攻击 verb（例如囚犯、无手动物）时返回 null 并给出原因。
        /// </summary>
        public static CombatUnitSnapshot FromPawn(Pawn pawn, bool mine, out string problem)
        {
            problem = null;
            if (pawn == null) { problem = "空 pawn"; return null; }
            if (pawn.Dead) { problem = pawn.LabelShort + " 已死亡"; return null; }
            if (pawn.Downed) { problem = pawn.LabelShort + " 已倒地"; return null; }

            Verb verb = pawn.TryGetAttackVerb(null, false, false);
            if (verb == null || verb.verbProps == null) { problem = pawn.LabelShort + " 没有可用的攻击方式"; return null; }

            VerbProperties vp = verb.verbProps;
            bool melee = vp.IsMeleeAttack;

            float range = melee ? CombatTuning.MeleeRange : Math.Max(1f, vp.AdjustedRange(verb, pawn));
            float shotsPerRound = ShotsPerRound(verb, pawn);
            float damage = DamagePerShot(verb, pawn, melee);
            float armorPen = vp.AdjustedArmorPenetration(verb, pawn);
            ArmorCategory category = ArmorCategoryOf(verb, melee, out DamageDef damageDef);
            float armor = ArmorRatingFor(pawn, category);

            float near, far;
            Accuracy(pawn, verb, range, melee, out near, out far);

            float healthScale = 1f;
            if (pawn.def != null && pawn.def.race != null) healthScale = pawn.def.race.baseHealthScale;
            float healthFraction = 1f;
            if (pawn.health != null && pawn.health.summaryHealth != null)
                healthFraction = Math.Max(0.05f, pawn.health.summaryHealth.SummaryHealthPercent);

            float moveCellsPerSecond = 0f;
            try { moveCellsPerSecond = pawn.GetStatValue(StatDefOf.MoveSpeed); }
            catch { moveCellsPerSecond = 0f; }

            CombatUnitSnapshot snap = new CombatUnitSnapshot
            {
                Name = pawn.LabelShortCap,
                IconDefName = pawn.def == null ? null : pawn.def.defName,   // S25/RIM-35：人族 / 动物 / 机械族各自的 uiIcon（核心只收 defName）
                IsMine = mine,
                MaxHealth = Math.Max(5f, healthScale * CombatTuning.HealthPoolPerScale * healthFraction),
                AccuracyNear = near,
                AccuracyFar = far,
                Range = range,
                MoveSpeed = CombatTuning.CellsPerRound(moveCellsPerSecond),
                IsMelee = melee,
                HasTerrainAdvantage = true,      // 由调用方按战场态势覆盖
                ShotsPerRound = shotsPerRound,
                DamagePerShot = damage,
                ArmorPen = armorPen,
                ArmorRating = armor,
                Category = category,
                ThreatWeight = Math.Max(0f, (near + far) * 0.5f * shotsPerRound * damage),
            };

            if (damage <= 0f && shotsPerRound <= 0f)
            {
                problem = pawn.LabelShort + " 无法造成伤害";
            }
            return snap;
        }

        /// <summary>每回合打出几次 = (回合秒数 / 一个完整周期秒数) × 每轮发数。</summary>
        private static float ShotsPerRound(Verb verb, Pawn pawn)
        {
            float cycleSeconds = verb.verbProps.AdjustedFullCycleTime(verb, pawn);
            if (cycleSeconds <= 0.01f) cycleSeconds = 0.01f;

            float secondsPerRound = CombatTuning.TicksPerRound / 60f;
            float cycles = secondsPerRound / cycleSeconds;
            int burst = Math.Max(1, verb.verbProps.burstShotCount);
            return Math.Max(0f, cycles * burst);
        }

        // ── 炮塔（§18.3 分流表：Turrets 从 ForbidAbstract 改为可抽象）──────────────
        //
        // 为什么炮塔**可以**抽象：`SitePartWorker_Turrets.GenerateDefaultParams` 在任务生成那一刻就把
        //     turretsCount = Clamp(round(威胁点数 / Turret_MiniTurret.building.combatPower), 2, 11)
        //     mortarsCount = Rand.RangeInclusive(0, 1)
        // 算好并写进 `SitePartParams`（随存档持久化）⇒ 编制是**确定性的整数**，与
        // `SleepingMechanoidsSitePartUtility.GetPawnGroupMakerSeed` 属于同一类可复现数据。
        // 无法抽象的是"布局"与"触发时机"，而这两件事在本模型里本来就由
        // `HasTerrainAdvantage` 与 `CombatScene.EnemyOutputFactor` 吸收。

        /// <summary>炮塔快照用的固定炮塔类型 —— 与 vanilla `SitePartWorker_Turrets` 里写死的那个一致。</summary>
        public static ThingDef TurretDefOfChoice => ThingDefOf.Turret_MiniTurret;

        /// <summary>
        /// 炮塔 → 快照。
        ///
        /// 炮塔不是 `Pawn`，所以命中的两个因子走的是 vanilla 自己的"非 Pawn 射手"路径：
        ///   · 射手项 ← <c>ShotReport.HitFactorFromShooter(turret, distance)</c>
        ///     —— 非 Pawn 分支是 **`ShootingAccuracyTurret ^ distance`**（**没有**四段曲线），下限 0.0201
        ///   · 武器项 ← <c>VerbProperties.GetHitChanceFactor(gun, distance)</c>
        ///     —— 必须传**枪的 Thing**：炮塔枪的 verb 级 `accuracyTouch..Long` 全是 1，
        ///        真值（0.77/0.70/0.45/0.24）在 `Gun_MiniTurret` 的 `statBases` 里，
        ///        只有传了 equipment 才会去读 `StatDefOf.Accuracy*`
        ///   · 伤害/破甲 ← <c>ProjectileProperties.GetDamageAmount/GetArmorPenetration</c>
        ///     （`Bullet_MiniTurret` 的 `armorPenetrationBase = -1` 是哨兵值，
        ///       vanilla 会把它解析成 `伤害 × 0.015` ⇒ 0.18；不要自己填 -1）
        ///
        /// 生成两个**未 spawn** 的临时 Thing（炮塔本体 + 它挂的枪）只为取数值，用完即弃 ——
        /// 与 <see cref="ThreatRosterFactory"/> 生成临时 pawn 是同一个套路（DESIGN §19.14 第 4 项）。
        /// </summary>
        public static CombatUnitSnapshot FromTurret(ThingDef turretDef, ThingDef gunDef, string name, out string problem)
        {
            problem = null;
            if (turretDef == null) { problem = "炮塔类型缺失"; return null; }

            Thing turret = null;
            Thing gun = null;
            try
            {
                turret = ThingMaker.MakeThing(turretDef);
                if (gunDef != null) gun = ThingMaker.MakeThing(gunDef);
            }
            catch (Exception ex)
            {
                problem = "炮塔实例构造失败（" + ex.GetType().Name + "）";
                Discard(turret);
                Discard(gun);
                return null;
            }

            try
            {
                VerbProperties vp = PrimaryRangedVerb(gunDef);
                if (vp == null) { problem = "炮塔枪没有可用的射击 verb"; return null; }

                float range = Math.Max(1f, vp.range);
                float damage = 0f;
                float armorPen = 0f;
                ArmorCategory category = ArmorCategory.Sharp;
                if (vp.defaultProjectile != null && vp.defaultProjectile.projectile != null)
                {
                    ProjectileProperties proj = vp.defaultProjectile.projectile;
                    damage = Math.Max(0f, proj.GetDamageAmount(gun, null));
                    armorPen = Math.Max(0f, proj.GetArmorPenetration(gun, null));
                    string cat = (proj.damageDef != null && proj.damageDef.armorCategory != null)
                        ? proj.damageDef.armorCategory.defName : null;
                    if (cat == "Blunt") category = ArmorCategory.Blunt;
                    else if (cat == "Heat") category = ArmorCategory.Heat;
                }

                float near, far;
                AccuracyForTurret(turret, gun, vp, range, out near, out far);

                float maxHp = 100f;
                try { maxHp = Math.Max(5f, turret.GetStatValue(StatDefOf.MaxHitPoints)); }
                catch { /* 用兜底值 */ }

                float armor = 0f;
                try { armor = Math.Max(0f, turret.GetStatValue(ArmorStatFor(category))); }
                catch { armor = 0f; }   // 建筑默认没有护甲评级

                CombatUnitSnapshot snap = new CombatUnitSnapshot
                {
                    Name = name,
                    IconDefName = turretDef == null ? null : turretDef.defName,   // S25/RIM-35：炮塔自己的 uiIcon（核心只收 defName）
                    IsMine = false,
                    MaxHealth = maxHp,
                    AccuracyNear = near,
                    AccuracyFar = far,
                    Range = range,
                    // ★ 固定火力：不推进、不后撤。核心的 SideAdvanceSpeed 取 min ⇒ 敌方推进速度变 0，
                    //   正是"顶着炮塔往上冲"要的形态。
                    MoveSpeed = 0f,
                    IsMelee = false,
                    HasTerrainAdvantage = true,     // 工事
                    ShotsPerRound = TurretShotsPerRound(turretDef, gun, vp),
                    DamagePerShot = damage,
                    ArmorPen = armorPen,
                    ArmorRating = armor,
                    Category = category,
                    ThreatWeight = Math.Max(0f, (near + far) * 0.5f * TurretShotsPerRound(turretDef, gun, vp) * damage),
                };

                if (damage <= 0f || snap.ShotsPerRound <= 0f)
                {
                    problem = name + " 无法造成伤害（伤害 " + damage.ToString("0.#") +
                              " / 射速 " + snap.ShotsPerRound.ToString("0.##") + "）";
                }
                return snap;
            }
            catch (Exception ex)
            {
                problem = "炮塔折算失败（" + ex.GetType().Name + "：" + ex.Message + "）";
                return null;
            }
            finally
            {
                Discard(turret);
                Discard(gun);
            }
        }

        /// <summary>炮塔枪的主射击 verb（`Gun_MiniTurret` 只有一条，但不要假设）。</summary>
        private static VerbProperties PrimaryRangedVerb(ThingDef gunDef)
        {
            // 注意：`ThingDef.verbs` 是私有字段，公开入口是 `Verbs` 属性（1.6）
            List<VerbProperties> verbs = gunDef?.Verbs;
            if (verbs == null) return null;
            VerbProperties fallback = null;
            for (int i = 0; i < verbs.Count; i++)
            {
                VerbProperties vp = verbs[i];
                if (vp == null || vp.IsMeleeAttack) continue;
                if (vp.isPrimary) return vp;
                if (fallback == null) fallback = vp;
            }
            return fallback;
        }

        /// <summary>
        /// 炮塔的两点命中率。
        ///
        /// ⚠️ 已知失真：vanilla 对非 Pawn 射手算的是 `ShootingAccuracyTurret ^ distance`
        /// （**纯指数衰减，没有四段曲线**），而本模型在两点之间**线性插值**。
        /// 迷你炮塔实测：8 格 ≈ 0.53、28.9 格 ≈ 0.12。
        /// 记在 DESIGN §19.20 的"两点近似"条目里，与 pawn 走的是同一种近似。
        /// </summary>
        private static void AccuracyForTurret(Thing turret, Thing gun, VerbProperties vp, float range,
                                              out float near, out float far)
        {
            float nearDist = Math.Min(CombatTuning.NearBand, Math.Max(1f, range * 0.5f));
            near = TurretHitChanceAt(turret, gun, vp, nearDist);
            far = TurretHitChanceAt(turret, gun, vp, Math.Max(nearDist, range));
        }

        private static float TurretHitChanceAt(Thing turret, Thing gun, VerbProperties vp, float distance)
        {
            float shooter;
            try { shooter = ShotReport.HitFactorFromShooter(turret, distance); }
            catch { shooter = 1f; }
            float weapon = 1f;
            try { weapon = vp.GetHitChanceFactor(gun, distance); }
            catch { weapon = 1f; }
            float targetSize = CombatMath.Clamp(CombatTuning.ReferenceTargetBodySize, 0.5f, 2f);
            return CombatMath.Clamp01(shooter * weapon * targetSize);
        }

        /// <summary>
        /// 炮塔每回合打出几次。
        ///
        /// 与 pawn 的差别：炮塔的节拍来自 `BuildingProperties.turretBurstCooldownTime`
        /// （迷你炮塔 = 4.8 秒），而不是 `VerbProperties.AdjustedFullCycleTime`
        /// —— 后者需要一个 Verb 实例，而我们从 def 直接取。
        /// 实测两者一致（`Gun_MiniTurret` 的 `RangedWeapon_Cooldown` 也是 4.8），所以取较大者。
        /// </summary>
        private static float TurretShotsPerRound(ThingDef turretDef, Thing gun, VerbProperties vp)
        {
            float turretCooldown = turretDef?.building != null ? turretDef.building.turretBurstCooldownTime : 0f;
            float statCooldown = 0f;
            if (gun != null)
            {
                try { statCooldown = gun.GetStatValue(StatDefOf.RangedWeapon_Cooldown); }
                catch { statCooldown = 0f; }
            }
            float warmup = vp != null ? Math.Max(0f, vp.warmupTime) : 0f;
            float cycleSeconds = Math.Max(turretCooldown, statCooldown) + warmup;
            if (cycleSeconds <= 0.01f) cycleSeconds = 0.01f;

            float secondsPerRound = CombatTuning.TicksPerRound / 60f;
            int burst = vp != null ? Math.Max(1, vp.burstShotCount) : 1;
            return Math.Max(0f, secondsPerRound / cycleSeconds * burst);
        }

        private static StatDef ArmorStatFor(ArmorCategory category)
        {
            switch (category)
            {
                case ArmorCategory.Blunt: return StatDefOf.ArmorRating_Blunt;
                case ArmorCategory.Heat: return StatDefOf.ArmorRating_Heat;
                default: return StatDefOf.ArmorRating_Sharp;
            }
        }

        /// <summary>丢弃"只为取数值"而生成的临时 Thing（从未 spawn ⇒ 不进任何容器）。</summary>
        private static void Discard(Thing t)
        {
            if (t == null) return;
            try
            {
                if (!t.Destroyed) t.Destroy();
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimDelegation] 清理临时炮塔实例失败：" + ex.Message, 0x5E0C8);
            }
        }

        private static float DamagePerShot(Verb verb, Pawn pawn, bool melee)
        {
            VerbProperties vp = verb.verbProps;
            if (melee) return Math.Max(0f, vp.AdjustedMeleeDamageAmount(verb, pawn));

            if (vp.defaultProjectile == null || vp.defaultProjectile.projectile == null) return 0f;
            int amount = vp.defaultProjectile.projectile.GetDamageAmount(verb.EquipmentSource, null);
            return Math.Max(0f, amount);
        }

        private static ArmorCategory ArmorCategoryOf(Verb verb, bool melee, out DamageDef damageDef)
        {
            damageDef = null;
            VerbProperties vp = verb.verbProps;

            if (melee) damageDef = vp.meleeDamageDef;
            else if (vp.defaultProjectile != null && vp.defaultProjectile.projectile != null)
                damageDef = vp.defaultProjectile.projectile.damageDef;

            // 注意：DamageArmorCategoryDefOf 在 1.6 里**只暴露了 Sharp**（反编译确认），
            // Blunt / Heat 只能按 defName 比对。
            string cat = (damageDef != null && damageDef.armorCategory != null)
                ? damageDef.armorCategory.defName : null;
            if (cat == "Blunt") return ArmorCategory.Blunt;
            if (cat == "Heat") return ArmorCategory.Heat;
            return ArmorCategory.Sharp;
        }

        private static float ArmorRatingFor(Pawn pawn, ArmorCategory category)
        {
            StatDef stat;
            switch (category)
            {
                case ArmorCategory.Blunt: stat = StatDefOf.ArmorRating_Blunt; break;
                case ArmorCategory.Heat: stat = StatDefOf.ArmorRating_Heat; break;
                default: stat = StatDefOf.ArmorRating_Sharp; break;
            }
            try { return Math.Max(0f, pawn.GetStatValue(stat)); }
            catch { return 0f; }
        }

        /// <summary>
        /// 两点命中率：近端（<see cref="CombatTuning.NearBand"/> 格）与远端（有效射程）。
        ///
        /// 用的是 vanilla 自己的两个函数，所以技能与武器的差异**自动正确**；
        /// 目标体积暂时按标准人形（1.0）计入 —— 按目标取值是 §19.19.3⑧ 的待办。
        /// 近战没有距离衰减，两点取同一个值（<c>MeleeHitChance</c>）。
        /// </summary>
        private static void Accuracy(Pawn pawn, Verb verb, float range, bool melee, out float near, out float far)
        {
            if (melee)
            {
                float meleeChance = 0.6f;
                try { meleeChance = pawn.GetStatValue(StatDefOf.MeleeHitChance); }
                catch { /* 用兜底值 */ }
                near = far = CombatMath.Clamp01(meleeChance);
                return;
            }

            float nearDist = Math.Min(CombatTuning.NearBand, Math.Max(1f, range * 0.5f));
            near = HitChanceAtDistance(pawn, verb, nearDist);
            far = HitChanceAtDistance(pawn, verb, Math.Max(nearDist, range));
        }

        private static float HitChanceAtDistance(Pawn pawn, Verb verb, float distance)
        {
            float shooter = ShotReport.HitFactorFromShooter(pawn, distance);
            float weapon = verb.verbProps.GetHitChanceFactor(verb.EquipmentSource, distance);
            float targetSize = CombatMath.Clamp(CombatTuning.ReferenceTargetBodySize, 0.5f, 2f);
            return CombatMath.Clamp01(shooter * weapon * targetSize);
        }

        /// <summary>把快照转成一行可读文本，用于开发 gizmo 与日志核对。</summary>
        public static string Describe(CombatUnitSnapshot s)
        {
            if (s == null) return "（空）";
            StringBuilder sb = new StringBuilder();
            sb.Append(s.IsMine ? "我 " : "敌 ").Append(s.Name).Append("：");
            sb.Append("耐久 ").Append(s.MaxHealth.ToString("0"));
            sb.Append(" · 射程 ").Append(s.Range.ToString("0.#"));
            sb.Append(s.IsMelee ? "（近战）" : "");
            sb.Append(" · 移动 ").Append(s.MoveSpeed.ToString("0.##")).Append(" 格/回合");
            sb.Append(" · 命中 ").Append(s.AccuracyNear.ToString("P0")).Append("→").Append(s.AccuracyFar.ToString("P0"));
            sb.Append(" · 射速 ").Append(s.ShotsPerRound.ToString("0.##")).Append("/回合");
            sb.Append(" · 伤害 ").Append(s.DamagePerShot.ToString("0.#"));
            sb.Append(" · 破甲 ").Append(s.ArmorPen.ToString("0.##"));
            sb.Append(" · 护甲 ").Append(s.ArmorRating.ToString("0.##"));
            return sb.ToString();
        }
    }
}
