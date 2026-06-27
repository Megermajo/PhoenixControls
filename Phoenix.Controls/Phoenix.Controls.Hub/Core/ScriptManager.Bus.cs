using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: bus.* command registrations.
    // Lifts the 2 Bus IPC handlers (bus.send / bus.broadcast)
    // out of RegisterHubCommands. Both serialise the args into a
    // BusMessage with Source="Hub" and post via
    // Bus.Instance.BroadcastAsync; the only difference is that
    // bus.broadcast uses Target="*" and substitutes "{}" for an empty
    // Payload so subscribers always see well-formed JSON.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterBusCommands()
        {
            _engine.RegisterCommand("bus.send", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string target  = bound?.GetOrDefault<string>("Target", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string type    = bound?.GetOrDefault<string>("Type", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string payload = bound?.GetOrDefault<string>("Payload", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(type)) return null;
                await Bus.Instance.BroadcastAsync(new Shared.Models.BusMessage {
                    Type = type, Source = "Hub", Target = target, Payload = payload
                });
                return null;
            });
            _engine.RegisterCommand("bus.broadcast", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string type    = bound?.GetOrDefault<string>("Type", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string payload = bound?.GetOrDefault<string>("Payload", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(type)) return null;
                await Bus.Instance.BroadcastAsync(new Shared.Models.BusMessage {
                    Type = type, Source = "Hub", Target = "*", Payload = string.IsNullOrEmpty(payload) ? "{}" : payload
                });
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
