# Verification without hardware: build with system csc (C# 5), logic tests in dry-run.
# No writes to firmware, power plan or system. Usage: .\scripts\verify.ps1 [-OutFile <file>]
param([string]$OutFile = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { $lines.Add($s); Write-Host $s }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$refs = "/r:System.dll","/r:System.Core.dll","/r:System.Drawing.dll","/r:System.Windows.Forms.dll","/r:System.Management.dll","/r:System.ServiceProcess.dll"
$opts = "/nologo","/langversion:5","/codepage:65001","/optimize+","/platform:anycpu"
$src = Get-ChildItem src\*.cs | ForEach-Object { $_.FullName }
$tests = Get-ChildItem tests\*.cs | ForEach-Object { $_.FullName }
New-Item -ItemType Directory -Force out | Out-Null

$ErrorActionPreference = "Continue"   # git stderr must not stop verification (copy outside git)
$head = (git rev-parse HEAD 2>$null); $dirty = (git status --porcelain 2>$null | Measure-Object).Count
if (-not $head) { $head = "(no commit)" }
$ErrorActionPreference = "Stop"
Say "verify.ps1  HEAD=$head  modified files=$dirty  $(Get-Date -Format s)"
Say "csc: $csc"
foreach ($file in @($src) + @($tests) + @((Join-Path $root 'scripts\verify.ps1'), (Join-Path $root 'src\app.config'), (Join-Path $root 'src\app.manifest'))) {
  $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
  Say "SHA256 $($file.Substring($root.Length + 1)) $hash"
}

$rc = 0
$builds = @(
  @{ name = "GUI/service"; args = $opts + "/target:winexe" + "/win32icon:src\app.ico" + "/win32manifest:src\app.manifest" + "/out:out\AcerBatteryControl.exe" + $refs + $src },
  @{ name = "CLI";          args = $opts + "/target:exe" + "/define:CLI" + "/out:out\acerbat.exe" + $refs + $src },
  @{ name = "test";         args = $opts + "/target:exe" + "/define:CLI" + "/main:T" + "/out:out\tests.exe" + $refs + $src + $tests }
)
foreach ($b in $builds) {
  $o = & $csc @($b.args) 2>&1 | Out-String
  Say "== build $($b.name): exit $LASTEXITCODE"
  if ($o.Trim()) { Say $o.TrimEnd() }
  if ($LASTEXITCODE -ne 0) { $rc = 1 }
}

# Process with a deadline: terminate a stuck test rather than leaving it waiting.
function Invoke-Tests([int]$timeoutMs) {
  $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $root 'out\tests.exe')
  $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $psi.WorkingDirectory = $root
  $p = [Diagnostics.Process]::Start($psi)
  $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
  if ($p.WaitForExit($timeoutMs)) { $p.WaitForExit(); $code = $p.ExitCode }
  else { try { $p.Kill() } catch { }; $p.WaitForExit(5000) | Out-Null; $code = 124 }
  $text = $so.Result + $(if ($se.Result) { "`n-- stderr --`n" + $se.Result } else { "" })
  if ($code -eq 124) { $text += "`nFAIL tests terminated after $($timeoutMs / 1000) s" }
  return $code, $text
}

if ($rc -eq 0) {
  Copy-Item -LiteralPath src\app.config -Destination out\AcerBatteryControl.exe.config
  $data = Join-Path $root ("out\acerbat-test-" + [Guid]::NewGuid().ToString("N"))
  $previousDryRun = $env:ACERBAT_DRYRUN; $previousDataDir = $env:ACERBAT_DATADIR
  try {
    $env:ACERBAT_DRYRUN = "1"; $env:ACERBAT_DATADIR = $data
    $t, $o = Invoke-Tests 180000
    # Gate self-test: a thread crash must exit with 3 before the deadline, without dialogs.
    $env:ACERBAT_TEST_CRASH = "1"
    try { $ct, $co = Invoke-Tests 30000 } finally { Remove-Item Env:ACERBAT_TEST_CRASH -ErrorAction SilentlyContinue }
    $o += "`n== thread crash self-test: exit $ct (expected 3)"
    if ($ct -ne 3) { $o += "`nFAIL crash self-test: $co"; if ($t -eq 0) { $t = 1 } }
  } finally {
    $ErrorActionPreference = "Stop"
    $env:ACERBAT_DRYRUN = $previousDryRun; $env:ACERBAT_DATADIR = $previousDataDir
    $resolvedData = [IO.Path]::GetFullPath($data)
    if (-not $resolvedData.StartsWith(($root.TrimEnd('\') + '\out\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Test folder outside the worktree' }
    if (Test-Path -LiteralPath $resolvedData) { Remove-Item -LiteralPath $resolvedData -Recurse -Force }
  }
  Say "== tests (dry-run, temporary datadir): exit $t"
  Say $o.TrimEnd()
  if ($t -ne 0) { $rc = 1 }
}
Say "== verify result: $(if ($rc -eq 0) { 'GREEN' } else { 'RED' })"
if ($OutFile) { [IO.File]::WriteAllLines($OutFile, $lines, (New-Object Text.UTF8Encoding($true))) }
exit $rc
