param(
    [string]$CampaignLog = (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'BannerlordMilitaryAIOverhaul.log'),
    [Parameter(Mandatory=$true)][string]$PacingLog,
    [Parameter(Mandatory=$true)][string]$Session
)
$ErrorActionPreference = 'Stop'
$logs = @($CampaignLog, $PacingLog)
$hashes = @($logs | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })
function Val($line, $pattern) { [regex]::Match($line, $pattern).Groups[1].Value }
function Num($text) { [double]::Parse($text, [Globalization.CultureInfo]::InvariantCulture) }
function Sum($rows, $key) { ($rows | Measure-Object $key -Sum).Sum }

$rows = @(Select-String -LiteralPath $CampaignLog -SimpleMatch "session=$Session ")
if (!$rows.Count) { throw "Session not found: $Session" }
$all = @($rows | ForEach-Object {
    [pscustomobject]@{ Text = $_.Line; Line = $_.LineNumber; Army = Val $_.Line '\barmy=([^ ;,]+@\d+(?:\.\d+)?)';
        Hour = Num (Val $_.Line '\bcampaignHour=(\S+)'); Utc = [datetimeoffset]::Parse((Val $_.Line '^\[([^\]]+)\]')) }
})
$starts = @($all | Where-Object Text -Match ' Siege selected:')
$closes = @($all | Where-Object Text -Match ' Siege closes:')
$debits = @($all | Where-Object Text -Match ' Muster debit .*accepted=True ' | ForEach-Object {
    [pscustomobject]@{ Army=$_.Army; Line=$_.Line; Party=Val $_.Text '\bparty=(\S+)'; Amount=Num (Val $_.Text 'actualDebit=([\d.-]+)') }
})
$created = @($all | Where-Object Text -Match ' Army created:')
$formed = @($all | Where-Object Text -Match ' Muster formed:')
$reports = @()
foreach ($start in $starts) {
    $next = $starts | Where-Object { $_.Army -eq $start.Army -and $_.Line -gt $start.Line } | Select-Object -First 1
    $end = $closes | Where-Object { $_.Army -eq $start.Army -and $_.Line -gt $start.Line -and (!$next -or $_.Line -lt $next.Line) } | Select-Object -First 1
    $stop = if ($end) { $end.Line } elseif ($next) { $next.Line } else { [int]::MaxValue }
    $context = @($all | Where-Object { $_.Army -eq $start.Army -and $_.Line -gt $start.Line -and $_.Line -le $stop })
    $isNew = $start.Text -match 'formation=muster '
    $paid = @($debits | Where-Object { $isNew -and $_.Army -eq $start.Army -and $_.Line -lt $start.Line })
    $failure = $context | Where-Object Text -Match ' Siege feasibility lost:' | Select-Object -Last 1
    $depart = $context | Where-Object Text -Match ' Siege departure:' | Select-Object -First 1
    $quoted = Val $start.Text ' projected=(\S+)'
    $projected = if ($failure) { Val $failure.Text ' projected=(\S+)' } else { '' }
    $result = if ($end) { Val $end.Text ' reason=(\S+)' } elseif ($next) { 'reassigned-without-offense-close' } else { 'no-offense-close-in-session' }
    $reports += [pscustomobject]@{ Start=$start; End=$end; Army=$start.Army; Kingdom=Val $start.Text ' kingdom=(\S+)';
        Target=Val $start.Text ' target=(\S+)'; New=$isNew; Calls=@($paid | Where-Object Party -NE (($start.Army -split '@')[0]));
        Paid=(Sum $paid Amount); Quoted=$quoted; Projected=$projected; Failure=$failure; Depart=$depart; Result=$result }
}
$new = @($reports | Where-Object New)
"campaign SHA256=$($hashes[0]) session=$Session rows=$($rows.Count) hours=$($all[0].Hour)..$($all[-1].Hour)"
"army created rows=$($created.Count) (unique IDs=$(@($created.Army | Sort-Object -Unique).Count)); commander musters=$($formed.Count); offensive selections=$($reports.Count) new=$($new.Count); raw offensive close rows=$($closes.Count)"
"new offense accepted escort calls=$(($new | ForEach-Object {$_.Calls.Count} | Measure-Object -Sum).Sum) debits=$(Sum $new Paid); other accepted campaign membership debits=$( (Sum $debits Amount) - (Sum $new Paid) ); refused membership debits=$(@($all | Where-Object Text -Match ' Muster debit .*accepted=False ').Count)"
"offense departures=$(@($all | Where-Object Text -Match ' Siege departure:').Count) native camps=$(@($all | Where-Object Text -Match ' Siege native camp:').Count) assaults=$(@($all | Where-Object Text -Match ' Siege native assault started:').Count)"
foreach ($r in ($reports | Group-Object Result | Sort-Object Name)) {
    "$($r.Name): selections=$($r.Count) newSummonDebit=$(Sum $r.Group Paid)"
}
foreach ($r in ($reports | Group-Object Kingdom | Sort-Object Name)) {
    "$($r.Name): new=$(@($r.Group | Where-Object New).Count) allSelections=$($r.Count) newSummonDebit=$(Sum $r.Group Paid)"
}
$rebuyByLine = @{}
$repeatLinks = $samePrintedHour = 0
foreach ($r in $reports) {
    "$($r.Kingdom) $($r.Target) $($r.Army) [line $($r.Start.Line)] new=$($r.New) called=$($r.Calls.Count) quotedDebit=$(Val $r.Start.Text ' costQuoted=(\S+)') actualDebit=$($r.Paid) selectedPower=$($r.Quoted) " +
        "affordablePower=$(Val $r.Start.Text ' affordablePrefixPower=(\S+)') departurePower=$(if($r.Depart) {Val $r.Depart.Text ' present=(\S+)'}) " +
        "end=$($r.Result) endHour=$($r.End.Hour) failedProjected=$($r.Projected) failedLine=$($r.Failure.Line)"
    if ($r.Failure) { "  $($r.Failure.Text)" }
    if ($r.New) { "  actual escorts: $(($r.Calls | ForEach-Object { "$($_.Party)=$($_.Amount)@$($_.Line)" }) -join ', ')" }
    $release = if ($r.End) { $all | Where-Object { $_.Army -eq $r.Army -and $_.Line -gt $r.End.Line -and $_.Text -match ' Muster release:' } | Select-Object -First 1 }
    if (!$release -or $r.Result -notmatch 'muster-projected|march-no-longer') { continue }
    $released = @((Val $release.Text 'parties=\[([^\]]*)\]') -split ', ' | ForEach-Object { ($_ -split ':')[0] })
    foreach ($again in ($new | Where-Object { $_.Kingdom -eq $r.Kingdom -and $_.Start.Line -gt $release.Line -and $_.Start.Hour - $r.End.Hour -le 8.25 })) {
        $rebought = @($again.Calls | Where-Object { $_.Party -in $released })
        if ($rebought.Count) {
            $repeatLinks++
            if ($again.Start.Hour -eq $r.End.Hour) { $samePrintedHour++ }
            foreach ($debit in $rebought) { $rebuyByLine[$debit.Line] = $debit.Amount }
            "  repeat release->call within 8.25h: $($again.Army) $($again.Target) at $($again.Start.Hour) escorts=$($rebought.Count) actualNewDebit=$(Sum $rebought Amount)"
        }
    }
}
"linked repeats=$repeatLinks samePrintedHour=$samePrintedHour uniqueChargeRows=$($rebuyByLine.Count) uniqueDebits=$(($rebuyByLine.Values | Measure-Object -Sum).Sum) (association, not a counterfactual cost)"
foreach ($group in ($new | Group-Object Kingdom)) {
    $timeline = @($group.Group | ForEach-Object {
        [pscustomobject]@{ Line=$_.Start.Line; Delta=1 }
        if ($_.End) { [pscustomobject]@{ Line=$_.End.Line; Delta=-1 } }
    })
    $active = $peak = 0
    foreach ($entry in ($timeline | Sort-Object Line)) { $active += $entry.Delta; $peak = [Math]::Max($peak, $active) }
    "offensive selection-to-close $($group.Name): observed peak=$peak no close=$active (not a native active-army census)"
}

$perf = @(Get-Content -LiteralPath $PacingLog)
$previous = [datetimeoffset]::Parse((Val $perf[0] ' utc=(\S+)'))
$slow = @()
$max100 = $ge100 = $planCalls = 0
$planTotalMs = 0.0
foreach ($line in ($perf | Where-Object { $_ -like 'window *' })) {
    $end = [datetimeoffset]::Parse((Val $line ' endUtc=(\S+)'))
    $maximum = Num (Val $line 'maxMs=(\S+)')
    $ge100 += [int](Val $line ' ge33/50/100=\d+/\d+/(\d+)')
    if ($line -match ' siegePlan=(\d+)/([\d.]+)/') { $planCalls += [int]$Matches[1]; $planTotalMs += Num $Matches[2] }
    if ($maximum -ge 100) { $max100++ }
    $plan = Val $line ' siegePlan=\d+/[\d.]+/[\d.]+/([\d.]+)'
    if ($maximum -ge 100 -and $plan) {
        $hour = Num (Val $line ' startCampaignHour=(\S+)')
        $near = @($all | Where-Object { $_.Utc -gt $previous -and $_.Utc -le $end -and $_.Hour -ge $hour -and
            $_.Hour -le $hour + 0.25 -and $_.Text -match ' Siege (search|selected):' })
        $slow += [pscustomobject]@{ Max=$maximum; Plan=(Num $plan); Pct=100*(Num $plan)/$maximum }
        "slow interval=$(Val $line ' slowInterval=(\d+)') hour=$hour maxMs=$maximum planMs=$plan nearNoSameTickClaim=$($near.Count)"
    }
    $previous = $end
}
"pacing SHA256=$($hashes[1]) windows=$(@($perf | Where-Object { $_ -like 'window *' }).Count) max>=100 windows=$max100 with measured plan=$($slow.Count) majority=$(@($slow | Where-Object Pct -GT 50).Count) eligibleIntervals>=100ms=$ge100"
"siegePlan total inclusive ms=$planTotalMs calls=$planCalls (includes cheap return/no-op calls; not a native-query count)"
if ($slow.Count) { "same-interval share min/max percent=$(($slow | Measure-Object Pct -Minimum).Minimum)..$(($slow | Measure-Object Pct -Maximum).Maximum)" }
if (Compare-Object $hashes @($logs | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })) { throw 'Input changed during read; use stable read-only copies.' }
