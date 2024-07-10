using System;
using BepInEx.Logging;
using MonoMod.Core;

namespace BepInEx.Unity.IL2CPP.Hook;

internal abstract class BaseNativeDetour<T> : ICoreNativeDetour where T : BaseNativeDetour<T>
{
    protected static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(typeof(T).Name);

    public IntPtr Source { get; }
    public IntPtr Target { get; }
    public bool HasOrigEntrypoint { get; protected set; }
    public IntPtr OrigEntrypoint { get; protected set; }
    public bool IsApplied { get; private set; }
    
    private bool IsPrepared { get; set; }

    
    protected BaseNativeDetour(nint originalMethodPtr, nint detourMethod)
    {
        Source = originalMethodPtr;
        Target = detourMethod;
    }
    
    public void Dispose()
    {
        if (!IsApplied) return;
        Undo();
        Free();
    }

    public void Apply()
    {
        if (IsApplied) return;

        Prepare();
        ApplyImpl();

        Logger.Log(LogLevel.Debug,
                   $"Original: {Source:X}, Trampoline: {OrigEntrypoint:X}, diff: {Math.Abs((nint)Source - OrigEntrypoint):X}");

        IsApplied = true;
    }

    public void Undo()
    {
        if (IsApplied && IsPrepared) UndoImpl();
    }

    public void Free()
    {
        FreeImpl();
    }

    protected abstract void ApplyImpl();

    private void Prepare()
    {
        if (IsPrepared) return;
        Logger.LogDebug($"Preparing detour from 0x{Source:X2} to 0x{Target:X2}");
        PrepareImpl();
        Logger.LogDebug($"Prepared detour; Trampoline: 0x{OrigEntrypoint:X2}");
        IsPrepared = true;
    }

    protected abstract void PrepareImpl();

    protected abstract void UndoImpl();

    protected abstract void FreeImpl();
}
