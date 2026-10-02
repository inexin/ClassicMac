; 'CODE' 1 "Main": 68k segment, near header
; Model: unknown
; Entry: CODE 1:+$4
; Also PowerPC code: 'cfrg' 0
; Jump-table entries: 2

00000000  0000 0002                 dc.w $0000,$0002  ; segment header; '....'

entry:
00000004  4EAD 002A                 jsr 42(a5)  ; CODE 1:+$A
00000008  4E75                      rts

JT1:
0000000A  7000                      moveq #0,d0
0000000C  4E75                      rts
