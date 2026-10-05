using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派行为基类。子类只负责"这是什么活、怎么算进度、完成后给什么"，
    /// 生命周期（开始/中断/完成、信件、UI）由 WorldObjectComp_Delegations 统一处理。
    /// </summary>
    public class DelegationWorker
    {
        public DelegationDef def;

        // ── 量纲：把"这门活的单位叫什么"交回给 worker ────────────────────────
        // 宿主与对话框里有几处文案原本写死了"格"（采矿的量纲）。物资藏匿点是"件"，
        // 于是会显示成"本次可采 12 格"这种错话。用这两个属性把量纲交回来。

        /// <summary>进度单位名（采矿 = "格"，物资点 = "件"）。</summary>
        public virtual string UnitName => "格";

        /// <summary>作业动词（"采" / "搬"），用于"预计 N 天 X 完 / 本次可 X N 格"这类**短句**。</summary>
        public virtual string WorkVerb => "采";

        /// <summary>
        /// 这门活在**句子**里的活动名（"开采" / "搜刮" / "营救"）。
        ///
        /// 与 <see cref="WorkVerb"/> 并存不是重复：WorkVerb 是单字动词，只进短句
        /// （"预计 3 天采完"、"本次可采 12 格"）；ActivityName 进需要完整词的句子
        /// （"委派开采中"、"恢复开采"、"饿着也继续开采"）。
        /// 合成一格的代价要么是"恢复搬。"这种病句，要么是"可开采 12 格"那种啰嗦 —— 所以留两格。
        /// </summary>
        public virtual string ActivityName => "开采";

        /// <summary>
        /// **产出**的量纲（采矿 = "单位"，物资点 = "件"，营救 = "人"）。
        ///
        /// 为什么不直接用 <see cref="UnitName"/>：配额（`quotaUnits`）比的是
        /// `Delegation.MinedUnitsTotal`（= 已交付产出），而采矿的进度单位是"格"、产出单位是"单位"
        /// —— 两者根本不是一回事。物资点恰好同值（都是件），但那是巧合，不该写进契约。
        /// </summary>
        public virtual string OutputUnitName => "单位";

        /// <summary>"一直干到目标取尽"这个结束条件的文案（"采空为止" / "搬空为止" / "救出为止"）。</summary>
        public virtual string UntilDepletedLabel => WorkVerb + "空为止";

        /// <summary>
        /// 远行队信息栏状态行的 Keyed 键后缀（"Mining" / "Search" / "Rescue"）。
        ///
        /// 那两行是 Keyed 翻译串（`RimDelegationCaravanDelegating` / `RimDelegationPawnWorking`），
        /// 而中英文措辞差得远（"开采中" vs "is mining"）—— 塞不进一个参数，
        /// 所以让 worker 给后缀，由 <see cref="StatusKey"/> 拼出 `..._Mining` 这样的键。
        /// </summary>
        public virtual string StatusKeySuffix => "Mining";

        /// <summary>
        /// 拼出带后缀的 Keyed 键；键不存在就退回基础键。
        ///
        /// 为什么必须验一下：漏配一条翻译会让玩家在信息栏里看到裸键名
        /// （`RimDelegationPawnWorking_Rescue`），那比"措辞不够贴切"糟得多。
        /// `Translator.CanTranslate` 就是原版判断"这个键有没有翻译"的入口。
        /// </summary>
        public static string StatusKey(string baseKey, string suffix)
        {
            if (baseKey.NullOrEmpty() || suffix.NullOrEmpty())
            {
                return baseKey;
            }
            string keyed = baseKey + "_" + suffix;
            return Translator.CanTranslate(keyed) ? keyed : baseKey;
        }

        /// <summary>
        /// **结构不变量**：这门活的目标规模是不是"掷定出来的抽象量"，因而可以被事件放大？
        ///
        /// 采矿 = true（规模是 `GenStep_PreciousLump` 的公式掷出来的格数，可以凭空 +n 格）。
        /// 物资藏匿点 = false（规模 = 现场实际件数，凭空 +n 会让 `cellsMined` 永远追不上
        /// `totalCells` ⇒ 委派永不结束、地点永不销毁、进图重掷 = 双吃）。
        ///
        /// 读取点：`DelegationEventDef_BonusYield.CanFire` 与 `Apply`。默认 false 是安全侧。
        /// </summary>
        public virtual bool AllowsScaleIncrease => false;

        /// <summary>
        /// 这个作战姿态下的成算描述（对话框底部显示）；不适用时返回 null。
        ///
        /// 放在 worker 上而不是写在对话框里：姿态的算法是**玩法专属**的
        /// （潜入按暴露概率、强攻按战斗预告），对话框只负责显示一行字。
        /// </summary>
        public virtual string ApproachForecast(Site site, List<Pawn> pawns, DelegationApproachDef approach)
        {
            return null;
        }

        /// <summary>开局：解析目标内容（如矿点矿种与总格数）。</summary>
        public virtual void OnStart(Delegation d, Site site)
        {
        }

        /// <summary>一个"有效工作 tick"（已经过工时门控）。delta = 工作时长。</summary>
        public virtual void Tick(Delegation d, Site site, int delta)
        {
        }

        /// <summary>
        /// worker 自己的"该中断了"判定（null / 空 = 继续）。
        ///
        /// 与 <see cref="WorkerEndReason"/> 的区别：那个是**干完了**（发完成信、可能销毁地点），
        /// 这个是**干不下去**（发中断信、地点保留、原版失效计时恢复）。
        /// 例：救援委派的清场战斗打输了 —— 车队要带着伤员撤，而不是"完成"。
        /// </summary>
        public virtual string WorkerAbortReason(Delegation d, Site site)
        {
            return null;
        }

        /// <summary>
        /// 目标达成时给宿主用的完成理由（null = 宿主用默认的"目标已采空"）。
        ///
        /// `TargetDepleted` 是**通用**的"干完了"信号，它的默认文案是采矿口径；
        /// 救援类委派干完的是"人已救出"，用那个词会很怪。
        /// </summary>
        public virtual string DepletedReason(Delegation d)
        {
            return null;
        }

        /// <summary>
        /// 这个作战姿态下"我方开局耐久折扣"的系数（0 = 无）。
        ///
        /// 用途：潜入失败会转强攻且**守军先手一轮**，这个折扣必须同时作用在
        /// 结算用的 <c>CombatScene</c> 与委派对话框里那个「威胁评估」按钮上，
        /// 否则"预告"与"实际"会差一轮火力（违背"预告即契约"）。
        /// </summary>
        public virtual float ApproachFirstStrikePenalty(DelegationApproachDef approach)
        {
            return 0f;
        }

        /// <summary>
        /// 追加到地点检视面板的**告警/前提说明**（null = 不显示）。
        ///
        /// 用途：把"这门活的已知不精确之处"摊给玩家看，而不是埋在文档里
        /// （例：采矿的存量与原版进图掷骰是两次独立掷骰）。
        /// </summary>
        public virtual string InspectWarning(Delegation d)
        {
            return null;
        }

        /// <summary>完成后交付产出（S2 实装）。</summary>
        public virtual void OnComplete(Delegation d, Site site)
        {
        }

        /// <summary>
        /// worker 自己的"该收工了"判定（null / 空 = 继续）。
        ///
        /// 用途：目标本身还给不出"采空"信号（<see cref="Delegation.TargetDepleted"/> 为假），
        /// 但客观上已经干不下去了。例：物资藏匿点装车装到车队满载，剩下的搬不走 ——
        /// 这时应当收工（**没取空 ⇒ 地点不销毁、原版失效计时恢复 ⇒ 剩下的留原地，玩家卸完货能回来接着委派**），
        /// 而不是静默卡死。
        ///
        /// 返回的字符串就是收工原因，会进入完成信件。
        /// </summary>
        public virtual string WorkerEndReason(Delegation d, Site site)
        {
            return null;
        }

        /// <summary>中断时的收尾（S2 实装"带已采部分撤回"）。</summary>
        public virtual void OnAbort(Delegation d, Site site)
        {
        }

        /// <summary>一行进度描述，显示在地点 inspect 面板。</summary>
        public virtual string ProgressLabel(Delegation d)
        {
            return null;
        }

        /// <summary>每人每天能推进多少"单位"（用于估算剩余时间）。</summary>
        public virtual float EstimatedUnitsPerDay(Delegation d, PlanetTile tile)
        {
            return 0f;
        }

        /// <summary>
        /// 开局前的估算（对话框用，避免为估算而创建 Delegation 实例）。
        ///
        /// `site` 是可选的补充信息：采矿只需要"人 + 模式"就够（速率与矿点无关），
        /// 但物资点的速率取决于现场物件的**质量**（见 <see cref="DelegationWorker_TakeItemStash"/>），
        /// 所以它必须能拿到事件点。默认 null ⇒ 老实现不受影响。
        /// </summary>
        public virtual float EstimateUnitsPerDayFor(List<Pawn> pawns, DelegationModeDef mode, PlanetTile tile, Site site = null)
        {
            return 0f;
        }

        /// <summary>
        /// "估算不出来"时给玩家看的**原因**（null = 用默认文案"无法估算（模式或人员缺失）"）。
        ///
        /// 为什么需要它：默认那句把"算不出来"一律归因于缺人/缺模式，而物资点的真实原因是
        /// **清单还没掷**（人和模式都在）。UI 说错原因，玩家就会去查错东西。
        /// </summary>
        public virtual string EstimateUnavailableReason(Site site, DelegationPreview preview, DelegationDeposit deposit)
        {
            return null;
        }

        /// <summary>
        /// 掷定这个事件点的存量。**只会在事件点上调用一次**，结果挂在地点上持久化，
        /// 之后每次委派都复用同一份并累计扣减（这就是"事件点计数、避免多次采集"）。
        /// </summary>
        public virtual void RollDeposit(DelegationDeposit deposit, Site site)
        {
        }

        /// <summary>
        /// **抵达前**的目标预览（只给范围）。抵达后由地点上已掷定的存量给出精确值。
        /// </summary>
        public virtual DelegationPreview MakePreview(Site site)
        {
            return null;
        }

        /// <summary>开局前的目标描述（对话框用）：读的是"范围预览"或"已掷定的存量"。</summary>
        public virtual string PreviewLabel(Site site, DelegationPreview preview, DelegationDeposit exactDeposit)
        {
            return null;
        }

        /// <summary>
        /// 对话框里「预期获得」的**列表**（S6）。返回 null / 空 = 这一块不显示。
        ///
        /// 与 <see cref="PreviewLabel"/> 的分工：PreviewLabel 是**一段话**（讲前提、讲近似、讲未定），
        /// 这里是**逐条的东西**（图标 + 名称 + 数量 + 质量 + 市价）。
        /// 两者并存不是重复 —— 玩家要"一眼看清能拿到什么"，也要"知道这份清单可不可信"。
        /// </summary>
        public virtual List<DelegationPreviewItem> PreviewItems(Site site, DelegationPreview preview,
            DelegationDeposit exactDeposit)
        {
            return null;
        }

        /// <summary>
        /// 作业**进行中**"现场还剩什么"（页签 / 地点检视面板用）。
        ///
        /// 与 <see cref="PreviewItems"/> 的分工：那个回答"我将会拿到什么"（计划期、对话框里），
        /// 这个回答"还剩什么没搬走"（作业期）。用户报的"本次进度里看不到物资具体是什么"就是缺这一格
        /// —— 页签只写「已搬走 0/2 件」，玩家却不知道那 2 件是什么。
        /// 返回 null = 这条玩法没有"清单"概念。
        /// </summary>
        public virtual List<DelegationPreviewItem> ProgressItems(Delegation d, Site site)
        {
            return null;
        }

        /// <summary>选人列表里每个人的一行细节（对话框用）。</summary>
        public virtual string PawnDetail(Pawn p)
        {
            return null;
        }

        /// <summary>
        /// 按钮图标。数据驱动：worker 知道这门活的目标是什么（采矿就返回矿物的 uiIcon）。
        /// 返回 null 时宿主会退回到 DelegationDef.gizmoIconPath 的贴图路径查找。
        /// </summary>
        public virtual Texture2D GetGizmoIcon(Site site)
        {
            return null;
        }

        /// <summary>
        /// 把"待交付的产出"送进车队库存，返回本次实际交付的数量。
        /// 由宿主按小时节奏、以及完成/中断时调用 → 中断天然就是"带已采部分撤回"。
        /// </summary>
        public virtual int FlushDeliveries(Delegation d, Caravan caravan)
        {
            return 0;
        }

        /// <summary>产出汇总（信件与 inspect 用）。</summary>
        public virtual string DeliverySummary(Delegation d)
        {
            return null;
        }
    }
}
