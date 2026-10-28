$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$toolsDir = Join-Path $root 'platform-tools'
$zipPath = Join-Path $root 'platform-tools.zip'

Write-Host '=== Mobile Transfer Pro: بناء نسخة Windows 10 ===' -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'لم يتم العثور على dotnet. ثبّت .NET 8 SDK من https://dotnet.microsoft.com/download/dotnet/8.0 ثم افتح PowerShell من جديد وأعد تشغيل هذا الملف.'
}
$sdks = dotnet --list-sdks
if (-not ($sdks -match '^8\.')) { throw 'المطلوب تثبيت .NET 8 SDK تحديداً.' }

if (-not (Test-Path (Join-Path $toolsDir 'adb.exe'))) {
    Write-Host 'تنزيل Android SDK Platform-Tools الرسمية من Google...'
    Invoke-WebRequest -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $zipPath
    if (Test-Path $toolsDir) { Remove-Item $toolsDir -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $root -Force
    Remove-Item $zipPath -Force
}

Push-Location $src
try {
    dotnet publish .\MobileTransferPro.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
    if ($LASTEXITCODE -ne 0) { throw 'فشل dotnet publish؛ راجع رسائل البناء أعلاه.' }
} finally { Pop-Location }

$publishDir = Join-Path $src 'bin\Release\net8.0-windows\win-x64\publish'
$publishTools = Join-Path $publishDir 'platform-tools'
New-Item -ItemType Directory -Path $publishTools -Force | Out-Null
Copy-Item (Join-Path $toolsDir '*') $publishTools -Recurse -Force
Write-Host ''
Write-Host 'اكتمل البناء. ملف البرنامج:' -ForegroundColor Green
Write-Host (Join-Path $publishDir 'MobileTransferPro.exe')
Write-Host 'جرّب أولاً بهاتفين تملكهما أو لديك إذن صريح بنقل بياناتهما.'
