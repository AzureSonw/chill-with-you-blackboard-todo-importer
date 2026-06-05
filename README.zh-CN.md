# Chill With You Blackboard Todo Importer

这是一个为 **Chill With You: Lo-Fi Story** 制作的 Blackboard Todo 导入插件。  
它可以把 Blackboard `Calendar > Due Dates` 里的任务导入到游戏内 Todo 列表中，让 deadline 以更轻松的方式显示在游戏里。

这个项目是在 AI 协助下完成的。AI 主要帮助我整理思路、排查问题、优化代码结构和润色文档；项目需求、测试过程和最终调整方向都是根据实际使用场景一步一步完成的。

---

## ✨ 功能

- 从 Blackboard `Calendar > Due Dates` 页面读取 due items
- 自动提取任务标题、课程简称和截止时间
- 通过本地 HTTP 接口发送到游戏插件
- 在游戏内创建普通 Todo 项
- 使用稳定 ID 避免重复导入
- 支持手动 JSON 导入
- 支持 `F10` 快捷键重新导入
- 数据只发送到 `localhost`，不会上传到外部服务器

---

## 📦 项目内容

项目主要包含两部分：

```text
src/BlackboardTodoImporterPlugin.cs
```

游戏端 BepInEx 插件，负责接收任务并写入游戏内 Todo 列表。

```text
browser/blackboard-to-chill-importer.js
```

浏览器书签脚本，负责从 Blackboard 页面读取 due items，并发送给本地插件。

---

## 🛠️ 安装步骤

### 环境要求

- Windows
- Steam 版 **Chill With You: Lo-Fi Story**
- Chrome 浏览器
- 已登录 Blackboard
- BepInEx 5.4.x 或更高版本

---

### 1. 安装 BepInEx

1. 下载 **BepInEx 5 x64** 版本。
2. 解压 BepInEx。
3. 把解压后的文件复制到游戏根目录。

游戏根目录大概类似：

```text
SteamLibrary\steamapps\common\Chill with You Lo-Fi Story
```

复制完成后，结构应该类似：

```text
[游戏根目录]/
├── BepInEx/
├── doorstop_config.ini
├── winhttp.dll
└── Chill With You.exe
```

4. 运行一次游戏。
5. 关闭游戏后，确认已经生成：

```text
BepInEx/plugins/
BepInEx/config/
BepInEx/LogOutput.log
```

如果这些文件夹和日志出现，说明 BepInEx 已经安装成功。

---

### 2. 安装插件

从 Release 下载最新版本的插件文件：

```text
ChillWithYou.BlackboardTodoImporter.dll
```

然后把它放到：

```text
[游戏根目录]/BepInEx/plugins/
```

最终结构应该类似：

```text
[游戏根目录]/
└── BepInEx/
    └── plugins/
        └── ChillWithYou.BlackboardTodoImporter.dll
```

启动游戏后，可以在日志里看到类似内容：

```text
Blackboard Todo Importer loaded.
Blackboard bookmarklet HTTP server listening on http://127.0.0.1:29472/blackboard-import
```

如果没有看到，可以检查：

```text
BepInEx/LogOutput.log
```

---

### 3. 安装浏览器书签脚本

从 Release 下载并解压：

```text
BlackboardAutoImportJS-v1.1.0.zip
```

打开里面的：

```text
install-bookmarklet.html
```

然后把页面里的：

```text
Blackboard -> Chill Todo
```

按钮拖到 Chrome 书签栏。

如果拖动失败，也可以手动创建 Chrome 书签，并把：

```text
blackboard-bookmarklet.txt
```

里的内容复制到书签 URL 里。

---

## 🚀 使用方法

1. 启动 **Chill With You: Lo-Fi Story**。
2. 打开 Blackboard 的 `Calendar > Due Dates` 页面。
3. 点击 Chrome 书签栏里的 `Blackboard -> Chill Todo`。
4. 浏览器会弹出检测到的任务列表。
5. 确认后，任务会发送到游戏插件。
6. 回到游戏内打开 Todo 面板查看。

有时候游戏 UI 不会立刻刷新，可以尝试：

- 重新打开 Todo 面板
- 等几秒
- 或重启游戏

---

## 📄 手动 JSON 导入

插件也支持手动导入 JSON 文件。

文件路径：

```text
[游戏根目录]/BepInEx/config/blackboard_tasks.json
```

示例内容：

```json
[
  {
    "id": "example-assignment-id",
    "title": "Example Homework",
    "due": "2026-07-15T23:59:00"
  }
]
```

放好文件后，在游戏内按：

```text
F10
```

即可手动导入。

---

## ⚙️ 配置文件

插件会自动生成配置文件：

```text
[游戏根目录]/BepInEx/config/com.local.chillwithyou.blackboardtodoimporter.cfg
```

常用配置：

```ini
[Import]
AutoImportOnStart = true
Hotkey = F10
HttpPort = 29472
```

说明：

- `AutoImportOnStart`：游戏启动后是否自动尝试导入
- `Hotkey`：手动导入快捷键
- `HttpPort`：本地 HTTP 接口端口

---

## 🧪 从源码构建

如果想自己构建插件，可以运行：

```powershell
.\build.ps1
```

如果脚本找不到游戏目录，可以手动指定：

```powershell
.\build.ps1 -GameRoot "<GameRoot>"
```

构建完成后，生成的 DLL 会在：

```text
bin/Release/
```

把 DLL 放入：

```text
[游戏根目录]/BepInEx/plugins/
```

然后重启游戏即可。

---

## 🔒 隐私说明

这个工具只读取当前浏览器里已经打开的 Blackboard 页面。

提取出的任务数据只会发送到本机地址：

```text
http://127.0.0.1:29472/blackboard-import
```

不会上传到外部服务器。

建议不要把真实的：

```text
blackboard_tasks.json
```

或其他导出的个人任务数据提交到公开仓库。

---

## 📝 备注

这个项目主要是一个个人工具型项目，用来练习：

- BepInEx 插件开发
- Unity 游戏运行时数据修改
- 浏览器脚本
- 本地 HTTP 通信
- JSON 数据处理
- 调试和整理一个完整的小工具

后续如果继续改，可能会优化：

- Blackboard 页面解析
- 游戏内 UI 刷新
- 错误提示
- 配置界面

---

## ⚠️ 免责声明

本项目仅供学习和个人使用。  
不同版本的游戏、BepInEx 或 Blackboard 页面结构可能会导致功能失效。

使用前建议备份游戏存档和配置文件。  
如果游戏或 Blackboard 页面更新，插件可能需要重新调整。
