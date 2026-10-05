using System;
using System.Collections.Generic;
using RimDelegation.Combat;

namespace RimDelegation.CombatLab
{
    /// <summary>单位模板 —— 只提供一组初始数值，加入编队后每一项都可以单独改。</summary>
    public sealed class LabUnitTemplate
    {
        public string Name;
        public string Note;

        // 生存
        public float Hp;

        // 空间
        public float Range;
        public float MoveSpeed;
        public bool IsMelee;
        public bool HasTerrainAdvantage;

        // 命中（中距离基准；近/远由校准规则导出）
        public float Hit;

        // 输出
        public float Shots, Dmg, Ap, Armor;
        public ArmorCategory Cat;

        /// <summary>
        /// 由中距离命中率导出近端命中率。系数 1.25 与远端的 0.55 配对，
        /// 使距离 ≈15 格时命中率 ≈ Hit —— 与旧版单一命中率的标定值可比。
        /// </summary>
        public float AccuracyNear => Math.Min(1f, Hit * 1.25f);
        public float AccuracyFar => Hit * 0.55f;

        public LabUnitTemplate(string name, string note, float hp, float hit, float shots,
                               float dmg, float ap, float armor, ArmorCategory cat,
                               float range = 25f, float moveSpeed = 4.5f,
                               bool isMelee = false, bool hasTerrainAdvantage = true)
        {
            Name = name; Note = note; Hp = hp; Hit = hit; Shots = shots;
            Dmg = dmg; Ap = ap; Armor = armor; Cat = cat;
            Range = range; MoveSpeed = moveSpeed;
            IsMelee = isMelee; HasTerrainAdvantage = hasTerrainAdvantage;
        }

        public override string ToString() => Name;
    }

    /// <summary>编队里的一个单位（UI 状态的载体，可编辑）。</summary>
    public sealed class LabUnit
    {
        public string Name;
        public bool IsMine;

        public float Hp = 100f;

        public float Range = 25f;
        public float MoveSpeed = 4.5f;
        public bool IsMelee;
        public bool HasTerrainAdvantage = true;

        public float AccNear = 0.75f;
        public float AccFar = 0.40f;

        public float Shots = 1f;
        public float Dmg = 10f;
        public float Ap;
        public float Armor;
        public ArmorCategory Cat = ArmorCategory.Sharp;

        public LabUnit Clone()
        {
            return (LabUnit)MemberwiseClone();
        }

        public static LabUnit FromTemplate(LabUnitTemplate t, bool mine, string nameSuffix = null)
        {
            return new LabUnit
            {
                Name = t.Name + (string.IsNullOrEmpty(nameSuffix) ? "" : nameSuffix),
                IsMine = mine,
                Hp = t.Hp,
                Range = t.Range,
                MoveSpeed = t.MoveSpeed,
                IsMelee = t.IsMelee,
                HasTerrainAdvantage = t.HasTerrainAdvantage,
                AccNear = t.AccuracyNear,
                AccFar = t.AccuracyFar,
                Shots = t.Shots,
                Dmg = t.Dmg,
                Ap = t.Ap,
                Armor = t.Armor,
                Cat = t.Cat,
            };
        }

        public CombatUnitSnapshot ToSnapshot()
        {
            return new CombatUnitSnapshot
            {
                Name = Name ?? "?",
                IsMine = IsMine,
                MaxHealth = Math.Max(1f, Hp),
                AccuracyNear = CombatMath.Clamp01(AccNear),
                AccuracyFar = CombatMath.Clamp01(AccFar),
                Range = Math.Max(0f, Range),
                MoveSpeed = Math.Max(0f, MoveSpeed),
                IsMelee = IsMelee,
                HasTerrainAdvantage = HasTerrainAdvantage,
                ShotsPerRound = Math.Max(0f, Shots),
                DamagePerShot = Math.Max(0f, Dmg),
                ArmorPen = Ap,
                ArmorRating = Armor,
                Category = Cat,
                // 索敌权重用中距离的等价命中率（近/远的中点附近），与旧语义一致
                ThreatWeight = Math.Max(0f, (AccNear + AccFar) * 0.5f * Shots * Dmg),
            };
        }
    }

    /// <summary>场景参数（对应 §19.16.7 与 §19.20 的全部待标定值 + 策略）。</summary>
    public sealed class LabSceneParams
    {
        // ── 空间 ──
        /// <summary>初始交战距离（格）。默认 40 > 常见射程 25 ⇒ 开局双方都够不着。</summary>
        public float StartDistance = 40f;
        public float MeleeRange = 1.5f;
        public float NearBand = 8f;
        /// <summary>没有地形优势的单位，射程乘以此系数（近战除外）。0.75 ≈ 射程打七五折。</summary>
        public float NoAdvantageRangeFactor = 0.75f;
        public ClosingPolicy Closing = ClosingPolicy.UntilShortestEngaged;

        /// <summary>后排最多能站多远（格）。0 = 退化成单标量距离模型。</summary>
        public float MaxStandoff = 12f;

        /// <summary>后撤速度系数（前进是全速）。</summary>
        public float FallbackFactor = 0.5f;

        /// <summary>纵深策略：什么时候后撤。</summary>
        public StandoffPolicy Standoff = StandoffPolicy.OnlyIfOutranging;

        // ── 其余 ──
        /// <summary>场景级掩体通过率（对称，替代 vanilla 的 PassCoverChance）。</summary>
        public float CoverFactor = 1.0f;
        public float EnemyOutputFactor = 1.0f;
        public float DisengageFactor = 0.5f;
        public float DownHealthFraction = 0.25f;
        public int TicksPerRound = 250;
        public int MaxRounds = 240;

        public TargetPriority OurPriority = TargetPriority.Strongest;
        public TargetPriority EnemyPriority = TargetPriority.Weakest;

        public RetreatPolicyKind RetreatKind = RetreatPolicyKind.CasualtyFraction;
        public float RetreatFraction = 0.34f;

        /// <summary>天气预设下标 —— 见 <see cref="LabWeather"/>。0 = 无天气。</summary>
        public int WeatherPreset;

        public LabSceneParams Clone() => (LabSceneParams)MemberwiseClone();
    }

    /// <summary>
    /// 天气预设。命中率乘子直接取自 vanilla <c>WeatherDef.accuracyMultiplier</c>，
    /// 权重取自 <c>BiomeDef.baseWeatherCommonalities</c>。
    /// </summary>
    public static class LabWeather
    {
        public sealed class Preset
        {
            public string Name;
            public string Note;
            public WeatherSample[] Table;

            public override string ToString() => Name;
        }

        private static WeatherSample W(string n, float acc, float w) => new WeatherSample(n, acc, w);

        public static List<Preset> All()
        {
            return new List<Preset>
            {
                new Preset { Name = "无天气（×1.00）", Note = "基线，用于与旧标定值对比", Table = new WeatherSample[0] },
                new Preset
                {
                    Name = "温带森林（期望 ×0.90）",
                    Note = "Clear 18 / 雨雪尘 0.8 / 雾 0.5",
                    Table = new[]
                    {
                        W("Clear", 1.00f, 18f), W("Fog", 0.50f, 1f), W("Rain", 0.80f, 2f),
                        W("DryThunderstorm", 1.00f, 1f), W("RainyThunderstorm", 0.80f, 1f),
                        W("FoggyRain", 0.50f, 1f), W("SnowGentle", 0.80f, 4f),
                        W("SnowHard", 0.80f, 4f), W("GrayPall", 0.80f, 1f), W("Overcast", 1.00f, 2f),
                    },
                },
                new Preset
                {
                    Name = "沙漠（期望 ×0.93，有沙尘暴）",
                    Note = "Clear 18 / 沙尘暴 0.8 / 雨雪 0.8",
                    Table = new[]
                    {
                        W("Clear", 1.00f, 18f), W("Rain", 0.80f, 2f), W("DryThunderstorm", 1.00f, 1f),
                        W("RainyThunderstorm", 0.80f, 1f), W("SnowGentle", 0.80f, 4f),
                        W("SnowHard", 0.80f, 4f), W("GrayPall", 0.80f, 1f), W("Sandstorm", 0.80f, 1f),
                    },
                },
                new Preset
                {
                    Name = "苔原（期望 ×0.91，有暴风雪）",
                    Note = "Clear 18 / 暴风雪 0.7",
                    Table = new[]
                    {
                        W("Clear", 1.00f, 18f), W("Fog", 0.50f, 1f), W("Rain", 0.80f, 2f),
                        W("DryThunderstorm", 1.00f, 1f), W("RainyThunderstorm", 0.80f, 1f),
                        W("FoggyRain", 0.50f, 1f), W("SnowGentle", 0.80f, 4f), W("SnowHard", 0.80f, 4f),
                        W("GrayPall", 0.80f, 1f), W("Windy", 1.00f, 2f), W("Overcast", 1.00f, 2f),
                        W("Blizzard", 0.70f, 1f),
                    },
                },
                new Preset
                {
                    Name = "冰原（期望 ×0.79，多数是雪）",
                    Note = "Clear 12 / SnowGentle 20 / SnowHard 40 / 暴风雪 0.7",
                    Table = new[]
                    {
                        W("Clear", 1.00f, 12f), W("DryThunderstorm", 1.00f, 2f),
                        W("SnowGentle", 0.80f, 20f), W("SnowHard", 0.80f, 40f),
                        W("GrayPall", 0.80f, 1f), W("Windy", 1.00f, 1f),
                        W("Overcast", 1.00f, 1f), W("Blizzard", 0.70f, 2f),
                    },
                },
                new Preset
                {
                    Name = "恒定浓雾（×0.50）",
                    Note = "压力测试：命中率腰斩",
                    Table = new[] { W("Fog", 0.50f, 1f) },
                },
                new Preset
                {
                    Name = "恒定暴风雪（×0.70）",
                    Note = "压力测试",
                    Table = new[] { W("Blizzard", 0.70f, 1f) },
                },
            };
        }

        /// <summary>
        /// 按名字片段查下标。**不要在下标上硬编码** —— 早先版本用 <c>WeatherPreset = 4</c>
        /// 指"恒定浓雾"，而 4 实际是"冰原"，于是那一档静默地跑成了雪天（SnowHard×0.8），
        /// 标签与行为不符。名字查找让增删预设不会打乱引用。
        /// </summary>
        public static int IndexOf(string nameFragment)
        {
            List<Preset> all = All();
            for (int i = 0; i < all.Count; i++)
                if (all[i].Name.Contains(nameFragment)) return i;
            return 0;
        }

        public static void Apply(CombatScene scene, int presetIndex)
        {
            List<Preset> all = All();
            if (presetIndex < 0 || presetIndex >= all.Count) return;
            scene.WeatherTable.Clear();
            WeatherSample[] t = all[presetIndex].Table;
            for (int i = 0; i < t.Length; i++)
                scene.WeatherTable.Add(new WeatherSample(t[i].Name, t[i].AccuracyMultiplier, t[i].Weight));
        }
    }

    /// <summary>UI 状态 → 计算核心场景。</summary>
    public static class SceneBuilder
    {
        public static CombatScene Build(IList<LabUnit> units, LabSceneParams p, out string problem)
        {
            problem = null;
            if (units == null || units.Count == 0) { problem = "编队为空。"; return null; }

            int mine = 0, foes = 0;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].IsMine) mine++; else foes++;
            }
            if (mine == 0) { problem = "我方没有人 —— 至少要有一个我方单位。"; return null; }
            if (foes == 0) { problem = "敌方没有人 —— 至少要有一个敌方单位。"; return null; }

            CombatScene scene = new CombatScene
            {
                OurPriority = p.OurPriority,
                EnemyPriority = p.EnemyPriority,
                Retreat = new RetreatPolicy { Kind = p.RetreatKind, CasualtyFraction = p.RetreatFraction },
                TicksPerRound = Math.Max(1, p.TicksPerRound),
                MaxRounds = Math.Max(1, p.MaxRounds),
                StartDistance = Math.Max(0f, p.StartDistance),
                MeleeRange = Math.Max(0f, p.MeleeRange),
                NearBand = Math.Max(0f, p.NearBand),
                NoAdvantageRangeFactor = Math.Max(0f, p.NoAdvantageRangeFactor),
                Closing = p.Closing,
                MaxStandoff = Math.Max(0f, p.MaxStandoff),
                FallbackFactor = Math.Max(0f, p.FallbackFactor),
                Standoff = p.Standoff,
                CoverFactor = Math.Max(0f, p.CoverFactor),
                DownHealthFraction = CombatMath.Clamp01(p.DownHealthFraction),
                DisengageFactor = CombatMath.Clamp01(p.DisengageFactor),
                EnemyOutputFactor = Math.Max(0f, p.EnemyOutputFactor),
            };

            for (int i = 0; i < units.Count; i++) scene.Add(units[i].ToSnapshot());
            LabWeather.Apply(scene, p.WeatherPreset);
            return scene;
        }
    }
}
