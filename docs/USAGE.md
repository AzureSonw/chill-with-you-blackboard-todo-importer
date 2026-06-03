# Usage Guide

## First-Time Setup

1. Install BepInEx 5 into Chill With You.
2. Build this project with `.\build.ps1`.
3. Copy the built DLL to `<GameRoot>\BepInEx\plugins`.
4. Start the game once so BepInEx creates the config file.

## Install the Blackboard Bookmarklet

1. Open `browser/install-bookmarklet.html`.
2. Show Chrome's bookmarks bar with `Ctrl+Shift+B`.
3. Drag `Blackboard -> Chill Todo` to the bookmarks bar.

If drag-and-drop does not work:

1. Create a new Chrome bookmark.
2. Name it `Blackboard -> Chill Todo`.
3. Copy the full line from `browser/blackboard-bookmarklet.txt` into the bookmark URL field.

## Sync Blackboard

1. Start Chill With You.
2. Open Blackboard `Calendar > Due Dates`.
3. Click the `Blackboard -> Chill Todo` bookmark.
4. Wait for the alert showing how many due items were sent.
5. Open the in-game Todo list.

## Troubleshooting

### The `.js` file shows Windows Script Host errors

Do not double-click `blackboard-to-chill-importer.js`. It is a browser script. Run it from Blackboard's page through DevTools Console or the bookmarklet.

### The bookmarklet says it cannot send to Chill With You

Make sure:

- The game is running.
- BepInEx loaded the plugin.
- `EnableLocalServer = true`.
- Nothing else is using port `29472`.

### Duplicate imports

Duplicates are skipped by checking the `[BB:<id>]` prefix in Todo text.

