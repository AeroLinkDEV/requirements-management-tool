#Requires -Version 5.1
<#
    Read-only migration-posture probe used by Get-AeroLinkDiagnostics.ps1.

    Windows PowerShell 5.1 mangles embedded double quotes when they travel inside a
    native-command argument (for example psql -tAc 'SELECT ... "__EFMigrationsHistory"'),
    so PostgreSQL receives the lowercase identifier and the health check reports a
    false negative. This helper sends the SQL over stdin instead, which preserves the
    exact bytes on both Windows PowerShell 5.1 and PowerShell 7.

    The query is strictly read-only: it only counts rows in the EF migrations history
    table and never creates, alters, or deletes database objects.
#>
function Get-AeroLinkMigrationCount {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PsqlPath,
        [string]$DatabaseHost = '127.0.0.1',
        [int]$DatabasePort = 54329,
        [string]$DatabaseUser = 'postgres',
        [string]$DatabaseName = 'aerolink'
    )

    if (-not (Test-Path -LiteralPath $PsqlPath -PathType Leaf)) {
        return [pscustomobject]@{
            Healthy = $false
            Count = $null
            Detail = 'psql.exe was not found under the configured AeroLink runtime.'
        }
    }

    # The exact read-only query. The mixed-case quoted identifier is what the false
    # negative was about; it must reach psql byte-for-byte, so it is piped via stdin.
    $sql = 'SELECT COUNT(*) FROM "__EFMigrationsHistory"'

    try {
        $output = $sql | & $PsqlPath -X -h $DatabaseHost -p $DatabasePort -U $DatabaseUser -d $DatabaseName -tA -q 2>$null
        $exitCode = $LASTEXITCODE
    }
    catch {
        return [pscustomobject]@{
            Healthy = $false
            Count = $null
            Detail = "Migration history query failed: $($_.Exception.GetType().Name)"
        }
    }

    if ($exitCode -ne 0) {
        return [pscustomobject]@{
            Healthy = $false
            Count = $null
            Detail = "psql exited $exitCode while querying __EFMigrationsHistory"
        }
    }

    $lastLine = $output | Select-Object -Last 1
    $countText = if ($null -eq $lastLine) { '' } else { $lastLine.ToString().Trim() }
    if ($countText -notmatch '^\d+$') {
        return [pscustomobject]@{
            Healthy = $false
            Count = $null
            Detail = 'Migration history query returned a non-numeric result.'
        }
    }

    return [pscustomobject]@{
        Healthy = $true
        Count = [int]$countText
        Detail = "$countText applied migration(s)"
    }
}

Export-ModuleMember -Function Get-AeroLinkMigrationCount
