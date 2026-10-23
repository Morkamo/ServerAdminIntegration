param(
    [string]$GameManagedDirectory = 'C:\SteamLibrary\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed'
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$taskBuildArguments = @(
    'build', (Join-Path $taskRoot 'ServerAdminIntegration.sln')
    '-c', 'Release'
    '-p:Platform=x64'
    "-p:GameManagedDirectory=$GameManagedDirectory"
)
dotnet @taskBuildArguments

if ($LASTEXITCODE -ne 0)
{
    throw 'Solution build failed'
}

$taskPublishArguments = @(
    'publish', (Join-Path $taskRoot 'ServerAdminBot\ServerAdminBot.csproj')
    '-c', 'Release'
    '-p:Platform=x64'
    '-r', 'linux-x64'
    '--self-contained', 'false'
    '-o', (Join-Path $taskRoot 'artifacts\bot')
)
dotnet @taskPublishArguments

if ($LASTEXITCODE -ne 0)
{
    throw 'Linux bot publish failed'
}

$taskPlugins = Join-Path $taskRoot 'artifacts\plugins'
$taskDeploy = Join-Path $taskRoot 'artifacts\deploy'
New-Item -ItemType Directory -Path $taskPlugins, $taskDeploy -Force | Out-Null

$taskPluginPath = Join-Path $taskRoot 'ServerAdminIntegration\bin\x64\Release\ServerAdminIntegration.dll'
Copy-Item -LiteralPath $taskPluginPath -Destination $taskPlugins -Force

foreach ($taskFile in @('bootstrap.sql', 'serveradminbot.service'))
{
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination $taskDeploy -Force
}

$taskBotConfig = Join-Path $taskRoot 'ServerAdminBot\config.example.json'
$taskPluginConfig = Join-Path $taskRoot 'ServerAdminIntegration\Config.example.yml'
Copy-Item -LiteralPath $taskBotConfig -Destination (Join-Path $taskDeploy 'bot.example.json') -Force
Copy-Item -LiteralPath $taskPluginConfig -Destination (Join-Path $taskDeploy 'plugin.example.yml') -Force

Write-Output "Linux artifacts: $(Join-Path $taskRoot 'artifacts')"
