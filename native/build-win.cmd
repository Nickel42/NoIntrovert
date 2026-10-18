@echo off
rem Builds 7-Zip's 7z.dll and noi_archive.dll for one Windows architecture and
rem copies them into native\out\win-<arch>.
rem
rem   native\build-win.cmd x64|arm64 <7-Zip source> <bit7z source>
rem
rem Run from any prompt; it sets up the Visual Studio build environment itself.
setlocal
set ARCH=%~1
set SRC7Z=%~f2
set SRCBIT7Z=%~f3
if "%ARCH%"=="" goto usage
if "%SRC7Z%"=="" goto usage
if "%SRCBIT7Z%"=="" goto usage

set HERE=%~dp0
set OUT=%HERE%out\win-%ARCH%
set BUILD=%HERE%build-%ARCH%

if "%ARCH%"=="x64" (set VCARCH=x64) else (set VCARCH=x64_arm64)
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set VSDIR=%%i
if not exist "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" (echo Visual Studio with C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" %VCARCH% >nul || exit /b 1

rem --- 7z.dll: 7-Zip's full format library (Bundles\Format7zF), built with its own makefiles
pushd "%SRC7Z%\CPP\7zip\Bundles\Format7zF" || exit /b 1
nmake /nologo PLATFORM=%ARCH% || (popd & exit /b 1)
popd

rem --- noi_archive.dll: our C interface over bit7z
cmake -S "%HERE%archive" -B "%BUILD%" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release ^
  "-DNOI_BIT7Z_DIR=%SRCBIT7Z%" "-DNOI_7ZIP_DIR=%SRC7Z%" || exit /b 1
cmake --build "%BUILD%" || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
copy /y "%SRC7Z%\CPP\7zip\Bundles\Format7zF\%ARCH%\7z.dll" "%OUT%\" >nul || exit /b 1
copy /y "%BUILD%\noi_archive.dll" "%OUT%\" >nul || exit /b 1
echo Built %OUT%\7z.dll and %OUT%\noi_archive.dll
exit /b 0

:usage
echo usage: build-win.cmd x64^|arm64 ^<7-Zip source^> ^<bit7z source^>
exit /b 2
