using System;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 纯 C# 确定性随机源（xorshift128）。
    ///
    /// 用于原型自测、离线复演战斗，以及将来在游戏内跑"预告蒙特卡洛"时
    /// 作为不触碰全局随机序列的一条可选路径。
    ///
    /// 关键性质：给定同一种子，序列完全可复现 —— 这是"战报可重演"与
    /// "预告/结算同源"两条设计承诺的基础（DESIGN.md §19.12 / §19.16.5）。
    /// </summary>
    public sealed class XorShiftRng : IRng
    {
        private uint x, y, z, w;

        public XorShiftRng(int seed)
        {
            // splitmix32 展开种子，避免低位种子导致前几个输出相关性过强
            uint s = unchecked((uint)seed);
            if (s == 0) s = 0x9E3779B9u;
            x = SplitMix32(ref s);
            y = SplitMix32(ref s);
            z = SplitMix32(ref s);
            w = SplitMix32(ref s);
            if ((x | y | z | w) == 0) x = 0x1234567u;
        }

        private static uint SplitMix32(ref uint state)
        {
            unchecked
            {
                state += 0x9E3779B9u;
                uint zz = state;
                zz = (zz ^ (zz >> 16)) * 0x85EBCA6Bu;
                zz = (zz ^ (zz >> 13)) * 0xC2B2AE35u;
                return zz ^ (zz >> 16);
            }
        }

        private uint NextUInt()
        {
            unchecked
            {
                uint t = x ^ (x << 11);
                x = y; y = z; z = w;
                w = w ^ (w >> 19) ^ t ^ (t >> 8);
                return w;
            }
        }

        public float Value
        {
            get
            {
                // 取高 24 位映射到 [0,1)，与 float 尾数精度匹配
                uint v = NextUInt() >> 8;
                return v / 16777216f;
            }
        }

        public bool Chance(float p)
        {
            if (p <= 0f) return false;
            if (p >= 1f) return true;
            return Value < p;
        }

        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            uint span = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % span);
        }
    }
}
