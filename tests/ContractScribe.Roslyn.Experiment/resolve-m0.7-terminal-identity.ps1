function Test-M07CommitIdentity([object]$Value) {
    return $null -ne $Value -and ([string]$Value) -cmatch "^[0-9a-f]{40}$"
}

function ConvertTo-M07PositiveInteger([object]$Value, [string]$FieldName) {
    $text = if ($null -eq $Value) { "" } else { [string]$Value }
    if ($text -cnotmatch "^[1-9][0-9]*$") {
        throw "$FieldName must be a positive integer without coercion."
    }

    $parsed = 0L
    if (-not [long]::TryParse($text, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        throw "$FieldName is outside the supported integer range."
    }

    return $parsed
}

function Get-M07EventDocument([string]$EventPath) {
    if ([string]::IsNullOrWhiteSpace($EventPath)) {
        return $null
    }
    if (-not (Test-Path -LiteralPath $EventPath -PathType Leaf)) {
        throw "The GitHub event payload path does not identify a file."
    }

    try {
        return Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "The GitHub event payload is not valid JSON."
    }
}

function Get-M07NestedString([object]$Document, [string[]]$Path) {
    $current = $Document
    foreach ($segment in $Path) {
        if ($null -eq $current) { return $null }
        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    if ($null -eq $current) { return $null }
    return [string]$current
}

function Resolve-M07TerminalIdentity {
    [CmdletBinding()]
    param(
        [string]$PrHeadCommit,
        [string]$ValidationMergeCommit,
        [string]$RunId,
        [object]$RunAttempt,
        [string]$RunnerOs,
        [string]$Rid,
        [string]$EventPath
    )

    $resolvedPrHead = $PrHeadCommit
    if ([string]::IsNullOrWhiteSpace($resolvedPrHead)) { $resolvedPrHead = $env:M07_PR_HEAD_SHA }
    if ([string]::IsNullOrWhiteSpace($resolvedPrHead)) {
        $resolvedEventPath = if ([string]::IsNullOrWhiteSpace($EventPath)) { $env:GITHUB_EVENT_PATH } else { $EventPath }
        $eventDocument = Get-M07EventDocument $resolvedEventPath
        $resolvedPrHead = Get-M07NestedString $eventDocument @("pull_request", "head", "sha")
        if ([string]::IsNullOrWhiteSpace($resolvedPrHead)) { $resolvedPrHead = Get-M07NestedString $eventDocument @("inputs", "pr_head_sha") }
    }
    if ([string]::IsNullOrWhiteSpace($resolvedPrHead)) { $resolvedPrHead = $env:GITHUB_SHA }

    $resolvedValidationMerge = $ValidationMergeCommit
    if ([string]::IsNullOrWhiteSpace($resolvedValidationMerge)) { $resolvedValidationMerge = $env:M07_VALIDATION_MERGE_SHA }
    if ([string]::IsNullOrWhiteSpace($resolvedValidationMerge)) { $resolvedValidationMerge = $env:GITHUB_SHA }

    $resolvedRunId = if ([string]::IsNullOrWhiteSpace($RunId)) { $env:GITHUB_RUN_ID } else { $RunId }
    $resolvedAttemptInput = if ($null -eq $RunAttempt -or [string]::IsNullOrWhiteSpace([string]$RunAttempt)) { $env:GITHUB_RUN_ATTEMPT } else { $RunAttempt }
    $resolvedRunnerOs = if ([string]::IsNullOrWhiteSpace($RunnerOs)) { $env:RUNNER_OS } else { $RunnerOs }

    if (-not (Test-M07CommitIdentity $resolvedPrHead)) { throw "The M0.7 PR-head identity must be a lowercase 40-hex commit." }
    if (-not (Test-M07CommitIdentity $resolvedValidationMerge)) { throw "The M0.7 validation-merge identity must be a lowercase 40-hex commit." }
    if ([string]::IsNullOrWhiteSpace($resolvedRunId) -or $resolvedRunId -cnotmatch "^[1-9][0-9]*$") { throw "The M0.7 run ID must be a non-empty decimal string." }
    $resolvedRunAttempt = ConvertTo-M07PositiveInteger $resolvedAttemptInput "The M0.7 run attempt"

    $expectedRid = switch -CaseSensitive ($resolvedRunnerOs) {
        "Linux" { "linux-x64" }
        "Windows" { "win-x64" }
        default { throw "The M0.7 runner OS must be Linux or Windows." }
    }
    $resolvedRid = if ([string]::IsNullOrWhiteSpace($Rid)) { $expectedRid } else { $Rid }
    if ($resolvedRid -cne $expectedRid) { throw "The M0.7 runner OS and RID are contradictory." }

    return [pscustomobject][ordered]@{
        protocolPrHeadCommit = $resolvedPrHead
        validationMergeCommit = $resolvedValidationMerge
        runnerOs = $resolvedRunnerOs
        rid = $resolvedRid
        runId = $resolvedRunId
        runAttempt = $resolvedRunAttempt
        protocolCommit = $resolvedValidationMerge
    }
}
