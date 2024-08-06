using BepInEx;
using BepInEx.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace TestSus;

[HarmonyPatch]
internal static class SplashSkipPatch
{
    [HarmonyPatch(typeof(GameCore.GameCoreManager), nameof(GameCore.GameCoreManager.Awake))]
    [HarmonyPrefix]
    private static void RemoveMinimumWait()
    {
        Debug.Log("yay");
    }
    [HarmonyPatch(typeof(GameCore.GameCoreManager), nameof(GameCore.GameCoreManager.Awake))]
    [HarmonyPostfix]
    private static void RemoveMinimumWait2()
    {
        Debug.Log("yay 2");
    }
}
