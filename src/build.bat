@echo off
rem Builds yt-dlp-studio.exe using the C# compiler and WPF that ship with Windows (.NET Framework 4.x).
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "ROOT=%~dp0.."
if not exist "%~dp0app.ico" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-icon.ps1"
if not exist "%ROOT%\assets\logo.png" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-icon.ps1"
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:"%ROOT%\yt-dlp-studio.exe" ^
  /win32icon:"%~dp0app.ico" /lib:"%FW%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.Xml.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /resource:"%~dp0MainWindow.xaml",MainWindow.xaml ^
  /resource:"%ROOT%\assets\logo.png",logo.png ^
  "%~dp0YtDlpStudio.cs"
