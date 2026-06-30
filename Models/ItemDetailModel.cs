namespace Poe2PriceGui.Models;

/// <summary>
/// 结构化的物品详情，用于在 Tooltip 中模拟官方弹窗样式。
/// </summary>
public class ItemDetailModel
{
    /// <summary>物品名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>基底类型。</summary>
    public string TypeLine { get; set; } = "";

    /// <summary>稀有度（Normal/Magic/Rare/Unique/Currency/Gem）。</summary>
    public string Rarity { get; set; } = "";

    /// <summary>物品等级。</summary>
    public int ItemLevel { get; set; }

    /// <summary>属性列表（咒符、品质、持续时间等）。</summary>
    public List<string> Properties { get; set; } = [];

    /// <summary>需求列表。</summary>
    public List<string> Requirements { get; set; } = [];

    /// <summary>插槽数量。</summary>
    public int Sockets { get; set; }

    /// <summary>隐式属性列表。</summary>
    public List<string> ImplicitMods { get; set; } = [];

    /// <summary>显式属性列表。</summary>
    public List<string> ExplicitMods { get; set; } = [];

    /// <summary>打造属性列表。</summary>
    public List<string> CraftedMods { get; set; } = [];

    /// <summary>是否已腐化。</summary>
    public bool Corrupted { get; set; }

    /// <summary>价格文本（如 "~b/o 155 神圣石"）。</summary>
    public string PriceText { get; set; } = "";

    /// <summary>是否为传奇物品。</summary>
    public bool IsUnique => Rarity.Contains("Unique", StringComparison.OrdinalIgnoreCase)
                           || Rarity.Contains("传奇", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为稀有物品。</summary>
    public bool IsRare => Rarity.Contains("Rare", StringComparison.OrdinalIgnoreCase)
                        || Rarity.Contains("稀有", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为魔法物品。</summary>
    public bool IsMagic => Rarity.Contains("Magic", StringComparison.OrdinalIgnoreCase)
                          || Rarity.Contains("魔法", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否有任何内容可显示。</summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(Name)
        || Properties.Count > 0
        || ImplicitMods.Count > 0
        || ExplicitMods.Count > 0;
}
