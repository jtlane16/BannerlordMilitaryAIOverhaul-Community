param(
    [string]$BannerlordDir = $env:BANNERLORD_DIR,
    [string]$PreviousModDll,
    [switch]$CampaignAcceptance,
    [string]$StageDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "MAIO-smoke-$([Guid]::NewGuid().ToString('N'))"),
    [string]$CaiDll
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BannerlordDir)) {
    throw 'Set BANNERLORD_DIR or pass -BannerlordDir pointing to the Bannerlord install root.'
}
$BannerlordDir = (Resolve-Path -LiteralPath $BannerlordDir).Path
$root = Split-Path -Parent $PSScriptRoot
$gameBin = Join-Path $BannerlordDir 'bin\Win64_Shipping_Client'
$harmonyBin = Join-Path $BannerlordDir 'Modules\Bannerlord.Harmony\bin\Win64_Shipping_Client'
$stage = (New-Item -ItemType Directory -Path $StageDirectory -Force).FullName

$previous = $null
if ($PreviousModDll) {
    # Capture the optional baseline BEFORE building the current source.
    $previous = Join-Path $stage 'previous\BannerlordMilitaryAIOverhaul.dll'
    New-Item -ItemType Directory -Path (Split-Path -Parent $previous) -Force | Out-Null
    Copy-Item -LiteralPath $PreviousModDll -Destination $previous
}

dotnet build "$root\BannerlordMilitaryAIOverhaul.csproj" -c Release --nologo "-p:BannerlordDir=$BannerlordDir" -p:DeployToGame=false "-p:CampaignAcceptance=$($CampaignAcceptance.IsPresent.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { throw 'Mod Release build failed.' }
Copy-Item -LiteralPath "$root\bin\Release\net472\BannerlordMilitaryAIOverhaul.dll" -Destination $stage
Copy-Item -LiteralPath "$PSScriptRoot\SmokeFixtures.cs", "$PSScriptRoot\SmokeRelief.cs", "$PSScriptRoot\SmokePerformance.cs", "$PSScriptRoot\SmokeFixtures.csproj" -Destination $stage
dotnet build "$stage\SmokeFixtures.csproj" -c Release --nologo "-p:BannerlordDir=$BannerlordDir"
if ($LASTEXITCODE -ne 0) { throw 'Local fixture build failed.' }

$folders = @($gameBin, $harmonyBin)
$resolveAssembly = {
    param($sender, $resolveArgs)
    $file = [System.Reflection.AssemblyName]::new($resolveArgs.Name).Name + '.dll'
    foreach ($directory in $folders) {
        $path = Join-Path $directory $file
        if ([System.IO.File]::Exists($path)) { return [System.Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
}.GetNewClosure()
$resolver = [System.ResolveEventHandler]$resolveAssembly
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $null = [System.Reflection.Assembly]::LoadFrom("$harmonyBin\0Harmony.dll")
    $campaign = [System.Reflection.Assembly]::LoadFrom("$gameBin\TaleWorlds.CampaignSystem.dll")
    if ($CaiDll) { $null = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $CaiDll).Path) }
    "CampaignSystem MVID: $($campaign.ManifestModule.ModuleVersionId)"
    $mod = [System.Reflection.Assembly]::LoadFrom("$stage\BannerlordMilitaryAIOverhaul.dll")
    $previousMod = $null
    if ($previous) {
        # LoadFile permits testing the previous assembly with the same display name.
        $previousMod = [System.Reflection.Assembly]::LoadFile($previous)
        "Testing old-to-new behavior data: $PreviousModDll"
    } else {
        'No previous DLL supplied; checking save contract with current writer only.'
    }

    $entry = $mod.GetType('BannerlordMilitaryAIOverhaul.SubModule', $true)
    $owner = $entry.GetField('PatchId', 'NonPublic,Static').GetRawConstantValue()
    if ($owner -cne 'BannerlordMilitaryAIOverhaul.Stage0') { throw "Unexpected Harmony owner: $owner" }
    $harmony = [HarmonyLib.Harmony]::new($owner)
    try {
        $harmony.PatchAll($mod)
        $expected = @(
            'Army.CheckArmyDispersion', 'Army.CheckInactivity', 'Army.DisperseInternal',
            'Army.MoveLeaderToGatheringLocationIfNeeded', 'Army.OnSettlementOwnerChanged',
            'Army.OnSiegeStarted', 'AiPartyThinkBehavior.PartyHourlyAiTick', 'Army.ThinkAboutCohesionBoost',
            'Army.OnAddPartyInternal', 'DefaultTargetScoreCalculatingModel.GetTargetScoreForFaction',
            'AiMilitaryBehavior.CalculateMilitaryBehaviorForFactionSettlements'
        )
        $actual = @($harmony.GetPatchedMethods() | ForEach-Object { "$($_.DeclaringType.Name).$($_.Name)" })
        if (Compare-Object $expected $actual) { throw "Unexpected native patch targets: $($actual -join ', ')" }
        "Installed the takeover hooks, membership debit guard and independent home/allied defense scoring under $owner"
        $null = [System.Reflection.Assembly]::LoadFrom("$stage\bin\Release\net472\SmokeFixtures.dll")
        try {
            [SmokeFixtures]::Run($mod, $harmony, $previousMod)
        } catch {
            throw $_.Exception.GetBaseException().ToString()
        }
    } finally {
        $harmony.UnpatchAll($owner)
        if (@($harmony.GetPatchedMethods()).Count -ne 0) { throw 'Smoke hook cleanup failed.' }
    }
    "Uninstalled all hooks. Local smoke artifacts: $stage"
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
