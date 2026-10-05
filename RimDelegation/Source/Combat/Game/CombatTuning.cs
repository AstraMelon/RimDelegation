using System;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 游戏侧接入的全部标定量集中在这里 —— 每一处都是"没有地图就算不出来、只能设定"的东西，
    /// 对应 DESIGN.md §19.19 的未知量清单。
    ///
    /// 放在一个类里而不是散落各处，是为了让预告面板能把它们**原样摊给玩家看**（§19.19.7）。
    /// </summary>
    public static class CombatTuning
    {
        // ── 时间与空间 ──────────────────────────────────────────────────

        /// <summary>一个回合多少 ticks。250 ticks = 6 秒，与 mod 现有的营养/休息模型一致。</summary>
        public const int TicksPerRound = 250;

        /// <summary>战斗的回合上限。240 回合 = 60000 ticks = 游戏内 1 天。</summary>
        public const int MaxRounds = 240;

        /// <summary>初始交战距离（格）。大于常见武器射程（约 25）⇒ 开局双方都够不着，必须先接近。</summary>
        public const float StartDistance = 40f;

        /// <summary>近战的有效射程（格）。</summary>
        public const float MeleeRange = 1.5f;

        /// <summary>命中率两点插值的近端距离（格）。</summary>
        public const float NearBand = 8f;

        // ── 地形与纵深 ──────────────────────────────────────────────────

        /// <summary>没有地形优势的单位，射程乘以此系数（近战除外）。</summary>
        public const float NoAdvantageRangeFactor = 0.75f;

        /// <summary>后排最多能站多远（格）。</summary>
        public const float MaxStandoff = 12f;

        /// <summary>后撤速度系数（前压为 1.0）。</summary>
        public const float FallbackFactor = 0.5f;

        /// <summary>
        /// **在敌方火力下推进的速度折损** —— vanilla <c>StatDefOf.MoveSpeed</c> 是"格/秒"，
        /// 全速 4.6 格/秒 = 每 6 秒回合 27.6 格，那是飞奔穿越空地，不是交火中的推进。
        /// 实测标定为全速的 15%（约 0.7 格/秒），与原型里手写的 4.5 格/回合吻合。
        /// </summary>
        public const float AdvanceCautionFactor = 0.15f;

        /// <summary>命中率插值用的标准目标体积 —— 真 pawn 的 BodySize 见 §19.19.3⑧（尚未按目标取）。</summary>
        public const float ReferenceTargetBodySize = 1.0f;

        // ── 状态与伤害 ──────────────────────────────────────────────────

        /// <summary>倒地被判定的耐久比例阈值（替代 vanilla 的疼痛/部位/失血模型）。</summary>
        public const float DownHealthFraction = 0.25f;

        /// <summary>撤退"脱离接触"轮的输出折扣。</summary>
        public const float DisengageFactor = 0.5f;

        /// <summary>
        /// 耐久池的基准值：<c>baseHealthScale × 100</c>。
        /// vanilla 的耐久分布在身体部位上，没有单一 HP 数字，这个换算是替代模型的入口。
        /// </summary>
        public const float HealthPoolPerScale = 100f;

        // ── 预告 ────────────────────────────────────────────────────────

        /// <summary>预告的蒙特卡洛次数。</summary>
        public const int ForecastIterations = 200;

        /// <summary>把"格/秒"的移动速度折算成"格/回合"（含推进谨慎度）。</summary>
        public static float CellsPerRound(float cellsPerSecond)
        {
            float secondsPerRound = TicksPerRound / 60f;
            return Math.Max(0f, cellsPerSecond) * secondsPerRound * AdvanceCautionFactor;
        }

        /// <summary>给玩家看的"已知假设"清单 —— 预告面板会原样显示（§19.19.7 第 2 条）。</summary>
        public static string[] Assumptions()
        {
            return new[]
            {
                "交战距离按 " + StartDistance.ToString("0") + " 格起算，双方都够不着，先接近",
                "推进速度 = 全速 × " + AdvanceCautionFactor.ToString("P0") + "（火力下的谨慎推进）",
                "无地形优势方射程 ×" + NoAdvantageRangeFactor.ToString("0.##") + "（近战除外）",
                "后排纵深上限 " + MaxStandoff.ToString("0") + " 格，后撤速度为前压的 " + FallbackFactor.ToString("0.##") + " 倍",
                "命中率随距离线性衰减（近端 " + NearBand.ToString("0") + " 格 → 有效射程）",
                "掩体按整片战场统一折损（不是逐目标）",
                "天气按生物群系的天气表逐场随机抽取",
                "倒地按耐久 " + DownHealthFraction.ToString("P0") + " 判定（替代疼痛/部位/失血）",
                "不模拟：视线、屋顶、烟雾、姿态、走位、爆炸溅射",
            };
        }
    }
}
