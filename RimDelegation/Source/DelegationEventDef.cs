using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派期间的随机事件（DESIGN.md §19.24）。
    ///
    /// 数据驱动：每个事件是一条 Def，`Apply` 返回一句结果描述，由宿主组件统一发信。
    /// 判定是"每游戏内 1 小时掷一次"，单项触发概率 ≈ 1 / (mtbDays × 24)，
    /// 且每个事件有自己的 `minDaysBetween` 冷却（记在 Delegation 上，会存档）。
    ///
    /// **这是 demo**：事件类型只做了 4 种，目的是把"开采期间世界不是静止的"这件事跑通，
    /// 而不是穷举内容。
    /// </summary>
    public class DelegationEventDef : Def
    {
        /// <summary>平均多少天发生一次（按工时段计）。</summary>
        public float mtbDays = 3f;

        /// <summary>
        /// 分级（S15）：`Flavor` 只留痕、`Effect` 有实质影响。
        /// 默认 `Effect`（有影响）—— 因为现有事件全是机制事件，漏配时按"有影响"处理更安全。
        /// </summary>
        public DelegationEventSeverity severity = DelegationEventSeverity.Effect;

        /// <summary>相对权重（同一小时内多项竞争时的倍率）。</summary>
        public float weight = 1f;

        /// <summary>同一事件两次之间的最短间隔（天）。</summary>
        public float minDaysBetween = 1f;

        /// <summary>发信用的 LetterDef；留空 = NeutralEvent。</summary>
        public LetterDef letterDef;

        public string letterLabel;

        /// <summary>信件正文；留空则只用 <see cref="Apply"/> 返回的描述。</summary>
        public string letterText;

        /// <summary>是否消耗"按天数收工"的预算。暂停不消耗，事故消耗。</summary>
        public bool consumesDayBudget = true;

        /// <summary>
        /// **静态闸门**：这条事件只对列出的委派生效（空 = 不限，向后兼容）。
        ///
        /// ⚠️ 为什么必须有它：`WorldObjectComp_Delegations.RollEvents` 遍历的是
        /// **全部** DelegationEventDef。在只有 CanFire 判定的年代，采矿专属的
        /// "塌方 / 富矿脉"会原样落到物资藏匿点、工作站点等任意委派上 —— 而
        /// 富矿脉还会 `d.deposit.totalUnits += n` 与 `d.totalCells += n`，
        /// 现场却**不会真的多出东西**，于是 `cellsMined` 永远追不上 `totalCells`：
        /// 委派永不结束、地点永不销毁、玩家进图时原版还会重掷一份新物资（双吃）。
        ///
        /// 这里管"内容归属"（哪些事件属于哪种活），
        /// <see cref="DelegationWorker.AllowsScaleIncrease"/> 管"结构不变量"（能不能被加规模）。
        /// 两道都要，缺一不可。
        /// </summary>
        [NoTranslate]
        public List<DelegationDef> onlyForDelegationDefs = new List<DelegationDef>();

        // ── S23：效果原语 + 条件原语（把机制从 C# 搬进 XML）──────────────────

        /// <summary>
        /// 这条事件**做了什么**：一串效果原语，按顺序执行，各返回一句话（宿主拼起来当事件描述）。
        ///
        /// 为什么要有它：在此之前每条有机制的事件都是一个 C# 子类（塌方 / 富矿脉 / 受挫 /
        /// 闹别扭 / 工伤），改一个数字都要退游戏重新编译部署。拆成原语后，"塌方"=
        /// `Effect_LoseOre` + `Effect_Stall` 两条，写在 XML 里。
        ///
        /// 写法（内联多态，与 `Patches/RimDelegation_SiteComps.xml` 追加 comps 同款）：
        /// <code>
        /// &lt;effects&gt;
        ///   &lt;li Class="RimDelegation.DelegationEffectDef_LoseOre"&gt;&lt;fraction&gt;0.2~0.5&lt;/fraction&gt;&lt;/li&gt;
        /// &lt;/effects&gt;
        /// </code>
        /// 空 = 退回 <see cref="flavorLines" /> 那条纯叙事路径（老写法继续有效）。
        /// </summary>
        public List<DelegationEffectDef> effects = new List<DelegationEffectDef>();

        /// <summary>全部满足才允许触发。空 = 无条件。</summary>
        public List<DelegationConditionDef> conditions = new List<DelegationConditionDef>();

        /// <summary>任一满足即禁止触发（写否定条件用 —— 比"全满足的否定"好读）。</summary>
        public List<DelegationConditionDef> blockers = new List<DelegationConditionDef>();

        /// <summary>相乘成动态概率倍率（工伤的疲劳曲线就是一条这个）。空 = ×1。</summary>
        public List<DelegationConditionDef> chanceMultipliers = new List<DelegationConditionDef>();

        /// <summary>前缀条件与后缀条件的清单（XML 解析期由 <see cref="ResolveReferences" /> 补全）。</summary>
        private static bool AllMet(List<DelegationConditionDef> list, Delegation d, Site site)
        {
            if (list == null) return true;
            for (int i = 0; i < list.Count; i++)
            {
                DelegationConditionDef c = list[i];
                if (c != null && !c.IsMet(d, site)) return false;
            }
            return true;
        }

        private static bool AnyMet(List<DelegationConditionDef> list, Delegation d, Site site)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                DelegationConditionDef c = list[i];
                if (c != null && c.IsMet(d, site)) return true;
            }
            return false;
        }

        /// <summary>
        /// 这条事件能不能作用于这次委派 —— 与委派当前状态无关的静态闸门。
        /// 动态条件（有没有已采矿石、人够不够）仍然写在 <see cref="CanFire"/> 里。
        ///
        /// 调用点：<see cref="WorldObjectComp_Delegations.RollEvents"/> 在 CanFire 之前先问它。
        /// ⚠️ DEV 按钮（`DevFireEvent`）会绕过这两道闸门直接 `Apply`，
        ///    所以破坏性的 <see cref="Apply"/> 实现还必须自己再判一次。
        /// </summary>
        public virtual bool AppliesTo(Delegation d, Site site)
        {
            if (d == null || d.def == null)
            {
                return false;
            }
            if (!onlyForDelegationDefs.NullOrEmpty() && !onlyForDelegationDefs.Contains(d.def))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 此刻的动态触发倍率（默认 1）。
        ///
        /// 基类只返回 1；子类可以按委派的实际状态放大/缩小概率。
        /// 目前唯一的用处是 <see cref="DelegationEventDef_PawnAccident"/>：
        /// 疲劳会显著抬高工伤概率（§19.25）—— 这是长工时模式**唯一真实的生理代价**，
        /// 因为 `NeedRest` 的疲劳心情在车队里不生效（见 `DelegationUtility` 的注释）。
        /// </summary>
        public virtual float ChanceMultiplier(Delegation d)
        {
            // S23：基类改成"把 chanceMultipliers 乘起来"。老派生类仍然 override 它（向后兼容）。
            if (chanceMultipliers.NullOrEmpty())
            {
                return 1f;
            }
            float f = 1f;
            for (int i = 0; i < chanceMultipliers.Count; i++)
            {
                DelegationConditionDef c = chanceMultipliers[i];
                if (c != null) f *= c.Factor(d);
            }
            return f;
        }

        /// <summary>能不能在此刻触发（例如"没有已采矿石"时塌方就没意义）。</summary>
        public virtual bool CanFire(Delegation d, Site site)
        {
            // S23：基类改成"conditions 全满足 + blockers 全不满足"。老派生类仍然 override 它。
            if (conditions.NullOrEmpty() && blockers.NullOrEmpty())
            {
                return true;   // 两个字段都空 = 无条件 —— 与加这两个字段之前逐字一致
            }
            return d != null && AllMet(conditions, d, site) && !AnyMet(blockers, d, site);
        }

        /// <summary>
        /// 纯叙事事件的台词池（S15）。`Flavor` 事件不需要任何机制：
        /// 在这里写几句话，基类的 <see cref="Apply"/> 会随机挑一条当事件描述。
        /// </summary>
        public List<string> flavorLines = new List<string>();

        /// <summary>
        /// 执行事件，返回结果描述（会进事件留痕 = 流程块那一行 / 结束报告 / 历史记录）。
        ///
        /// S23 起基类实现分两路：
        ///   · 配了 <see cref="effects" /> ⇒ 按顺序执行，各取一句话拼起来；
        ///   · 没有 ⇒ 从 <see cref="flavorLines" /> 随机挑一条（纯叙事事件的老路径）。
        /// </summary>
        public virtual string Apply(Delegation d, Site site)
        {
            if (effects.NullOrEmpty())
            {
                return flavorLines.NullOrEmpty() ? null : flavorLines.RandomElement();
            }

            // ⚠️ DEV 按钮（`DevFireEvent`）绕过 CanFire/AppliesTo 直接调这里，
            //    所以破坏性效果前**再查一次** blockers —— 与旧类在自己 Apply 里复检同一个道理。
            if (AnyMet(blockers, d, site))
            {
                return "（前提不成立，这次事件被略过了）";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < effects.Count; i++)
            {
                DelegationEffectDef e = effects[i];
                if (e == null) continue;
                string line;
                try
                {
                    line = e.Apply(d, site);
                }
                catch (Exception ex)
                {
                    line = "（" + (e.GetType().Name) + " 执行失败：" + ex.GetType().Name + "）";
                    Log.Error("[RimDelegation] 事件 " + defName + " 的效果 " + e.GetType().Name + " 抛异常：" + ex);
                }
                if (line.NullOrEmpty()) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            foreach (string bad in CheckNulls(effects, "effects", "DelegationEffectDef"))
            {
                yield return bad;
            }
            foreach (string bad in CheckNulls(conditions, "conditions", "DelegationConditionDef"))
            {
                yield return bad;
            }
            foreach (string bad in CheckNulls(blockers, "blockers", "DelegationConditionDef"))
            {
                yield return bad;
            }
            foreach (string bad in CheckNulls(chanceMultipliers, "chanceMultipliers", "DelegationConditionDef"))
            {
                yield return bad;
            }
        }

        /// <summary>清单里出现 null（`Class="…"` 写错类型名时 XML 加载器多半会直接报错，这里是二道闸）。</summary>
        private static IEnumerable<string> CheckNulls<T>(List<T> list, string field, string expectBase)
        {
            if (list == null) yield break;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null)
                {
                    yield return string.Format("{0}[{1}] 解析不到对象（Class 写错了？应为 {2} 的子类）", field, i, expectBase);
                }
            }
        }
    }

    /// <summary>塌方：埋掉一部分**已采但尚未交付**的矿石，并让作业停顿一段时间。</summary>
    public class DelegationEventDef_CaveIn : DelegationEventDef
    {
        public FloatRange lostFraction = new FloatRange(0.2f, 0.5f);
        public FloatRange stallHours = new FloatRange(1f, 4f);

        public override bool CanFire(Delegation d, Site site)
        {
            // 没有已采矿石时，"埋掉一部分"没有意义
            return d != null && d.oreUnits >= 5f;
        }

        public override string Apply(Delegation d, Site site)
        {
            int lost = Mathf.FloorToInt(d.oreUnits * lostFraction.RandomInRange);
            if (lost <= 0) lost = Mathf.Min(1, Mathf.FloorToInt(d.oreUnits));
            d.oreUnits = Mathf.Max(0f, d.oreUnits - lost);

            float hours = stallHours.RandomInRange;
            d.StallFor(hours);

            return "矿洞顶板塌了一块，已经采出但还没运回车上的一部分矿石被埋住了：损失 " + lost +
                   " 单位。清理塌方花了 " + hours.ToString("0.#") + " 小时。";
        }
    }

    /// <summary>
    /// 发现富矿脉：事件点总规模变大。
    ///
    /// **加多少必须看矿种**（§19.24.7）。这里用两道闸：
    ///   ① **按比例**：加成 = 该矿点总格数 × <see cref="bonusFraction"/>；
    ///   ② **按稀有度压制**：再乘 `Clamp(rarityReferenceValue / 矿石单价, minRarityScale, 1)`；
    ///   ③ **按价值封顶**：把最终加成换算成银两，不超过 <see cref="maxBonusValue"/>。
    ///
    /// 为什么不直接给"固定格数"：vanilla 的矿点规模本身是
    /// `V / (mineableYield × 矿石单价)`（V ≈ 3500–5000），也就是说**稀有矿的格数天生就少**。
    /// 固定加 20 格会给黄金矿凭空灌进 800 单位黄金（8000 银），那是灾难。
    ///
    /// 实测（所有 `Mineable*` 的 `mineableYield` = 40，每格产出 = `EffectiveMineableYield`
    /// = 40 × 难度 `mineYieldFactor`，中/普通难度为 1.0，取下表 40；`bonusFraction` 取中值 0.25）：
    ///
    /// | 矿种 | 单价 | 矿点格数 | 本事件加成 | 折算价值 |
    /// |---|---|---|---|---|
    /// | 钢铁 | 1.9 | 46–60 | +12 ~ +15 格 | ~900–1100 银 |
    /// | 白银 | 1.0 | 88–113 | +22 ~ +30 格 | ~880–1200 银 |
    /// | 翡翠 | 5 | 18–23 | +2 格 | ~400 银 |
    /// | 铀 | 6 | 15–19 | +1 ~ +2 格 | ~240–480 银 |
    /// | 玻璃钢 | 9 | 10–13 | +1 格 | ~360 银 |
    /// | 黄金 | 10 | 9–12 | +1 格 | ~400 银 |
    /// | 零部件 | 32 | 3–4 | +1 格 | ~1280 银 |
    ///
    /// 注意两点：
    ///   · 难度会整体缩放产出（和平 1.2 / 困难 0.95 / 极端 0.8），所以格数是近似值；
    ///   · 零部件的价值封顶算出 0 格，但被 <see cref="minBonusUnits"/> 兜到 1 格 —— 这是有意的，
    ///     "发现富矿脉"却一格不加会让玩家觉得事件坏了。1 格零部件的价值（~1280 银）也在合理区间。
    /// </summary>
    public class DelegationEventDef_BonusYield : DelegationEventDef
    {
        /// <summary>相对**该矿点总格数**的加成比例。</summary>
        public FloatRange bonusFraction = new FloatRange(0.15f, 0.35f);

        /// <summary>无论怎么算，至少加这么多格（保证事件不空转）。</summary>
        public int minBonusUnits = 1;

        /// <summary>按价值封顶（银两）。0 = 不封顶。</summary>
        public float maxBonusValue = 1200f;

        /// <summary>"常见矿"的参考单价 —— 用它做稀有度基准。钢铁 = 1.9。</summary>
        public float rarityReferenceValue = 1.9f;

        /// <summary>稀有度压制系数的下限，避免高价值矿被压到完全没效果。</summary>
        public float minRarityScale = 0.25f;

        /// <summary>顺手采出一部分（占加成的比例），让玩家立刻看到产出。</summary>
        public float immediateFraction = 0.25f;

        public override bool CanFire(Delegation d, Site site)
        {
            if (d == null || d.deposit == null || d.deposit.IsDepleted)
            {
                return false;
            }
            // ★ 结构不变量（比 onlyForDelegationDefs 更硬的一道闸）：
            //   只有"目标规模本来就是掷定出来的抽象量"（采矿 = 格数）的委派才允许被加规模。
            //   物资藏匿点的规模 = 现场实际件数，凭空 +n 只会让 cellsMined 永远追不上 totalCells。
            //   即使有人在 XML 里漏配了 onlyForDelegationDefs，这里也兜得住。
            return d.Worker?.AllowsScaleIncrease ?? false;
        }

        public override string Apply(Delegation d, Site site)
        {
            // ★ DEV 按钮（WorldObjectComp_Delegations.DevFireEvent）会绕过 CanFire 直接调这里，
            //   所以破坏性实现必须自己也把关 —— 否则调试一次"富矿脉"就能把物资点委派推进死循环。
            if (d == null || d.deposit == null || !(d.Worker?.AllowsScaleIncrease ?? false))
            {
                return "（这一处的规模本来就不是能变多的东西，事件对它没有效果 —— 已忽略）";
            }

            ThingDef mineable = d.resourceDef;
            ThingDef ore = mineable?.building?.mineableThing;
            float oreValue = ore != null ? Mathf.Max(0.01f, ore.BaseMarketValue) : 0f;
            int perCellYield = Mathf.Max(0, d.yieldPerCell);

            int totalCells = Mathf.Max(1, d.deposit.totalUnits);

            // ① 按比例
            int n = Mathf.RoundToInt(totalCells * bonusFraction.RandomInRange);

            // ② 按稀有度压制
            float rarityScale = 1f;
            if (oreValue > 0f && rarityReferenceValue > 0f)
            {
                rarityScale = Mathf.Clamp(rarityReferenceValue / oreValue, minRarityScale, 1f);
                n = Mathf.RoundToInt(n * rarityScale);
            }

            // ③ 按价值封顶
            float perCellValue = perCellYield * oreValue;
            if (maxBonusValue > 0f && perCellValue > 0f)
            {
                int valueCap = Mathf.FloorToInt(maxBonusValue / perCellValue);
                if (valueCap < n) n = valueCap;
            }

            n = Mathf.Max(minBonusUnits, n);

            d.deposit.totalUnits += n;
            d.deposit.rolledUnits += n;   // 必须同步，否则 UI 的"剩余/总数"对不上
            // ★ 关键：d.totalCells 是"本次委派开工时"从 UnitsRemaining 抓的快照，
            //   不跟着加的话，新露出来的矿脉当场谁也采不到，"要挖更久"就成了空话。
            if (d.totalCells > 0) d.totalCells += n;

            float immediate = 0f;
            if (immediateFraction > 0f)
            {
                immediate = n * immediateFraction;
                d.oreUnits += immediate;
            }

            string oreName = ore != null ? ore.LabelCap.ToString() : "矿石";
            return "塌落的岩层后面露出一条更富的 " + oreName + " 矿脉：事件点总规模 +" + n +
                   " 格（原 " + totalCells + " 格）" +
                   (oreValue > 0f ? "，折算约 " + (n * perCellValue).ToString("0") + " 银" : "") +
                   (immediate > 0f ? "，并已顺手采出一部分。" : "。") +
                   "\n（加成按矿点规模的 " + bonusFraction.ToString() + " 计算，" +
                   "再按稀有度 ×" + rarityScale.ToString("0.##") + " 压制，" +
                   (maxBonusValue > 0f ? "并按 " + maxBonusValue.ToString("0") + " 银封顶）" : "不封顶）");
        }
    }

    /// <summary>设备故障 / 恶劣天气：纯粹的时间损失（消耗天数预算）。</summary>
    public class DelegationEventDef_Setback : DelegationEventDef
    {
        public FloatRange stallHours = new FloatRange(3f, 8f);

        public override string Apply(Delegation d, Site site)
        {
            float hours = stallHours.RandomInRange;
            d.StallFor(hours);
            return "工具损坏加上恶劣天气，作业停摆了 " + hours.ToString("0.#") +
                   " 小时。这段时间照样计入计划天数。";
        }
    }

    /// <summary>
    /// 士气受挫 / 争执：给参与者挂一条记忆型心情。
    ///
    /// **需要 3 人以上**（§19.24.7）：两三个人的小队闹别扭不像话，
    /// 而且人少了这条事件也显得刻意。人数门槛写在 Def 的 <see cref="minParticipants"/> 里，
    /// 想调不用改代码。
    /// </summary>
    public class DelegationEventDef_Mood : DelegationEventDef
    {
        public ThoughtDef thought;

        /// <summary>触发所需的最少参与人数（严格大于此值）。默认 3 ⇒ 至少 4 人。</summary>
        public int minParticipants = 3;

        public override bool CanFire(Delegation d, Site site)
        {
            if (thought == null || d == null) return false;
            return CountAlive(d) > minParticipants;
        }

        private static int CountAlive(Delegation d)
        {
            int n = 0;
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                if (p != null && !p.Dead) n++;
            }
            return n;
        }

        public override string Apply(Delegation d, Site site)
        {
            int n = 0;
            List<Pawn> list = d.participants;
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == null || p.Dead) continue;
                if (p.needs != null && p.needs.mood != null && p.needs.mood.thoughts != null
                    && p.needs.mood.thoughts.memories != null)
                {
                    p.needs.mood.thoughts.memories.TryGainMemory(thought);
                    n++;
                }
            }
            return "队伍里为这事闹了别扭，" + n + " 个人心情受影响。";
        }
    }

    /// <summary>
    /// 工伤：真的对 pawn 施加伤害。
    ///
    /// **概率与伤害都随疲劳放大**（§19.25）。这是"长工时"唯一的生理代价：
    ///   · 原版 `NeedRest` 的疲劳心情在车队里不生效（情境型 + 没写 validWhileDespawned）；
    ///   · `DelegationWorker_Mining` 也从不读 rest。
    ///   ⇒ 不接上这条的话，24 小时连轴转除了一个固定心情数字以外毫无后果。
    ///
    /// 倍率是**瞬时**读取全队平均休息，所以它自然形成日周期。
    /// 实测（40 天稳态模拟，`fatigueOnsetRest` = 0.7、`maxFatigueMultiplier` = 4、MTB = 10 天）：
    ///
    /// | 模式 | 最低休息 | 倍率均值 | 倍率峰值 | 等效 MTB | 伤害均值 |
    /// |---|---|---|---|---|---|
    /// | 轻松工作 6h | 76% | ×1.00 | ×1.00 | 10.0 天 | ×1.00 |
    /// | 正常工作 8h | 68% | ×1.00 | ×1.07 | 10.0 天 | ×1.00 |
    /// | 加班工作 16h | 37% | ×1.36 | ×2.43 | 7.4 天 | ×1.06 |
    /// | 全天候 24h | 10% | ×3.52 | ×3.57 | 2.8 天 | ×1.42 |
    ///
    /// ⚠️ 别用"平均休息对应的倍率"来估：`1 + (max-1) × factor` 是分段线性（凸）的，
    /// 按小时算再平均，比按平均休息算要大得多（加班：1.36 vs 1.00）。
    /// </summary>
    public class DelegationEventDef_PawnAccident : DelegationEventDef
    {
        public DamageDef damageDef;
        public FloatRange damageAmount = new FloatRange(6f, 12f);
        public IntRange victims = new IntRange(1, 1);

        /// <summary>全队平均休息低于这个水平才开始抬高工伤概率。</summary>
        public float fatigueOnsetRest = 0.7f;

        /// <summary>疲劳拉满（平均休息 = 0）时的概率倍率上限。</summary>
        public float maxFatigueMultiplier = 4f;

        /// <summary>伤害量随疲劳放大的比例（0.5 ⇒ 疲劳满时伤害 ×1.5）。0 = 不放大。</summary>
        public float damageFatigueScale = 0.5f;

        public override float ChanceMultiplier(Delegation d)
        {
            return 1f + (maxFatigueMultiplier - 1f) * DelegationUtility.FatigueFactor(d, fatigueOnsetRest);
        }

        public override bool CanFire(Delegation d, Site site)
        {
            if (d == null) return false;
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                if (p != null && !p.Dead && !p.Downed) return true;
            }
            return false;
        }

        public override string Apply(Delegation d, Site site)
        {
            List<Pawn> pool = new List<Pawn>();
            for (int i = 0; i < d.participants.Count; i++)
            {
                Pawn p = d.participants[i];
                if (p != null && !p.Dead && !p.Downed) pool.Add(p);
            }
            if (pool.Count == 0) return "无人可受伤。";

            float fatigue = DelegationUtility.FatigueFactor(d, fatigueOnsetRest);
            float damageScale = 1f + damageFatigueScale * fatigue;

            int n = Mathf.Clamp(victims.RandomInRange, 0, pool.Count);
            DamageDef def = damageDef ?? DamageDefOf.Blunt;

            StringBuilder sb = new StringBuilder();
            sb.Append("作业时出了事故：");
            for (int i = 0; i < n; i++)
            {
                Pawn p = pool.RandomElement();
                pool.Remove(p);
                float amount = damageAmount.RandomInRange * damageScale;
                if (i > 0) sb.Append("、");

                // ⚠️ 这是"无地图施加伤害"的实测点（DESIGN §19.14 第 1 项）：
                //    车队成员不在任何地图上，TakeDamage 是否安全尚无定论，
                //    所以这里必须兜住异常，不能让一次随机事件把游戏打崩。
                try
                {
                    p.TakeDamage(new DamageInfo(def, amount));
                    sb.Append(p.LabelShort).Append(" 受伤（").Append(amount.ToString("0.#")).Append(" 点 ").Append(def.label).Append("）");
                }
                catch (Exception ex)
                {
                    sb.Append(p.LabelShort).Append(" 受伤失败（").Append(ex.GetType().Name).Append("）");
                    Log.WarningOnce("[RimDelegation] 无地图施加伤害失败（§19.14①）：" + ex, 0x5E0C2);
                }
            }
            if (fatigue > 0.01f)
            {
                sb.Append("\n（全队平均休息 ").Append(DelegationUtility.AverageRest(d).ToStringPercent())
                  .Append("，疲劳让这起事故的伤害 ×").Append(damageScale.ToString("0.##")).Append("）");
            }
            return sb.ToString();
        }
    }
}
