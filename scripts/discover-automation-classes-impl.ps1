<#
.SYNOPSIS
    Scans VS Code's UI Automation tree to discover CSS class names used by the
    Copilot chat pane. Run this when Copilot updates break the pane detector.

.DESCRIPTION
    VS Code (Electron/Chromium) exposes its DOM classes via the Windows UI
    Automation ClassName property. This script enumerates all VS Code windows,
    walks every descendant element, and filters for class names containing
    common chat/confirmation/loading patterns.

    If the critical patterns (confirmation dialog, loading spinner) are not
    found, the script enters guided discovery mode: it opens a fresh VS Code
    window, sends Copilot prompts designed to provoke the confirmation and
    loading UI, takes snapshots of the automation tree before/after each
    prompt, and diffs them to surface the new class names.

    Use the output to update core.config.json -> PaneDetector section with the
    new ConfirmationClassName and LoadingClassName values.

.NOTES
    Requirements: Windows, VS Code on PATH (code.cmd), Copilot extension
    installed. The script creates a temp folder for the guided discovery
    workspace and cleans it up afterward.

.EXAMPLE
    .\discover-automation-classes.ps1
    .\discover-automation-classes.ps1 -Filter "confirm|loading|widget"
    .\discover-automation-classes.ps1 -DumpAll
    .\discover-automation-classes.ps1 -SkipGuided
#>

#requires -Version 7.0

param(
    [string]$Filter = "chat-|confirm|loading|widget|spinner|progress|action-required",
    [switch]$DumpAll,
    [switch]$SkipGuided
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$windowClass = "Chrome_WidgetWin_1"
$titleMatch = "Visual Studio Code"

function Find-VsCodeWindows {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $allWindows = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty,
            $windowClass
        ))
    )
    $results = @()
    foreach ($win in $allWindows) {
        if ($win.Current.Name -match [regex]::Escape($titleMatch)) {
            $results += $win
        }
    }
    return $results
}

function Get-ClassSnapshot {
    param([System.Windows.Automation.AutomationElement]$Window)
    $descendants = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition
    )
    $classes = @{}
    foreach ($el in $descendants) {
        $cls = $el.Current.ClassName
        if ($cls) {
            if (-not $classes.ContainsKey($cls)) { $classes[$cls] = 0 }
            $classes[$cls]++
        }
    }
    return $classes
}

function Show-ScanResults {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [hashtable]$KnownPatterns,
        [string]$FilterPattern,
        [bool]$ShowAll
    )

    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Window: $($Window.Current.Name)" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $descendants = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition
    )
    $sw.Stop()
    Write-Host "Scanned $($descendants.Count) elements in $($sw.ElapsedMilliseconds)ms" -ForegroundColor DarkGray
    Write-Host ""

    Write-Host "--- Known Pattern Check ---" -ForegroundColor Yellow
    $foundConfirmation = $false
    $foundLoading = $false
    foreach ($key in $KnownPatterns.Keys) {
        $pattern = $KnownPatterns[$key]
        $found = $false
        foreach ($el in $descendants) {
            $cls = $el.Current.ClassName
            if ($cls -and $cls.Contains($pattern)) {
                $found = $true
                break
            }
        }
        if ($key -match "CONFIRMATION" -and $found) { $foundConfirmation = $true }
        if ($key -match "LOADING" -and $found) { $foundLoading = $true }
        $icon = if ($found) { "[FOUND]" } else { "[  --  ]" }
        $color = if ($found) { "Green" } else { "DarkGray" }
        Write-Host ("  {0,-8} {1,-45} {2}" -f $icon, $key, $pattern) -ForegroundColor $color
    }
    Write-Host ""

    Write-Host "--- Filtered Matches ---" -ForegroundColor Yellow
    $seen = @{}
    foreach ($el in $descendants) {
        $cls = $el.Current.ClassName
        if (-not $cls) { continue }
        if ($ShowAll -or ($cls -match $FilterPattern)) {
            if (-not $seen.ContainsKey($cls)) { $seen[$cls] = 0 }
            $seen[$cls]++
        }
    }

    $sorted = $seen.GetEnumerator() | Sort-Object -Property Value -Descending
    foreach ($entry in $sorted) {
        $highlight = $false
        foreach ($pattern in $KnownPatterns.Values) {
            if ($entry.Key.Contains($pattern)) { $highlight = $true; break }
        }
        $color = if ($highlight) { "Green" } else { "White" }
        Write-Host ("  {0,3}x  {1}" -f $entry.Value, $entry.Key) -ForegroundColor $color
    }

    $matchCount = $seen.Count
    if ($matchCount -eq 0) {
        Write-Host "  (no matches for filter pattern)" -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "Total unique matching classes: $matchCount" -ForegroundColor DarkGray
    Write-Host ""

    return @{ FoundConfirmation = $foundConfirmation; FoundLoading = $foundLoading }
}

function Show-Diff {
    param(
        [hashtable]$Before,
        [hashtable]$After,
        [string]$Label
    )
    Write-Host ""
    Write-Host "--- DIFF: $Label ---" -ForegroundColor Magenta
    $newClasses = @()
    $goneClasses = @()

    foreach ($key in $After.Keys) {
        if (-not $Before.ContainsKey($key)) {
            $newClasses += $key
        }
    }
    foreach ($key in $Before.Keys) {
        if (-not $After.ContainsKey($key)) {
            $goneClasses += $key
        }
    }

    if ($newClasses.Count -gt 0) {
        Write-Host "  NEW classes (appeared after prompt):" -ForegroundColor Green
        foreach ($cls in ($newClasses | Sort-Object)) {
            Write-Host "    + $cls" -ForegroundColor Green
        }
    } else {
        Write-Host "  No new classes appeared." -ForegroundColor DarkGray
    }

    if ($goneClasses.Count -gt 0) {
        Write-Host "  REMOVED classes (disappeared after prompt):" -ForegroundColor Red
        foreach ($cls in ($goneClasses | Sort-Object)) {
            Write-Host "    - $cls" -ForegroundColor Red
        }
    } else {
        Write-Host "  No classes disappeared." -ForegroundColor DarkGray
    }
    Write-Host ""
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " VS Code UI Automation Class Discovery" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Filter pattern: $Filter" -ForegroundColor DarkGray
Write-Host ""

$vsCodeWindows = Find-VsCodeWindows

if ($vsCodeWindows.Count -eq 0) {
    Write-Host "ERROR: No VS Code windows found." -ForegroundColor Red
    Write-Host "  - Is VS Code running?" -ForegroundColor Yellow
    Write-Host "  - Window class expected: $windowClass" -ForegroundColor Yellow
    Write-Host "  - Title expected to contain: $titleMatch" -ForegroundColor Yellow
    exit 1
}

Write-Host "Found $($vsCodeWindows.Count) VS Code window(s):" -ForegroundColor Green
foreach ($win in $vsCodeWindows) {
    Write-Host "  - $($win.Current.Name)" -ForegroundColor DarkGray
}
Write-Host ""

$knownPatterns = [ordered]@{
    "CONFIRMATION (copilot.waiting trigger)" = "chat-confirmation-widget-container"
    "LOADING (copilot.done trigger)"         = "chat-response-loading"
    "Chat response container"                = "interactive-item-container"
    "Chat markdown content"                  = "chat-markdown-part"
    "Chat input area"                        = "chat-input"
    "Chat attachments"                       = "chat-attach"
    "Chat status bar"                        = "chat.statusBarEntry"
    "Terminal command decoration"             = "chat-terminal-command"
}

$globalFoundConfirmation = $false
$globalFoundLoading = $false

foreach ($win in $vsCodeWindows) {
    $result = Show-ScanResults -Window $win -KnownPatterns $knownPatterns -FilterPattern $Filter -ShowAll $DumpAll
    if ($result.FoundConfirmation) { $globalFoundConfirmation = $true }
    if ($result.FoundLoading) { $globalFoundLoading = $true }
}

$missingCritical = (-not $globalFoundConfirmation) -or (-not $globalFoundLoading)

if (-not $missingCritical) {
    Write-Host "========================================" -ForegroundColor Green
    Write-Host " All critical patterns found!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "The current PaneDetector config values are valid." -ForegroundColor White
    Write-Host "No changes needed in core.config.json." -ForegroundColor White
    Write-Host ""
    exit 0
}

Write-Host "========================================" -ForegroundColor Red
Write-Host " Missing critical patterns!" -ForegroundColor Red
Write-Host "========================================" -ForegroundColor Red
Write-Host ""
if (-not $globalFoundConfirmation) {
    Write-Host "  MISSING: Confirmation dialog class (copilot.waiting trigger)" -ForegroundColor Red
    Write-Host "    Current value: chat-confirmation-widget-container" -ForegroundColor DarkGray
}
if (-not $globalFoundLoading) {
    Write-Host "  MISSING: Loading spinner class (copilot.done trigger)" -ForegroundColor Red
    Write-Host "    Current value: chat-response-loading" -ForegroundColor DarkGray
}
Write-Host ""

if ($SkipGuided) {
    Write-Host "Guided discovery skipped (-SkipGuided). Review filtered matches above manually." -ForegroundColor Yellow
    exit 1
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Guided Discovery Mode" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "This will open a fresh VS Code window, send Copilot prompts to" -ForegroundColor White
Write-Host "provoke the confirmation and loading UI, snapshot the automation" -ForegroundColor White
Write-Host "tree before/after each action, and diff the results to find the" -ForegroundColor White
Write-Host "new class names." -ForegroundColor White
Write-Host ""

$tempDir = Join-Path $env:TEMP "copilot-beacon-discovery-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
Write-Host "Created temp workspace: $tempDir" -ForegroundColor DarkGray

Write-Host ""
Write-Host "Opening fresh VS Code window..." -ForegroundColor Yellow
code --new-window $tempDir
Write-Host "Waiting 5 seconds for window to initialize..." -ForegroundColor DarkGray
Start-Sleep -Seconds 5

$discoveryWindow = $null
$attempts = 0
while ($attempts -lt 10 -and -not $discoveryWindow) {
    $attempts++
    $allVsCode = Find-VsCodeWindows
    foreach ($w in $allVsCode) {
        if ($w.Current.Name -match [regex]::Escape((Split-Path $tempDir -Leaf))) {
            $discoveryWindow = $w
            break
        }
    }
    if (-not $discoveryWindow) {
        Write-Host "  Waiting for discovery window to appear (attempt $attempts)..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 2
    }
}

if (-not $discoveryWindow) {
    Write-Host "ERROR: Could not find the discovery VS Code window." -ForegroundColor Red
    Write-Host "  Expected window title to contain: $(Split-Path $tempDir -Leaf)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Manual fallback:" -ForegroundColor Yellow
    Write-Host "  1. Open VS Code manually" -ForegroundColor White
    Write-Host "  2. Open the Copilot chat pane (Ctrl+Shift+I or click the Copilot icon)" -ForegroundColor White
    Write-Host "  3. Ask Copilot: 'Run the command: echo hello'" -ForegroundColor White
    Write-Host "  4. Re-run this script while the confirmation dialog is visible" -ForegroundColor White
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    exit 1
}

Write-Host "Found discovery window: $($discoveryWindow.Current.Name)" -ForegroundColor Green
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Step 1: Baseline snapshot (idle state)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Taking baseline snapshot of the idle window..." -ForegroundColor Yellow
$baselineSnapshot = Get-ClassSnapshot -Window $discoveryWindow
Write-Host "  Captured $($baselineSnapshot.Count) unique classes" -ForegroundColor DarkGray
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Step 2: Trigger a loading state" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "We need Copilot to start generating a response so the loading" -ForegroundColor White
Write-Host "spinner appears. This requires sending a prompt via the chat pane." -ForegroundColor White
Write-Host ""
Write-Host "ACTION REQUIRED:" -ForegroundColor Yellow
Write-Host "  1. Switch to the discovery VS Code window" -ForegroundColor White
Write-Host "  2. Open the Copilot chat pane (Ctrl+Alt+I or click the chat icon)" -ForegroundColor White
Write-Host "  3. Type this prompt and press Enter:" -ForegroundColor White
Write-Host ""
Write-Host "     Write a long poem about the ocean" -ForegroundColor Cyan
Write-Host ""
Write-Host "  4. IMMEDIATELY come back here and press Enter while Copilot is" -ForegroundColor White
Write-Host "     still generating (spinner visible)" -ForegroundColor White
Write-Host ""
Write-Host "Press Enter when the loading spinner is visible..." -ForegroundColor Yellow
Read-Host | Out-Null

Write-Host "Taking loading snapshot..." -ForegroundColor Yellow
$discoveryWindow = $null
$allVsCode = Find-VsCodeWindows
foreach ($w in $allVsCode) {
    if ($w.Current.Name -match [regex]::Escape((Split-Path $tempDir -Leaf))) {
        $discoveryWindow = $w
        break
    }
}
if (-not $discoveryWindow) {
    Write-Host "ERROR: Lost the discovery window. Was it closed?" -ForegroundColor Red
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    exit 1
}

$loadingSnapshot = Get-ClassSnapshot -Window $discoveryWindow
Write-Host "  Captured $($loadingSnapshot.Count) unique classes" -ForegroundColor DarkGray

Show-Diff -Before $baselineSnapshot -After $loadingSnapshot -Label "Idle -> Loading (new classes = loading indicator candidates)"

$loadingCandidates = @()
foreach ($key in $loadingSnapshot.Keys) {
    if (-not $baselineSnapshot.ContainsKey($key)) {
        if ($key -match "loading|spinner|progress|generat|stream|active|running|chat-response") {
            $loadingCandidates += $key
        }
    }
}

if ($loadingCandidates.Count -gt 0) {
    Write-Host "LIKELY LOADING CLASS CANDIDATES:" -ForegroundColor Green
    foreach ($c in $loadingCandidates) {
        Write-Host "  >>> $c" -ForegroundColor Green
    }
} else {
    Write-Host "No obvious loading candidates found in diff. Check the NEW classes above" -ForegroundColor Yellow
    Write-Host "for anything that looks like a loading/progress indicator." -ForegroundColor Yellow
}
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Step 3: Wait for response to finish" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Wait for Copilot to finish generating the response, then" -ForegroundColor White
Write-Host "press Enter. We will diff again to see what disappeared" -ForegroundColor White
Write-Host "(the loading class should vanish)." -ForegroundColor White
Write-Host ""
Write-Host "Press Enter after Copilot finishes responding..." -ForegroundColor Yellow
Read-Host | Out-Null

$allVsCode = Find-VsCodeWindows
foreach ($w in $allVsCode) {
    if ($w.Current.Name -match [regex]::Escape((Split-Path $tempDir -Leaf))) {
        $discoveryWindow = $w
        break
    }
}

$doneSnapshot = Get-ClassSnapshot -Window $discoveryWindow
Write-Host "  Captured $($doneSnapshot.Count) unique classes" -ForegroundColor DarkGray

Show-Diff -Before $loadingSnapshot -After $doneSnapshot -Label "Loading -> Done (removed classes = loading indicator)"

$loadingConfirmed = @()
foreach ($key in $loadingSnapshot.Keys) {
    if (-not $doneSnapshot.ContainsKey($key)) {
        if ($key -match "loading|spinner|progress|generat|stream|active|running|chat-response") {
            $loadingConfirmed += $key
        }
    }
}

if ($loadingConfirmed.Count -gt 0) {
    Write-Host "CONFIRMED LOADING CLASS (appeared during generation, disappeared after):" -ForegroundColor Green
    foreach ($c in $loadingConfirmed) {
        Write-Host "  >>> $c" -ForegroundColor Green
    }
} else {
    Write-Host "Could not auto-detect the loading class. Compare the two diffs above:" -ForegroundColor Yellow
    Write-Host "  - A class that appeared in Step 2 (Idle->Loading) AND" -ForegroundColor Yellow
    Write-Host "  - Disappeared in Step 3 (Loading->Done)" -ForegroundColor Yellow
    Write-Host "  That class is your new LoadingClassName." -ForegroundColor Yellow
}
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Step 4: Trigger a confirmation dialog" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "We need Copilot to show a confirmation dialog (Allow/Run/Continue)." -ForegroundColor White
Write-Host "This happens when Copilot wants to run a terminal command." -ForegroundColor White
Write-Host ""
Write-Host "Taking pre-confirmation snapshot..." -ForegroundColor Yellow
$preConfirmSnapshot = Get-ClassSnapshot -Window $discoveryWindow
Write-Host "  Captured $($preConfirmSnapshot.Count) unique classes" -ForegroundColor DarkGray
Write-Host ""
Write-Host "ACTION REQUIRED:" -ForegroundColor Yellow
Write-Host "  1. Switch to the discovery VS Code window" -ForegroundColor White
Write-Host "  2. In the Copilot chat, type this prompt and press Enter:" -ForegroundColor White
Write-Host ""
Write-Host "     Run the command: echo hello" -ForegroundColor Cyan
Write-Host ""
Write-Host "  3. Wait for the confirmation dialog to appear (Allow/Run button)" -ForegroundColor White
Write-Host "  4. DO NOT click Allow/Run — come back here and press Enter" -ForegroundColor White
Write-Host ""
Write-Host "Press Enter when the confirmation dialog is visible..." -ForegroundColor Yellow
Read-Host | Out-Null

$allVsCode = Find-VsCodeWindows
foreach ($w in $allVsCode) {
    if ($w.Current.Name -match [regex]::Escape((Split-Path $tempDir -Leaf))) {
        $discoveryWindow = $w
        break
    }
}

Write-Host "Taking confirmation snapshot..." -ForegroundColor Yellow
$confirmSnapshot = Get-ClassSnapshot -Window $discoveryWindow
Write-Host "  Captured $($confirmSnapshot.Count) unique classes" -ForegroundColor DarkGray

Show-Diff -Before $preConfirmSnapshot -After $confirmSnapshot -Label "Idle -> Confirmation dialog (new classes = confirmation candidates)"

$confirmCandidates = @()
foreach ($key in $confirmSnapshot.Keys) {
    if (-not $preConfirmSnapshot.ContainsKey($key)) {
        if ($key -match "confirm|widget|action|allow|approve|dialog|accept|button|container") {
            $confirmCandidates += $key
        }
    }
}

if ($confirmCandidates.Count -gt 0) {
    Write-Host "LIKELY CONFIRMATION CLASS CANDIDATES:" -ForegroundColor Green
    foreach ($c in $confirmCandidates) {
        Write-Host "  >>> $c" -ForegroundColor Green
    }
} else {
    Write-Host "No obvious confirmation candidates found in diff. Check the NEW classes" -ForegroundColor Yellow
    Write-Host "above for anything that looks like a confirmation/dialog/widget container." -ForegroundColor Yellow
}
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Step 5: Dismiss the confirmation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Now click Allow/Run (or dismiss) the confirmation dialog, then" -ForegroundColor White
Write-Host "press Enter. We will diff to confirm which class disappeared." -ForegroundColor White
Write-Host ""
Write-Host "Press Enter after dismissing the confirmation dialog..." -ForegroundColor Yellow
Read-Host | Out-Null

$allVsCode = Find-VsCodeWindows
foreach ($w in $allVsCode) {
    if ($w.Current.Name -match [regex]::Escape((Split-Path $tempDir -Leaf))) {
        $discoveryWindow = $w
        break
    }
}

$postConfirmSnapshot = Get-ClassSnapshot -Window $discoveryWindow
Write-Host "  Captured $($postConfirmSnapshot.Count) unique classes" -ForegroundColor DarkGray

Show-Diff -Before $confirmSnapshot -After $postConfirmSnapshot -Label "Confirmation -> Dismissed (removed classes = confirmation indicator)"

$confirmConfirmed = @()
foreach ($key in $confirmSnapshot.Keys) {
    if (-not $postConfirmSnapshot.ContainsKey($key)) {
        if ($key -match "confirm|widget|action|allow|approve|dialog|accept|container") {
            $confirmConfirmed += $key
        }
    }
}

if ($confirmConfirmed.Count -gt 0) {
    Write-Host "CONFIRMED CONFIRMATION CLASS (appeared with dialog, disappeared after dismiss):" -ForegroundColor Green
    foreach ($c in $confirmConfirmed) {
        Write-Host "  >>> $c" -ForegroundColor Green
    }
} else {
    Write-Host "Could not auto-detect the confirmation class. Compare the two diffs above:" -ForegroundColor Yellow
    Write-Host "  - A class that appeared in Step 4 (Idle->Confirmation) AND" -ForegroundColor Yellow
    Write-Host "  - Disappeared in Step 5 (Confirmation->Dismissed)" -ForegroundColor Yellow
    Write-Host "  That class is your new ConfirmationClassName." -ForegroundColor Yellow
}
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Summary & Next Steps" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Update core.config.json -> Core -> PaneDetector with the new values:" -ForegroundColor White
Write-Host ""

if ($loadingConfirmed.Count -gt 0) {
    Write-Host "  LoadingClassName:" -ForegroundColor Yellow -NoNewline
    Write-Host "        $($loadingConfirmed[0])" -ForegroundColor Green
} elseif ($loadingCandidates.Count -gt 0) {
    Write-Host "  LoadingClassName:" -ForegroundColor Yellow -NoNewline
    Write-Host "        $($loadingCandidates[0])  (candidate, verify manually)" -ForegroundColor Yellow
} else {
    Write-Host "  LoadingClassName:        (not detected — review diffs above)" -ForegroundColor Red
}

if ($confirmConfirmed.Count -gt 0) {
    Write-Host "  ConfirmationClassName:" -ForegroundColor Yellow -NoNewline
    Write-Host "   $($confirmConfirmed[0])" -ForegroundColor Green
} elseif ($confirmCandidates.Count -gt 0) {
    Write-Host "  ConfirmationClassName:" -ForegroundColor Yellow -NoNewline
    Write-Host "   $($confirmCandidates[0])  (candidate, verify manually)" -ForegroundColor Yellow
} else {
    Write-Host "  ConfirmationClassName:   (not detected — review diffs above)" -ForegroundColor Red
}

Write-Host ""
Write-Host "Cleaning up temp workspace..." -ForegroundColor DarkGray
Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
Write-Host "Done." -ForegroundColor Green
Write-Host ""
