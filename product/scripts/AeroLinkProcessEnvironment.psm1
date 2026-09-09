function Get-AeroLinkProcessEnvironmentSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Name
    )

    $snapshot = @{}
    foreach ($item in $Name) {
        $value = [Environment]::GetEnvironmentVariable($item, 'Process')
        $snapshot[$item] = [pscustomobject]@{
            Present = $null -ne $value
            Value = $value
        }
    }
    return $snapshot
}

function Restore-AeroLinkProcessEnvironmentSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Snapshot
    )

    foreach ($entry in $Snapshot.GetEnumerator()) {
        if ($entry.Value.Present) {
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value.Value, 'Process')
            continue
        }

        # PowerShell 7 leaves a process variable present-but-empty when SetEnvironmentVariable is
        # called with $null. Removing the environment entry is the only exact representation of the
        # original absence and works in both supported Windows PowerShell engines.
        $path = "Env:\$($entry.Key)"
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -ErrorAction Stop
        }
    }
}

Export-ModuleMember -Function Get-AeroLinkProcessEnvironmentSnapshot, Restore-AeroLinkProcessEnvironmentSnapshot
