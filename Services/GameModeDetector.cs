using System.IO;

namespace Poe2PriceGui.Services;

/// <summary>
/// 检测 POE2 安装类型与客户端版本。
/// </summary>
public static class GameModeDetector
{
    public static GameModeInfo Detect(string gameDirectory)
    {
        var info = new GameModeInfo
        {
            GameDirectory = gameDirectory,
        };

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            info.ErrorMessage = "游戏目录未设置或不存在";
            return info;
        }

        var contentGgpk = Path.Combine(gameDirectory, "Content.ggpk");
        var bundles2Index = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");

        if (File.Exists(contentGgpk))
        {
            info.Mode = GameMode.GGPK;
            info.DisplayName = "国际服官方 GGPK";
            info.BaseItemsPath = "data/balance/baseitemtypes.datc64";
            info.WordsPath = "data/balance/words.datc64";
        }
        else if (File.Exists(bundles2Index))
        {
            info.Mode = GameMode.Bundles2;

            // WeGame 检测。
            var wegameScore = 0;
            var wegameMarkers = new[]
            {
                "wegame.ini",
                "rail_api64.dll",
                "rail_files",
                "WeGameLauncher",
                "TCLS",
                "AntiCheatExpert",
                "QQOpenSDK.dll",
            };
            foreach (var marker in wegameMarkers)
            {
                if (File.Exists(Path.Combine(gameDirectory, marker)) || Directory.Exists(Path.Combine(gameDirectory, marker)))
                {
                    wegameScore++;
                }
            }

            var msdkFiles = Directory.GetFiles(gameDirectory, "MSDK*.dll");
            if (msdkFiles.Length > 0)
            {
                wegameScore++;
            }

            if (wegameScore >= 2)
            {
                info.InstallKind = InstallKind.WeGameBundles2;
                info.DisplayName = "国服 WeGame Bundles2";
                info.IsChina = true;
            }
            else
            {
                info.InstallKind = InstallKind.SteamEpicBundles2;
                info.DisplayName = "国际服 Steam/Epic Bundles2";
                info.IsChina = false;
            }

            // Bundles2 默认使用简体中文路径。
            info.BaseItemsPath = "data/balance/simplified chinese/baseitemtypes.datc64";
            info.WordsPath = "data/balance/simplified chinese/words.datc64";
            info.EndgameMapsPath = "data/balance/simplified chinese/endgamemaps.datc64";
        }
        else
        {
            info.ErrorMessage = "无法检测游戏模式：找不到 Content.ggpk 或 Bundles2\\_.index.bin";
            return info;
        }

        info.IsValid = true;
        return info;
    }
}

public enum GameMode
{
    Unknown,
    GGPK,
    Bundles2,
}

public enum InstallKind
{
    Unknown,
    SteamEpicBundles2,
    WeGameBundles2,
    IntlStandaloneGGPK,
}

public class GameModeInfo
{
    public bool IsValid { get; set; }
    public string GameDirectory { get; set; } = "";
    public GameMode Mode { get; set; } = GameMode.Unknown;
    public InstallKind InstallKind { get; set; } = InstallKind.Unknown;
    public string DisplayName { get; set; } = "未知";
    public bool IsChina { get; set; }
    public string BaseItemsPath { get; set; } = "";
    public string WordsPath { get; set; } = "";
    public string EndgameMapsPath { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}
