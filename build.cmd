@echo off
echo Building GrowLinkAgent...
dotnet publish -c Release -o publish\
if %ERRORLEVEL% EQU 0 (
    echo.
    echo Build successful. Output: publish\GrowLinkAgent.exe
) else (
    echo Build failed.
)
pause
