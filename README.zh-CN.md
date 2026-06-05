# Chill With You Blackboard Todo Importer

[English](README.md) | [简体中文](README.zh-CN.md)

本项目开发过程中使用了 AI 协助。

这是一个给 **Chill With You: Lo-Fi Story** 用的 BepInEx 5 插件，可以把 Blackboard Calendar / Due Dates 里的截止事项导入到游戏内 Todo 列表。

项目包含两部分：

- `src/BlackboardTodoImporterPlugin.cs`：Unity 游戏里加载的 BepInEx 插件。
- `browser/blackboard-to-chill-importer.js`：Blackboard 页面用的 Chrome 书签脚本，负责扫描 due items 并发送给本地游戏插件。

## 功能

- Blackboard due items 会作为普通 `Bulbul.TodoData` 导入。
- 使用 `TodoListData.AddTodo(todo)`，让游戏自己的保存系统处理 Todo。
- 使用稳定的 Blackboard 派生 ID 和标题规范化来避免重复。
- 新导入的 Todo 默认保持 `Working` 状态。
- 使用 `TodoData.SetExpire(DateTime)` 设置截止日期。
- 通过反射和 Harmony patch 查找当前活跃 Todo 列表。
- 找不到活跃列表时，会回退到第一个已保存 Todo 列表。
- 启动本地导入接口：`http://127.0.0.1:29472/blackboard-import`。
- 附带 Chrome 书签脚本，可扫描 Blackboard `Calendar > Due Dates` 未来 21 天内的项目。
- 会从 Blackboard 课程链接里提取 subject，例如 `CS - Example Homework`、`CS - Example Project`、`CS - Example Quiz`。
- 会清理旧导入项，包括遗留的 `[BB:...]` 标题，以及误带日期头的标题，例如 `Today - July 15, 2026 Example Homework`。

## 需求

- Windows
- Steam 安装的 Chill With You: Lo-Fi Story
- 游戏目录里已经安装 BepInEx 5
- Chrome，并且已经登录 Blackboard
- 如果要从源码构建，需要 PowerShell 运行 `build.ps1`

下面示例使用的游戏目录是：

```text
SteamLibrary\steamapps\common\Chill with You Lo-Fi Story
```

如果你的 Steam 库在其他盘或其他文件夹，把示例里的 `<GameRoot>` 换成你自己的游戏根目录即可。

## 从 Release 快速安装

如果你只想安装插件和书签脚本，用这个方式最快。

1. 打开最新版 Release：

```text
https://github.com/AzureSonw/chill-with-you-blackboard-todo-importer/releases/latest
```

2. 下载这两个 release 文件：

```text
ChillWithYou.BlackboardTodoImporter.dll
BlackboardAutoImportJS-v1.1.0.zip
```

3. 把 DLL 放进游戏的 BepInEx 插件文件夹：

```text
<GameRoot>\BepInEx\plugins\ChillWithYou.BlackboardTodoImporter.dll
```

按开发时的 Steam 库结构，路径看起来像这样：

```text
SteamLibrary\steamapps\common\Chill with You Lo-Fi Story\BepInEx\plugins\ChillWithYou.BlackboardTodoImporter.dll
```

4. 解压 `BlackboardAutoImportJS-v1.1.0.zip` 到任意方便的位置。

5. 用 Chrome 打开解压后的文件：

```text
install-bookmarklet.html
```

6. 在 Chrome 按 `Ctrl+Shift+B` 显示书签栏，然后把页面里的 `Blackboard -> Chill Todo` 按钮拖到书签栏。

如果拖不动，就手动新建一个 Chrome 书签，把 `blackboard-bookmarklet.txt` 里的完整单行内容粘贴到书签的网址字段。

7. 启动或重启 Chill With You。

8. 打开日志确认插件加载：

```text
<GameRoot>\BepInEx\LogOutput.log
```

正常会看到类似：

```text
Loading [Blackboard Todo Importer 1.1.0]
Blackboard bookmarklet HTTP server listening on http://127.0.0.1:29472/blackboard-import
```

## 从 Blackboard 自动导入

1. 启动 Chill With You，并保持游戏运行。
2. 在 Chrome 打开 Blackboard 的 `Calendar > Due Dates` 页面。
3. 点击书签栏里的 `Blackboard -> Chill Todo`。
4. 浏览器弹窗会显示检测到的 due items，确认无误后发送。
5. 打开游戏内 Todo 面板查看。

游戏 UI 有时需要重新打开 Todo 面板，或重启游戏后，才会显示新导入项目。

## 从源码构建

1. 构建插件：

```powershell
.\build.ps1
```

如果脚本无法自动找到游戏目录，就手动传入：

```powershell
.\build.ps1 -GameRoot "<GameRoot>"
```

2. 把构建出的 DLL：

```text
bin\Release\ChillWithYou.BlackboardTodoImporter.dll
```

复制到：

```text
<GameRoot>\BepInEx\plugins\ChillWithYou.BlackboardTodoImporter.dll
```

3. 启动或重启 Chill With You。

4. 打开日志确认插件加载：

```text
<GameRoot>\BepInEx\LogOutput.log
```

能看到 `Blackboard Todo Importer` 的日志就说明插件已运行。

## 手动 JSON 导入

插件也支持手动 JSON 文件：

```text
<GameRoot>\BepInEx\config\blackboard_tasks.json
```

示例：

```json
[
  {
    "id": "example-assignment-id",
    "title": "CS - Example Homework 8",
    "due": "2026-07-15T23:59:00"
  }
]
```

在游戏内按 `F10` 可以导入这个 JSON 文件。

## 配置

BepInEx 会创建配置文件：

```text
<GameRoot>\BepInEx\config\com.local.chillwithyou.blackboardtodoimporter.cfg
```

常用设置：

```ini
[Import]
AutoImportOnStart = true
Hotkey = F10
HttpPort = 29472
```

本地 HTTP 接口是：

```text
http://127.0.0.1:29472/blackboard-import
```

## 隐私

浏览器脚本只读取你当前已经打开的 Blackboard 页面里的 due items。它只会把提取出的 JSON 发送到本机：

```text
http://127.0.0.1:29472/blackboard-import
```

不要提交真实的 `blackboard_tasks.json` 或导出的作业数据。`.gitignore` 已经排除了常见的本地和私有 payload 文件名。

## 备注

- 普通 Todo 使用 `TodoData`，本项目故意不使用 `TaskES3`。
- 插件通过反射访问游戏类型，降低小游戏更新导致 assembly 加载失败的概率。
- Blackboard 的 DOM 会因学校和主题不同而变化。浏览器脚本优先读取可见 due cards，也会回退读取折叠的月份列表文本。
