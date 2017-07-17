echo off
start /B http://localhost:8097 && start docfx.exe serve _site -p 8097
exit

