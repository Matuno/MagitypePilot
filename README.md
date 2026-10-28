# Magitype Pilot

<img src="assets/logo.png" alt="A golden magical quill over keyboard keys" width="160" height="160">

An experimental Dalamud plugin that plays the Gold Saucer's Magitype typing game. It is enabled automatically when loaded and starts when the Magitype screen appears.

## What it does

- Attempts direct actions against uniquely matched targets without typing, using a configurable target order and the lowest on-screen target as a proximity estimate.
- Keeps retrying direct actions when they are unavailable or a target remains visible; it does not enter or submit text.
- Pauses actions during Cursed Gaze and Dynamic Sensory Jammer, including a short provisional delay after the cast.
- Attempts to prioritize interruptible casts, including Magitype's flashing ??? Needles, when the caster can be matched confidently to a visible phrase by screen position.
- Attempts to recognize barrier reinforcement phrases using a nearby game object and the green phrase color.
- Optionally exits results and requeues from the Magitype lobby, with separate controls for each action.
- Choose an average human (~50 WPM), 90 WPM, fast human (~120 WPM, the default), or ludicrous action pace. Ludicrous speed retries as quickly as the game permits.
- Refreshes the game's activity timers during enabled Magitype duties, including when input is stalled. It can operate without the game window in focus.

Open `/magitypepilot` to reorder targets and change the speed, automatic exit, and requeue settings. If automatic exit is off, requeue waits until you return to the lobby. Stop-typing mechanics always take precedence. Barrier recognition is experimental and uses observed game UI and object data. Direct actions use the selected pace and pause for stop-typing mechanics. When a target cannot be matched confidently, the pilot waits or tries another eligible target. Choose **Disable** to stop automatic play and registration. Closing the window does not disable it. Reloading the plugin enables it again. The pilot does not edit or submit text in the input field.

This is a work in progress. Direct action matching, automatic requeueing, the stop-typing delay, cast matching and background play need further testing, and game updates can cause the plugin to pause until it is updated. Supervise it while playing.

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
