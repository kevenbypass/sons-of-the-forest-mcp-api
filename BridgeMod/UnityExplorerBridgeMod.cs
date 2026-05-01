using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SonsSdk;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityExplorerBridge;

[SupportedOSPlatform("windows")]
public sealed class UnityExplorerBridgeMod : SonsMod
{
    internal const string BridgeVersion = "0.1.3";
    internal const string PipeName = "unityexplorer_bridge";
    private const int MaxRequestsPerFrame = 8;
    private const double FrameBudgetMs = 4.0;
    private const int DefaultMaxScanObjects = 8000;
    private readonly ConcurrentQueue<PendingRequest> _pendingRequests = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedEntry> _shortCache = new();
    private readonly object _scanLock = new();
    private readonly Dictionary<string, PlayerScanSession> _playerScans = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private BridgePipeServer? _pipeServer;
    private volatile bool _sdkInitialized;
    private volatile bool _gameStarted;
    private readonly string _bridgeLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonsOfTheForest",
        "UnityExplorerBridge.log");

    public UnityExplorerBridgeMod()
    {
        OnUpdateCallback = ProcessPendingRequests;
    }

    protected override void OnInitializeMod()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_bridgeLogPath)!);
        _pipeServer = new BridgePipeServer(this);
        _pipeServer.Start();
        LogInfo($"Bridge server started on pipe '{PipeName}'.");
    }

    protected override void OnSdkInitialized()
    {
        _sdkInitialized = true;
        LogInfo("SDK initialized.");
    }

    protected override void OnGameStart()
    {
        _gameStarted = true;
        InvalidateShortCache();
        LogInfo("Game start observed.");
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        if (string.Equals(sceneName, "SonsMain", StringComparison.OrdinalIgnoreCase))
        {
            _gameStarted = false;
        }
    }

    protected override void OnApplicationQuit()
    {
        _pipeServer?.Stop();
        base.OnApplicationQuit();
    }

    protected override void OnDeinitializeMod()
    {
        _pipeServer?.Stop();
        base.OnDeinitializeMod();
    }

    internal string SerializeResponse(BridgeEnvelope envelope)
    {
        return JsonSerializer.Serialize(envelope, _jsonOptions);
    }

    internal void Enqueue(PendingRequest request)
    {
        _pendingRequests.Enqueue(request);
    }

    private void ProcessPendingRequests()
    {
        var frameTimer = Stopwatch.StartNew();
        for (var i = 0; i < MaxRequestsPerFrame; i++)
        {
            if (frameTimer.Elapsed.TotalMilliseconds >= FrameBudgetMs)
            {
                break;
            }

            if (!_pendingRequests.TryDequeue(out var pending))
            {
                break;
            }

            var commandTimer = Stopwatch.StartNew();
            try
            {
                pending.ResponseJson = ExecuteOnMainThread(pending.Request);
            }
            catch (Exception ex)
            {
                pending.ResponseJson = SerializeResponse(BridgeEnvelope.Failure(
                    pending.Request.Id,
                    "command_failed",
                    ex.Message,
                    ex.ToString()));
                LogError($"Command '{pending.Request.Command}' failed: {ex}");
            }
            finally
            {
                commandTimer.Stop();
                LogInfo($"Command '{pending.Request.Command}' completed in {commandTimer.Elapsed.TotalMilliseconds:F2}ms");
                pending.Signal.Set();
            }
        }
    }

    private string ExecuteOnMainThread(BridgeRequest request)
    {
        var command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
        try
        {
            return SerializeResponse(BridgeEnvelope.Success(request.Id, ExecuteCommand(command, request.Args)));
        }
        catch (Exception ex)
        {
            return SerializeResponse(BridgeEnvelope.Failure(request.Id, "command_failed", ex.Message, ex.ToString()));
        }
    }

    private object ExecuteCommand(string command, JsonElement args)
    {
        return command switch
        {
            "ping" => new Dictionary<string, object?>
            {
                ["pong"] = true,
                ["bridgeVersion"] = BridgeVersion,
                ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
            },
            "status" => GetCached("status", 200, BuildStatusPayload),
            "list_scenes" => GetCached("list_scenes", 150, BuildScenesPayload),
            "network_status" => GetCached("network_status", 350, BuildNetworkStatusPayload),
            "host_client_state" => GetCached("host_client_state", 200, BuildHostClientStatePayload),
            "current_lobby_info" => BuildCurrentLobbyInfoPayload(),
            "show_message" => ShowMessage(args),
            "run_game_command" => RunGameCommand(args),
            "send_chat_message" => SendChatMessage(args),
            "find_gameobjects" => FindGameObjects(args),
            "get_local_player" => GetLocalPlayer(args),
            "get_local_player_items" => GetLocalPlayerItems(args),
            "list_players" => ListPlayers(args),
            "players_scan_start" => PlayersScanStart(args),
            "players_scan_next" => PlayersScanNext(args),
            "players_scan_stop" => PlayersScanStop(args),
            "list_bolt_entities" => ListBoltEntities(args),
            "get_gameobject" => GetGameObject(args),
            "list_children" => ListChildren(args),
            "list_components" => ListComponents(args),
            "get_component_members" => GetComponentMembers(args),
            "set_component_member" => SetComponentMember(args),
            "inspect_gameobject" => InspectGameObject(args),
            "get_type_layout" => GetTypeLayout(args),
            "get_field_offset" => GetFieldOffset(args),
            "execute_many" => ExecuteMany(args),
            _ => throw new InvalidOperationException($"Unknown command '{command}'.")
        };
    }

    private object ExecuteMany(JsonElement args)
    {
        if (!TryGetProperty(args, "commands", out var commandsElement) || commandsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Missing required array argument 'commands'.");
        }

        var results = new List<Dictionary<string, object?>>();
        var index = 0;
        foreach (var commandElement in commandsElement.EnumerateArray())
        {
            if (index >= 64)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["ok"] = false,
                    ["error"] = "too_many_commands",
                    ["details"] = "execute_many supports up to 64 commands per request.",
                });
                break;
            }

            var subCommand = GetString(commandElement, "command");
            var subArgs = TryGetProperty(commandElement, "args", out var subArgsElement) && subArgsElement.ValueKind == JsonValueKind.Object
                ? subArgsElement
                : default;
            var startedAt = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(subCommand))
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["ok"] = false,
                    ["error"] = "invalid_request",
                    ["details"] = "Each command item must include a non-empty 'command' string.",
                    ["durationMs"] = 0,
                });
                index++;
                continue;
            }

            var normalized = subCommand.Trim().ToLowerInvariant();
            if (normalized == "execute_many")
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["command"] = normalized,
                    ["ok"] = false,
                    ["error"] = "nested_execute_many_not_allowed",
                    ["details"] = "Nested execute_many calls are not allowed.",
                    ["durationMs"] = 0,
                });
                index++;
                continue;
            }

            try
            {
                var data = ExecuteCommand(normalized, subArgs);
                results.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["command"] = normalized,
                    ["ok"] = true,
                    ["data"] = data,
                    ["durationMs"] = (DateTime.UtcNow - startedAt).TotalMilliseconds,
                });
            }
            catch (Exception ex)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["command"] = normalized,
                    ["ok"] = false,
                    ["error"] = "command_failed",
                    ["details"] = ex.Message,
                    ["durationMs"] = (DateTime.UtcNow - startedAt).TotalMilliseconds,
                });
            }

            index++;
        }

        return new Dictionary<string, object?>
        {
            ["count"] = results.Count,
            ["results"] = results,
        };
    }

    private object GetCached(string cacheKey, int ttlMs, Func<object> factory)
    {
        var now = DateTime.UtcNow;
        lock (_cacheLock)
        {
            if (_shortCache.TryGetValue(cacheKey, out var cached) &&
                (now - cached.CreatedUtc).TotalMilliseconds <= ttlMs)
            {
                return cached.Data;
            }
        }

        var created = factory();
        lock (_cacheLock)
        {
            _shortCache[cacheKey] = new CachedEntry(created, now);
        }

        return created;
    }

    private void InvalidateShortCache()
    {
        lock (_cacheLock)
        {
            _shortCache.Clear();
        }
    }

    private object PlayersScanStart(JsonElement args)
    {
        var includeInactive = GetBool(args, "includeInactive", true);
        var maxObjects = Math.Clamp(GetInt(args, "maxObjects", DefaultMaxScanObjects), 500, 50000);
        var candidates = EnumeratePlayerCandidates(includeInactive, limit: 500, maxObjects: maxObjects);
        var scanId = Guid.NewGuid().ToString("N");
        var session = new PlayerScanSession(candidates, DateTime.UtcNow);

        lock (_scanLock)
        {
            PruneOldScans();
            _playerScans[scanId] = session;
        }

        return new Dictionary<string, object?>
        {
            ["scanId"] = scanId,
            ["count"] = candidates.Count,
            ["includeInactive"] = includeInactive,
            ["maxObjects"] = maxObjects,
        };
    }

    private object PlayersScanNext(JsonElement args)
    {
        var scanId = RequireString(args, "scanId");
        var batchSize = Math.Clamp(GetInt(args, "batchSize", 25), 1, 100);
        PlayerScanSession session;
        lock (_scanLock)
        {
            if (!_playerScans.TryGetValue(scanId, out session!))
            {
                throw new InvalidOperationException($"Unknown scanId '{scanId}'.");
            }
            session.LastAccessUtc = DateTime.UtcNow;
        }

        var start = session.Cursor;
        var end = Math.Min(session.Cursor + batchSize, session.Candidates.Count);
        var page = new List<object>(Math.Max(0, end - start));
        for (var i = start; i < end; i++)
        {
            var go = session.Candidates[i];
            if (go == null)
            {
                continue;
            }

            page.Add(BuildPlayerSnapshot(go));
        }

        session.Cursor = end;
        var done = session.Cursor >= session.Candidates.Count;
        if (done)
        {
            lock (_scanLock)
            {
                _playerScans.Remove(scanId);
            }
        }

        return new Dictionary<string, object?>
        {
            ["scanId"] = scanId,
            ["cursor"] = session.Cursor,
            ["total"] = session.Candidates.Count,
            ["done"] = done,
            ["count"] = page.Count,
            ["results"] = page,
        };
    }

    private object PlayersScanStop(JsonElement args)
    {
        var scanId = RequireString(args, "scanId");
        var removed = false;
        lock (_scanLock)
        {
            removed = _playerScans.Remove(scanId);
        }

        return new Dictionary<string, object?>
        {
            ["scanId"] = scanId,
            ["removed"] = removed,
        };
    }

    private void PruneOldScans()
    {
        var now = DateTime.UtcNow;
        var expired = _playerScans
            .Where(entry => (now - entry.Value.LastAccessUtc).TotalMinutes > 3)
            .Select(entry => entry.Key)
            .ToList();
        foreach (var key in expired)
        {
            _playerScans.Remove(key);
        }
    }

    private object BuildStatusPayload()
    {
        return new Dictionary<string, object?>
        {
            ["bridgeVersion"] = BridgeVersion,
            ["pipeName"] = PipeName,
            ["sdkInitialized"] = _sdkInitialized,
            ["gameStarted"] = _gameStarted,
            ["unityExplorerLoaded"] = AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, "UnityExplorer", StringComparison.OrdinalIgnoreCase)),
            ["activeScene"] = SceneManager.GetActiveScene().name,
            ["loadedScenes"] = EnumerateLoadedScenes(),
            ["network"] = BuildHostClientStatePayload(),
        };
    }

    private object BuildScenesPayload()
    {
        return new Dictionary<string, object?>
        {
            ["activeScene"] = SceneManager.GetActiveScene().name,
            ["scenes"] = EnumerateLoadedScenes(),
        };
    }

    private object ShowMessage(JsonElement args)
    {
        var message = RequireString(args, "message");
        var duration = GetFloat(args, "duration", 4f);
        SonsTools.ShowMessage(message, duration);
        return new Dictionary<string, object?>
        {
            ["shown"] = true,
            ["message"] = message,
            ["duration"] = duration,
        };
    }

    private object RunGameCommand(JsonElement args)
    {
        var command = RequireString(args, "command").Trim();
        var argument = GetString(args, "argument") ?? string.Empty;
        var normalizedCommand = NormalizeCommandName(command);
        var commandLine = string.IsNullOrWhiteSpace(argument) ? command : $"{command} {argument}";

        var gameCommandsType = FindLoadedType("SonsSdk.GameCommands", "GameCommands");
        if (gameCommandsType != null)
        {
            var method = ResolveGameCommandMethod(gameCommandsType, normalizedCommand);
            if (method != null)
            {
                method.Invoke(null, new object[] { argument });
                return new Dictionary<string, object?>
                {
                    ["executed"] = true,
                    ["command"] = command,
                    ["argument"] = argument,
                    ["method"] = method.Name,
                    ["path"] = "SonsSdk.GameCommands",
                };
            }
        }

        var dispatcher = TryInvokeConsoleDispatcher(commandLine);
        if (dispatcher != null)
        {
            return new Dictionary<string, object?>
            {
                ["executed"] = true,
                ["command"] = command,
                ["argument"] = argument,
                ["path"] = "ConsoleDispatcher",
                ["dispatcher"] = dispatcher,
            };
        }

        var available = gameCommandsType == null
            ? new List<string>()
            : gameCommandsType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
                .Select(m => m.Name)
                .OrderBy(n => n)
                .ToList();

        return new Dictionary<string, object?>
        {
            ["executed"] = false,
            ["command"] = command,
            ["reason"] = "unknown_game_command",
            ["availableMethods"] = available,
        };
    }

    private static MethodInfo? ResolveGameCommandMethod(Type gameCommandsType, string normalizedCommand)
    {
        var methods = gameCommandsType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
            .ToList();

        // Fast explicit aliases for commands we already know are registered.
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["togglegrass"] = "ToggleGrassCommand",
            ["xfreecam"] = "FreecamCommand",
            ["cancelblueprints"] = "CancelBlueprintsCommand",
            ["finishblueprints"] = "FinishBlueprintsCommand",
            ["noforest"] = "NoForestCommand",
            ["clearpickups"] = "ClearPickupsCommand",
            ["gotopickup"] = "GoToPickup",
            ["aighostplayer"] = "GhostPlayerCommand",
            ["saveconsolepos"] = "SaveConsolePos",
            ["virginiasentiment"] = "VirginiaSentiment",
            ["virginiavisit"] = "VirginiaVisit",
            ["dump"] = "DumpCommand",
            ["playcutscene"] = "PlayCutsceneCommand",
            ["dumpboltserializers"] = "DumpBoltSerializersCommand",
        };

        if (aliases.TryGetValue(normalizedCommand, out var methodName))
        {
            return methods.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.Ordinal));
        }

        return methods.FirstOrDefault(m =>
            NormalizeCommandName(m.Name) == normalizedCommand ||
            NormalizeCommandName(m.Name.Replace("Command", string.Empty, StringComparison.OrdinalIgnoreCase)) == normalizedCommand);
    }

    private static string NormalizeCommandName(string value)
    {
        var chars = value
            .Where(ch => char.IsLetterOrDigit(ch))
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static string? TryInvokeConsoleDispatcher(string commandLine)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name ?? string.Empty;
            if (!assemblyName.Contains("RedLoader", StringComparison.OrdinalIgnoreCase) &&
                !assemblyName.Contains("Sons", StringComparison.OrdinalIgnoreCase) &&
                !assemblyName.Contains("Endnight", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var type in SafeGetTypes(assembly))
            {
                var fullName = type.FullName ?? type.Name;
                if (!fullName.Contains("Command", StringComparison.OrdinalIgnoreCase) &&
                    !fullName.Contains("Console", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (fullName.Contains("ConsoleWindow", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                foreach (var method in type.GetMethods(bindingFlags))
                {
                    if (!LooksLikeDispatcherMethod(method))
                    {
                        continue;
                    }

                    try
                    {
                        var parameters = method.GetParameters();
                        object? instance = null;
                        if (!method.IsStatic)
                        {
                            instance = ResolveTypeInstance(type);
                            if (instance == null)
                            {
                                continue;
                            }
                        }

                        object? result = null;
                        if (parameters.Length == 1)
                        {
                            result = method.Invoke(instance, new object[] { commandLine });
                        }
                        else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(bool))
                        {
                            result = method.Invoke(instance, new object[] { commandLine, false });
                        }
                        else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string[]))
                        {
                            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var tail = parts.Length <= 1 ? Array.Empty<string>() : parts.Skip(1).ToArray();
                            result = method.Invoke(instance, new object[] { parts[0], tail });
                        }
                        else
                        {
                            continue;
                        }

                        return $"{fullName}.{method.Name} => {SerializeValue(result) ?? "void"}";
                    }
                    catch
                    {
                    }
                }
            }
        }

        return null;
    }

    private static object? ResolveTypeInstance(Type type)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var props = new[] { "Instance", "Current", "Singleton" };
        foreach (var name in props)
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                try
                {
                    var value = prop.GetValue(null);
                    if (value != null)
                    {
                        return value;
                    }
                }
                catch
                {
                }
            }
        }

        foreach (var name in props)
        {
            var field = type.GetField(name, flags);
            if (field != null)
            {
                try
                {
                    var value = field.GetValue(null);
                    if (value != null)
                    {
                        return value;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static bool LooksLikeDispatcherMethod(MethodInfo method)
    {
        var name = method.Name;
        var hasCommandWord = name.Contains("Command", StringComparison.OrdinalIgnoreCase);
        var hasExecutorWord =
            name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Process", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Run", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Invoke", StringComparison.OrdinalIgnoreCase);
        if (!(hasCommandWord || hasExecutorWord))
        {
            return false;
        }
        if (name.Contains("Set", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Get", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Title", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Window", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
        {
            return true;
        }

        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) &&
            (parameters[1].ParameterType == typeof(bool) || parameters[1].ParameterType == typeof(string[])))
        {
            return true;
        }

        return false;
    }

    private object BuildNetworkStatusPayload()
    {
        var boltNetworkType = FindLoadedType("Bolt.BoltNetwork", "BoltNetwork");
        var state = ReadBoltNetworkState(boltNetworkType);
        var boltEntities = EnumerateBoltEntityComponents(includeInactive: true, maxObjects: 4000)
            .Take(250)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["assemblies"] = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetName().Name)
                .Where(name => !string.IsNullOrWhiteSpace(name) && MatchesNetworkAssemblyName(name!))
                .OrderBy(name => name)
                .ToList(),
            ["boltNetworkFound"] = boltNetworkType != null,
            ["boltState"] = state,
            ["boltEntityCount"] = boltEntities.Count,
            ["playerCandidateCount"] = EnumeratePlayerCandidates(includeInactive: true, limit: 500, maxObjects: 4000).Count,
            ["scene"] = SceneManager.GetActiveScene().name,
        };
    }

    private object BuildHostClientStatePayload()
    {
        var boltNetworkType = FindLoadedType("Bolt.BoltNetwork", "BoltNetwork");
        var state = ReadBoltNetworkState(boltNetworkType);
        var isServer = TryGetBoolMember(null, boltNetworkType, "isServer", "IsServer");
        var isClient = TryGetBoolMember(null, boltNetworkType, "isClient", "IsClient");
        var isConnected = TryGetBoolMember(null, boltNetworkType, "isConnected", "IsConnected");

        var role = "offline";
        if (isServer == true)
        {
            role = isClient == true ? "host" : "server";
        }
        else if (isClient == true)
        {
            role = "client";
        }

        return new Dictionary<string, object?>
        {
            ["role"] = role,
            ["isServer"] = isServer,
            ["isClient"] = isClient,
            ["isConnected"] = isConnected,
            ["boltState"] = state,
        };
    }

    private object BuildCurrentLobbyInfoPayload()
    {
        var lobbyTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => MatchesNetworkAssemblyName(assembly.GetName().Name ?? string.Empty))
            .SelectMany(SafeGetTypes)
            .Where(type => type.FullName != null && (type.FullName.Contains("Lobby", StringComparison.OrdinalIgnoreCase) || type.FullName.Contains("Steam", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(type => type.FullName)
            .Take(20)
            .ToList();

        var snapshots = lobbyTypes
            .Select(type => new Dictionary<string, object?>
            {
                ["type"] = type.FullName,
                ["members"] = SnapshotStaticMembers(type, limit: 10),
            })
            .Where(entry => ((List<object>)entry["members"]!).Count > 0)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["candidateTypes"] = lobbyTypes.Select(type => type.FullName).ToList(),
            ["snapshots"] = snapshots,
        };
    }

    private object SendChatMessage(JsonElement args)
    {
        var message = RequireString(args, "message");
        var color = GetString(args, "color") ?? "#ffffff";
        var boltNetworkType = FindLoadedType("Bolt.BoltNetwork", "BoltNetwork");
        var isServer = TryGetBoolMember(null, boltNetworkType, "isServer", "IsServer");
        var isConnected = TryGetBoolMember(null, boltNetworkType, "isConnected", "IsConnected");
        var role = "offline";
        if (isServer == true)
        {
            role = TryGetBoolMember(null, boltNetworkType, "isClient", "IsClient") == true ? "host" : "server";
        }
        else if (TryGetBoolMember(null, boltNetworkType, "isClient", "IsClient") == true)
        {
            role = "client";
        }

        // SonsSdk chat only renders/sends while in multiplayer and running as server/host.
        if (isServer != true || isConnected != true)
        {
            return new Dictionary<string, object?>
            {
                ["sent"] = false,
                ["message"] = message,
                ["color"] = color,
                ["reason"] = "Chat send requires multiplayer host/server state.",
                ["role"] = role,
                ["isServer"] = isServer,
                ["isConnected"] = isConnected,
                ["receiver"] = "broadcast",
            };
        }

        var netUtilsType = FindLoadedType("SonsSdk.Networking.NetUtils");
        if (netUtilsType == null)
        {
            throw new InvalidOperationException("SonsSdk.Networking.NetUtils is not loaded.");
        }

        var sendMethod = netUtilsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => string.Equals(method.Name, "SendChatMessage", StringComparison.Ordinal) && method.GetParameters().Length == 4);
        if (sendMethod == null)
        {
            throw new InvalidOperationException("NetUtils.SendChatMessage was not found.");
        }

        var parameters = sendMethod.GetParameters();
        var sender = parameters[0].ParameterType.IsValueType
            ? Activator.CreateInstance(parameters[0].ParameterType)
            : null;
        object? receiver = null;

        sendMethod.Invoke(null, new[] { sender, (object)message, color, receiver });

        return new Dictionary<string, object?>
        {
            ["sent"] = true,
            ["message"] = message,
            ["color"] = color,
            ["role"] = role,
            ["receiver"] = "broadcast",
        };
    }

    private object FindGameObjects(JsonElement args)
    {
        var exactName = GetString(args, "name");
        var contains = GetString(args, "contains");
        var includeInactive = GetBool(args, "includeInactive", true);
        var limit = Math.Clamp(GetInt(args, "limit", 25), 1, 250);

        var matches = EnumerateGameObjects(includeInactive)
            .Where(go => Matches(go, exactName, contains))
            .Take(limit)
            .Select(go => BuildGameObjectSnapshot(go, includeComponents: false))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = matches.Count,
            ["results"] = matches,
        };
    }

    private object GetGameObject(JsonElement args)
    {
        var target = ResolveGameObject(args);
        return BuildGameObjectSnapshot(
            target,
            includeComponents: GetBool(args, "includeComponents", true),
            includeChildren: GetBool(args, "includeChildren", false),
            childDepth: Math.Clamp(GetInt(args, "childDepth", 1), 0, 5),
            childLimit: Math.Clamp(GetInt(args, "childLimit", 50), 1, 500));
    }

    private object GetLocalPlayer(JsonElement args)
    {
        var includeComponents = GetBool(args, "includeComponents", true);
        var maxObjects = Math.Clamp(GetInt(args, "maxObjects", DefaultMaxScanObjects), 500, 50000);
        EnsureGameplayScene();
        var target = GameObject.Find("LocalPlayer") ?? ResolveLocalPlayer(includeInactive: true, maxObjects: maxObjects);
        return BuildGameObjectSnapshot(
            target,
            includeComponents: includeComponents,
            includeChildren: GetBool(args, "includeChildren", false),
            childDepth: Math.Clamp(GetInt(args, "childDepth", 1), 0, 5),
            childLimit: Math.Clamp(GetInt(args, "childLimit", 50), 1, 500));
    }

    private object GetLocalPlayerItems(JsonElement args)
    {
        EnsureGameplayScene();
        var includeInactive = GetBool(args, "includeInactive", true);
        var category = (GetString(args, "category") ?? "all").Trim().ToLowerInvariant();
        var limit = Math.Clamp(GetInt(args, "limit", 50), 1, 500);
        var maxObjects = Math.Clamp(GetInt(args, "maxObjects", DefaultMaxScanObjects), 500, 50000);
        var root = GameObject.Find("LocalPlayer") ?? ResolveLocalPlayer(includeInactive, maxObjects);
        var results = EnumerateHierarchy(root, includeRoot: false)
            .Select(gameObject => new
            {
                GameObject = gameObject,
                Path = BuildPath(gameObject.transform),
            })
            .Where(entry => MatchesLocalPlayerItem(entry.GameObject, entry.Path, category))
            .OrderBy(entry => GetPathDepth(entry.Path))
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(entry => BuildGameObjectSnapshot(entry.GameObject, includeComponents: false))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["root"] = BuildGameObjectSnapshot(root, includeComponents: false),
            ["category"] = category,
            ["count"] = results.Count,
            ["maxObjects"] = maxObjects,
            ["results"] = results,
        };
    }

    private object ListPlayers(JsonElement args)
    {
        EnsureGameplayScene();
        var includeInactive = GetBool(args, "includeInactive", true);
        var limit = Math.Clamp(GetInt(args, "limit", 25), 1, 250);
        var maxObjects = Math.Clamp(GetInt(args, "maxObjects", DefaultMaxScanObjects), 500, 50000);
        var matches = EnumeratePlayerCandidates(includeInactive, limit, maxObjects)
            .Select(gameObject => BuildPlayerSnapshot(gameObject))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = matches.Count,
            ["maxObjects"] = maxObjects,
            ["results"] = matches,
        };
    }

    private object ListBoltEntities(JsonElement args)
    {
        var includeInactive = GetBool(args, "includeInactive", true);
        var limit = Math.Clamp(GetInt(args, "limit", 50), 1, 500);
        var maxObjects = Math.Clamp(GetInt(args, "maxObjects", DefaultMaxScanObjects), 500, 50000);
        var matches = EnumerateBoltEntityComponents(includeInactive, maxObjects)
            .Take(limit)
            .Select(BuildBoltEntitySnapshot)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = matches.Count,
            ["maxObjects"] = maxObjects,
            ["results"] = matches,
        };
    }

    private object ListComponents(JsonElement args)
    {
        var target = ResolveGameObject(args);
        var components = target.GetComponents<Component>()
            .Where(component => component != null)
            .Select(BuildComponentSummary)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["gameObject"] = BuildGameObjectSnapshot(target, includeComponents: false),
            ["components"] = components,
        };
    }

    private object ListChildren(JsonElement args)
    {
        var target = ResolveGameObject(args);
        var recursive = GetBool(args, "recursive", false);
        var maxDepth = Math.Clamp(GetInt(args, "maxDepth", recursive ? 3 : 1), 1, 8);
        var limit = Math.Clamp(GetInt(args, "limit", 100), 1, 1000);
        var contains = GetString(args, "contains");
        var includeInactive = GetBool(args, "includeInactive", true);

        var descendants = EnumerateHierarchy(target, includeRoot: false)
            .Select(child => new
            {
                GameObject = child,
                Path = BuildPath(child.transform),
                RelativeDepth = GetRelativeDepth(target.transform, child.transform),
            })
            .Where(entry => entry.RelativeDepth >= 1)
            .Where(entry => recursive || entry.RelativeDepth == 1)
            .Where(entry => entry.RelativeDepth <= maxDepth)
            .Where(entry => includeInactive || entry.GameObject.activeInHierarchy)
            .Where(entry => string.IsNullOrWhiteSpace(contains) ||
                            entry.GameObject.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            entry.Path.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(limit)
            .Select(entry => new Dictionary<string, object?>
            {
                ["depth"] = entry.RelativeDepth,
                ["gameObject"] = BuildGameObjectSnapshot(entry.GameObject, includeComponents: false),
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["root"] = BuildGameObjectSnapshot(target, includeComponents: false),
            ["recursive"] = recursive,
            ["maxDepth"] = maxDepth,
            ["count"] = descendants.Count,
            ["results"] = descendants,
        };
    }

    private object GetComponentMembers(JsonElement args)
    {
        var target = ResolveGameObject(args);
        var componentType = RequireString(args, "componentType");
        var includeNonPublic = GetBool(args, "includeNonPublic", false);
        var component = ResolveComponent(target, componentType);
        return new Dictionary<string, object?>
        {
            ["gameObject"] = BuildGameObjectSnapshot(target, includeComponents: false),
            ["component"] = BuildComponentSummary(component),
            ["includeNonPublic"] = includeNonPublic,
            ["members"] = SnapshotMembers(component, includeNonPublic),
        };
    }

    private object SetComponentMember(JsonElement args)
    {
        var target = ResolveGameObject(args);
        var componentType = RequireString(args, "componentType");
        var memberName = RequireString(args, "memberName");
        var component = ResolveComponent(target, componentType);
        var targetType = GetActualRuntimeType(component);
        var reflectionTarget = GetReflectionTarget(component, targetType);
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        var field = targetType.GetField(memberName, bindingFlags);
        var property = field == null ? targetType.GetProperty(memberName, bindingFlags) : null;

        if (field == null && property == null)
        {
            throw new InvalidOperationException($"Member '{memberName}' not found on '{targetType.FullName}'.");
        }

        if (!TryGetProperty(args, "value", out var rawValue))
        {
            throw new InvalidOperationException("Missing 'value' argument.");
        }

        object? converted;
        if (field != null)
        {
            converted = ConvertJsonToType(rawValue, field.FieldType);
            field.SetValue(reflectionTarget, converted);
        }
        else
        {
            if (property == null || !property.CanWrite)
            {
                throw new InvalidOperationException($"Property '{memberName}' is read-only.");
            }

            converted = ConvertJsonToType(rawValue, property.PropertyType);
            property.SetValue(reflectionTarget, converted);
        }

        return new Dictionary<string, object?>
        {
            ["gameObject"] = BuildGameObjectSnapshot(target, includeComponents: false),
            ["component"] = BuildComponentSummary(component),
            ["member"] = memberName,
            ["value"] = SerializeValue(field != null ? field.GetValue(reflectionTarget) : property!.GetValue(reflectionTarget)),
        };
    }

    private object InspectGameObject(JsonElement args)
    {
        var target = ResolveGameObject(args);
        var showExplorer = GetBool(args, "showExplorer", false);
        DebugTools.Inspect(target, showExplorer);
        return new Dictionary<string, object?>
        {
            ["inspected"] = true,
            ["showExplorer"] = showExplorer,
            ["gameObject"] = BuildGameObjectSnapshot(target, includeComponents: true),
        };
    }

    private object GetTypeLayout(JsonElement args)
    {
        var typeName = RequireString(args, "typeName");
        var includeNonPublic = GetBool(args, "includeNonPublic", true);
        var includeProperties = GetBool(args, "includeProperties", true);
        var targetType = ResolveLoadedTypeByName(typeName);
        if (targetType == null)
        {
            throw new InvalidOperationException($"Type '{typeName}' was not found among loaded assemblies.");
        }

        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if (includeNonPublic)
        {
            flags |= BindingFlags.NonPublic;
        }

        var fields = targetType.GetFields(flags)
            .Select(field =>
            {
                var nativeFieldPtr = TryGetNativeFieldPointer(targetType, field.Name);
                return new Dictionary<string, object?>
                {
                    ["name"] = field.Name,
                    ["type"] = field.FieldType.FullName ?? field.FieldType.Name,
                    ["isStatic"] = field.IsStatic,
                    ["visibility"] = field.IsPublic ? "public" : field.IsFamily ? "protected" : field.IsPrivate ? "private" : "nonPublic",
                    ["nativeFieldPointer"] = nativeFieldPtr == IntPtr.Zero ? null : nativeFieldPtr.ToInt64(),
                    ["offset"] = TryGetIl2CppFieldOffset(nativeFieldPtr),
                };
            })
            .OrderBy(entry => entry["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["requestedType"] = typeName,
            ["resolvedType"] = targetType.FullName ?? targetType.Name,
            ["assembly"] = targetType.Assembly.GetName().Name,
            ["fieldCount"] = fields.Count,
            ["fields"] = fields,
        };

        if (includeProperties)
        {
            var properties = targetType.GetProperties(flags)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(property => new Dictionary<string, object?>
                {
                    ["name"] = property.Name,
                    ["type"] = property.PropertyType.FullName ?? property.PropertyType.Name,
                    ["canRead"] = property.CanRead,
                    ["canWrite"] = property.CanWrite,
                })
                .OrderBy(entry => entry["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            payload["propertyCount"] = properties.Count;
            payload["properties"] = properties;
        }

        return payload;
    }

    private object GetFieldOffset(JsonElement args)
    {
        var typeName = RequireString(args, "typeName");
        var fieldName = RequireString(args, "fieldName");
        var targetType = ResolveLoadedTypeByName(typeName);
        if (targetType == null)
        {
            throw new InvalidOperationException($"Type '{typeName}' was not found among loaded assemblies.");
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;
        var field = targetType.GetField(fieldName, flags);
        var property = field == null ? targetType.GetProperty(fieldName, flags) : null;
        if (field == null && property == null)
        {
            throw new InvalidOperationException($"Field or property '{fieldName}' was not found on type '{targetType.FullName ?? targetType.Name}'.");
        }

        var canonicalName = field?.Name ?? property!.Name;
        var nativeFieldPtr = TryGetNativeFieldPointer(targetType, canonicalName);
        var offset = TryGetIl2CppFieldOffset(nativeFieldPtr);

        return new Dictionary<string, object?>
        {
            ["requestedType"] = typeName,
            ["resolvedType"] = targetType.FullName ?? targetType.Name,
            ["fieldName"] = canonicalName,
            ["memberKind"] = field != null ? "field" : "property",
            ["fieldType"] = field != null
                ? (field.FieldType.FullName ?? field.FieldType.Name)
                : (property!.PropertyType.FullName ?? property.PropertyType.Name),
            ["isStatic"] = field != null ? field.IsStatic : ((property!.GetMethod ?? property.SetMethod)?.IsStatic ?? false),
            ["nativeFieldPointer"] = nativeFieldPtr == IntPtr.Zero ? null : nativeFieldPtr.ToInt64(),
            ["offset"] = offset,
        };
    }

    private static bool Matches(GameObject go, string? exactName, string? contains)
    {
        if (!string.IsNullOrWhiteSpace(exactName) &&
            !string.Equals(go.name, exactName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(contains) &&
            go.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0 &&
            BuildPath(go.transform).IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

    private List<GameObject> EnumeratePlayerCandidates(bool includeInactive, int limit, int maxObjects = DefaultMaxScanObjects)
    {
        return EnumerateGameObjects(includeInactive, maxObjects)
            .Select(gameObject => new { GameObject = gameObject, Score = ScorePlayerCandidate(gameObject) })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.GameObject.name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(entry => entry.GameObject)
            .ToList();
    }

    private IEnumerable<Component> EnumerateBoltEntityComponents(bool includeInactive, int maxObjects = DefaultMaxScanObjects)
    {
        foreach (var gameObject in EnumerateGameObjects(includeInactive, maxObjects))
        {
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                var componentType = GetActualRuntimeType(component);
                if (string.Equals(componentType.Name, "BoltEntity", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(componentType.FullName, "BoltEntity", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(componentType.FullName, "Bolt.BoltEntity", StringComparison.OrdinalIgnoreCase))
                {
                    yield return component;
                }
            }
        }
    }

    private GameObject ResolveGameObject(JsonElement args)
    {
        var instanceId = GetNullableInt(args, "instanceId");
        if (instanceId.HasValue)
        {
            var byId = EnumerateGameObjects(includeInactive: true)
                .FirstOrDefault(go => go.GetInstanceID() == instanceId.Value);

            if (byId != null)
            {
                return byId;
            }

            throw new InvalidOperationException($"GameObject with instanceId={instanceId.Value} not found.");
        }

        var exactName = GetString(args, "name");
        var contains = GetString(args, "contains");
        var includeInactive = GetBool(args, "includeInactive", true);
        var match = EnumerateGameObjects(includeInactive)
            .FirstOrDefault(go => Matches(go, exactName, contains));

        if (match != null)
        {
            return match;
        }

        throw new InvalidOperationException("Unable to resolve GameObject from the supplied arguments.");
    }

    private Component ResolveComponent(GameObject target, string componentType)
    {
        var component = target.GetComponents<Component>()
            .FirstOrDefault(candidate =>
                candidate != null &&
                ComponentMatches(candidate, componentType));

        return component ?? throw new InvalidOperationException($"Component '{componentType}' not found on '{target.name}'.");
    }

    private List<object> SnapshotMembers(Component component, bool includeNonPublic = false)
    {
        var targetType = GetActualRuntimeType(component);
        var reflectionTarget = GetReflectionTarget(component, targetType);
        var results = new List<object>();
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | (includeNonPublic ? BindingFlags.NonPublic : 0);

        foreach (var field in targetType.GetFields(bindingFlags))
        {
            results.Add(new Dictionary<string, object?>
            {
                ["name"] = field.Name,
                ["kind"] = "field",
                ["type"] = field.FieldType.FullName,
                ["canWrite"] = !field.IsInitOnly,
                ["visibility"] = field.IsPublic ? "public" : field.IsFamily ? "protected" : field.IsPrivate ? "private" : "nonPublic",
                ["value"] = SerializeValue(SafeRead(() => field.GetValue(reflectionTarget))),
            });
        }

        foreach (var property in targetType.GetProperties(bindingFlags))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var getter = property.GetGetMethod(includeNonPublic);
            var setter = property.GetSetMethod(includeNonPublic);
            if (getter == null && setter == null)
            {
                continue;
            }

            results.Add(new Dictionary<string, object?>
            {
                ["name"] = property.Name,
                ["kind"] = "property",
                ["type"] = property.PropertyType.FullName,
                ["canWrite"] = property.CanWrite,
                ["visibility"] = getter?.IsPublic == true || setter?.IsPublic == true
                    ? "public"
                    : getter?.IsFamily == true || setter?.IsFamily == true
                        ? "protected"
                        : getter?.IsPrivate == true && setter?.IsPrivate != false || setter?.IsPrivate == true && getter?.IsPrivate != false
                            ? "private"
                            : "nonPublic",
                ["value"] = SerializeValue(SafeRead(() => property.CanRead ? property.GetValue(reflectionTarget) : "<unreadable>")),
            });
        }

        return results;
    }

    private static object? SafeRead(Func<object?> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    private Dictionary<string, object?> BuildGameObjectSnapshot(
        GameObject go,
        bool includeComponents,
        bool includeChildren = false,
        int childDepth = 1,
        int childLimit = 50)
    {
        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = go.GetInstanceID(),
            ["name"] = go.name,
            ["path"] = BuildPath(go.transform),
            ["scene"] = go.scene.name,
            ["activeSelf"] = go.activeSelf,
            ["activeInHierarchy"] = go.activeInHierarchy,
            ["tag"] = go.tag,
            ["layer"] = go.layer,
            ["position"] = SerializeValue(go.transform.position),
            ["rotation"] = SerializeValue(go.transform.rotation),
            ["childCount"] = go.transform.childCount,
        };

        if (includeComponents)
        {
            data["components"] = go.GetComponents<Component>()
                .Where(component => component != null)
                .Select(BuildComponentSummary)
                .ToList();
        }

        if (includeChildren && childDepth > 0)
        {
            data["children"] = BuildChildSnapshots(go.transform, childDepth, childLimit);
        }

        return data;
    }

    private Dictionary<string, object?> BuildPlayerSnapshot(GameObject go)
    {
        var components = go.GetComponents<Component>()
            .Where(component => component != null)
            .Select(component => GetActualRuntimeType(component).Name)
            .Where(name => name.Contains("Player", StringComparison.OrdinalIgnoreCase) || name.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(name => name)
            .ToList();

        var boltEntity = go.GetComponents<Component>()
            .FirstOrDefault(component => component != null && GetActualRuntimeType(component).Name.Contains("BoltEntity", StringComparison.OrdinalIgnoreCase));

        return new Dictionary<string, object?>
        {
            ["gameObject"] = BuildGameObjectSnapshot(go, includeComponents: false),
            ["matchingComponents"] = components,
            ["boltEntity"] = boltEntity != null ? BuildBoltEntitySnapshot(boltEntity) : null,
        };
    }

    private Dictionary<string, object?> BuildBoltEntitySnapshot(Component component)
    {
        var type = GetActualRuntimeType(component);
        return new Dictionary<string, object?>
        {
            ["component"] = BuildComponentSummary(component),
            ["gameObject"] = BuildGameObjectSnapshot(component.gameObject, includeComponents: false),
            ["networkId"] = SerializeValue(TryGetMemberValue(component, type, "networkId", "NetworkId", "netId")),
            ["prefabId"] = SerializeValue(TryGetMemberValue(component, type, "prefabId", "PrefabId")),
            ["isAttached"] = SerializeValue(TryGetMemberValue(component, type, "isAttached", "IsAttached")),
            ["isOwner"] = SerializeValue(TryGetMemberValue(component, type, "isOwner", "IsOwner")),
            ["hasControl"] = SerializeValue(TryGetMemberValue(component, type, "hasControl", "HasControl")),
        };
    }

    private Dictionary<string, object?> BuildComponentSummary(Component component)
    {
        var actualType = GetActualRuntimeType(component);
        var wrapperType = component.GetType();
        return new Dictionary<string, object?>
        {
            ["name"] = actualType.Name,
            ["fullName"] = actualType.FullName ?? actualType.Name,
            ["instanceId"] = component.GetInstanceID(),
            ["rawName"] = string.Equals(wrapperType.Name, actualType.Name, StringComparison.Ordinal) ? null : wrapperType.Name,
            ["rawFullName"] = string.Equals(wrapperType.FullName, actualType.FullName, StringComparison.Ordinal) ? null : wrapperType.FullName,
        };
    }

    private static bool ComponentMatches(Component component, string componentType)
    {
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return false;
        }

        var actualType = GetActualRuntimeType(component);
        var wrapperType = component.GetType();

        return MatchesTypeName(actualType, componentType) ||
               MatchesTypeName(wrapperType, componentType);
    }

    private static bool MatchesTypeName(Type type, string typeName)
    {
        return string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type.AssemblyQualifiedName, typeName, StringComparison.OrdinalIgnoreCase);
    }

    private static Type GetActualRuntimeType(object target)
    {
        if (target == null)
        {
            return typeof(object);
        }

        var fallback = target.GetType();

        try
        {
            var method = ResolveUniverseLibGetActualTypeMethod();
            if (method != null && method.Invoke(null, new[] { target }) is Type actualType && actualType != null)
            {
                return actualType;
            }
        }
        catch
        {
            // Fall back to the wrapper type if UniverseLib resolution is unavailable.
        }

        return fallback;
    }

    private static object GetReflectionTarget(object target, Type targetType)
    {
        if (target == null)
        {
            return target!;
        }

        try
        {
            var method = ResolveUniverseLibTryCastMethod();
            if (method != null)
            {
                var casted = method.Invoke(null, new[] { target, targetType });
                if (casted != null)
                {
                    return casted;
                }
            }
        }
        catch
        {
            // Fall back to the wrapper target if UniverseLib cast fails.
        }

        return target;
    }

    private static MethodInfo? ResolveUniverseLibGetActualTypeMethod()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? extensionsType;
            try
            {
                extensionsType = assembly.GetType("UniverseLib.ReflectionExtensions", throwOnError: false, ignoreCase: false);
            }
            catch
            {
                continue;
            }

            if (extensionsType == null)
            {
                continue;
            }

            var method = extensionsType.GetMethod(
                "GetActualType",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(object) },
                modifiers: null);

            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    private static MethodInfo? ResolveUniverseLibTryCastMethod()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? extensionsType;
            try
            {
                extensionsType = assembly.GetType("UniverseLib.ReflectionExtensions", throwOnError: false, ignoreCase: false);
            }
            catch
            {
                continue;
            }

            if (extensionsType == null)
            {
                continue;
            }

            var method = extensionsType.GetMethod(
                "TryCast",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(object), typeof(Type) },
                modifiers: null);

            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    private List<object> EnumerateLoadedScenes()
    {
        var scenes = new List<object>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            scenes.Add(new Dictionary<string, object?>
            {
                ["name"] = scene.name,
                ["isLoaded"] = scene.isLoaded,
                ["buildIndex"] = scene.buildIndex,
            });
        }

        return scenes;
    }

    private static void EnsureGameplayScene()
    {
        var scene = SceneManager.GetActiveScene().name;
        if (string.Equals(scene, "SonsTitleScene", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Command is only available after entering a loaded gameplay scene.");
        }
    }

    private GameObject ResolveLocalPlayer(bool includeInactive, int maxObjects = DefaultMaxScanObjects)
    {
        var exactByEnumeration = EnumerateGameObjects(includeInactive, maxObjects)
            .FirstOrDefault(gameObject => string.Equals(gameObject.name, "LocalPlayer", StringComparison.OrdinalIgnoreCase));
        if (exactByEnumeration != null)
        {
            return exactByEnumeration;
        }

        var match = EnumerateGameObjects(includeInactive, maxObjects)
            .Select(gameObject => new { GameObject = gameObject, Score = ScoreLocalPlayerCandidate(gameObject) })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.GameObject.name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (match != null)
        {
            return match.GameObject;
        }

        throw new InvalidOperationException("LocalPlayer was not found.");
    }

    private static int ScoreLocalPlayerCandidate(GameObject gameObject)
    {
        var score = 0;
        var path = BuildPath(gameObject.transform);
        var name = gameObject.name;

        if (string.Equals(name, "LocalPlayer", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }

        if (path.StartsWith("LocalPlayer/", StringComparison.OrdinalIgnoreCase))
        {
            score += 700;
        }

        if (path.Contains("/PlayerBase", StringComparison.OrdinalIgnoreCase))
        {
            score += 250;
        }

        if (path.Contains("/PlayerAnimator", StringComparison.OrdinalIgnoreCase))
        {
            score += 150;
        }

        if (name.Contains("player", StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (component == null)
            {
                continue;
            }

            var typeName = GetActualRuntimeType(component).Name;
            if (string.Equals(typeName, "LocalPlayer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "LocalPlayerController", StringComparison.OrdinalIgnoreCase))
            {
                score += 1000;
            }

            if (typeName.Contains("Player", StringComparison.OrdinalIgnoreCase))
            {
                score += 120;
            }

            if (typeName.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
            }
        }

        return score;
    }

    private static int ScorePlayerCandidate(GameObject gameObject)
    {
        var score = ScoreLocalPlayerCandidate(gameObject);
        var path = BuildPath(gameObject.transform);
        var name = gameObject.name;

        if (name.Contains("Player", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (path.Contains("/PlayerUI/", StringComparison.OrdinalIgnoreCase))
        {
            score -= 400;
        }

        if (path.Contains("/MessageList/", StringComparison.OrdinalIgnoreCase))
        {
            score -= 400;
        }

        if (path.Contains("PlayerMiniMap", StringComparison.OrdinalIgnoreCase))
        {
            score -= 400;
        }

        if (name.Contains("HistoryNode", StringComparison.OrdinalIgnoreCase))
        {
            score -= 400;
        }

        if (name.Contains("PlayerFireStimuli", StringComparison.OrdinalIgnoreCase))
        {
            score -= 500;
        }

        if (path.Contains("playerMarker", StringComparison.OrdinalIgnoreCase))
        {
            score -= 300;
        }

        if (path.Contains("PlayerStandin", StringComparison.OrdinalIgnoreCase))
        {
            score -= 350;
        }

        return score;
    }

    private static bool LooksLikePlayer(GameObject gameObject)
    {
        if (ScorePlayerCandidate(gameObject) > 0)
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<GameObject> EnumerateGameObjects(bool includeInactive, int maxObjects = int.MaxValue)
    {
        var source = includeInactive
            ? Resources.FindObjectsOfTypeAll<GameObject>()
            : UnityEngine.Object.FindObjectsOfType<GameObject>();
        var seen = new HashSet<int>();
        var yielded = 0;

        foreach (var go in source)
        {
            if (yielded >= maxObjects)
            {
                yield break;
            }

            if (go == null)
            {
                continue;
            }

            if (!go.scene.IsValid())
            {
                continue;
            }

            if (!seen.Add(go.GetInstanceID()))
            {
                continue;
            }

            yielded++;
            yield return go;
        }
    }

    private static IEnumerable<GameObject> EnumerateHierarchy(GameObject root, bool includeRoot)
    {
        if (includeRoot)
        {
            yield return root;
        }

        var stack = new Stack<Transform>();
        for (var i = root.transform.childCount - 1; i >= 0; i--)
        {
            stack.Push(root.transform.GetChild(i));
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == null)
            {
                continue;
            }

            yield return current.gameObject;

            for (var i = current.childCount - 1; i >= 0; i--)
            {
                stack.Push(current.GetChild(i));
            }
        }
    }

    private static string BuildPath(Transform transform)
    {
        var parts = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            parts.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", parts);
    }

    private List<object> BuildChildSnapshots(Transform root, int childDepth, int childLimit)
    {
        var remaining = childLimit;
        return BuildChildSnapshotsCore(root, childDepth, ref remaining);
    }

    private List<object> BuildChildSnapshotsCore(Transform root, int depthRemaining, ref int remaining)
    {
        var results = new List<object>();
        if (depthRemaining <= 0 || remaining <= 0)
        {
            return results;
        }

        for (var i = 0; i < root.childCount && remaining > 0; i++)
        {
            var child = root.GetChild(i);
            if (child == null)
            {
                continue;
            }

            remaining--;
            var entry = new Dictionary<string, object?>
            {
                ["instanceId"] = child.gameObject.GetInstanceID(),
                ["name"] = child.name,
                ["path"] = BuildPath(child),
                ["activeSelf"] = child.gameObject.activeSelf,
                ["activeInHierarchy"] = child.gameObject.activeInHierarchy,
                ["childCount"] = child.childCount,
            };

            if (depthRemaining > 1 && child.childCount > 0 && remaining > 0)
            {
                entry["children"] = BuildChildSnapshotsCore(child, depthRemaining - 1, ref remaining);
            }

            results.Add(entry);
        }

        return results;
    }

    private static bool MatchesLocalPlayerItem(GameObject gameObject, string path, string category)
    {
        var normalized = category switch
        {
            "held" or "weapon" => "held",
            "inventory" => "inventory",
            "equipment" or "clothing" or "gear" => "equipment",
            _ => "all",
        };

        var held = path.Contains("/rightHandHeld/", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/leftHandHeld/", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/RightHandWeapon/", StringComparison.OrdinalIgnoreCase) ||
                   gameObject.name.EndsWith("Held", StringComparison.OrdinalIgnoreCase);

        var inventory = path.Contains("/InventorySystem/", StringComparison.OrdinalIgnoreCase) &&
                        (gameObject.name.Contains("Item", StringComparison.OrdinalIgnoreCase) ||
                         gameObject.name.Contains("Inventory", StringComparison.OrdinalIgnoreCase) ||
                         gameObject.name.Contains("Backpack", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("/InventoryLayoutGroups/", StringComparison.OrdinalIgnoreCase));

        var equipment = path.Contains("/ClothingSystem/", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("/RaceSystem/", StringComparison.OrdinalIgnoreCase) ||
                        gameObject.name.Contains("Backpack", StringComparison.OrdinalIgnoreCase) ||
                        gameObject.name.Contains("Jacket", StringComparison.OrdinalIgnoreCase) ||
                        gameObject.name.Contains("Pants", StringComparison.OrdinalIgnoreCase) ||
                        gameObject.name.Contains("Boots", StringComparison.OrdinalIgnoreCase) ||
                        gameObject.name.Contains("Gloves", StringComparison.OrdinalIgnoreCase);

        return normalized switch
        {
            "held" => held,
            "inventory" => inventory,
            "equipment" => equipment,
            _ => held || inventory || equipment,
        };
    }

    private static int GetPathDepth(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? 0 : path.Count(ch => ch == '/');
    }

    private static int GetRelativeDepth(Transform root, Transform child)
    {
        var depth = 0;
        var current = child;
        while (current != null && current != root)
        {
            current = current.parent;
            depth++;
        }

        return current == root ? depth : int.MaxValue;
    }

    private static Type? FindLoadedType(params string[] typeNames)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var typeName in typeNames)
            {
                var exact = assembly.GetType(typeName, throwOnError: false, ignoreCase: true);
                if (exact != null)
                {
                    return exact;
                }
            }

            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.FullName == null)
                {
                    continue;
                }

                if (typeNames.Any(typeName =>
                    string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    type.FullName.EndsWith("." + typeName, StringComparison.OrdinalIgnoreCase)))
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static Type? ResolveLoadedTypeByName(string typeName)
    {
        var direct = FindLoadedType(typeName);
        if (direct != null)
        {
            return direct;
        }

        var normalized = typeName.Trim();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (string.Equals(type.FullName, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static IntPtr TryGetNativeFieldPointer(Type targetType, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return IntPtr.Zero;
        }

        var probeNames = new List<string>();
        if (fieldName.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal))
        {
            probeNames.Add(fieldName);
        }
        else
        {
            probeNames.Add($"NativeFieldInfoPtr_{fieldName}");
            probeNames.Add(fieldName);
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var current = targetType;
        while (current != null)
        {
            foreach (var probeName in probeNames)
            {
                var pointerField = current.GetField(probeName, flags);
                if (pointerField == null)
                {
                    continue;
                }

                try
                {
                    var raw = pointerField.GetValue(null);
                    var ptr = ConvertToIntPtr(raw);
                    if (ptr != IntPtr.Zero)
                    {
                        return ptr;
                    }
                }
                catch
                {
                }
            }

            current = current.BaseType;
        }

        return IntPtr.Zero;
    }

    private static int? TryGetIl2CppFieldOffset(IntPtr nativeFieldPointer)
    {
        if (nativeFieldPointer == IntPtr.Zero)
        {
            return null;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (!string.Equals(type.Name, "IL2CPP", StringComparison.Ordinal))
                {
                    continue;
                }

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(method => method.Name.Contains("field_get_offset", StringComparison.OrdinalIgnoreCase) ||
                                     method.Name.Contains("GetFieldOffset", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var method in methods)
                {
                    try
                    {
                        var parameters = method.GetParameters();
                        object? result = null;
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(IntPtr))
                        {
                            result = method.Invoke(null, new object[] { nativeFieldPointer });
                        }
                        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(long))
                        {
                            result = method.Invoke(null, new object[] { nativeFieldPointer.ToInt64() });
                        }
                        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(ulong))
                        {
                            result = method.Invoke(null, new object[] { unchecked((ulong)nativeFieldPointer.ToInt64()) });
                        }
                        else
                        {
                            continue;
                        }

                        if (result is int intOffset)
                        {
                            return intOffset;
                        }

                        if (result is long longOffset)
                        {
                            return unchecked((int)longOffset);
                        }

                        if (result is uint uintOffset)
                        {
                            return unchecked((int)uintOffset);
                        }

                        if (result is ulong ulongOffset)
                        {
                            return unchecked((int)ulongOffset);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        return null;
    }

    private static IntPtr ConvertToIntPtr(object? raw)
    {
        if (raw == null)
        {
            return IntPtr.Zero;
        }

        return raw switch
        {
            IntPtr ptr => ptr,
            long value => new IntPtr(value),
            ulong value => value <= long.MaxValue ? new IntPtr((long)value) : IntPtr.Zero,
            int value => new IntPtr(value),
            uint value => new IntPtr(unchecked((int)value)),
            _ => IntPtr.Zero,
        };
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }

    private static bool MatchesNetworkAssemblyName(string assemblyName)
    {
        return assemblyName.Contains("Bolt", StringComparison.OrdinalIgnoreCase) ||
               assemblyName.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase) ||
               assemblyName.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(assemblyName, "SonsSdk", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> ReadBoltNetworkState(Type? boltNetworkType)
    {
        if (boltNetworkType == null)
        {
            return new Dictionary<string, object?>
            {
                ["available"] = false,
            };
        }

        return new Dictionary<string, object?>
        {
            ["available"] = true,
            ["type"] = boltNetworkType.FullName,
            ["isRunning"] = TryGetBoolMember(null, boltNetworkType, "isRunning", "IsRunning"),
            ["isServer"] = TryGetBoolMember(null, boltNetworkType, "isServer", "IsServer"),
            ["isClient"] = TryGetBoolMember(null, boltNetworkType, "isClient", "IsClient"),
            ["isConnected"] = TryGetBoolMember(null, boltNetworkType, "isConnected", "IsConnected"),
            ["connectionsCount"] = TryGetCollectionCount(null, boltNetworkType, "connections", "Connections", "clients", "Clients"),
            ["server"] = SerializeValue(TryGetMemberValue(null, boltNetworkType, "server", "Server")),
            ["client"] = SerializeValue(TryGetMemberValue(null, boltNetworkType, "client", "Client")),
        };
    }

    private static List<object> SnapshotStaticMembers(Type type, int limit)
    {
        var results = new List<object>();

        foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var value = SafeRead(() => field.GetValue(null));
            var serialized = SerializeValue(value);
            if (!IsUsefulSnapshotValue(serialized))
            {
                continue;
            }

            results.Add(new Dictionary<string, object?>
            {
                ["name"] = field.Name,
                ["kind"] = "field",
                ["value"] = serialized,
            });

            if (results.Count >= limit)
            {
                return results;
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            var value = SafeRead(() => property.GetValue(null));
            var serialized = SerializeValue(value);
            if (!IsUsefulSnapshotValue(serialized))
            {
                continue;
            }

            results.Add(new Dictionary<string, object?>
            {
                ["name"] = property.Name,
                ["kind"] = "property",
                ["value"] = serialized,
            });

            if (results.Count >= limit)
            {
                return results;
            }
        }

        return results;
    }

    private static bool IsUsefulSnapshotValue(object? value)
    {
        return value != null && value is not string { Length: > 256 };
    }

    private static bool? TryGetBoolMember(object? target, Type? type, params string[] memberNames)
    {
        if (type == null)
        {
            return null;
        }

        var value = TryGetMemberValue(target, type, memberNames);
        return value switch
        {
            bool boolValue => boolValue,
            _ => null,
        };
    }

    private static int? TryGetCollectionCount(object? target, Type? type, params string[] memberNames)
    {
        if (type == null)
        {
            return null;
        }

        var value = TryGetMemberValue(target, type, memberNames);
        return value switch
        {
            Array array => array.Length,
            System.Collections.ICollection collection => collection.Count,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>().Count(),
            _ => null,
        };
    }

    private static object? TryGetMemberValue(object? target, Type? type, params string[] memberNames)
    {
        if (type == null)
        {
            return null;
        }

        var bindingFlags = (target == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        var reflectionTarget = target != null ? GetReflectionTarget(target, type) : null;
        foreach (var memberName in memberNames)
        {
            var property = type.GetProperty(memberName, bindingFlags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(reflectionTarget);
                }
                catch
                {
                }
            }

            var field = type.GetField(memberName, bindingFlags);
            if (field != null)
            {
                try
                {
                    return field.GetValue(reflectionTarget);
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static object? SerializeValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        switch (value)
        {
            case string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return value;
            case Enum enumValue:
                return enumValue.ToString();
            case Vector2 vector2:
                return new Dictionary<string, object?> { ["x"] = vector2.x, ["y"] = vector2.y };
            case Vector3 vector3:
                return new Dictionary<string, object?> { ["x"] = vector3.x, ["y"] = vector3.y, ["z"] = vector3.z };
            case Vector4 vector4:
                return new Dictionary<string, object?> { ["x"] = vector4.x, ["y"] = vector4.y, ["z"] = vector4.z, ["w"] = vector4.w };
            case Quaternion quaternion:
                return new Dictionary<string, object?> { ["x"] = quaternion.x, ["y"] = quaternion.y, ["z"] = quaternion.z, ["w"] = quaternion.w };
            case Color color:
                return new Dictionary<string, object?> { ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a };
            case UnityEngine.Object unityObject:
                var unityObjectType = GetActualRuntimeType(unityObject);
                return new Dictionary<string, object?>
                {
                    ["type"] = unityObjectType.FullName ?? unityObjectType.Name,
                    ["instanceId"] = unityObject.GetInstanceID(),
                    ["name"] = unityObject.name,
                };
            default:
                return value.ToString();
        }
    }

    private static object? ConvertJsonToType(JsonElement value, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
        }

        if (targetType == typeof(bool) || targetType == typeof(bool?))
        {
            return value.GetBoolean();
        }

        if (targetType == typeof(int) || targetType == typeof(int?))
        {
            return value.GetInt32();
        }

        if (targetType == typeof(float) || targetType == typeof(float?))
        {
            return value.GetSingle();
        }

        if (targetType == typeof(double) || targetType == typeof(double?))
        {
            return value.GetDouble();
        }

        if (targetType == typeof(long) || targetType == typeof(long?))
        {
            return value.GetInt64();
        }

        if (targetType.IsEnum)
        {
            return value.ValueKind == JsonValueKind.String
                ? Enum.Parse(targetType, value.GetString() ?? string.Empty, ignoreCase: true)
                : Enum.ToObject(targetType, value.GetInt32());
        }

        if (targetType == typeof(Vector3))
        {
            return new Vector3(
                GetRequiredFloat(value, "x"),
                GetRequiredFloat(value, "y"),
                GetRequiredFloat(value, "z"));
        }

        if (targetType == typeof(Vector2))
        {
            return new Vector2(
                GetRequiredFloat(value, "x"),
                GetRequiredFloat(value, "y"));
        }

        if (targetType == typeof(Color))
        {
            return new Color(
                GetRequiredFloat(value, "r"),
                GetRequiredFloat(value, "g"),
                GetRequiredFloat(value, "b"),
                GetFloat(value, "a", 1f));
        }

        throw new InvalidOperationException($"Setting values of type '{targetType.FullName}' is not supported yet.");
    }

    private static float GetRequiredFloat(JsonElement element, string name)
    {
        if (TryGetProperty(element, name, out var child))
        {
            return child.GetSingle();
        }

        throw new InvalidOperationException($"Missing numeric property '{name}'.");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }

            var snakeName = ToSnakeCase(name);
            if (!string.Equals(name, snakeName, StringComparison.Ordinal) &&
                element.TryGetProperty(snakeName, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
    }

    private static string RequireString(JsonElement element, string name)
    {
        var value = GetString(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required string argument '{name}'.");
        }

        return value;
    }

    private static int GetInt(JsonElement element, string name, int defaultValue)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : defaultValue;
    }

    private static int? GetNullableInt(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;
    }

    private static bool GetBool(JsonElement element, string name, bool defaultValue)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetBoolean()
            : defaultValue;
    }

    private static float GetFloat(JsonElement element, string name, float defaultValue)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetSingle()
            : defaultValue;
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var chars = new List<char>(name.Length + 8);
        foreach (var ch in name)
        {
            if (char.IsUpper(ch) && chars.Count > 0)
            {
                chars.Add('_');
            }

            chars.Add(char.ToLowerInvariant(ch));
        }

        return new string(chars.ToArray());
    }

    private void LogInfo(string message)
    {
        TryLog("Msg", "info", message);
    }

    private void LogError(string message)
    {
        TryLog("Error", "error", message);
    }

    private void TryLog(string methodName, string level, string message)
    {
        var line = $"[{DateTime.UtcNow:O}] [{level}] {message}";
        try
        {
            File.AppendAllText(_bridgeLogPath, line + Environment.NewLine);
        }
        catch
        {
        }

        try
        {
            var logger = LoggerInstance;
            var method = logger?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (method != null)
            {
                method.Invoke(logger, new object[] { message });
                return;
            }
        }
        catch
        {
        }

        Console.WriteLine($"[UnityExplorerBridge] {message}");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class BridgePipeServer
{
    private readonly UnityExplorerBridgeMod _mod;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _serverThread;
    private readonly PipeSecurity _pipeSecurity = BuildPipeSecurity();

    internal BridgePipeServer(UnityExplorerBridgeMod mod)
    {
        _mod = mod;
    }

    internal void Start()
    {
        _serverThread = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "UnityExplorerBridgePipe",
        };
        _serverThread.Start();
    }

    internal void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
        }
    }

    private void ServerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(
                    UnityExplorerBridgeMod.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None,
                    4096,
                    4096,
                    _pipeSecurity);
                server.WaitForConnection();

                using var reader = new StreamReader(server, new System.Text.UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                while (server.IsConnected && !_cts.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = reader.ReadLine();
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        break;
                    }

                    var response = HandleRequest(line);
                    try
                    {
                        writer.WriteLine(response);
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ioEx) when (ioEx.Message.Contains("Pipe is broken", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnityExplorerBridge] Pipe server error: {ex}");
                Thread.Sleep(250);
            }
        }
    }

    private string HandleRequest(string line)
    {
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(line);
        }
        catch (Exception ex)
        {
            return _mod.SerializeResponse(BridgeEnvelope.Failure(null, "invalid_json", ex.Message));
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Command))
        {
            return _mod.SerializeResponse(BridgeEnvelope.Failure(request?.Id, "invalid_request", "Command is required."));
        }

        if (string.Equals(request.Command, "ping", StringComparison.OrdinalIgnoreCase))
        {
            return _mod.SerializeResponse(BridgeEnvelope.Success(request.Id, new Dictionary<string, object?>
            {
                ["pong"] = true,
                ["bridgeVersion"] = UnityExplorerBridgeMod.BridgeVersion,
                ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
            }));
        }

        using var pending = new PendingRequest(request);
        _mod.Enqueue(pending);
        if (!pending.Signal.Wait(TimeSpan.FromMilliseconds(request.TimeoutMs <= 0 ? 5000 : request.TimeoutMs)))
        {
            return _mod.SerializeResponse(BridgeEnvelope.Failure(request.Id, "timeout", $"Command '{request.Command}' timed out."));
        }

        return pending.ResponseJson ?? _mod.SerializeResponse(BridgeEnvelope.Failure(request.Id, "empty_response", "No response was generated."));
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.SetAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.SetAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return pipeSecurity;
    }
}

internal sealed class PendingRequest : IDisposable
{
    internal PendingRequest(BridgeRequest request)
    {
        Request = request;
    }

    internal BridgeRequest Request { get; }
    internal ManualResetEventSlim Signal { get; } = new(false);
    internal string? ResponseJson { get; set; }

    public void Dispose()
    {
        Signal.Dispose();
    }
}

internal sealed class CachedEntry
{
    internal CachedEntry(object data, DateTime createdUtc)
    {
        Data = data;
        CreatedUtc = createdUtc;
    }

    internal object Data { get; }
    internal DateTime CreatedUtc { get; }
}

internal sealed class PlayerScanSession
{
    internal PlayerScanSession(List<GameObject> candidates, DateTime createdUtc)
    {
        Candidates = candidates;
        LastAccessUtc = createdUtc;
    }

    internal List<GameObject> Candidates { get; }
    internal int Cursor { get; set; }
    internal DateTime LastAccessUtc { get; set; }
}

internal sealed class BridgeRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 5000;

    [JsonPropertyName("args")]
    public JsonElement Args { get; set; }
}

internal sealed class BridgeEnvelope
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("details")]
    public object? Details { get; init; }

    public static BridgeEnvelope Success(string? id, object? data)
    {
        return new BridgeEnvelope
        {
            Id = id,
            Ok = true,
            Data = data,
        };
    }

    public static BridgeEnvelope Failure(string? id, string error, object? details = null, object? extra = null)
    {
        return new BridgeEnvelope
        {
            Id = id,
            Ok = false,
            Error = error,
            Details = extra ?? details,
        };
    }
}
