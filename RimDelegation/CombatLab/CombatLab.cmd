@echo off
rem ============================================================
rem  RimDelegation CombatLab launcher
rem  Requires the .NET 10 Desktop Runtime (bundled with the SDK).
rem
rem  Just double-click this file. Arguments are passed through:
rem    CombatLab.cmd --selftest
rem    CombatLab.cmd --smoke
rem    CombatLab.cmd --render preview.png
rem
rem  NOTE: comments are ASCII on purpose -- cmd.exe reads .cmd
rem  files in the OEM code page, so UTF-8 Chinese would garble.
rem ============================================================
start "" "%~dp0dist\RimDelegation.CombatLab.exe" %*
