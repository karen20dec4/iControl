@echo off
rem Compileaza iControl cu compilatorul C# 5 inclus in .NET Framework 4.8 (fara SDK).
rem Rezultat: bin\iControl.exe. Daca aplicatia ruleaza, foloseste deploy.ps1 (o opreste curat inainte).
setlocal
cd /d "%~dp0"
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe ^
  /win32manifest:src\iControl.manifest /r:System.Management.dll ^
  /out:bin\iControl.exe /recurse:src\*.cs
exit /b %ERRORLEVEL%
