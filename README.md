# sons-of-the-forest-mcp-api

RedLoader mod plus MCP server for runtime inspection and automation in Sons of the Forest.

The repository name describes the public project. The internal mod assembly is still named `UnityExplorerBridge` for compatibility with the existing RedLoader manifest, DLL name, and deployment flow.

## Contents

- `BridgeMod/` - C# RedLoader mod loaded by the game.
- `MCP_Server/` - Python MCP server that talks to the in-game bridge over the named pipe `unityexplorer_bridge`.

## Requirements

- Windows.
- Sons of the Forest installed through Steam.
- RedLoader installed for Sons of the Forest.
- .NET SDK compatible with `net6`.
- Python 3.11+ recommended.
- Python packages from `requirements.txt`.

The default game path used by the project is:

```text
C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest
```

If your game is installed somewhere else, pass `GameDir` during build/deploy.

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

## Notes

- Build output, copied DLLs, PDB files, Python cache, and local config files are ignored by `.gitignore`.
- This clean repository intentionally does not include local status files, generated build folders, or machine-specific paths from a developer profile.
