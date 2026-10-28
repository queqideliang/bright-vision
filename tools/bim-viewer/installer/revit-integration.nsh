; electron-builder nsis.include. Revit registration is current-user only.
; Both successful and deferred integration leave the viewer installation intact.
!macro customInstall
  Push $R7
  Push $R8
  Push $R9
  StrCpy $R7 "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe"
  ; The installer is 32-bit; prefer the 64-bit host for Revit registry discovery.
  ${If} ${FileExists} "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
    StrCpy $R7 "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
  ${EndIf}
  ${If} $installMode == "CurrentUser"
    nsExec::ExecToStack '"$R7" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "$INSTDIR\resources\revit-integration\Install-RevitIntegration.ps1" -Action Install -ModulePath "$INSTDIR\resources\revit-plugin" -InstallContext CurrentUser'
  ${Else}
    ; Never register into the elevated administrator's profile for an all-users
    ; viewer installation. The normal user's app can perform the later repair.
    nsExec::ExecToStack '"$R7" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "$INSTDIR\resources\revit-integration\Install-RevitIntegration.ps1" -Action Install -ModulePath "$INSTDIR\resources\revit-plugin" -InstallContext AllUsers -LogPath "$INSTDIR\resources\revit-integration\machine-setup.json"'
  ${EndIf}
  Pop $R8
  Pop $R9
  DetailPrint "Revit integration: $R9"
  ${If} $R8 != "0"
    DetailPrint "Revit integration can be repaired from the viewer. Viewer installation will continue."
  ${EndIf}
  Pop $R9
  Pop $R8
  Pop $R7
!macroend

!macro customUnInstall
  ; electron-builder runs the old uninstaller during upgrades. Never remove
  ; the exporter here, including when viewer data-removal flags are supplied.
  ; Explicit exporter removal is available inside the application's tools.
  DetailPrint "Revit exporter retained. Remove it from the viewer's Revit integration tools before uninstalling the viewer if desired."
!macroend
