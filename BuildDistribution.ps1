$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$rootDir = $PSScriptRoot
$dotnet = "C:\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "   Сборка плагина и подготовка дистрибутива для коллег" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Компиляция в Release
Write-Host "[1/4] Компиляция проекта в Release конфигурации..." -ForegroundColor Yellow
$proj = Join-Path $rootDir "RevitViewPlacer.csproj"
& $dotnet build $proj -c Release -p:DeployAddin=false
if ($LASTEXITCODE -ne 0) {
    Write-Error "Ошибка при сборке проекта! Дистрибутив не создан."
    exit $LASTEXITCODE
}
Write-Host "[OK] Сборка завершена успешно." -ForegroundColor Green

# 2. Подготовка каталога дистрибутива
Write-Host "[2/4] Подготовка папки дистрибутива..." -ForegroundColor Yellow
$setupDir = Join-Path $rootDir "RevitViewPlacer_Setup"
if (Test-Path $setupDir) {
    Remove-Item $setupDir -Recurse -Force
}
New-Item -ItemType Directory -Path $setupDir | Out-Null

$filesDir = Join-Path $setupDir "Files"
$pluginSubDir = Join-Path $filesDir "RevitViewPlacer"
New-Item -ItemType Directory -Path $pluginSubDir | Out-Null

# 3. Копирование скриптов и файлов плагина
Write-Host "[3/4] Копирование установочных скриптов и библиотек..." -ForegroundColor Yellow

$templateDir = Join-Path $rootDir "dist_template"
Copy-Item (Join-Path $templateDir "Установить_Плагин.cmd") $setupDir
Copy-Item (Join-Path $templateDir "Удалить_Плагин.cmd") $setupDir
Copy-Item (Join-Path $templateDir "Инструкция.txt") $setupDir

Copy-Item (Join-Path $rootDir "RevitViewPlacer.addin") $filesDir
Copy-Item (Join-Path $rootDir "bin\Release\net8.0-windows\*.dll") $pluginSubDir
Copy-Item (Join-Path $rootDir "Лист (Форма3).rfa") $pluginSubDir
Copy-Item (Join-Path $rootDir "Лист_(Форма3)без_надписи.rfa") $pluginSubDir
Copy-Item (Join-Path $rootDir "ADSK_Марка_Помещение_ДваЗначения.rfa") $pluginSubDir

# 4. Упаковка в ZIP
Write-Host "[4/4] Создание ZIP-архива для отправки..." -ForegroundColor Yellow
$zipPath = Join-Path $rootDir "RevitViewPlacer_Setup.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path "$setupDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "   ДИСТРИБУТИВ УСПЕШНО СОЗДАН!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Архив для отправки коллегам:" -ForegroundColor White
$zipSizeFormatted = "{0:N2} MB" -f $zipSize
Write-Host "  -> $zipPath ($zipSizeFormatted)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Коллегам достаточно:" -ForegroundColor White
Write-Host "  1. Download and extract this ZIP archive." -ForegroundColor Gray
Write-Host "  2. Double-click install script." -ForegroundColor Gray
Write-Host "  3. Open Revit and enjoy!" -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor Green
