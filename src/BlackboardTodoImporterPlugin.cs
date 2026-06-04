using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace BlackboardTodoImporter
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BlackboardTodoImporterPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.local.chillwithyou.blackboardtodoimporter";
        private const string PluginName = "Blackboard Todo Importer";
        private const string PluginVersion = "1.1.0";

        private static readonly string[] CurrentListMemberNames =
        {
            "CurrentTodoListData",
            "currentTodoListData",
            "TodoListData",
            "todoListData",
            "SelectedTodoListData",
            "selectedTodoListData",
            "ActiveTodoListData",
            "activeTodoListData"
        };

        private static readonly string[] CurrentListIdMemberNames =
        {
            "CurrentTodoListID",
            "currentTodoListID",
            "CurrentTodoListId",
            "currentTodoListId",
            "CurrentTodoListUuid",
            "currentTodoListUuid",
            "SelectedTodoListID",
            "selectedTodoListID",
            "SelectedTodoListId",
            "selectedTodoListId"
        };

        private static readonly string[] KnownBlackboardImportIds =
        {
            "aefis-survey-2026-06-04t23-59-00",
            "assignment-10-due-06-07-11-59-pm-2026-06-07t23-59-00",
            "assignment-9-due-06-04-11-59-pm-2026-06-04t23-59-00",
            "deliverable-3-final-reflection-2026-06-04t23-59-00",
            "eval3-2026-06-04t23-59-00",
            "final-presentations-feedback-2026-06-04t23-59-00",
            "homework-05-2026-06-04t23-59-00",
            "homework-5-2026-06-04t23-59-00",
            "last-quiz-2026-06-04t23-59-00",
            "letter-of-advice-to-yourself-2026-06-04t23-59-00",
            "project-2-adaptation-due-sunday-june-7th-by-11-59-pm-2026-06-07t23-59-00",
            "reflection-9-2026-06-04t23-59-00",
            "review3-2026-06-04t23-59-00",
            "week-10-discussion-board-note-earlier-due-date-for-this-week-2026-06-04t18-00-00",
            "week-10-reading-quiz-2026-06-04t23-59-00",
            "week-9-discussion-board-2026-06-04t23-59-00",
            "assignment-9",
            "week-10-discussion-board-note-earlier",
            "assignment-10",
            "project-2-adaptation",
            "aefis-survey",
            "final-presentations-feedback",
            "last-quiz",
            "deliverable-3-final-reflection"
        };

        private static ManualLogSource Log;

        private ConfigEntry<KeyboardShortcut> importHotkey;
        private ConfigEntry<int> httpPort;
        private ConfigEntry<bool> autoImportOnStart;
        private readonly Queue<string> pendingHttpImports = new Queue<string>();
        private readonly object pendingHttpImportsLock = new object();
        private TcpListener httpListener;
        private Thread httpThread;
        private volatile bool httpServerRunning;
        private int remainingAutoImportAttempts;
        private float nextAutoImportAttemptTime;
        private BlackboardTodoImporterRunner runner;
        private bool importInProgress;
        private Harmony harmony;
        private int remainingRuntimeImportRequests;
        private volatile int pendingUiRefreshRequests;
        private static BlackboardTodoImporterPlugin Instance;
        private readonly object importLock = new object();

        private string TasksJsonPath
        {
            get { return Path.Combine(Paths.ConfigPath, "blackboard_tasks.json"); }
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            DontDestroyOnLoad(gameObject);
            importHotkey = Config.Bind(
                "Import",
                "Hotkey",
                new KeyboardShortcut(KeyCode.F10),
                "手动导入 Blackboard deadlines 的热键。");
            httpPort = Config.Bind(
                "Import",
                "HttpPort",
                29472,
                "本地书签脚本发送 Blackboard tasks 的 HTTP 端口。");
            autoImportOnStart = Config.Bind(
                "Import",
                "AutoImportOnStart",
                true,
                "启动后自动尝试清理旧 Blackboard Todo 并导入 JSON。");

            Log.LogInfo(string.Format("{0} {1} loaded.", PluginName, PluginVersion));
            Log.LogInfo("Blackboard JSON path: " + TasksJsonPath);
            StartHttpServer();
            remainingRuntimeImportRequests = 8;
            PatchTodoRuntimeHooks();
            EnsureRunner();
            StartBackgroundImportWorker("startup");
            if (autoImportOnStart.Value)
            {
                remainingAutoImportAttempts = 24;
                nextAutoImportAttemptTime = 3f;
                StartCoroutine(AutoImportCoroutine());
            }
        }

        private void Update()
        {
            Tick();
        }

        private void OnGUI()
        {
            if (Event.current != null &&
                Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.F10)
            {
                Log.LogInfo("Manual Blackboard import requested from OnGUI F10.");
                ImportFromJson();
            }
        }

        internal void Tick()
        {
            ProcessPendingHttpImports();
            ProcessAutoImportAttempts();
            ProcessPendingUiRefresh();

            if (importHotkey.Value.IsDown() || Input.GetKeyDown(KeyCode.F10))
            {
                Log.LogInfo("Manual Blackboard import requested.");
                ImportFromJson();
            }
        }

        private void OnApplicationQuit()
        {
            StopHttpServer();
            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }
        }

        private void EnsureRunner()
        {
            if (runner != null)
            {
                return;
            }

            var runnerObject = new GameObject("Blackboard Todo Importer Runner");
            DontDestroyOnLoad(runnerObject);
            runnerObject.hideFlags = HideFlags.HideAndDontSave;
            runner = runnerObject.AddComponent<BlackboardTodoImporterRunner>();
            runner.Owner = this;
            Log.LogInfo("Blackboard Todo Importer runner created.");
        }

        internal IEnumerator AutoImportCoroutine()
        {
            Log.LogInfo("Blackboard auto import coroutine started.");
            yield return new WaitForSecondsRealtime(3f);

            while (remainingAutoImportAttempts > 0)
            {
                Tick();
                yield return new WaitForSecondsRealtime(5f);
            }
        }

        internal void LogRunnerStarted()
        {
            Log.LogInfo("Blackboard Todo Importer runner started.");
        }

        internal void LogRunnerManualImport()
        {
            Log.LogInfo("Manual Blackboard import requested from runner OnGUI F10.");
        }

        private void PatchTodoRuntimeHooks()
        {
            try
            {
                harmony = new Harmony(PluginGuid);
                PatchNoArgPostfix("Bulbul.FacilityTodo", "Setup");
                PatchNoArgPostfix("Bulbul.FacilityTodo", "Activate");
                PatchNoArgPostfix("Bulbul.FacilityTodo", "UpdateFacility");
                PatchNoArgPostfix("Bulbul.UIManagerForPC", "OnClickButtonFacilityTodo");
                PatchNoArgPostfix("Bulbul.Mobile.UIManagerForMobile", "OnClickButtonFacilityTodo");
                PatchNoArgPostfix("Bulbul.Mobile.TodoListUIModel", "Initialize");
                PatchNoArgPostfix("Bulbul.Mobile.FacilityTodoListContentsUI", "Setup");
                PatchNoArgPostfix("Bulbul.SaveDataManager", "LoadTodoAllData");
                PatchNoArgPostfix("UnityEngine.UI.Button", "Press");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not patch Todo runtime hooks: " + ex);
            }
        }

        private void PatchNoArgPostfix(string typeName, string methodName)
        {
            var type = FindType(typeName);
            if (type == null)
            {
                Log.LogWarning("Todo runtime hook type not found: " + typeName);
                return;
            }

            var method = type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            if (method == null)
            {
                Log.LogWarning("Todo runtime hook method not found: " + typeName + "." + methodName);
                return;
            }

            var postfix = new HarmonyMethod(typeof(BlackboardTodoImporterPlugin).GetMethod(
                "OnTodoRuntimeReady",
                BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(method, postfix: postfix);
            Log.LogInfo("Patched Todo runtime hook: " + typeName + "." + methodName);
        }

        private static void OnTodoRuntimeReady()
        {
            var instance = Instance;
            if (instance != null)
            {
                instance.RequestRuntimeImport("Todo runtime hook");
            }
        }

        private void RequestRuntimeImport(string reason)
        {
            if (remainingRuntimeImportRequests <= 0)
            {
                return;
            }

            remainingRuntimeImportRequests--;
            Log.LogInfo(reason + " requested Blackboard cleanup/import. RemainingRuntimeRequests=" + remainingRuntimeImportRequests);
            ImportFromJson();
        }

        private void ProcessAutoImportAttempts()
        {
            if (remainingAutoImportAttempts <= 0)
            {
                return;
            }

            if (Time.unscaledTime < nextAutoImportAttemptTime)
            {
                return;
            }

            remainingAutoImportAttempts--;
            nextAutoImportAttemptTime = Time.unscaledTime + 5f;
            Log.LogInfo("Auto Blackboard cleanup/import attempt. Remaining=" + remainingAutoImportAttempts);
            if (ImportFromJson())
            {
                remainingAutoImportAttempts = 0;
            }
        }

        private void ProcessPendingHttpImports()
        {
            string json = null;
            lock (pendingHttpImportsLock)
            {
                if (pendingHttpImports.Count > 0)
                {
                    json = pendingHttpImports.Dequeue();
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                AcceptHttpImport(json, "HTTP queue");
                if (ImportFromJsonDataOnly("HTTP queue immediate"))
                {
                    pendingUiRefreshRequests = Math.Max(pendingUiRefreshRequests, 16);
                    return;
                }

                StartBackgroundImportWorker("HTTP queue retry");
            }
            catch (Exception ex)
            {
                Log.LogError("Could not process HTTP Blackboard import: " + ex);
            }
        }

        private void ProcessPendingUiRefresh()
        {
            if (pendingUiRefreshRequests <= 0)
            {
                return;
            }

            pendingUiRefreshRequests--;
            try
            {
                RefreshTodoUi();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Deferred Todo UI refresh failed: " + ex.Message);
            }
        }

        private int AcceptHttpImport(string json, string source)
        {
            File.WriteAllText(TasksJsonPath, json, Encoding.UTF8);
            var taskCount = 0;
            try
            {
                var tasks = JsonConvert.DeserializeObject<List<BlackboardTask>>(json);
                taskCount = tasks != null ? tasks.Count(t => t != null) : 0;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Received Blackboard HTTP payload but could not count tasks: " + ex.Message);
            }

            Log.LogInfo("Received Blackboard tasks through local HTTP bookmarklet from " + source + ". Count=" + taskCount);
            return taskCount;
        }

        internal bool ImportFromJson()
        {
            lock (importLock)
            {
                if (importInProgress)
                {
                    Log.LogInfo("Blackboard import already in progress; skipping overlapping request.");
                    return false;
                }

                importInProgress = true;
                try
                {
                    return ImportFromJsonCore(true);
                }
                finally
                {
                    importInProgress = false;
                }
            }
        }

        private bool ImportFromJsonDataOnly(string reason)
        {
            lock (importLock)
            {
                if (importInProgress)
                {
                    return false;
                }

                importInProgress = true;
                try
                {
                    Log.LogInfo("Background data-only Blackboard cleanup/import requested: " + reason);
                    return ImportFromJsonCore(false);
                }
                finally
                {
                    importInProgress = false;
                }
            }
        }

        private bool ImportFromJsonCore(bool allowUnityObjectSearch)
        {
            try
            {
                var tasks = ReadTasks();
                Log.LogInfo("Read Blackboard task count: " + tasks.Count);
                if (tasks.Count == 0)
                {
                    Log.LogInfo("No Blackboard tasks to import.");
                    return true;
                }

                var todoDataType = FindType("Bulbul.TodoData");
                var todoListDataType = FindType("Bulbul.TodoListData");
                var todoAllDataType = FindType("Bulbul.TodoAllData");

                if (todoDataType == null || todoListDataType == null)
                {
                    Log.LogError("Could not find Bulbul.TodoData or Bulbul.TodoListData. Is Assembly-CSharp loaded?");
                    return false;
                }

                var todoAllData = FindTodoAllData(todoAllDataType, allowUnityObjectSearch);
                var cleanupCount = CleanupAllTodoLists(todoListDataType, todoAllData);
                var todoListData = FindCurrentTodoListData(todoListDataType, todoAllData, allowUnityObjectSearch);

                if (todoListData == null && todoAllData != null)
                {
                    todoListData = GetFirstTodoListData(todoAllData, todoListDataType);
                }

                if (todoListData == null && todoAllData != null)
                {
                    todoListData = CreateTodoListData(todoAllData, todoListDataType);
                }

                if (todoListData == null)
                {
                    Log.LogError("Could not locate or create a TodoListData instance. Import aborted.");
                    return false;
                }

                cleanupCount += CleanupSyncedBlackboardTodos(todoListDataType, todoAllData, todoListData, tasks);
                cleanupCount += CleanupSyncedBlackboardTodosByTitle(todoListDataType, todoAllData, todoListData, tasks);
                ImportTasks(tasks, todoDataType, todoListDataType, todoListData);
                PersistTodoChanges(todoAllData, todoListData, allowUnityObjectSearch);
                Log.LogInfo("Blackboard cleanup/import saved. LegacyRemoved=" + cleanupCount);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError("Import failed: " + ex);
                return false;
            }
        }

        private void StartHttpServer()
        {
            StopHttpServer();

            try
            {
                httpServerRunning = true;
                httpListener = new TcpListener(IPAddress.Loopback, httpPort.Value);
                httpListener.Start();
                httpThread = new Thread(HttpServerLoop);
                httpThread.IsBackground = true;
                httpThread.Name = "BlackboardTodoImporterHttp";
                httpThread.Start();
                Log.LogInfo("Blackboard bookmarklet HTTP server listening on http://127.0.0.1:" + httpPort.Value + "/blackboard-import");
            }
            catch (Exception ex)
            {
                httpServerRunning = false;
                Log.LogError("Could not start Blackboard bookmarklet HTTP server: " + ex);
            }
        }

        private void StopHttpServer()
        {
            httpServerRunning = false;

            try
            {
                if (httpListener != null)
                {
                    httpListener.Stop();
                }
            }
            catch
            {
            }

            httpListener = null;
        }

        private void StartBackgroundImportWorker(string reason)
        {
            try
            {
                var thread = new Thread(() =>
                {
                    for (var attempt = 1; attempt <= 20; attempt++)
                    {
                        Thread.Sleep(attempt == 1 ? 5000 : 3000);
                        try
                        {
                                if (ImportFromJsonDataOnly(reason + " attempt " + attempt))
                                {
                                    pendingUiRefreshRequests = Math.Max(pendingUiRefreshRequests, 8);
                                    return;
                                }
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning("Background data-only Blackboard import attempt failed: " + ex.Message);
                        }
                    }
                });
                thread.IsBackground = true;
                thread.Name = "BlackboardTodoImporterDataOnly";
                thread.Start();
                Log.LogInfo("Started background data-only Blackboard import worker: " + reason);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not start background data-only Blackboard import worker: " + ex.Message);
            }
        }

        private void HttpServerLoop()
        {
            while (httpServerRunning)
            {
                TcpClient client = null;
                try
                {
                    client = httpListener.AcceptTcpClient();
                    HandleHttpClient(client);
                }
                catch
                {
                    if (httpServerRunning)
                    {
                        Thread.Sleep(250);
                    }
                }
                finally
                {
                    if (client != null)
                    {
                        try
                        {
                            client.Close();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private void HandleHttpClient(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            var stream = client.GetStream();
            var headerBytes = new List<byte>();
            var buffer = new byte[4096];
            var contentLength = 0;
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                for (var i = 0; i < read; i++)
                {
                    headerBytes.Add(buffer[i]);
                }

                headerEnd = IndexOfHeaderEnd(headerBytes);
            }

            var headerText = Encoding.UTF8.GetString(headerBytes.ToArray(), 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                WriteHttpResponse(stream, 400, "Bad Request");
                return;
            }

            var requestLine = lines[0];
            var isOptions = requestLine.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase);
            var isPost = requestLine.StartsWith("POST ", StringComparison.OrdinalIgnoreCase);
            var isImportPath = requestLine.IndexOf(" /blackboard-import", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isOptions)
            {
                WriteHttpResponse(stream, 204, "");
                return;
            }

            if (!isPost || !isImportPath)
            {
                WriteHttpResponse(stream, 404, "Not Found");
                return;
            }

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, out contentLength);
                }
            }

            if (contentLength <= 0 || contentLength > 1024 * 1024)
            {
                WriteHttpResponse(stream, 400, "Invalid Content-Length");
                return;
            }

            var bodyStart = headerEnd + 4;
            var allBytes = headerBytes.ToArray();
            var body = new List<byte>();
            for (var i = bodyStart; i < allBytes.Length; i++)
            {
                body.Add(allBytes[i]);
            }

            while (body.Count < contentLength)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, contentLength - body.Count));
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    body.Add(buffer[i]);
                }
            }

            var json = Encoding.UTF8.GetString(body.ToArray(), 0, Math.Min(body.Count, contentLength));
            if (!json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                WriteHttpResponse(stream, 400, "Expected JSON array");
                return;
            }

            try
            {
                var taskCount = AcceptHttpImport(json, "HTTP POST");
                if (ImportFromJsonDataOnly("HTTP POST immediate"))
                {
                    pendingUiRefreshRequests = Math.Max(pendingUiRefreshRequests, 16);
                    WriteHttpResponse(stream, 200, "Imported Blackboard tasks: " + taskCount);
                    return;
                }

                StartBackgroundImportWorker("HTTP POST retry");
                WriteHttpResponse(stream, 200, "Accepted Blackboard import; retry queued");
            }
            catch (Exception ex)
            {
                Log.LogError("Could not accept HTTP Blackboard import: " + ex);
                WriteHttpResponse(stream, 500, "Failed to accept Blackboard import");
            }
        }

        private static int IndexOfHeaderEnd(List<byte> bytes)
        {
            for (var i = 3; i < bytes.Count; i++)
            {
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static void WriteHttpResponse(NetworkStream stream, int statusCode, string body)
        {
            var statusText =
                statusCode == 200 ? "OK" :
                statusCode == 204 ? "No Content" :
                statusCode == 400 ? "Bad Request" :
                statusCode == 404 ? "Not Found" :
                "OK";

            var bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
            var header =
                "HTTP/1.1 " + statusCode + " " + statusText + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n\r\n";

            var headerBytes = Encoding.UTF8.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0)
            {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }
        }

        private List<BlackboardTask> ReadTasks()
        {
            if (!File.Exists(TasksJsonPath))
            {
                Log.LogWarning("JSON file not found: " + TasksJsonPath);
                return new List<BlackboardTask>();
            }

            var json = File.ReadAllText(TasksJsonPath).Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                Log.LogWarning("JSON file is empty.");
                return new List<BlackboardTask>();
            }

            if (!json.StartsWith("[", StringComparison.Ordinal))
            {
                Log.LogWarning("JSON root must be an array.");
                return new List<BlackboardTask>();
            }

            var tasks = JsonConvert.DeserializeObject<List<BlackboardTask>>(json);
            return tasks != null
                ? tasks.Where(t => t != null).ToList()
                : new List<BlackboardTask>();
        }

        private void ImportTasks(
            IList<BlackboardTask> tasks,
            Type todoDataType,
            Type todoListDataType,
            object todoListData)
        {
            var todoDic = GetMemberValue(todoListData, "TodoDic") as IDictionary;
            CleanupExistingBlackboardTodos(todoListDataType, todoListData, todoDic);

            todoDic = GetMemberValue(todoListData, "TodoDic") as IDictionary;
            var existingIds = GetExistingTodoIds(todoDic);

            var imported = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.id) || string.IsNullOrWhiteSpace(task.title))
                {
                    Log.LogWarning("Skipped task with missing id or title.");
                    skipped++;
                    continue;
                }

                DateTime due;
                if (!DateTime.TryParse(
                    task.due,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out due))
                {
                    Log.LogWarning(string.Format("Skipped Blackboard task '{0}' because due datetime is invalid: {1}", task.id, task.due));
                    skipped++;
                    continue;
                }

                var id = task.id.Trim();
                var prefix = "[BB:" + id + "]";
                var title = task.title.Trim();
                var uniqueId = GenerateStableUniqueId("blackboard:" + id);
                var existingTodo = FindExistingBlackboardTodo(todoDic, id, uniqueId);
                if (existingTodo != null)
                {
                    StripLegacyPrefixIfNeeded(todoListDataType, todoListData, existingTodo, prefix, title);
                    Log.LogInfo("Skipped duplicate Blackboard task: " + id);
                    skipped++;
                    continue;
                }

                try
                {
                    var todo = Activator.CreateInstance(todoDataType);
                    if (existingIds.Contains(uniqueId))
                    {
                        Log.LogWarning("Skipped Blackboard task because generated UniqueID already exists: " + id);
                        skipped++;
                        continue;
                    }

                    SetMemberValue(todo, "UniqueID", uniqueId);
                    SetMemberValue(todo, "TodoText", title);

                    // Keep CurrentState untouched. A fresh TodoData should default to Working in the game.
                    InvokeMethod(todo, "SetExpire", new object[] { due });

                    var added = InvokeAddTodo(todoListDataType, todoListData, todo);
                    if (!added)
                    {
                        Log.LogWarning("TodoListData.AddTodo returned false for Blackboard task: " + id);
                        failed++;
                        continue;
                    }

                    existingIds.Add(uniqueId);
                    imported++;
                    Log.LogInfo(string.Format("Imported Blackboard task: {0} due {1:yyyy-MM-dd HH:mm}", title, due));
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.LogError(string.Format("Failed to import Blackboard task '{0}': {1}", id, ex));
                }
            }

            Log.LogInfo(string.Format("Blackboard import finished. Imported={0}, Skipped={1}, Failed={2}", imported, skipped, failed));
        }

        private object FindTodoAllData(Type todoAllDataType, bool allowUnityObjectSearch)
        {
            if (todoAllDataType == null)
            {
                Log.LogWarning("Bulbul.TodoAllData type was not found.");
                return null;
            }

            var saveDataManager = FindSaveDataManagerInstance();
            if (saveDataManager != null)
            {
                var direct = FindAssignableMemberValue(saveDataManager, todoAllDataType);
                if (direct != null)
                {
                    Log.LogInfo("Found TodoAllData on SaveDataManager.Instance.");
                    return direct;
                }

                var loaded = InvokeNoArgMethodReturning(saveDataManager, "LoadTodoAllData", todoAllDataType);
                if (loaded != null)
                {
                    Log.LogInfo("Found TodoAllData through SaveDataManager.Instance.LoadTodoAllData().");
                    return loaded;
                }
            }

            foreach (var type in GetAllTypesSafe())
            {
                var value = FindStaticAssignableMemberValue(type, todoAllDataType);
                if (value != null)
                {
                    Log.LogInfo("Found TodoAllData through static member on " + type.FullName + ".");
                    return value;
                }
            }

            if (allowUnityObjectSearch)
            {
                foreach (var behaviour in FindRuntimeBehaviours())
                {
                    var value = FindAssignableMemberValue(behaviour, todoAllDataType);
                    if (value != null)
                    {
                        Log.LogInfo("Found TodoAllData through runtime object " + behaviour.GetType().FullName + ".");
                        return value;
                    }
                }
            }

            Log.LogWarning("TodoAllData was not found.");
            return null;
        }

        private int CleanupExistingBlackboardTodos(Type todoListDataType, object todoListData, IDictionary todoDic)
        {
            if (todoDic == null)
            {
                return 0;
            }

            var removedCount = 0;
            var todosToRemove = new List<object>();

            foreach (var todo in CopyDictionaryValues(todoDic))
            {
                var currentText = GetMemberValue(todo, "TodoText") as string;
                string prefix;
                string cleaned;
                if (!TryStripLegacyPrefix(currentText, out prefix, out cleaned))
                {
                    continue;
                }

                todosToRemove.Add(todo);
            }

            foreach (var todo in todosToRemove)
            {
                var todoId = GetMemberValue(todo, "UniqueID");
                if (InvokeRemoveTodo(todoListDataType, todoListData, todo))
                {
                    DirectRemoveTodoIfStillPresent(todoListData, todoDic, todo, todoId);
                    removedCount++;
                }
                else if (DirectRemoveTodoIfStillPresent(todoListData, todoDic, todo, todoId))
                {
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                Log.LogInfo("Removed legacy Blackboard Todo item(s): " + removedCount);
            }

            return removedCount;
        }

        private int CleanupAllTodoLists(Type todoListDataType, object todoAllData)
        {
            if (todoAllData == null)
            {
                return 0;
            }

            var todoListDic = GetMemberValue(todoAllData, "TodoListDic") as IDictionary;
            if (todoListDic == null || todoListDic.Count == 0)
            {
                return 0;
            }

            var listCount = 0;
            var totalRemoved = 0;
            foreach (DictionaryEntry entry in todoListDic)
            {
                var todoListData = entry.Value;
                if (!IsInstanceOf(todoListData, todoListDataType))
                {
                    continue;
                }

                listCount++;
                var todoDic = GetMemberValue(todoListData, "TodoDic") as IDictionary;
                totalRemoved += CleanupExistingBlackboardTodos(todoListDataType, todoListData, todoDic);
            }

            Log.LogInfo(string.Format("Scanned Todo lists for legacy Blackboard cleanup: Lists={0}, Removed={1}", listCount, totalRemoved));
            return totalRemoved;
        }

        private int CleanupSyncedBlackboardTodos(
            Type todoListDataType,
            object todoAllData,
            object fallbackTodoListData,
            List<BlackboardTask> tasks)
        {
            var blackboardIds = BuildBlackboardImportUniqueIds(tasks);
            var totalRemoved = 0;

            if (todoAllData != null)
            {
                var todoListDic = GetMemberValue(todoAllData, "TodoListDic") as IDictionary;
                if (todoListDic != null)
                {
                    foreach (DictionaryEntry entry in todoListDic)
                    {
                        var todoListData = entry.Value;
                        if (IsInstanceOf(todoListData, todoListDataType))
                        {
                            totalRemoved += CleanupTodoListByUniqueIds(todoListDataType, todoListData, blackboardIds);
                        }
                    }
                }
            }

            if (totalRemoved == 0 && fallbackTodoListData != null)
            {
                totalRemoved += CleanupTodoListByUniqueIds(todoListDataType, fallbackTodoListData, blackboardIds);
            }

            if (totalRemoved > 0)
            {
                Log.LogInfo("Removed synced Blackboard Todo item(s) before reimport: " + totalRemoved);
            }

            return totalRemoved;
        }

        private static HashSet<ulong> BuildBlackboardImportUniqueIds(List<BlackboardTask> tasks)
        {
            var ids = new HashSet<ulong>();
            foreach (var legacyId in KnownBlackboardImportIds)
            {
                if (!string.IsNullOrWhiteSpace(legacyId))
                {
                    ids.Add(GenerateStableUniqueId("blackboard:" + legacyId.Trim()));
                }
            }

            if (tasks != null)
            {
                foreach (var task in tasks)
                {
                    if (task != null && !string.IsNullOrWhiteSpace(task.id))
                    {
                        ids.Add(GenerateStableUniqueId("blackboard:" + task.id.Trim()));
                    }
                }
            }

            return ids;
        }

        private int CleanupTodoListByUniqueIds(Type todoListDataType, object todoListData, HashSet<ulong> blackboardIds)
        {
            if (todoListData == null || blackboardIds == null || blackboardIds.Count == 0)
            {
                return 0;
            }

            var todoDic = GetMemberValue(todoListData, "TodoDic") as IDictionary;
            if (todoDic == null)
            {
                return 0;
            }

            var todosToRemove = new List<object>();
            foreach (var todo in CopyDictionaryValues(todoDic))
            {
                ulong id;
                var todoId = GetMemberValue(todo, "UniqueID");
                if (TryConvertToUInt64(todoId, out id) && blackboardIds.Contains(id))
                {
                    todosToRemove.Add(todo);
                }
            }

            var removedCount = 0;
            foreach (var todo in todosToRemove)
            {
                var todoId = GetMemberValue(todo, "UniqueID");
                if (InvokeRemoveTodo(todoListDataType, todoListData, todo))
                {
                    DirectRemoveTodoIfStillPresent(todoListData, todoDic, todo, todoId);
                    removedCount++;
                }
                else if (DirectRemoveTodoIfStillPresent(todoListData, todoDic, todo, todoId))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        private int CleanupSyncedBlackboardTodosByTitle(
            Type todoListDataType,
            object todoAllData,
            object fallbackTodoListData,
            List<BlackboardTask> tasks)
        {
            var titleKeys = BuildBlackboardImportTitleKeys(tasks);
            if (titleKeys.Count == 0)
            {
                return 0;
            }

            var totalRemoved = 0;
            if (todoAllData != null)
            {
                var todoListDic = GetMemberValue(todoAllData, "TodoListDic") as IDictionary;
                if (todoListDic != null)
                {
                    foreach (DictionaryEntry entry in todoListDic)
                    {
                        var todoListData = entry.Value;
                        if (IsInstanceOf(todoListData, todoListDataType))
                        {
                            totalRemoved += CleanupTodoListByTitleKeys(todoListDataType, todoListData, titleKeys);
                        }
                    }
                }
            }

            if (totalRemoved == 0 && fallbackTodoListData != null)
            {
                totalRemoved += CleanupTodoListByTitleKeys(todoListDataType, fallbackTodoListData, titleKeys);
            }

            if (totalRemoved > 0)
            {
                Log.LogInfo("Removed Blackboard Todo item(s) by normalized title before reimport: " + totalRemoved);
            }

            return totalRemoved;
        }

        private static HashSet<string> BuildBlackboardImportTitleKeys(List<BlackboardTask> tasks)
        {
            var keys = new HashSet<string>();
            if (tasks == null)
            {
                return keys;
            }

            foreach (var task in tasks)
            {
                if (task == null || string.IsNullOrWhiteSpace(task.title))
                {
                    continue;
                }

                var key = NormalizeBlackboardTitleKey(task.title);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private int CleanupTodoListByTitleKeys(Type todoListDataType, object todoListData, HashSet<string> titleKeys)
        {
            if (todoListData == null || titleKeys == null || titleKeys.Count == 0)
            {
                return 0;
            }

            var todoDic = GetMemberValue(todoListData, "TodoDic") as IDictionary;
            if (todoDic == null)
            {
                return 0;
            }

            var todosToRemove = new List<object>();
            foreach (var todo in CopyDictionaryValues(todoDic))
            {
                var text = GetMemberValue(todo, "TodoText") as string;
                var key = NormalizeBlackboardTitleKey(text);
                if (!string.IsNullOrWhiteSpace(key) && titleKeys.Contains(key))
                {
                    todosToRemove.Add(todo);
                }
            }

            var removedCount = 0;
            foreach (var todo in todosToRemove)
            {
                var todoId = GetMemberValue(todo, "UniqueID");
                if (InvokeRemoveTodo(todoListDataType, todoListData, todo))
                {
                    DirectRemoveTodoIfStillPresent(todoListData, todoDic, todo, todoId);
                    removedCount++;
                }
                else if (DirectRemoveTodoIfStillPresent(todoListData, todoDic, todo, todoId))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        private static string NormalizeBlackboardTitleKey(string title)
        {
            var value = title ?? string.Empty;
            value = Regex.Replace(value, @"^\s*(ENGL|MATH|CI|CS|CIS|BIO|BIOL|CHEM|PHYS|HIST|PSYC|SOC|ART|MUS|COMM|ECON|ACCT|BUS|STAT)\s*-\s*", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"^\s*(today|tomorrow|yesterday|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\s*-\s*[a-z]+\s+\d{1,2},\s*20\d{2}\s+", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+-?\s*due(?: date)?:?.*$", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+due\s+\d{1,2}/\d{1,2}.*$", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();
            return value;
        }

        private bool DirectRemoveTodoIfStillPresent(object todoListData, IDictionary todoDic, object todo, object todoId)
        {
            if (todoListData == null || todoDic == null || todo == null)
            {
                return false;
            }

            ulong id;
            if (!TryConvertToUInt64(todoId, out id))
            {
                todoId = GetMemberValue(todo, "UniqueID");
                if (!TryConvertToUInt64(todoId, out id))
                {
                    return false;
                }
            }

            var wasPresent = false;
            var keysToRemove = new List<object>();
            foreach (DictionaryEntry entry in todoDic)
            {
                if (KeysMatch(entry.Key, id) || ReferenceEquals(entry.Value, todo))
                {
                    keysToRemove.Add(entry.Key);
                    wasPresent = true;
                }
            }

            foreach (var key in keysToRemove)
            {
                todoDic.Remove(key);
            }

            var todoOrderList = GetMemberValue(todoListData, "TodoOrderList") as IList;
            if (todoOrderList != null)
            {
                for (var i = todoOrderList.Count - 1; i >= 0; i--)
                {
                    if (KeysMatch(todoOrderList[i], id))
                    {
                        todoOrderList.RemoveAt(i);
                        wasPresent = true;
                    }
                }
            }

            return wasPresent;
        }

        private static List<object> CopyDictionaryValues(IDictionary dictionary)
        {
            var values = new List<object>();
            if (dictionary == null)
            {
                return values;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                values.Add(entry.Value);
            }

            return values;
        }

        private object FindSaveDataManagerInstance()
        {
            var saveDataManagerType = FindType("Bulbul.SaveDataManager") ??
                GetAllTypesSafe().FirstOrDefault(t => t.FullName == "Bulbul.SaveDataManager" || t.Name == "SaveDataManager");

            if (saveDataManagerType == null)
            {
                Log.LogWarning("SaveDataManager type was not found.");
                return null;
            }

            var instance =
                GetStaticMemberValue(saveDataManagerType, "Instance") ??
                GetStaticMemberValue(saveDataManagerType, "instance");

            if (instance == null)
            {
                Log.LogWarning("SaveDataManager.Instance was not found or was null.");
                return null;
            }

            Log.LogInfo("Found SaveDataManager instance: " + saveDataManagerType.FullName);
            return instance;
        }

        private void PersistTodoChanges(object todoAllData, object todoListData, bool refreshUi)
        {
            var saved = false;
            var saveDataManager = FindSaveDataManagerInstance();
            if (saveDataManager != null)
            {
                if (todoListData != null && InvokeMethodIfExists(saveDataManager, "SaveTodoList", new[] { todoListData }))
                {
                    saved = true;
                    Log.LogInfo("Saved current TodoListData through SaveDataManager.SaveTodoList.");
                }

                if (InvokeMethodIfExists(saveDataManager, "SaveTodo", new object[0]))
                {
                    saved = true;
                    Log.LogInfo("Saved TodoAllData through SaveDataManager.SaveTodo.");
                }
            }

            if (!saved)
            {
                Log.LogWarning("Could not find a SaveDataManager Todo save entrypoint; Todo data changed in memory only.");
            }

            if (refreshUi)
            {
                RefreshTodoUi();
            }
        }

        private void RefreshTodoUi()
        {
            var refreshCount = 0;
            foreach (var behaviour in FindRuntimeBehaviours())
            {
                var type = behaviour.GetType();
                if (type.FullName == "Bulbul.FacilityTodo" && InvokeMethodIfExists(behaviour, "UpdateFacility", new object[0]))
                {
                    refreshCount++;
                    continue;
                }

                if (type.FullName == "Bulbul.Mobile.TodoListUIModel")
                {
                    var currentId = GetMemberValue(behaviour, "CurrentTodoListUuid");
                    if (currentId != null && InvokeMethodIfExists(behaviour, "SelectTodoList", new[] { currentId }))
                    {
                        refreshCount++;
                    }
                }
            }

            if (refreshCount > 0)
            {
                Log.LogInfo("Requested Todo UI refresh on runtime object(s): " + refreshCount);
            }
        }

        private object FindCurrentTodoListData(Type todoListDataType, object todoAllData, bool allowUnityObjectSearch)
        {
            foreach (var type in GetAllTypesSafe())
            {
                foreach (var name in CurrentListMemberNames)
                {
                    var value = GetStaticMemberValue(type, name);
                    if (IsInstanceOf(value, todoListDataType))
                    {
                        Log.LogInfo(string.Format("Found current TodoListData through static member {0}.{1}.", type.FullName, name));
                        return value;
                    }
                }

                foreach (var name in CurrentListIdMemberNames)
                {
                    var id = GetStaticMemberValue(type, name);
                    var resolved = ResolveTodoListById(todoAllData, id, todoListDataType);
                    if (resolved != null)
                    {
                        Log.LogInfo(string.Format("Found current TodoListData through static ID member {0}.{1}.", type.FullName, name));
                        return resolved;
                    }
                }
            }

            var saveDataManager = FindSaveDataManagerInstance();
            var saveDataCurrentList = FindCurrentTodoListDataOnInstance(saveDataManager, todoListDataType, todoAllData);
            if (saveDataCurrentList != null)
            {
                return saveDataCurrentList;
            }

            if (allowUnityObjectSearch)
            {
                foreach (var behaviour in FindRuntimeBehaviours())
                {
                    var behaviourType = behaviour.GetType();
                    var relevantName =
                        behaviourType.Name.IndexOf("Todo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        behaviourType.Name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        behaviourType.Name.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!relevantName)
                    {
                        continue;
                    }

                    var runtimeCurrentList = FindCurrentTodoListDataOnInstance(behaviour, todoListDataType, todoAllData);
                    if (runtimeCurrentList != null)
                    {
                        return runtimeCurrentList;
                    }
                }
            }

            Log.LogWarning("Current TodoListData was not found.");
            return null;
        }

        private object FindCurrentTodoListDataOnInstance(object instance, Type todoListDataType, object todoAllData)
        {
            if (instance == null)
            {
                return null;
            }

            var instanceType = instance.GetType();
            foreach (var name in CurrentListMemberNames)
            {
                var value = GetMemberValue(instance, name);
                if (IsInstanceOf(value, todoListDataType))
                {
                    Log.LogInfo(string.Format("Found current TodoListData through runtime member {0}.{1}.", instanceType.FullName, name));
                    return value;
                }
            }

            foreach (var name in CurrentListIdMemberNames)
            {
                var id = GetMemberValue(instance, name);
                var resolved = ResolveTodoListById(todoAllData, id, todoListDataType);
                if (resolved != null)
                {
                    Log.LogInfo(string.Format("Found current TodoListData through runtime ID member {0}.{1}.", instanceType.FullName, name));
                    return resolved;
                }
            }

            return null;
        }

        private object GetFirstTodoListData(object todoAllData, Type todoListDataType)
        {
            var todoListDic = GetMemberValue(todoAllData, "TodoListDic") as IDictionary;
            if (todoListDic == null || todoListDic.Count == 0)
            {
                Log.LogWarning("TodoAllData.TodoListDic was missing or empty.");
                return null;
            }

            foreach (DictionaryEntry entry in todoListDic)
            {
                if (IsInstanceOf(entry.Value, todoListDataType))
                {
                    Log.LogInfo("Using first TodoListData from TodoAllData.TodoListDic.");
                    return entry.Value;
                }
            }

            return null;
        }

        private object CreateTodoListData(object todoAllData, Type todoListDataType)
        {
            try
            {
                var todoListData = Activator.CreateInstance(todoListDataType);
                SetMemberValue(todoListData, "UniqueID", GenerateUniqueId("blackboard-list", new HashSet<ulong>()));
                SetMemberValue(todoListData, "TitleText", "Blackboard");

                var addTodoList = todoAllData.GetType().GetMethod(
                    "AddTodoList",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { todoListDataType },
                    null);

                if (addTodoList == null)
                {
                    Log.LogWarning("TodoAllData.AddTodoList(TodoListData) was not found.");
                    return null;
                }

                var result = addTodoList.Invoke(todoAllData, new[] { todoListData });
                if (result is bool && !(bool)result)
                {
                    Log.LogWarning("TodoAllData.AddTodoList returned false.");
                    return null;
                }

                Log.LogInfo("Created new TodoListData named Blackboard through TodoAllData.AddTodoList.");
                return todoListData;
            }
            catch (Exception ex)
            {
                Log.LogError("Could not create TodoListData: " + ex);
                return null;
            }
        }

        private object ResolveTodoListById(object todoAllData, object idValue, Type todoListDataType)
        {
            if (todoAllData == null || idValue == null)
            {
                return null;
            }

            var todoListDic = GetMemberValue(todoAllData, "TodoListDic") as IDictionary;
            if (todoListDic == null)
            {
                return null;
            }

            foreach (DictionaryEntry entry in todoListDic)
            {
                if (KeysMatch(entry.Key, idValue) && IsInstanceOf(entry.Value, todoListDataType))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        private bool InvokeAddTodo(Type todoListDataType, object todoListData, object todo)
        {
            var addTodo = todoListDataType.GetMethod(
                "AddTodo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (addTodo == null)
            {
                throw new MissingMethodException(todoListDataType.FullName, "AddTodo");
            }

            var result = addTodo.Invoke(todoListData, new[] { todo });
            return !(result is bool) || (bool)result;
        }

        private object FindExistingBlackboardTodo(IDictionary todoDic, string blackboardId, ulong stableUniqueId)
        {
            if (todoDic == null)
            {
                return null;
            }

            var legacyPrefix = "[BB:" + blackboardId + "]";
            foreach (DictionaryEntry entry in todoDic)
            {
                if (KeysMatch(entry.Key, stableUniqueId))
                {
                    return entry.Value;
                }

                var uniqueId = GetMemberValue(entry.Value, "UniqueID");
                if (KeysMatch(uniqueId, stableUniqueId))
                {
                    return entry.Value;
                }

                var text = GetMemberValue(entry.Value, "TodoText") as string;
                if (text != null && text.IndexOf(legacyPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entry.Value;
                }
            }

            return null;
        }

        private void StripLegacyPrefixIfNeeded(
            Type todoListDataType,
            object todoListData,
            object todo,
            string prefix,
            string fallbackTitle)
        {
            var currentText = GetMemberValue(todo, "TodoText") as string;
            string foundPrefix;
            string cleaned;
            if (!TryStripLegacyPrefix(currentText, out foundPrefix, out cleaned) ||
                !string.Equals(foundPrefix, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                cleaned = fallbackTitle;
            }

            if (string.Equals(currentText, cleaned, StringComparison.Ordinal))
            {
                return;
            }

            if (InvokeSetInputTextTodo(todoListDataType, todoListData, todo, cleaned))
            {
                Log.LogInfo("Removed legacy Blackboard prefix from Todo text: " + cleaned);
            }
            else
            {
                SetMemberValue(todo, "TodoText", cleaned);
                Log.LogWarning("Removed legacy Blackboard prefix by setting TodoText directly; game save may require another Todo edit.");
            }
        }

        private static bool TryStripLegacyPrefix(string text, out string prefix, out string cleaned)
        {
            prefix = null;
            cleaned = null;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith("[BB:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var end = trimmed.IndexOf(']');
            if (end < 4)
            {
                return false;
            }

            prefix = trimmed.Substring(0, end + 1);
            cleaned = trimmed.Substring(prefix.Length).TrimStart();
            return true;
        }

        private bool InvokeSetInputTextTodo(Type todoListDataType, object todoListData, object todo, string text)
        {
            var setInputTextTodo = todoListDataType.GetMethod(
                "SetInputTextTodo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (setInputTextTodo == null)
            {
                return false;
            }

            try
            {
                var result = setInputTextTodo.Invoke(todoListData, new[] { todo, text });
                return !(result is bool) || (bool)result;
            }
            catch (Exception ex)
            {
                Log.LogWarning("TodoListData.SetInputTextTodo failed: " + ex.Message);
                return false;
            }
        }

        private bool InvokeRemoveTodo(Type todoListDataType, object todoListData, object todo)
        {
            var removeTodo = todoListDataType.GetMethod(
                "RemoveTodo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (removeTodo == null)
            {
                Log.LogWarning("TodoListData.RemoveTodo was not found.");
                return false;
            }

            try
            {
                removeTodo.Invoke(todoListData, new[] { todo });
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("TodoListData.RemoveTodo failed: " + ex.Message);
                return false;
            }
        }

        private static string NormalizeTodoTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            return System.Text.RegularExpressions.Regex
                .Replace(title.Trim(), "\\s+", " ")
                .ToLowerInvariant();
        }

        private static HashSet<ulong> GetExistingTodoIds(IDictionary todoDic)
        {
            var ids = new HashSet<ulong>();
            if (todoDic == null)
            {
                return ids;
            }

            foreach (DictionaryEntry entry in todoDic)
            {
                ulong key;
                if (TryConvertToUInt64(entry.Key, out key))
                {
                    ids.Add(key);
                }

                var uniqueId = GetMemberValue(entry.Value, "UniqueID");
                ulong todoId;
                if (TryConvertToUInt64(uniqueId, out todoId))
                {
                    ids.Add(todoId);
                }
            }

            return ids;
        }

        private static ulong GenerateStableUniqueId(string seed)
        {
            return GenerateUniqueId(seed, new HashSet<ulong>());
        }

        private static ulong GenerateUniqueId(string seed, HashSet<ulong> existingIds)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            var hash = fnvOffset;
            foreach (var ch in seed ?? string.Empty)
            {
                hash ^= ch;
                hash *= fnvPrime;
            }

            if (hash == 0)
            {
                hash = (ulong)DateTime.UtcNow.Ticks;
            }

            while (existingIds.Contains(hash) || hash == 0)
            {
                hash += 0x9E3779B97F4A7C15UL;
            }

            return hash;
        }

        private static Type FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }

        private static IEnumerable<Type> GetAllTypesSafe()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    yield return type;
                }
            }
        }

        private static IEnumerable<MonoBehaviour> FindRuntimeBehaviours()
        {
            MonoBehaviour[] behaviours;
            try
            {
                behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            }
            catch
            {
                behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            }

            return behaviours.Where(behaviour => behaviour != null);
        }

        private static object FindAssignableMemberValue(object instance, Type targetType)
        {
            if (instance == null || targetType == null)
            {
                return null;
            }

            var type = instance.GetType();
            foreach (var member in GetFieldsAndProperties(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!targetType.IsAssignableFrom(GetMemberType(member)))
                {
                    continue;
                }

                var value = GetMemberValue(instance, member);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static object FindStaticAssignableMemberValue(Type type, Type targetType)
        {
            foreach (var member in GetFieldsAndProperties(type, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!targetType.IsAssignableFrom(GetMemberType(member)))
                {
                    continue;
                }

                var value = GetStaticMemberValue(type, member);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static object InvokeNoArgMethodReturning(object instance, string methodName, Type returnType)
        {
            var method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            if (method == null || !returnType.IsAssignableFrom(method.ReturnType))
            {
                return null;
            }

            try
            {
                return method.Invoke(instance, new object[0]);
            }
            catch
            {
                return null;
            }
        }

        private static void InvokeMethod(object instance, string methodName, object[] args)
        {
            var methods = instance.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == methodName)
                .ToArray();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                {
                    continue;
                }

                var convertedArgs = ConvertArguments(args, parameters);
                method.Invoke(instance, convertedArgs);
                return;
            }

            throw new MissingMethodException(instance.GetType().FullName, methodName);
        }

        private static bool InvokeMethodIfExists(object instance, string methodName, object[] args)
        {
            if (instance == null)
            {
                return false;
            }

            var methods = instance.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == methodName)
                .ToArray();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                {
                    continue;
                }

                try
                {
                    var convertedArgs = ConvertArguments(args, parameters);
                    method.Invoke(instance, convertedArgs);
                    return true;
                }
                catch (Exception ex)
                {
                    if (Log != null)
                    {
                        Log.LogWarning(string.Format("{0}.{1} failed: {2}", instance.GetType().FullName, methodName, ex.Message));
                    }
                }
            }

            return false;
        }

        private static object GetStaticMemberValue(Type type, string name)
        {
            var member = GetFieldOrProperty(type, name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return member == null ? null : GetStaticMemberValue(type, member);
        }

        private static object GetStaticMemberValue(Type type, MemberInfo member)
        {
            try
            {
                var field = member as FieldInfo;
                if (field != null)
                {
                    return field.GetValue(null);
                }

                var property = member as PropertyInfo;
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(null, null);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            var member = GetFieldOrProperty(
                instance.GetType(),
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            return member == null ? null : GetMemberValue(instance, member);
        }

        private static object GetMemberValue(object instance, MemberInfo member)
        {
            try
            {
                var field = member as FieldInfo;
                if (field != null)
                {
                    return field.GetValue(instance);
                }

                var property = member as PropertyInfo;
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(instance, null);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static void SetMemberValue(object instance, string name, object value)
        {
            var member = GetFieldOrProperty(
                instance.GetType(),
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (member == null)
            {
                return;
            }

            try
            {
                var field = member as FieldInfo;
                if (field != null)
                {
                    field.SetValue(instance, ConvertValue(value, field.FieldType));
                }

                var property = member as PropertyInfo;
                if (property != null && property.CanWrite)
                {
                    property.SetValue(instance, ConvertValue(value, property.PropertyType), null);
                }
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogWarning(string.Format("Could not set {0}.{1}: {2}", instance.GetType().FullName, name, ex.Message));
                }
            }
        }

        private static MemberInfo GetFieldOrProperty(Type type, string name, BindingFlags flags)
        {
            while (type != null)
            {
                var field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }

                var property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    return property;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static IEnumerable<MemberInfo> GetFieldsAndProperties(Type type, BindingFlags flags)
        {
            while (type != null)
            {
                foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    yield return field;
                }

                foreach (var property in type.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    if (property.GetIndexParameters().Length == 0)
                    {
                        yield return property;
                    }
                }

                type = type.BaseType;
            }
        }

        private static Type GetMemberType(MemberInfo member)
        {
            var field = member as FieldInfo;
            if (field != null)
            {
                return field.FieldType;
            }

            var property = member as PropertyInfo;
            if (property != null)
            {
                return property.PropertyType;
            }

            return typeof(void);
        }

        private static bool IsInstanceOf(object value, Type type)
        {
            return value != null && type != null && type.IsInstanceOfType(value);
        }

        private static bool KeysMatch(object left, object right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            ulong leftUlong;
            ulong rightUlong;
            if (TryConvertToUInt64(left, out leftUlong) && TryConvertToUInt64(right, out rightUlong))
            {
                return leftUlong == rightUlong;
            }

            return string.Equals(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryConvertToUInt64(object value, out ulong result)
        {
            try
            {
                if (value is ulong)
                {
                    result = (ulong)value;
                    return true;
                }

                if (value is long)
                {
                    var longValue = (long)value;
                    if (longValue >= 0)
                    {
                        result = (ulong)longValue;
                        return true;
                    }
                }

                if (value is uint)
                {
                    result = (uint)value;
                    return true;
                }

                if (value is int)
                {
                    var intValue = (int)value;
                    if (intValue >= 0)
                    {
                        result = (ulong)intValue;
                        return true;
                    }
                }

                var stringValue = value as string;
                if (stringValue != null)
                {
                    return ulong.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                }

                result = 0;
                return false;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null || targetType.IsInstanceOfType(value))
            {
                return value;
            }

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null)
            {
                return ConvertValue(value, nullableType);
            }

            if (targetType.IsEnum)
            {
                return Enum.ToObject(targetType, value);
            }

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static object[] ConvertArguments(object[] args, ParameterInfo[] parameters)
        {
            var converted = new object[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                converted[i] = ConvertValue(args[i], parameters[i].ParameterType);
            }

            return converted;
        }

        [Serializable]
        private sealed class BlackboardTaskEnvelope
        {
            public List<BlackboardTask> items;
        }

        [Serializable]
        private sealed class BlackboardTask
        {
            public string id;
            public string title;
            public string due;
        }
    }

    internal sealed class BlackboardTodoImporterRunner : MonoBehaviour
    {
        public BlackboardTodoImporterPlugin Owner;

        private void Start()
        {
            if (Owner != null)
            {
                Owner.LogRunnerStarted();
                StartCoroutine(Owner.AutoImportCoroutine());
            }
        }

        private void Update()
        {
            if (Owner != null)
            {
                Owner.Tick();
            }
        }

        private void OnGUI()
        {
            if (Owner == null || Event.current == null)
            {
                return;
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F10)
            {
                Owner.LogRunnerManualImport();
                Owner.ImportFromJson();
            }
        }
    }
}
