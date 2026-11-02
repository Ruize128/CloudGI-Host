/* Synthetic ABI-valid functions using upstream signatures solely to exercise our
 * production DLL in an owned test process. No game code. AGPL-3.0-only. */
.text
.p2align 4
.globl FixtureFov
FixtureFov:
    .byte 0x40
    pushq %rbx
    subq $0x60, %rsp
    movaps %xmm6, 0x20(%rsp)
    .byte 0x48,0x8b,0xd9
    movaps %xmm1, %xmm6
    call FixtureNoop
    testq %rax, %rax
    .byte 0x0f,0x84
    .long FovReturn - . - 4
    call FixtureNoop
    .byte 0x48,0x8b,0xc8
FovReturn:
    movaps 0x20(%rsp), %xmm6
    addq $0x60, %rsp
    popq %rbx
    ret
FixtureNoop:
    xorq %rax, %rax
    ret

.macro TouchStub name, marker
.p2align 4
.globl \name
\name:
    pushq %rsi
    pushq %rdi
    subq $0x28, %rsp
    movq %rcx, %rsi
    cmpb $0, FixtureGuard(%rip)
    movq FixtureRoot(%rip), %rax
    .byte 0x0f,0x85
    .long 1f - . - 4
    movq 0x100(%rax), %rcx
    testq %rcx, %rcx
    .byte 0x0f,0x84
    .long 1f - . - 4
    movq FixtureRoot(%rip), %rdx
    call FixtureNoop
    movq %rax, %rdi
    movq FixtureRoot(%rip), %rax
    movq 0x100(%rax), %rcx
    testq %rcx, %rcx
    .byte 0x0f,0x84
    .long 1f - . - 4
    xorl %edx, %edx
1:
    movl $\marker, FixtureTouchMarker(%rip)
    addq $0x28, %rsp
    popq %rdi
    popq %rsi
    ret
.endm
TouchStub FixtureTouchFirst, 1
TouchStub FixtureTouchSecond, 2

.data
.p2align 3
FixtureRoot: .quad 0
FixtureGuard: .byte 1
.p2align 2
.globl FixtureTouchMarker
FixtureTouchMarker: .long 0
