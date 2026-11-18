@echo off
chcp 65001 >nul
title Сборка дистрибутива RevitViewPlacer

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildDistribution.ps1"

echo.
pause
