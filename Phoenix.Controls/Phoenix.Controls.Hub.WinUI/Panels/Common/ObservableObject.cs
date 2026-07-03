using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

// Hand-rolled INotifyPropertyChanged base — sized for the panel ViewModels here.
// We avoid CommunityToolkit.Mvvm because adding the package would require a
// csproj edit; this keeps the Panels folder self-sufficient.
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }
}
