# One-time, run by a person on the VM: runs dialog-watch.ps1 at every logon (interactive session) and starts it now. Undo: schtasks /delete
# /tn AcadDialogWatch /f, then stop the dialog-watch powershell (schtasks /end only stops the run-hidden.vbs wrapper). Re-running it replaces a
# running watcher. Without it, dialogs.sh --start runs the watcher until logoff.
$tr = 'wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\retrace-vm\dialog-watch.ps1'
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object CommandLine -match '[\\/]dialog-watch\.ps1' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
schtasks /create /tn AcadDialogWatch /tr $tr /sc onlogon /it /f /rl limited | Out-Null
schtasks /run /tn AcadDialogWatch | Out-Null
schtasks /query /tn AcadDialogWatch /fo list | Select-String 'TaskName|Status'
