param(
    [string]$GameRoot = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $projectRoot "bin\$Configuration"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $relativeGameRoot = "SteamLibrary\steamapps\common\Chill with You Lo-Fi Story"
    $GameRoot = [System.IO.DriveInfo]::GetDrives() |
        Where-Object { $_.DriveType -eq "Fixed" -and $_.IsReady } |
        ForEach-Object { Join-Path $_.RootDirectory $relativeGameRoot } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($GameRoot)) {
        throw "Game root was not found automatically. Pass -GameRoot with your Chill With You install folder."
    }
}

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) {
    throw "csc.exe was not found at $csc"
}

$managed = Join-Path $GameRoot "Chill With You_Data\Managed"
$bepinexCore = Join-Path $GameRoot "BepInEx\core"
$targetDll = Join-Path $outDir "ChillWithYou.BlackboardTodoImporter.dll"
$sourceFile = Join-Path $projectRoot "src\BlackboardTodoImporterPlugin.cs"
$bepInExDll = Join-Path $bepinexCore "BepInEx.dll"
$harmonyDll = Join-Path $bepinexCore "0Harmony.dll"
$unityEngineDll = Join-Path $managed "UnityEngine.dll"
$unityCoreDll = Join-Path $managed "UnityEngine.CoreModule.dll"
$unityInputDll = Join-Path $managed "UnityEngine.InputLegacyModule.dll"
$unityImguiDll = Join-Path $managed "UnityEngine.IMGUIModule.dll"
$newtonsoftDll = Join-Path $managed "Newtonsoft.Json.dll"
$netstandardDll = Join-Path $managed "netstandard.dll"

& $csc `
    /nologo `
    /target:library `
    /optimize+ `
    /debug:pdbonly `
    "/out:$targetDll" `
    "/reference:$bepInExDll" `
    "/reference:$harmonyDll" `
    "/reference:$unityEngineDll" `
    "/reference:$unityCoreDll" `
    "/reference:$unityInputDll" `
    "/reference:$unityImguiDll" `
    "/reference:$newtonsoftDll" `
    "/reference:$netstandardDll" `
    "$sourceFile"

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $targetDll"
