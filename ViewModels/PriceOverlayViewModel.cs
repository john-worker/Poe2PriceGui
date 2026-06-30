using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Poe2PriceGui.Models;
using Poe2PriceGui.Services;

namespace Poe2PriceGui.ViewModels;

/// <summary>
/// 查价器叠加层 ViewModel，支持「配置搜索字段」和「显示结果」两种状态。
/// </summary>
public class PriceOverlayViewModel : INotifyPropertyChanged
{
    private bool _isConfigMode = true;
    private bool _isSearching;
    private bool _isExactSearch;
    private string _resultSummary = "";
    private string _errorMessage = "";
    private ItemInfo _itemInfo = new();
    private int _currentPage;
    private int _totalPages;
    private bool _canGoPrev;
    private bool _canGoNext;
    private bool _isPageChanging;

    /// <summary>每页显示的条目数。</summary>
    public const int PageSize = 10;

    /// <summary>搜索 ID，用于翻页时 fetch。</summary>
    public string SearchId { get; set; } = "";

    /// <summary>所有结果 ID 列表，翻页时按 PageSize 分片 fetch。</summary>
    public List<string> AllResultIds { get; set; } = [];

    /// <summary>当前页码（从 0 开始）。</summary>
    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    /// <summary>总页数。</summary>
    public int TotalPages
    {
        get => _totalPages;
        set
        {
            if (SetProperty(ref _totalPages, value))
            {
                OnPropertyChanged(nameof(HasPagination));
            }
        }
    }

    /// <summary>是否有翻页（总页数 > 1）。</summary>
    public bool HasPagination => TotalPages > 1;

    /// <summary>是否可以上一页。</summary>
    public bool CanGoPrev
    {
        get => _canGoPrev;
        set => SetProperty(ref _canGoPrev, value);
    }

    /// <summary>是否可以下一页。</summary>
    public bool CanGoNext
    {
        get => _canGoNext;
        set => SetProperty(ref _canGoNext, value);
    }

    /// <summary>是否正在翻页加载中。</summary>
    public bool IsPageChanging
    {
        get => _isPageChanging;
        set
        {
            if (SetProperty(ref _isPageChanging, value))
            {
                OnPropertyChanged(nameof(CanChangePage));
            }
        }
    }

    /// <summary>是否可以翻页（非加载中）。</summary>
    public bool CanChangePage => !IsPageChanging && !IsSearching;

    /// <summary>页码信息文本。</summary>
    public string PageInfo => TotalPages > 0
        ? $"第 {CurrentPage + 1}/{TotalPages} 页"
        : "";

    /// <summary>原始装备信息。</summary>
    public ItemInfo ItemInfo
    {
        get => _itemInfo;
        set
        {
            if (SetProperty(ref _itemInfo, value))
            {
                OnPropertyChanged(nameof(HasMods));
            }
        }
    }

    /// <summary>是否有词缀可显示。</summary>
    public bool HasMods => ItemInfo?.Mods != null && ItemInfo.Mods.Count > 0;

    /// <summary>可选搜索字段列表。</summary>
    public ObservableCollection<SearchField> SearchFields { get; set; } = [];

    private ObservableCollection<TradeListing> _listings = [];
    /// <summary>搜索结果列表。</summary>
    public ObservableCollection<TradeListing> Listings
    {
        get => _listings;
        set
        {
            _listings = value;
            OnPropertyChanged();
        }
    }

    /// <summary>true=配置模式（选字段），false=结果模式。</summary>
    public bool IsConfigMode
    {
        get => _isConfigMode;
        set
        {
            if (SetProperty(ref _isConfigMode, value))
            {
                OnPropertyChanged(nameof(IsResultsMode));
            }
        }
    }

    /// <summary>结果模式（!IsConfigMode），用于 XAML 绑定。</summary>
    public bool IsResultsMode => !IsConfigMode;

    /// <summary>是否有错误信息。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>是否无错误（用于 XAML 绑定隐藏错误时显示结果）。</summary>
    public bool HasNoError => string.IsNullOrEmpty(ErrorMessage);

    /// <summary>是否正在搜索中。</summary>
    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(CanSearch));
                OnPropertyChanged(nameof(CanChangePage));
            }
        }
    }

    /// <summary>是否可以搜索（!IsSearching）。</summary>
    public bool CanSearch => !IsSearching;

    /// <summary>是否精确搜索词缀数值。false=只匹配词缀类型，true=同时匹配具体数值。</summary>
    public bool IsExactSearch
    {
        get => _isExactSearch;
        set => SetProperty(ref _isExactSearch, value);
    }

    /// <summary>结果摘要。</summary>
    public string ResultSummary
    {
        get => _resultSummary;
        set => SetProperty(ref _resultSummary, value);
    }

    /// <summary>错误信息（非空时显示在结果区域）。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasNoError));
            }
        }
    }

    /// <summary>搜索命令。</summary>
    public ICommand SearchCommand { get; }

    /// <summary>返回配置模式命令。</summary>
    public ICommand BackCommand { get; }

    /// <summary>关闭窗口命令。</summary>
    public ICommand CloseCommand { get; }

    /// <summary>下一页命令。</summary>
    public ICommand NextPageCommand { get; }

    /// <summary>上一页命令。</summary>
    public ICommand PrevPageCommand { get; }

    /// <summary>搜索回调，由 MainViewModel 注入。</summary>
    public Func<PriceOverlayViewModel, Task>? SearchCallback { get; set; }

    /// <summary>翻页回调，由 MainViewModel 注入。</summary>
    public Func<PriceOverlayViewModel, int, Task>? FetchPageCallback { get; set; }

    /// <summary>关闭回调，由窗口注入。</summary>
    public Action? CloseAction { get; set; }

    public PriceOverlayViewModel()
    {
        SearchCommand = new RelayCommand(async () => await ExecuteSearchAsync(),
                                         () => !IsSearching);
        BackCommand = new RelayCommand(() => IsConfigMode = true);
        CloseCommand = new RelayCommand(() => CloseAction?.Invoke());
        NextPageCommand = new RelayCommand(async () => await ExecutePageChangeAsync(CurrentPage + 1),
                                           () => CanGoNext && CanChangePage);
        PrevPageCommand = new RelayCommand(async () => await ExecutePageChangeAsync(CurrentPage - 1),
                                           () => CanGoPrev && CanChangePage);
    }

    private async Task ExecuteSearchAsync()
    {
        if (IsSearching) return;
        IsSearching = true;
        ErrorMessage = "";

        try
        {
            if (SearchCallback != null)
            {
                await SearchCallback(this);
            }
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>显示错误信息并切换到结果模式。</summary>
    public void ShowError(string message)
    {
        ErrorMessage = message;
        Listings.Clear();
        ResultSummary = "";
        TotalPages = 0;
        CurrentPage = 0;
        IsConfigMode = false;
    }

    /// <summary>显示搜索结果并切换到结果模式，同时初始化分页状态。</summary>
    public void ShowResults(string summary, IEnumerable<TradeListing> results)
    {
        ErrorMessage = "";
        ResultSummary = summary;
        Listings = new ObservableCollection<TradeListing>(results);
        CurrentPage = 0;
        TotalPages = (int)Math.Ceiling(AllResultIds.Count / (double)PageSize);
        UpdatePaginationState();
        IsConfigMode = false;
    }

    /// <summary>翻页后更新结果列表（不重置页码）。</summary>
    public void UpdatePageResults(IEnumerable<TradeListing> results, int page)
    {
        ErrorMessage = "";
        CurrentPage = page;
        Listings = new ObservableCollection<TradeListing>(results);
        UpdatePaginationState();
    }

    /// <summary>更新翻页按钮可用状态和页码文本。</summary>
    private void UpdatePaginationState()
    {
        CanGoPrev = CurrentPage > 0;
        CanGoNext = CurrentPage < TotalPages - 1;
        OnPropertyChanged(nameof(PageInfo));
        ((RelayCommand)NextPageCommand).RaiseCanExecuteChanged();
        ((RelayCommand)PrevPageCommand).RaiseCanExecuteChanged();
    }

    private async Task ExecutePageChangeAsync(int newPage)
    {
        if (IsPageChanging || newPage < 0 || newPage >= TotalPages) return;
        if (FetchPageCallback == null) return;

        IsPageChanging = true;
        ErrorMessage = "";
        try
        {
            await FetchPageCallback(this, newPage);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"翻页失败：{ex.Message}";
        }
        finally
        {
            IsPageChanging = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

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
