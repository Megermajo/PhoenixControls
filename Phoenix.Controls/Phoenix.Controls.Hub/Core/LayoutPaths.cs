using System;
using System.IO;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Hub.Core
{
    // Resolves cross-session storage locations for the Hub. The dock layout
    // lives under %AppData%/PhoenixControls/Hub/ so it survives reinstalls
    // that wipe the application directory. Falls back to BaseDirectory when
    // %AppData% is unavailable (sandboxed environments / CI).
    public static class LayoutPaths
    {
        public static string Hub()
        {
            try
            {
                string dir = Paths.RoamingAppData("Hub");
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "dock-layout.xml");
                }
            }
            catch { /* Fall through to BaseDirectory. */ }
            return Path.Combine(Paths.AppBase, "dock-layout.xml");
        }
    }
}
