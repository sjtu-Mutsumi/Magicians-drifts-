param(
    [string]$ClientDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '_installers\Magicians-v1.2-Windows-20261030'),
    [string]$ShortcutName = 'Magicians!.lnk'
)
$ErrorActionPreference = 'Stop'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop $ShortcutName
$client = Join-Path $ClientDirectory 'Magicians.exe'
if (!(Test-Path -LiteralPath $client -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $ClientDirectory 'integrity.json'))) {
    throw 'Please select a complete extracted portable release with -ClientDirectory.'
}
$ClientDirectory = (Resolve-Path -LiteralPath $ClientDirectory).Path
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $ClientDirectory 'Magicians.exe'
$shortcut.Arguments = ''
$shortcut.WorkingDirectory = $ClientDirectory
$shortcut.IconLocation = $shortcut.TargetPath + ',0'
$shortcut.Description = '启动 Magicians! v1.2 正式客户端（内置 Python 与六风格 AI）'
$shortcut.Save()
Write-Output $shortcutPath
