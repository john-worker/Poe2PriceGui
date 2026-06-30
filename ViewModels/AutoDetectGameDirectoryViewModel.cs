using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Poe2PriceGui.Models;
using Poe2PriceGui.Services;

namespace Poe2PriceGui.ViewModels;

/// <summary>
/// 自动查找游戏目录弹窗的 ViewModel。
/// </summary>
public class AutoDetectGameDirectoryViewModel : INotifyPropertyChanged
{
    private RegionOption _selectedRegion = null!;
    private ObservableCollection<GameDirectoryCandidate> _candidates = [];
    private GameDirectoryCandidate? _selectedCandidate;
    private bool _isScanning;

    public AutoDetectGameDirectoryViewModel()
    {
        RegionOptions =
        [
            new RegionOption { Value = ServerRegion.China, DisplayName = "国服" },
            new RegionOption { Value = ServerRegion.International, DisplayName = "国际服" },
        ];
        _selectedRegion = RegionOptions[0];

        ScanCommand = new RelayCommand(Scan, () => !IsScanning);
        ApplyCommand = new RelayCommand(Apply, () => SelectedCandidate != null);
        CancelCommand = new RelayCommand(Cancel);

        Scan();
    }

    /// <summary>
    /// 可选区服列表。
    /// </summary>
    public ObservableCollection<RegionOption> RegionOptions { get; }

    /// <summary>
    /// 当前选中的区服。
    /// </summary>
    public RegionOption SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                OnPropertyChanged(nameof(FilteredCandidates));
            }
        }
    }

    /// <summary>
    /// 所有检测到的候选目录。
    /// </summary>
    public ObservableCollection<GameDirectoryCandidate> Candidates
    {
        get => _candidates;
        private set
        {
            if (SetProperty(ref _candidates, value))
            {
                OnPropertyChanged(nameof(FilteredCandidates));
            }
        }
    }

    /// <summary>
    /// 按当前区服过滤后的候选目录。
    /// </summary>
    public ObservableCollection<GameDirectoryCandidate> FilteredCandidates
    {
        get
        {
            var filtered = Candidates.Where(c => c.Region == SelectedRegion.Value).ToList();
            return new ObservableCollection<GameDirectoryCandidate>(filtered);
        }
    }

    /// <summary>
    /// 当前选中的候选目录。
    /// </summary>
    public GameDirectoryCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value))
            {
                ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 是否正在扫描。
    /// </summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand ScanCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// 应用选中目录时的回调。
    /// </summary>
    public Action<string>? OnApply { get; set; }

    /// <summary>
    /// 取消时的回调。
    /// </summary>
    public Action? OnCancel { get; set; }

    private void Scan()
    {
        IsScanning = true;
        try
        {
            var found = GameDirectoryDetector.FindCandidates();
            Candidates = new ObservableCollection<GameDirectoryCandidate>(found);

            // 如果当前过滤后为空，自动切换到另一个区服试试。
            if (FilteredCandidates.Count == 0 && Candidates.Count > 0)
            {
                var option = RegionOptions.FirstOrDefault(r => r.Value == Candidates[0].Region);
                if (option != null)
                {
                    SelectedRegion = option;
                }
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Apply()
    {
        if (SelectedCandidate != null)
        {
            OnApply?.Invoke(SelectedCandidate.Path);
        }
    }

    private void Cancel()
    {
        OnCancel?.Invoke();
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
