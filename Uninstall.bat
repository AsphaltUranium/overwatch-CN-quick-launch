@echo off
chcp 936 >nul
rem ============================================
rem  OverwatchLauncher - Uninstall script
rem  Deletes scheduled task: OWOneClickElevated
rem  Deletes OverwatchLauncher.exe (same folder)
rem  Usage: right-click -> Run as administrator
rem ============================================

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo 需要管理员权限才能删除计划任务。
    echo 请右键本文件，选择"以管理员身份运行"。
    echo.
    pause
    exit /b
)

echo 正在删除计划任务...
schtasks /Delete /TN OWOneClickElevated /F >nul 2>&1
if %errorlevel% equ 0 (
    echo   计划任务 OWOneClickElevated 已删除
) else (
    echo   计划任务删除失败（可能不存在，或需要手动在任务计划程序中删除）
)

echo 正在删除程序...
if exist "%~dp0OverwatchLauncher.exe" (
    del /f /q "%~dp0OverwatchLauncher.exe"
    echo   OverwatchLauncher.exe 已删除
) else (
    echo   未找到 OverwatchLauncher.exe（请把本脚本放到它旁边再运行）
)

echo.
echo 卸载完成。桌面快捷方式如失效，请手动删除。
echo.
pause