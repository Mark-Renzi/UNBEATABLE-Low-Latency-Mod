# UNBEATABLE Low Latency Mod

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for [UNBEATABLE](https://store.steampowered.com/) that
reduces FMOD audio latency.


Are you sick of the audio feedback in UNBEATABLE being super delayed? Do you miss sound effects because you turned them off to make the game playable?
I (MIGHT) have a solution for you!

What does it do?
- It (tries to) force FMOD to output through **ASIO** instead of WASAPI when a working ASIO driver is available (falling back to WASAPI if it fails).
- Regardless of whether or not ASIO works, it can lower the DSP buffer size and count (much more with ASIO).

Here's an example video of what you can expect! I recorded this with mic sound hearing my keyboard and speakers so fair warning the recording is a little nasty.

[![IMAGE ALT TEXT HERE](https://img.youtube.com/vi/lR-WPMsbCRo/0.jpg)](https://www.youtube.com/watch?v=lR-WPMsbCRo)

## Requirements

- **BepInEx 5.4.x (x64)** installed for UNBEATABLE. If you don't have it yet, install it manually from the [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases) (BepInEx_win_x64_5.4.x.zip), extract it into your UNBEATABLE game folder (so there's a `BepInEx/` folder and `winhttp.dll` next to `UNBEATABLE.exe`), then launch the game once so it generates its `BepInEx/plugins`, `BepInEx/config`, etc. folders.
- An **ASIO driver** for your audio interface. The mod can help a little without it... but it can pretty much entirely remove latency with ASIO. If you're not sure if your audio interface has ASIO, it likely doesn't, but here's a list I found referencing a mod for another game called rocksmith [expand the list, the rest of the instructions are for a different game so ignore](https://github.com/mdias/rs_asio#audio-interfaces-reported-to-work-well) and ignore all of the ones that need ASIO4ALL.

## Audio Interfaces reported to work well for THIS mod

<details>
<summary>Click to expand</summary>

- MOTU M2
- You can add to this list! Open an issue to let me know please!

</details>


## Installation

1. Make sure BepInEx is installed (see [requirements](#requirements) above).
2. Download `UNBEATABLE-Low-Latency-Mod-vX.Y.Z-manual.zip` from the releases.
3. Extract it directly into your UNBEATABLE game folder (the one containing `UNBEATABLE.exe` and the `BepInEx` folder). It will merge straight into `BepInEx/plugins/LowLatencyMod/`.
4. Launch the game.

## Verifying it worked

Check `BepInEx/LogOutput.log` after launching. Look for:

```
FMOD platform values forced to <length> x <count> (ASIO).
...
>>> SUCCESS: FMOD OUTPUT IS ASIO (driver N = "Your Device" (48000 Hz)) <<<
```

If ASIO isn't available on your machine, you'll instead see the probe fail and the log will say it's staying on WASAPI... boo lame.

## Finding your ASIO device

If you have more than one ASIO driver installed, the mod defaults to whichever one FMOD picks first, which may not be the one you want.
To pick a specific one:

1. Launch the game once with the mod installed, then close it.
2. Open `BepInEx/LogOutput.log` in a text editor and find the probe results. Search for
   `[ASIO probe] Driver`. You'll see a line per driver on your system, e.g.:
   ```
   [ASIO probe] Driver 0: "Ableton Move" (48000 Hz, STEREO/2ch)
   [ASIO probe] Driver 1: "MOTU M Series" (48000 Hz, STEREO/2ch)
   [ASIO probe] Driver 2: "Realtek ASIO" (48000 Hz, STEREO/2ch)
   ```
3. Note the name of the device you actually want to use.
4. Open `BepInEx/config/io.github.mark-renzi.lowlatencymod.cfg` and set `Device` under `[ASIO]` to part of that name (case-insensitive, just needs to be a unique substring), e.g.:
   ```
   Device = MOTU
   ```
5. Launch the game again and confirm it took effect. Success in the log should now name your chosen device:
   ```
   >>> SUCCESS: FMOD OUTPUT IS ASIO (driver 1 = "MOTU M Series" (48000 Hz)) <<<
   ```

If `Device` doesn't match any enumerated driver, the log will say so and fall back to FMOD's default choice (usually driver 0) rather than failing outright.

If the audio is crunchy... settings live in `BepInEx/config/io.github.mark-renzi.lowlatencymod.cfg` (created after first launch) and they all have their own explanations but you should probably increase buffer count to 4 and raise buffer size a bit.

## Uninstalling

Delete the `BepInEx/plugins/LowLatencyMod/` folder.

## Building from source

Requires the .NET SDK. `LowLatencyMod.csproj` references game/BepInEx DLLs via absolute `HintPath` entries. Update those paths for your own UNBEATABLE install location, then:

```
dotnet build LowLatencyMod.csproj -c Release
```

`./package.ps1` will build and produce the release zip in `dist/`.
