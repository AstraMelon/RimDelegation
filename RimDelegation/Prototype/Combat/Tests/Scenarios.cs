using System;
using System.Collections.Generic;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 样例场景。数值经过标定，让每场对抗都落在"有意义区间"（不是一边倒）。
    ///
    /// 这些数字是**为了验证引擎行为**而标定的，不是 vanilla 实测值 ——
    /// 真实数值要由游戏侧工厂（CombatSnapshotFactory）从真 pawn 折算后重新标定，
    /// 见 DESIGN.md §19.17。
    ///
    /// 空间参数（§19.20）：默认初始距离 40 格 > 常见武器射程 25，
    /// 所以每场都从"双方都够不着"开始，必须先接近。
    /// </summary>
    public static class Scenarios
    {
        // ── 校准规则：AccuracyNear / AccuracyFar ────────────────────────
        // 传进来的 hit 是"中距离命中率"（旧模型的单一字段）。
        // 按两点线性衰减拆开：near = 1.25×hit（≤8 格）、far = 0.55×hit（≥有效射程）。
        // 于是距离 ≈15 格时命中率 ≈ hit —— 保持与旧标定值可比。
        private const float NearFactor = 1.25f;
        private const float FarFactor = 0.55f;

        private static CombatUnitSnapshot Unit(string name, bool mine, float hp, float hit,
                                               float shots, float dmg, float ap, float armor,
                                               ArmorCategory cat,
                                               float range = 25f, float move = 4.5f,
                                               bool melee = false, bool adv = true)
        {
            float near = Math.Min(1f, hit * NearFactor);
            float far = hit * FarFactor;
            return new CombatUnitSnapshot
            {
                Name = name,
                IsMine = mine,
                MaxHealth = hp,
                AccuracyNear = near,
                AccuracyFar = far,
                Range = range,
                MoveSpeed = move,
                IsMelee = melee,
                HasTerrainAdvantage = adv,
                ShotsPerRound = shots,
                DamagePerShot = dmg,
                ArmorPen = ap,
                ArmorRating = armor,
                Category = cat,
                ThreatWeight = hit * shots * dmg,
            };
        }

        /// <summary>4 名殖民者（混合武装）—— 各场景共用的我方。默认无地形优势（进攻方）。</summary>
        public static List<CombatUnitSnapshot> OurSquad(bool hasTerrainAdvantage = false)
        {
            return new List<CombatUnitSnapshot>
            {
                Unit("Chisa",  true, 100f, 0.62f, 3.0f, 12f, 0.16f, 0.31f, ArmorCategory.Sharp, 25.9f, 4.6f, false, hasTerrainAdvantage), // 突击步枪
                Unit("Denia",  true, 100f, 0.55f, 1.5f, 18f, 0.30f, 0.55f, ArmorCategory.Sharp, 12.0f, 4.6f, false, hasTerrainAdvantage), // 霰弹
                Unit("老王",   true, 100f, 0.48f, 6.0f,  6f, 0.09f, 0.18f, ArmorCategory.Sharp, 20.0f, 4.6f, false, hasTerrainAdvantage), // 冲锋枪
                Unit("阿花",   true, 100f, 0.70f, 1.0f, 22f, 0.22f, 0.40f, ArmorCategory.Sharp, 40.0f, 4.6f, false, hasTerrainAdvantage), // 狙击
            };
        }

        /// <summary>Manhunters：一群猎杀人类的动物冲锋（近战 ⇒ 交火距离会被拉到近身）。</summary>
        public static CombatScene Manhunters()
        {
            CombatScene s = new CombatScene
            {
                OurPriority = TargetPriority.Strongest,
                EnemyPriority = TargetPriority.Weakest,
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.CasualtyFraction, CasualtyFraction = 0.34f },
            };
            foreach (CombatUnitSnapshot u in OurSquad()) s.Add(u);

            // 8 只野兽：近战、几乎无护甲、跑得快 —— 它们会把距离一路拉到 1.5
            for (int i = 1; i <= 8; i++)
                s.Add(Unit("野兽" + i, false, 80f, 0.45f, 1.0f, 8f, 0.05f, 0.05f, ArmorCategory.Sharp,
                           25f, 5.5f, true, false));
            return s;
        }

        /// <summary>
        /// Outpost：5 名海盗守卫。
        /// **地形优势完全由 §19.20 的空间机制表达**：海盗有工事（射程满额 25），
        /// 我方进攻（射程 ×0.6 = 15）⇒ 他们能在我们还够不着的时候先开火。
        /// 因此这里**不再需要** `EnemyOutputFactor = 1.15` 那种粗略补偿。
        /// </summary>
        public static CombatScene Outpost()
        {
            CombatScene s = new CombatScene
            {
                OurPriority = TargetPriority.Strongest,
                EnemyPriority = TargetPriority.Weakest,
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.CasualtyFraction, CasualtyFraction = 0.34f },
            };
            foreach (CombatUnitSnapshot u in OurSquad(false)) s.Add(u);

            for (int i = 1; i <= 5; i++)
                s.Add(Unit("海盗" + i, false, 90f, 0.40f, 2.0f, 9f, 0.14f, 0.35f, ArmorCategory.Sharp,
                           24f, 4.5f, false, true));   // 有工事 = 有地形优势
            return s;
        }

        /// <summary>SleepingMechanoids：休眠机械族 —— 被偷袭的代价用输出折扣表示（地形中立）。</summary>
        public static CombatScene SleepingMechanoids()
        {
            CombatScene s = new CombatScene
            {
                OurPriority = TargetPriority.Strongest,
                EnemyPriority = TargetPriority.Weakest,
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.CasualtyFraction, CasualtyFraction = 0.34f },
                EnemyOutputFactor = 0.20f,          // 偷袭：它们还在休眠，前段基本白打
            };
            foreach (CombatUnitSnapshot u in OurSquad(true)) s.Add(u);

            for (int i = 1; i <= 3; i++)
                s.Add(Unit("机械族" + i, false, 150f, 0.50f, 3.0f, 12f, 0.22f, 0.50f, ArmorCategory.Sharp,
                           24f, 3.5f, false, true));
            return s;
        }

        /// <summary>Turrets：固定火力阵地（§18.3 已判 ForbidAbstract，此处用作"必败局"压力测试）。</summary>
        public static CombatScene Turrets()
        {
            CombatScene s = new CombatScene
            {
                Retreat = new RetreatPolicy { Kind = RetreatPolicyKind.OnAnyDown },
            };
            foreach (CombatUnitSnapshot u in OurSquad(false)) s.Add(u);

            for (int i = 1; i <= 4; i++)
                s.Add(Unit("迷你炮塔" + i, false, 200f, 0.60f, 4.0f, 9f, 0.18f, 0.60f, ArmorCategory.Sharp,
                           25f, 0f, false, true));   // 炮塔不移动
            return s;
        }

        /// <summary>
        /// 一支"注定要输"的编队，专用于验证撤退语义：敌方输出被拉高到足以在
        /// 我方全灭之前先打倒人。撤退策略的差异只有在这种局里才看得出来。
        /// </summary>
        public static CombatScene LosingFight(RetreatPolicyKind kind)
        {
            CombatScene s = Outpost();
            s.Retreat.Kind = kind;
            for (int i = 0; i < s.Units.Count; i++)
                if (!s.Units[i].IsMine) s.Units[i].DamagePerShot *= 3.5f;
            return s;
        }

        /// <summary>不可击穿：护甲远高于破甲 ⇒ 应当打到回合上限（Timeout）。</summary>
        public static CombatScene Invulnerable()
        {
            CombatScene s = new CombatScene { MaxRounds = 30 };
            s.Add(Unit("我", true, 100f, 0.9f, 3f, 10f, 0f, 0f, ArmorCategory.Sharp, 25f, 4.5f, false, true));
            s.Add(Unit("铁罐头", false, 100f, 0f, 0f, 0f, 0f, 2f, ArmorCategory.Sharp, 25f, 0f, false, true));
            return s;
        }

        /// <summary>压倒性优势：应当零伤亡胜利。</summary>
        public static CombatScene Overwhelming()
        {
            CombatScene s = new CombatScene();
            for (int i = 1; i <= 8; i++)
                s.Add(Unit("老兵" + i, true, 100f, 0.9f, 4f, 20f, 0.5f, 0.9f, ArmorCategory.Sharp, 25f, 4.5f, false, true));
            s.Add(Unit("落单野狗", false, 40f, 0.4f, 1f, 5f, 0f, 0f, ArmorCategory.Sharp, 25f, 5.5f, true, false));
            return s;
        }

        public static Dictionary<string, Func<CombatScene>> All()
        {
            return new Dictionary<string, Func<CombatScene>>
            {
                { "manhunters", Manhunters },
                { "outpost", Outpost },
                { "sleepingmechs", SleepingMechanoids },
                { "turrets", Turrets },
                { "invulnerable", Invulnerable },
                { "overwhelming", Overwhelming },
            };
        }
    }
}
