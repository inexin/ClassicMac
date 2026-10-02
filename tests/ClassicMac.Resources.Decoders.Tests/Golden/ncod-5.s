; 'ncod' 5: PowerPC fragment ('pwpc'), 3 sections
; Section 0: Code, 0x50 bytes
; Section 1: UnpackedData, 0x1C bytes
; Section 2: Loader, 0x9E bytes
; Main: 1:0x0 -> 0:0x0
; TOC base: 1:0x8
; Imports: 1 from 1 library
; Exports: 1

main:
0:00000000  7C0802A6  mflr r0
0:00000004  48000015  bl 0x18  ; InterfaceLib::InitGraf
0:00000008  80410014  lwz r2,20(r1)
0:0000000C  48000025  bl 0x30  ; Helper
0:00000010  80620004  lwz r3,4(r2)  ; 1:0x10
0:00000014  4E800020  blr

.InitGraf:
0:00000018  81820000  lwz r12,0(r2)  ; InterfaceLib::InitGraf
0:0000001C  90410014  stw r2,20(r1)
0:00000020  800C0000  lwz r0,0(r12)
0:00000024  804C0004  lwz r2,4(r12)
0:00000028  7C0903A6  mtctr r0
0:0000002C  4E800420  bctr

Helper:
0:00000030  4E800020  blr
0:00000034  00000000 00002040 00000000 00000004  dc.l $00000000,$00002040,$00000000,$00000004  ; traceback table .Helper
0:00000044  00072E48 656C7065 72000000  dc.l $00072E48,$656C7065,$72000000

; Transition vectors
1:00000000  00000000 00000008  dc.l $00000000,$00000008  ; main: code 0:0x0, TOC 1:0x8
1:00000014  00000030 00000008  dc.l $00000030,$00000008  ; Helper: code 0:0x30, TOC 1:0x8
