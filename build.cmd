@echo off
chcp 65001 >nul
rem ============================================================
rem  WaterCoolSim (虚拟水冷坞) 一键构建脚本
rem  依赖：仅系统自带的 .NET Framework 4.x（csc.exe），无第三方库
rem ============================================================
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /win32manifest:"src\app.manifest" /win32icon:"src\app.ico" /out:"dist\虚拟水冷坞.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll "src\WaterCoolSim.cs" "src\MonetM3.cs"
if errorlevel 1 ( echo 编译失败 & pause & exit /b 1 )
echo 已生成: %~dp0dist\虚拟水冷坞.exe
echo （可选）数字签名: powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign.ps1" -Dir "%~dp0dist"
pause
