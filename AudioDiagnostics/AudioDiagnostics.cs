using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using FMOD;
using FMODUnity;
using HarmonyLib;
using UnityEngine;

[BepInPlugin(
"com.cheez.unbeatable.audiotest",
"UNBEATABLE Audio Diagnostics",
"3.6.0"
)]
public class AudioDiagnostics : BaseUnityPlugin
{
private const int TargetBufferLength = 64;
private const int TargetBufferCount = 2;

private float nextCheck;
private const float CheckInterval = 2.0f;

private void Awake()
{
    Logger.LogInfo("==============================================");
    Logger.LogInfo("=== UNBEATABLE FMOD PLATFORM PATCH ===========");
    Logger.LogInfo("=== Version 3.6.0 =============================");
    Logger.LogInfo("==============================================");

    try
    {
        var harmony = new Harmony(
            "com.cheez.unbeatable.audiotest"
        );

        PatchProperty(
            harmony,
            "DSPBufferLength",
            TargetBufferLength
        );

        PatchProperty(
            harmony,
            "DSPBufferCount",
            TargetBufferCount
        );

        Logger.LogInfo(
            $"FMOD platform values forced to " +
            $"{TargetBufferLength} × {TargetBufferCount}."
        );

        Logger.LogInfo(
            "Starting periodic FMOD verification..."
        );

        nextCheck = Time.unscaledTime + 1f;
    }
    catch (Exception ex)
    {
        Logger.LogError(
            $"Patch installation failed: {ex}"
        );
    }
}

private void PatchProperty(
    Harmony harmony,
    string propertyName,
    int value
)
{
    PropertyInfo property = AccessTools.Property(
        typeof(Platform),
        propertyName
    );

    if (property == null)
    {
        Logger.LogError(
            $"Could not find Platform.{propertyName}."
        );
        return;
    }

    MethodInfo getter = property.GetGetMethod();

    if (getter == null)
    {
        Logger.LogError(
            $"Could not find getter for Platform.{propertyName}."
        );
        return;
    }

    Logger.LogInfo(
        $"Found Platform.{propertyName}: {getter}"
    );

    MethodInfo prefix = AccessTools.Method(
        typeof(AudioDiagnostics),
        propertyName == "DSPBufferLength"
            ? nameof(DSPBufferLengthPrefix)
            : nameof(DSPBufferCountPrefix)
    );

    harmony.Patch(
        getter,
        prefix: new HarmonyMethod(prefix)
    );

    Logger.LogInfo(
        $"Patched Platform.{propertyName} getter."
    );
}

private static bool DSPBufferLengthPrefix(
    ref int __result
)
{
    __result = TargetBufferLength;

    if (Instance != null)
    {
        Instance.Logger.LogInfo(
            $"Platform.DSPBufferLength() -> {TargetBufferLength}"
        );
    }

    return false;
}

private static bool DSPBufferCountPrefix(
    ref int __result
)
{
    __result = TargetBufferCount;

    if (Instance != null)
    {
        Instance.Logger.LogInfo(
            $"Platform.DSPBufferCount() -> {TargetBufferCount}"
        );
    }

    return false;
}

private static AudioDiagnostics Instance;

private void Start()
{
    Instance = this;

    Logger.LogInfo(
        "AudioDiagnostics.Start() — FMOD initialization should " +
        "already have occurred or be occurring."
    );
}

private void Update()
{
    if (Time.unscaledTime < nextCheck)
        return;

    nextCheck = Time.unscaledTime + CheckInterval;

    CheckFMOD();
}

private void CheckFMOD()
{
    try
    {
        if (!RuntimeManager.IsInitialized)
        {
            Logger.LogInfo(
                "FMOD not initialized yet."
            );

            return;
        }

        FMOD.System system = RuntimeManager.CoreSystem;

        RESULT result = system.getDSPBufferSize(
            out uint bufferLength,
            out int bufferCount
        );

        Logger.LogInfo(
            $"FMOD DSP check: result={result}, " +
            $"buffer={bufferLength} × {bufferCount}"
        );

        if (result != RESULT.OK)
        {
            Logger.LogWarning(
                $"getDSPBufferSize failed: {result}"
            );

            return;
        }

        Logger.LogInfo(
            $"Requested: {TargetBufferLength} × {TargetBufferCount}"
        );

        if (
            bufferLength == TargetBufferLength &&
            bufferCount == TargetBufferCount
        )
        {
            Logger.LogInfo(
                ">>> SUCCESS: FMOD IS USING 64 × 2 <<<"
            );
        }
        else
        {
            Logger.LogWarning(
                $"FMOD is using {bufferLength} × {bufferCount}, " +
                $"NOT {TargetBufferLength} × {TargetBufferCount}."
            );
        }
    }
    catch (Exception ex)
    {
        Logger.LogError(
            $"FMOD verification failed: {ex}"
        );
    }
}

}
