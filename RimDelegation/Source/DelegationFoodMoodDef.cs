using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一条分类规则。三个字段都可以单独用，但**同一行里只该填一个**。
    ///
    /// 刻意做成**顶层类**而不是 `DelegationFoodMoodDef` 的嵌套类：
    /// 原版 Def 里的集合元素类型几乎都是顶层类，这样最不容易在
    /// `DirectXmlToObject` 实例化元素时出意外（本机无法启动游戏验证，取保守写法）。
    /// </summary>
    public class DelegationFoodMoodEntry
    {
        /// <summary>按原版味觉想法精确匹配（最高优先级）。</summary>
        public ThoughtDef tasteThought;

        /// <summary>按原版"可接受度"档位匹配。</summary>
        public FoodPreferability preferability = FoodPreferability.Undefined;

        /// <summary>命中后挂第几个阶段。</summary>
        public int stage;
    }

    /// <summary>
    /// 「野外伙食」：委派期间吃饭时额外挂的一条记忆型心情（§19.25）。
    ///
    /// **为什么是"额外"而不是从零加**：原版在车队里**已经**会按 `tasteThought` 给餐食心情
    /// （`Thing.Ingested` → `FoodUtility.ThoughtsFromIngesting`）：
    /// 奢华餐 +12 / 精致餐 +5 / 生食 -7 / 干粮 -12，
    /// 而且这些记忆的 `stackLimit` 默认 1 ⇒ 只刷新不叠加。
    /// 所以这条 Def 做的是**在原版之上再加一层委派专属的余味**，
    /// 目的不是替代原版，而是把它变成"可以规划的东西"（UI 里会预告）。
    ///
    /// 分类走原版自己的两级信息，不硬编码食物清单：
    ///   ① `IngestibleProperties.tasteThought`（与原版心情完全一致：奢华/精致/干粮/生食）
    ///   ② `IngestibleProperties.preferability`（兜住没有 tasteThought 的：营养膏、简单餐、肉干）
    /// </summary>
    public class DelegationFoodMoodDef : Def
    {
        /// <summary>要挂的记忆型 ThoughtDef（阶段数由它决定）。</summary>
        public ThoughtDef thought;

        /// <summary>分类表：命中第一条匹配的 entry。</summary>
        public List<DelegationFoodMoodEntry> entries = new List<DelegationFoodMoodEntry>();

        /// <summary>什么都没命中时用哪个阶段（默认 1 = 中性）。</summary>
        public int defaultStage = 1;

        /// <summary>这份食物对应哪个阶段。永远返回合法下标。</summary>
        public int StageFor(ThingDef foodDef)
        {
            if (foodDef?.ingestible == null)
            {
                return ClampStage(defaultStage);
            }
            ThoughtDef taste = foodDef.ingestible.tasteThought;

            // ① 味觉想法优先 —— 它和原版给的心情是同一条依据，不会出现"原版说好吃、我们说难吃"
            if (taste != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    DelegationFoodMoodEntry e = entries[i];
                    if (e?.tasteThought != null && e.tasteThought == taste)
                    {
                        return ClampStage(e.stage);
                    }
                }
            }

            // ② 其次按可接受度档位（营养膏 MealAwful、简单餐 MealSimple、肉干 MealSimple…）
            FoodPreferability pref = foodDef.ingestible.preferability;
            for (int i = 0; i < entries.Count; i++)
            {
                DelegationFoodMoodEntry e = entries[i];
                if (e != null && e.tasteThought == null && e.preferability != FoodPreferability.Undefined
                    && e.preferability == pref)
                {
                    return ClampStage(e.stage);
                }
            }

            return ClampStage(defaultStage);
        }

        /// <summary>某个阶段的心情值。阶段越界或 thought 缺失都返回 0。</summary>
        public float MoodOfStage(int stage)
        {
            if (thought?.stages == null || thought.stages.Count == 0)
            {
                return 0f;
            }
            return thought.stages[ClampStage(stage)].baseMoodEffect;
        }

        private int ClampStage(int stage)
        {
            if (thought?.stages == null || thought.stages.Count == 0)
            {
                return 0;
            }
            return Mathf.Clamp(stage, 0, thought.stages.Count - 1);
        }
    }
}
