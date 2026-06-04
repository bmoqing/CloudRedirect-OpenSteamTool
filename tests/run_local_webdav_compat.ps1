param(
    [int]$Port = 18987,
    [string]$Username = 'cloudredirect',
    [string]$Password = 'cloudredirect'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$serverScript = Join-Path $PSScriptRoot 'local_webdav_server.py'
$smokeScript = Join-Path $PSScriptRoot 'webdav_compat_smoke.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('cloudredirect-local-webdav-' + [Guid]::NewGuid().ToString('N'))
$stdoutLog = Join-Path $root 'server.out.log'
$stderrLog = Join-Path $root 'server.err.log'

function Find-Python {
    $candidates = @(
        (Join-Path $repoRoot '.tools\python\python.exe'),
        'python',
        'py'
    )

    foreach ($candidate in $candidates) {
        try {
            $cmd = Get-Command $candidate -ErrorAction Stop
            return $cmd.Source
        }
        catch {
        }
    }

    throw 'Python was not found. Install Python 3 or put python.exe on PATH.'
}

New-Item -ItemType Directory -Path $root | Out-Null
$python = Find-Python
$baseUrl = "http://127.0.0.1:$Port/dav"

$args = @(
    $serverScript,
    '--host', '127.0.0.1',
    '--port', [string]$Port,
    '--root', $root,
    '--username', $Username,
    '--password', $Password
)

$server = $null
try {
    $server = Start-Process -FilePath $python `
        -ArgumentList $args `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -PassThru `
        -WindowStyle Hidden

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $resp = Invoke-WebRequest -Uri $baseUrl -Method Options -UseBasicParsing -TimeoutSec 1
            if ($resp.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    if (-not $ready) {
        throw "Local WebDAV server did not start. Logs: $stdoutLog / $stderrLog"
    }

    & $smokeScript -BaseUrl $baseUrl -Username $Username -Password $Password -AllowHttpForLocal
}
finally {
    if ($server -and -not $server.HasExited) {
        $server.Kill()
        $server.WaitForExit()
    }
    Write-Output "Local WebDAV test root left for manual cleanup: $root"
}
