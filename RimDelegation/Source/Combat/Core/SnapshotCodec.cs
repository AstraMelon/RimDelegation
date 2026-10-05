using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimDelegation.Combat
{
    /// <summary>
    /// 快照的文本编解码器。
    ///
    /// 存在的意义：**把游戏内抓到的真实 pawn 数值搬到离线来跑**。
    /// 游戏侧的开发 harness（DebugGizmos）把当前车队/敌人 dump 成这个格式贴进日志，
    /// 本原型 / CombatLab 就能用它重演同一场战斗，不需要开游戏。
    ///
    /// v2 格式（行首 <c>#</c> 注释，<c>@</c> 场景参数，其余为单位）：
    /// <code>
    /// # RimDelegation combat snapshot v2
    /// @ticks=250
    /// @rounds=240
    /// @start=40            ← 初始交战距离（双方都够不着）
    /// @melee=1.5
    /// @nearband=8
    /// @noadv=0.6           ← 无地形优势者的射程乘子
    /// @closing=UntilShortestEngaged
    /// @enemyfactor=1
    /// @weather=Clear:1:18;Fog:0.5:1;SnowHard:0.8:4      ← 名称:命中乘子:权重，分号分隔
    /// mine|Chisa|hp=100|near=0.775|far=0.341|rng=25.9|move=4.6|melee=0|adv=1|shots=3|dmg=12|ap=0.16|armor=0.31|cat=Sharp|threat=27.1
    /// </code>
    ///
    /// **向后兼容 v1**：v1 只有 <c>hit=</c>（单一命中率、无距离概念），
    /// 读到时令 <c>near = far = hit</c>（即没有距离衰减），其余新字段取默认值。
    /// </summary>
    public static class SnapshotCodec
    {
        public const string Header = "# RimDelegation combat snapshot v2";
        private const string HeaderV1 = "# RimDelegation combat snapshot v1";

        public static string Encode(CombatScene scene)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Header);
            sb.AppendLine("@ticks=" + scene.TicksPerRound.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("@rounds=" + scene.MaxRounds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("@start=" + F(scene.StartDistance));
            sb.AppendLine("@melee=" + F(scene.MeleeRange));
            sb.AppendLine("@nearband=" + F(scene.NearBand));
            sb.AppendLine("@noadv=" + F(scene.NoAdvantageRangeFactor));
            sb.AppendLine("@standoff=" + F(scene.MaxStandoff));
            sb.AppendLine("@standoffpolicy=" + scene.Standoff);
            sb.AppendLine("@fallback=" + F(scene.FallbackFactor));
            sb.AppendLine("@closing=" + scene.Closing);
            sb.AppendLine("@cover=" + F(scene.CoverFactor));
            sb.AppendLine("@enemyfactor=" + F(scene.EnemyOutputFactor));
            sb.AppendLine("@disengage=" + F(scene.DisengageFactor));
            sb.AppendLine("@downfrac=" + F(scene.DownHealthFraction));
            sb.AppendLine("@ourprio=" + scene.OurPriority);
            sb.AppendLine("@enemyprio=" + scene.EnemyPriority);
            sb.AppendLine("@retreat=" + scene.Retreat.Kind + ":" + F(scene.Retreat.CasualtyFraction));
            sb.AppendLine("@weather=" + EncodeWeather(scene));

            for (int i = 0; i < scene.Units.Count; i++)
            {
                CombatUnitSnapshot u = scene.Units[i];
                sb.Append(u.IsMine ? "mine|" : "enemy|").Append(Esc(u.Name))
                  .Append("|hp=").Append(F(u.MaxHealth))
                  .Append("|near=").Append(F(u.AccuracyNear))
                  .Append("|far=").Append(F(u.AccuracyFar))
                  .Append("|rng=").Append(F(u.Range))
                  .Append("|move=").Append(F(u.MoveSpeed))
                  .Append("|melee=").Append(u.IsMelee ? "1" : "0")
                  .Append("|adv=").Append(u.HasTerrainAdvantage ? "1" : "0")
                  .Append("|shots=").Append(F(u.ShotsPerRound))
                  .Append("|dmg=").Append(F(u.DamagePerShot))
                  .Append("|ap=").Append(F(u.ArmorPen))
                  .Append("|armor=").Append(F(u.ArmorRating))
                  .Append("|cat=").Append(u.Category)
                  .Append("|threat=").Append(F(u.ThreatWeight))
                  .AppendLine();
            }
            return sb.ToString();
        }

        private static string EncodeWeather(CombatScene scene)
        {
            if (scene.WeatherTable.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < scene.WeatherTable.Count; i++)
            {
                WeatherSample w = scene.WeatherTable[i];
                if (i > 0) sb.Append(';');
                sb.Append(Esc(w.Name)).Append(':').Append(F(w.AccuracyMultiplier)).Append(':').Append(F(w.Weight));
            }
            return sb.ToString();
        }

        public static CombatScene Decode(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            CombatScene scene = new CombatScene();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line[0] == '@') { ApplySceneParam(scene, line.Substring(1)); continue; }

                string[] parts = line.Split('|');
                if (parts.Length < 3) throw new FormatException("无法解析快照行：" + line);

                CombatUnitSnapshot u = new CombatUnitSnapshot
                {
                    IsMine = parts[0].Trim().Equals("mine", StringComparison.OrdinalIgnoreCase),
                    Name = Unesc(parts[1]),
                };

                bool sawNear = false, sawFar = false, sawLegacyHit = false;
                float legacyHit = 0f;

                for (int p = 2; p < parts.Length; p++)
                {
                    int eq = parts[p].IndexOf('=');
                    if (eq <= 0) continue;
                    string key = parts[p].Substring(0, eq).Trim();
                    string val = parts[p].Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "hp": u.MaxHealth = ParseF(val); break;
                        case "near": u.AccuracyNear = ParseF(val); sawNear = true; break;
                        case "far": u.AccuracyFar = ParseF(val); sawFar = true; break;
                        case "hit": legacyHit = ParseF(val); sawLegacyHit = true; break;   // v1
                        case "rng": u.Range = ParseF(val); break;
                        case "move": u.MoveSpeed = ParseF(val); break;
                        case "melee": u.IsMelee = ParseB(val); break;
                        case "adv": u.HasTerrainAdvantage = ParseB(val); break;
                        case "shots": u.ShotsPerRound = ParseF(val); break;
                        case "dmg": u.DamagePerShot = ParseF(val); break;
                        case "ap": u.ArmorPen = ParseF(val); break;
                        case "armor": u.ArmorRating = ParseF(val); break;
                        case "threat": u.ThreatWeight = ParseF(val); break;
                        case "cat": u.Category = (ArmorCategory)Enum.Parse(typeof(ArmorCategory), val, true); break;
                    }
                }

                // v1 兼容：只有一个命中率 ⇒ 近远同值（无距离衰减）
                if (sawLegacyHit && !sawNear && !sawFar)
                {
                    u.AccuracyNear = legacyHit;
                    u.AccuracyFar = legacyHit;
                }

                scene.Add(u);
            }

            scene.Validate();
            return scene;
        }

        private static void ApplySceneParam(CombatScene scene, string kv)
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) return;
            string key = kv.Substring(0, eq).Trim();
            string val = kv.Substring(eq + 1).Trim();

            switch (key)
            {
                case "ticks": scene.TicksPerRound = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "rounds": scene.MaxRounds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "start": scene.StartDistance = ParseF(val); break;
                case "melee": scene.MeleeRange = ParseF(val); break;
                case "nearband": scene.NearBand = ParseF(val); break;
                case "noadv": scene.NoAdvantageRangeFactor = ParseF(val); break;
                case "standoff": scene.MaxStandoff = ParseF(val); break;
                case "standoffpolicy": scene.Standoff = (StandoffPolicy)Enum.Parse(typeof(StandoffPolicy), val, true); break;
                case "fallback": scene.FallbackFactor = ParseF(val); break;
                case "closing": scene.Closing = (ClosingPolicy)Enum.Parse(typeof(ClosingPolicy), val, true); break;
                case "cover": scene.CoverFactor = ParseF(val); break;
                case "enemyfactor": scene.EnemyOutputFactor = ParseF(val); break;
                case "disengage": scene.DisengageFactor = ParseF(val); break;
                case "downfrac": scene.DownHealthFraction = ParseF(val); break;
                case "ourprio": scene.OurPriority = (TargetPriority)Enum.Parse(typeof(TargetPriority), val, true); break;
                case "enemyprio": scene.EnemyPriority = (TargetPriority)Enum.Parse(typeof(TargetPriority), val, true); break;
                case "retreat":
                    string[] rp = val.Split(':');
                    scene.Retreat.Kind = (RetreatPolicyKind)Enum.Parse(typeof(RetreatPolicyKind), rp[0], true);
                    if (rp.Length > 1) scene.Retreat.CasualtyFraction = ParseF(rp[1]);
                    break;
                case "weather":
                    DecodeWeather(scene, val);
                    break;
            }
        }

        private static void DecodeWeather(CombatScene scene, string val)
        {
            scene.WeatherTable.Clear();
            if (string.IsNullOrEmpty(val)) return;

            string[] entries = val.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                string e = entries[i].Trim();
                if (e.Length == 0) continue;
                string[] f = e.Split(':');
                if (f.Length < 2) continue;
                WeatherSample w = new WeatherSample
                {
                    Name = Unesc(f[0]),
                    AccuracyMultiplier = ParseF(f[1]),
                    Weight = f.Length > 2 ? ParseF(f[2]) : 1f,
                };
                scene.WeatherTable.Add(w);
            }
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static float ParseF(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static bool ParseB(string s)
        {
            s = (s ?? "").Trim();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string Esc(string s) => (s ?? "").Replace("|", "/").Replace(";", ",").Replace(":", "-");

        private static string Unesc(string s) => s ?? "";
    }
}
