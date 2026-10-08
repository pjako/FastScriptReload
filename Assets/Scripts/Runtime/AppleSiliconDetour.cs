#if UNITY_EDITOR || LiveScriptReload_Enabled

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FastScriptReload.Runtime
{
    /// <summary>
    /// Method detour for macOS on Apple Silicon, where Harmony 2.2's MonoMod fails with 'mprotect returned EACCES':
    /// the OS never lets a page be writable and executable at once. Mono's JIT code lives in MAP_JIT pages, which
    /// the native helper in Plugins/macOS (FsrJitWrite.dylib) can write by briefly switching the thread to write mode.
    /// The detour overwrites the start of the old method's machine code with a jump to the new method.
    /// </summary>
    public static class AppleSiliconDetour
    {
        public static readonly bool IsRequired =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        [DllImport("FsrJitWrite")]
        static extern void fsr_write_code(IntPtr destination, byte[] source, UIntPtr length);

        public static void DetourMethod(MethodBase original, MethodBase replacement)
        {
            var from = GetNativeStart(original);
            var to = GetNativeStart(replacement);
            if (from == to)
            {
                return;
            }

            // ldr x16, #8 ; br x16 ; .quad to
            var jump = new byte[16];
            BitConverter.GetBytes(0x58000050u).CopyTo(jump, 0);
            BitConverter.GetBytes(0xD61F0200u).CopyTo(jump, 4);
            BitConverter.GetBytes(to.ToInt64()).CopyTo(jump, 8);
            fsr_write_code(from, jump, (UIntPtr)jump.Length);
        }

        static IntPtr GetNativeStart(MethodBase method)
        {
            // Compiles the method if needed, so the pointer is its machine code rather than a JIT trampoline.
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return method.MethodHandle.GetFunctionPointer();
        }
    }
}
#endif
