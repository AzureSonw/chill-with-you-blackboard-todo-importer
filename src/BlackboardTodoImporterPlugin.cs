using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ChillWithYou.BlackboardTodoImporter
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BlackboardTodoImporterPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "local.chillwithyou.blackboard.todoimporter";
        private const string PluginName = "Chill With You Blackboard Todo Importer";
        private const string PluginVersion = "1.0.0";
        private const string ConfigFileName = "blackboard_tasks.json";

        private static ManualLogSource Log;
        private static readonly object Sync = new object();
        private static BlackboardTodoImporterPlugin _instance;
        private static volatile bool _applicationQuitting;

        private Harmony _harmony;
        private string _jsonPath;
        private float _nextReadyCheck;
        private bool _loggedReady;
        private int _lastHotkeyFrame = -1;
        private ConfigEntry<KeyCode> _importHotkey;
        private ConfigEntry<bool> _importOnceWhenReady;
        private ConfigEntry<bool> _enableBrowserImportServer;
        private ConfigEntry<int> _browserImportPort;
        private SynchronizationContext _unityContext;
        private TcpListener _browserImportListener;
        private Thread _browserImportThread;
        private volatile bool _browserImportServerRunning;

        private static Type _saveDataManagerType;
        private static Type _todoAllDataType;
        private static Type _todoListDataType;
        private static Type _todoDataType;
        private static Type _todoStateType;
        private static Type _todoListUiType;
        private static Type _mobileTodoListModelType;

        private static object _lastTodoListUi;
        private static object _lastMobileTodoListModel;
        private static ulong _lastTodoListId;

        private void Awake()
        {
            _instance = this;
            _applicationQuitting = false;
            Log = Logger;
            transform.SetParent(null);
            DontDestroyOnLoad(gameObject);
            _jsonPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
            _unityContext = SynchronizationContext.Current;
            _importHotkey = Config.Bind("Input", "ImportHotkey", KeyCode.F10, "Hotkey used to import Blackboard deadlines.");
            _importOnceWhenReady = Config.Bind("Import", "ImportOnceWhenReady", false, "When true, import once after TodoAllData becomes available, then reset this value to false.");
            _enableBrowserImportServer = Config.Bind("BrowserImport", "EnableLocalServer", true, "When true, listen on 127.0.0.1 so the Blackboard bookmarklet can push deadlines directly into the game.");
            _browserImportPort = Config.Bind("BrowserImport", "LocalServerPort", 29472, "Localhost port used by the Blackboard browser importer.");

            DiscoverGameTypes();
            InstallHarmonyHooks();

            Logger.LogInfo(PluginName + " loaded.");
            Logger.LogInfo("Config JSON path: " + _jsonPath);
            Logger.LogInfo("Press " + _importHotkey.Value + " in-game to import Blackboard deadlines.");
            Application.onBeforeRender += OnBeforeRender;
            StartBrowserImportServer();
        }

        private void OnDestroy()
        {
            if (!_applicationQuitting)
            {
                Logger.LogWarning("Plugin component was destroyed before application quit; keeping Harmony hooks and browser import server alive.");
                return;
            }

            Application.onBeforeRender -= OnBeforeRender;
            StopBrowserImportServer();
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
            Application.onBeforeRender -= OnBeforeRender;
            StopBrowserImportServer();
        }

        public void Update()
        {
            TickImporter();
        }

        private void OnBeforeRender()
        {
            TickImporter();
        }

        private void TickImporter()
        {
            if (Input.GetKeyDown(_importHotkey.Value))
            {
                if (_lastHotkeyFrame == Time.frameCount)
                {
                    return;
                }

                _lastHotkeyFrame = Time.frameCount;
                ImportFromJson();
            }

            if (!_loggedReady && Time.unscaledTime >= _nextReadyCheck)
            {
                _nextReadyCheck = Time.unscaledTime + 5f;
                if (TryGetTodoAllData(false) != null)
                {
                    _loggedReady = true;
                    Logger.LogInfo("SaveDataManager.TodoAllData is available. F10 import is ready.");
                    ImportOnceIfConfigured("ready poll");
                }
            }
        }

        private void DiscoverGameTypes()
        {
            // Keep game-facing types late-bound so small Assembly-CSharp changes do not
            // prevent the plugin assembly itself from loading.
            _saveDataManagerType = FindType("Bulbul.SaveDataManager");
            _todoAllDataType = FindType("Bulbul.TodoAllData");
            _todoListDataType = FindType("Bulbul.TodoListData");
            _todoDataType = FindType("Bulbul.TodoData");
            _todoStateType = FindType("TodoState");
            _todoListUiType = FindType("TodoListUI");
            _mobileTodoListModelType = FindType("Bulbul.Mobile.TodoListUIModel");

            LogTypeStatus("SaveDataManager", _saveDataManagerType);
            LogTypeStatus("TodoAllData", _todoAllDataType);
            LogTypeStatus("TodoListData", _todoListDataType);
            LogTypeStatus("TodoData", _todoDataType);
            LogTypeStatus("TodoState", _todoStateType);
            LogTypeStatus("TodoListUI", _todoListUiType);
            LogTypeStatus("Mobile TodoListUIModel", _mobileTodoListModelType);
        }

        private void InstallHarmonyHooks()
        {
            _harmony = new Harmony(PluginGuid);

            // These hooks are only used to remember the active list. Import still works
            // without them by falling back to live UI lookup or the first saved list.
            PatchPostfix("TodoListUI", "OnSelectTodoListUI", "TodoListUI_OnSelectTodoListUI_Postfix");
            PatchPostfix("Bulbul.Mobile.TodoListUIModel", "Initialize", "MobileTodoListModel_Postfix");
            PatchPostfix("Bulbul.Mobile.TodoListUIModel", "SelectTodoList", "MobileTodoListModel_Postfix");
            PatchPostfix("Bulbul.SaveDataManager", "LoadTodoAllData", "SaveDataManager_LoadTodoAllData_Postfix");
        }

        private void PatchPostfix(string typeName, string methodName, string postfixName)
        {
            Type targetType = FindType(typeName);
            if (targetType == null)
            {
                Logger.LogWarning("Harmony target type not found: " + typeName);
                return;
            }

            MethodInfo target = AccessTools.Method(targetType, methodName);
            MethodInfo postfix = AccessTools.Method(typeof(BlackboardTodoImporterPlugin), postfixName);
            if (target == null || postfix == null)
            {
                Logger.LogWarning("Harmony patch skipped: " + typeName + "." + methodName);
                return;
            }

            _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Logger.LogInfo("Harmony patched " + typeName + "." + methodName);
        }

        private static void TodoListUI_OnSelectTodoListUI_Postfix(object __instance)
        {
            lock (Sync)
            {
                _lastTodoListUi = __instance;
                _lastTodoListId = ReadUInt64Member(__instance, "CurrentTodoListID");
            }

            if (_lastTodoListId != 0)
            {
                Log.LogInfo("Captured current desktop TodoListUI id: " + _lastTodoListId);
            }
        }

        private static void MobileTodoListModel_Postfix(object __instance)
        {
            lock (Sync)
            {
                _lastMobileTodoListModel = __instance;
                ulong id = ReadUInt64Member(__instance, "CurrentTodoListUuid");
                if (id != 0)
                {
                    _lastTodoListId = id;
                }
            }

            if (_lastTodoListId != 0)
            {
                Log.LogInfo("Captured current mobile TodoListUIModel id: " + _lastTodoListId);
            }
        }

        private static void SaveDataManager_LoadTodoAllData_Postfix()
        {
            if (_instance == null)
            {
                return;
            }

            _instance.Logger.LogInfo("SaveDataManager.LoadTodoAllData completed.");
            _instance.ImportOnceIfConfigured("LoadTodoAllData postfix");
        }

        private void ImportOnceIfConfigured(string reason)
        {
            if (!_importOnceWhenReady.Value)
            {
                return;
            }

            Logger.LogInfo("ImportOnceWhenReady is enabled by " + reason + "; importing once now.");
            _importOnceWhenReady.Value = false;
            Config.Save();
            ImportFromJson();
        }

        private void StartBrowserImportServer()
        {
            if (!_enableBrowserImportServer.Value)
            {
                Logger.LogInfo("Blackboard browser import server is disabled.");
                return;
            }

            try
            {
                _browserImportListener = new TcpListener(IPAddress.Loopback, _browserImportPort.Value);
                _browserImportListener.Start();
                _browserImportServerRunning = true;
                _browserImportThread = new Thread(BrowserImportServerLoop);
                _browserImportThread.IsBackground = true;
                _browserImportThread.Name = "Blackboard Todo Import Server";
                _browserImportThread.Start();
                Logger.LogInfo("Blackboard browser import server listening on http://127.0.0.1:" + _browserImportPort.Value + "/blackboard-import");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to start browser import server: " + ex);
            }
        }

        private void StopBrowserImportServer()
        {
            _browserImportServerRunning = false;
            Logger.LogInfo("Stopping Blackboard browser import server.");
            try
            {
                if (_browserImportListener != null)
                {
                    _browserImportListener.Stop();
                }
            }
            catch
            {
            }
        }

        private void BrowserImportServerLoop()
        {
            Log.LogInfo("Blackboard browser import server thread started.");
            while (_browserImportServerRunning)
            {
                try
                {
                    TcpClient client = _browserImportListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleBrowserImportClient(client); });
                }
                catch (SocketException ex)
                {
                    if (_browserImportServerRunning)
                    {
                        Log.LogWarning("Browser import listener socket error: " + ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    if (_browserImportServerRunning)
                    {
                        Log.LogWarning("Browser import listener error: " + ex);
                    }
                }
            }

            Log.LogInfo("Blackboard browser import server thread stopped.");
        }

        private void HandleBrowserImportClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    stream.WriteTimeout = 5000;

                    HttpRequest request = ReadHttpRequest(stream);
                    if (request == null)
                    {
                        WriteHttpResponse(stream, 400, "Bad Request", "{\"ok\":false,\"error\":\"bad request\"}");
                        return;
                    }

                    if (request.Method == "OPTIONS")
                    {
                        WriteHttpResponse(stream, 204, "No Content", string.Empty);
                        return;
                    }

                    if (request.Method == "GET" && request.Path == "/blackboard-import/ping")
                    {
                        WriteHttpResponse(stream, 200, "OK", "{\"ok\":true,\"name\":\"" + PluginName + "\"}");
                        return;
                    }

                    if (request.Method != "POST" || request.Path != "/blackboard-import")
                    {
                        WriteHttpResponse(stream, 404, "Not Found", "{\"ok\":false,\"error\":\"not found\"}");
                        return;
                    }

                    string body = request.Body ?? string.Empty;
                    if (body.Trim().Length == 0)
                    {
                        WriteHttpResponse(stream, 400, "Bad Request", "{\"ok\":false,\"error\":\"empty body\"}");
                        return;
                    }

                    File.WriteAllText(_jsonPath, body, Encoding.UTF8);
                    Log.LogInfo("Received Blackboard browser import payload (" + body.Length + " chars).");

                    if (_unityContext != null)
                    {
                        _unityContext.Post(delegate { ImportFromJsonText(body, "browser"); }, null);
                    }
                    else
                    {
                        ImportFromJsonText(body, "browser");
                    }

                    WriteHttpResponse(stream, 200, "OK", "{\"ok\":true}");
                }
                catch (Exception ex)
                {
                    Log.LogError("Browser import request failed: " + ex);
                    try
                    {
                        WriteHttpResponse(client.GetStream(), 500, "Internal Server Error", "{\"ok\":false,\"error\":\"server error\"}");
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static HttpRequest ReadHttpRequest(NetworkStream stream)
        {
            MemoryStream headerBytes = new MemoryStream();
            int matched = 0;
            int b;
            byte[] ending = new byte[] { 13, 10, 13, 10 };
            while ((b = stream.ReadByte()) != -1)
            {
                headerBytes.WriteByte((byte)b);
                if (b == ending[matched])
                {
                    matched++;
                    if (matched == ending.Length)
                    {
                        break;
                    }
                }
                else
                {
                    matched = b == ending[0] ? 1 : 0;
                }
            }

            string header = Encoding.ASCII.GetString(headerBytes.ToArray());
            if (header.Length == 0)
            {
                return null;
            }

            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2)
            {
                return null;
            }

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                }
            }

            string body = string.Empty;
            if (contentLength > 0)
            {
                byte[] bodyBytes = new byte[contentLength];
                int offset = 0;
                while (offset < contentLength)
                {
                    int read = stream.Read(bodyBytes, offset, contentLength - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }

                body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
            }

            return new HttpRequest { Method = first[0].ToUpperInvariant(), Path = first[1], Body = body };
        }

        private static void WriteHttpResponse(NetworkStream stream, int statusCode, string reason, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            string header =
                "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0)
            {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }
        }

        private void ImportFromJson()
        {
            Logger.LogInfo("Import requested.");

            if (!File.Exists(_jsonPath))
            {
                Logger.LogWarning("JSON file not found: " + _jsonPath);
                Logger.LogWarning("Create BepInEx/config/" + ConfigFileName + " and press F10 again.");
                return;
            }

            List<BlackboardTask> tasks;
            try
            {
                string json = File.ReadAllText(_jsonPath);
                tasks = JsonConvert.DeserializeObject<List<BlackboardTask>>(json);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to read or parse JSON: " + ex);
                return;
            }

            ImportTasks(tasks, "file");
        }

        private void ImportFromJsonText(string json, string source)
        {
            Logger.LogInfo("Import requested from " + source + ".");

            List<BlackboardTask> tasks;
            try
            {
                tasks = JsonConvert.DeserializeObject<List<BlackboardTask>>(json);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to parse pushed Blackboard JSON: " + ex);
                return;
            }

            ImportTasks(tasks, source);
        }

        private void ImportTasks(List<BlackboardTask> tasks, string source)
        {
            if (tasks == null || tasks.Count == 0)
            {
                Logger.LogWarning("Blackboard import from " + source + " contains no tasks.");
                return;
            }

            object todoAllData = TryGetTodoAllData(true);
            if (todoAllData == null)
            {
                Logger.LogError("TodoAllData is unavailable; import aborted.");
                return;
            }

            object targetList = ResolveTargetTodoList(todoAllData);
            if (targetList == null)
            {
                Logger.LogError("Could not locate or create a TodoListData; import aborted.");
                return;
            }

            int imported = 0;
            int skipped = 0;
            int failed = 0;

            for (int i = 0; i < tasks.Count; i++)
            {
                BlackboardTask task = tasks[i];
                ImportResult result = ImportOne(targetList, task);
                if (result == ImportResult.Imported)
                {
                    imported++;
                }
                else if (result == ImportResult.Skipped)
                {
                    skipped++;
                }
                else
                {
                    failed++;
                }
            }

            TryRefreshTodoUi(targetList);
            Logger.LogInfo("Import finished from " + source + ". Imported=" + imported + ", skipped=" + skipped + ", failed=" + failed);
        }

        private ImportResult ImportOne(object targetList, BlackboardTask task)
        {
            if (task == null)
            {
                Logger.LogWarning("Skipped null JSON entry.");
                return ImportResult.Failed;
            }

            string id = Clean(task.id);
            string title = Clean(task.title);
            string dueText = Clean(task.due);

            if (id.Length == 0 || title.Length == 0 || dueText.Length == 0)
            {
                Logger.LogWarning("Skipped malformed entry. id/title/due are required.");
                return ImportResult.Failed;
            }

            string prefix = "[BB:" + id + "]";
            if (TodoListContainsPrefix(targetList, prefix))
            {
                Logger.LogInfo("Skipped duplicate Blackboard task: " + prefix);
                return ImportResult.Skipped;
            }

            DateTime due;
            if (!DateTime.TryParse(dueText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out due))
            {
                Logger.LogWarning("Skipped task with invalid due date: " + prefix + " due=" + dueText);
                return ImportResult.Failed;
            }

            object todo = CreateTodoData(prefix + " " + title, due);
            if (todo == null)
            {
                Logger.LogError("Failed to create TodoData for " + prefix);
                return ImportResult.Failed;
            }

            MethodInfo addTodo = _todoListDataType.GetMethod("AddTodo", BindingFlags.Public | BindingFlags.Instance);
            if (addTodo == null)
            {
                Logger.LogError("TodoListData.AddTodo was not found.");
                return ImportResult.Failed;
            }

            bool added = false;
            try
            {
                // AddTodo is intentionally used instead of editing dictionaries directly:
                // the game method appends order data and calls SaveTodoList automatically.
                object result = addTodo.Invoke(targetList, new object[] { todo });
                added = result is bool && (bool)result;
            }
            catch (Exception ex)
            {
                Logger.LogError("TodoListData.AddTodo failed for " + prefix + ": " + Unwrap(ex));
                return ImportResult.Failed;
            }

            if (!added)
            {
                Logger.LogWarning("TodoListData.AddTodo returned false for " + prefix);
                return ImportResult.Failed;
            }

            Logger.LogInfo("Imported Blackboard task: " + prefix + " due=" + due.ToString("s"));
            return ImportResult.Imported;
        }

        private object CreateTodoData(string todoText, DateTime due)
        {
            if (_todoDataType == null)
            {
                return null;
            }

            object todo = Activator.CreateInstance(_todoDataType);
            SetFieldOrProperty(todo, "TodoText", todoText);

            if (_todoStateType != null)
            {
                object working = Enum.Parse(_todoStateType, "Working");
                SetFieldOrProperty(todo, "CurrentState", working);
            }

            MethodInfo setExpire = _todoDataType.GetMethod("SetExpire", BindingFlags.Public | BindingFlags.Instance);
            if (setExpire != null)
            {
                setExpire.Invoke(todo, new object[] { due });
            }

            return todo;
        }

        private object ResolveTargetTodoList(object todoAllData)
        {
            IDictionary dic = GetTodoListDictionary(todoAllData);
            if (dic == null)
            {
                Logger.LogError("TodoAllData.TodoListDic was not found.");
                return null;
            }

            // Prefer the list the player is actually looking at, then degrade to saved data.
            object list = ResolveCurrentListFromCapturedId(dic);
            if (list != null)
            {
                Logger.LogInfo("Using captured current TodoListData: " + ReadUInt64Member(list, "UniqueID"));
                return list;
            }

            list = ResolveCurrentListFromLiveDesktopUi(dic);
            if (list != null)
            {
                Logger.LogInfo("Using live TodoListUI current TodoListData: " + ReadUInt64Member(list, "UniqueID"));
                return list;
            }

            list = ResolveCurrentListFromMobileModel();
            if (list != null)
            {
                Logger.LogInfo("Using mobile TodoListUIModel current TodoListData: " + ReadUInt64Member(list, "UniqueID"));
                return list;
            }

            if (dic.Count > 0)
            {
                foreach (DictionaryEntry entry in dic)
                {
                    Logger.LogInfo("No current list found; using first TodoListData: " + entry.Key);
                    return entry.Value;
                }
            }

            return CreateTodoList(todoAllData);
        }

        private object ResolveCurrentListFromCapturedId(IDictionary dic)
        {
            ulong id;
            lock (Sync)
            {
                id = _lastTodoListId;
            }

            if (id == 0)
            {
                return null;
            }

            return dic.Contains(id) ? dic[id] : null;
        }

        private object ResolveCurrentListFromLiveDesktopUi(IDictionary dic)
        {
            if (_todoListUiType == null)
            {
                return null;
            }

            try
            {
                UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(_todoListUiType);
                for (int i = 0; i < objects.Length; i++)
                {
                    object ui = objects[i];
                    ulong id = ReadUInt64Member(ui, "CurrentTodoListID");
                    if (id != 0 && dic.Contains(id))
                    {
                        lock (Sync)
                        {
                            _lastTodoListUi = ui;
                            _lastTodoListId = id;
                        }

                        return dic[id];
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Live TodoListUI lookup failed: " + Unwrap(ex));
            }

            return null;
        }

        private object ResolveCurrentListFromMobileModel()
        {
            object model;
            lock (Sync)
            {
                model = _lastMobileTodoListModel;
            }

            if (model == null)
            {
                return null;
            }

            return GetFieldOrProperty(model, "CurrentTodoList");
        }

        private object CreateTodoList(object todoAllData)
        {
            if (_todoListDataType == null)
            {
                return null;
            }

            object list = Activator.CreateInstance(_todoListDataType);
            SetFieldOrProperty(list, "TitleText", "Blackboard");

            MethodInfo addTodoList = _todoAllDataType.GetMethod("AddTodoList", BindingFlags.Public | BindingFlags.Instance);
            if (addTodoList == null)
            {
                Logger.LogError("TodoAllData.AddTodoList was not found.");
                return null;
            }

            try
            {
                object result = addTodoList.Invoke(todoAllData, new object[] { list });
                if (result is bool && (bool)result)
                {
                    Logger.LogInfo("Created new TodoListData for Blackboard imports: " + ReadUInt64Member(list, "UniqueID"));
                    return list;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("TodoAllData.AddTodoList failed: " + Unwrap(ex));
            }

            return null;
        }

        private object TryGetTodoAllData(bool loadIfNeeded)
        {
            if (_saveDataManagerType == null)
            {
                return null;
            }

            object manager = null;
            try
            {
                PropertyInfo instanceProperty = _saveDataManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                manager = instanceProperty != null ? instanceProperty.GetValue(null, null) : null;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("SaveDataManager.Instance lookup failed: " + Unwrap(ex));
            }

            if (manager == null)
            {
                return null;
            }

            object todoAllData = GetFieldOrProperty(manager, "TodoAllData");
            if (todoAllData == null && loadIfNeeded)
            {
                // SaveDataManager exposes the game's own loader; use it before creating data.
                MethodInfo load = _saveDataManagerType.GetMethod("LoadTodoAllData", BindingFlags.Public | BindingFlags.Instance);
                if (load != null)
                {
                    try
                    {
                        load.Invoke(manager, null);
                        todoAllData = GetFieldOrProperty(manager, "TodoAllData");
                        Logger.LogInfo("Called SaveDataManager.LoadTodoAllData().");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("LoadTodoAllData failed: " + Unwrap(ex));
                    }
                }
            }

            if (todoAllData == null && loadIfNeeded && _todoAllDataType != null)
            {
                todoAllData = Activator.CreateInstance(_todoAllDataType);
                if (SetFieldOrProperty(manager, "TodoAllData", todoAllData))
                {
                    Logger.LogInfo("Created missing TodoAllData instance.");
                }
                else
                {
                    todoAllData = null;
                }
            }

            return todoAllData;
        }

        private static IDictionary GetTodoListDictionary(object todoAllData)
        {
            return GetFieldOrProperty(todoAllData, "TodoListDic") as IDictionary;
        }

        private bool TodoListContainsPrefix(object list, string prefix)
        {
            IDictionary todoDic = GetFieldOrProperty(list, "TodoDic") as IDictionary;
            if (todoDic == null)
            {
                return false;
            }

            foreach (DictionaryEntry entry in todoDic)
            {
                object todo = entry.Value;
                object textObject = GetFieldOrProperty(todo, "TodoText");
                string text = textObject as string;
                if (!string.IsNullOrEmpty(text) && text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void TryRefreshTodoUi(object targetList)
        {
            object ui;
            lock (Sync)
            {
                ui = _lastTodoListUi;
            }

            if (ui == null)
            {
                return;
            }

            ulong uiListId = ReadUInt64Member(ui, "CurrentTodoListID");
            ulong targetListId = ReadUInt64Member(targetList, "UniqueID");
            if (uiListId == 0 || targetListId == 0 || uiListId != targetListId)
            {
                return;
            }

            MethodInfo update = ui.GetType().GetMethod("UpdateUI", BindingFlags.Public | BindingFlags.Instance);
            if (update == null)
            {
                return;
            }

            try
            {
                update.Invoke(ui, null);
                Logger.LogInfo("Requested TodoListUI.UpdateUI() after import.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("TodoListUI.UpdateUI failed; reopen the Todo panel if new items are not visible. " + Unwrap(ex));
            }
        }

        private static Type FindType(string fullName)
        {
            Type type = AccessTools.TypeByName(fullName);
            if (type != null)
            {
                return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static object GetFieldOrProperty(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            Type type = instance.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field != null)
            {
                return field.GetValue(instance);
            }

            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (property != null && property.CanRead)
            {
                return property.GetValue(instance, null);
            }

            return null;
        }

        private static bool SetFieldOrProperty(object instance, string name, object value)
        {
            if (instance == null)
            {
                return false;
            }

            Type type = instance.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field != null)
            {
                field.SetValue(instance, value);
                return true;
            }

            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, value, null);
                return true;
            }

            return false;
        }

        private static ulong ReadUInt64Member(object instance, string name)
        {
            object value = GetFieldOrProperty(instance, name);
            if (value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static void LogTypeStatus(string label, Type type)
        {
            if (Log == null)
            {
                return;
            }

            if (type == null)
            {
                Log.LogWarning("Game type missing: " + label);
            }
            else
            {
                Log.LogInfo("Game type found: " + label + " -> " + type.FullName);
            }
        }

        private static string Clean(string text)
        {
            return text == null ? string.Empty : text.Trim();
        }

        private static string Unwrap(Exception ex)
        {
            TargetInvocationException target = ex as TargetInvocationException;
            if (target != null && target.InnerException != null)
            {
                return target.InnerException.ToString();
            }

            return ex.ToString();
        }

        private enum ImportResult
        {
            Imported,
            Skipped,
            Failed
        }

        private sealed class HttpRequest
        {
            public string Method;
            public string Path;
            public string Body;
        }

#pragma warning disable 0649
        private sealed class BlackboardTask
        {
            public string id;
            public string title;
            public string due;
        }
#pragma warning restore 0649
    }
}
