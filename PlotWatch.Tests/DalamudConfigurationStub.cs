// The pure configuration model only depends on this persistence marker interface.
// Keep this stub scoped to the standalone policy tests; production builds use Dalamud's interface.
namespace Dalamud.Configuration;

public interface IPluginConfiguration
{
    int Version { get; set; }
}
