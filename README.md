# UNBEATABLE Low Latency Mod

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for [UNBEATABLE](https://store.steampowered.com/) that
reduces FMOD audio latency.

- Forces FMOD to output through **ASIO** instead of WASAPI when a working ASIO driver is available
  (automatically falls back to WASAPI if it isn't, so audio never breaks).
- Shrinks FMOD's DSP buffer size/count for a much shorter output pipeline.
- Adds a brick-wall **limiter** on FMOD's master bus, since ASIO exclusive mode bypasses Windows'
  audio engine (which normally hides clipping in songs that already peak above 0 dBFS).

## Requirements

- **BepInEx 5.4.x (x64)** installed for UNBEATABLE. If you don't have it yet, install it manually from
  the [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases) (grab the `BepInEx_x64` build
  matching the game's Unity/Mono runtime), extract it into your UNBEATABLE game folder (next to
  `UNBEATABLE.exe`), then launch the game once so it generates its `BepInEx/plugins`, `BepInEx/config`,
  etc. folders.
- Optional: an **ASIO driver** for your audio interface (or something like ASIO4ALL). Not required —
  the mod probes ASIO on startup and safely falls back to WASAPI if none is found or it fails.

## Installation

1. Make sure BepInEx is installed (see above) and you've launched the game at least once with it.
2. Download `UNBEATABLE-Low-Latency-Mod-vX.Y.Z-manual.zip` from the releases.
3. Extract it directly into your UNBEATABLE game folder (the one containing `UNBEATABLE.exe` and the
   `BepInEx` folder). It will merge straight into `BepInEx/plugins/LowLatencyMod/`.
4. Launch the game.

## Verifying it worked

Check `BepInEx/LogOutput.log` after launching. Look for:

```
FMOD platform values forced to <length> × <count> (ASIO).
...
>>> SUCCESS: FMOD OUTPUT IS ASIO (driver N = "Your Device" (48000 Hz)) <<<
>>> Master limiter installed: ceiling=-0.3 dB, release=50 ms <<<
```

If ASIO isn't available on your machine, you'll instead see the probe fail and the log will say it's
staying on the fallback/WASAPI settings — that's expected behavior, not an error.

## Configuration

Settings live in `BepInEx/config/io.github.mark-renzi.lowlatencymod.cfg` (created after first launch).

| Section    | Key         | Default | Notes                                                                 |
| ---------- | ----------- | ------- | ---------------------------------------------------------------------|
| `[ASIO]`   | `UseASIO`   | `true`  | Master switch for trying ASIO at all.                                |
| `[ASIO]`   | `Device`    | `""`    | Substring match against ASIO driver names (see log for exact names). |
| `[ASIO]`   | `BufferSize`| `128`   | DSP buffer length in samples. Lower = less latency, more risk of crackle. |
| `[ASIO]`   | `BufferCount`| `4`    | Number of DSP buffers. Raise if you get crunch/underruns.            |
| `[WASAPI]` | `BufferSize`| `64`    | Buffer length used when ASIO is off or fails its probe.              |
| `[WASAPI]` | `BufferCount`| `4`    | Buffer count used when ASIO is off or fails its probe.               |
| `[Limiter]`| `Enabled`   | `true`  | Master-bus limiter to prevent clipping. Recommended to leave on.     |
| `[Limiter]`| `CeilingDb` | `-0.3`  | Output ceiling in dB, range -12 to 0.                                |
| `[Limiter]`| `ReleaseMs` | `50`    | Limiter release time in ms, range 1-1000.                            |

If audio sounds crunchy on specific (usually loud) songs, that's almost always clipping, not latency —
try lowering `CeilingDb` a bit further (e.g. `-1.0`) before touching buffer sizes.

## Uninstalling

Delete the `BepInEx/plugins/LowLatencyMod/` folder.

## Building from source

Requires the .NET SDK. `LowLatencyMod.csproj` references game/BepInEx DLLs via absolute `HintPath`
entries — update those paths for your own UNBEATABLE install location, then:

```
dotnet build LowLatencyMod.csproj -c Release
```

Or run `./package.ps1` to build and produce the release zip in `dist/`.
