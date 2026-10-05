using System;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 随机源接口。
    ///
    /// 这是整个战斗计算核心与游戏之间的唯一随机性通道 —— 核心只调这个接口，
    /// 绝不直接碰 <c>Verse.Rand</c> 或 <c>System.Random</c>。
    ///
    /// 两个实现：
    ///   • <see cref="XorShiftRng"/> —— 纯 C#，无依赖。原型自测与离线复演用。
    ///   • 游戏内 <c>RimDelegation.Combat.RandRng</c> —— 包一层 <c>Rand.PushState(seed)</c> /
    ///     <c>Rand.PopState()</c>。之所以必须用 Rand 而不是自建 RNG：我们要调用的一批
    ///     vanilla 函数内部就在用 Rand（例如 ArmorUtility.ApplyArmor 里的 Rand.Value），
    ///     自建 RNG 拦不住它们。PushState/PopState 是 vanilla 自己的隔离机制。
    ///
    /// 见 DESIGN.md §19.16.5。
    /// </summary>
    public interface IRng
    {
        /// <summary>[0, 1) 均匀分布。对应 vanilla 的 <c>Rand.Value</c>。</summary>
        float Value { get; }

        /// <summary>以概率 <paramref name="p"/> 返回 true。p 会被 Clamp01。</summary>
        bool Chance(float p);

        /// <summary>[minInclusive, maxExclusive) 均匀整数。</summary>
        int Range(int minInclusive, int maxExclusive);
    }

    public static class RngExtensions
    {
        /// <summary>带边界的均匀浮点，语义对齐 <c>Rand.Range(min, max)</c>。</summary>
        public static float Range(this IRng rng, float min, float max)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            return min + (max - min) * rng.Value;
        }

        /// <summary>Fisher-Yates 洗牌。用于每回合的行动顺序（原地打乱传入的列表）。</summary>
        public static void Shuffle<T>(this IRng rng, System.Collections.Generic.IList<T> list)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (list == null) return;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Range(0, i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
