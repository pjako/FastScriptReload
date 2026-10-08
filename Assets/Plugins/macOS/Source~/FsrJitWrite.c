// Writes machine code into Mono's JIT memory on macOS / Apple Silicon. JIT pages are allocated with MAP_JIT and
// are either writable or executable for the calling thread, never both. While the thread is in write mode it can't
// run any JIT-compiled code, so the switch, the copy and the switch back must all happen here in native code.
//
// Build (from this folder):
//   clang -arch arm64 -O2 -dynamiclib -o ../FsrJitWrite.dylib FsrJitWrite.c

#include <libkern/OSCacheControl.h>
#include <pthread.h>
#include <string.h>

__attribute__((visibility("default")))
void fsr_write_code(void *dst, const void *src, size_t length)
{
    pthread_jit_write_protect_np(0);
    memcpy(dst, src, length);
    pthread_jit_write_protect_np(1);
    sys_icache_invalidate(dst, length);
}
