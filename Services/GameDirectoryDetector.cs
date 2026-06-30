using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 通过已安装软件列表、游戏平台配置等自动查找 POE2 游戏目录。
/// </summary>
public static class GameDirectoryDetector
{
    /// <summary>
    /// 查找所有候选目录。
    /// </summary>
    public static List<GameDirectoryCandidate> FindCandidates()
    {
        var candidates = new List<GameDirectoryCandidate>();

        AddRegistryCandidates(candidates);
        AddSteamCandidates(candidates);
        AddEpicCandidates(candidates);
        AddWeGameDefaultCandidates(candidates);

        // 去重并按路径排序。
        return candidates
            .GroupBy(c => c.Path)
            .Select(g => g.First())
            .OrderBy(c => c.Region)
            .ThenBy(c => c.Path)
            .ToList();
    }

    private static void AddRegistryCandidates(List<GameDirectoryCandidate> candidates)
    {
        var searchNames = new[] { "Path of Exile 2", "流放之路：降临", "流放之路" };
        var uninstallRoots = new[]
        {
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var root in uninstallRoots)
        {
            if (root == null) continue;
            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var displayName = subKey.GetValue("DisplayName") as string ?? "";
                var installLocation = subKey.GetValue("InstallLocation") as string ?? "";

                if (!searchNames.Any(n => displayName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    continue;

                TryAddCandidate(candidates, installLocation, $"已安装软件：{displayName}");
            }
        }
    }

    private static void AddSteamCandidates(List<GameDirectoryCandidate> candidates)
    {
        var libraryFolders = FindSteamLibraryFolders();
        foreach (var libraryPath in libraryFolders)
        {
            var gamePath = Path.Combine(libraryPath, "steamapps", "common", "Path of Exile 2");
            TryAddCandidate(candidates, gamePath, "Steam 库");
        }
    }

    private static List<string> FindSteamLibraryFolders()
    {
        var paths = new List<string>();
        var defaultSteamPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var defaultVdf = Path.Combine(defaultSteamPath, "steamapps", "libraryfolders.vdf");

        if (File.Exists(defaultVdf))
        {
            ExtractVdfPaths(defaultVdf, paths);
            paths.Add(Path.Combine(defaultSteamPath, "steamapps"));
        }

        return paths.Distinct().ToList();
    }

    private static void ExtractVdfPaths(string vdfPath, List<string> paths)
    {
        try
        {
            var text = File.ReadAllText(vdfPath);
            // VDF 格式示例："path"\t\t"D:\\SteamLibrary"
            var matches = Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"");
            foreach (Match match in matches)
            {
                var rawPath = match.Groups[1].Value.Replace("\\\\", "\\").Trim();
                if (Directory.Exists(rawPath))
                {
                    paths.Add(Path.Combine(rawPath, "steamapps"));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"解析 Steam libraryfolders.vdf 失败：{ex.Message}");
        }
    }

    private static void AddEpicCandidates(List<GameDirectoryCandidate> candidates)
    {
        var manifestDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) return;

        foreach (var itemFile in Directory.GetFiles(manifestDir, "*.item"))
        {
            try
            {
                var json = File.ReadAllText(itemFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("DisplayName", out var displayNameElement) ||
                    !root.TryGetProperty("InstallLocation", out var installLocationElement))
                {
                    continue;
                }

                var displayName = displayNameElement.GetString() ?? "";
                var installLocation = installLocationElement.GetString() ?? "";

                if (!displayName.Contains("Path of Exile 2", StringComparison.OrdinalIgnoreCase))
                    continue;

                TryAddCandidate(candidates, installLocation, "Epic Games");
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warn($"解析 Epic manifest 失败 {itemFile}：{ex.Message}");
            }
        }
    }

    private static void AddWeGameDefaultCandidates(List<GameDirectoryCandidate> candidates)
    {
        var weGameBases = new[]
        {
            Path.Combine("D:", "WeGameApps", "rail_apps"),
            Path.Combine("C:", "WeGameApps", "rail_apps"),
        };

        foreach (var basePath in weGameBases)
        {
            if (!Directory.Exists(basePath)) continue;
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var name = Path.GetFileName(dir);
                if (name.Contains("流放之路", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Path of Exile", StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCandidate(candidates, dir, "WeGame 默认路径");
                }
            }
        }
    }

    private static void TryAddCandidate(List<GameDirectoryCandidate> candidates, string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(path)) return;

        var info = GameModeDetector.Detect(path);
        if (!info.IsValid) return;

        var region = info.IsChina ? ServerRegion.China : ServerRegion.International;
        candidates.Add(new GameDirectoryCandidate
        {
            Path = path,
            Region = region,
            Source = source,
        });
    }
}
