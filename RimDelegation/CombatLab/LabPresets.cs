using System;
using System.Collections.Generic;
using RimDelegation.Combat;

namespace RimDelegation.CombatLab
{
    /// <summary>
    /// 模板库与预设对局。
    ///
    /// 「预设对局」里前 4 套刻意与 <c>Prototype/Combat/Tests/Scenarios.cs</c> 的控制台场景**同参数**，
    /// 因此同一预设在本 UI 里跑出的胜率应当与控制台 <c>dotnet run -- demo</c> 一致 ——
    /// 这是"两个程序确实共用同一个计算核心"的交叉验证手段。
    /// </summary>
    public static class LabPresets
    {
        // ── 模板 ────────────────────────────────────────────────────────
        // 数值为 vanilla 量级的占位标定值；射程取自典型 VerbProperties.range。
        // 正式接入游戏后应由真 pawn 的 stat 折算（§19.6 / §19.17.5）。

        public static List<LabUnitTemplate> OurTemplates()
        {
            return new List<LabUnitTemplate>
            {
                new LabUnitTemplate("殖民者·突击步枪", "均衡主力", 100f, 0.62f, 3.0f, 12f, 0.16f, 0.31f, ArmorCategory.Sharp, 25.9f, 4.6f),
                new LabUnitTemplate("殖民者·霰弹枪",   "近距高伤", 100f, 0.55f, 1.5f, 18f, 0.30f, 0.55f, ArmorCategory.Sharp, 12.0f, 4.6f),
                new LabUnitTemplate("殖民者·狙击枪",   "超远射程", 100f, 0.70f, 1.0f, 22f, 0.22f, 0.40f, ArmorCategory.Sharp, 40.0f, 4.6f),
                new LabUnitTemplate("殖民者·冲锋枪",   "低伤高射速", 100f, 0.48f, 6.0f, 6f, 0.09f, 0.18f, ArmorCategory.Sharp, 20.0f, 4.6f),
                new LabUnitTemplate("殖民者·长剑",     "近战：会把距离拉到 1.5", 100f, 0.80f, 1.0f, 15f, 0.20f, 0.45f, ArmorCategory.Sharp, 25f, 5.0f, true),
                new LabUnitTemplate("殖民者·平民",     "无武装", 100f, 0.35f, 1.0f, 4f, 0.00f, 0.05f, ArmorCategory.Sharp, 12f, 4.6f),
                new LabUnitTemplate("海军装甲兵",      "重甲老兵", 100f, 0.68f, 3.0f, 14f, 0.30f, 0.90f, ArmorCategory.Sharp, 25.9f, 4.6f),
                new LabUnitTemplate("训练假人",        "零输出靶子", 100f, 0.0f, 0.0f, 0f, 0f, 0f, ArmorCategory.Sharp, 25f, 0f),
            };
        }

        public static List<LabUnitTemplate> EnemyTemplates()
        {
            return new List<LabUnitTemplate>
            {
                new LabUnitTemplate("海盗·步枪",   "标准人类敌人", 90f, 0.40f, 2.0f, 9f, 0.14f, 0.35f, ArmorCategory.Sharp, 24f, 4.5f),
                new LabUnitTemplate("部落·短弓",   "低护甲", 80f, 0.38f, 1.0f, 7f, 0.06f, 0.10f, ArmorCategory.Sharp, 20f, 4.8f),
                new LabUnitTemplate("野兽·撕咬",   "近战：会一路冲到 1.5 格", 80f, 0.45f, 1.0f, 8f, 0.05f, 0.05f, ArmorCategory.Sharp, 25f, 5.5f, true),
                new LabUnitTemplate("机械族·镰刀", "高护甲高耐久", 150f, 0.50f, 3.0f, 12f, 0.22f, 0.50f, ArmorCategory.Sharp, 24f, 3.5f),
                new LabUnitTemplate("迷你炮塔",    "固定火力，不移动", 200f, 0.60f, 4.0f, 9f, 0.18f, 0.60f, ArmorCategory.Sharp, 25f, 0f),
                new LabUnitTemplate("机械蜈蚣",    "重型机械族", 400f, 0.55f, 5.0f, 16f, 0.40f, 0.75f, ArmorCategory.Sharp, 30f, 3.0f),
                new LabUnitTemplate("不可击穿靶",  "用于验证 Timeout", 100f, 0f, 0f, 0f, 0f, 2.0f, ArmorCategory.Sharp, 25f, 0f),
            };
        }

        public static LabUnitTemplate FindTemplate(List<LabUnitTemplate> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Name == name) return list[i];
            return list.Count > 0 ? list[0] : null;
        }

        // ── 预设对局 ────────────────────────────────────────────────────

        public sealed class Preset
        {
            public string Name;
            public string Note;
            public List<LabUnit> Units = new List<LabUnit>();
            public LabSceneParams Params = new LabSceneParams();

            public override string ToString() => Name;
        }

        private static void Squad(List<LabUnit> into, List<LabUnitTemplate> ours, bool terrainAdvantage)
        {
            string[] names = { "Chisa", "Denia", "老王", "阿花" };
            string[] picks = { "殖民者·突击步枪", "殖民者·霰弹枪", "殖民者·冲锋枪", "殖民者·狙击枪" };
            for (int i = 0; i < picks.Length; i++)
            {
                LabUnit u = LabUnit.FromTemplate(FindTemplate(ours, picks[i]), true);
                u.Name = names[i];
                u.HasTerrainAdvantage = terrainAdvantage;
                into.Add(u);
            }
        }

        private static void Many(List<LabUnit> into, LabUnitTemplate t, int n, bool mine)
        {
            for (int i = 1; i <= n; i++)
                into.Add(LabUnit.FromTemplate(t, mine, i.ToString()));
        }

        public static List<Preset> All()
        {
            List<LabUnitTemplate> ours = OurTemplates();
            List<LabUnitTemplate> foes = EnemyTemplates();
            List<Preset> list = new List<Preset>();

            // 与控制台 Scenarios.Manhunters() 同参数
            {
                Preset p = new Preset { Name = "猎杀人类（8 野兽冲锋）", Note = "与控制台 manhunters 同参数：野兽近战 ⇒ 距离被拉到 1.5" };
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "野兽·撕咬"), 8, false);
                list.Add(p);
            }

            // 与控制台 Scenarios.Outpost() 同参数
            {
                Preset p = new Preset { Name = "海盗哨所（我方无掩体）", Note = "与控制台 outpost 同参数：海盗有工事 ⇒ 射程满额，我方 ×0.6" };
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 5, false);
                list.Add(p);
            }

            // 与控制台 Scenarios.SleepingMechanoids() 同参数
            {
                Preset p = new Preset { Name = "休眠机械族（偷袭）", Note = "与控制台 sleepingmechs 同参数：敌方输出 ×0.20，地形中立" };
                p.Params.EnemyOutputFactor = 0.20f;
                Squad(p.Units, ours, true);
                Many(p.Units, FindTemplate(foes, "机械族·镰刀"), 3, false);
                list.Add(p);
            }

            // 与控制台 Scenarios.Turrets() 同参数
            {
                Preset p = new Preset { Name = "炮塔阵地（必败）", Note = "与控制台 turrets 同参数：炮塔不移动、有工事" };
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "迷你炮塔"), 4, false);
                list.Add(p);
            }

            // 与控制台 Scenarios.Overwhelming() 同参数
            {
                Preset p = new Preset { Name = "压倒性优势", Note = "8 重甲老兵 vs 1 野兽 —— 应为零伤亡胜利" };
                Many(p.Units, FindTemplate(ours, "海军装甲兵"), 8, true);
                Many(p.Units, FindTemplate(foes, "野兽·撕咬"), 1, false);
                list.Add(p);
            }

            // 与控制台 Scenarios.Invulnerable() 同参数
            {
                Preset p = new Preset { Name = "不可击穿（Timeout）", Note = "护甲 2.0 且零输出 —— 应打到回合上限" };
                p.Params.MaxRounds = 30;
                p.Units.Add(LabUnit.FromTemplate(FindTemplate(ours, "殖民者·突击步枪"), true, "（测试）"));
                p.Units.Add(LabUnit.FromTemplate(FindTemplate(foes, "不可击穿靶"), false, "（测试）"));
                list.Add(p);
            }

            // ── 空间机制专门预设 ────────────────────────────────────────

            {
                Preset p = new Preset
                {
                    Name = "地形对比：我方也有掩体",
                    Note = "与上一套编队完全相同，只把我方地形优势打开 —— 看射程差怎么决定谁先开火",
                };
                Squad(p.Units, ours, true);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 5, false);
                list.Add(p);
            }

            {
                Preset p = new Preset
                {
                    Name = "地形差放大：我方无掩体 + 无优势系数 0.4",
                    Note = "NoAdvantageRangeFactor 从 0.6 降到 0.4 —— 我方射程只剩 10 格",
                };
                p.Params.NoAdvantageRangeFactor = 0.4f;
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 5, false);
                list.Add(p);
            }

            {
                Preset p = new Preset
                {
                    Name = "近战混编：3 枪手 + 1 长剑",
                    Note = "近战会把全队拖到 1.5 格 —— 命中率上升，但也被迫进入肉搏",
                };
                string[] names = { "Chisa", "Denia", "阿花" };
                string[] picks = { "殖民者·突击步枪", "殖民者·霰弹枪", "殖民者·狙击枪" };
                for (int i = 0; i < picks.Length; i++)
                {
                    LabUnit u = LabUnit.FromTemplate(FindTemplate(ours, picks[i]), true);
                    u.Name = names[i];
                    u.HasTerrainAdvantage = false;
                    p.Units.Add(u);
                }
                LabUnit melee = LabUnit.FromTemplate(FindTemplate(ours, "殖民者·长剑"), true);
                melee.Name = "剑士";
                melee.HasTerrainAdvantage = false;
                p.Units.Add(melee);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 5, false);
                list.Add(p);
            }

            {
                Preset p = new Preset
                {
                    Name = "天气：温带森林（期望 ×0.90）",
                    Note = "与哨所局相同，叠加温带森林天气表 —— 每场随机抽一种天气",
                };
                p.Params.WeatherPreset = LabWeather.IndexOf("温带森林");
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 5, false);
                list.Add(p);
            }

            {
                Preset p = new Preset
                {
                    Name = "天气：恒定浓雾（×0.50）",
                    Note = "压力测试 —— 命中率腰斩，看战斗被拉长多少",
                };
                p.Params.WeatherPreset = LabWeather.IndexOf("恒定浓雾");
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 5, false);
                list.Add(p);
            }

            {
                Preset p = new Preset
                {
                    Name = "远距离开局：60 格",
                    Note = "把初始距离拉到 60 格 —— 接近阶段更长，谁射程远谁白打更多轮",
                };
                p.Params.StartDistance = 60f;
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 5, false);
                list.Add(p);
            }

            {
                Preset p = new Preset { Name = "同等兵力（4 vs 4 海盗）", Note = "势均力敌，看伤亡分布" };
                Squad(p.Units, ours, false);
                Many(p.Units, FindTemplate(foes, "海盗·步枪"), 4, false);
                list.Add(p);
            }

            return list;
        }
    }
}
