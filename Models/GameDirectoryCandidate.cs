namespace Poe2PriceGui.Models;

/// <summary>
/// 区服类型。
/// </summary>
public enum ServerRegion
{
    /// <summary>国服（WeGame）。</summary>
    China,

    /// <summary>国际服（Steam / Epic / 官网）。</summary>
    International,
}

/// <summary>
/// 自动检测到的游戏目录候选。
/// </summary>
public class GameDirectoryCandidate
{
    /// <summary>游戏根目录。</summary>
    public string Path { get; set; } = "";

    /// <summary>区服。</summary>
    public ServerRegion Region { get; set; }

    /// <summary>检测来源描述。</summary>
    public string Source { get; set; } = "";

    /// <summary>区服显示文本。</summary>
    public string RegionText => Region == ServerRegion.China ? "国服" : "国际服";

    /// <summary>在列表中显示的完整文本。</summary>
    public string DisplayName => $"[{RegionText}] {Path} ({Source})";
}
