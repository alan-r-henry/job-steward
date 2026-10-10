@echo off
rem Builds JobTray.exe with the C# compiler that ships with Windows (.NET Framework 4).
rem   tray\build.cmd            -> tray\bin\JobTray.exe
cd /d %~dp0
if not exist bin mkdir bin
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
  /win32icon:jobtray.ico /out:bin\JobTray.exe ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  JobTray.cs
