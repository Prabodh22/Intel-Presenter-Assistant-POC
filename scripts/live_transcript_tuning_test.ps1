param(
    [string]$RepoRoot = "C:\PPT-gnai-help"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-LatestLogFile {
    param([string]$Root)

    $candidateDirs = @(
        (Join-Path $Root "src\PptPoc.App\bin\Debug\net8.0-windows10.0.17763.0\win-x64\logs"),
        (Join-Path $Root "logs"),
        (Join-Path $env:LOCALAPPDATA "PptPoc\logs")
    )

    # Include any additional logs folders under src/**/bin/** so we can attach
    # to whichever app output is currently active.
    if (Test-Path (Join-Path $Root "src")) {
        $discovered = Get-ChildItem -Path (Join-Path $Root "src") -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\bin\\.*\\logs$" } |
            Select-Object -ExpandProperty FullName
        $candidateDirs += $discovered
    }

    $candidateDirs = @($candidateDirs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

    $allLogs = New-Object System.Collections.Generic.List[object]

    foreach ($dir in $candidateDirs) {
        if (-not (Test-Path $dir)) { continue }

        $logs = Get-ChildItem -Path $dir -Filter "pptpoc-*.log" -File -ErrorAction SilentlyContinue
        foreach ($log in $logs) {
            $allLogs.Add($log)
        }
    }

    $latest = $allLogs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -ne $latest) { return $latest.FullName }

    return $null
}

function Get-TranscriptLinesInWindow {
    param(
        [string]$LogFile,
        [datetimeoffset]$Start,
        [datetimeoffset]$End
    )

    $results = New-Object System.Collections.Generic.List[object]
    $lines = Get-Content -Path $LogFile

    foreach ($line in $lines) {
        if ($line -notmatch "^(?<dto>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [\+\-]\d{2}:\d{2}) \[DBG\] Transcript UI='(?<ui>.*?)' \| Raw='(?<raw>.*?)'") {
            continue
        }

        $lineTs = [datetimeoffset]::ParseExact($Matches.dto, "yyyy-MM-dd HH:mm:ss.fff zzz", $null)
        if ($lineTs -lt $Start -or $lineTs -gt $End) { continue }

        $results.Add([pscustomobject]@{
            Timestamp = $lineTs
            TranscriptUi = $Matches.ui
            TranscriptRaw = $Matches.raw
        })
    }

    return $results
}

function Get-WindowDiagnostics {
    param(
        [string]$LogFile,
        [datetimeoffset]$Start,
        [datetimeoffset]$End
    )

    $diag = [ordered]@{
        AnyLogLinesInWindow = 0
        AsrCallsInWindow = 0
        VadSkipsInWindow = 0
        LastLineAt = ""
    }

    $lines = Get-Content -Path $LogFile
    foreach ($line in $lines) {
        if ($line -notmatch "^(?<dto>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [\+\-]\d{2}:\d{2}) ") {
            continue
        }

        $lineTs = [datetimeoffset]::ParseExact($Matches.dto, "yyyy-MM-dd HH:mm:ss.fff zzz", $null)
        if ($lineTs -lt $Start -or $lineTs -gt $End) { continue }

        $diag.AnyLogLinesInWindow++
        $diag.LastLineAt = $lineTs.ToString("o")

        if ($line -match "Calling ASR with") { $diag.AsrCallsInWindow++ }
        if ($line -match "VAD: Skipping ASR") { $diag.VadSkipsInWindow++ }
    }

    return [pscustomobject]$diag
}

$tests = @(
    [pscustomobject]@{ Id = "CTRL01"; Category = "Command"; Sentence = "laser on"; Expected = "Control phrase only" },
    [pscustomobject]@{ Id = "CTRL02"; Category = "Command"; Sentence = "laser off"; Expected = "Control phrase only" },
    [pscustomobject]@{ Id = "FILL01"; Category = "Filler"; Sentence = "um hmm okay"; Expected = "No useful transcript" },
    [pscustomobject]@{ Id = "TEXT01"; Category = "Text"; Sentence = "this is a simple accuracy benchmark"; Expected = "Text intent captured" },
    [pscustomobject]@{ Id = "TEXT02"; Category = "Text"; Sentence = "tool for generative models"; Expected = "Text intent captured" },
    [pscustomobject]@{ Id = "IMG01"; Category = "ImageWhole"; Sentence = "as you can see in this chart"; Expected = "Whole-visual intent" },
    [pscustomobject]@{ Id = "IMG02"; Category = "ImageSub"; Sentence = "highlight physics chemistry law"; Expected = "OCR-word intent" },
    [pscustomobject]@{ Id = "LOC01"; Category = "Spatial"; Sentence = "show the image on the right"; Expected = "Spatial cue retained" },
    [pscustomobject]@{ Id = "AMB01"; Category = "Ambiguous"; Sentence = "show this image"; Expected = "Conservative/ambiguous" }
)

$logFile = Get-LatestLogFile -Root $RepoRoot
if ($null -eq $logFile) {
    Write-Host "No log file found. Start app first, then rerun." -ForegroundColor Yellow
    exit 1
}

$logAgeSec = [int](([datetime]::Now) - (Get-Item $logFile).LastWriteTime).TotalSeconds
if ($logAgeSec -gt 45) {
    Write-Host ("Warning: latest log is stale ({0}s old). Start/Resume engine first." -f $logAgeSec) -ForegroundColor Yellow
    Write-Host "If engine is running, wait for fresh log activity before starting this script." -ForegroundColor Yellow
    $proceed = Read-Host "Type Y to continue anyway, anything else to exit"
    if ($proceed -notin @("Y", "y")) {
        Write-Host "Cancelled by user." -ForegroundColor Yellow
        exit 1
    }
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outCsv = Join-Path $RepoRoot ("logs\live-transcript-tuning-{0}.csv" -f $timestamp)
$outJson = Join-Path $RepoRoot ("logs\live-transcript-tuning-{0}.json" -f $timestamp)

Write-Host "Using log file: $logFile" -ForegroundColor Cyan
Write-Host ("Log last write: {0}" -f (Get-Item $logFile).LastWriteTime) -ForegroundColor DarkGray
Write-Host ""
Write-Host "For each case:" -ForegroundColor Green
Write-Host "1) Press Enter to arm capture"
Write-Host "2) Speak the exact sentence once"
Write-Host "3) Press Enter to stop capture"
Write-Host ""

$records = New-Object System.Collections.Generic.List[object]

foreach ($test in $tests) {
    Write-Host "======================================================"
    Write-Host ("Case {0} [{1}]" -f $test.Id, $test.Category) -ForegroundColor Yellow
    Write-Host ("Speak: ""{0}""" -f $test.Sentence) -ForegroundColor White
    Write-Host ("Expected: {0}" -f $test.Expected) -ForegroundColor DarkGray

    Read-Host "Press Enter to arm"
    $start = [datetimeoffset]::Now.AddSeconds(-1)

    Read-Host "Speak now, then press Enter"
    $end = [datetimeoffset]::Now.AddSeconds(2)

    $logFile = Get-LatestLogFile -Root $RepoRoot
    $windowLines = Get-TranscriptLinesInWindow -LogFile $logFile -Start $start -End $end
    $windowLineCount = @($windowLines).Count
    $last = @($windowLines) | Sort-Object Timestamp | Select-Object -Last 1
    $diag = Get-WindowDiagnostics -LogFile $logFile -Start $start -End $end

    $records.Add([pscustomobject]@{
        Id = $test.Id
        Category = $test.Category
        LogFileUsed = $logFile
        PromptSentence = $test.Sentence
        Expected = $test.Expected
        WindowStart = $start.ToString("o")
        WindowEnd = $end.ToString("o")
        ObservedAt = if ($null -ne $last) { $last.Timestamp.ToString("o") } else { "" }
        ObservedTranscriptUi = if ($null -ne $last) { $last.TranscriptUi } else { "" }
        ObservedTranscriptRaw = if ($null -ne $last) { $last.TranscriptRaw } else { "" }
        TranscriptLinesInWindow = $windowLineCount
        AnyLogLinesInWindow = $diag.AnyLogLinesInWindow
        AsrCallsInWindow = $diag.AsrCallsInWindow
        VadSkipsInWindow = $diag.VadSkipsInWindow
        LastLogLineInWindowAt = $diag.LastLineAt
        Notes = ""
    })

    if ($null -eq $last) {
        Write-Host "Observed: (no transcript line)" -ForegroundColor Red
        Write-Host ("Window diagnostics: any={0}, asrCalls={1}, vadSkips={2}" -f $diag.AnyLogLinesInWindow, $diag.AsrCallsInWindow, $diag.VadSkipsInWindow) -ForegroundColor DarkYellow
    } else {
        Write-Host ("Observed UI: {0}" -f $last.TranscriptUi) -ForegroundColor Green
    }
}

$records | Export-Csv -Path $outCsv -NoTypeInformation -Encoding UTF8
$records | ConvertTo-Json -Depth 6 | Set-Content -Path $outJson -Encoding UTF8

Write-Host ""
Write-Host "Done. Share this for tuning:" -ForegroundColor Green
Write-Host ("  {0}" -f $outCsv)
Write-Host ("  {0}" -f $outJson)
