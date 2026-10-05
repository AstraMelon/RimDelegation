using System.Collections.Generic;
using System.Text;
using RimDelegation.Combat;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「威胁评估」的统一入口与摘要 —— 单一事实来源。
    ///
    /// 为什么必须抽出来：这条入口有两个触发点，两处**必须给出完全一致的数字与跳转**，
    /// 否则玩家会看到两个口径：
    ///   ① 车队停在 / 驶向事件点时：<see cref="WorldObjectComp_ThreatAssessment"/> 出 gizmo 与右键菜单；
    ///   ② **委派对话框**里"要不要把车队派到这个地方"的那一刻：`Dialog_ChooseDelegation`。
    ///
    /// ② 对**物资藏匿点**尤其要紧：`OpportunitySite_ItemStash` 的 `siteThreatChance = 0.85`，
    /// 也就是绝大多数物资点都有守军，而"派不派"的决定正是在那个框里做的 ——
    /// 只在车队落地之后才给评估，等于让玩家"人到了才发现打不过"。
    ///
    /// 全部判定都只读存档里的数据（`SitePartParams.threatPoints` 在任务生成时就已掷好并存盘），
    /// 不需要生成地图。
    /// </summary>
    public static class ThreatAssessmentEntry
    {
        /// <summary>有威胁点的部件才算威胁部件（例：`PreciousLump` / `ItemStash` 主件本身 wantsThreatPoints = false）。</summary>
        public static bool IsThreatPart(SitePart part)
        {
            return part?.def != null && part.parms != null && part.parms.threatPoints > 0f;
        }

        public static bool HasThreat(Site site)
        {
            if (site?.parts == null)
            {
                return false;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                if (IsThreatPart(site.parts[i]))
                {
                    return true;
                }
            }
            return false;
        }

        public static int ThreatPartCount(Site site)
        {
            if (site?.parts == null)
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < site.parts.Count; i++)
            {
                if (IsThreatPart(site.parts[i]))
                {
                    n++;
                }
            }
            return n;
        }

        public static float TotalThreatPoints(Site site)
        {
            if (site?.parts == null)
            {
                return 0f;
            }
            float sum = 0f;
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (IsThreatPart(part))
                {
                    sum += part.parms.threatPoints;
                }
            }
            return sum;
        }

        /// <summary>
        /// 有没有**未探明**（`hidden`）的威胁部件。
        ///
        /// 物资藏匿点是用 `hiddenSitePartsPossible = true` 生成的（矿点是 false），
        /// 所以它的威胁件常常是隐藏的 —— 地点检视面板上不会写"那里有守军"。
        /// 委派对话框属于"你已经派人去侦察了"，这里如实标出来，但不能假装玩家早就知道。
        /// </summary>
        public static bool HasHiddenThreat(Site site)
        {
            if (site?.parts == null)
            {
                return false;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (IsThreatPart(part) && part.hidden)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 有几个威胁部件**无法**用无地图战斗抽象（需要进图清剿）。
        /// 判据来自 <see cref="ThreatRosterFactory.SupportOf"/> —— 与真正生成编队时用的是同一张表。
        /// </summary>
        public static int UnassessablePartCount(Site site)
        {
            if (site?.parts == null)
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];
                if (!IsThreatPart(part))
                {
                    continue;
                }
                if (ThreatRosterFactory.SupportOf(part.def) != ThreatRosterFactory.RosterSupport.Abstractable)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 一行守军摘要（委派对话框与检视面板共用）。没有守军时返回 null。
        ///
        /// 例：`守军：威胁点数 480 · 1 个威胁部件，其中 1 个无法无地图评估（需进图）· 含未探明的威胁`
        /// </summary>
        public static string SummaryLine(Site site)
        {
            if (!HasThreat(site))
            {
                return null;
            }
            int total = ThreatPartCount(site);
            int blocked = UnassessablePartCount(site);

            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("守军：威胁点数 {0:0} · {1} 个威胁部件", TotalThreatPoints(site), total);
            if (blocked > 0)
            {
                sb.AppendFormat("，其中 {0} 个无法无地图评估（需进图）", blocked);
            }
            else
            {
                sb.Append("（均可无地图评估）");
            }
            if (HasHiddenThreat(site))
            {
                sb.Append(" · 含未探明的威胁");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 构建战场（薄封装，调用方不必再 `using RimDelegation.Combat`）。
        ///
        /// <paramref name="firstStrikePenalty"/> &gt; 0 时计入"守军先手一轮"的耐久折扣 ——
        /// 委派对话框会把当前作战姿态的折扣传进来，保证这里的预告与委派实际结算
        /// 看到的是同一个战场。
        /// </summary>
        public static CombatSetup Build(Caravan caravan, Site site, float firstStrikePenalty = 0f,
            List<Pawn> excludeFromCombat = null)
        {
            return CombatSceneFactory.Build(caravan, site, firstStrikePenalty, excludeFromCombat);
        }

        /// <summary>打开威胁评估面板。</summary>
        public static void Open(Caravan caravan, Site site, float firstStrikePenalty = 0f)
        {
            if (caravan == null || site == null)
            {
                return;
            }
            Find.WindowStack.Add(new Dialog_ThreatAssessment(Build(caravan, site, firstStrikePenalty)));
        }
    }
}
