@echo off
chcp 65001 >nul
title Удаление плагина RevitViewPlacer

echo ======================================================================
echo               УДАЛЕНИЕ ПЛАГИНА REVIT VIEW PLACER
echo ======================================================================
echo.

set "REVIT_ADDINS_BASE=%APPDATA%\Autodesk\Revit\Addins"
set "REMOVED=0"

for %%V in (2026 2025 2024) do (
    if exist "%REVIT_ADDINS_BASE%\%%V\RevitViewPlacer.addin" (
        del /f /q "%REVIT_ADDINS_BASE%\%%V\RevitViewPlacer.addin" >nul 2>&1
        rd /s /q "%REVIT_ADDINS_BASE%\%%V\RevitViewPlacer" >nul 2>&1
        echo [OK] Плагин успешно удален из Revit %%V.
        set "REMOVED=1"
    )
)

if "%REMOVED%"=="0" (
    echo [ИНФО] Файлы плагина в каталогах Revit не найдены.
)

echo.
echo ======================================================================
echo   Готово. Плагин удален из Revit.
echo ======================================================================
echo.
pause
