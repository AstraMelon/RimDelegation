using System.Collections.Generic;
using System.Globalization;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 一条阶段旁白（RIM-13 / RIM-14）。
    ///
    /// 为什么从 `string` 升级成条目：原来池子是一串纯文本，**没有任何"这句话现在能不能说"的判据**
    /// —— 于是"夜里才说的话"会在正午出现、"乐天派才说的话"会由悲观的人说出口
    /// （用户 S26 原话：「暂时关闭一下流程的随机描述，有些不符合逻辑」）。
    ///
    /// 为什么 XML 里**仍然是 `<li>纯文本</li>`**、只在行首加一段可选的方括号指令：
    ///   · 全库 260 条旁白一条都不用改写（旧写法 = 无指令 = 永远可说），改动面从"重排 1400 行 XML"
    ///     降到"想加条件的那些行各加一个前缀"；
    ///   · RimWorld 的 `<li Class="...">` 逐字段写法会让这个文件膨胀一倍以上，且改一条文案要动五行；
    ///   · 热重载/读档/DefInjected 的行为都不变（还是 `List<string>`，解析在 `ResolveReferences` 之后按需做）。
    ///
    /// ── 行首指令语法（全部可选，`|` 分隔，标签之间也可以用逗号）────────────────
    ///   `[solo]                 ` 只有 1 个人时才说
    ///   `[group]                ` 多人时才说
    ///   `[packed]               ` 队伍带驮兽时才说
    ///   `[hostile]              ` 该地点真有守军时才说
    ///   `[night] / [day]        ` 当地时间 20:00–06:00 / 06:00–20:00
    ///   `[early] / [mid] / [late]` 本段进度 &lt;34% / 34–80% / &gt;80%
    ///   `w=2                    ` 权重（默认 1）
    ///   `p=80-100               ` 进度区间（百分比，可只写一头：`p=80-`）
    ///   `h=22-5                 ` 当地小时区间（跨零点照写，`h=22-5` = 22 点后或 5 点前）
    ///   `t=NaturalMood:2        ` **队伍里有人**带这个特质（分度特质必须写 degree）
    ///   `ts=NaturalMood:2       ` **这一段的说话人本人**带这个特质
    ///   `c=ColdSnap             ` 存在这个全球事件（`Find.World.GameConditionManager`）
    ///   `once                   ` 每支委派只说一次
    ///
    /// 例：`<li>[night,late|w=2] 天快亮了，火也快灭了。</li>`
    /// </summary>
    public class AmbientLine
    {
        /// <summary>去掉指令块之后的**正文**（玩家看到的那一句）。</summary>
        public string text = "";

        /// <summary>权重（默认 1）。抽签概率 ∝ <c>weight × (1 + 标签数)</c>（标签越多越"具体"，越该优先）。</summary>
        public float weight = 1f;

        /// <summary>只在进度 ≥ 这个比例时出现（0 = 不限）。</summary>
        public float minProgress;

        /// <summary>只在进度 ≤ 这个比例时出现（1 = 不限）。</summary>
        public float maxProgress = 1f;

        /// <summary>只在当地小时落在这个区间时出现（&lt;0 = 不限；`to &lt; from` 表示跨零点）。</summary>
        public float hourFrom = -1f;

        /// <summary>见 <see cref="hourFrom" />。</summary>
        public float hourTo = -1f;

        /// <summary>队伍里**任意一人**带这个特质即可（null = 不限）。</summary>
        public string traitAny;

        /// <summary>见 <see cref="traitAny" />；<see cref="int.MinValue" /> = 不限分度。</summary>
        public int traitAnyDegree = int.MinValue;

        /// <summary>**说话人本人**必须带这个特质（null = 不限）。</summary>
        public string traitSpeaker;

        /// <summary>见 <see cref="traitSpeaker" />。</summary>
        public int traitSpeakerDegree = int.MinValue;

        /// <summary>存在这个全球事件时才出现（null = 不限）。</summary>
        public string gameCondition;

        /// <summary>每支委派只说一次（见 <see cref="DelegationFlowState.ambientSeenOnce" />）。</summary>
        public bool once;

        /// <summary>这条旁白带的情境标签（`solo` / `packed` / `hostile` / `night` / `late` …）。</summary>
        public readonly List<string> tags = new List<string>();

        /// <summary>它来自哪个池（`ambientLines` / `ambientLinesSolo`…）。只用于稳定 id 与日志。</summary>
        public string sourceKey = "";

        /// <summary>它在原池里的下标。</summary>
        public int indexInSource;

        /// <summary>指令块解析失败的原文（null = 正常）。写进 `ConfigErrors` 提示 modder。</summary>
        public string syntaxError;

        /// <summary>FNV-1a（稳定 id）—— 存档里存的是它，不是下标（见 <see cref="DelegationFlowState.ambientPickId" />）。</summary>
        public int id;

        /// <summary>这句有没有"时机/说话人"判据（没有 ⇒ 任何时刻都能说）。</summary>
        public bool HasGates => tags.Count > 0 || minProgress > 0f || maxProgress < 1f
            || hourFrom >= 0f || !traitAny.NullOrEmpty() || !traitSpeaker.NullOrEmpty()
            || !gameCondition.NullOrEmpty() || once;

        /// <summary>抽签用的有效权重：权重 × 特异性（标签越多越具体）。</summary>
        public float EffectiveWeight => (weight <= 0f ? 0.01f : weight) * (1f + tags.Count);

        /// <summary>把 `sourceKey#indexInSource` 折成一个稳定 id。</summary>
        public static int MakeId(string sourceKey, int indexInSource)
        {
            return DelegationAmbient.StableHash((sourceKey ?? "?") + "#" + indexInSource);
        }
    }

    /// <summary>旁白标签词汇表 + 行首指令解析（RIM-13 / RIM-14 的唯一一份）。</summary>
    public static class AmbientLineSyntax
    {
        // ── 情境标签（由"此刻的状况"决定，不写在行上也会按来源池隐式带上）──
        public const string TagSolo = "solo";        // 只有 1 个人
        public const string TagGroup = "group";      // 多人
        public const string TagPacked = "packed";    // 带驮兽
        public const string TagHostile = "hostile";  // 该地点有守军
        public const string TagNight = "night";      // 当地 20:00–06:00
        public const string TagDay = "day";          // 当地 06:00–20:00
        public const string TagEarly = "early";      // 本段进度 < 34%
        public const string TagMid = "mid";          // 34% – 80%
        public const string TagLate = "late";        // > 80%
        public const string TagOnce = "once";        // 每支委派只说一次

        /// <summary>夜间起点（当地小时，含）。</summary>
        public const float NightFrom = 20f;

        /// <summary>夜间终点（当地小时，不含）。</summary>
        public const float NightTo = 6f;

        /// <summary>进度分档边界（`early` / `mid` / `late`）。</summary>
        public const float EarlyUntil = 0.34f;

        /// <summary>见 <see cref="EarlyUntil" />。</summary>
        public const float MidUntil = 0.80f;

        /// <summary>只给"来自哪个池"用的隐式标签（池本身就是一种情境判据，S14 定下的语义原样保留）。</summary>
        public static string ImplicitTag(string sourceKey)
        {
            switch (sourceKey)
            {
                case "ambientLinesSolo":
                case "workAmbientLinesSolo":
                    return TagSolo;
                case "ambientLinesPacked":
                case "workAmbientLinesPacked":
                    return TagPacked;
                case "ambientLinesHostile":
                case "workAmbientLinesHostile":
                    return TagHostile;
                default:
                    return null;   // 默认池 / 休息池：没有任何隐式标签 = 任何时刻都能说
            }
        }

        /// <summary>
        /// 切出行首指令块。返回 true = 有指令块（<paramref name="spec" /> 是方括号里的原文）。
        /// 没写方括号 ⇒ 一律当普通正文（**旧写法 100% 兼容**）。
        /// </summary>
        public static bool TrySplitDirective(string raw, out string spec, out string body)
        {
            spec = null;
            body = raw ?? "";
            if (body.Length < 2 || body[0] != '[')
            {
                return false;
            }
            int close = body.IndexOf(']', 1);
            // 只认"行首很近的地方"那个 `]`：正文里本来就有方括号的句子不会被误判
            if (close < 2 || close > 200)
            {
                return false;
            }
            spec = body.Substring(1, close - 1);
            body = body.Substring(close + 1).TrimStart();
            return true;
        }

        /// <summary>
        /// 解析整条（`raw` = XML 里的原样文本），来源池给出隐式标签与稳定 id。
        /// </summary>
        public static AmbientLine Parse(string raw, string sourceKey, int indexInSource)
        {
            AmbientLine line = new AmbientLine
            {
                sourceKey = sourceKey ?? "",
                indexInSource = indexInSource,
                id = AmbientLine.MakeId(sourceKey, indexInSource),
            };
            string spec;
            string body;
            if (TrySplitDirective(raw, out spec, out body))
            {
                line.text = body;
                ParseSpec(spec, line);
            }
            else
            {
                line.text = raw ?? "";
            }
            string implicitTag = ImplicitTag(sourceKey);
            if (implicitTag != null && !line.tags.Contains(implicitTag))
            {
                line.tags.Insert(0, implicitTag);
            }
            return line;
        }

        /// <summary>解析方括号里那一串（`|` 分隔；不带 `=` 的按逗号拆成标签）。</summary>
        private static void ParseSpec(string spec, AmbientLine line)
        {
            string[] parts = spec.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0)
                {
                    continue;
                }
                int eq = p.IndexOf('=');
                if (eq < 0)
                {
                    string[] tagParts = p.Split(',');
                    for (int t = 0; t < tagParts.Length; t++)
                    {
                        AddTag(tagParts[t].Trim(), line);
                    }
                    continue;
                }
                string key = p.Substring(0, eq).Trim();
                string val = p.Substring(eq + 1).Trim();
                ApplyKeyValue(key, val, line);
            }
        }

        private static void AddTag(string tag, AmbientLine line)
        {
            switch (tag)
            {
                case TagSolo:
                case TagGroup:
                case TagPacked:
                case TagHostile:
                case TagNight:
                case TagDay:
                case TagEarly:
                case TagMid:
                case TagLate:
                    if (!line.tags.Contains(tag))
                    {
                        line.tags.Add(tag);
                    }
                    return;
                case TagOnce:
                    line.once = true;
                    return;
                case "":
                    return;
                default:
                    line.syntaxError = "未知的旁白标签 `" + tag + "`（可用：" + TagSolo + " / " + TagGroup
                        + " / " + TagPacked + " / " + TagHostile + " / " + TagNight + " / " + TagDay
                        + " / " + TagEarly + " / " + TagMid + " / " + TagLate + " / " + TagOnce + "）";
                    return;
            }
        }

        private static void ApplyKeyValue(string key, string val, AmbientLine line)
        {
            switch (key)
            {
                case "w":
                {
                    float w;
                    if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out w) || w <= 0f)
                    {
                        line.syntaxError = "权重 `w=" + val + "` 不是正数";
                        return;
                    }
                    line.weight = w;
                    return;
                }
                case "p":
                {
                    float lo;
                    float hi;
                    if (!TryParseRange(val, 0f, 100f, out lo, out hi))
                    {
                        line.syntaxError = "进度区间 `p=" + val + "` 写错了（例：`p=80-100` / `p=80-`）";
                        return;
                    }
                    line.minProgress = lo / 100f;
                    line.maxProgress = hi / 100f;
                    return;
                }
                case "h":
                {
                    float lo;
                    float hi;
                    if (!TryParseRange(val, 0f, 24f, out lo, out hi))
                    {
                        line.syntaxError = "小时区间 `h=" + val + "` 写错了（例：`h=22-5` / `h=6-20`）";
                        return;
                    }
                    line.hourFrom = lo;
                    line.hourTo = hi;
                    return;
                }
                case "t":
                    SplitTrait(val, out line.traitAny, out line.traitAnyDegree, line);
                    return;
                case "ts":
                    SplitTrait(val, out line.traitSpeaker, out line.traitSpeakerDegree, line);
                    return;
                case "c":
                    if (val.NullOrEmpty())
                    {
                        line.syntaxError = "全球事件 `c=` 后面没有写 GameConditionDef 名";
                        return;
                    }
                    line.gameCondition = val;
                    return;
                default:
                    line.syntaxError = "未知的旁白指令 `" + key + "`（可用：w / p / h / t / ts / c / once）";
                    return;
            }
        }

        private static void SplitTrait(string val, out string defName, out int degree, AmbientLine line)
        {
            defName = null;
            degree = int.MinValue;
            if (val.NullOrEmpty())
            {
                line.syntaxError = "特质指令后面没有写 TraitDef 名";
                return;
            }
            int colon = val.IndexOf(':');
            if (colon < 0)
            {
                defName = val;
                return;
            }
            defName = val.Substring(0, colon).Trim();
            string d = val.Substring(colon + 1).Trim();
            if (d.Length == 0)
            {
                return;   // `t=NaturalMood:` = 只按特质、不看分度
            }
            int parsed;
            if (!int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                line.syntaxError = "特质分度 `" + val + "` 不是整数（例：`t=NaturalMood:2`）";
                defName = null;
                return;
            }
            degree = parsed;
        }

        /// <summary>`lo-hi`（`hi` 可省略 = 上界）。两端都夹到 [<paramref name="min" />, <paramref name="max" />]。</summary>
        private static bool TryParseRange(string val, float min, float max, out float lo, out float hi)
        {
            lo = min;
            hi = max;
            if (val.NullOrEmpty())
            {
                return false;
            }
            int dash = val.IndexOf('-');
            string a = dash < 0 ? val : val.Substring(0, dash);
            string b = dash < 0 ? "" : val.Substring(dash + 1);
            bool okA = true;
            bool okB = true;
            if (a.Trim().Length > 0)
            {
                okA = float.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lo);
            }
            if (b.Trim().Length > 0)
            {
                okB = float.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hi);
            }
            if (!okA || !okB)
            {
                return false;
            }
            if (dash < 0)
            {
                hi = lo;   // `p=80` = 只在 80% 那一刻（罕见的写法，但要有个确定语义）
            }
            lo = lo < min ? min : (lo > max ? max : lo);
            hi = hi < min ? min : (hi > max ? max : hi);
            return true;
        }
    }

    /// <summary>
    /// 一个池的解析缓存：靠**源列表引用**判断要不要重算（热重载会把 `List&lt;string&gt;` 换成新实例，
    /// 与 `DelegationDef.cachedFlow` 同一套判据）。
    /// </summary>
    public class AmbientPoolCache
    {
        public List<string> source;

        public List<AmbientLine> parsed;
    }

    /// <summary>
    /// 一段（或一条委派）的**四个池 + 并集**。
    ///
    /// RIM-13 的核心改动：选池从"短路优先级链"改成**多标签并集** ——
    /// 原来"单人 + 有守军"只能命中单人池（那 5 条敌情旁白永远不出现），
    /// 现在四个池里的每一句各自带标签，**只要它的标签都被当前状况满足就有资格被抽**。
    /// 默认池的句子没有隐式标签 ⇒ 永远在候选里（与改动前的兜底语义一致）。
    /// </summary>
    public class AmbientPoolSet
    {
        private readonly AmbientPoolCache[] slots = { new AmbientPoolCache(), new AmbientPoolCache(), new AmbientPoolCache(), new AmbientPoolCache() };

        private string[] sourceKey = new string[4];

        private List<string>[] sourceRef = new List<string>[4];

        private List<AmbientLine> union;

        /// <summary>
        /// 四个池的并集（顺序 = 默认 → 单人 → 驮兽 → 敌情，池内按 XML 顺序）。
        /// <paramref name="root" /> = 默认池的字段名（`ambientLines` 或 `workAmbientLines`），另三个由它推出来。
        /// </summary>
        public List<AmbientLine> Union(string root, List<string> baseLines, List<string> solo, List<string> packed,
            List<string> hostile)
        {
            List<string>[] src = { baseLines, solo, packed, hostile };
            string[] names = { root, root + "Solo", root + "Packed", root + "Hostile" };
            bool dirty = union == null;
            for (int i = 0; i < 4 && !dirty; i++)
            {
                if (!ReferenceEquals(sourceRef[i], src[i]) || sourceKey[i] != names[i])
                {
                    dirty = true;
                }
            }
            if (!dirty)
            {
                return union;
            }

            union = new List<AmbientLine>();
            for (int i = 0; i < 4; i++)
            {
                sourceRef[i] = src[i];
                sourceKey[i] = names[i];
                AmbientPoolCache slot = slots[i];
                if (src[i].NullOrEmpty())
                {
                    slot.source = src[i];
                    slot.parsed = new List<AmbientLine>();
                    continue;
                }
                if (!ReferenceEquals(slot.source, src[i]) || slot.parsed == null)
                {
                    slot.source = src[i];
                    slot.parsed = ParseAll(src[i], names[i]);
                }
                union.AddRange(slot.parsed);
            }
            return union;
        }

        /// <summary>池里所有行（含解析失败的，用于 `ConfigErrors`）。</summary>
        public static List<AmbientLine> ParseAll(List<string> raw, string sourceKey)
        {
            List<AmbientLine> list = new List<AmbientLine>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                list.Add(AmbientLineSyntax.Parse(raw[i], sourceKey, i));
            }
            return list;
        }
    }
}
