using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace TestSus;

[BepInAutoPlugin("gg.reactor.api")]
[BepInProcess("Among Us.exe")]
public partial class ReactorPlugin : BasePlugin
{
    public Harmony Harmony { get; } = new(Id);
    
    public override void Load()
    {
        Harmony.PatchAll();
    }
}
