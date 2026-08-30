[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Start', 'Status', 'Stop')]
    [string]$Action,
    [int]$SmtpPort = 2525,
    [int]$WebPort = 5000
)

# A local mail catcher is operator tooling, not product state. Keep the pinned binary and its SQLite mail
# store under the current user's LocalAppData rather than product/.local, where a delivery demonstration
# must never be confused with controlled evidence.
$ErrorActionPreference = 'Stop'
$version = '3.15.0'
$root = Join-Path $env:LOCALAPPDATA "AeroLink\smtp4dev\$version"
$tool = Join-Path $root ".store\rnwood.smtp4dev\$version\rnwood.smtp4dev.win-x64\$version\tools\net10.0\win-x64\Rnwood.Smtp4dev.exe"
$data = Join-Path $root 'messages.db'
$stdout = Join-Path $root 'smtp4dev.stdout.log'
$stderr = Join-Path $root 'smtp4dev.stderr.log'

function Get-OwnedSmtp4devProcess {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { return @() }
    $expected = [IO.Path]::GetFullPath($tool)
    return @(Get-CimInstance Win32_Process -Filter "Name='Rnwood.Smtp4dev.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $expected -and
            $_.CommandLine -and $_.CommandLine.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
}

function Get-Listener([int]$Port) {
    try {
        return [bool](Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    }
    catch { return $false }
}

switch ($Action) {
    'Start' {
        if ($SmtpPort -lt 1 -or $SmtpPort -gt 65535 -or $WebPort -lt 1 -or $WebPort -gt 65535) {
            throw 'SMTP and web ports must be between 1 and 65535.'
        }
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
            $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
            if (-not $dotnet) { throw 'smtp4dev 3.15.0 is not installed and dotnet was not found to install the local tool.' }
            Write-Host 'Installing pinned smtp4dev 3.15.0 into LocalAppData (one-time, no product files are changed)...'
            & $dotnet.Source tool install Rnwood.Smtp4dev --version $version --tool-path $root
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tool -PathType Leaf)) {
                throw 'The pinned smtp4dev tool installation failed. Check NuGet/network access; no AeroLink data was changed.'
            }
        }
        $owned = @(Get-OwnedSmtp4devProcess)
        if ($owned.Count -gt 0) {
            Write-Host "AeroLink smtp4dev is already running (PID $($owned[0].ProcessId)). SMTP: 127.0.0.1:$SmtpPort; inbox: http://127.0.0.1:$WebPort"
            exit 0
        }
        if ((Get-Listener $SmtpPort) -or (Get-Listener $WebPort)) {
            throw "Port $SmtpPort or $WebPort is already listening. Refusing to attach to or replace another process."
        }
        # This is an unauthenticated development catcher, so every protocol must be local-only and relay
        # must stay disabled. The web URL does not govern SMTP/IMAP/POP binding in smtp4dev.
        $arguments = "--allowremoteconnections- --bindaddress 127.0.0.1 --disableipv6+ --smtpport $SmtpPort --imapport= --pop3port= --relaysmtpserver= --urls http://127.0.0.1:$WebPort --db `"$data`" --locksettings+"
        $process = Start-Process -FilePath $tool -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        $deadline = (Get-Date).AddSeconds(15)
        do {
            if ($process.HasExited) { break }
            $smtpReady = Get-Listener $SmtpPort
            $webReady = Get-Listener $WebPort
            if ($smtpReady -and $webReady) { break }
            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $deadline)
        if ($process.HasExited -or -not $smtpReady -or -not $webReady) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            throw "smtp4dev did not begin listening on SMTP 127.0.0.1:$SmtpPort and web http://127.0.0.1:$WebPort within 15 seconds. Read $stdout and $stderr."
        }
        Write-Host "AeroLink smtp4dev ready. SMTP: 127.0.0.1:$SmtpPort; inbox: http://127.0.0.1:$WebPort; data: $data"
    }
    'Status' {
        $owned = @(Get-OwnedSmtp4devProcess)
        if ($owned.Count -gt 0 -and (Get-Listener $SmtpPort) -and (Get-Listener $WebPort)) {
            Write-Host "AEROLINK SMTP4DEV READY (PID $($owned[0].ProcessId), SMTP 127.0.0.1:$SmtpPort, inbox http://127.0.0.1:$WebPort)"
            exit 0
        }
        Write-Host 'AEROLINK SMTP4DEV NOT READY'
        exit 1
    }
    'Stop' {
        $owned = @(Get-OwnedSmtp4devProcess)
        if ($owned.Count -eq 0) { Write-Host 'No AeroLink-owned smtp4dev process is running.'; exit 0 }
        foreach ($process in $owned) { Stop-Process -Id $process.ProcessId -Force }
        $deadline = (Get-Date).AddSeconds(10)
        do {
            $stillOwned = @(Get-OwnedSmtp4devProcess)
            if ($stillOwned.Count -eq 0 -and -not (Get-Listener $SmtpPort) -and -not (Get-Listener $WebPort)) { break }
            Start-Sleep -Milliseconds 200
        } while ((Get-Date) -lt $deadline)
        if ($stillOwned.Count -gt 0 -or (Get-Listener $SmtpPort) -or (Get-Listener $WebPort)) {
            throw 'The AeroLink-owned smtp4dev process did not stop within 10 seconds.'
        }
        Write-Host 'AeroLink-owned smtp4dev stopped. Captured messages remain under LocalAppData until the operator removes them.'
    }
}
