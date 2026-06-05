# Chill With You Blackboard Todo Importer

[English](README.md) | [简体中文](README.zh-CN.md)

This project was built with AI assistance.

BepInEx 5 plugin for **Chill With You: Lo-Fi Story** that imports Blackboard Calendar deadlines into the in-game Todo list.

The project has two parts:

- `src/BlackboardTodoImporterPlugin.cs`: the BepInEx plugin loaded by the Unity game.
- `browser/blackboard-to-chill-importer.js`: a Blackboard Calendar bookmarklet script that scans due items and sends them to the local game plugin.

## Features

- Imports Blackboard due items as normal `Bulbul.TodoData`.
- Uses `TodoListData.AddTodo(todo)`, so the game saves through its own save system.
- Avoids duplicates with stable Blackboard-derived IDs and normalized title cleanup.
- Keeps new todos in the `Working` state.
- Adds expiration dates with `TodoData.SetExpire(DateTime)`.
- Finds the active/current Todo list through reflection and Harmony patches.
- Falls back to the first saved Todo list if no active list is found.
- Starts a localhost import endpoint at `http://127.0.0.1:29472/blackboard-import`.
- Includes a browser bookmarklet that scans Blackboard `Calendar > Due Dates` for the next 21 days.
- Extracts course subjects from Blackboard due-item course links, so titles can look like `CS - Example Homework`, `CS - Example Project`, or `CS - Example Quiz`.
- Cleans older imported Blackboard items, including legacy `[BB:...]` titles and accidentally duplicated date-header titles such as `Today - July 15, 2026 Example Homework`.

## Requirements

- Windows
- Chill With You: Lo-Fi Story installed through Steam
- BepInEx 5 installed in the game folder
- Chrome with an already logged-in Blackboard session
- PowerShell for building with `build.ps1` if you build from source

The examples below use this game folder:

```text
SteamLibrary\steamapps\common\Chill with You Lo-Fi Story
```

If your Steam library is on another drive or folder, use your own game root path in the same places.

## Quick Install From Release

Use this path if you only want to install the plugin and bookmarklet.

1. Open the latest release:

```text
https://github.com/AzureSonw/chill-with-you-blackboard-todo-importer/releases/latest
```

2. Download these release assets:

```text
ChillWithYou.BlackboardTodoImporter.dll
BlackboardAutoImportJS-v1.1.0.zip
```

3. Copy the DLL into the game's BepInEx plugin folder:

```text
<GameRoot>\BepInEx\plugins\ChillWithYou.BlackboardTodoImporter.dll
```

For the Steam library layout used during development, that path looks like:

```text
SteamLibrary\steamapps\common\Chill with You Lo-Fi Story\BepInEx\plugins\ChillWithYou.BlackboardTodoImporter.dll
```

4. Extract `BlackboardAutoImportJS-v1.1.0.zip` anywhere convenient.

5. Open the extracted file:

```text
install-bookmarklet.html
```

6. In Chrome, show the bookmarks bar with `Ctrl+Shift+B`, then drag the `Blackboard -> Chill Todo` button to the bookmarks bar.

If dragging does not work, create a Chrome bookmark manually and paste the full single-line contents of `blackboard-bookmarklet.txt` into the bookmark URL field.

7. Start or restart Chill With You.

8. Confirm the plugin loaded in:

```text
<GameRoot>\BepInEx\LogOutput.log
```

Expected log lines include:

```text
Loading [Blackboard Todo Importer 1.1.0]
Blackboard bookmarklet HTTP server listening on http://127.0.0.1:29472/blackboard-import
```

## Use Blackboard Auto Import

1. Start Chill With You and leave it running.
2. Open Chrome and go to Blackboard `Calendar > Due Dates`.
3. Click the `Blackboard -> Chill Todo` bookmark.
4. Confirm the browser popup if the detected due items look correct.
5. Open the in-game Todo panel.

The game may need the Todo panel to be reopened, or the game restarted, before newly imported items appear in the UI.

## Build From Source

1. Build the plugin:

```powershell
.\build.ps1
```

If the script cannot find your game folder automatically, pass it explicitly:

```powershell
.\build.ps1 -GameRoot "<GameRoot>"
```

2. Copy the built DLL:

```text
bin\Release\ChillWithYou.BlackboardTodoImporter.dll
```

to:

```text
<GameRoot>\BepInEx\plugins\ChillWithYou.BlackboardTodoImporter.dll
```

3. Start or restart Chill With You.

4. Confirm the plugin loaded in:

```text
<GameRoot>\BepInEx\LogOutput.log
```

You should see log lines from `Blackboard Todo Importer`.

## Manual JSON Import

The plugin also supports a manual JSON file:

```text
<GameRoot>\BepInEx\config\blackboard_tasks.json
```

Example:

```json
[
  {
    "id": "example-assignment-id",
    "title": "CS - Example Homework 8",
    "due": "2026-07-15T23:59:00"
  }
]
```

Press `F10` in-game to import the JSON file.

## Configuration

BepInEx creates:

```text
<GameRoot>\BepInEx\config\com.local.chillwithyou.blackboardtodoimporter.cfg
```

Important settings:

```ini
[Import]
AutoImportOnStart = true
Hotkey = F10
HttpPort = 29472
```

The local HTTP endpoint is:

```text
http://127.0.0.1:29472/blackboard-import
```

## Privacy

The browser script reads due items from the Blackboard page that is already open in your browser. It sends the extracted JSON only to localhost:

```text
http://127.0.0.1:29472/blackboard-import
```

Do not commit real `blackboard_tasks.json` files or exported assignment payloads. `.gitignore` excludes common local/private payload names.

## Notes

- Ordinary Todo items use `TodoData`; this project intentionally does not use `TaskES3`.
- The plugin uses reflection for game types so small game updates are less likely to break assembly loading.
- Blackboard's DOM can vary by school/theme. The browser script uses visible due cards first and falls back to collapsed month-list text.
