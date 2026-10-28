# Magitype Pilot

<img src="assets/logo.png" alt="A golden magical quill over keyboard keys" width="160" height="160">

An experimental Dalamud plugin that plays the Gold Saucer's Magitype typing game. It is enabled automatically when loaded and starts when the Magitype screen appears.

## What it does

- Types and submits visible phrases, using a configurable target order and the lowest on-screen target as a proximity estimate.
- Recovers when a target disappears while its phrase is being typed, provided the input still matches the plugin's own text.
- Pauses typing during Cursed Gaze and Dynamic Sensory Jammer, including a short provisional delay after the cast.
- Attempts to prioritize interruptible casts, including Magitype's flashing ??? Needles, when the caster can be matched confidently to a visible phrase by screen position.
- Automatically registers again from the Magitype lobby while the plugin is enabled.
- Types at a fast human pace by default, speeding up when an enemy cast reaches 70% progress. Optional ludicrous speed types as fast as the game update loop permits.
- Offers an experimental option to mute sounds made by automated text entry.
- Refreshes the game's activity timers while entering text. The plugin uses the game's text field and can operate without the game window in focus.

Open `/magitypepilot` to reorder targets and change the speed and sound settings. Stop-typing mechanics always take precedence. Barrier recognition is not implemented yet. Choose **Disable** to stop automatic play and registration. Closing the window does not disable it. Reloading the plugin enables it again. The pilot resumes matching partial input or submits unmatched input to recover; disable it before typing manually if you do not want that text submitted.

This is a work in progress. Automatic requeueing, the stop-typing delay, cast matching and background play need further testing, and game updates can cause the plugin to pause until it is updated. Supervise it while playing.

## Install

1. Open **Dalamud Settings → Experimental → Custom Plugin Repositories** and add:

   ```text
   https://raw.githubusercontent.com/Matuno/DalamudPlugins/main/repo.json
   ```

2. Enable the repository and save. In the Plugin Installer, search for **Magitype Pilot** and install it.
3. Open `/magitypepilot` to see its status or disable it.

## Build

Build with the .NET 10 SDK and Dalamud.NET.Sdk 15.0.0 using `dotnet build -c Release`. Dalamud supplies the game libraries.

By Matuno. Developed with substantial OpenAI Codex assistance; logo generated with AI. [AGPL-3.0 license](LICENSE).
