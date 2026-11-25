@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

cd /d "%~dp0"

set "PROJECT_PATH=src\DriverVaultCleaner\DriverVaultCleaner.csproj"
set "MODE=sc"
set "NO_PAUSE=0"

:parse_args
if "%~1"=="" goto after_args
if /i "%~1"=="--mode" (
    set "MODE=%~2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--no-pause" (
    set "NO_PAUSE=1"
    shift
    goto parse_args
)
if /i "%~1"=="-h" goto show_help
if /i "%~1"=="--help" goto show_help
echo [エラー] 不明な引数: %~1
goto fail_exit

:show_help
echo 使用方法: build.bat [--mode sc^|fd] [--no-pause]
exit /b 0

:after_args
if not exist "%PROJECT_PATH%" (
    echo [エラー] プロジェクトファイルが見つかりません: %PROJECT_PATH%
    goto fail_exit
)

if not exist "assets\app.ico" (
    echo [エラー] アプリアイコンが見つかりません: assets\app.ico
    goto fail_exit
)

if /i "%MODE%"=="sc" (
    call :publish_sc || goto fail_exit
) else if /i "%MODE%"=="fd" (
    call :publish_fd || goto fail_exit
) else (
    echo [エラー] --mode は sc または fd を指定してください。指定値: %MODE%
    goto fail_exit
)

echo.
echo ============================================================
echo  ビルドが正常に完了しました！
echo ============================================================
if exist "publish\DriverVaultCleaner.exe" (
    echo 単一実行ファイル: .\publish\DriverVaultCleaner.exe
)
if "%NO_PAUSE%"=="0" pause
exit /b 0

:publish_sc
echo.
echo Self-Contained 単一ファイル EXE を発行しています...
dotnet publish "%PROJECT_PATH%" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o ".\publish"
if errorlevel 1 exit /b 1
if exist ".\publish\DriverVaultCleaner.pdb" del /f /q ".\publish\DriverVaultCleaner.pdb"
echo 出力完了: .\publish\DriverVaultCleaner.exe
exit /b 0

:publish_fd
echo.
echo Framework-Dependent 単一ファイル EXE を発行しています...
dotnet publish "%PROJECT_PATH%" -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o ".\publish"
if errorlevel 1 exit /b 1
if exist ".\publish\DriverVaultCleaner.pdb" del /f /q ".\publish\DriverVaultCleaner.pdb"
echo 出力完了: .\publish\DriverVaultCleaner.exe
exit /b 0

:fail_exit
echo.
echo [失敗] ビルド中にエラーが発生しました。
if "%NO_PAUSE%"=="0" pause
exit /b 1
