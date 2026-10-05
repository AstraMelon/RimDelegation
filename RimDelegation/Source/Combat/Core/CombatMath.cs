using System;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 战斗计算的全部纯数学部分。**不引用任何游戏类型**，因此可在独立控制台工程中
    /// 直接编译与测试（见 Prototype/RimDelegation.Prototype.csproj）。
    ///
    /// 设计原则（DESIGN.md §19.16）：能复用 vanilla 的就复用，只建模必须建模的。
    /// 本文件只负责 vanilla 复用的那两个函数无法覆盖的部分：
    ///   • 护甲分布的**解析形式**（vanilla 的实现是 if + Rand.Value，我们用它的等价分布）
    ///   • 单调插值之类的零碎工具
    /// 凡是能从 vanilla 读的数值（命中率、每回合射击数、伤害、破甲）都由游戏适配层
    /// 算好后填进 CombatUnitSnapshot，本文件不重复推导。
    /// </summary>
    public static class CombatMath
    {        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

        /// <summary>
        /// vanilla 的随机取整：<c>GenMath.RoundRandom(f)</c> 的忠实实现 ——
        /// 整数部分直接取，小数部分以该概率进位。用于护甲减半那一支。
        /// </summary>
        public static int RoundRandom(IRng rng, float value)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (value <= 0f) return 0;
            int num = (int)value;
            float frac = value - num;
            if (frac > 0f && rng.Chance(frac)) num++;
            return num;
        }

        /// <summary>
        /// 护甲三档概率。**这是本方案的关键推导。**
        ///
        /// vanilla <c>ArmorUtility.ApplyArmor</c> 的本体（反编译）：
        /// <code>
        /// float num   = Mathf.Max(armorRating - armorPenetration, 0f);
        /// float value = Rand.Value;                       // 均匀 [0,1)
        /// if      (value &lt; num * 0.5f) damAmount = 0f;                       // 弹开
        /// else if (value &lt; num)        damAmount = RoundRandom(damAmount/2); // 减半
        ///                                                                     // 否则全额
        /// </code>
        ///
        /// 因为 <c>Rand.Value</c> 是均匀分布，这三档的概率是**解析可得**的，不需要采样：
        ///   pDeflect = Clamp01(num * 0.5)
        ///   pHalf    = Clamp01(num) - pDeflect
        ///   pFull    = 1 - pDeflect - pHalf
        ///
        /// num &gt; 1（护甲远高于破甲）时全部落进前两档，永不全额 —— 与 vanilla 一致。
        /// </summary>
        public static void ArmorProbabilities(float armorRating, float armorPenetration,
                                              out float pDeflect, out float pHalf, out float pFull)
        {
            float num = armorRating - armorPenetration;
            if (num < 0f) num = 0f;

            pDeflect = Clamp01(num * 0.5f);
            pHalf = Clamp01(num) - pDeflect;
            pFull = 1f - pDeflect - pHalf;

            // 浮点兜底，保证三者和恒为 1
            if (pFull < 0f) pFull = 0f;
            float sum = pDeflect + pHalf + pFull;
            if (sum > 0f && Math.Abs(sum - 1f) > 1e-5f)
            {
                pDeflect /= sum; pHalf /= sum; pFull /= sum;
            }
        }

        /// <summary>
        /// 一次命中后的期望伤害（解析值，不掷骰、不采样）。
        /// 注意：忽略 RoundRandom 的取整噪声（期望偏差 &lt; 0.5 点），测试里对此留容差。
        /// </summary>
        public static float ExpectedPostArmorDamage(float amount, float armorRating, float armorPenetration)
        {
            if (amount <= 0f) return 0f;
            float pDeflect, pHalf, pFull;
            ArmorProbabilities(armorRating, armorPenetration, out pDeflect, out pHalf, out pFull);
            return amount * pFull + (amount * 0.5f) * pHalf;
        }

        /// <summary>一次命中后的实际伤害（按解析分布掷一次）。</summary>
        public static float RollPostArmorDamage(IRng rng, float amount, float armorRating, float armorPenetration)
        {
            ArmorRollResult ignored;
            return RollPostArmorDamage(rng, amount, armorRating, armorPenetration, out ignored);
        }

        /// <summary>
        /// 一次命中后的实际伤害，并返回**护甲判定的档位** ——
        /// 战斗日志要如实记录"这次是被弹开了、减半了、还是全额吃下"（甲弹对抗）。
        /// </summary>
        public static float RollPostArmorDamage(IRng rng, float amount, float armorRating,
                                                float armorPenetration, out ArmorRollResult result)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (amount <= 0f) { result = ArmorRollResult.None; return 0f; }

            float pDeflect, pHalf, pFull;
            ArmorProbabilities(armorRating, armorPenetration, out pDeflect, out pHalf, out pFull);

            float v = rng.Value;
            if (v < pDeflect) { result = ArmorRollResult.Deflected; return 0f; }
            if (v < pDeflect + pHalf) { result = ArmorRollResult.Halved; return amount * 0.5f; }
            result = ArmorRollResult.Full;
            return amount;
        }

        /// <summary>
        /// **参照实现**：逐字转写 vanilla <c>ArmorUtility.ApplyArmor</c> 的分支链。
        /// 只用于自测 —— 拿它 100 万次采样出的分布，去校验
        /// <see cref="ArmorProbabilities"/> 与 <see cref="ExpectedPostArmorDamage"/> 的解析值。
        /// 生产路径不调用本方法。
        /// </summary>
        public static float ReferenceVanillaPostArmorDamage(IRng rng, float amount, float armorRating, float armorPenetration)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            float num = armorRating - armorPenetration;
            if (num < 0f) num = 0f;

            float value = rng.Value;
            if (value < num * 0.5f) return 0f;
            if (value < num) return RoundRandom(rng, amount / 2f);
            return amount;
        }
    }
}
