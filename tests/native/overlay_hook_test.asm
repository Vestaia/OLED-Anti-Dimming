; SPDX-License-Identifier: GPL-3.0-only
PUBLIC ProbeOverlayRegisters
PUBLIC ClobberVolatiles
.code
ClobberVolatiles PROC
    mov rdx, 2222h
    mov r8, 8888h
    mov r9, 9999h
    mov r10, 0aaaah
    mov r11, 0bbbbh
    pxor xmm0, xmm0
    pxor xmm1, xmm1
    pxor xmm2, xmm2
    pxor xmm3, xmm3
    pxor xmm4, xmm4
    pxor xmm5, xmm5
    xor eax, eax
    ret
ClobberVolatiles ENDP
ProbeOverlayRegisters PROC FRAME
    push rbx
    .pushreg rbx
    push rsi
    .pushreg rsi
    push rdi
    .pushreg rdi
    sub rsp, 32
    .allocstack 32
    .endprolog
    mov rsi, rcx
    mov rdi, rdx
    mov rbx, r8
    mov rcx, rdi
    mov rax, 7711h
    mov rdx, 22h
    mov r8, 88h
    mov r9, 99h
    mov r10, 0aah
    mov r11, 0bbh
    pcmpeqd xmm0, xmm0
    pcmpeqd xmm1, xmm1
    pcmpeqd xmm2, xmm2
    pcmpeqd xmm3, xmm3
    pcmpeqd xmm4, xmm4
    pcmpeqd xmm5, xmm5
    call rsi
    mov [rbx], rax
    mov [rbx+8], rcx
    mov [rbx+16], rdx
    mov [rbx+24], r8
    mov [rbx+32], r9
    mov [rbx+40], r10
    mov [rbx+48], r11
    movdqu [rbx+56], xmm0
    movdqu [rbx+72], xmm1
    movdqu [rbx+88], xmm2
    movdqu [rbx+104], xmm3
    movdqu [rbx+120], xmm4
    movdqu [rbx+136], xmm5
    add rsp, 32
    pop rdi
    pop rsi
    pop rbx
    ret
ProbeOverlayRegisters ENDP
END
