using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using FMOD;
using FMODUnity;
using HarmonyLib;
using UnityEngine;

[BepInPlugin(
"io.github.mark-renzi.lowlatencymod",
"UNBEATABLE Low Latency Mod",
"1.0.0"
)]
public class LowLatencyMod : BaseUnityPlugin
{
private static int TargetBufferLength = 64;
private static int TargetBufferCount = 4;

private static ConfigEntry<bool> ConfigUseASIO;
private static ConfigEntry<string> ConfigASIODevice;
private static ConfigEntry<int> ConfigASIOBufferSize;
private static ConfigEntry<int> ConfigASIOBufferCount;
private static ConfigEntry<int> ConfigWASAPIBufferSize;
private static ConfigEntry<int> ConfigWASAPIBufferCount;

private static ConfigEntry<bool> ConfigLimiterEnabled;
private static ConfigEntry<float> ConfigLimiterCeilingDb;
private static ConfigEntry<float> ConfigLimiterReleaseMs;

private static bool asioForced;
private static bool limiterInstalled;
private static bool limiterInstallAttempted;

private float nextCheck;
private const float CheckInterval = 2.0f;

private void Awake()
{
    Logger.LogInfo("==============================================");
    Logger.LogInfo("=== UNBEATABLE LOW LATENCY MOD ================");
    Logger.LogInfo("=== Version 1.0.0 ==============================");
    Logger.LogInfo("==============================================");

    ConfigUseASIO = Config.Bind(
        "ASIO",
        "UseASIO",
        true,
        "Try to force FMOD to output through ASIO instead of WASAPI " +
        "for lower audio latency. A throwaway FMOD system probes ASIO " +
        "first; if the probe fails, the game automatically falls back " +
        "to its normal output driver at the WASAPI buffer size below."
    );

    ConfigASIODevice = Config.Bind(
        "ASIO",
        "Device",
        "",
        "Search string used to pick the ASIO driver by name " +
        "(case-insensitive substring match). Leave empty to use " +
        "FMOD's default (driver 0). After first launch, check the log " +
        "for lines like '[ASIO probe] Driver N: \"name\"' to see the " +
        "exact device names available on this machine, then paste " +
        "(part of) the one you want here."
    );

    ConfigASIOBufferSize = Config.Bind(
        "ASIO",
        "BufferSize",
        16,
        "DSP buffer length in samples used when ASIO is active and its " +
        "probe succeeds. Lower = less latency but more risk of " +
        "crackling/underruns. If audio is crunchy, try raising " +
        "BufferCount first (it costs less latency per step); only raise " +
        "this (e.g. 16 -> 32 -> 64 -> 128 -> ... 1024) if that's not enough."
    );

    ConfigASIOBufferCount = Config.Bind(
        "ASIO",
        "BufferCount",
        2,
        "Number of DSP buffers used when ASIO is active and its probe " +
        "succeeds. The game's own default is 4; I previously forced " +
        "this down to 2 to save latency, which can starve FMOD under " +
        "heavy songs and cause crunch/underruns even at a decent " +
        "buffer size. Raise this (e.g. 3 or 4) if you're still getting " +
        "crunchiness. Each extra buffer adds one BufferSize's worth of " +
        "latency."
    );

    ConfigWASAPIBufferSize = Config.Bind(
        "WASAPI",
        "BufferSize",
        64,
        "DSP buffer length in samples used when UseASIO is false, or " +
        "when the ASIO probe fails and the game falls back to its " +
        "normal (WASAPI) output driver."
    );

    ConfigWASAPIBufferCount = Config.Bind(
        "WASAPI",
        "BufferCount",
        4,
        "Number of DSP buffers used in the fallback (non-ASIO) case. " +
        "The game's own default is 4."
    );

    ConfigLimiterEnabled = Config.Bind(
        "Limiter",
        "Enabled",
        false,
        "Attach FMOD's built-in brick-wall limiter to the master " +
        "channel group. ASIO exclusive mode bypasses Windows' audio " +
        "engine, which normally soft-clips/attenuates peaks for you; " +
        "without it, songs whose mix already exceeds 0 dBFS will " +
        "crackle. This limiter restores a safety ceiling. Recommended " +
        "to enable when you can't remove crackle with buffer size + count"
    );

    ConfigLimiterCeilingDb = Config.Bind(
        "Limiter",
        "CeilingDb",
        -0.3f,
        "Master limiter output ceiling in dB, range -12 to 0. Lower " +
        "values leave more headroom (safer, very slightly quieter); " +
        "0 only stops sample values from exceeding full scale, it " +
        "won't catch inter-sample peaks."
    );

    ConfigLimiterReleaseMs = Config.Bind(
        "Limiter",
        "ReleaseMs",
        50.0f,
        "Master limiter release time in milliseconds, range 1-1000. " +
        "Shorter reacts faster to transients but can pump/distort on " +
        "sustained loud passages; longer is smoother but may hold " +
        "gain reduction slightly longer after a peak."
    );

    try
    {
        var harmony = new Harmony(
            "io.github.mark-renzi.lowlatencymod"
        );

        bool asioActive = false;

        if (ConfigUseASIO.Value)
        {
            asioActive = TryEnableAsio(
                harmony,
                ConfigASIOBufferSize.Value,
                ConfigASIOBufferCount.Value
            );
        }
        else
        {
            Logger.LogInfo(
                "UseASIO disabled via config; staying on WASAPI."
            );
        }

        TargetBufferLength = asioActive
            ? ConfigASIOBufferSize.Value
            : ConfigWASAPIBufferSize.Value;

        TargetBufferCount = asioActive
            ? ConfigASIOBufferCount.Value
            : ConfigWASAPIBufferCount.Value;

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
            $"{TargetBufferLength} × {TargetBufferCount} " +
            $"({(asioActive ? "ASIO" : "fallback/original output")})."
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

private bool TryEnableAsio(Harmony harmony, int bufferLength, int bufferCount)
{
    Logger.LogInfo(
        $"Probing ASIO availability ({bufferLength} × {bufferCount}) " +
        "on a throwaway FMOD core system..."
    );

    bool probeOk = ProbeAsio(bufferLength, bufferCount, out string report);

    foreach (string line in report.Split('\n'))
    {
        string trimmed = line.TrimEnd('\r');

        if (trimmed.Length > 0)
        {
            Logger.LogInfo($"  [ASIO probe] {trimmed}");
        }
    }

    if (!probeOk)
    {
        Logger.LogWarning(
            $"ASIO probe failed at {bufferLength} × {bufferCount}. " +
            "Leaving FMOD's output driver untouched so the game keeps " +
            "working normally."
        );

        return false;
    }

    Logger.LogInfo(
        "ASIO probe succeeded. Patching Platform.GetOutputType() " +
        "to force ASIO."
    );

    MethodInfo getOutputType = AccessTools.Method(
        typeof(Platform),
        "GetOutputType"
    );

    if (getOutputType == null)
    {
        Logger.LogError(
            "Could not find Platform.GetOutputType(); cannot force ASIO."
        );

        return false;
    }

    harmony.Patch(
        getOutputType,
        prefix: new HarmonyMethod(
            AccessTools.Method(
                typeof(LowLatencyMod),
                nameof(GetOutputTypePrefix)
            )
        )
    );

    Logger.LogInfo(
        "Patched Platform.GetOutputType() -> forces ASIO."
    );

    MethodInfo setOutput = AccessTools.Method(
        typeof(FMOD.System),
        nameof(FMOD.System.setOutput)
    );

    if (setOutput == null)
    {
        Logger.LogWarning(
            "Could not find FMOD.System.setOutput(); driver selection " +
            "and result logging for the real FMOD system will be " +
            "skipped, but ASIO output is still forced."
        );
    }
    else
    {
        harmony.Patch(
            setOutput,
            postfix: new HarmonyMethod(
                AccessTools.Method(
                    typeof(LowLatencyMod),
                    nameof(SetOutputPostfix)
                )
            )
        );

        Logger.LogInfo(
            "Patched FMOD.System.setOutput() -> selects configured " +
            "ASIO driver and logs the result."
        );
    }

    asioForced = true;
    return true;
}

private static bool GetOutputTypePrefix(ref OUTPUTTYPE __result)
{
    __result = OUTPUTTYPE.ASIO;
    return false;
}

private static void SetOutputPostfix(
    ref FMOD.System __instance,
    OUTPUTTYPE output,
    RESULT __result
)
{
    if (output != OUTPUTTYPE.ASIO)
        return;

    if (Instance != null)
    {
        Instance.Logger.LogInfo(
            $"FMOD.System.setOutput(ASIO) -> {__result}"
        );
    }

    if (__result != RESULT.OK)
        return;

    RESULT numResult = __instance.getNumDrivers(out int numDrivers);

    if (numResult != RESULT.OK || numDrivers <= 0)
    {
        if (Instance != null)
        {
            Instance.Logger.LogWarning(
                $"getNumDrivers -> {numResult}, count={numDrivers}. " +
                "No ASIO driver selection performed."
            );
        }

        return;
    }

    var sb = new StringBuilder();
    int driverIndex = ResolveDriverIndex(__instance, numDrivers, sb);

    if (Instance != null && sb.Length > 0)
    {
        foreach (string line in sb.ToString().Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');

            if (trimmed.Length > 0)
            {
                Instance.Logger.LogInfo($"  [ASIO driver] {trimmed}");
            }
        }
    }

    if (driverIndex < 0)
        return;

    RESULT setResult = __instance.setDriver(driverIndex);

    if (Instance != null)
    {
        Instance.Logger.LogInfo(
            $"FMOD.System.setDriver({driverIndex}) -> {setResult}"
        );
    }
}

private static int ResolveDriverIndex(
    FMOD.System system,
    int numDrivers,
    StringBuilder sb
)
{
    string nameFilter = ConfigASIODevice?.Value;

    for (int i = 0; i < numDrivers; i++)
    {
        RESULT r = system.getDriverInfo(
            i,
            out string name,
            256,
            out Guid guid,
            out int systemRate,
            out SPEAKERMODE speakerMode,
            out int speakerModeChannels
        );

        sb.AppendLine(
            r == RESULT.OK
                ? $"Driver {i}: \"{name}\" ({systemRate} Hz, " +
                  $"{speakerMode}/{speakerModeChannels}ch)"
                : $"Driver {i}: getDriverInfo failed ({r})"
        );
    }

    if (!string.IsNullOrEmpty(nameFilter))
    {
        for (int i = 0; i < numDrivers; i++)
        {
            RESULT r = system.getDriverInfo(
                i,
                out string name,
                256,
                out Guid guid,
                out int systemRate,
                out SPEAKERMODE speakerMode,
                out int speakerModeChannels
            );

            if (
                r == RESULT.OK &&
                name != null &&
                name.IndexOf(
                    nameFilter,
                    StringComparison.OrdinalIgnoreCase
                ) >= 0
            )
            {
                sb.AppendLine(
                    $"Selected driver {i} by name filter " +
                    $"\"{nameFilter}\"."
                );

                return i;
            }
        }

        sb.AppendLine(
            $"No ASIO driver matched device filter \"{nameFilter}\"."
        );
    }

    sb.AppendLine(
        "Leaving driver selection at FMOD's default (no explicit " +
        "setDriver call)."
    );

    return -1;
}

private static bool ProbeAsio(
    int bufferLength,
    int bufferCount,
    out string report
)
{
    var sb = new StringBuilder();
    FMOD.System testSystem = default;
    bool created = false;
    bool initialized = false;

    try
    {
        RESULT result = Factory.System_Create(out testSystem);
        sb.AppendLine($"System_Create: {result}");

        if (result != RESULT.OK)
        {
            report = sb.ToString();
            return false;
        }

        created = true;

        result = testSystem.setOutput(OUTPUTTYPE.ASIO);
        sb.AppendLine($"setOutput(ASIO): {result}");

        if (result != RESULT.OK)
        {
            report = sb.ToString();
            return false;
        }

        result = testSystem.getNumDrivers(out int numDrivers);
        sb.AppendLine($"getNumDrivers: {result}, count={numDrivers}");

        if (result != RESULT.OK || numDrivers <= 0)
        {
            report = sb.ToString();
            return false;
        }

        int driverIndex = ResolveDriverIndex(testSystem, numDrivers, sb);

        if (driverIndex >= 0)
        {
            result = testSystem.setDriver(driverIndex);
            sb.AppendLine($"setDriver({driverIndex}): {result}");

            if (result != RESULT.OK)
            {
                report = sb.ToString();
                return false;
            }
        }

        result = testSystem.setSoftwareFormat(0, SPEAKERMODE.STEREO, 0);
        sb.AppendLine($"setSoftwareFormat: {result}");

        result = testSystem.setDSPBufferSize(
            (uint)bufferLength,
            bufferCount
        );

        sb.AppendLine(
            $"setDSPBufferSize({bufferLength}x{bufferCount}): {result}"
        );

        result = testSystem.init(32, INITFLAGS.NORMAL, IntPtr.Zero);
        sb.AppendLine($"init: {result}");

        if (result != RESULT.OK)
        {
            report = sb.ToString();
            return false;
        }

        initialized = true;

        report = sb.ToString();
        return true;
    }
    catch (Exception ex)
    {
        sb.AppendLine($"Exception: {ex}");
        report = sb.ToString();
        return false;
    }
    finally
    {
        if (initialized)
        {
            testSystem.close();
        }

        if (created)
        {
            testSystem.release();
        }
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
        typeof(LowLatencyMod),
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

private static LowLatencyMod Instance;

private void Start()
{
    Instance = this;

    Logger.LogInfo(
        "LowLatencyMod.Start() — FMOD initialization should " +
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

        if (!limiterInstallAttempted)
        {
            limiterInstallAttempted = true;
            InstallMasterLimiter(system);
        }

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

        if (asioForced)
        {
            CheckOutputDriver(system);
        }

        Logger.LogInfo(
            $"Master limiter installed: {limiterInstalled}"
        );
    }
    catch (Exception ex)
    {
        Logger.LogError(
            $"FMOD verification failed: {ex}"
        );
    }
}

private void InstallMasterLimiter(FMOD.System system)
{
    if (!ConfigLimiterEnabled.Value)
    {
        Logger.LogInfo(
            "Master limiter disabled via config."
        );

        return;
    }

    try
    {
        RESULT result = system.getMasterChannelGroup(
            out ChannelGroup master
        );

        if (result != RESULT.OK)
        {
            Logger.LogWarning(
                $"getMasterChannelGroup failed: {result}. " +
                "Master limiter not installed."
            );

            return;
        }

        result = system.createDSPByType(DSP_TYPE.LIMITER, out DSP limiter);

        if (result != RESULT.OK)
        {
            Logger.LogWarning(
                $"createDSPByType(LIMITER) failed: {result}. " +
                "Master limiter not installed."
            );

            return;
        }

        float ceilingDb = ConfigLimiterCeilingDb.Value;
        float releaseMs = ConfigLimiterReleaseMs.Value;

        limiter.setParameterFloat(
            (int)DSP_LIMITER.CEILING,
            ceilingDb
        );

        limiter.setParameterFloat(
            (int)DSP_LIMITER.RELEASETIME,
            releaseMs
        );

        limiter.setParameterFloat(
            (int)DSP_LIMITER.MAXIMIZERGAIN,
            0.0f
        );

        result = master.addDSP(
            CHANNELCONTROL_DSP_INDEX.TAIL,
            limiter
        );

        if (result != RESULT.OK)
        {
            Logger.LogWarning(
                $"master.addDSP(limiter) failed: {result}. " +
                "Master limiter not installed."
            );

            return;
        }

        limiterInstalled = true;

        Logger.LogInfo(
            $">>> Master limiter installed: ceiling={ceilingDb} dB, " +
            $"release={releaseMs} ms <<<"
        );
    }
    catch (Exception ex)
    {
        Logger.LogError(
            $"Failed to install master limiter: {ex}"
        );
    }
}

private void CheckOutputDriver(FMOD.System system)
{
    RESULT outputResult = system.getOutput(out OUTPUTTYPE activeOutput);

    if (outputResult != RESULT.OK)
    {
        Logger.LogWarning(
            $"getOutput failed: {outputResult}"
        );

        return;
    }

    if (activeOutput == OUTPUTTYPE.ASIO)
    {
        RESULT driverResult = system.getDriver(out int activeDriver);

        string driverName = "<unknown>";

        if (driverResult == RESULT.OK)
        {
            RESULT infoResult = system.getDriverInfo(
                activeDriver,
                out string name,
                256,
                out Guid guid,
                out int systemRate,
                out SPEAKERMODE speakerMode,
                out int speakerModeChannels
            );

            if (infoResult == RESULT.OK)
            {
                driverName = $"\"{name}\" ({systemRate} Hz)";
            }
        }

        Logger.LogInfo(
            $">>> SUCCESS: FMOD OUTPUT IS ASIO (driver " +
            $"{(driverResult == RESULT.OK ? activeDriver.ToString() : "?")} " +
            $"= {driverName}) <<<"
        );
    }
    else
    {
        Logger.LogWarning(
            $"ASIO was requested but FMOD's active output is " +
            $"{activeOutput}."
        );
    }
}

}
