param(
    [string]$Log = (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'BannerlordMilitaryAIOverhaul.log'),
    [string]$Session = '3f62050c',
    [string]$RunId = '23eb911b494549a98cce30bdeac35fdb',
    [string]$Kingdom = 'empire_s',
    [string]$PreviousTarget = 'town_ES3',
    [string]$Target = 'town_ES4',
    [string]$PreviousHour = '2214936.13',
    [string]$TransferHour = '2214959.19',
    [string]$Receipt
)

$ErrorActionPreference = 'Stop'
$sessionRx = [regex]::Escape($Session)
$runRx = [regex]::Escape($RunId)
$kingRx = [regex]::Escape($Kingdom)
$oldRx = [regex]::Escape($PreviousTarget)
$targetRx = [regex]::Escape($Target)
$sha = (Get-FileHash -LiteralPath $Log -Algorithm SHA256).Hash
$rows = @(Select-String -LiteralPath $Log -Pattern "session=$sessionRx campaignHour=")

function Assert-Evidence($condition, $message) {
    if (-not $condition) { throw "Evidence not established: $message" }
}

function ByHour($hour) {
    $prefix = 'campaignHour=' + [regex]::Escape($hour) + ' '
    $rows | Where-Object { $_.Line -match $prefix }
}

function ArmyIds($departures) {
    foreach ($row in $departures) {
        Assert-Evidence ($row.Line -match '\barmy=(\S+) ') 'missing native departure army ID'
        $Matches[1]
    }
}

function Show-Evidence($items) {
    foreach ($row in @($items | Sort-Object LineNumber -Unique)) {
        '{0}: {1}' -f $row.LineNumber, $row.Line
    }
}

$arm = @($rows | Where-Object { $_.Line -match "Acceptance run=$runRx armed scenario=coordination-v1" })
$verdict = @($rows | Where-Object { $_.Line -match "Acceptance final: Run ${runRx}: INCONCLUSIVE:" })
Assert-Evidence ($arm.Count -eq 1 -and $verdict.Count -eq 1) 'unique run arm and original inconclusive log verdict'
$prior = @(ByHour $PreviousHour)
$current = @(ByHour $TransferHour)
$oldOrders = @($prior | Where-Object { $_.Line -match "Relief departure: kingdom=$kingRx target=$oldRx .*order=DefendSettlement orderTarget=$oldRx " })
$newOrders = @($current | Where-Object { $_.Line -match "Relief departure: kingdom=$kingRx target=$targetRx .*order=DefendSettlement orderTarget=$targetRx " })
$oldIds = @(ArmyIds $oldOrders)
$newIds = @(ArmyIds $newOrders)
Assert-Evidence ($newIds.Count -ge 2 -and @($newIds | Sort-Object -Unique).Count -eq $newIds.Count -and
    $oldIds.Count -eq $newIds.Count -and -not (Compare-Object $oldIds $newIds)) 'same distinct armies have native defend orders in both passes'
$choice = @($current | Where-Object { $_.Line -match "Relief response selected: kingdom=$kingRx target=$targetRx .*formations=\[.*\].*why=send-available-understrength" })
$oldOpen = @($prior | Where-Object { $_.Line -match "Relief objective opened: kingdom=$kingRx target=$oldRx scope=home .*execution=native-land-defense" })
$newOpen = @($current | Where-Object { $_.Line -match "Relief objective opened: kingdom=$kingRx target=$targetRx scope=home .*execution=native-land-defense" })
$oldClaim = @($prior | Where-Object { $_.Line -match "Coordination commitment: kingdom=$kingRx defense=$oldRx action=joint-response .* duplicate=\[\]" })
$claim = @($current | Where-Object { $_.Line -match "Coordination commitment: kingdom=$kingRx defense=$targetRx action=joint-response .* duplicate=\[\]" })
$review = @($current | Where-Object { $_.Line -match "Relief review: kingdom=$kingRx target=$targetRx rank=1/\d+ scope=home .*why=understrength-response-open" })
$lower = @($current | Where-Object { $_.Line -match "Relief review: kingdom=$kingRx target=$oldRx rank=[2-9]\d*/\d+ scope=home .*committed=0 .*committed-higher-priority:$($newIds.Count)(?:,|\])" })
Assert-Evidence ($oldOpen.Count -eq 1 -and $newOpen.Count -eq 1 -and $oldClaim.Count -eq 1 -and $claim.Count -eq 1 -and
    $choice.Count -eq 1 -and $review.Count -eq 1 -and $lower.Count -eq 1) 'expected openings, joint snapshots and priority reviews'
Assert-Evidence ($arm[0].LineNumber -lt $oldOpen[0].LineNumber -and $oldClaim[0].LineNumber -lt $newOpen[0].LineNumber -and
    $newOpen[0].LineNumber -lt $choice[0].LineNumber -and $review[0].LineNumber -lt $lower[0].LineNumber -and
    $lower[0].LineNumber -lt $verdict[0].LineNumber) 'chronological run and allocator boundaries'
if ($choice[0].Line -match 'formations=\[([^\]]+)\]') { $chosen = @($Matches[1] -split ', ') }
Assert-Evidence ($chosen.Count -eq $newIds.Count -and -not (Compare-Object $chosen $newIds)) 'the selection and departures name exactly the same formation set'
$allParties = @()
$transferRows = @()
$routeRows = @()
foreach ($army in $newIds) {
    $idRx = [regex]::Escape($army)
    $priorOrder = @($oldOrders | Where-Object { $_.Line -match "army=$idRx " })
    $newOrder = @($newOrders | Where-Object { $_.Line -match "army=$idRx " })
    $reuse = @($current | Where-Object { $_.Line -match "Relief reuses formation: kingdom=$kingRx target=$targetRx army=$idRx .*previous=$oldRx .*firstETA=\+\d" })
    $oldRoute = @($rows | Where-Object { $_.LineNumber -gt $priorOrder[0].LineNumber -and $_.LineNumber -lt $choice[0].LineNumber -and
        $_.Line -match "Relief execution: kingdom=$kingRx target=$oldRx army=$idRx phase=en-route " })
    Assert-Evidence ($reuse.Count -eq 1 -and $reuse[0].LineNumber -gt $choice[0].LineNumber -and
        $reuse[0].LineNumber -lt $newOrder[0].LineNumber -and $newOrder[0].LineNumber -lt $claim[0].LineNumber -and
        $priorOrder[0].LineNumber -gt $oldOpen[0].LineNumber -and $priorOrder[0].LineNumber -lt $oldClaim[0].LineNumber -and
        $priorOrder[0].Line -match 'routeETA=\+\d' -and $newOrder[0].Line -match 'routeETA=\+\d' -and $oldRoute.Count -gt 0) "continuous transfer with actual earlier route evidence for $army"
    $intervening = @($rows | Where-Object { $_.LineNumber -gt $priorOrder[0].LineNumber -and $_.LineNumber -lt $reuse[0].LineNumber -and
        $_.Line -match "(Relief (departure|reuses formation|native dispersal|assignment ending|muster redirected|order authority lost|lost order authority)|Siege (departure|native dispersal|selected)|Muster release):.*army=$idRx(?: |;)" })
    Assert-Evidence ($intervening.Count -eq 0) "no intervening reassignment/release recorded for $army"
    Assert-Evidence ($oldClaim[0].Line -match "home/${oldRx}:${idRx}:ordered\(") "prior joint order snapshot for $army"
    Assert-Evidence ($claim[0].Line -match "home/${targetRx}:${idRx}:ordered\(present=([^;]+); traveling=([^;]*); protected=[^;]*; pendingRelease=([^)]*)\)") "current roster claim for $army"
    $allParties += @(@($Matches[1], $Matches[2], $Matches[3]) -split ',' | Where-Object { $_ })
    $transferRows += $reuse[0]
    $routeRows += $oldRoute[-1]
}
Assert-Evidence (@($allParties | Sort-Object -Unique).Count -eq $allParties.Count) 'the logged roster lists do not overlap'
Assert-Evidence ($claim[0].LineNumber -lt $review[0].LineNumber) 'same-pass registry check precedes the priority reviews'
$ended = @($rows | Where-Object { $_.LineNumber -gt $oldOpen[0].LineNumber -and $_.LineNumber -lt $lower[0].LineNumber -and
    $_.Line -match "Relief objective ended: kingdom=$kingRx target=$oldRx " })
Assert-Evidence ($ended.Count -eq 0) 'no logged closure/replacement of the lower siege before the priority review'
$otherLower = @($current | Where-Object { $_.Line -match "Relief review: kingdom=$kingRx target=\S+ rank=[2-9]\d*/\d+ .*committed=0 .*committed-higher-priority:" })
$progress = @($rows | Where-Object { $_.LineNumber -gt $claim[0].LineNumber -and $_.LineNumber -lt $verdict[0].LineNumber -and
    $_.Line -match "Acceptance run=$runRx $kingRx/${targetRx}: native defense ordered;" })
$enRoute = @($rows | Where-Object { $_.LineNumber -gt $claim[0].LineNumber -and $_.LineNumber -lt $verdict[0].LineNumber -and
    $_.Line -match "Relief execution: kingdom=$kingRx target=$targetRx army=(\S+) phase=en-route " -and $newIds -contains $Matches[1] })

"Read-only reconstruction: session=$Session run=$RunId logSHA256=$sha"
if ($Receipt) {
    $receiptText = (Get-Content -LiteralPath $Receipt -Raw).Trim()
    Assert-Evidence ($receiptText.StartsWith("Run ${RunId}: INCONCLUSIVE:") -and $verdict[0].Line.EndsWith($receiptText)) 'receipt is unchanged in meaning and matches the historical final log'
    "Original receipt SHA-256: $((Get-FileHash -LiteralPath $Receipt -Algorithm SHA256).Hash)"
}
Show-Evidence ($arm + $oldOpen + $oldOrders + $oldClaim + $routeRows + $newOpen + $choice + $transferRows + $newOrders + $claim +
    $review + $otherLower + @($progress | Select-Object -First 1) + @($enRoute | Select-Object -First 3) + $verdict)
"Finding: $($newIds.Count) distinct same-kingdom armies and $($allParties.Count) distinct logged roster entries transferred in one allocator pass; $PreviousTarget was still open but unassigned."
"Limits: earlier lower-target land route/order and observed transit, not a new path probe at transfer. Hours are printed to two decimals; the contiguous selection, reuse, departures, snapshot and reviews establish the ordering. No claim of arrival, victory, or an amended popup."
Assert-Evidence (((Get-FileHash -LiteralPath $Log -Algorithm SHA256).Hash) -eq $sha) 'log changed while reading; use a stable copy'
