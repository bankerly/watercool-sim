@echo off
chcp 65001 >nul
rem 一键启动虚拟水冷坞（需要 Python 3.8+，无需第三方库）
cd /d "%~dp0"
where python >nul 2>nul
if errorlevel 1 (
  echo 未找到 python，请先安装 Python 3 或把 python.exe 加入 PATH
  pause
  exit /b 1
)
python lc_sim.py --verbose %*
pause
