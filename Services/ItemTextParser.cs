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

    /// <summary>
    /// 解析装备文本，提取稀有度、名称、基础类型、物品等级、需求等级等字段。
    /// </summary>
    public static ItemInfo Parse(string text)
    {
        var info = new ItemInfo { FullText = text };
        if (string.IsNullOrWhiteSpace(text))
        {
            return info;
        }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
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
            ParseMods(lines, info);
            return info;
        }

        info.Name = nameLines[0];
        info.BaseType = nameLines.Count > 1 ? nameLines[1] : nameLines[0];

        ParseNumericFields(lines, info);
        ParseMods(lines, info);
        return info;
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
        }
    }
}
