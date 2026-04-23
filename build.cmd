REM XFocusKeeper
echo me="%~dpnx0"
echo cd=%cd%
CD /D "%~dp0"
echo cd=%cd%
echo --060--
set "myBatchName=%~n0"  
title XFocusKeeper
taskkill /F /IM XFocusKeeper.exe
timeout 1
del XFocusKeeper.exe
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /target:winexe /win32icon:XFocusKeeper.ico  /out:XFocusKeeper.exe /reference:System.Web.Extensions.dll XFocusKeeper.cs

XFocusKeeper.exe
timeout 60