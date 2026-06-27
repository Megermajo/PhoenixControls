using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    // IExporterHandler, ExporterContext, SocketArg, SimpleEmitDescriptor,
    // SimpleEmitHandler, ExporterRegistry moved to ExporterRegistry.Framework.cs ().

    #region Built-in registrations

    // All Register* methods moved to ExporterRegistry.Handlers1.cs ().
    // All handler impl classes (BranchHandler … ProcessExitHandler) moved to
    // ExporterRegistry.Handlers2.cs ().
    public static partial class ExporterRegistrations { }

    #endregion
}
