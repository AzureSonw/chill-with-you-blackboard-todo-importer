# Build Guide

## Build With PowerShell

```powershell
.\build.ps1
```

Custom game path:

```powershell
.\build.ps1 -GameRoot "SteamLibrary\steamapps\common\Chill with You Lo-Fi Story"
```

Output:

```text
bin\Release\ChillWithYou.BlackboardTodoImporter.dll
```

## Build With dotnet

If you have a .NET SDK and .NET Framework targeting support:

```powershell
dotnet build -c Release
```

If your game is not installed in a standard Steam library folder, pass `-GameRoot` to `build.ps1` or set `GameRoot` when running `dotnet build`.

## References

The project references game/BepInEx assemblies from:

```text
<GameRoot>\BepInEx\core
<GameRoot>\Chill With You_Data\Managed
```
