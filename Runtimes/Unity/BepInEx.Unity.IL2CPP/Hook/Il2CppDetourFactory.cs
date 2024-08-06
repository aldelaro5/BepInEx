using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Hook.Dobby;
using BepInEx.Unity.IL2CPP.Hook.Funchook;
using Il2CppInterop.Common;
using Il2CppInterop.HarmonySupport;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using MonoMod.Cil;
using MonoMod.Core;
using MonoMod.Core.Platforms;
using MonoMod.Utils;

namespace BepInEx.Unity.IL2CPP.Hook;

internal class Il2CppDetourFactory : IDetourFactory
{
    private enum DetourProvider
    {
        Default,
        Dobby,
        Funchook
    }
    
    private static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource("Il2CppDetourFactory");
    
    private static readonly ConfigEntry<DetourProvider> DetourProviderType = ConfigFile.CoreConfig.Bind(
         "Detours", "DetourProviderType",
         DetourProvider.Default,
         "The native provider to use for managed detours"
        );

    private DynamicMethodDefinition CopyOriginal(MethodBase src, INativeMethodInfoStruct modifiedNativeMethodInfo)
    {
        var dmd = new DynamicMethodDefinition(src);
        dmd.Definition.Name = "UnhollowedWrapper_" + dmd.Definition.Name;
        var cursor = new ILCursor(new ILContext(dmd.Definition));


        // Remove il2cpp_object_get_virtual_method
        if (cursor.TryGotoNext(x => x.MatchLdarg(0),
                               x => x.MatchCall(typeof(Il2CppInterop.Runtime.IL2CPP),
                                                nameof(Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtr)),
                               x => x.MatchLdsfld(out _),
                               x => x.MatchCall(typeof(Il2CppInterop.Runtime.IL2CPP),
                                                nameof(Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_virtual_method))))
        {
            cursor.RemoveRange(4);
        }
        else
        {
            cursor.Goto(0)
                  .GotoNext(x =>
                                x.MatchLdsfld(Il2CppInteropUtils
                                                  .GetIl2CppMethodInfoPointerFieldForGeneratedMethod(src)))
                  .Remove();
        }

        // Replace original IL2CPPMethodInfo pointer with a modified one that points to the trampoline
        cursor
            .Emit(Mono.Cecil.Cil.OpCodes.Ldc_I8, modifiedNativeMethodInfo.Pointer.ToInt64())
            .Emit(Mono.Cecil.Cil.OpCodes.Conv_I);

        return dmd;
    }

    unsafe ICoreDetour IDetourFactory.CreateDetour(CreateDetourRequest request)
    {
        Logger.LogDebug("source:");
        Logger.LogDebug(((MethodInfo)request.Source).Name);
        Logger.LogDebug(string.Join(", ", ((MethodInfo)request.Source).GetParameters().Select(x => x.ParameterType.ToString())));
        Logger.LogDebug(((MethodInfo)request.Source).ReturnType);
        Logger.LogDebug("target:");
        Logger.LogDebug(((MethodInfo)request.Target).Name);
        Logger.LogDebug(string.Join(", ", ((MethodInfo)request.Target).GetParameters().Select(x => x.ParameterType.ToString())));
        Logger.LogDebug(((MethodInfo)request.Target).ReturnType);
        Logger.LogDebug("clone create?");
        Logger.LogDebug(request.CreateSourceCloneIfNotILClone);
        var declaringType = request.Source.DeclaringType;
        if (declaringType == null ||
            Il2CppType.From(declaringType, false) == null ||
            !ClassInjector.IsTypeRegisteredInIl2Cpp(declaringType))
        {
            return DetourFactory.Current.CreateDetour(request);
        }
        
        var methodField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(request.Source);

        // Get the native MethodInfo struct for the target method
        var originalNativeMethodInfo =
            UnityVersionHandler.Wrap((Il2CppMethodInfo*)(IntPtr)methodField.GetValue(null));
        // Create a modified native MethodInfo struct, that will point towards the trampoline
        var modifiedNativeMethodInfo = UnityVersionHandler.NewMethod();
        Buffer.MemoryCopy(originalNativeMethodInfo.Pointer.ToPointer(),
                          modifiedNativeMethodInfo.Pointer.ToPointer(), UnityVersionHandler.MethodSize(),
                          UnityVersionHandler.MethodSize());
        
        Logger.LogDebug("source:");
        Logger.LogDebug(new ILContext(new DynamicMethodDefinition(request.Source).Definition).Body.ToILDasmString());

        // Generate a new DMD of the modified unhollowed method, and apply harmony patches to it
        // var copiedDmd = CopyOriginal(request.Source);

        var copiedDmd = CopyOriginal(request.Source, modifiedNativeMethodInfo);
        Logger.LogDebug("copiedDmd:");
        Logger.LogDebug(new ILContext(copiedDmd.Definition).Body.ToILDasmString());
        // Generate the MethodInfo instances
        var managedHookedMethod = copiedDmd.Generate();
        
        var unmanagedTrampolineMethodDmd = Il2CppDetourMethodPatcher.GenerateNativeToManagedTrampolineStatic(request.Source, (MethodInfo)request.Target);
        Logger.LogDebug("unmanagedTrampolineMethod:");
        Logger.LogDebug(new ILContext(unmanagedTrampolineMethodDmd.Definition).Body.ToILDasmString());
        
        var unmanagedTrampolineMethod = unmanagedTrampolineMethodDmd.Generate();
        var ptrTarget = PlatformTriple.Current.Runtime.GetMethodHandle(unmanagedTrampolineMethod).GetFunctionPointer();
        var nativeDetour = this.CreateNativeDetour
            (
             originalNativeMethodInfo.MethodPointer,
             ptrTarget,
             applyByDefault: false
             );
        var managedDetour = DetourFactory.Current.CreateDetour(request.Source, request.Target, applyByDefault: false);
        
        NativeWrappedDetour wrapper = new NativeWrappedDetour(request.Source, request.Target,
                                                              nativeDetour, modifiedNativeMethodInfo, 
                                                              originalNativeMethodInfo, managedDetour,
                                                              managedHookedMethod, copiedDmd);
        
        if (request.ApplyByDefault)
            wrapper.Apply();

        return wrapper;
    }

    ICoreNativeDetour IDetourFactory.CreateNativeDetour(CreateNativeDetourRequest request)
    {
        var detour = DetourProviderType.Value switch
        {
            DetourProvider.Dobby    => new DobbyDetour(request.Source, request.Target),
            DetourProvider.Funchook => new FunchookDetour(request.Source, request.Target),
            _                       => CreateDefault(request.Source, request.Target)
        };

        return detour;
    }

    private static ICoreNativeDetour CreateDefault(nint original, nint target) =>
        // TODO: check and provide an OS accurate provider
        new DobbyDetour(original, target);
}
