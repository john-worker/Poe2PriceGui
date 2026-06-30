namespace Poe2PriceGui.Models;

/// <summary>
/// 查价器可选搜索字段。
/// </summary>
public class SearchField
{
    /// <summary>显示标签（如"名称"、"基底"、"物品等级"）。</summary>
    public string Label { get; set; } = "";

    /// <summary>字段键（如 name / type / itemLevel）。</summary>
    public string Key { get; set; } = "";

    /// <summary>字段值。</summary>
    public string Value { get; set; } = "";

    /// <summary>是否选中参与搜索。</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>是否为数值字段（物品等级等）。</summary>
    public bool IsNumeric { get; set; }
}
