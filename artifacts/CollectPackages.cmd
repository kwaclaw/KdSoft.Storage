
@SETLOCAL enableextensions enabledelayedexpansion
@ECHO off

RMDIR /S /Q upload
MKDIR upload

PUSHD ..

FOR /R %%I IN (*.csproj) DO IF EXIST %%~fI (
  XCOPY "%%~dpIbin\debug\*.nupkg" "artifacts\upload"
)

XCOPY /Y "artifacts\upload\*.nupkg" "artifacts\nuget"

POPD

ENDLOCAL

PAUSE