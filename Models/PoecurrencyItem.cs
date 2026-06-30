using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Poe2PriceGui.Models;

/// <summary>
/// poecurrency.top 单个物品的价格观测数据。
/// 对应参考项目 build_poe2scout_price_patch.py 中归一化后的价格项。
/// </summary>
public class PoecurrencyItem : INotifyPropertyChanged
{
    private string _categoryLabel = "";
    private string _itemName = "";
    private string? _iconUrl;
    private BitmapImage? _iconImage;
    private decimal _latestBuy1;
    private decimal _latestSell1;
    private decimal _buyAverage;
    private decimal _sellAverage;
    private decimal _previousBuy1;
    private string _currencyUnit = "e";
    private bool _hasError;
    private string _errorInfo = "";
    private decimal _priceExalted;
    private decimal _divineExaltedRatio;
    private string _sourcePair = "";
    private bool _isPriceChanged;
    private bool _isEdited;

    /// <summary>分类名称，例如"通货仓库"。</summary>
    public string CategoryLabel
    {
        get => _categoryLabel;
        set => SetProperty(ref _categoryLabel, value);
    }

    /// <summary>物品名称（国服为中文）。</summary>
    public string ItemName
    {
        get => _itemName;
        set => SetProperty(ref _itemName, value);
    }

    /// <summary>道具图标远程 URL。</summary>
    public string? IconUrl
    {
        get => _iconUrl;
        set => SetProperty(ref _iconUrl, value);
    }

    /// <summary>已加载并缓存到本地的图标图像。</summary>
    public BitmapImage? IconImage
    {
        get => _iconImage;
        set => SetProperty(ref _iconImage, value);
    }

    /// <summary>最新求购价。</summary>
    public decimal LatestBuy1
    {
        get => _latestBuy1;
        set => SetProperty(ref _latestBuy1, value);
    }

    /// <summary>最新出售价。</summary>
    public decimal LatestSell1
    {
        get => _latestSell1;
        set => SetProperty(ref _latestSell1, value);
    }

    /// <summary>今日求购均价。</summary>
    public decimal BuyAverage
    {
        get => _buyAverage;
        set => SetProperty(ref _buyAverage, value);
    }

    /// <summary>今日出售均价。</summary>
    public decimal SellAverage
    {
        get => _sellAverage;
        set => SetProperty(ref _sellAverage, value);
    }

    /// <summary>前一日买价，用于 error 兜底。</summary>
    public decimal PreviousBuy1
    {
        get => _previousBuy1;
        set => SetProperty(ref _previousBuy1, value);
    }

    /// <summary>价格单位：d（神圣石）或 e（崇高石）。</summary>
    public string CurrencyUnit
    {
        get => _currencyUnit;
        set => SetProperty(ref _currencyUnit, value);
    }

    /// <summary>接口是否标记该物品价格异常。</summary>
    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    /// <summary>异常说明。</summary>
    public string ErrorInfo
    {
        get => _errorInfo;
        set => SetProperty(ref _errorInfo, value);
    }

    /// <summary>
    /// 折算为崇高石（E）后的最终价格，可编辑。
    /// 编辑后会触发 <see cref="IsEdited"/> 标记。
    /// </summary>
    public decimal PriceExalted
    {
        get => _priceExalted;
        set
        {
            if (SetProperty(ref _priceExalted, value))
            {
                OnPropertyChanged(nameof(DisplayPrice));
            }
        }
    }

    /// <summary>
    /// 当前 1 神圣石折算为崇高石的比例，用于在游戏中自动切换 e/d 显示。
    /// </summary>
    public decimal DivineExaltedRatio
    {
        get => _divineExaltedRatio;
        set
        {
            if (SetProperty(ref _divineExaltedRatio, value))
            {
                OnPropertyChanged(nameof(DisplayPrice));
            }
        }
    }

    /// <summary>
    /// 游戏中显示的价格文本，例如 [50e] 或 [1.2d]。
    /// </summary>
    public string DisplayPrice
    {
        get
        {
            if (PriceExalted < 1)
            {
                return "";
            }

            if (DivineExaltedRatio > 0 && PriceExalted >= DivineExaltedRatio)
            {
                var value = PriceExalted / DivineExaltedRatio;
                return $"[{value.ToString("0.##", CultureInfo.InvariantCulture)}d]";
            }

            return $"[{PriceExalted.ToString("0", CultureInfo.InvariantCulture)}e]";
        }
    }

    /// <summary>价格来源说明，例如"poecurrency.top/通货仓库/latest_buy1_only/e"。</summary>
    public string SourcePair
    {
        get => _sourcePair;
        set => SetProperty(ref _sourcePair, value);
    }

    /// <summary>用户是否手动编辑过该价格。</summary>
    public bool IsEdited
    {
        get => _isEdited;
        set => SetProperty(ref _isEdited, value);
    }

    /// <summary>与上一次本地缓存相比，最终价是否发生变动。</summary>
    public bool IsPriceChanged
    {
        get => _isPriceChanged;
        set => SetProperty(ref _isPriceChanged, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
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
        OnPropertyChanged(propertyName);
        return true;
    }
}
