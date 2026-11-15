<div align="center">

# Smart Buffer

**Instant replay for Windows, powered by OBS Studio.**

A free alternative to NVIDIA ShadowPlay / Instant Replay that sets itself up in one click and never turns itself off.

[Download](https://github.com/bwinslow1022/smart-buffer/releases/latest) &nbsp;&bull;&nbsp; [Install](#install) &nbsp;&bull;&nbsp; [Features](#features) &nbsp;&bull;&nbsp; [Hotkeys](#hotkeys)

</div>

## About

NVIDIA Instant Replay turns itself off whenever it sees protected video, after driver updates and after resolution changes. Smart Buffer configures OBS to keep a rolling replay buffer in the background, so you can save the last few minutes of gameplay with a single hotkey. An overlay lets you record, take screenshots, browse your clips and share them without ever opening OBS.

## Features

- **Instant Replay** keeps the last 30 seconds to 10 minutes in memory. Press a hotkey to save it as a clip.
- **Recording** starts and stops full-length recordings from the overlay or a hotkey.
- **Screenshots** are saved next to your clips with one key.
- **Overlay** opens a sidebar with Instant Replay, recording, screenshot and microphone controls.
- **Clip library** sorts clips and recordings into a folder per game, with thumbnails, sorting by date or size, and a built-in player.
- **Share copies** trim a clip and squeeze it under a size limit (10, 20, 25, 50 or 100 MB) so it uploads to Discord without Nitro. The copy lands on your clipboard, ready to paste.
- **Saved notification** shows a small "Clip saved" card in the corner of the screen you're playing on, like ShadowPlay. It never takes focus from the game.
- **ShadowPlay-style bitrates** (12, 20 or 35 Mbps) for predictable file sizes, or constant-quality modes.
- **Sharing** also lets you copy the original video file, or upload it to kappa.lol and get a link you can paste anywhere.
- **Microphone control** with device selection, an on/off toggle that works without restarting OBS, and optional RNNoise or Speex noise suppression.
- **Game or desktop capture**, with desktop capture available for games with strict anti-cheat.
- **Hardware encoding** on NVIDIA, AMD and Intel, using H.264, HEVC or AV1 when your GPU supports it.
- **One-click setup and removal**, using its own OBS profile so your existing profiles are left alone.

## Install

1. Download `SmartBuffer.exe` from the [latest release](https://github.com/bwinslow1022/smart-buffer/releases/latest).
2. Run it, click **Set up** and accept the admin prompt.

Or run this in PowerShell:

```powershell
irm https://raw.githubusercontent.com/bwinslow1022/smart-buffer/main/install.ps1 | iex
```

> [!NOTE]
> The exe isn't code signed yet, so Windows SmartScreen may show a warning. Click **More info**, then **Run anyway**.

**Requirements:** Windows 10 or 11. OBS Studio 31 or newer is installed for you if it's missing or out of date.

## Hotkeys

| Action | Default |
| --- | --- |
| Save the last few minutes | <kbd>Alt</kbd> + <kbd>M</kbd> |
| Open the overlay | <kbd>Alt</kbd> + <kbd>X</kbd> |
| Start or stop recording | <kbd>Alt</kbd> + <kbd>F9</kbd> |
| Save a screenshot | <kbd>Alt</kbd> + <kbd>F1</kbd> |
| Turn Instant Replay on or off | <kbd>Alt</kbd> + <kbd>Shift</kbd> + <kbd>F10</kbd> |
| Turn the microphone on or off | <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>M</kbd> |

All hotkeys can be changed in **Settings > Hotkeys**. While Smart Buffer is running these combinations belong to it, so a game that uses one of them won't receive it. Press <kbd>Delete</kbd> in a hotkey box to turn that hotkey off.

## Where your files go

| Type | Location |
| --- | --- |
| Clips | `Videos\Clips\<game>` |
| Recordings | `Videos\Recordings\<game>` |
| Screenshots | Next to your clips, in the game's folder |

Both folders can be changed in **Settings > Clips**, along with clip length and sorting by game.

## Sharing clips

**Make share copy** (in the clip player) trims the clip to the start and end you pick, then re-encodes it to fit the size you choose. Lower limits and longer clips get a lower resolution, because that looks better than a blocky full-size picture. On NVIDIA GPUs it encodes on the GPU, so a 2 minute clip takes about 10 seconds. "Full quality (trim only)" cuts without re-encoding and is instant.

The first time, it downloads [ffmpeg](https://ffmpeg.org) 9.0.2 (about 115 MB) from [GyanD's builds](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2) and checks it against a pinned SHA-256 before using it.

**Upload to kappa.lol** sends the clip to [kappa.lol](https://kappa.lol), a free third-party file host, and gives you a public link. Uploads only happen when you click the button.

- Files can be up to 100 MB.
- Anyone with the link can watch the clip.
- kappa.lol stores your IP address and doesn't state how long it keeps files, so use **Delete** when you're done.

## What it changes on your PC

- Installs OBS Studio if you don't have it.
- Adds its own OBS profile and scene collection. Your other profiles stay as they are.
- Starts OBS in the tray at login, running as admin. Both can be turned off in settings.
- Turns off Xbox Game Bar capture and the OBS exit warning.

**Remove** in the app puts all of that back. Your clips and OBS itself are kept.

## Good to know

- **Anti-cheat:** game capture hooks into fullscreen games, so some anti-cheats may block it or object. Switch to desktop capture in settings for games like Valorant, CS2 or FACEIT, since it doesn't touch the game. The overlay itself is a normal window and never injects into games.
- **Desktop capture** records the whole monitor, so notifications and the overlay can end up in clips.
- **Microphone:** your mic is recorded by default. Turn it off from the overlay if you don't want that.
- **DRM:** Smart Buffer doesn't get around DRM. Protected video should show up black in clips.
- **Crash recovery:** if OBS crashes, Smart Buffer skips the safe mode question so recording comes back on its own. This also means you won't be offered safe mode if a plugin keeps crashing OBS.
- **Old OBS plugins:** an outdated plugin can make OBS show a plugin load error when it starts. Just click OK.
- **GPU support:** only tested on NVIDIA so far. AMD and Intel should work, but haven't been tested yet.

## Building from source

Requires the [.NET SDK](https://dotnet.microsoft.com/download). The app targets .NET Framework 4.8, which is built into Windows.

```powershell
dotnet build SmartBuffer.csproj -c Release
```

The exe is written to `bin\Release\net48\SmartBuffer.exe`.

## Credits

Smart Buffer is a fork of [ShadBuffer](https://github.com/seandotnet/ShadBuffer) by seandotnet, used under the MIT license. It adds share copies sized for Discord, a saved notification, fixed-bitrate presets, a modern folder picker and other changes.

## License

[MIT](LICENSE). Smart Buffer is not affiliated with or endorsed by the OBS Project or NVIDIA.
