param(
    [string]$Log = (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'BannerlordMilitaryAIOverhaul.log'),
    [string]$Session = '3f62050c',
    [string]$Kingdom = 'sturgia'
)

$ErrorActionPreference = 'Stop'
$sha = (Get-FileHash -LiteralPath $Log).Hash
$rows = @(Select-String -LiteralPath $Log -SimpleMatch "session=$Session ")
$king = [regex]::Escape($Kingdom)
$formed = @($rows | Where-Object { $_.Line -match "Siege selected: kingdom=$king .*formation=muster " })
if ($formed.Count -eq 0) { throw "No matching offensive musters in $Session" }

function Value($line, $pattern) {
    [regex]::Match($line, $pattern).Groups[1].Value
}
function Number($value) { [double]::Parse($value, [Globalization.CultureInfo]::InvariantCulture) }

$peak = ($rows | Group-Object { $_.Line.Substring(1, 19) } | Sort-Object Count -Descending | Select-Object -First 1)
"Session=$Session logSHA256=$sha rows=$($rows.Count) forcePartyRows=$(@($rows | Where-Object { $_.Line -match ' Force party ' }).Count) peakPrintedSecond=$($peak.Name) rowsInPeak=$($peak.Count)"
"Print times and rows do not measure wall time per frame or file writes."
$records = @()
foreach ($formation in $formed) {
    $id = Value $formation.Line ' army=(\S+) '
    $idRx = [regex]::Escape($id)
    $related = @($rows | Where-Object { $_.Line -match "\barmy=$idRx(?: |;|\.)" })
    $debits = @($related | Where-Object { $_.Line -match ' Muster debit .*accepted=True ' } | ForEach-Object {
        [pscustomobject]@{ Party = Value $_.Line '\bparty=(\S+) '; Amount = Number (Value $_.Line 'actualDebit=([\d.-]+)'); Line = $_.LineNumber }
    })
    $progress = $related | Where-Object { $_.Line -match ' Siege muster progress:' } | Select-Object -Last 1
    $departure = $related | Where-Object { $_.Line -match ' Siege departure:' } | Select-Object -First 1
    $end = $related | Where-Object { $_.Line -match ' Siege closes:' } | Select-Object -First 1
    $spent = ($debits | Measure-Object Amount -Sum).Sum
    $record = [pscustomobject]@{
        Id = $id; Target = Value $formation.Line ' target=(\S+) '; Paid = $spent
        Start = $formation; End = $end; Debits = $debits
    }
    $records += $record
    "Line $($formation.LineNumber) $id $($record.Target): quoted selected power=$(Value $formation.Line ' projected=(\S+) ') paid=$spent"
    "  accepted debit rows: $(($debits | ForEach-Object { "$($_.Party)=$($_.Amount)@$($_.Line)" }) -join ', ')"
    foreach ($row in @($progress, $departure, $end)) { if ($row) { "  $($row.LineNumber): $($row.Line)" } }
}
$events = @($records | ForEach-Object {
    [pscustomobject]@{ Line = $_.Start.LineNumber; Delta = 1 }
    if ($_.End) { [pscustomobject]@{ Line = $_.End.LineNumber; Delta = -1 } }
})
$active = $peakActive = 0
foreach ($evt in ($events | Sort-Object Line)) { $active += $evt.Delta; $peakActive = [Math]::Max($active, $peakActive) }
"Observed new offensive musters=$($records.Count) peakConcurrent=$peakActive open=$active totalAcceptedSummonDebit=$(($records | Measure-Object Paid -Sum).Sum)"

foreach ($new in $records) {
    $hour = Value $new.Start.Line 'campaignHour=(\S+) '
    foreach ($old in $records | Where-Object { $_.End -and $_.End.LineNumber -lt $new.Start.LineNumber -and
        (Value $_.End.Line 'campaignHour=(\S+) ') -eq $hour }) {
        $release = $rows | Where-Object { $_.LineNumber -gt $old.End.LineNumber -and $_.LineNumber -lt $new.Start.LineNumber -and
            $_.Line -match "Muster release: army=$([regex]::Escape($old.Id)) " } | Select-Object -First 1
        if (-not $release) { continue }
        $released = @((Value $release.Line 'parties=\[([^\]]+)\]') -split ', ' | ForEach-Object { ($_ -split ':')[0] })
        $rebought = @($new.Debits | Where-Object { $released -contains $_.Party -and $_.Party -ne ($new.Id -split '@')[0] })
        if ($rebought.Count -gt 0) {
            "Same printed hour $hour $($old.Target) -> $($new.Target): $($rebought.Count) re-summoned released escorts; paid=$(($rebought | Measure-Object Amount -Sum).Sum); release line=$($release.LineNumber)"
        }
    }
}
if ((Get-FileHash -LiteralPath $Log).Hash -ne $sha) { throw 'Log changed while reading; use a stable copy.' }
