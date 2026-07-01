using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace Poe2PriceGui.Services;

/// <summary>
/// 调用 poe2_name_price_patch.py 与 Bundles2/GGPK 工具生成并安装补丁。
/// </summary>
public class PatchInstaller
{
    private const string DefaultLanguagePath = "data/balance/simplified chinese/baseitemtypes.datc64";

    private readonly PatchExportService _exportService;

    public PatchInstaller(PatchExportService exportService)
    {
        _exportService = exportService;
    }

    /// <summary>
    /// 仅生成 zip 补丁包到 output 目录，不修改游戏文件。
    /// </summary>
    public async Task<InstallResult> ExportPatchZipAsync(
        IEnumerable<Models.PoecurrencyItem> prices,
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        return await BuildAndMaybeInstallAsync(prices, gameDirectory, install: false, null, cancellationToken);
    }

    /// <summary>
    /// 生成补丁并安装到游戏目录。
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        IEnumerable<Models.PoecurrencyItem> prices,
        string gameDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await BuildAndMaybeInstallAsync(prices, gameDirectory, install: true, progress, cancellationToken);
    }

    /// <summary>
    /// 还原原始备份文件（ggpk_restore.zip / bundles2_backup.zip / .original）。
    /// </summary>
    public async Task<InstallResult> RestoreLatestBackupAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        var result = new InstallResult();

        var modeInfo = GameModeDetector.Detect(gameDirectory);
        if (!modeInfo.IsValid)
        {
            result.ErrorMessage = modeInfo.ErrorMessage;
            return result;
        }

        var backupDir = Path.Combine(_exportService.OutputDirectory, "backup");

        if (modeInfo.Mode == GameMode.GGPK)
        {
            var targetFile = Path.Combine(gameDirectory, "Content.ggpk");
            var restoreZip = Path.Combine(backupDir, "ggpk_restore.zip");

            // 优先从还原 zip 还原（仅几 MB），兼容旧版 Content.ggpk.original（100GB 完整复制）。
            if (File.Exists(restoreZip))
            {
                if (!TryResolveToolPaths(out var tools, out var toolError))
                {
                    result.ErrorMessage = toolError;
                    return result;
                }
                var restoreResult = await RestoreGgpkFromZipAsync(gameDirectory, restoreZip, tools, modeInfo, cancellationToken);
                if (!restoreResult.Success)
                {
                    return restoreResult;
                }
                AppLogger.Instance.Info($"从还原包还原：{restoreZip} -> {targetFile}");
                return restoreResult;
            }

            var ggpkOldBackup = Path.Combine(backupDir, "Content.ggpk.original");
            if (!File.Exists(ggpkOldBackup))
            {
                result.ErrorMessage = $"未找到 GGPK 还原包或原始备份文件：{restoreZip}";
                return result;
            }

            try
            {
                File.Copy(ggpkOldBackup, targetFile, overwrite: true);
                AppLogger.Instance.Info($"还原原始备份（旧格式）：{ggpkOldBackup} -> {targetFile}");
                result.Success = true;
                result.InstalledPath = targetFile;
                result.GameMode = modeInfo.DisplayName;
                result.BackupPath = ggpkOldBackup;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "还原备份失败");
                result.ErrorMessage = $"还原备份失败：{ex.Message}";
            }
            return result;
        }

        // Bundles2 模式：优先从 ZIP 还原，兼容旧版 .original 文件
        var zipBackup = Path.Combine(backupDir, "bundles2_backup.zip");
        if (File.Exists(zipBackup))
        {
            try
            {
                var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
                using var archive = ZipFile.OpenRead(zipBackup);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var destPath = Path.Combine(bundles2Dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                    AppLogger.Instance.Info($"还原备份文件：{entry.FullName} -> {destPath}");
                }
                result.Success = true;
                result.InstalledPath = Path.Combine(bundles2Dir, "_.index.bin");
                result.GameMode = modeInfo.DisplayName;
                result.BackupPath = zipBackup;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "从 ZIP 还原备份失败");
                result.ErrorMessage = $"从 ZIP 还原备份失败：{ex.Message}";
            }
            return result;
        }

        // 兼容旧版：仅还原 _.index.bin.original
        var oldBackup = Path.Combine(backupDir, "_.index.bin.original");
        var oldTarget = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");
        if (File.Exists(oldBackup))
        {
            try
            {
                File.Copy(oldBackup, oldTarget, overwrite: true);
                AppLogger.Instance.Info($"还原原始备份（旧格式）：{oldBackup} -> {oldTarget}");
                result.Success = true;
                result.InstalledPath = oldTarget;
                result.GameMode = modeInfo.DisplayName;
                result.BackupPath = oldBackup;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "还原备份失败");
                result.ErrorMessage = $"还原备份失败：{ex.Message}";
            }
            return result;
        }

        result.ErrorMessage = $"未找到备份文件：{zipBackup} 或 {oldBackup}";
        return result;
    }

    private async Task<InstallResult> BuildAndMaybeInstallAsync(
        IEnumerable<Models.PoecurrencyItem> prices,
        string gameDirectory,
        bool install,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new InstallResult();

        // 1. 导出 CSV。
        progress?.Report("1/6 正在导出价格数据...");
        var exportedCount = await _exportService.ExportPricesCsvAsync(prices, cancellationToken);
        result.ExportedCount = exportedCount;

        // 2. 检测游戏版本。
        progress?.Report("2/6 正在检测游戏版本...");
        var modeInfo = GameModeDetector.Detect(gameDirectory);
        if (!modeInfo.IsValid)
        {
            result.ErrorMessage = modeInfo.ErrorMessage;
            return result;
        }

        result.GameMode = modeInfo.DisplayName;

        // 3. 校验工具。
        progress?.Report("3/6 正在校验补丁工具...");
        if (!TryResolveToolPaths(out var tools, out var toolError))
        {
            result.ErrorMessage = toolError;
            return result;
        }

        // 4. 还原原始备份（如果存在），再提取/定位源数据文件。
        //    避免在已打补丁的文件上叠加补丁，导致 PatchBundle3 无法正确覆盖已有条目。
        var backupDir = Path.Combine(_exportService.OutputDirectory, "backup");
        string targetGameFile = modeInfo.Mode == GameMode.GGPK
            ? Path.Combine(gameDirectory, "Content.ggpk")
            : Path.Combine(gameDirectory, "Bundles2", "_.index.bin");

        if (modeInfo.Mode == GameMode.GGPK)
        {
            // GGPK 模式：优先从 ggpk_restore.zip 还原（仅几 MB），兼容旧版 Content.ggpk.original（100GB 完整复制）。
            var ggpkRestoreZip = Path.Combine(backupDir, "ggpk_restore.zip");
            if (File.Exists(ggpkRestoreZip))
            {
                progress?.Report("4/6 正在从还原包还原原始数据文件...");
                var restoreResult = await RestoreGgpkFromZipAsync(gameDirectory, ggpkRestoreZip, tools, modeInfo, cancellationToken);
                if (!restoreResult.Success)
                {
                    result.ErrorMessage = restoreResult.ErrorMessage;
                    return result;
                }
                AppLogger.Instance.Info($"安装前从还原包还原：{ggpkRestoreZip}");
            }
            else
            {
                var ggpkOldBackup = Path.Combine(backupDir, "Content.ggpk.original");
                if (File.Exists(ggpkOldBackup))
                {
                    progress?.Report("4/6 正在还原原始数据文件（旧格式完整备份）...");
                    try
                    {
                        File.Copy(ggpkOldBackup, targetGameFile, overwrite: true);
                        AppLogger.Instance.Info($"安装前还原原始备份（旧格式）：{ggpkOldBackup} -> {targetGameFile}");
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = $"还原原始备份失败：{ex.Message}";
                        return result;
                    }
                }
            }
        }
        else
        {
            // Bundles2 模式：优先从 ZIP 还原，兼容旧版 .original 文件
            var zipBackup = Path.Combine(backupDir, "bundles2_backup.zip");
            var oldBackup = Path.Combine(backupDir, "_.index.bin.original");
            if (File.Exists(zipBackup))
            {
                progress?.Report("4/6 正在还原原始数据文件...");
                try
                {
                    var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
                    using var archive = ZipFile.OpenRead(zipBackup);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var destPath = Path.Combine(bundles2Dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                    AppLogger.Instance.Info($"安装前从 ZIP 还原原始备份：{zipBackup}");
                }
                catch (Exception ex)
                {
                    result.ErrorMessage = $"还原原始备份失败：{ex.Message}";
                    return result;
                }
            }
            else if (File.Exists(oldBackup))
            {
                progress?.Report("4/6 正在还原原始数据文件...");
                try
                {
                    File.Copy(oldBackup, targetGameFile, overwrite: true);
                    AppLogger.Instance.Info($"安装前还原原始备份（旧格式）：{oldBackup} -> {targetGameFile}");
                }
                catch (Exception ex)
                {
                    result.ErrorMessage = $"还原原始备份失败：{ex.Message}";
                    return result;
                }
            }
        }

        string sourceDat;
        if (modeInfo.Mode == GameMode.Bundles2)
        {
            var zipBackup = Path.Combine(backupDir, "bundles2_backup.zip");
            var oldBackup = Path.Combine(backupDir, "_.index.bin.original");
            if (!File.Exists(zipBackup) && !File.Exists(oldBackup))
            {
                progress?.Report("4/6 正在从 Bundles2 提取原始数据文件...");
            }
            var extracted = await ExtractFromBundles2Async(gameDirectory, modeInfo.BaseItemsPath, tools.BundleExtractor, cancellationToken);
            if (!extracted.Success)
            {
                result.ErrorMessage = extracted.ErrorMessage;
                return result;
            }
            sourceDat = extracted.FilePath;
        }
        else
        {
            // GGPK 模式：使用 GGPKExtractor 从 Content.ggpk 提取 datc64 到临时目录。
            if (string.IsNullOrEmpty(tools.GgpkExtractor) || !File.Exists(tools.GgpkExtractor))
            {
                result.ErrorMessage = $"国际服 GGPK 模式需要 GGPKExtractor.exe，未找到：{tools.GgpkExtractor}";
                return result;
            }
            progress?.Report("4/6 正在从 Content.ggpk 提取原始数据文件...");
            var extracted = await ExtractFromGgpkAsync(gameDirectory, modeInfo.BaseItemsPath, tools.GgpkExtractor, cancellationToken);
            if (!extracted.Success)
            {
                result.ErrorMessage = extracted.ErrorMessage;
                return result;
            }
            sourceDat = extracted.FilePath;

            // 将提取的原始 datc64 打包成还原 zip（仅几 MB），避免备份整个 Content.ggpk（可达 100GB）。
            // 还原时用 PatchBundledGGPK3 将这些干净条目写回 Content.ggpk。
            var ggpkRestoreZip = Path.Combine(backupDir, "ggpk_restore.zip");
            try
            {
                CreateGgpkRestoreZip(ggpkRestoreZip, sourceDat, modeInfo.BaseItemsPath);
                AppLogger.Instance.Info($"已创建 GGPK 还原包：{ggpkRestoreZip}");
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"创建 GGPK 还原包失败：{ex.Message}";
                return result;
            }
        }

        // 5. 生成补丁 datc64 与 zip。
        progress?.Report("5/6 正在生成补丁文件...");
        var patchedDat = Path.Combine(_exportService.OutputDirectory, "patched_baseitemtypes.datc64");
        var zipPath = Path.Combine(_exportService.OutputDirectory, "物价补丁.zip");
        var scriptPath = ResolvePatchScriptPath();
        var buildResult = await RunPythonPatchScriptAsync(
            scriptPath,
            sourceDat,
            _exportService.PricesCsvPath,
            patchedDat,
            modeInfo.BaseItemsPath,
            zipPath,
            cancellationToken);

        if (!buildResult.Success)
        {
            result.ErrorMessage = buildResult.ErrorMessage;
            return result;
        }

        if (!File.Exists(zipPath))
        {
            result.ErrorMessage = $"补丁 zip 未生成：{zipPath}";
            return result;
        }

        if (!install)
        {
            result.Success = true;
            result.InstalledPath = zipPath;
            AppLogger.Instance.Info($"补丁包生成完成：{zipPath}");
            return result;
        }

        // 6. 备份并安装补丁。
        progress?.Report("6/6 正在备份并安装补丁...");
        var installResult = modeInfo.Mode == GameMode.GGPK
            ? await InstallToGgpkAsync(gameDirectory, zipPath, tools, cancellationToken)
            : await InstallToBundles2Async(gameDirectory, zipPath, tools, cancellationToken);
        // 回填导出数量和游戏模式（安装方法创建新 InstallResult，需保留前序信息）。
        installResult.ExportedCount = exportedCount;
        installResult.GameMode = modeInfo.DisplayName;
        return installResult;
    }

    private async Task<InstallResult> InstallToGgpkAsync(
        string gameDirectory,
        string zipPath,
        ToolPaths tools,
        CancellationToken cancellationToken)
    {
        var ggpkPath = Path.Combine(gameDirectory, "Content.ggpk");
        // GGPK 模式不备份整个 Content.ggpk（可能高达 100GB），
        // 还原包（ggpk_restore.zip，仅含 datc64 小文件）在 BuildAndMaybeInstallAsync 提取阶段已创建。
        var result = new InstallResult
        {
            BackupPath = Path.Combine(_exportService.OutputDirectory, "backup", "ggpk_restore.zip")
        };

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{tools.PatchBundledGgpk}\" \"{ggpkPath}\" \"{zipPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        AppLogger.Instance.Info($"安装 GGPK 补丁：{psi.FileName} {psi.Arguments}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 dotnet 进程";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"GGPK 补丁安装失败：{error}";
            return result;
        }

        result.Success = true;
        result.InstalledPath = ggpkPath;
        AppLogger.Instance.Info($"GGPK 补丁安装完成：{ggpkPath}");
        return result;
    }

    private async Task<InstallResult> InstallToBundles2Async(
        string gameDirectory,
        string zipPath,
        ToolPaths tools,
        CancellationToken cancellationToken)
    {
        var indexBin = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");
        var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
        var backupDir = Path.Combine(_exportService.OutputDirectory, "backup");
        Directory.CreateDirectory(backupDir);
        // 使用 ZIP 保存"原始"备份，包含 _.index.bin、_.index.high.bin、_.index.low.bin、.index.dbg 和 LibGGPK3/ 目录
        // 只在首次安装时创建，避免备份已打补丁的文件导致无法还原。
        var zipBackupPath = Path.Combine(backupDir, "bundles2_backup.zip");
        var oldBackupPath = Path.Combine(backupDir, "_.index.bin.original");
        var result = new InstallResult
        {
            BackupPath = zipBackupPath
        };

        try
        {
            // 如果旧版 .original 存在但 ZIP 不存在，迁移旧备份并补充其他文件
            if (!File.Exists(zipBackupPath))
            {
                using (var archive = ZipFile.Open(zipBackupPath, ZipArchiveMode.Create))
                {
                    // 如果旧版 .original 存在，先将其加入 ZIP
                    if (File.Exists(oldBackupPath))
                    {
                        archive.CreateEntryFromFile(oldBackupPath, "_.index.bin");
                        AppLogger.Instance.Info($"迁移旧版备份到 ZIP：{oldBackupPath}");
                    }
                    else
                    {
                        // 首次备份 _.index.bin
                        archive.CreateEntryFromFile(indexBin, "_.index.bin");
                    }

                    // 备份其他索引文件
                    foreach (var name in new[] { "_.index.high.bin", "_.index.low.bin", ".index.dbg" })
                    {
                        var srcPath = Path.Combine(bundles2Dir, name);
                        if (File.Exists(srcPath))
                        {
                            archive.CreateEntryFromFile(srcPath, name);
                            AppLogger.Instance.Info($"备份文件：{name}");
                        }
                    }

                    // 备份 LibGGPK3 目录
                    var libDir = Path.Combine(bundles2Dir, "LibGGPK3");
                    if (Directory.Exists(libDir))
                    {
                        var files = Directory.GetFiles(libDir, "*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            var relative = file.Substring(bundles2Dir.Length + 1).Replace('\\', '/');
                            archive.CreateEntryFromFile(file, relative);
                            AppLogger.Instance.Info($"备份文件：{relative}");
                        }
                    }
                }
                AppLogger.Instance.Info($"创建 Bundles2 完整备份 ZIP：{zipBackupPath}");

                // 删除旧版 .original 文件（已迁移到 ZIP）
                if (File.Exists(oldBackupPath))
                {
                    File.Delete(oldBackupPath);
                }
            }
            else
            {
                AppLogger.Instance.Info($"原始备份 ZIP 已存在，跳过备份：{zipBackupPath}");
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"备份 Bundles2 文件失败：{ex.Message}";
            return result;
        }

        var psi = new ProcessStartInfo
        {
            FileName = tools.PatchBundle3,
            Arguments = $"\"{indexBin}\" \"{zipPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        AppLogger.Instance.Info($"安装 Bundles2 补丁：{psi.FileName} {psi.Arguments}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 PatchBundle3 进程";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"Bundles2 补丁安装失败：{error}";
            return result;
        }

        result.Success = true;
        result.InstalledPath = indexBin;
        AppLogger.Instance.Info($"Bundles2 补丁安装完成：{indexBin}");
        return result;
    }

    private async Task<ExtractionResult> ExtractFromBundles2Async(
        string gameDirectory,
        string virtualPath,
        string bundleExtractor,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult();
        var indexBin = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");
        var outputDir = Path.Combine(_exportService.OutputDirectory, "extracted");
        Directory.CreateDirectory(outputDir);
        result.FilePath = Path.Combine(outputDir, Path.GetFileName(virtualPath.Replace('/', Path.DirectorySeparatorChar)));

        var psi = new ProcessStartInfo
        {
            FileName = bundleExtractor,
            Arguments = $"\"{indexBin}\" \"{virtualPath}\" \"{result.FilePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        AppLogger.Instance.Info($"提取 Bundles2 文件：{psi.FileName} {psi.Arguments}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 BundleExtractor 进程";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"提取 {virtualPath} 失败：{error}";
            return result;
        }

        if (!File.Exists(result.FilePath))
        {
            result.ErrorMessage = $"提取后文件不存在：{result.FilePath}";
            return result;
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// GGPK 模式：调用 GGPKExtractor.exe 从 Content.ggpk 提取指定虚拟路径的文件到临时目录。
    /// GGPKExtractor 参数：&lt;Content.ggpk&gt; &lt;输出目录&gt;，提取后保持内部路径结构。
    /// </summary>
    private async Task<ExtractionResult> ExtractFromGgpkAsync(
        string gameDirectory,
        string virtualPath,
        string ggpkExtractor,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult();
        var contentGgpk = Path.Combine(gameDirectory, "Content.ggpk");
        if (!File.Exists(contentGgpk))
        {
            result.ErrorMessage = $"未找到 Content.ggpk：{contentGgpk}";
            return result;
        }

        var outputDir = Path.Combine(_exportService.OutputDirectory, "extracted_ggpk");
        Directory.CreateDirectory(outputDir);
        // GGPKExtractor 保持内部路径结构，提取后文件位于 outputDir/<virtualPath>。
        result.FilePath = Path.Combine(outputDir, virtualPath.Replace('/', Path.DirectorySeparatorChar));

        // 若已提取且文件存在，先删除避免覆盖冲突。
        if (File.Exists(result.FilePath))
        {
            try { File.Delete(result.FilePath); }
            catch { /* 忽略删除失败，GGPKExtractor 会覆盖 */ }
        }

        var psi = new ProcessStartInfo
        {
            FileName = ggpkExtractor,
            Arguments = $"\"{contentGgpk}\" \"{outputDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        AppLogger.Instance.Info($"提取 GGPK 文件：{psi.FileName} {psi.Arguments}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 GGPKExtractor 进程";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"GGPKExtractor 提取失败（退出码 {process.ExitCode}）：{error}";
            return result;
        }

        if (!File.Exists(result.FilePath))
        {
            result.ErrorMessage = $"提取后文件不存在：{result.FilePath}，虚拟路径：{virtualPath}";
            return result;
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// 将提取的原始 datc64 打包成 GGPK 还原 zip。
    /// zip 内条目名为游戏内虚拟路径（如 data/balance/baseitemtypes.datc64），
    /// PatchBundledGGPK3 还原时按此路径写回 Content.ggpk。
    /// </summary>
    private static void CreateGgpkRestoreZip(string zipPath, string sourceDat, string virtualPath)
    {
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // 若已存在则覆盖，确保还原包始终对应最新提取的干净数据。
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        // 条目名使用 / 分隔符（GGPK 内部虚拟路径格式）。
        archive.CreateEntryFromFile(sourceDat, virtualPath.Replace('\\', '/'), CompressionLevel.Optimal);
    }

    /// <summary>
    /// 调用 PatchBundledGGPK3 将还原 zip 中的干净 datc64 条目写回 Content.ggpk。
    /// 替代旧的 100GB 完整文件复制还原方式。
    /// </summary>
    private async Task<InstallResult> RestoreGgpkFromZipAsync(
        string gameDirectory,
        string restoreZip,
        ToolPaths tools,
        GameModeInfo modeInfo,
        CancellationToken cancellationToken)
    {
        var ggpkPath = Path.Combine(gameDirectory, "Content.ggpk");
        var result = new InstallResult
        {
            BackupPath = restoreZip,
        };

        if (!File.Exists(ggpkPath))
        {
            result.ErrorMessage = $"未找到 Content.ggpk：{ggpkPath}";
            return result;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{tools.PatchBundledGgpk}\" \"{ggpkPath}\" \"{restoreZip}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        AppLogger.Instance.Info($"从还原包还原 GGPK：{psi.FileName} {psi.Arguments}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 dotnet 进程（PatchBundledGGPK3）";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"GGPK 还原失败（退出码 {process.ExitCode}）：{error}";
            return result;
        }

        result.Success = true;
        result.InstalledPath = ggpkPath;
        result.GameMode = modeInfo.DisplayName;
        AppLogger.Instance.Info($"GGPK 还原完成：{ggpkPath}");
        return result;
    }

    private async Task<ScriptResult> RunPythonPatchScriptAsync(
        string scriptPath,
        string sourceDat,
        string pricesCsv,
        string patchedDat,
        string gamePath,
        string outputZipPath,
        CancellationToken cancellationToken)
    {
        var result = new ScriptResult();
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePythonPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("--source");
        psi.ArgumentList.Add(sourceDat);
        psi.ArgumentList.Add("--prices");
        psi.ArgumentList.Add(pricesCsv);
        psi.ArgumentList.Add("--patched-dat");
        psi.ArgumentList.Add(patchedDat);
        psi.ArgumentList.Add("--game-path");
        psi.ArgumentList.Add(gamePath);
        psi.ArgumentList.Add("--output-zip");
        psi.ArgumentList.Add(outputZipPath);
        psi.ArgumentList.Add("--separator");
        psi.ArgumentList.Add("");

        AppLogger.Instance.Info($"生成补丁 datc64：{psi.FileName} {string.Join(" ", psi.ArgumentList)}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 Python 进程";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"补丁脚本执行失败：{error}";
            return result;
        }

        if (!File.Exists(patchedDat))
        {
            result.ErrorMessage = $"补丁文件未生成：{patchedDat}";
            return result;
        }

        var patchedNamesMatch = Regex.Match(output, @"patched names:\s*(\d+)");
        if (patchedNamesMatch.Success && int.TryParse(patchedNamesMatch.Groups[1].Value, out var patchedNames) && patchedNames == 0)
        {
            result.ErrorMessage = "补丁脚本未匹配到任何物品名称，请检查 prices.csv 中的物品名是否与游戏数据一致";
            return result;
        }

        result.Success = true;
        return result;
    }

    private static bool TryResolveToolPaths(out ToolPaths tools, out string error)
    {
        tools = new ToolPaths();
        error = "";

        tools.BundleExtractor = ResolveToolPath("tools", "BundleExtractor", "BundleExtractor.exe");
        if (!File.Exists(tools.BundleExtractor))
        {
            error = $"未找到 BundleExtractor.exe：{tools.BundleExtractor}";
            return false;
        }

        tools.PatchBundle3 = ResolveToolPath("tools", "PatchTools", "PatchBundle3.exe");
        if (!File.Exists(tools.PatchBundle3))
        {
            error = $"未找到 PatchBundle3.exe：{tools.PatchBundle3}";
            return false;
        }

        tools.PatchBundledGgpk = ResolveToolPath("tools", "PatchTools", "PatchBundledGGPK3.dll");
        if (!File.Exists(tools.PatchBundledGgpk))
        {
            error = $"未找到 PatchBundledGGPK3.dll：{tools.PatchBundledGgpk}";
            return false;
        }

        // GGPKExtractor 为国际服 GGPK 模式专用，国服不需要，此处仅解析路径不强制检查。
        tools.GgpkExtractor = ResolveToolPath("tools", "GGPKExtractor", "GGPKExtractor.exe");

        return true;
    }

    private static string ResolvePythonPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "tools", "python", "python.exe");
        return File.Exists(bundledPath) ? bundledPath : "python";
    }

    private static string ResolvePatchScriptPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "scripts", "poe2_name_price_patch.py");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "scripts",
            "poe2_name_price_patch.py");
        projectPath = Path.GetFullPath(projectPath);

        if (File.Exists(projectPath))
        {
            return projectPath;
        }

        return bundledPath;
    }

    private static string ResolveToolPath(params string[] parts)
    {
        var outputPath = Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var segments = new List<string> { AppContext.BaseDirectory, "..", "..", ".." };
        segments.AddRange(parts);
        return Path.GetFullPath(Path.Combine(segments.ToArray()));
    }

    private static void LogProcessOutput(string output, string error)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            AppLogger.Instance.Info($"子进程输出：{output}");
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppLogger.Instance.Warn($"子进程错误输出：{error}");
        }
    }

    private class ToolPaths
    {
        public string BundleExtractor { get; set; } = "";
        public string PatchBundle3 { get; set; } = "";
        public string PatchBundledGgpk { get; set; } = "";
        /// <summary>GGPKExtractor.exe 路径，国际服 GGPK 模式下必需，国服可缺省。</summary>
        public string GgpkExtractor { get; set; } = "";
    }

    private class ExtractionResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }

    private class ScriptResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
    }
}

public class InstallResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int ExportedCount { get; set; }
    public string InstalledPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string GameMode { get; set; } = "";
}
