;; Retrace sample: a LISP command that runs UCS and then a SCRIPT file, which types DRAWCHAINDLG.
;; Same shape as a vendor add-in button whose LISP starts a script. Copy this file and chain.scr to C:/temp/retrace-chain/.
;; The "Chain..." button of the Retrace Samples tab (Dialogs panel) loads it when the drawing lacks C:CHAINADD, then runs it.
(defun C:CHAINADD ()
  (command "_.UCS" "_W")
  (command "_.SCRIPT" "C:/temp/retrace-chain/chain.scr")
  (princ)
)
