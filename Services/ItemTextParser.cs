using System.Buffers;
using System.Text.RegularExpressions;

namespace Poe2PriceGui.Services;

using Poe2PriceGui.Models;

/// <summary>
/// 解析 POE/POE2 剪贴板装备文本，同时支持国服（中文）与国际服（英文）格式。
/// </summary>
public static class ItemTextParser
{
    // 国服中文关键字
    private const string RarityZh = "稀有度:";
    private const string ItemClassZh = "物品类别:";
    private const string ItemLevelZh = "物品等级:";
    private const string RequirementZh = "需求";

    // 国际服英文关键字
    private const string RarityEn = "Rarity:";
    private const string ItemClassEn = "Item Class:";
    private const string ItemLevelEn = "Item Level:";
    private const string RequirementEn = "Requirements";

    // 物品等级正则：匹配 "物品等级: 80" 或 "Item Level: 80"
    private static readonly Regex ItemLevelRegex = new(
        @"(?:物品等级|Item\s*Level)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 需求等级正则：匹配 "需求： 等级 12" 或 "Requirements: Level 12" 或 "Level: 12"
    private static readonly Regex ReqLevelRegex = new(
        @"(?:等级|Level)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 插槽正则：匹配 "插槽: S S" 或 "Sockets: S S R" — 计算空格分隔的标记数。
    private static readonly Regex SocketRegex = new(
        @"(?:插槽|Sockets)\s*:?\s*(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 品质正则：匹配 "品质: +20%" 或 "Quality: +20%"
    private static readonly Regex QualityRegex = new(
        @"(?:品质|Quality)\s*:?\s*([+-]?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 护甲正则：匹配 "护甲值: 2673" / "护甲: 2673" / "Armour: 2673"
    private static readonly Regex ArmourRegex = new(
        @"(?:护甲值?|Armour)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 闪避正则：匹配 "闪避值: 2673" / "Evasion Rating: 2673" / "Evasion: 2673"
    private static readonly Regex EvasionRegex = new(
        @"(?:闪避值?|Evasion\s*Rating|Evasion)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 能量护盾正则：匹配 "能量护盾: 2673" / "Energy Shield: 2673"
    private static readonly Regex EnergyShieldRegex = new(
        @"(?:能量护盾|Energy\s*Shield)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 每秒总伤害正则：匹配 "每秒总伤害: 200" / "每秒伤害: 200" / "DPS: 200" / "Damage per Second: 200"
    private static readonly Regex DpsTotalRegex = new(
        @"(?:每秒总伤害|每秒伤害|DPS|Damage\s*per\s*Second)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 每秒物理伤害正则：匹配 "每秒物理伤害: 120" / "Physical DPS: 120" / "Phys DPS: 120"
    private static readonly Regex DpsPhysRegex = new(
        @"(?:每秒物理伤害|Physical\s*DPS|Phys\s*DPS|Pdps)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 每秒元素伤害正则：匹配 "每秒元素伤害: 80" / "Elemental DPS: 80" / "Elem DPS: 80" / "Edps: 80"
    private static readonly Regex DpsElemRegex = new(
        @"(?:每秒元素伤害|Elemental\s*DPS|Elem\s*DPS|Edps)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 解析装备文本，提取稀有度、名称、基础类型、物品等级、需求等级等字段。
    /// 先经过 NormalizeItemText 标准化预处理，再走原有解析逻辑。
    /// </summary>
    public static ItemInfo Parse(string text)
    {
        var info = new ItemInfo { FullText = text };
        if (string.IsNullOrWhiteSpace(text))
        {
            return info;
        }

        // 标准化预处理：统一行尾、移除空括号、解析 [key|value]、移除价格行。
        var normalizedText = NormalizeItemText(text.AsSpan());
        var lines = normalizedText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        // 解析物品类别
        var classLine = lines.FirstOrDefault(l =>
            l.StartsWith(ItemClassZh, StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith(ItemClassEn, StringComparison.OrdinalIgnoreCase));
        if (classLine != null)
        {
            var prefix = classLine.StartsWith(ItemClassZh, StringComparison.OrdinalIgnoreCase)
                ? ItemClassZh
                : ItemClassEn;
            info.ItemClass = classLine[prefix.Length..].Trim();
        }

        // 同时支持中英文 Rarity 行。
        var rarityLine = lines.FirstOrDefault(l =>
            l.StartsWith(RarityZh, StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith(RarityEn, StringComparison.OrdinalIgnoreCase));

        if (rarityLine == null)
        {
            // 没有稀有度行时，把第一条非「物品类别/Item Class」的行当名字。
            var firstContentLine = lines.FirstOrDefault(l =>
                !l.StartsWith(ItemClassZh, StringComparison.OrdinalIgnoreCase) &&
                !l.StartsWith(ItemClassEn, StringComparison.OrdinalIgnoreCase));

            info.Name = firstContentLine ?? "";
            info.BaseType = info.Name;
            info.Rarity = "Unknown";
            ParseNumericFields(lines, info);
            ParseItemFlags(lines, info);
            ParseMods(lines, info);
            return info;
        }

        // 提取稀有度值（去掉前缀）。
        var rarityPrefix = rarityLine.StartsWith(RarityZh, StringComparison.OrdinalIgnoreCase)
            ? RarityZh
            : RarityEn;
        info.Rarity = rarityLine[rarityPrefix.Length..].Trim();

        var rarityIndex = lines.IndexOf(rarityLine);
        var nameLines = lines
            .Skip(rarityIndex + 1)
            .TakeWhile(l => l != "--------")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (nameLines.Count == 0)
        {
            ParseNumericFields(lines, info);
            ParseItemFlags(lines, info);
            ParseMods(lines, info);
            return info;
        }

        info.Name = nameLines[0];
        info.BaseType = nameLines.Count > 1 ? nameLines[1] : nameLines[0];

        ParseNumericFields(lines, info);
        ParseItemFlags(lines, info);
        ParseMods(lines, info);
        return info;
    }

    /// <summary>
    /// 标准化物品文本：统一 CRLF 行尾、移除空括号 ()、解析 [key|value] 标记、移除价格行。
    /// 参考 xiletrade-master/InfoDescription.cs 的 NormalizeItemText 实现。
    /// </summary>
    private static string NormalizeItemText(ReadOnlySpan<char> input)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(input.Length * 2);
        int len = 0;

        // 1. 行尾标准化 + 移除空括号 "()"
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            // 移除 "()"
            if (c is '(' && i + 1 < input.Length && input[i + 1] is ')')
            {
                i++;
                continue;
            }

            // 行尾标准化：统一为 CRLF
            if (c is '\r')
            {
                if (i + 1 < input.Length && input[i + 1] is '\n')
                {
                    buffer[len++] = '\r';
                    buffer[len++] = '\n';
                    i++;
                }
                else
                {
                    // 孤独的 \r 也转为 CRLF
                    buffer[len++] = '\r';
                    buffer[len++] = '\n';
                }
                continue;
            }

            if (c is '\n')
            {
                buffer[len++] = '\r';
                buffer[len++] = '\n';
                continue;
            }

            buffer[len++] = c;
        }

        Span<char> span = buffer.AsSpan(0, len);

        // 2. 解析 [key|value] 标记 → value
        Span<char> bracketParsed = span.Length <= 2048
            ? stackalloc char[span.Length] : new char[span.Length];
        int write = 0;
        int j = 0;
        while (j < span.Length)
        {
            if (span[j] is '[')
            {
                int start = j + 1;
                int endRel = span[start..].IndexOf(']');
                if (endRel < 0)
                {
                    bracketParsed[write++] = span[j++];
                    continue;
                }
                int end = start + endRel;
                int pipeRel = span.Slice(start, endRel).IndexOf('|');
                ReadOnlySpan<char> part = pipeRel >= 0
                    ? span[(start + pipeRel + 1)..end]
                    : span[start..end];
                part.CopyTo(bracketParsed[write..]);
                write += part.Length;
                j = end + 1;
                continue;
            }
            bracketParsed[write++] = span[j++];
        }

        ReadOnlySpan<char> trimSpan = bracketParsed[..write];

        // 3. 全局 Trim
        int startTrim = 0;
        int endTrim = trimSpan.Length - 1;
        while (startTrim <= endTrim && char.IsWhiteSpace(trimSpan[startTrim])) startTrim++;
        while (endTrim >= startTrim && char.IsWhiteSpace(trimSpan[endTrim])) endTrim--;
        ReadOnlySpan<char> finalSpan = startTrim > endTrim
            ? trimSpan
            : trimSpan.Slice(startTrim, endTrim - startTrim + 1);

        // 4. 移除价格行（最后一行包含 ~b/o 或 price）
        string delimiter = "\r\n";
        int lastPos = finalSpan.LastIndexOf(delimiter);
        if (lastPos >= 0)
        {
            ReadOnlySpan<char> lastLine = finalSpan[(lastPos + delimiter.Length)..];
            if (lastLine.Contains("~b/o", StringComparison.OrdinalIgnoreCase) ||
                lastLine.Contains("price", StringComparison.OrdinalIgnoreCase) ||
                lastLine.Contains("注价", StringComparison.OrdinalIgnoreCase))
            {
                finalSpan = finalSpan[..lastPos];
            }
        }

        var result = finalSpan.ToString();
        ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        return result;
    }

    /// <summary>
    /// 解析 { } 词缀块。每个 { ... } 行是词缀头，后续行直到下一个 { } 或 -------- 是词缀效果。
    /// </summary>
    private static void ParseMods(List<string> lines, ItemInfo info)
    {
        string? currentModType = null;
        var currentModLines = new List<string>();

        foreach (var line in lines)
        {
            // 检测词缀头行：{ ... }
            if (line.StartsWith("{") && line.EndsWith("}"))
            {
                // 保存前一个词缀块。
                if (currentModType != null && currentModLines.Count > 0)
                {
                    info.Mods.Add(new ItemMod
                    {
                        Type = currentModType,
                        Text = string.Join(" ", currentModLines).Trim(),
                    });
                }

                // 提取词缀类型（去掉大括号和多余信息）。
                // 例如 "{ 传奇属性 — 咒符 }" → "传奇属性"
                //      "{ 前缀属性 \"易变的\" (等阶：1) — 闪避 }" → "前缀属性"
                var inner = line[1..^1].Trim();
                var typeEnd = inner.IndexOfAny(new[] { ' ', '—', '"' });
                currentModType = typeEnd > 0 ? inner[..typeEnd].Trim() : inner;
                currentModLines.Clear();
            }
            else if (line == "--------")
            {
                // 分隔符：保存当前词缀块。
                if (currentModType != null && currentModLines.Count > 0)
                {
                    info.Mods.Add(new ItemMod
                    {
                        Type = currentModType,
                        Text = string.Join(" ", currentModLines).Trim(),
                    });
                }
                currentModType = null;
                currentModLines.Clear();
            }
            else if (currentModType != null)
            {
                // 词缀效果行。
                currentModLines.Add(line);
            }
        }

        // 保存最后一个词缀块。
        if (currentModType != null && currentModLines.Count > 0)
        {
            info.Mods.Add(new ItemMod
            {
                Type = currentModType,
                Text = string.Join(" ", currentModLines).Trim(),
            });
        }
    }

    /// <summary>
    /// 从文本行中解析物品等级和需求等级。
    /// </summary>
    private static void ParseNumericFields(List<string> lines, ItemInfo info)
    {
        foreach (var line in lines)
        {
            // 物品等级
            if (info.ItemLevel == 0)
            {
                var m = ItemLevelRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var il))
                {
                    info.ItemLevel = il;
                    continue;
                }
            }

            // 需求等级（在 "需求：" 或 "Requirements:" 开头的行里找 Level）
            if (info.RequiredLevel == 0 &&
                (line.StartsWith(RequirementZh, StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith(RequirementEn, StringComparison.OrdinalIgnoreCase)))
            {
                var m = ReqLevelRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var rl))
                {
                    info.RequiredLevel = rl;
                }
            }

            // 插槽数量（"插槽: S S" → 2 个）
            if (info.SocketCount == 0 &&
                (line.StartsWith("插槽", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Sockets", StringComparison.OrdinalIgnoreCase)))
            {
                var m = SocketRegex.Match(line);
                if (m.Success)
                {
                    // 按空格分隔，计算标记数（S/R/U 等）。
                    var tokens = m.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    info.SocketCount = tokens.Length;
                }
            }

            // 品质（"品质: +20%" → 20）
            if (info.Quality == 0)
            {
                var m = QualityRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var q))
                {
                    info.Quality = q;
                }
            }

            // 护甲值（参考 xiletrade-master equipment_filters.filters.ar）
            if (info.Armour == 0)
            {
                var m = ArmourRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var ar))
                {
                    info.Armour = ar;
                }
            }

            // 闪避值（参考 xiletrade-master equipment_filters.filters.ev）
            if (info.Evasion == 0)
            {
                var m = EvasionRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var ev))
                {
                    info.Evasion = ev;
                }
            }

            // 能量护盾（参考 xiletrade-master equipment_filters.filters.es）
            if (info.EnergyShield == 0)
            {
                var m = EnergyShieldRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var es))
                {
                    info.EnergyShield = es;
                }
            }

            // 每秒总伤害（参考 xiletrade-master equipment_filters.filters.dps）
            if (info.DpsTotal == 0)
            {
                var m = DpsTotalRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var dps))
                {
                    info.DpsTotal = dps;
                }
            }

            // 每秒物理伤害（参考 xiletrade-master equipment_filters.filters.pdps）
            if (info.DpsPhys == 0)
            {
                var m = DpsPhysRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pdps))
                {
                    info.DpsPhys = pdps;
                }
            }

            // 每秒元素伤害（参考 xiletrade-master equipment_filters.filters.edps）
            if (info.DpsElem == 0)
            {
                var m = DpsElemRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var edps))
                {
                    info.DpsElem = edps;
                }
            }
        }
    }

    /// <summary>
    /// 解析物品标志：已腐化 / 未鉴定等。
    /// 参考 xiletrade-master ItemFlag.cs 的标志扫描逻辑。
    /// </summary>
    private static void ParseItemFlags(List<string> lines, ItemInfo info)
    {
        foreach (var line in lines)
        {
            if (line.Contains("已腐化", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Corrupted", StringComparison.OrdinalIgnoreCase))
            {
                info.Corrupted = true;
            }

            // "未鉴定" / "Unidentified" 表示物品未鉴定。
            // 注意：默认 Identified=true，只有明确出现"未鉴定"才置 false。
            if (line.Contains("未鉴定", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Unidentified", StringComparison.OrdinalIgnoreCase))
            {
                info.Identified = false;
            }
        }
    }
}
