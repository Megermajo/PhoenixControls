using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI;

// Hub-local contract — deliberately not in Shared.WinUI because panel
// composition is a Hub-internal concern. MainWindowViewModel uses this
// to hand panels into MainWindow's named regions.
public interface IPanelFactory
{
    UserControl CreateLiveFeedPanel(IHubServices services);
    UserControl CreateChatPanel(IHubServices services);
    UserControl CreateScriptPanel(IHubServices services);
    UserControl CreateSystemLogPanel(IHubServices services);
    UserControl CreateStatusStrip(IHubServices services);
}
