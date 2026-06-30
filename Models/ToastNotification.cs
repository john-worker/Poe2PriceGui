using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Poe2PriceGui.Models;

/// <summary>
/// 右上角弹出的 Toast 通知项。
/// </summary>
public class ToastNotification : INotifyPropertyChanged
{
    private string _message = "";
    private Brush _background = Brushes.DodgerBlue;
    private Brush _foreground = Brushes.White;

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public Brush Background
    {
        get => _background;
        set => SetProperty(ref _background, value);
    }

    public Brush Foreground
    {
        get => _foreground;
        set => SetProperty(ref _foreground, value);
    }

    /// <summary>创建时间，用于调试或排序。</summary>
    public DateTime CreatedAt { get; } = DateTime.Now;

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
