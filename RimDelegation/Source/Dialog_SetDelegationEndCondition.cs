using RimWorld;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「结束条件」编辑器（S9）：作业期间改一条在途委派什么时候收工。
    ///
    /// 为什么单独开一个窗口而不是塞进 <c>FloatMenu</c>：条件有三档，其中两档要带一个数值
    /// （天数 / 配额）。FloatMenu 只能"点一下给一个固定值"，玩家想设 7 天就得先看一串
    /// 30 个数字的菜单 —— 那是把选择成本转嫁给玩家。这里照抄委派对话框里那套
    /// 「单选 + 步进器」的画法，量纲与措辞全部问 worker（"采空为止" / "搬空为止" / "救出为止"）。
    ///
    /// ⚠️ 一条必须在界面上说清的事：改的是**目标**，不是**账本**。
    ///    `ElapsedDays` / `MinedUnitsTotal` 都是从开工算起的累计值，
    ///    所以把已经干了 2 天的活改成"干满 1 天"会在下一 tick 立刻收工 —— 这是对的，
    ///    但玩家必须提前知道，否则会觉得"我刚设的值没生效就结束了"。
    /// </summary>
    public class Dialog_SetDelegationEndCondition : Window
    {
        private const float RowH = 30f;

        private readonly WorldObjectComp_Delegations comp;
        private readonly Delegation d;
        private readonly DelegationWorker worker;

        private DelegationEndCondition condition;
        private int daysLimit;
        private int quotaUnits;

        public override Vector2 InitialSize => new Vector2(480f, 306f);

        public Dialog_SetDelegationEndCondition(WorldObjectComp_Delegations comp, Delegation d)
        {
            this.comp = comp;
            this.d = d;
            worker = d?.Worker;
            condition = d?.endCondition ?? DelegationEndCondition.UntilDepleted;
            daysLimit = Mathf.Max(1, d?.daysLimit ?? 5);
            quotaUnits = Mathf.Max(1, d?.quotaUnits ?? 400);

            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            onlyOneOfTypeAllowed = true;
        }

        private int MaxDays => Mathf.Max(1, d?.def?.maxDaysLimit ?? 30);

        private int QuotaCap => WorldObjectComp_Delegations.QuotaCapOf(d);

        public override void DoWindowContents(Rect inRect)
        {
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            float x = inRect.x;
            float w = inRect.width;
            float y = inRect.y;

            Widgets.Label(new Rect(x, y, w, 26f), "修改结束条件");
            y += 30f;

            // ---- 取尽（措辞由 worker 给：采空 / 搬空 / 救出为止）
            if (Widgets.RadioButtonLabeled(new Rect(x, y, w, RowH),
                    "一直干到" + (worker?.UntilDepletedLabel ?? "采空为止"),
                    condition == DelegationEndCondition.UntilDepleted))
            {
                condition = DelegationEndCondition.UntilDepleted;
            }
            y += RowH + 4f;

            // ---- 按天数
            if (Widgets.RadioButtonLabeled(new Rect(x, y, w, RowH),
                    string.Format("按天数（1–{0} 天）", MaxDays), condition == DelegationEndCondition.Days))
            {
                condition = DelegationEndCondition.Days;
            }
            y += RowH;
            if (condition == DelegationEndCondition.Days)
            {
                IntStepper(new Rect(x + 24f, y, 260f, RowH), ref daysLimit, 1, MaxDays, "天");
            }
            y += RowH + 4f;

            // ---- 按产出配额（量纲 = OutputUnitName，不是进度单位）
            string outputUnit = worker?.OutputUnitName ?? "单位";
            if (Widgets.RadioButtonLabeled(new Rect(x, y, w, RowH),
                    string.Format("按产出配额（最多约 {0} {1}）", QuotaCap, outputUnit),
                    condition == DelegationEndCondition.Quota))
            {
                condition = DelegationEndCondition.Quota;
            }
            y += RowH;
            if (condition == DelegationEndCondition.Quota)
            {
                IntStepper(new Rect(x + 24f, y, 260f, RowH), ref quotaUnits, 1, QuotaCap, outputUnit);
            }
            y += RowH + 8f;

            // ---- 账本不重置（必须写在按钮上方，玩家点"确定"之前看得到）
            Color old = GUI.color;
            GUI.color = new Color(0.85f, 0.85f, 0.85f);
            Widgets.Label(new Rect(x, y, w, 40f),
                "已过去的工期与已交付的产出不会重置：新条件从开工那一刻的累计值算起。");
            GUI.color = old;

            // ---- 确定 / 取消
            const float BtnW = 120f;
            float by = inRect.yMax - 34f;
            if (Widgets.ButtonText(new Rect(x + w - BtnW, by, BtnW, 30f), "确定"))
            {
                comp?.SetEndCondition(d, condition, daysLimit, quotaUnits);
                Close();
            }
            if (Widgets.ButtonText(new Rect(x + w - BtnW * 2f - 8f, by, BtnW, 30f), "取消"))
            {
                Close();
            }
        }

        /// <summary>[-] 数值 [+]（与委派对话框同一个画法，玩家不必学两套控件）。</summary>
        private static void IntStepper(Rect rect, ref int value, int min, int max, string unit)
        {
            const float ButtonWidth = 26f;
            const float NumberWidth = 62f;
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, ButtonWidth, rect.height), "-"))
            {
                value--;
            }
            if (Widgets.ButtonText(new Rect(rect.x + ButtonWidth + NumberWidth + 4f, rect.y, ButtonWidth, rect.height), "+"))
            {
                value++;
            }
            value = Mathf.Clamp(value, min, Mathf.Max(min, max));
            Widgets.Label(new Rect(rect.x + ButtonWidth + 4f, rect.y, NumberWidth, rect.height), value.ToString());
            if (!unit.NullOrEmpty())
            {
                Widgets.Label(new Rect(rect.x + ButtonWidth * 2f + NumberWidth + 6f, rect.y, rect.width - 100f, rect.height), unit);
            }
        }
    }
}
