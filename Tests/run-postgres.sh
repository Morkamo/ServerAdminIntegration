#!/usr/bin/env bash
set -euo pipefail
task_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
: "${GAME_MANAGED_DIRECTORY:?Set GAME_MANAGED_DIRECTORY to SCPSL_Data/Managed}"
if command -v pg_config >/dev/null; then
    export PATH="$(pg_config --bindir):$PATH"
fi
task_directory="$(mktemp -d -t server-admin-postgres-XXXXXXXX)"
task_started=0
cleanup() {
    if [[ "$task_started" == 1 ]]; then
        pg_ctl -D "$task_directory/data" -m fast -w stop || return
    fi
    rm -rf -- "$task_directory"
}
trap cleanup EXIT
task_port="${SERVERADMIN_TEST_PORT:-55439}"
export SERVERADMIN_DB_PASSWORD='test-server-admin-password'
initdb -D "$task_directory/data" -U postgres -A trust --no-locale --encoding=UTF8
pg_ctl -D "$task_directory/data" -l "$task_directory/postgres.log" -o "-p $task_port -h 127.0.0.1 -k $task_directory" -w start
task_started=1
psql -h 127.0.0.1 -p "$task_port" -U postgres -d postgres -v ON_ERROR_STOP=1 -f "$task_root/deploy/bootstrap.sql"
dotnet run --project "$task_root/Tests/Tests.csproj" -c Release -p:Platform=x64 "-p:GameManagedDirectory=$GAME_MANAGED_DIRECTORY" -- "$task_port"
