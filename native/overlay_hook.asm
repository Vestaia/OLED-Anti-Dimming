; DWM's private leaf-function callers rely on more than the public x64 ABI.
; Preserve every volatile register except the boolean result in AL.
EXTERN OledOverlaysEnabledImpl:PROC
PUBLIC OledOverlaysEnabledThunk
.code
OledOverlaysEnabledThunk PROC FRAME
    sub rsp, 184
    .allocstack 184
    .endprolog
    mov [rsp+32], rax
    mov [rsp+40], rcx
    mov [rsp+48], rdx
    mov [rsp+56], r8
    mov [rsp+64], r9
    mov [rsp+72], r10
    mov [rsp+80], r11
    movdqu [rsp+88], xmm0
    movdqu [rsp+104], xmm1
    movdqu [rsp+120], xmm2
    movdqu [rsp+136], xmm3
    movdqu [rsp+152], xmm4
    movdqu [rsp+168], xmm5
    call OledOverlaysEnabledImpl
    mov [rsp+32], al
    mov rax, [rsp+32]
    mov rcx, [rsp+40]
    mov rdx, [rsp+48]
    mov r8, [rsp+56]
    mov r9, [rsp+64]
    mov r10, [rsp+72]
    mov r11, [rsp+80]
    movdqu xmm0, [rsp+88]
    movdqu xmm1, [rsp+104]
    movdqu xmm2, [rsp+120]
    movdqu xmm3, [rsp+136]
    movdqu xmm4, [rsp+152]
    movdqu xmm5, [rsp+168]
    add rsp, 184
    ret
OledOverlaysEnabledThunk ENDP
END
