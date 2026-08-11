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
"1.0.1"
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
        Logger.LogInfo("UNBEATABLE LOW LATENCY MOD");
        Logger.LogInfo("Version 1.0.1");

        ConfigUseASIO = Config.Bind(
            "ASIO",
            "UseASIO",
            true,
            "Try to force FMOD to output through ASIO instead of WASAPI to reduce " +
            "latency... If the created FMOD system can't use it, it will fallback " +
            "to WASAPI (what the game normally uses)."
        );

        ConfigASIODevice = Config.Bind(
            "ASIO",
            "Device",
            "",
            "Case-insensitive substring search string used to pick the ASIO driver by name. " +
            "Leave empty to use the first driver (0). After first launch, check the log " +
            "for '[ASIO probe] Driver N: \"name\"' to see the " +
            "exact device names available on this machine, then paste " +
            "part of the one you want here."
        );

        ConfigASIOBufferSize = Config.Bind(
            "ASIO",
            "BufferSize",
            16,
            "DSP buffer length in samples used when ASIO is active. " +
            "Lower = less latency but more risk of " +
            "crackling/underruns. If audio is crunchy, try raising " +
            "BufferCount to 4 first because it adds less latency. If that " +
            "fails, raise this 16 -> 32 -> 64 -> 128 -> 512 -> 1024. " +
            "The game's own default is 1024."
        );

        ConfigASIOBufferCount = Config.Bind(
            "ASIO",
            "BufferCount",
            2,
            "Number of DSP buffers used when ASIO is active. The game's own default is 4. " +
            "Each extra buffer adds one BufferSize's worth of latency."
        );

        ConfigWASAPIBufferSize = Config.Bind(
            "WASAPI",
            "BufferSize",
            64,
            "DSP buffer length in samples used when UseASIO is false, or " +
            "when ASIO fails and the game falls back to WASAPI. The game's own default is 1024."
        );

        ConfigWASAPIBufferCount = Config.Bind(
            "WASAPI",
            "BufferCount",
            4,
            "Number of DSP buffers used when UseASIO is false, or " +
            "when ASIO fails and the game falls back to WASAPI. " +
            "The game's own default is 4."
        );

        ConfigLimiterEnabled = Config.Bind(
            "Limiter",
            "Enabled",
            false,
            "Attach FMOD's built-in limiter to the master channel group. " +
            "ASIO bypasses Windows' audio which can usually clip these peaks. " +
            "Recommended as a last resort when you can't remove crackle with buffer size + count."
        );

        ConfigLimiterCeilingDb = Config.Bind(
            "Limiter",
            "CeilingDb",
            -0.3f,
            "Master limiter output ceiling in dB, [-12, 0]. closer to 0 " +
            "doesn't reduce volume as far but might let audio peak."
        );

        ConfigLimiterReleaseMs = Config.Bind(
            "Limiter",
            "ReleaseMs",
            50.0f,
            "Master limiter release time in milliseconds [1,1000]. " +
            "Shorter reacts faster to transients but can distort."
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
                    "UseASIO disabled via config: using WASAPI."
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
                $"{TargetBufferLength} x {TargetBufferCount} " +
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
            $"Probing ASIO availability ({bufferLength} x {bufferCount})"
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
                $"ASIO probe failed at {bufferLength} x {bufferCount}. " +
                "Falling back to WASAPI."
            );

            return false;
        }

        Logger.LogInfo(
            "ASIO probe succeeded. Patching Platform.GetOutputType()..."
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
            "Patched Platform.GetOutputType(); Using ASIO."
        );

        MethodInfo setOutput = AccessTools.Method(
            typeof(FMOD.System),
            nameof(FMOD.System.setOutput)
        );

        if (setOutput == null)
        {
            Logger.LogWarning(
                "Could not find FMOD.System.setOutput(); logging won't work, but ASIO output should use whatever driver is in the config..."
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
                "Patched FMOD.System.setOutput(); Should use ASIO driver in the config and log the result."
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
                $"FMOD.System.setOutput(ASIO): {__result}"
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
                    $"getNumDrivers: {numResult}, count={numDrivers}. No ASIO driver set in the config."
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
            "Leaving driver selection at driver 0... you should probably set one in the config."
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
                $"Platform.DSPBufferLength(): {TargetBufferLength}"
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
                $"Platform.DSPBufferCount(): {TargetBufferCount}"
            );
        }

        return false;
    }

    private static LowLatencyMod Instance;

    private void Start()
    {
        Instance = this;

        Logger.LogInfo(
            "LowLatencyMod.Start()"
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
                $"buffer={bufferLength} x {bufferCount}"
            );

            if (result != RESULT.OK)
            {
                Logger.LogWarning(
                    $"getDSPBufferSize failed: {result}"
                );

                return;
            }

            Logger.LogInfo(
                $"Requested: {TargetBufferLength} x {TargetBufferCount}"
            );

            if (
                bufferLength == TargetBufferLength &&
                bufferCount == TargetBufferCount
            )
            {
                Logger.LogInfo(
                    ">>> SUCCESS: FMOD IS USING 64 x 2 <<<"
                );
            }
            else
            {
                Logger.LogWarning(
                    $"FMOD is using {bufferLength} x {bufferCount}, " +
                    $"NOT {TargetBufferLength} x {TargetBufferCount}."
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
                $">>> Master limiter installed: ceiling={ceilingDb} dB, release={releaseMs} ms <<<"
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
                $">>> SUCCESS: FMOD OUTPUT IS ASIO (driver {(driverResult == RESULT.OK ? activeDriver.ToString() : "?")} = {driverName}) <<<"
            );
        }
        else
        {
            Logger.LogWarning(
                $"ASIO was requested but FMOD's active output is {activeOutput}."
            );
        }
    }
}
