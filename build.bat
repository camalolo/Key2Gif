@echo off
echo Starting Key2Gif publish process...

REM Set variables
set PROJECT_DIR=E:\Development\CSharp\Key2Gif\Key2Gif
set PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net8.0-windows\win-x64\publish

REM Step 1: Run dotnet publish
echo Running dotnet publish...
dotnet publish -p:PublishSingleFile=true -c Release -r win-x64 --self-contained true %PROJECT_DIR%\Key2Gif.csproj
if %ERRORLEVEL% NEQ 0 (
    echo Error: Publish failed!
    pause
    exit /b %ERRORLEVEL%
)

REM Step 2: Copy to E:\Apps
echo Copying to E:\Apps...
copy /Y %PUBLISH_DIR%\Key2Gif.exe E:\Apps\

echo Done.
