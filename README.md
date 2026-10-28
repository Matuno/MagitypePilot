# Magitype Pilot

<img src="assets/logo.png" alt="A golden magical quill over keyboard keys" width="160" height="160">

An experimental Dalamud plugin that plays the Gold Saucer's Magitype typing game. It is enabled automatically when loaded and starts when the Magitype screen appears.

## What it does

- Types and submits visible phrases, choosing the lowest on-screen target first as a proximity estimate.
- Recovers when a target disappears while its phrase is being typed, provided the input still matches the plugin's own text.
- Pauses typing while the Magitype Cursed Gaze action is being cast. This behavior still needs in-game validation.
- Refreshes the game's activity timers while entering text. The plugin uses the game's text field and can operate without the game window in focus.

Open `/magitypepilot` and choose **Disable** to stop automatic play. Closing the window does not disable it. Reloading the plugin enables it again. If the input box contains text the plugin cannot identify as its own, it waits for you to clear it.

This is a work in progress. Interruptible targets are not yet prioritized, background play and the Cursed Gaze pause need further testing, and game updates can cause the plugin to pause until it is updated. Supervise it while playing.

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
