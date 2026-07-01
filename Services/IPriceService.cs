using System.Collections.ObjectModel;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 价格服务统一抽象，支持国服与国际服切换。
/// </summary>
public interface IPriceService
{
    /// <summary>数据来源说明，例如 "poecurrency.top (国服)"。</summary>
    string DataSourceLabel { get; }

    /// <summary>是否为国服价格源。</summary>
    bool IsChina { get; }

    /// <summary>拉取并归一化价格数据。</summary>
    Task<ObservableCollection<PoecurrencyItem>> FetchPricesAsync(CancellationToken cancellationToken = default);
}
