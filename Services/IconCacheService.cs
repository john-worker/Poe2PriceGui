using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Poe2PriceGui.Services;

/// <summary>
/// 从 poecurrency.top 获取道具图标映射，并缓存图标到本地目录。
/// 缓存路径优先按照接口返回的 item_icon_local 保存，缺失时使用 URL 哈希作为文件名。
/// </summary>
public class IconCacheService
{
    private const string IconApiUrl = "https://poecurrency.top/api/db/currencies?version=2";

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private Dictionary<string, string> _iconUrls = [];
    private Dictionary<string, string> _iconLocalPaths = [];

    public IconCacheService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache");
    }

    /// <summary>图标缓存根目录。</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// 清理所有本地图标缓存文件。
    /// </summary>
    public (int deletedCount, long freedBytes) CleanCache()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return (0, 0);
        }

        var deletedCount = 0;
        long freedBytes = 0;

        foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                freedBytes += info.Length;
                File.Delete(file);
                deletedCount++;
            }
            catch
            {
                // 忽略无法删除的文件。
            }
        }

        // 删除空目录。
        foreach (var dir in Directory.EnumerateDirectories(_cacheDirectory, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            try
            {
                Directory.Delete(dir, recursive: false);
            }
            catch
            {
                // 忽略非空或无法删除的目录。
            }
        }

        return (deletedCount, freedBytes);
    }

    /// <summary>
    /// 获取图标映射。返回 item_name -> item_icon 的字典。
    /// </summary>
    public async Task LoadMappingAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(IconApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var urls = new Dictionary<string, string>();
        var locals = new Dictionary<string, string>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var itemName = GetString(element, "item_name");
            var iconUrl = GetString(element, "item_icon");
            var iconLocal = GetString(element, "item_icon_local");

            if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(iconUrl))
            {
                continue;
            }

            urls[itemName] = iconUrl;
            if (!string.IsNullOrWhiteSpace(iconLocal))
            {
                locals[itemName] = iconLocal;
            }
        }

        _iconUrls = urls;
        _iconLocalPaths = locals;
    }

    public bool HasIcon(string itemName)
    {
        return _iconUrls.ContainsKey(itemName);
    }

    /// <summary>
    /// 获取指定道具名称的图标。如果本地缓存不存在则先下载。
    /// </summary>
    public async Task<BitmapImage?> GetIconAsync(string itemName, CancellationToken cancellationToken = default)
    {
        if (!_iconUrls.TryGetValue(itemName, out var iconUrl))
        {
            AppLogger.Instance.Warn($"图标映射缺失：itemName={itemName}");
            return null;
        }

        var localPath = GetLocalCachePath(itemName, iconUrl);
        if (!File.Exists(localPath))
        {
            try
            {
                await DownloadIconAsync(itemName, iconUrl, localPath, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, $"图标下载失败：itemName={itemName}, url={iconUrl}, localPath={localPath}");
                return null;
            }
        }

        if (!File.Exists(localPath))
        {
            AppLogger.Instance.Error($"图标缓存文件不存在：itemName={itemName}, localPath={localPath}");
            return null;
        }

        try
        {
            return LoadBitmap(localPath);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, $"图标加载失败：itemName={itemName}, localPath={localPath}");
            return null;
        }
    }

    private string GetLocalCachePath(string itemName, string iconUrl)
    {
        if (_iconLocalPaths.TryGetValue(itemName, out var localRelativePath) &&
            !string.IsNullOrWhiteSpace(localRelativePath))
        {
            var relative = localRelativePath.TrimStart('/', '\\');
            return Path.Combine(_cacheDirectory, relative);
        }

        var fileName = $"{Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(iconUrl)))}.webp";
        return Path.Combine(_cacheDirectory, "icons", fileName);
    }

    private async Task DownloadIconAsync(string itemName, string url, string localPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = await _httpClient.GetByteArrayAsync(url, cancellationToken);
        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
        AppLogger.Instance.Info($"图标下载成功：itemName={itemName}, url={url}, localPath={localPath}");
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            return value.GetString()?.Trim() ?? "";
        }

        return "";
    }
}
