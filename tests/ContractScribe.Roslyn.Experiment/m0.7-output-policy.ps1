function Test-M07PublicOutputSafe([string]$publicOutput) {
    return $publicOutput -notmatch "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key|password\s*=)"
}
