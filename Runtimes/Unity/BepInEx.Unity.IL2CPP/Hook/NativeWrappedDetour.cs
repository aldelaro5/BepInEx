using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using MonoMod.Core;
using MonoMod.Utils;

namespace BepInEx.Unity.IL2CPP.Hook;

internal class NativeWrappedDetour : ICoreDetourWithClone
{
    private static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource("NativeWrappedDetour");
    
    public bool IsApplied => nativeDetour.IsApplied;
    public MethodBase Source { get; }
    public MethodBase Target { get; }
    private readonly ICoreNativeDetour nativeDetour;
    private readonly ICoreDetour managedDetour;
    private readonly INativeMethodInfoStruct modifiedNativeMethodInfo;
    private readonly INativeMethodInfoStruct originalNativeMethodInfo;
    private readonly MethodInfo clonedOriginal;
    private readonly DynamicMethodDefinition clonedDmd;
    public void Dispose()
    {
        Logger.LogDebug("Dispose...");
        managedDetour.Dispose();
        nativeDetour.Dispose();
        modifiedNativeMethodInfo.MethodPointer = originalNativeMethodInfo.MethodPointer;
        Logger.LogDebug("Disposed");
    }

    public void Apply()
    {
        Logger.LogDebug("Apply...");
        Logger.LogDebug($"managed: from {managedDetour.Source.FullDescription()} to {managedDetour.Target.FullDescription()}");
        managedDetour.Apply();
        Logger.LogDebug($"Pointer before: {modifiedNativeMethodInfo.Pointer}");
        Logger.LogDebug($"MethodPointer before: {modifiedNativeMethodInfo.MethodPointer}");
        nativeDetour.Apply();
        modifiedNativeMethodInfo.MethodPointer = nativeDetour.OrigEntrypoint;
        Logger.LogDebug($"Pointer after: {modifiedNativeMethodInfo.Pointer}");
        Logger.LogDebug($"MethodPointer after: {modifiedNativeMethodInfo.MethodPointer}");
        Logger.LogDebug("Applied");
    }

    public void Undo()
    {
        Logger.LogDebug("Undo...");
        managedDetour.Undo();
        nativeDetour.Undo();
        modifiedNativeMethodInfo.MethodPointer = originalNativeMethodInfo.MethodPointer;
        Logger.LogDebug("Undid");
    }

    public NativeWrappedDetour(MethodBase source, MethodBase target, ICoreNativeDetour nativeDetour, INativeMethodInfoStruct modifiedNativeMethodInfo, INativeMethodInfoStruct originalNativeMethodInfo, ICoreDetour managedDetour, MethodInfo clonedOriginal, DynamicMethodDefinition clonedDmd)
    {
        Source = source;
        Target = target;
        this.nativeDetour = nativeDetour;
        this.modifiedNativeMethodInfo = modifiedNativeMethodInfo;
        this.originalNativeMethodInfo = originalNativeMethodInfo;
        this.managedDetour = managedDetour;
        this.clonedOriginal = clonedOriginal;
        this.clonedDmd = clonedDmd;
    }

    public MethodInfo SourceMethodClone => clonedOriginal;
    public DynamicMethodDefinition SourceMethodCloneIL => clonedDmd;
}
