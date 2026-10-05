using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「参与者」编辑器（S9）：作业期间增减一条在途委派的干活的殖民者。
    ///
    /// 为什么需要它：委派开始后车队可能合并/拆分（伤员归队、访客加入、路上又捎了人），
    /// 而参与者名单直接决定产能与负重上限。以前想改只能中止重开，代价是一笔
    /// 「白跑一趟」-6 的心情 —— 那等于对"改主意"罚款，不是好设计。
    ///
    /// 名单来源与对话框同一条口径（<see cref="DelegationUtility.EligiblePawns" />：
    /// 能行动的自由人/奴隶），行画法也用同一个 <see cref="DelegationUIUtility.DrawPawnLine" />，
    /// 所以两处的名单与长相不会分叉。
    /// </summary>
    public class Dialog_EditDelegationParticipants : Window
    {
        private const float RowH = DelegationUIUtility.RowHeight;

        private readonly WorldObjectComp_Delegations comp;
        private readonly Delegation d;
        private readonly DelegationWorker worker;
        private readonly DelegationDef def;

        private readonly List<Pawn> candidates;
        private readonly HashSet<Pawn> selected = new HashSet<Pawn>();
        private PawnSortMode sortMode = PawnSortMode.SkillDesc;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(520f, 520f);

        public Dialog_EditDelegationParticipants(WorldObjectComp_Delegations comp, Delegation d)
        {
            this.comp = comp;
            this.d = d;
            worker = d?.Worker;
            def = d?.def;

            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            onlyOneOfTypeAllowed = true;

            candidates = DelegationUtility.EligiblePawns(d?.caravan);
            if (d?.participants != null)
            {
                for (int i = 0; i < d.participants.Count; i++)
                {
                    if (d.participants[i] != null)
                    {
                        selected.Add(d.participants[i]);
                    }
                }
            }
            SortCandidates();
        }

        public override void DoWindowContents(Rect inRect)
        {
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            float x = inRect.x;
            float w = inRect.width;
            float y = inRect.y;

            Widgets.Label(new Rect(x, y, w, 26f),
                string.Format("参与人员（{0}）", d?.def?.label ?? "委派"));
            y += 28f;

            int need = Mathf.Max(1, def?.minPawns ?? 1);
            int cap = def != null && def.maxPawns > 0 ? def.maxPawns : int.MaxValue;
            Widgets.Label(new Rect(x, y, w, 22f),
                cap == int.MaxValue
                    ? string.Format("人数下限 {0}，无上限；队里共 {1} 人可选", need, candidates.Count)
                    : string.Format("人数下限 {0}，上限 {1}；队里共 {2} 人可选", need, cap, candidates.Count));
            y += 24f;

            // ---- 工具条：排序 / 全选 / 全不选
            const float SortW = 200f;
            const float BtnW = 70f;
            if (Widgets.ButtonText(new Rect(x, y, SortW, 28f),
                    "排序：" + DelegationUIUtility.SortLabel(sortMode)))
            {
                sortMode = DelegationUIUtility.NextSort(sortMode);
                SortCandidates();
            }
            if (Widgets.ButtonText(new Rect(x + SortW + 8f, y, BtnW, 28f), "全选"))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    selected.Add(candidates[i]);
                }
            }
            if (Widgets.ButtonText(new Rect(x + SortW + BtnW + 16f, y, BtnW + 10f, 28f), "全不选"))
            {
                selected.Clear();
            }
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(x, y, w, 28f), string.Format("已勾选 {0}/{1}", selected.Count, candidates.Count));
            Text.Anchor = TextAnchor.UpperLeft;
            y += 34f;

            // ---- 名单（滚动：一队最多 12 人，塞得下就不滚）
            Rect listRect = new Rect(x, y, w, Mathf.Max(60f, inRect.yMax - 40f - y));
            float viewH = Mathf.Max(candidates.Count * RowH + 8f, listRect.height);
            Rect view = new Rect(0f, 0f, w - 24f, viewH);
            Widgets.BeginScrollView(listRect, ref scroll, view);
            if (candidates.Count == 0)
            {
                Widgets.Label(new Rect(0f, 0f, view.width, 30f), "远行队里没有可参加委派的人员。");
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                DrawPawnRow(new Rect(0f, i * RowH, view.width, RowH), candidates[i]);
            }
            Widgets.EndScrollView();

            // ---- 确定 / 取消
            const float ConfirmW = 120f;
            float by = inRect.yMax - 34f;
            if (Widgets.ButtonText(new Rect(x + w - ConfirmW, by, ConfirmW, 30f), "确定"))
            {
                comp?.SetParticipants(d, ChosenList());
                Close();
            }
            if (Widgets.ButtonText(new Rect(x + w - ConfirmW * 2f - 8f, by, ConfirmW, 30f), "取消"))
            {
                Close();
            }
        }

        private void SortCandidates()
        {
            DelegationUIUtility.SortPawns(candidates, sortMode, def);
        }

        private void DrawPawnRow(Rect row, Pawn p)
        {
            bool sel = selected.Contains(p);
            if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }
            Widgets.Checkbox(row.x + 4f, row.y + (row.height - 24f) * 0.5f, ref sel, 24f, false, false, null, null);

            Rect body = new Rect(row.x + 34f, row.y, row.width - 34f, row.height);
            DelegationUIUtility.DrawPawnLine(body, p, DelegationUIUtility.PawnLine(p, worker, def));
            if (Widgets.ButtonInvisible(DelegationUIUtility.LabelRectFor(body)))
            {
                sel = !sel;
            }

            if (sel != selected.Contains(p))
            {
                if (sel)
                {
                    selected.Add(p);
                }
                else
                {
                    selected.Remove(p);
                }
            }
        }

        private List<Pawn> ChosenList()
        {
            List<Pawn> list = new List<Pawn>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (selected.Contains(candidates[i]))
                {
                    list.Add(candidates[i]);
                }
            }
            return list;
        }
    }
}
