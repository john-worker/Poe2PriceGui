namespace Poe2PriceGui.Models;

/// <summary>
/// 从物品文本解析出的词缀/属性块。
/// </summary>
public class ItemMod
{
    /// <summary>词缀类型（如"基底属性"、"传奇属性"、"前缀属性"、"后缀属性"）。</summary>
    public string Type { get; set; } = "";

    /// <summary>词缀效果文本（可能多行）。</summary>
    public string Text { get; set; } = "";

    /// <summary>是否选中参与搜索。</summary>
    public bool IsSelected { get; set; } = false;

    /// <summary>显示用文本（类型 + 效果）。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Type) ? Text : $"[{Type}] {Text}";
}
