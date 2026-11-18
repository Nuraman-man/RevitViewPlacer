@echo off
chcp 65001 >nul
title Установка плагина RevitViewPlacer

echo ======================================================================
echo          УСТАНОВКА ПЛАГИНА REVIT VIEW PLACER И ГЕНПЛАНА
echo ======================================================================
echo.

set "SOURCE_DIR=%~dp0"
set "REVIT_ADDINS_BASE=%APPDATA%\Autodesk\Revit\Addins"

if not exist "%SOURCE_DIR%Files\RevitViewPlacer.addin" (
    echo [ОШИБКА] Не найдены исходные файлы плагина в папке "Files".
    echo Убедитесь, что вы полностью распаковали архив перед запуском.
    echo.
    pause
    exit /b 1
)

:: Проверяем, запущен ли Revit
tasklist /FI "IMAGENAME eq Revit.exe" 2>NUL | find /I /N "Revit.exe" >NUL
if "%ERRORLEVEL%"=="0" (
    echo [ВНИМАНИЕ] Процесс Revit сейчас запущен!
    echo Рекомендуется закрыть Revit перед установкой, чтобы обновить файлы.
    echo.
)

set "INSTALLED_COUNT=0"

:: Проверяем папки версий Revit (2026, 2025, 2024)
for %%V in (2026 2025 2024) do (
    if exist "%REVIT_ADDINS_BASE%\%%V" (
        echo [1/2] Обнаружена папка для Revit %%V. Копирование файлов...
        
        if not exist "%REVIT_ADDINS_BASE%\%%V\RevitViewPlacer" (
            mkdir "%REVIT_ADDINS_BASE%\%%V\RevitViewPlacer" >nul 2>&1
        )
        
        copy /y "%SOURCE_DIR%Files\RevitViewPlacer.addin" "%REVIT_ADDINS_BASE%\%%V\" >nul
        copy /y "%SOURCE_DIR%Files\RevitViewPlacer\*.*" "%REVIT_ADDINS_BASE%\%%V\RevitViewPlacer\" >nul 2>&1
        copy /y "%SOURCE_DIR%Files\RevitViewPlacer\RevitViewPlacer.dll" "%REVIT_ADDINS_BASE%\%%V\" >nul 2>&1
        
        echo [OK] Плагин успешно установлен для Revit %%V!
        echo.
        set /a INSTALLED_COUNT+=1
    )
)

:: Если ни одна папка 2024-2026 еще не существовала в AppData, устанавливаем для 2026 по умолчанию
if "%INSTALLED_COUNT%"=="0" (
    echo [1/2] Создаем каталог надстроек для Revit 2026...
    if not exist "%REVIT_ADDINS_BASE%\2026\RevitViewPlacer" (
        mkdir "%REVIT_ADDINS_BASE%\2026\RevitViewPlacer" >nul 2>&1
    )
    copy /y "%SOURCE_DIR%Files\RevitViewPlacer.addin" "%REVIT_ADDINS_BASE%\2026\" >nul
    copy /y "%SOURCE_DIR%Files\RevitViewPlacer\*.*" "%REVIT_ADDINS_BASE%\2026\RevitViewPlacer\" >nul 2>&1
    copy /y "%SOURCE_DIR%Files\RevitViewPlacer\RevitViewPlacer.dll" "%REVIT_ADDINS_BASE%\2026\" >nul 2>&1
    echo [OK] Плагин установлен в каталог Revit 2026!
    echo.
    set /a INSTALLED_COUNT+=1
)

echo ======================================================================
echo   [УСПЕХ] Установка плагина завершена!
echo.
echo   Что делать дальше:
echo   1. Откройте Autodesk Revit.
echo   2. При первом запуске нажмите "Всегда загружать" (Always Load).
echo   3. На панели "Надстройки" (Add-Ins) появятся кнопки плагина:
echo      - "Авторазмещение видов на листы"
echo      - "Детальный генплан ГП-1"
echo ======================================================================
echo.
pause
