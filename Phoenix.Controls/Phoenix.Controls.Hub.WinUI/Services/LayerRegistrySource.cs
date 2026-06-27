using System;
using System.Collections.Generic;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// Thin pass-through from the Hub-side LayerRegistry singleton to the
/// IHubServices contract. Exists so Visualist (and any other WinUI host
/// consuming IHubServices) can read live layer presence without taking
/// a project dependency on Phoenix.Controls.Hub.
/// </summary>
internal sealed class LayerRegistrySource : ILayerRegistrySource
{
    private readonly LayerRegistry _registry;
    public LayerRegistrySource(LayerRegistry registry) { _registry = registry; }
    public bool IsLayerActive(string layerId) => _registry.IsLayerActive(layerId);
    public IReadOnlyList<string> GetActiveLayerIds() => _registry.GetActiveLayerIds();
    public event Action<string, bool>? LiveLayerChanged
    {
        add    => _registry.LiveLayerChanged += value;
        remove => _registry.LiveLayerChanged -= value;
    }
}
