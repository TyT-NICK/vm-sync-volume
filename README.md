# Voicemeeter Volume Sync

A tiny tray app that makes Voicemeeter strips and buses follow the Windows volume slider (and mute).
Use your keyboard volume keys, the Windows volume flyout or any app that changes the system volume:
the chosen Voicemeeter faders move with it.

- **No admin rights.** Runs as a normal user, and autostarts from the per-user `Run` registry key.
- **Portable.** It's a single ~40 KB exe with no installer, runtime, Node or Python. Settings sit in an INI file next to the exe.
- **Works with every edition.** Voicemeeter, Banana and Potato are all supported. The menu lists the strips and buses that the running edition actually has, using your custom labels.
- **Event driven.** Windows volume changes are pushed instantly through Core Audio notifications, with no polling of the volume.

## Usage

1. Run `VMVolumeSync.exe`. A speaker icon appears in the tray. A red dot on it means Voicemeeter isn't running.
2. Click the icon, open **Inputs (strips)** or **Outputs (buses)**, and tick the controls that should follow Windows.
   You'll usually want the virtual input that Windows plays into, such as *Voicemeeter VAIO*.
3. Optionally enable **Start with Windows**.

| Menu | What it does |
| --- | --- |
| Inputs / Outputs | Pick any number of strips or buses (hardware and virtual) |
| Windows output device | Follow the default device (it tracks device switches) or one specific device |
| Gain range | dB sent at Windows 0% and at 100% (default −60 … +12 dB) |
| Volume curve | *Linear in dB*, or *Logarithmic*: `max + 20·log10(volume)`, like a real attenuator |
| Sync mute | Windows mute sets the targets' Mute |
| Two-way sync | Moving or muting a bound control in Voicemeeter also changes the Windows volume, and the other bound controls follow |
| Show Voicemeeter / Restart audio engine | Shortcuts to Voicemeeter commands |

Two-way sync is off by default.
When it's on, the bound controls stay within the gain range. If you set one outside the range in Voicemeeter, all of them snap back to the nearest end of the range.

## Settings

The app saves settings to `VMVolumeSync.ini` next to the exe. If that folder isn't writable (for example, under Program Files), it uses `%APPDATA%\VMVolumeSync\` instead.
You can edit the file by hand while the app is closed:

```ini
[VMVolumeSync]
Targets=Strip[3],Bus[0]
Device=
GainMin=-60
GainMax=12
Curve=linear
SyncMute=1
TwoWay=0
; Only needed if Voicemeeter is installed somewhere unusual
VoicemeeterDir=
```

## Building

You need only Windows. `build.cmd` uses the C# compiler that ships with the .NET Framework 4.x, which is preinstalled on Windows 10 and 11:

```bat
build.cmd
```

The build produces `VMVolumeSync.exe` in the repository root. All the source is in `src/VMVolumeSync.cs`.

## How it works

- **Windows side:** it uses the Core Audio `IAudioEndpointVolume` interface with a change-notify callback, plus `IMMNotificationClient` to notice default-device and hot-plug changes.
- **Voicemeeter side:** it loads `VoicemeeterRemote64.dll` from the Voicemeeter install folder (located through the registry) and talks to it through VB-Audio's official Remote API. It gets the edition from `VBVMR_GetVoicemeeterType` and sends updates with `VBVMR_SetParameters`, for example `Strip[3].Gain=-24.0;Strip[3].Mute=0;`.
- **Two-way sync:**
  - Voicemeeter has no change notifications, so the app calls `VBVMR_IsParametersDirty` every 100 ms and reads the bound controls only when something changed.
  - It keeps track of the last value sent to each control. It ignores read-backs that match, plus anything from a control written within the last 400 ms. This stops the two sides from echoing each other in a loop.
  - It never writes back to the control you're dragging in Voicemeeter.
- **Reconnecting:** Voicemeeter can start before or after the app, and can be restarted or switched to another edition. The app reconnects on its own and re-sends the current volume.

## Credits

This project was inspired by [voicemeeter-windows-volume](https://github.com/Frosthaven/voicemeeter-windows-volume) by **Frosthaven**, and it's meant as a lightweight alternative that doesn't need admin rights.

This project is a from-scratch rewrite in C#; it contains no code from the original. These ideas come from the original:

- The core concept: a tray menu ("Bind Windows Volume To") where you tick the Voicemeeter inputs and outputs that should follow the Windows volume.
- The configurable gain range (`gain_min` / `gain_max`, default −60 to +12 dB). The original's "Limit Max Gain To 0dB" option corresponds to choosing a 0 dB maximum here.
- The choice between linear and logarithmic volume scaling ("Use Linear Volume Scaling").
- "Sync Mute".
- The tray shortcut to restart the Voicemeeter audio engine, and the option to start with Windows. The original registers a scheduled task with elevated rights; this app uses a per-user `Run` registry entry, which needs no admin rights.

These features of the original aren't included here:

- **"Apply Crackle Fix":** it sets `audiodg.exe` priority and CPU affinity, which needs admin rights. Avoiding admin rights is the main point of this project.
- **"Restore Volume At Launch":** it remembers the volume and restores it on the next launch.
- **"Prevent 100% Volume Spikes":** it works around drivers that reset volume to 100% when the audio engine restarts.
- **Automatic audio engine restarts on device change, resume or launch.**
- **The device blacklist and the configurable polling rate.** This app reacts to Windows volume events instead of polling.

## License

MIT
