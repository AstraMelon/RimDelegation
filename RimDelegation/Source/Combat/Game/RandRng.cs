using System;
using Verse;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 把 vanilla <c>Rand</c> 包成计算核心用的 <see cref="IRng"/>。
    ///
    /// **为什么要用 Rand 而不是自建 RNG**：本算法在生产路径上会调用内部使用
    /// <c>Rand</c> 的 vanilla 函数（例如 <c>ArmorUtility</c>）。自建 RNG 拦不住它们，
    /// 只会让全局随机序列被静默推进。`Rand.PushState/PopState` 是 vanilla 自己的隔离机制：
    /// 期间产生的随机数完全由我们给的种子决定，退出后全局序列分毫不动。
    ///
    /// 见 DESIGN.md §19.16.5。**必须用 using/try-finally 保证 PopState 一定被调用**，
    /// 否则随机栈会泄漏，之后所有游戏内随机都会错位。
    /// </summary>
    public sealed class RandRng : IRng, IDisposable
    {
        private bool pushed;

        public RandRng(int seed)
        {
            Rand.PushState(seed);
            pushed = true;
        }

        public float Value
        {
            get { return Rand.Value; }
        }

        public bool Chance(float p)
        {
            if (p <= 0f) return false;
            if (p >= 1f) return true;
            return Rand.Chance(p);
        }

        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return Rand.Range(minInclusive, maxExclusive);
        }

        public void Dispose()
        {
            if (!pushed) return;
            pushed = false;
            Rand.PopState();
        }
    }
}
