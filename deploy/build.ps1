param([string]$GameManagedDirectory = 'C:\SteamLibrary\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
dotnet build (Join-Path $taskRoot 'ServerAdminIntegration.sln') -c Release -p:Platform=x64 "-p:GameManagedDirectory=$GameManagedDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed' }
dotnet publish (Join-Path $taskRoot 'ServerAdminBot\ServerAdminBot.csproj') -c Release -p:Platform=x64 -r linux-x64 --self-contained false -o (Join-Path $taskRoot 'artifacts\bot')
if ($LASTEXITCODE -ne 0) { throw 'Linux bot publish failed' }
$taskPlugins = Join-Path $taskRoot 'artifacts\plugins'
$taskDeploy = Join-Path $taskRoot 'artifacts\deploy'
New-Item -ItemType Directory -Path $taskPlugins, $taskDeploy -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'ServerAdminIntegration\bin\x64\Release\ServerAdminIntegration.dll') -Destination $taskPlugins -Force
foreach ($taskFile in @('bootstrap.sql', 'serveradminbot.service')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination $taskDeploy -Force
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'ServerAdminBot\config.example.json') -Destination (Join-Path $taskDeploy 'bot.example.json') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'ServerAdminIntegration\Config.example.yml') -Destination (Join-Path $taskDeploy 'plugin.example.yml') -Force
Write-Output "Linux artifacts: $(Join-Path $taskRoot 'artifacts')"
