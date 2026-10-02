; 'PACK' 3: 68k code resource
; Package: 'PACK' 3, selectors 0 to 1

00000000  A9FF 5041 434B 0003       dc.w $A9FF,$5041,$434B,$0003  ; package header; '..PACK..'
00000008  0001 0000 0001 0008       dc.w $0001,$0000,$0001,$0008  ; '........'
00000010  0000                      dc.w $0000  ; '..'

selector_0:
00000012  4E75                      rts
