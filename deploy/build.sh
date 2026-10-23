#!/usr/bin/env bash
set -euo pipefail
task_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
: "${GAME_MANAGED_DIRECTORY:?Set GAME_MANAGED_DIRECTORY to SCPSL_Data/Managed}"
dotnet build "$task_root/ServerAdminIntegration.sln" -c Release -p:Platform=x64 "-p:GameManagedDirectory=$GAME_MANAGED_DIRECTORY"
dotnet publish "$task_root/ServerAdminBot/ServerAdminBot.csproj" -c Release -p:Platform=x64 -r linux-x64 --self-contained false -o "$task_root/artifacts/bot"
mkdir -p "$task_root/artifacts/plugins" "$task_root/artifacts/deploy"
cp "$task_root/ServerAdminIntegration/bin/x64/Release/ServerAdminIntegration.dll" "$task_root/artifacts/plugins/"
cp "$task_root/deploy/bootstrap.sql" "$task_root/deploy/serveradminbot.service" "$task_root/artifacts/deploy/"
cp "$task_root/ServerAdminBot/config.example.json" "$task_root/artifacts/deploy/bot.example.json"
cp "$task_root/ServerAdminIntegration/Config.example.yml" "$task_root/artifacts/deploy/plugin.example.yml"
printf 'Linux artifacts: %s/artifacts\n' "$task_root"
