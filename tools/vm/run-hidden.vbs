' Runs its arguments as one command line without a console window and waits for it; exit code passes through.
' powershell -WindowStyle Hidden still flashes a console before it hides; wscript has no console, and Run(..., 0) never shows one.
' Usage (e.g. as a schtasks /tr): wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\retrace-vm\x.ps1
Set a = WScript.Arguments
cmd = ""
For i = 0 To a.Count - 1
  s = a(i)
  If InStr(s, " ") > 0 Then s = """" & s & """"
  cmd = cmd & " " & s
Next
WScript.Quit CreateObject("WScript.Shell").Run(Trim(cmd), 0, True)
