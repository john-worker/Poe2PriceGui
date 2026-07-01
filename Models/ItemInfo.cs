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

    /// <summary>品质（+20 → 20）。</summary>
    public int Quality { get; set; }

    /// <summary>护甲值（参考 xiletrade-master equipment_filters.filters.ar）。</summary>
    public int Armour { get; set; }

    /// <summary>闪避值（参考 xiletrade-master equipment_filters.filters.ev）。</summary>
    public int Evasion { get; set; }

    /// <summary>能量护盾（参考 xiletrade-master equipment_filters.filters.es）。</summary>
    public int EnergyShield { get; set; }

    /// <summary>每秒总伤害（参考 xiletrade-master equipment_filters.filters.dps）。</summary>
    public int DpsTotal { get; set; }

    /// <summary>每秒物理伤害（参考 xiletrade-master equipment_filters.filters.pdps）。</summary>
    public int DpsPhys { get; set; }

    /// <summary>每秒元素伤害（参考 xiletrade-master equipment_filters.filters.edps）。</summary>
    public int DpsElem { get; set; }

    /// <summary>是否已腐化。</summary>
    public bool Corrupted { get; set; }

    /// <summary>是否已鉴定。</summary>
    public bool Identified { get; set; } = true;

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

