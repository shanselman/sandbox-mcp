$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\list-companion-profiles.ps1'
$one = 'S-1-5-110-1-2-3-1001'
$two = 'S-1-5-110-1-2-3-1002'
$three = 'S-1-5-110-1-2-3-1003'
$ordinary = 'S-1-5-21-1-2-3-1001'
$fixtureProfiles = @(
    [pscustomobject]@{ SID = $one; LocalPath = 'C:\Users\hidden-agent'; Loaded = $false; Special = $false },
    [pscustomobject]@{ SID = $two; LocalPath = 'C:\Users\active-agent'; Loaded = $false; Special = $false }
)
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($ClassName -ne 'Win32_UserProfile' -or $Filter -ne "SID LIKE 'S-1-5-110-%'") { throw 'Unexpected CIM query' }
    $fixtureProfiles
}
function Get-ChildItem {
    param($LiteralPath)
    foreach ($id in @($one, $two, $three, $ordinary)) {
        [pscustomobject]@{ PSChildName = $id; PSPath = "Registry::$id" }
    }
}
function Get-ItemProperty {
    param($LiteralPath)
    [pscustomobject]@{ ProfileImagePath = 'C:\Users\registry-only' }
}
function Get-Item {
    param($LiteralPath, [switch]$Force)
    if (-not $Force) { throw 'Hidden profiles must be included' }
    if ($LiteralPath -eq 'C:\Users\registry-only') { throw [UnauthorizedAccessException]::new('Synthetic metadata denial') }
    [pscustomobject]@{ Attributes = [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::Hidden }
}
function Test-Path {
    param($LiteralPath)
    $LiteralPath -eq "Registry::HKEY_USERS\$two"
}
$passed = 0
function Assert($Condition, $Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}

$rows = @(& $script -WarningAction SilentlyContinue)
Assert ($rows.Count -eq 3) 'Companion CIM and registry entries must be joined without including ordinary users'
$hidden = $rows | Where-Object SID -EQ $one
Assert ($hidden.Hidden -and $hidden.DirectoryExists) 'Hidden directories must be listed'
Assert ($hidden.State -eq 'Unloaded; ownership must be verified') 'Unloaded must not be presented as safe deletion'
$active = $rows | Where-Object SID -EQ $two
Assert ($active.State -eq 'In use' -and $active.HiveLoaded) 'A loaded hive must override a stale CIM unloaded flag'
$unknown = $rows | Where-Object SID -EQ $three
Assert (-not $unknown.CimPresent -and $null -eq $unknown.Loaded) 'Missing CIM data is unknown, not unloaded'
Assert ($null -eq $unknown.DirectoryExists -and $unknown.InspectionError) 'Denied metadata must not become a missing directory'
$json = & $script -Sid $one -AsJson | ConvertFrom-Json
Assert (@($json).Count -eq 1 -and $json[0].SID -eq $one) 'Exact SID filter and JSON array must agree'
$rejected = $false
try { & $script -Sid $ordinary | Out-Null } catch { $rejected = $_.Exception.Message -like '*only lists companion*' }
Assert $rejected 'An ordinary user SID must be rejected'
Write-Output "$passed companion-profile inventory assertions passed; all system access mocked, no mutations."
