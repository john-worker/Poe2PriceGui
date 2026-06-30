using System.Collections.ObjectModel;

namespace Poe2PriceGui.Models;

/// <summary>
/// 从剪贴板解析出的装备信息。
/// </summary>
public class ItemInfo
{
    /// <summary>稀有度，例如 Normal / Magic / Rare / Unique / Currency / Gem。</summary>
    public string Rarity { get; set; } = "";

    /// <summary>物品显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>基础类型（通货/传奇时与 Name 相同）。</summary>
    public string BaseType { get; set; } = "";

    /// <summary>物品类别（如"咒符"、"法杖"）。</summary>
    public string ItemClass { get; set; } = "";

    /// <summary>物品等级。</summary>
    public int ItemLevel { get; set; }

    /// <summary>需求等级。</summary>
    public int RequiredLevel { get; set; }

    /// <summary>插槽数量。</summary>
    public int SocketCount { get; set; }

    /// <summary>解析出的词缀/属性列表。</summary>
    public ObservableCollection<ItemMod> Mods { get; set; } = [];

    /// <summary>原始文本。</summary>
    public string FullText { get; set; } = "";

    /// <summary>是否解析成功。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Name);

    /// <summary>是否为传奇物品。</summary>
    public bool IsUnique => Rarity.Equals("传奇", StringComparison.OrdinalIgnoreCase)
                           || Rarity.Equals("Unique", StringComparison.OrdinalIgnoreCase);

    /// <summary>用于搜索的查询名称。</summary>
    public string SearchName => string.IsNullOrWhiteSpace(BaseType) ? Name : BaseType;
}

