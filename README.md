# sons-of-the-forest-mcp-api

RedLoader mod plus MCP server for runtime inspection and automation in Sons of the Forest.

The repository name describes the public project. The internal mod assembly is still named `UnityExplorerBridge` for compatibility with the existing RedLoader manifest, DLL name, and deployment flow.

## Contents

- `BridgeMod/` - C# RedLoader mod loaded by the game.
- `MCP_Server/` - Python MCP server that talks to the in-game bridge over the named pipe `unityexplorer_bridge`.

## Requirements

- Windows.
- Sons of the Forest installed through Steam.
- [RedLoader](https://github.com/ToniMacaroni/RedLoader) installed for Sons of the Forest.
- .NET SDK compatible with `net6`.
- Python 3.11+ recommended.
- Python packages from `requirements.txt`.

The default game path used by the project is:

```text
C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest
```

If your game is installed somewhere else, pass `GameDir` during build/deploy.

## RedLoader setup

Install RedLoader first, then confirm the game has a `_RedLoader` folder after setup. This project builds a RedLoader mod DLL and deploys it into the game's `Mods` folder.

Expected deploy layout:

```text
<GameDir>\Mods\UnityExplorerBridge.dll
<GameDir>\Mods\UnityExplorerBridge\manifest.json
```

The internal mod id remains `UnityExplorerBridge` for compatibility with the existing manifest and DLL name.

## Build the bridge mod

From the repository root:

```powershell
dotnet build .\BridgeMod\UnityExplorerBridge.csproj -c Debug /p:DisableCopyToGame=True
```

Output:

```text
BridgeMod\bin\Debug\net6\UnityExplorerBridge.dll
```

## Deploy to the game

Close the game before copying a new DLL, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\BridgeMod\deploy_bridge_update.ps1
```

For a custom install path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\BridgeMod\deploy_bridge_update.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Sons Of The Forest"
```

The deploy script copies:

```text
UnityExplorerBridge.dll -> <GameDir>\Mods\UnityExplorerBridge.dll
manifest.json          -> <GameDir>\Mods\UnityExplorerBridge\manifest.json
```

## Run the MCP server

Install Python dependencies:

```powershell
python -m pip install -r requirements.txt
```

Start the MCP server:

```powershell
python .\MCP_Server\mcp_unityexplorer.py
```

The game must be running with the bridge mod loaded before MCP tools can talk to the named pipe.

## MCP client configuration

The in-game bridge does not need an MCP JSON config by itself. The JSON config is only needed by the MCP client you want to use, so it can launch `MCP_Server/mcp_unityexplorer.py` over stdio.

Generic `mcpServers` example:

```json
{
  "mcpServers": {
    "sons-of-the-forest": {
      "command": "python",
      "args": [
        "C:\\path\\to\\sons-of-the-forest-mcp-api\\MCP_Server\\mcp_unityexplorer.py"
      ]
    }
  }
}
```

Replace `C:\\path\\to\\sons-of-the-forest-mcp-api` with the folder where you cloned this repository.

For Gemini CLI, the equivalent is:

```powershell
gemini mcp add sons-of-the-forest python C:\path\to\sons-of-the-forest-mcp-api\MCP_Server\mcp_unityexplorer.py
```

After configuring the client:

1. Start Sons of the Forest with RedLoader.
2. Confirm the `UnityExplorerBridge` mod is loaded.
3. Start or refresh your MCP client.
4. Run the `status` or `ping` tool first.

## MCP tools

The MCP server is registered as `unityexplorer` and exposes these tools:

| Tool | Purpose |
| --- | --- |
| `status` | Passive health check plus an optional probe to the RedLoader bridge. |
| `ping` | Ping the in-game RedLoader bridge. |
| `list_scenes` | List loaded Unity scenes from the running game. |
| `network_status` | Get current multiplayer/Bolt state from the running game. |
| `host_client_state` | Summarize whether the current session is host, server, client, or offline. |
| `current_lobby_info` | Return lobby and Steam-related static state snapshots when available. |
| `show_message` | Show an in-game toast message. |
| `run_game_command` | Execute a registered SonsSdk game command. |
| `get_type_layout` | Inspect a loaded IL2CPP/managed type and return members, native field pointers, and offsets when available. |
| `get_field_offset` | Return native field pointer and IL2CPP offset for a specific field when available. |
| `send_chat_message` | Broadcast a chat message via SonsSdk networking helpers. |
| `execute_many` | Execute multiple bridge commands in one roundtrip. |
| `find_gameobjects` | Find GameObjects by exact name or substring match. |
| `list_players` | Find likely player objects using name/component heuristics. |
| `players_scan_start` | Start a paged player scan session. |
| `players_scan_next` | Fetch the next page from a players scan session. |
| `players_scan_stop` | Stop and clean up a players scan session. |
| `get_local_player` | Return the best LocalPlayer match instead of a broad player heuristic list. |
| `get_local_player_items` | List LocalPlayer-related held items, inventory roots, or equipment. |
| `list_bolt_entities` | List BoltEntity-like components currently loaded in the game. |
| `get_gameobject` | Get a full snapshot for a GameObject instance id. |
| `inspect_gameobject` | Inspect a GameObject and optionally focus UnityExplorer on it. |
| `list_components` | List components attached to a GameObject. |
| `list_children` | List direct children or descendants of a GameObject. |
| `get_component_members` | Read component fields and properties. |
| `set_component_member` | Set a writable component field or property. |

## Notes

- Build output, copied DLLs, PDB files, Python cache, and local config files are ignored by `.gitignore`.
- This clean repository intentionally does not include local status files, generated build folders, or machine-specific paths from a developer profile.
