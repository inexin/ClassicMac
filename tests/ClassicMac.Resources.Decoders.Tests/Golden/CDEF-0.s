; 'CDEF' 0: 68k code resource
; Standard header: 'CDEF' 0, version $0001

entry:
00000000  600A                      bra.s $000C  ; main
00000002  0000 4344 4546 0000       dc.w $0000,$4344,$4546,$0000  ; code resource header; '..CDEF..'
0000000A  0001                      dc.w $0001  ; '..'

main:
0000000C  7000                      moveq #0,d0
0000000E  4E75                      rts
