# Chill With You Blackboard Todo Importer

BepInEx 5 plugin for **Chill With You: Lo-Fi Story** that imports Blackboard Calendar deadlines into the in-game Todo list.

The project has two parts:

- `src/BlackboardTodoImporterPlugin.cs`: the BepInEx plugin loaded by the Unity game.
- `browser/blackboard-to-chill-importer.js`: a Blackboard Calendar bookmarklet script that scans due items and sends them to the local game plugin.

## Features

- Imports Blackboard due items as normal `Bulbul.TodoData`.
- Uses `TodoListData.AddTodo(todo)`, so the game saves through its own save system.
- Avoids duplicates with a stable `[BB:<id>]` prefix.
- Keeps new todos in the `Working` state.
- Adds expiration dates with `TodoData.SetExpire(DateTime)`.
- Finds the active/current Todo list through reflection and Harmony patches.
- Falls back to the first saved Todo list if no active list is found.
- Starts a localhost import endpoint at `http://127.0.0.1:29472/blackboard-import`.
- Includes a browser bookmarklet that scans Blackboard `Calendar > Due Dates` for the next 14 days.
- Formats titles like `MATH-122 - Assignment 10`; if too long, it uses only the assignment name.

## Requirements

- Windows
- Chill With You: Lo-Fi Story installed through Steam
- BepInEx 5 installed in the game folder
- Chrome with an already logged-in Blackboard session
- PowerShell for building with `build.ps1`

The default game path used by the build script is:

```text
D:\SteamLibrary\steamapps\common\Chill with You Lo-Fi Story
```

Pass `-GameRoot` to `build.ps1` if your install path is different.

## Install

1. Build the plugin:

```powershell
.\build.ps1
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

You should see log lines from `Chill With You Blackboard Todo Importer`.

## Use Blackboard Auto Import

1. Start Chill With You and leave it running.
2. Open Chrome and go to Blackboard `Calendar > Due Dates`.
3. Run the bookmarklet from `browser/blackboard-bookmarklet.txt`, or paste `browser/blackboard-to-chill-importer.js` into the DevTools Console.
4. The script scans today through the next 14 days and sends found due items to the game plugin.
5. Open the in-game Todo panel.

For a friendlier bookmarklet installer, open:

```text
browser/install-bookmarklet.html
```

Then drag the `Blackboard -> Chill Todo` button to Chrome's bookmarks bar.

## Manual JSON Import

The plugin also supports a manual JSON file:

```text
<GameRoot>\BepInEx\config\blackboard_tasks.json
```

Example:

```json
[
  {
    "id": "blackboard-course-assignment-id",
    "title": "MATH-122 - Homework 8",
    "due": "2026-06-05T23:59:00"
  }
]
```

Press `F10` in-game to import the JSON file.

## Configuration

BepInEx creates:

```text
<GameRoot>\BepInEx\config\local.chillwithyou.blackboard.todoimporter.cfg
```

Important settings:

```ini
[BrowserImport]
EnableLocalServer = true
LocalServerPort = 29472

[Input]
ImportHotkey = F10
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

