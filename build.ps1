# Idle-timer 빌드 스크립트 (Windows 내장 .NET Framework csc 사용, SDK 불필요)
# 사용: powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src\Program.cs'
$out  = Join-Path $root 'IdleTimer.exe'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe 를 찾을 수 없습니다 (.NET Framework 4.x 필요)" }

$refs = @(
  '/r:System.dll',
  '/r:System.Core.dll',
  '/r:System.Windows.Forms.dll',
  '/r:System.Drawing.dll'
)

Write-Host "컴파일 중... -> $out"
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
       /out:"$out" $refs "$src"
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (exit $LASTEXITCODE)" }
Write-Host "빌드 완료: $out"
Write-Host "실행: .\IdleTimer.exe  (트레이에 시계 아이콘이 나타납니다)"
