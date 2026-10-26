using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Base de todos os ViewModels: avisa a tela (via binding) quando uma propriedade muda.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Atribui o valor e notifica a tela somente se ele realmente mudou.
    /// [CallerMemberName] preenche o nome da propriedade automaticamente.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
