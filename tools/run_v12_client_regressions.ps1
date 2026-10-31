param([string]$Godot = $env:MAGICIANS_GODOT)
$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Godot -or -not (Test-Path -LiteralPath $Godot -PathType Leaf)) {
    throw 'Pass -Godot <Godot .NET console executable>, or set MAGICIANS_GODOT.'
}
Push-Location $projectPath
try {
    & dotnet run --project 'tools/Magicians.Tooling/Magicians.Tooling.csproj' -- client-regressions --godot $Godot
    if ($LASTEXITCODE -ne 0) { throw 'C# client regressions failed. See runtime/client-regressions.' }
} finally { Pop-Location }
