using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Poe2PriceGui.Models;
using Poe2PriceGui.Services;
using Poe2PriceGui.Windows;

namespace Poe2PriceGui.ViewModels;

/// <summary>
/// 主窗口 ViewModel：绑定价格表格、刷新与保存命令。
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly PoecurrencyPriceService _priceService;
    private readonly IconCacheService _iconCacheService;
    private readonly PriceDataService _priceDataService;
    private readonly ToastService _toastService;
    private readonly SettingsService _settingsService;
    private readonly PatchExportService _patchExportService;
    private readonly PatchInstaller _patchInstaller;
    private readonly HttpClient _httpClient;
    private readonly PoeTradeService _tradeService;
    private readonly UpdateService _updateService;
    private AppSettings _settings;
    private CancellationTokenSource? _autoSaveDebounceCts;
    private ObservableCollection<PoecurrencyItem> _prices = [];
    private string _statusMessage = "就绪";
    private string _lastRefreshTime = "无";
    private bool _isBusy;
    private int _editedCount;
    private ObservableCollection<string> _categories = [];
    private string _selectedCategory = "全部";
    private string _searchText = "";
    private string _cacheStatusMessage = "";
    private string _settingsStatusMessage = "";
    private bool _priceCheckerEnabled;
    private string _priceCheckerHotkey = "Ctrl+D";
    private string _priceCheckerPoeSessionId = "";
    private string _priceCheckerLeague = "奥杜尔秘符";
    private string _currencyPriceToken = "789486ce3baf2c4a7e18f4ba0b9aa4ab8edb9da64ca92bca10ca74c094cd8f8d";
    private ListCollectionView _filteredPrices = new(new ObservableCollection<PoecurrencyItem>());
    private PriceOverlayWindow? _currentOverlay;

    public MainViewModel()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Poe2PriceGui/1.0");
        _tradeService = new PoeTradeService(_httpClient);
        _priceService = new PoecurrencyPriceService(_httpClient);
        _iconCacheService = new IconCacheService(_httpClient);
        _priceDataService = new PriceDataService();
        _toastService = new ToastService();
        _settingsService = new SettingsService();
        _patchExportService = new PatchExportService();
        _patchInstaller = new PatchInstaller(_patchExportService);
        _updateService = new UpdateService();
        _settings = _settingsService.Load();
        _priceCheckerEnabled = _settings.PriceCheckerEnabled;
        _priceCheckerHotkey = _settings.PriceCheckerHotkey;
        _priceCheckerPoeSessionId = _settings.PriceCheckerPoeSessionId;
        _priceCheckerLeague = _settings.PriceCheckerLeague;
        _currencyPriceToken = _settings.CurrencyPriceToken;
        RefreshLastRefreshTimeDisplay();
        RefreshDetectedGameMode();

        RefreshCommand = new RelayCommand(async () => await RefreshPricesAsync(), () => !IsBusy);
        CleanCacheCommand = new RelayCommand(CleanCache, () => !IsBusy);
        OpenLogCommand = new RelayCommand(OpenLogFile, () => File.Exists(AppLogger.Instance.LogFilePath));
        CleanLogCommand = new RelayCommand(CleanLogs, () => Directory.Exists(AppLogger.Instance.LogDirectory) && Directory.GetFiles(AppLogger.Instance.LogDirectory, "*.log").Length > 0);
        ExportStatsCacheCommand = new RelayCommand(async () => await ExportStatsCacheAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(PriceCheckerPoeSessionId));
        ExportPricesCommand = new RelayCommand(async () => await ExportPricesAsync(), () => Prices.Count > 0);
        ExportPatchCommand = new RelayCommand(async () => await ExportPatchAsync(), () => Prices.Count > 0);
        InstallPatchCommand = new RelayCommand(async () => await InstallPatchAsync(), () => Prices.Count > 0);
        RestoreBackupCommand = new RelayCommand(async () => await RestoreBackupAsync(), () => !IsBusy);
        AutoDetectGameDirectoryCommand = new RelayCommand(ShowAutoDetectGameDirectory, () => !IsBusy);
        OpenPriceCheckerLoginCommand = new RelayCommand(OpenPriceCheckerLoginBrowser);
        CaptureHotkeyCommand = new RelayCommand(CaptureHotkey);
        TestPriceCheckerCommand = new RelayCommand(async () => await TestPriceCheckerAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(PriceCheckerPoeSessionId));
        CheckForUpdateCommand = new RelayCommand(async () => await CheckForUpdateAsync(), () => !IsBusy);

        _filteredPrices.Filter = FilterBySelectedCategory;

        // 启动时优先加载本地数据。
        _ = LoadLocalPricesAsync();
    }

    /// <summary>原始价格列表。</summary>
    public ObservableCollection<PoecurrencyItem> Prices
    {
        get => _prices;
        private set
        {
            if (SetProperty(ref _prices, value))
            {
                RefreshCategoriesAndFilter();
            }
        }
    }

    /// <summary>按当前选中分类过滤后的价格视图，绑定到 DataGrid。</summary>
    public ICollectionView FilteredPrices => _filteredPrices;

    /// <summary>分类页签列表，末尾包含"全部"。</summary>
    public ObservableCollection<string> Categories
    {
        get => _categories;
        private set => SetProperty(ref _categories, value);
    }

    /// <summary>当前选中的分类页签。</summary>
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(IsAllCategorySelected));
                _filteredPrices.Refresh();
                StatusMessage = $"当前分类：{value}，共 {_filteredPrices.Count} 条";
            }
        }
    }

    /// <summary>当前是否为"全部"分类。</summary>
    public bool IsAllCategorySelected => SelectedCategory == "全部";

    /// <summary>物品搜索文本，仅在"全部"分类下生效。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _filteredPrices.Refresh();
            }
        }
    }

    /// <summary>底部状态栏文本。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>上次成功刷新价格的本地时间显示。</summary>
    public string LastRefreshTime
    {
        get => _lastRefreshTime;
        set => SetProperty(ref _lastRefreshTime, value);
    }

    /// <summary>是否正在执行后台操作。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>用户已编辑的价格条目数量。</summary>
    public int EditedCount
    {
        get => _editedCount;
        set => SetProperty(ref _editedCount, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand CleanCacheCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand CleanLogCommand { get; }
    public ICommand ExportStatsCacheCommand { get; }
    public ICommand ExportPricesCommand { get; }
    public ICommand ExportPatchCommand { get; }
    public ICommand InstallPatchCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand AutoDetectGameDirectoryCommand { get; }
    public ICommand OpenPriceCheckerLoginCommand { get; }
    public ICommand CaptureHotkeyCommand { get; }
    public ICommand TestPriceCheckerCommand { get; }
    public ICommand CheckForUpdateCommand { get; }

    /// <summary>
    /// 状态栏消息。设置页缓存清理状态文本。
    /// </summary>
    public string CacheStatusMessage
    {
        get => _cacheStatusMessage;
        set => SetProperty(ref _cacheStatusMessage, value);
    }

    /// <summary>
    /// Toast 通知列表，绑定到右上角提示面板。
    /// </summary>
    public ObservableCollection<ToastNotification> Toasts => _toastService.Toasts;

    /// <summary>
    /// 当前日志文件路径，显示在设置页。
    /// </summary>
    public string LogFilePath => AppLogger.Instance.LogFilePath;

    /// <summary>
    /// POE2 游戏根目录。
    /// </summary>
    public string GameDirectory
    {
        get => _settings.GameDirectory;
        set
        {
            if (_settings.GameDirectory != value)
            {
                _settings.GameDirectory = value;
                _settingsService.Save(_settings);
                OnPropertyChanged();
                RefreshDetectedGameMode();
                ((RelayCommand)InstallPatchCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private string _detectedGameMode = "未检测";

    /// <summary>
    /// 根据 GameDirectory 自动检测到的游戏版本。
    /// </summary>
    public string DetectedGameMode
    {
        get => _detectedGameMode;
        set => SetProperty(ref _detectedGameMode, value);
    }

    /// <summary>查价器是否启用。</summary>
    public bool PriceCheckerEnabled
    {
        get => _priceCheckerEnabled;
        set
        {
            if (SetProperty(ref _priceCheckerEnabled, value))
            {
                _settings.PriceCheckerEnabled = value;
                _settingsService.Save(_settings);
                PriceCheckerSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>查价器热键文本，例如 "Ctrl+D"。</summary>
    public string PriceCheckerHotkey
    {
        get => _priceCheckerHotkey;
        set
        {
            if (SetProperty(ref _priceCheckerHotkey, value))
            {
                _settings.PriceCheckerHotkey = value;
                _settingsService.Save(_settings);
                PriceCheckerSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>查价器 POESESSID。</summary>
    public string PriceCheckerPoeSessionId
    {
        get => _priceCheckerPoeSessionId;
        set
        {
            if (SetProperty(ref _priceCheckerPoeSessionId, value))
            {
                _settings.PriceCheckerPoeSessionId = value;
                _settingsService.Save(_settings);
                ((RelayCommand)ExportStatsCacheCommand).RaiseCanExecuteChanged();
                ((RelayCommand)TestPriceCheckerCommand).RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsLoggedIn));
                OnPropertyChanged(nameof(LoginStatusText));
                OnPropertyChanged(nameof(LoginStatusColor));
            }
        }
    }

    /// <summary>是否已登录（POESESSID 非空）。</summary>
    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(PriceCheckerPoeSessionId);

    /// <summary>登录状态文本。</summary>
    public string LoginStatusText => IsLoggedIn ? "已登录" : "未登录";

    /// <summary>登录状态颜色。</summary>
    public System.Windows.Media.Brush LoginStatusColor => IsLoggedIn
        ? System.Windows.Media.Brushes.DarkGreen
        : System.Windows.Media.Brushes.Gray;

    /// <summary>查价器目标赛季。</summary>
    public string PriceCheckerLeague
    {
        get => _priceCheckerLeague;
        set
        {
            if (SetProperty(ref _priceCheckerLeague, value))
            {
                _settings.PriceCheckerLeague = value;
                _settingsService.Save(_settings);
            }
        }
    }

    /// <summary>通货价格查询 Token，为空时使用公共接口，非空时使用 summary_validate 接口。</summary>
    public string CurrencyPriceToken
    {
        get => _currencyPriceToken;
        set
        {
            if (SetProperty(ref _currencyPriceToken, value))
            {
                _settings.CurrencyPriceToken = value;
                _settingsService.Save(_settings);
            }
        }
    }

    /// <summary>查价器开关/热键变更通知。</summary>
    public event EventHandler? PriceCheckerSettingsChanged;

    /// <summary>
    /// 执行查价器：读取剪贴板装备文本，解析后显示配置叠加层（不直接搜索）。
    /// 保证同一时刻只有一个叠加层。
    /// </summary>
    public async Task RunPriceCheckerAsync()
    {
        AppLogger.Instance.Info($"查价器触发，Enabled={PriceCheckerEnabled}, IsBusy={IsBusy}");

        if (!PriceCheckerEnabled || IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PriceCheckerLeague))
        {
            _toastService.ShowWarning("请先在设置页配置查价器目标赛季");
            return;
        }

        IsBusy = true;
        try
        {
            var itemText = ClipboardService.CopyItemTextFromGame();
            AppLogger.Instance.Info($"剪贴板获取文本长度：{itemText?.Length ?? 0}");
            if (string.IsNullOrWhiteSpace(itemText))
            {
                ShowOverlayError("未获取到装备信息", "请确保鼠标悬停在装备上后再按热键");
                return;
            }

            var itemInfo = ItemTextParser.Parse(itemText);
            AppLogger.Instance.Info($"解析结果：Name={itemInfo.Name}, BaseType={itemInfo.BaseType}, Rarity={itemInfo.Rarity}, ItemLevel={itemInfo.ItemLevel}, IsValid={itemInfo.IsValid}");
            if (!itemInfo.IsValid || itemInfo.Rarity == "Unknown")
            {
                ShowOverlayError("无法解析装备信息", "剪贴板内容不是有效的装备文本，请确保游戏窗口处于前台且鼠标悬停在装备上");
                AppLogger.Instance.Warn($"剪贴板内容前 100 字符：{itemText[..Math.Min(100, itemText.Length)]}");
                return;
            }

            // 构建可选搜索字段。
            var fields = new ObservableCollection<SearchField>();

            // 传奇物品默认选「名称」，其它默认选「基底」。
            if (itemInfo.IsUnique)
            {
                fields.Add(new SearchField { Label = "名称", Key = "name", Value = itemInfo.Name, IsSelected = true });
                if (!string.IsNullOrWhiteSpace(itemInfo.BaseType) && itemInfo.BaseType != itemInfo.Name)
                {
                    fields.Add(new SearchField { Label = "基底", Key = "type", Value = itemInfo.BaseType, IsSelected = false });
                }
            }
            else
            {
                fields.Add(new SearchField { Label = "基底", Key = "type", Value = itemInfo.BaseType, IsSelected = true });
            }

            // 物品等级（可作为筛选条件）。
            if (itemInfo.ItemLevel > 0)
            {
                fields.Add(new SearchField { Label = "物品等级", Key = "itemLevel", Value = itemInfo.ItemLevel.ToString(), IsSelected = false, IsNumeric = true });
            }

            // 稀有度。
            if (!string.IsNullOrWhiteSpace(itemInfo.Rarity) && itemInfo.Rarity != "Unknown")
            {
                fields.Add(new SearchField { Label = "稀有度", Key = "rarity", Value = itemInfo.Rarity, IsSelected = false });
            }

            // 插槽数量（用于搜索卓越装备等）。
            if (itemInfo.SocketCount > 0)
            {
                fields.Add(new SearchField { Label = "插槽", Key = "sockets", Value = itemInfo.SocketCount.ToString(), IsSelected = false, IsNumeric = true });
            }

            // 需求等级。
            if (itemInfo.RequiredLevel > 0)
            {
                fields.Add(new SearchField { Label = "需求等级", Key = "reqLevel", Value = itemInfo.RequiredLevel.ToString(), IsSelected = false, IsNumeric = true });
            }

            // 物品类别。
            if (!string.IsNullOrWhiteSpace(itemInfo.ItemClass))
            {
                fields.Add(new SearchField { Label = "类别", Key = "category", Value = itemInfo.ItemClass, IsSelected = false });
            }

            var viewModel = new PriceOverlayViewModel
            {
                ItemInfo = itemInfo,
                SearchFields = fields,
                SearchCallback = ExecuteOverlaySearchAsync,
            };

            ShowOverlay(viewModel);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "查价器执行失败");
            ShowOverlayError("查价失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 测试查价器：使用内置的猎首腰带文本模拟查价流程，不依赖剪贴板。
    /// </summary>
    public async Task TestPriceCheckerAsync()
    {
        AppLogger.Instance.Info("测试查价器触发");

        if (string.IsNullOrWhiteSpace(PriceCheckerLeague))
        {
            _toastService.ShowWarning("请先在设置页配置查价器目标赛季");
            return;
        }

        IsBusy = true;
        try
        {
            var itemText = @"物品类别: 腰带
稀有度: 传奇
猎首
重革腰带
--------
需求： 等级 50
--------
物品等级: 81
--------
{ 基底属性 }
晕眩阈值提高 23 (20-30)%
{ 基底属性 — 咒符 }
具有 2 (1-3) 个咒符位
--------
{ 传奇属性 — 属性 }
+35 (20-40) 力量
{ 传奇属性 — 属性 }
+22 (20-40) 敏捷
{ 传奇属性 — 生命 }
+59 (40-60) 生命上限
{ 传奇属性 }
当你击败稀有怪物时，获得它的词缀，持续 60 秒
--------
" + "\"骨骼是灵魂的居所，\n血肉是精神和世界交流的窗口，推动一切的力量就在心窝。\n即使有了这些，失去了头脑就没有自我。\"\n——冈姆军师拉维安加\n--------\n引路石掉落";

            var itemInfo = ItemTextParser.Parse(itemText);
            AppLogger.Instance.Info($"测试解析结果：Name={itemInfo.Name}, BaseType={itemInfo.BaseType}, Rarity={itemInfo.Rarity}, ItemLevel={itemInfo.ItemLevel}, IsValid={itemInfo.IsValid}");
            if (!itemInfo.IsValid || itemInfo.Rarity == "Unknown")
            {
                _toastService.ShowError("测试文本解析失败");
                return;
            }

            var fields = new ObservableCollection<SearchField>();

            if (itemInfo.IsUnique)
            {
                fields.Add(new SearchField { Label = "名称", Key = "name", Value = itemInfo.Name, IsSelected = true });
                if (!string.IsNullOrWhiteSpace(itemInfo.BaseType) && itemInfo.BaseType != itemInfo.Name)
                {
                    fields.Add(new SearchField { Label = "基底", Key = "type", Value = itemInfo.BaseType, IsSelected = false });
                }
            }
            else
            {
                fields.Add(new SearchField { Label = "基底", Key = "type", Value = itemInfo.BaseType, IsSelected = true });
            }

            if (itemInfo.ItemLevel > 0)
            {
                fields.Add(new SearchField { Label = "物品等级", Key = "itemLevel", Value = itemInfo.ItemLevel.ToString(), IsSelected = false, IsNumeric = true });
            }

            if (!string.IsNullOrWhiteSpace(itemInfo.Rarity) && itemInfo.Rarity != "Unknown")
            {
                fields.Add(new SearchField { Label = "稀有度", Key = "rarity", Value = itemInfo.Rarity, IsSelected = false });
            }

            if (itemInfo.RequiredLevel > 0)
            {
                fields.Add(new SearchField { Label = "需求等级", Key = "reqLevel", Value = itemInfo.RequiredLevel.ToString(), IsSelected = false, IsNumeric = true });
            }

            if (!string.IsNullOrWhiteSpace(itemInfo.ItemClass))
            {
                fields.Add(new SearchField { Label = "类别", Key = "category", Value = itemInfo.ItemClass, IsSelected = false });
            }

            var viewModel = new PriceOverlayViewModel
            {
                ItemInfo = itemInfo,
                SearchFields = fields,
                SearchCallback = ExecuteOverlaySearchAsync,
            };

            ShowOverlay(viewModel);
            _toastService.ShowInfo("已加载测试物品（猎首），可在叠加层中点击搜索");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "测试查价器执行失败");
            _toastService.ShowError($"测试失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 叠加层搜索回调：根据选中的字段执行搜索并显示结果。
    /// </summary>
    private async Task ExecuteOverlaySearchAsync(PriceOverlayViewModel vm)
    {
        try
        {
            var selectedFields = vm.SearchFields.Where(f => f.IsSelected).ToList();
            if (selectedFields.Count == 0)
            {
                vm.ShowError("请至少选择一个搜索条件");
                return;
            }

            var nameField = selectedFields.FirstOrDefault(f => f.Key == "name");
            var typeField = selectedFields.FirstOrDefault(f => f.Key == "type");
            var itemLevelField = selectedFields.FirstOrDefault(f => f.Key == "itemLevel");
            var rarityField = selectedFields.FirstOrDefault(f => f.Key == "rarity");

            // 确定搜索词：优先 name，其次 type。
            var searchTerm = nameField?.Value ?? typeField?.Value ?? "";
            var searchByType = nameField == null && typeField != null;

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                vm.ShowError("搜索条件中没有有效的名称或基底");
                return;
            }

            int? itemLevel = null;
            if (itemLevelField != null && int.TryParse(itemLevelField.Value, out var il))
            {
                itemLevel = il;
            }

            string? rarity = rarityField?.Value;

            // 收集选中的词缀（文本 + 类型，类型用于映射到正确的 stat 分类）。
            var selectedMods = vm.ItemInfo?.Mods?
                .Where(m => m.IsSelected)
                .Select(m => (m.Text, m.Type))
                .ToList();
            if (selectedMods != null && selectedMods.Count == 0)
            {
                selectedMods = null;
            }

            var isExactSearch = vm.IsExactSearch;

            AppLogger.Instance.Info($"叠加层搜索：league={PriceCheckerLeague}, term={searchTerm}, byType={searchByType}, ilvl={itemLevel}, rarity={rarity}, mods={selectedMods?.Count ?? 0}, exact={isExactSearch}");

            var searchResult = await _tradeService.SearchAsync(
                PriceCheckerLeague,
                searchTerm,
                PriceCheckerPoeSessionId,
                searchByType: searchByType,
                itemLevel: itemLevel,
                rarity: rarity,
                selectedMods: selectedMods,
                isExactSearch: isExactSearch);

            AppLogger.Instance.Info($"搜索结果：total={searchResult.Total}, ids={searchResult.ResultIds.Count}");
            if (searchResult.ResultIds.Count == 0)
            {
                vm.ShowError("未找到该物品的市集挂单");
                return;
            }

            // 存储搜索上下文到 ViewModel，供翻页使用。
            vm.SearchId = searchResult.SearchId;
            vm.AllResultIds = searchResult.ResultIds;
            vm.FetchPageCallback = FetchOverlayPageAsync;

            var pageIds = searchResult.ResultIds.Take(PriceOverlayViewModel.PageSize).ToList();
            var listings = await _tradeService.FetchAsync(
                searchResult.SearchId,
                pageIds,
                PriceCheckerPoeSessionId);

            if (listings.Count == 0)
            {
                vm.ShowError("未找到有效价格");
                return;
            }

            var totalPages = (int)Math.Ceiling(searchResult.ResultIds.Count / (double)PriceOverlayViewModel.PageSize);
            vm.ShowResults(
                $"共 {searchResult.Total} 条结果，第 1/{totalPages} 页，本页 {listings.Count} 条",
                listings.OrderBy(l => l.Amount));
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "叠加层搜索失败");
            vm.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 翻页回调：fetch 指定页的结果列表。
    /// </summary>
    private async Task FetchOverlayPageAsync(PriceOverlayViewModel vm, int newPage)
    {
        if (string.IsNullOrEmpty(vm.SearchId) || vm.AllResultIds.Count == 0) return;

        var pageIds = vm.AllResultIds
            .Skip(newPage * PriceOverlayViewModel.PageSize)
            .Take(PriceOverlayViewModel.PageSize)
            .ToList();

        if (pageIds.Count == 0) return;

        AppLogger.Instance.Info($"翻页请求：page={newPage + 1}, fetchCount={pageIds.Count}");

        var listings = await _tradeService.FetchAsync(
            vm.SearchId,
            pageIds,
            PriceCheckerPoeSessionId);

        if (listings.Count == 0)
        {
            vm.ErrorMessage = "该页无有效数据";
            return;
        }

        var totalPages = (int)Math.Ceiling(vm.AllResultIds.Count / (double)PriceOverlayViewModel.PageSize);
        vm.ResultSummary = $"共 {vm.AllResultIds.Count} 条结果，第 {newPage + 1}/{totalPages} 页，本页 {listings.Count} 条";
        vm.UpdatePageResults(listings.OrderBy(l => l.Amount), newPage);
    }

    /// <summary>
    /// 显示叠加层窗口（单实例：关闭旧的再显示新的）。
    /// </summary>
    private void ShowOverlay(PriceOverlayViewModel viewModel)
    {
        // 关闭已有叠加层。
        if (_currentOverlay != null)
        {
            try
            {
                _currentOverlay.Close();
            }
            catch { /* 忽略关闭异常 */ }
            _currentOverlay = null;
        }

        _currentOverlay = new PriceOverlayWindow(viewModel);
        _currentOverlay.Closed += (_, _) => _currentOverlay = null;
        _currentOverlay.Show();
    }

    /// <summary>
    /// 用 Topmost 叠加层显示错误信息，确保在游戏上方可见。
    /// </summary>
    private void ShowOverlayError(string title, string detail)
    {
        var viewModel = new PriceOverlayViewModel
        {
            ItemInfo = new ItemInfo { Name = title },
            IsConfigMode = false,
            ErrorMessage = detail,
        };
        ShowOverlay(viewModel);
    }

    private void RefreshDetectedGameMode()
    {
        var info = GameModeDetector.Detect(GameDirectory);
        DetectedGameMode = info.IsValid ? info.DisplayName : (string.IsNullOrWhiteSpace(info.ErrorMessage) ? "未检测" : info.ErrorMessage);
    }

    private void RefreshLastRefreshTimeDisplay()
    {
        LastRefreshTime = _settings.LastRefreshTime.HasValue
            ? _settings.LastRefreshTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "无";
    }

    private void ShowAutoDetectGameDirectory()
    {
        var viewModel = new AutoDetectGameDirectoryViewModel();
        var window = new AutoDetectGameDirectoryWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel,
        };

        viewModel.OnApply = path =>
        {
            GameDirectory = path;
            window.DialogResult = true;
            window.Close();
        };
        viewModel.OnCancel = () =>
        {
            window.DialogResult = false;
            window.Close();
        };

        window.ShowDialog();
    }

    private void OpenPriceCheckerLoginBrowser()
    {
        try
        {
            var window = new LoginBrowserWindow(PriceCheckerPoeSessionId)
            {
                Owner = Application.Current.MainWindow,
            };

            if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.CapturedPoeSessionId))
            {
                PriceCheckerPoeSessionId = window.CapturedPoeSessionId;
                _toastService.ShowSuccess("已自动获取并保存 POESESSID");
                AppLogger.Instance.Info("通过内置浏览器获取 POESESSID 成功");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "打开内置登录浏览器失败");
            _toastService.ShowError($"打开登录窗口失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 弹出热键捕获窗口：监听下一次按键组合，更新 PriceCheckerHotkey。
    /// 仅接受带修饰键（Ctrl/Alt/Shift/Win）的组合或功能键（F1-F12）。
    /// </summary>
    private void CaptureHotkey()
    {
        var owner = Application.Current.MainWindow;
        var captureWindow = new Window
        {
            Owner = owner,
            Title = "捕获热键",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
        };

        var label = new TextBlock
        {
            Text = "请按下热键组合（Esc 取消）\n例如：Ctrl+D、Alt+F1、Shift+Q",
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        captureWindow.Content = label;

        string? captured = null;

        captureWindow.KeyDown += (_, e) =>
        {
            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                captureWindow.Close();
                return;
            }

            // 修饰键单独按下不处理，等待主键。
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return;
            }

            var parts = new List<string>();
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            // 功能键（F1-F12）可单独使用，其余键必须配合修饰键。
            var isFunctionKey = e.Key.ToString().StartsWith("F", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(e.Key.ToString()[1..], out var fnNum) && fnNum >= 1 && fnNum <= 12;

            if (parts.Count == 0 && !isFunctionKey)
            {
                label.Text = "需要配合修饰键（Ctrl/Alt/Shift/Win）\n请重新按下热键组合（Esc 取消）";
                return;
            }

            parts.Add(e.Key.ToString());
            captured = string.Join("+", parts);
            captureWindow.Close();
        };

        captureWindow.ShowDialog();

        if (!string.IsNullOrWhiteSpace(captured))
        {
            PriceCheckerHotkey = captured;
            _toastService.ShowSuccess($"热键已更新为 {captured}");
        }
    }

    /// <summary>
    /// 设置页操作状态文本。
    /// </summary>
    public string SettingsStatusMessage
    {
        get => _settingsStatusMessage;
        set => SetProperty(ref _settingsStatusMessage, value);
    }

    /// <summary>
    /// 当前应用版本号。
    /// </summary>
    public string AppVersion => _updateService.CurrentVersion;

    /// <summary>
    /// 检查 GitHub Releases 是否有新版本。
    /// </summary>
    public async Task CheckForUpdateAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在检查更新...";

        try
        {
            var updateInfo = await _updateService.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                SettingsStatusMessage = $"当前已是最新版本（v{AppVersion}）";
                _toastService.ShowInfo($"当前已是最新版本（v{AppVersion}）");
                return;
            }

            var newVersion = updateInfo.TargetFullRelease.Version;
            AppLogger.Instance.Info($"发现新版本：{newVersion}");

            // 询问用户是否下载并安装更新。
            var result = MessageBox.Show(
                $"发现新版本 v{newVersion}！\n当前版本：v{AppVersion}\n\n是否立即下载并安装更新？",
                "发现新版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                SettingsStatusMessage = "已跳过本次更新";
                return;
            }

            // 下载更新包。
            SettingsStatusMessage = $"正在下载更新 v{newVersion}...";
            var downloaded = await _updateService.DownloadUpdatesAsync(updateInfo, progressPercent =>
            {
                SettingsStatusMessage = $"正在下载更新... {progressPercent}%";
            });

            if (!downloaded)
            {
                SettingsStatusMessage = "更新下载失败，请稍后重试";
                _toastService.ShowError("更新下载失败，请稍后重试");
                return;
            }

            SettingsStatusMessage = "更新下载完成，即将重启并安装...";
            _toastService.ShowSuccess("更新下载完成，即将重启并安装");

            // 短暂延迟让用户看到提示，然后应用更新并重启。
            await Task.Delay(1500);
            _updateService.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "检查更新失败");
            SettingsStatusMessage = $"检查更新失败：{ex.Message}";
            _toastService.ShowError($"检查更新失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 从 poecurrency.top 抓取最新价格。
    /// </summary>
    public async Task RefreshPricesAsync()
    {
        IsBusy = true;
        StatusMessage = "正在从 poecurrency.top 获取价格...";
        AppLogger.Instance.Info("开始刷新价格...");

        try
        {
            // 刷新前先读取本地旧数据，用于后续对比价格变动。
            var oldPrices = _priceDataService.LocalDataExists
                ? await _priceDataService.LoadAsync()
                : [];
            var oldPriceMap = oldPrices.ToDictionary(p => p.ItemName, p => p.PriceExalted);
            AppLogger.Instance.Info($"读取本地旧数据 {oldPrices.Count} 条用于对比");

            var pricesTask = _priceService.FetchPricesAsync(token: CurrencyPriceToken);
            var mappingTask = _iconCacheService.LoadMappingAsync();

            await Task.WhenAll(pricesTask, mappingTask);

            var prices = await pricesTask;
            var changedCount = 0;
            AppLogger.Instance.Info($"从网络获取价格 {prices.Count} 条");

            // 订阅属性变更以统计编辑次数，并对比价格变动。
            foreach (var item in prices)
            {
                item.PropertyChanged += OnPriceItemPropertyChanged;

                if (oldPriceMap.TryGetValue(item.ItemName, out var oldPrice) && oldPrice != item.PriceExalted)
                {
                    item.IsPriceChanged = true;
                    changedCount++;
                }
            }

            Prices = prices;
            UpdateEditedCount();

            // 保存到本地。
            await _priceDataService.SaveAsync(prices);
            AppLogger.Instance.Info($"保存 {prices.Count} 条价格到本地");

            // 记录并保存最后刷新时间。
            _settings.LastRefreshTime = DateTime.UtcNow;
            _settingsService.Save(_settings);
            RefreshLastRefreshTimeDisplay();

            _toastService.ShowSuccess($"刷新成功，价格变动 {changedCount} 个物品");
            AppLogger.Instance.Info($"刷新成功，价格变动 {changedCount} 个物品");
            StatusMessage = $"已加载 {prices.Count} 条价格数据，正在加载图标...";

            // 异步加载图标并缓存到本地。
            _ = Task.Run(async () => await LoadIconsAsync(prices, CancellationToken.None));
        }
        catch (Exception ex)
        {
            StatusMessage = $"获取失败：{ex.Message}";
            AppLogger.Instance.Error(ex, "刷新价格失败");
            _toastService.ShowError($"刷新失败：{ex.Message}");
            MessageBox.Show($"获取价格失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 启动时优先加载本地保存的价格数据，并异步加载图标。
    /// </summary>
    private async Task LoadLocalPricesAsync()
    {
        if (!_priceDataService.LocalDataExists)
        {
            StatusMessage = "未找到本地数据，请点击刷新价格从网络获取";
            AppLogger.Instance.Info("启动时未找到本地价格数据");
            return;
        }

        try
        {
            StatusMessage = "正在加载本地价格数据...";
            AppLogger.Instance.Info($"开始从本地加载价格数据：{_priceDataService.DataFilePath}");
            var localPrices = await _priceDataService.LoadAsync();

            // 加载图标映射，用于后续图标显示。
            await _iconCacheService.LoadMappingAsync();

            foreach (var item in localPrices)
            {
                item.PropertyChanged += OnPriceItemPropertyChanged;
            }

            Prices = new ObservableCollection<PoecurrencyItem>(localPrices);
            UpdateEditedCount();
            StatusMessage = $"已从本地加载 {localPrices.Count} 条价格数据，正在加载图标...";
            _toastService.ShowInfo($"已从本地加载 {localPrices.Count} 条价格数据");
            AppLogger.Instance.Info($"已从本地加载 {localPrices.Count} 条价格数据");

            // 异步加载图标并缓存到本地。
            _ = Task.Run(async () => await LoadIconsAsync(localPrices, CancellationToken.None));
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载本地数据失败：{ex.Message}";
            AppLogger.Instance.Error(ex, "加载本地价格数据失败");
            _toastService.ShowError($"加载本地数据失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 编辑后自动保存到本地，带 500ms 去抖。
    /// </summary>
    private async Task AutoSaveAsync()
    {
        _autoSaveDebounceCts?.Cancel();
        _autoSaveDebounceCts = new CancellationTokenSource();
        var token = _autoSaveDebounceCts.Token;

        try
        {
            await Task.Delay(500, token);
            await _priceDataService.SaveAsync(Prices);
            AppLogger.Instance.Info($"自动保存 {Prices.Count} 条价格数据");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"已自动保存 {Prices.Count} 条价格数据";
            });
        }
        catch (OperationCanceledException)
        {
            // 去抖被取消，忽略。
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "自动保存价格数据失败");
        }
    }

    private void OnPriceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PoecurrencyItem.IsEdited))
        {
            UpdateEditedCount();
        }

        if (e.PropertyName == nameof(PoecurrencyItem.PriceExalted) && sender is PoecurrencyItem item)
        {
            item.IsEdited = true;
            _ = AutoSaveAsync();
        }
    }

    private void UpdateEditedCount()
    {
        EditedCount = Prices.Count(p => p.IsEdited);
        ((RelayCommand)ExportPricesCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ExportPatchCommand).RaiseCanExecuteChanged();
        ((RelayCommand)InstallPatchCommand).RaiseCanExecuteChanged();
    }

    private async Task LoadIconsAsync(IEnumerable<PoecurrencyItem> prices, CancellationToken cancellationToken)
    {
        var loadedCount = 0;
        var missingCount = 0;
        var semaphore = new SemaphoreSlim(10, 10);

        async Task LoadIconAsync(PoecurrencyItem item)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!_iconCacheService.HasIcon(item.ItemName))
                {
                    AppLogger.Instance.Warn($"图标映射缺失：{item.ItemName}（分类：{item.CategoryLabel}）");
                    Interlocked.Increment(ref missingCount);
                    return;
                }

                var icon = await _iconCacheService.GetIconAsync(item.ItemName, cancellationToken);
                if (icon != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => item.IconImage = icon);
                    Interlocked.Increment(ref loadedCount);
                }
                else
                {
                    AppLogger.Instance.Warn($"图标加载结果为空：{item.ItemName}（分类：{item.CategoryLabel}）");
                    Interlocked.Increment(ref missingCount);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, $"图标加载异常：{item.ItemName}（分类：{item.CategoryLabel}）");
                Interlocked.Increment(ref missingCount);
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = prices.Select(LoadIconAsync).ToArray();
        await Task.WhenAll(tasks);

        AppLogger.Instance.Info($"图标加载完成：{loadedCount} 个成功，{missingCount} 个缺失");
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusMessage = $"图标加载完成：{loadedCount} 个成功，{missingCount} 个缺失";
        });
    }

    private void CleanCache()
    {
        try
        {
            var (deletedCount, freedBytes) = _iconCacheService.CleanCache();
            var freedMb = freedBytes / 1024.0 / 1024.0;
            CacheStatusMessage = $"已清理 {deletedCount} 个文件，释放 {freedMb:F2} MB";
            AppLogger.Instance.Info($"清理图标缓存：删除 {deletedCount} 个文件，释放 {freedMb:F2} MB");
            ((RelayCommand)OpenLogCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            CacheStatusMessage = $"清理失败：{ex.Message}";
            AppLogger.Instance.Error(ex, "清理图标缓存失败");
        }
    }

    private void OpenLogFile()
    {
        try
        {
            var logPath = AppLogger.Instance.LogFilePath;
            if (!File.Exists(logPath))
            {
                _toastService.ShowWarning("日志文件不存在");
                return;
            }

            Process.Start(new ProcessStartInfo(logPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "打开日志文件失败");
            _toastService.ShowError($"打开日志文件失败：{ex.Message}");
        }
    }

    private void CleanLogs()
    {
        try
        {
            var count = AppLogger.Instance.CleanLogs();
            SettingsStatusMessage = $"已清理 {count} 个日志文件";
            _toastService.ShowSuccess($"已清理 {count} 个日志文件");
            ((RelayCommand)OpenLogCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CleanLogCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "清理日志失败");
            _toastService.ShowError($"清理日志失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 手动导出查价器 stats 缓存到 data/stats_cache_debug.json，便于排查词缀匹配问题。
    /// 需要 POESESSID，若缓存未加载会先从 API 拉取。
    /// </summary>
    private async Task ExportStatsCacheAsync()
    {
        if (string.IsNullOrWhiteSpace(PriceCheckerPoeSessionId))
        {
            _toastService.ShowWarning("请先配置 POESESSID");
            return;
        }

        IsBusy = true;
        SettingsStatusMessage = "正在从 API 拉取 stats 数据并导出...";
        try
        {
            var count = await _tradeService.DumpStatsCacheAsync(PriceCheckerPoeSessionId);
            if (count > 0)
            {
                SettingsStatusMessage = $"stats 缓存已导出（{count} 条）到 data/stats_cache_debug.json";
                _toastService.ShowSuccess($"stats 缓存已导出（{count} 条）");
            }
            else
            {
                SettingsStatusMessage = "未获取到 stats 数据，请检查 POESESSID 是否有效及网络连接";
                _toastService.ShowError("未获取到 stats 数据，请检查 POESESSID 和网络");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "导出 stats 缓存失败");
            SettingsStatusMessage = $"导出失败：{ex.Message}";
            _toastService.ShowError($"导出 stats 缓存失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ((RelayCommand)ExportStatsCacheCommand).RaiseCanExecuteChanged();
        }
    }

    private async Task RestoreBackupAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在查找并还原备份...";

        try
        {
            var result = await _patchInstaller.RestoreLatestBackupAsync(GameDirectory);
            if (result.Success)
            {
                SettingsStatusMessage = $"已还原备份：{result.InstalledPath}";
                _toastService.ShowSuccess($"已还原备份：{result.InstalledPath}");
            }
            else
            {
                SettingsStatusMessage = $"还原备份失败：{result.ErrorMessage}";
                _toastService.ShowError($"还原备份失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "还原备份失败");
            SettingsStatusMessage = $"还原备份失败：{ex.Message}";
            _toastService.ShowError($"还原备份失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportPricesAsync()
    {
        try
        {
            var count = await _patchExportService.ExportPricesCsvAsync(Prices);
            await _patchExportService.ExportEditedPricesJsonAsync(Prices);
            SettingsStatusMessage = $"已导出 {count} 条价格到 {_patchExportService.OutputDirectory}";
            _toastService.ShowSuccess($"导出成功：{count} 条价格");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "导出价格失败");
            SettingsStatusMessage = $"导出失败：{ex.Message}";
            _toastService.ShowError($"导出失败：{ex.Message}");
        }
    }

    private async Task ExportPatchAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在生成补丁包...";

        try
        {
            var result = await _patchInstaller.ExportPatchZipAsync(Prices, GameDirectory);
            if (result.Success)
            {
                SettingsStatusMessage = $"[{result.GameMode}] 补丁包已生成：{result.InstalledPath}";
                _toastService.ShowSuccess($"[{result.GameMode}] 补丁包生成成功，导出 {result.ExportedCount} 条价格");
            }
            else
            {
                SettingsStatusMessage = $"补丁包生成失败：{result.ErrorMessage}";
                _toastService.ShowError($"补丁包生成失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "生成补丁包失败");
            SettingsStatusMessage = $"生成补丁包失败：{ex.Message}";
            _toastService.ShowError($"生成补丁包失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallPatchAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在生成并安装补丁...";

        try
        {
            var progress = new Progress<string>(msg => _toastService.ShowInfo(msg));
            var result = await _patchInstaller.InstallAsync(Prices, GameDirectory, progress);
            if (result.Success)
            {
                SettingsStatusMessage = $"补丁安装成功，备份：{result.BackupPath}";
                _toastService.ShowSuccess($"补丁安装成功\n导出 {result.ExportedCount} 条价格 · {result.GameMode} · 已备份");
            }
            else
            {
                SettingsStatusMessage = $"补丁安装失败：{result.ErrorMessage}";
                _toastService.ShowError($"补丁安装失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "安装补丁失败");
            SettingsStatusMessage = $"安装补丁失败：{ex.Message}";
            _toastService.ShowError($"安装补丁失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshCategoriesAndFilter()
    {
        var categoryList = Prices
            .Select(p => p.CategoryLabel)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        categoryList.Add("全部");

        Categories = new ObservableCollection<string>(categoryList);
        _selectedCategory = "全部";
        OnPropertyChanged(nameof(SelectedCategory));

        _filteredPrices = new ListCollectionView(Prices);
        _filteredPrices.Filter = FilterBySelectedCategory;
        OnPropertyChanged(nameof(FilteredPrices));

        // DataGrid 在 ItemsSource 切换时会清除新视图的 SortDescriptions，
        // 因此需在 DataGrid 绑定更新后再异步添加默认排序。
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            _filteredPrices.SortDescriptions.Add(new SortDescription(nameof(PoecurrencyItem.PriceExalted), ListSortDirection.Descending));
        }), System.Windows.Threading.DispatcherPriority.DataBind);
    }

    private bool FilterBySelectedCategory(object obj)
    {
        if (obj is not PoecurrencyItem item)
        {
            return false;
        }

        var matchesCategory = string.IsNullOrEmpty(SelectedCategory) || SelectedCategory == "全部" || item.CategoryLabel == SelectedCategory;
        if (!matchesCategory)
        {
            return false;
        }

        if (SelectedCategory != "全部" || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return item.ItemName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

}
