param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$ExpectedHash,
    [ValidateSet('reuse-v1','relief-v1','allied-relief-v1','siege-v1','coordination-v1')][string]$Scenario = 'reuse-v1',
    [switch]$KeepPendingArm,
    [string]$BannerlordDir = $env:BANNERLORD_DIR
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BannerlordDir)) {
    throw 'Set BANNERLORD_DIR or pass -BannerlordDir pointing to the Bannerlord install root.'
}
$game = (Resolve-Path -LiteralPath $BannerlordDir).ProviderPath
$module = Join-Path $game 'Modules\BannerlordMilitaryAIOverhaul'
$name = 'BannerlordMilitaryAIOverhaul.dll'
$dest = Join-Path $module "bin\Win64_Shipping_Client\$name"
$source = Join-Path $Package $name
$markerName = "acceptance-$Scenario.armed"
$arm = Join-Path $module $markerName
$stagedArm = Join-Path $Package $markerName
$contents = [IO.File]::ReadAllText($stagedArm).Trim()
if ($contents -cnotmatch "^$Scenario ([0-9a-f]{32})$") { throw 'Invalid package arm marker.' }
$run = $Matches[1]
function Hash($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function AssertClosed {
    $running = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match 'Bannerlord|TaleWorlds|BLSE|^(Launcher|LauncherEx)\.exe$' -or
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith("$game\", [StringComparison]::OrdinalIgnoreCase))
    }
    if ($running) { throw ("No installation/arming while running: " + (($running | ForEach-Object {"$($_.Name) ($($_.ProcessId))"}) -join ', ')) }
}
function AssertUnlocked {
    $probe = [IO.File]::Open($dest, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $probe.Dispose()
}
if ((Hash $source) -ne $ExpectedHash) { throw 'Package DLL does not match the tested hash.' }
function AssertArmState {
    $markers = @(Get-ChildItem -LiteralPath $module -Filter 'acceptance-*-v1.armed')
    if (Get-ChildItem -LiteralPath $module -Filter "acceptance-*-v1.$run.*") {
        throw 'This run ID already has a receipt.'
    }
    if ($KeepPendingArm) {
        if ($markers.Count -ne 1 -or $markers[0].Name -cne $markerName -or (Hash $arm) -ne (Hash $stagedArm)) {
            throw 'Requested pending arm is not the exact, sole installed arm.'
        }
    } elseif ($markers.Count -ne 0) {
        throw 'An arm is already pending.'
    }
}
AssertClosed
AssertUnlocked
AssertArmState
$pendingDll = "$dest.$run.pending"
$pendingArm = "$arm.$run.pending"
try {
    [IO.File]::Copy($source, $pendingDll)
    if ((Hash $pendingDll) -ne $ExpectedHash) { throw 'Install candidate did not verify.' }
    AssertClosed
    AssertUnlocked
    AssertArmState
    [IO.File]::Replace($pendingDll, $dest, [NullString]::Value)
    if ((Hash $dest) -ne $ExpectedHash) { throw 'Installed DLL did not verify. Not armed.' }
    AssertClosed
    if (-not $KeepPendingArm) {
        [IO.File]::Copy($stagedArm, $pendingArm)
        [IO.File]::Move($pendingArm, $arm)
    }
    if ((Hash $arm) -ne (Hash $stagedArm)) { throw 'Installed marker did not verify.' }
    "Installed SHA256: $ExpectedHash"
    if ($KeepPendingArm) { "Preserved unclaimed acceptance arm: $run" }
    else { "Armed next acceptance campaign: $run" }
} finally {
    foreach ($pending in @($pendingDll, $pendingArm)) {
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    }
}
