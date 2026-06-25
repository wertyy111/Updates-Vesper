param(
  [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
  [string[]]$OutputPath
)

$ErrorActionPreference = 'Stop'

foreach ($candidate in $OutputPath) {
  if (-not (Test-Path -LiteralPath $candidate)) {
    continue
  }

  $procName = [System.IO.Path]::GetFileNameWithoutExtension($candidate)
  $processes = Get-Process -Name $procName -ErrorAction SilentlyContinue
  foreach ($proc in $processes) {
    $isMatching = $false
    try {
      if ($null -ne $proc.Path) {
        $isMatching = [System.IO.Path]::GetFullPath($proc.Path) -eq [System.IO.Path]::GetFullPath($candidate)
      } else {
        $isMatching = $true
      }
    }
    catch {
      $isMatching = $true
    }

    if ($isMatching) {
      Write-Error "Close running Vesper Launcher before Release build. Locked file: $candidate"
      exit 1
    }
  }
}
