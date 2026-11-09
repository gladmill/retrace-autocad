;; Retrace sample: a LISP command that runs UCS and then a SCRIPT file, which types DRAWCHAINDLG.
;; Same shape as a vendor add-in button whose LISP starts a script. Copy this file and chain.scr to C:/temp/retrace-chain/.
;; The "Chain..." button of the Retrace Samples tab (Dialogs panel) loads it when the drawing lacks C:CHAINADD, then runs it.
(defun C:CHAINADD ()
  (command "_.UCS" "_W")
  (command "_.SCRIPT" "C:/temp/retrace-chain/chain.scr")
  (princ)
)

;; DRAGADD: like CHAINADD, but its script (dragplace.scr) types DRAWCHAINDLG and then DRAWDRAGPLACE (a jig: insertion point, then rotation).
;; The "Drag place..." button of the Retrace Samples tab (Dialogs panel) runs it. Copy dragplace.scr to C:/temp/retrace-chain/ too.
(defun C:DRAGADD ()
  (command "_.UCS" "_W")
  (command "_.SCRIPT" "C:/temp/retrace-chain/dragplace.scr")
  (princ)
)

;; HIDDENADD: its script (hiddenmodal.scr) types DRAWHIDDENMODAL: a modal form nobody sees for 2 s, then the DRAWDRAGPLACE jig.
;; SLOWADD: its script (slowdialog.scr) types DRAWSLOWDIALOG: a real dialog that shows 0.8 s after AutoCAD entered its modal state.
(defun C:HIDDENADD ()
  (command "_.UCS" "_W")
  (command "_.SCRIPT" "C:/temp/retrace-chain/hiddenmodal.scr")
  (princ)
)
(defun C:SLOWADD ()
  (command "_.UCS" "_W")
  (command "_.SCRIPT" "C:/temp/retrace-chain/slowdialog.scr")
  (princ)
)
