using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Phoenix.Controls.Architect.WinUI.ViewModels;

// Minimal INotifyPropertyChanged base. Plenty of MVVM toolkits would do —
// CommunityToolkit.Mvvm in particular — but pulling a NuGet package only
// to get one class in this turn would expand Track 1's PackageReference
// surface unnecessarily. Track 5 may swap this for ObservableObject from
// Microsoft.Toolkit.Mvvm once the project becomes a real WinUI exe.
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Release every <see cref="PropertyChanged"/> subscriber. Field-like events
    /// can only be cleared from inside the declaring type, so derived VMs that
    /// implement <see cref="System.IDisposable"/> call this from their Dispose
    /// to break the pin-to-subscriber chain (otherwise long-lived listeners
    /// — UI bindings, host shells — keep the VM rooted past teardown).
    /// </summary>
    protected void ClearPropertyChangedSubscribers() => PropertyChanged = null;

    /// <summary>
    /// Set <paramref name="field"/> to <paramref name="value"/> if changed, raise
    /// PropertyChanged, return whether anything changed. Standard MVVM pattern.
    /// </summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
