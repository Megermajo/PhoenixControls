using System;
using System.Threading.Tasks;

namespace Phoenix.Controls.Architect.WinUI.ViewModels;

// Architect.exe CLI entry point — Track 5's App.xaml.cs forwards argv
// here from OnLaunched(). Honours --open <path> per the Track 6 brief
// so Hub's right-click "Open in Architect" context menu can deep-link
// into a specific .phxg file.
//
// Usage from Track 5:
//
//     protected override async void OnLaunched(LaunchActivatedEventArgs args)
//     {
//         var window = new MainWindow();
//         window.Activate();
//         await CliBootstrap.HandleCliAsync(Environment.GetCommandLineArgs(), window.ViewModel);
//     }
//
// The ViewModel parameter is the live ArchitectViewModel the window
// uses; HandleCliAsync pushes the requested .phxg into LogicCanvas via OpenAsync().
//
// Args handled:
//   --open <path>   load <path> as the initial graph
//   <path>          (positional) load <path> as the initial graph
//   --new           start with an empty graph (no-op — that's the default)
//   --help          ignored at runtime; surfaced only when packaging/installer wants it
public static class CliBootstrap
{
    /// <summary>
    /// Parse argv and apply the requested startup behaviour to <paramref name="vm"/>.
    /// Returns true when a graph was opened, false otherwise.
    ///  Was sync HandleCli that called the deadlock-prone Open shim;
    /// now awaits ArchitectViewModel.OpenAsync directly so the deserialize +
    /// wildcard cascade runs on the thread pool and resumes on the captured
    /// context (the same context the WinUI Loaded handler ran us on).
    /// </summary>
    public static async Task<bool> HandleCliAsync(string[]? argv, ArchitectViewModel vm)
    {
        if (vm is null) throw new ArgumentNullException(nameof(vm));
        if (argv is null || argv.Length == 0) return false;

        // Skip argv[0] (the exe path) when present. We can't reliably detect
        // "is this the exe?" portably, but Environment.GetCommandLineArgs()
        // always front-loads it, so just probe whether the first entry
        // matches a real .exe; if it does, drop it.
        int start = 0;
        if (argv.Length > 0 &&
            (argv[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
             argv[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            start = 1;
        }

        for (int i = start; i < argv.Length; i++)
        {
            string a = argv[i];
            if (string.IsNullOrEmpty(a)) continue;

            if (a == "--open")
            {
                if (i + 1 >= argv.Length) return false;
                return await vm.OpenAsync(argv[++i]).ConfigureAwait(true);
            }
            if (a == "--new" || a == "--help") continue;
            if (a.StartsWith("--")) continue;   // unknown flag — ignore so future flags don't crash old builds

            // Positional path — first non-flag wins.
            return await vm.OpenAsync(a).ConfigureAwait(true);
        }
        return false;
    }
}
