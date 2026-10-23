param([string]$PostgresBin = 'C:\Program Files\PostgreSQL\18\bin')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDirectory = Join-Path $taskRoot ('.test-postgres\' + [Guid]::NewGuid().ToString('N'))
$taskData = Join-Path $taskDirectory 'data'
$taskProcess = $null
$taskStopped = $true
$taskOldPassword = $env:SERVERADMIN_DB_PASSWORD
$env:SERVERADMIN_DB_PASSWORD = 'test-server-admin-password'
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
$taskListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$taskListener.Start()
$taskPort = $taskListener.LocalEndpoint.Port
$taskListener.Stop()
try {
    & (Join-Path $PostgresBin 'initdb.exe') -D $taskData -U postgres -A trust --no-locale --encoding=UTF8
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
    $taskProcess = Start-Process -FilePath (Join-Path $PostgresBin 'postgres.exe') -ArgumentList @('-D', ('"' + $taskData + '"'), '-p', $taskPort, '-h', '127.0.0.1') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskDirectory 'stdout.log') -RedirectStandardError (Join-Path $taskDirectory 'stderr.log') -PassThru
    $taskStopped = $false
    $taskReady = $false
    for ($taskAttempt = 0; $taskAttempt -lt 50; $taskAttempt++) {
        & (Join-Path $PostgresBin 'pg_isready.exe') -h 127.0.0.1 -p $taskPort -U postgres | Out-Null
        if ($LASTEXITCODE -eq 0) { $taskReady = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (!$taskReady) { throw 'Temporary PostgreSQL did not become ready' }
    & (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $taskPort -U postgres -d postgres -v ON_ERROR_STOP=1 -f (Join-Path $taskRoot 'deploy\bootstrap.sql')
    if ($LASTEXITCODE -ne 0) { throw 'Database bootstrap failed' }
    dotnet run --project (Join-Path $taskRoot 'Tests\Tests.csproj') -c Release -p:Platform=x64 -- $taskPort
    if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed' }
}
finally {
    $env:SERVERADMIN_DB_PASSWORD = $taskOldPassword
    if ($null -ne $taskProcess -and !$taskProcess.HasExited) {
        & (Join-Path $PostgresBin 'pg_ctl.exe') -D $taskData -m fast -w stop
        $taskStopped = $LASTEXITCODE -eq 0
        if ($taskStopped) { $taskProcess.WaitForExit() }
    }
    elseif ($null -ne $taskProcess) { $taskStopped = $true }
    $taskExpectedRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot '.test-postgres')) + [IO.Path]::DirectorySeparatorChar
    $taskResolvedDirectory = [IO.Path]::GetFullPath($taskDirectory)
    if ($taskStopped -and $taskResolvedDirectory.StartsWith($taskExpectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $taskResolvedDirectory -Recurse -Force
        $taskParent = Split-Path $taskResolvedDirectory -Parent
        if (!(Get-ChildItem -LiteralPath $taskParent -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $taskParent -Force }
    }
    elseif (!$taskStopped) { Write-Warning "Temporary PostgreSQL still running: $taskData" }
}
