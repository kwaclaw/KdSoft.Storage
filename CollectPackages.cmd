
@SETLOCAL enableextensions enabledelayedexpansion
@ECHO off

DEL artifacts\*.nupkg


FOR /R %%I IN (*.csproj) DO IF EXIST %%~fI (
  XCOPY %%~dpI\bin\debug\*.nupkg artifacts
)


ENDLOCAL

PAUSE