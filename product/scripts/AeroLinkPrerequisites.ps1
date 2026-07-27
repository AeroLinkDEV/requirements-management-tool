# What a machine needs before AeroLink can start, and where to find it.
#
# Dot-sourced by both launchers. It exists because the launchers used to call `dotnet` and trust PATH, while
# `playwright.config.ts` and product/README.md had long since agreed that the SDK usually lives in the user's
# profile — the layout `dotnet-install.ps1` produces, which is the only way to install it on a machine where
# you are not an administrator. So the browser journeys found the SDK and the launcher did not.
#
# The checks run before anything slow. A work laptop with no SDK previously installed npm packages, compiled
# the whole client, started the API, waited two minutes for a health endpoint that would never answer, and only
# then printed "No .NET SDKs were found" — the right diagnosis, four minutes after it was knowable.

function Resolve-AeroLinkDotnet {
    <#
        .SYNOPSIS
        The dotnet executable to run AeroLink with, or a clear explanation of how to get one.
    #>
    # `dotnet` being present is not the same as an SDK being present: a machine with only the runtime, or with
    # the App Host shim Windows ships, answers `dotnet` and cannot run `dotnet run`. The SDK list decides, and a
    # version line is what a real SDK looks like.
    function Test-HasSdk([string]$path) {
        if (-not (Test-Path $path)) { return $false }
        $sdks = & $path --list-sdks 2>&1
        return $LASTEXITCODE -eq 0 -and [bool]($sdks | Where-Object { $_ -match '^\d+\.\d+\.\d+' })
    }

    # An explicit override that does not work is a misconfiguration, not a hint. Falling through to a different
    # dotnet would run the build against something the operator did not choose and say nothing about it.
    if ($env:AEROLINK_DOTNET) {
        if (Test-HasSdk $env:AEROLINK_DOTNET) { return $env:AEROLINK_DOTNET }
        throw "AEROLINK_DOTNET is set to '$($env:AEROLINK_DOTNET)', which is not a .NET SDK. Correct it or clear it."
    }

    # Every place a .NET SDK realistically lands on Windows, because there is no single one.
    #
    # `dotnet-install.ps1` — the only route on a machine where you are not an administrator — installs to
    # %LOCALAPPDATA%\Microsoft\dotnet on Windows. It is %USERPROFILE%\.dotnet that is the Linux and macOS
    # convention, and the first version of this file probed only that, because it was written on a machine
    # that happened to have the SDK there. It therefore missed the exact case it was written for.
    #
    # Neither location is added to PATH permanently: dotnet-install prints "Adding to current process PATH",
    # which lasts until that window closes. So a machine can have a working SDK that nothing on PATH knows
    # about, which is why these are probed directly rather than asked of the shell.
    $candidates = @()
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }
    if ($env:USERPROFILE) { $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    # Written out rather than with `?.`, because these scripts run under Windows PowerShell 5.1, where the
    # null-conditional operator is a parse error — and a parse error takes the whole file with it, so the
    # launcher fails before it can report anything useful.
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }

    foreach ($candidate in $candidates) { if (Test-HasSdk $candidate) { return $candidate } }

    $install = 'Invoke-WebRequest -UseBasicParsing ''https://dot.net/v1/dotnet-install.ps1'' -OutFile "$env:TEMP\dotnet-install.ps1"; & "$env:TEMP\dotnet-install.ps1" -Channel 10.0'
    throw @"
No .NET SDK was found, so AeroLink cannot be built or started.

Looked in:
$(($candidates | ForEach-Object { "  $_" }) -join [Environment]::NewLine)

Install the .NET 10 SDK into your user profile — this needs no administrator rights, which matters on a
managed machine:

  $install

On Windows it installs to %LOCALAPPDATA%\Microsoft\dotnet, and this launcher looks there directly — you do
not need to change PATH, and the "Adding to current process PATH" line it prints only lasts for that window.
If your organization publishes the SDK through its own software portal, that works equally well.
"@
}

function Assert-AeroLinkNode {
    <#
        .SYNOPSIS
        Confirms Node is available, because the client cannot be built without it.
    #>
    if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue) -and -not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw @"
Node.js was not found, so the client cannot be built.

Install Node.js 24 or later from https://nodejs.org, or through your organization's software portal, then run
this launcher again.
"@
    }
}
