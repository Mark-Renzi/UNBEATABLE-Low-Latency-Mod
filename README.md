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

## Finding your ASIO device

If you have more than one ASIO driver installed (e.g. an audio interface plus something like
ASIO4ALL), the mod defaults to whichever one FMOD picks first, which may not be the one you want.
To pick a specific one:

1. Launch the game once with the mod installed, then close it.
2. Open `BepInEx/LogOutput.log` in a text editor and find the probe results — search for
   `[ASIO probe] Driver`. You'll see a line per driver on your system, e.g.:
   ```
   [ASIO probe] Driver 0: "Ableton Move" (48000 Hz, STEREO/2ch)
   [ASIO probe] Driver 1: "MOTU M Series" (48000 Hz, STEREO/2ch)
   [ASIO probe] Driver 2: "Realtek ASIO" (48000 Hz, STEREO/2ch)
   ```
3. Note the name of the device you actually want to use.
4. Open `BepInEx/config/io.github.mark-renzi.lowlatencymod.cfg` and set `Device` under `[ASIO]` to
   part of that name (case-insensitive, just needs to be a unique substring), e.g.:
   ```
   Device = MOTU
   ```
5. Launch the game again and confirm it took effect — the success line in the log should now name
   your chosen device:
   ```
   >>> SUCCESS: FMOD OUTPUT IS ASIO (driver 1 = "MOTU M Series" (48000 Hz)) <<<
   ```

If `Device` doesn't match any enumerated driver, the log will say so and fall back to FMOD's default
choice (usually driver 0) rather than failing outright.

## Configuration

Settings live in `BepInEx/config/io.github.mark-renzi.lowlatencymod.cfg` (created after first launch).

| Section    | Key         | Default | Notes                                                                 |
| ---------- | ----------- | ------- | ---------------------------------------------------------------------|
| `[ASIO]`   | `UseASIO`   | `true`  | Master switch for trying ASIO at all.                                |
| `[ASIO]`   | `Device`    | `""`    | Substring match against ASIO driver names (see log for exact names). |
| `[ASIO]`   | `BufferSize`| `16`    | DSP buffer length in samples. Lower = less latency, more risk of crackle. |
| `[ASIO]`   | `BufferCount`| `4`    | Number of DSP buffers. If you get crunch/underruns, raise this before raising `BufferSize` — it costs less added latency per step. |
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
