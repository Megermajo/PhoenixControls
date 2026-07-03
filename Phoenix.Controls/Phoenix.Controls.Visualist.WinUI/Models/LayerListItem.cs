using System.ComponentModel;

namespace Phoenix.Controls.Visualist.WinUI.Models;

/// <summary>
/// Per-row state for Visualist's LayerRail. Sprint K converted this from a
/// positional record to a class-with-INotifyPropertyChanged so the rail's
/// {x:Bind Mode=OneWay} on Active reacts to live presence updates from
/// Hub's LayerRegistry without rebuilding the row.
///
/// A row can now also represent an UNSAVED /
/// virtual layer — the auto-created default and any new/unsaved document
/// that has no .phxlayer on disk yet. Such a row carries
/// <see cref="IsUnsaved"/> == true and an empty <see cref="Path"/>; the
/// VisualistViewModel keys off the empty path to avoid reloading it from
/// disk (there is no file) when the rail selects it.
/// </summary>
public sealed class LayerListItem : INotifyPropertyChanged
{
    /// <summary>
    /// Standard ctor for a saved layer enumerated from <c>data/layers/</c>.
    /// </summary>
    public LayerListItem(string path, string fileName, int width, int height, bool active, int fps)
    {
        Path     = path ?? "";
        FileName = fileName;
        Width    = width;
        Height   = height;
        _active  = active;
        _fps     = fps;
        IsUnsaved = false;
    }

    private LayerListItem(string fileName, int width, int height, int fps)
    {
        // Virtual/unsaved row — no file path. Active is meaningless for an
        // unsaved doc (no HUD presence), so it stays false.
        Path      = "";
        FileName  = fileName;
        Width     = width;
        Height    = height;
        _active   = false;
        _fps      = fps;
        IsUnsaved = true;
    }

    /// <summary>
    /// Build the synthetic "(unsaved)" rail row for an in-memory document that
    /// has not yet been saved to disk. <paramref name="layerName"/> is the
    /// document's <c>Layer.Name</c>; falls back to "untitled" when blank.
    /// </summary>
    public static LayerListItem CreateUnsaved(string? layerName, int width, int height, int fps = 60)
    {
        string baseName = string.IsNullOrWhiteSpace(layerName) ? "untitled" : layerName!.Trim();
        return new LayerListItem($"{baseName} (unsaved)", width, height, fps);
    }

    /// <summary>Absolute .phxlayer path, or empty string for an unsaved/virtual row.</summary>
    public string Path     { get; }
    public string FileName { get; }
    public int    Width    { get; }
    public int    Height   { get; }

    /// <summary>
    /// True when this row stands in for an in-memory document with no file on
    /// disk. The rail still shows it (so the active layer is always visible),
    /// but selecting it must NOT trigger a disk reload.
    /// </summary>
    public bool IsUnsaved { get; }

    private bool _active;
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }

    private int _fps;
    public int Fps
    {
        get => _fps;
        set
        {
            if (_fps == value) return;
            _fps = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fps)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
