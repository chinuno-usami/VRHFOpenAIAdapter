@echo off
setlocal
set "ROOT=%~dp0.."
pushd "%ROOT%" >nul || exit /b 1
set "DLL=VRHandsFrame_Data\Managed\Assembly-CSharp.dll"
set "BACKUP=%DLL%.vrhf-original"
set "RC=0"
if not exist "%BACKUP%" (
  echo Backup not found: %BACKUP%
  set "RC=1"
  goto done
)
copy /y "%BACKUP%" "%DLL%" >nul
if errorlevel 1 (
  set "RC=1"
  goto done
)
del /q "VRHandsFrame_Data\Managed\VRHF.OpenAIAdapter.dll" 2>nul
echo Original Assembly-CSharp.dll restored.
:done
popd >nul
exit /b %RC%
