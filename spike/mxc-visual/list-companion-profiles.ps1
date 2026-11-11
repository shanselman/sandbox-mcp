<#
.SYNOPSIS
Lists companion user profiles, not running Windows sessions. Read-only.
.DESCRIPTION
Includes CIM and ProfileList entries in the observed S-1-5-110 SID family.
Unloaded does not mean orphaned or safe to delete. No profile contents or ACLs
are read, and no accounts, sessions, hives, directories or settings are changed.
#>
[CmdletBinding()]
param(
    [string]$Sid,
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
$prefix = 'S-1-5-110-'
if ($Sid) {
    $Sid = [Security.Principal.SecurityIdentifier]::new($Sid).Value
    if (-not $Sid.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw 'This helper only lists companion S-1-5-110 profiles, not ordinary user profiles.'
    }
}
$profiles = @{}
foreach ($profile in Get-CimInstance Win32_UserProfile -Filter "SID LIKE 'S-1-5-110-%'") {
    if (-not $Sid -or $profile.SID -eq $Sid) { $profiles[$profile.SID] = $profile }
}
$registered = @{}
$profileList = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
foreach ($key in Get-ChildItem -LiteralPath $profileList) {
    if ($key.PSChildName.StartsWith($prefix, [StringComparison]::Ordinal) -and (-not $Sid -or $key.PSChildName -eq $Sid)) {
        $registered[$key.PSChildName] = Get-ItemProperty -LiteralPath $key.PSPath
    }
}
$ids = @(@($profiles.Keys) + @($registered.Keys) | Sort-Object -Unique)
if ($Sid -and $ids.Count -eq 0) { throw "No companion profile found for SID $Sid" }
$rows = @(
    foreach ($id in $ids) {
        $profile = $profiles[$id]
        $registry = $registered[$id]
        $profilePath = if ($profile) { $profile.LocalPath } else { $registry.ProfileImagePath }
        if ($profilePath) { $profilePath = [Environment]::ExpandEnvironmentVariables($profilePath) }
        $exists = $null; $hidden = $null; $inspectionError = $null
        if (-not $profilePath -or $profilePath -notmatch '^[A-Za-z]:\\') {
            $inspectionError = 'Missing or non-local profile path; directory not inspected.'
        } else {
            try {
                $directory = Get-Item -LiteralPath $profilePath -Force
                $exists = $true
                $hidden = [bool]($directory.Attributes -band [IO.FileAttributes]::Hidden)
            } catch [System.Management.Automation.ItemNotFoundException] {
                $exists = $false
            } catch [System.UnauthorizedAccessException], [System.Security.SecurityException] {
                $inspectionError = $_.Exception.Message
            }
        }
        $hiveLoaded = Test-Path -LiteralPath ("Registry::HKEY_USERS\" + $id)
        $state = if ($hiveLoaded -or ($profile -and $profile.Loaded)) { 'In use' }
            elseif (-not $profile) { 'Registry only; load state unverified' }
            elseif ($profile.Special) { 'Special profile; do not remove' }
            else { 'Unloaded; ownership must be verified' }
        if ($inspectionError) { Write-Warning "$id : $inspectionError" }
        [pscustomobject][ordered]@{
            SID = $id
            ProfilePath = $profilePath
            State = $state
            Loaded = $(if ($profile) { [bool]$profile.Loaded } else { $null })
            HiveLoaded = [bool]$hiveLoaded
            Special = $(if ($profile) { [bool]$profile.Special } else { $null })
            CimPresent = [bool]$profile
            ProfileListPresent = [bool]$registry
            DirectoryExists = $exists
            Hidden = $hidden
            InspectionError = $inspectionError
        }
    }
)
if ($AsJson) { ConvertTo-Json -InputObject $rows -Depth 4 }
else { $rows }
