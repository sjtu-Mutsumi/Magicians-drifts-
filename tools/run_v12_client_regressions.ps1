param([string]$Godot = 'D:\Magicians!\_tools\Godot\4.7.2\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe')
$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspacePath = Split-Path -Parent $projectPath
$runtimePath = Join-Path $workspacePath 'runtime'
$tempPath = Join-Path $runtimePath 'v12-temp'
New-Item -ItemType Directory -Force $runtimePath, $tempPath | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspacePath '.nuget\packages'
$env:TEMP = $tempPath
$env:TMP = $tempPath
$pythonPath = $env:MAGICIANS_PYTHON
if (-not $pythonPath) {
    $pythonRoot = Join-Path $env:USERPROFILE 'AppData\Local\Programs\Python'
    $pythonPath = Get-ChildItem -LiteralPath $pythonRoot -Directory -Filter 'Python*' |
        Sort-Object FullName -Descending | ForEach-Object { Join-Path $_.FullName 'python.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $pythonPath) { throw 'Set MAGICIANS_PYTHON to a Python 3.12 executable.' }
Push-Location $projectPath
try {
    & dotnet build 'Magicians!.csproj' --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
    & $pythonPath -m unittest discover -s tools/sim_v12 -t .
    if ($LASTEXITCODE -ne 0) { throw 'Frozen v1.2 mechanism regressions failed.' }
    & $pythonPath -m unittest tools.client_v12.test_bridge tools.client_v12.test_match
    if ($LASTEXITCODE -ne 0) { throw 'v1.2 client protocol regressions failed.' }
    foreach ($verifier in @('tools/verify_v12.py')) {
        & $pythonPath $verifier
        if ($LASTEXITCODE -ne 0) { throw "Frozen release failed: $verifier" }
    }
    foreach ($runner in @('V12SinglePlayerRunner', 'V12LifecycleRunner', 'MainMenuFlowRunner', 'V12CardUiRunner', 'V12DeletionRunner', 'V12SelectionRunner')) {
        & $Godot --headless --log-file (Join-Path $runtimePath "$runner.log") --path $projectPath "res://Scenes/Tests/$runner.tscn"
        if ($LASTEXITCODE -ne 0) { throw "Godot regression failed: $runner" }
    }
    Write-Output 'V12_CLIENT_REGRESSIONS_OK'
} finally { Pop-Location }
