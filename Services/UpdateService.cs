using Velopack;
using Velopack.Sources;

namespace Poe2PriceGui.Services;

/// <summary>
/// 基于 Velopack 的自动更新服务，使用 GitHub Releases 作为更新源。
/// </summary>
public class UpdateService
{
    private readonly UpdateManager _updateManager;

    /// <summary>
    /// GitHub 仓库地址（发布时需改为公开仓库）。
    /// </summary>
    private const string RepoUrl = "https://github.com/john-worker/Poe2PriceGui";

    public UpdateService()
    {
        // 仓库改为公开后无需 accessToken。
        _updateManager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>
    /// 检查是否有可用更新。
    /// 返回 null 表示无更新或检查失败。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var info = await _updateManager.CheckForUpdatesAsync();
            AppLogger.Instance.Info($"更新检查完成：{(info == null ? "无新版本" : $"发现 {info.TargetFullRelease.Version}")}");
            return info;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "检查更新失败");
            return null;
        }
    }

    /// <summary>
    /// 下载更新包（不立即安装）。
    /// </summary>
    public async Task<bool> DownloadUpdatesAsync(UpdateInfo updateInfo, Action<int>? progressCallback = null)
    {
        try
        {
            await _updateManager.DownloadUpdatesAsync(updateInfo, progressCallback);
            AppLogger.Instance.Info($"更新包下载完成：{updateInfo.TargetFullRelease.Version}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "下载更新失败");
            return false;
        }
    }

    /// <summary>
    /// 应用已下载的更新并重启应用。
    /// </summary>
    public void ApplyUpdatesAndRestart(UpdateInfo updateInfo)
    {
        _updateManager.ApplyUpdatesAndRestart(updateInfo);
    }

    /// <summary>
    /// 当前应用版本号。
    /// </summary>
    public string CurrentVersion => _updateManager.CurrentVersion?.ToString() ?? "未知";
}
