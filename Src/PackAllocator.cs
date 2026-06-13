using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if UNITY_5_3_OR_NEWER
using Unity.Collections.LowLevel.Unsafe;
#endif

namespace FFS.Libraries.StaticPack {
    /// <summary>
    /// Optional custom allocator for <see cref="BinaryPackWriter"/>. Fully unmanaged (function pointers only),
    /// so a writer carrying it remains a valid field of a Burst job.
    /// <para>An allocator is bound at creation through <c>BinaryPackWriter.Create(capacity, allocator)</c>
    /// and owns the buffer end to end: the initial allocation, every growth through <see cref="Realloc"/>,
    /// and the release through <see cref="Free"/> on <c>Dispose</c>.</para>
    /// <para>The function pointers use the C (Cdecl) calling convention so the calls compile inside
    /// Burst (<c>calli</c> with the default managed convention is rejected by Burst). Producing them:
    /// Burst-compiled code — <c>(delegate* unmanaged[Cdecl]&lt;...&gt;) BurstCompiler.CompileFunctionPointer&lt;ReallocDelegate&gt;(MyRealloc).Value</c>;
    /// plain .NET — <c>&amp;MyRealloc</c> on a method marked <c>[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]</c>;
    /// anything else — marshalled thunks via <see cref="FromDelegates"/>. A pointer to an ordinary managed
    /// method is not valid here.</para>
    /// <para>Both callbacks cross an unmanaged boundary: an exception must not escape them (that tears the
    /// process down instead of surfacing at the call site) and <see cref="Realloc"/> must never return null.</para>
    /// </summary>
    public readonly unsafe struct PackAllocator {
        /// <summary> Managed signature of <see cref="Realloc"/> for <see cref="FromDelegates"/>. </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate byte* ReallocDelegate(void* state, byte* oldPtr, uint oldCapacity, uint usedBytes, uint newCapacity);

        /// <summary> Managed signature of <see cref="Free"/> for <see cref="FromDelegates"/>. </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FreeDelegate(void* state, byte* ptr);

        /// <summary> Opaque user state passed as the first argument to <see cref="Realloc"/> and <see cref="Free"/>. </summary>
        #if UNITY_5_3_OR_NEWER
        [NativeDisableUnsafePtrRestriction]
        #endif
        public readonly void* State;
        public readonly IntPtr ReallocPtr;
        public readonly IntPtr FreePtr;

        /// <summary>
        /// (state, oldPtr, oldCapacity, usedBytes, newCapacity) -> newPtr.
        /// <para><c>oldCapacity</c> is the size of the old block, which the allocator needs for its own bookkeeping;
        /// <c>usedBytes</c> is how much of it actually has to survive. Copy <c>min(usedBytes, newCapacity)</c> bytes
        /// and release the old block — copying the whole <c>oldCapacity</c> is correct but wastes time on the
        /// unwritten tail, and <c>newCapacity</c> may be smaller than <c>oldCapacity</c> (a shrink), so never copy
        /// more than <c>newCapacity</c> bytes.</para>
        /// <para>Called with <c>oldPtr == null, oldCapacity == 0, usedBytes == 0</c> for the initial allocation
        /// (<c>BinaryPackWriter.Create(capacity, allocator)</c>) — implementations must treat that as a fresh alloc.</para>
        /// </summary>
        public delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*> Realloc {
            [MethodImpl(AggressiveInlining)] get => (delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*>)ReallocPtr;
        }

        /// <summary> (state, ptr). Required; pass an empty method when there is nothing to release on Dispose. </summary>
        public delegate* unmanaged[Cdecl]<void*, byte*, void> Free {
            [MethodImpl(AggressiveInlining)] get => (delegate* unmanaged[Cdecl]<void*, byte*, void>)FreePtr;
        }

        [MethodImpl(AggressiveInlining)]
        public PackAllocator(delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*> realloc, delegate* unmanaged[Cdecl]<void*, byte*, void> free, void* state = null) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (realloc == null)
                throw new Exception("[StaticPack] PackAllocator: realloc is null");
            if (free == null)
                throw new Exception("[StaticPack] PackAllocator: free is null, pass an empty method when there is nothing to release");
            #endif
            ReallocPtr = (IntPtr)realloc;
            FreePtr = (IntPtr)free;
            State = state;
        }

        /// <summary>
        /// Builds an allocator from managed methods: the delegates are marshalled into native-callable
        /// (Cdecl) thunks and rooted for the process lifetime — intended for long-lived allocators,
        /// not per-frame creation. On IL2CPP the target methods must be static and carry
        /// <c>[AOT.MonoPInvokeCallback]</c>.
        /// <para>The resulting pointers are reverse-pinvoke thunks: callable from regular C# (and from
        /// IL2CPP-compiled jobs), but NOT Burst-optimized. For an allocator that must grow/free from inside
        /// a Burst-compiled job, produce the pointers via <c>BurstCompiler.CompileFunctionPointer</c> and use
        /// the raw-pointer constructor instead.</para>
        /// <para>Each <see cref="GCHandle"/> is allocated and intentionally never freed — a deliberate
        /// process-lifetime root keeping the marshalled thunk valid forever.</para>
        /// </summary>
        public static PackAllocator FromDelegates(ReallocDelegate realloc, FreeDelegate free, void* state = null) {
            GCHandle.Alloc(realloc);
            GCHandle.Alloc(free);
            return new PackAllocator(
                (delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*>)Marshal.GetFunctionPointerForDelegate(realloc),
                (delegate* unmanaged[Cdecl]<void*, byte*, void>)Marshal.GetFunctionPointerForDelegate(free),
                state);
        }

        public bool IsCreated {
            [MethodImpl(AggressiveInlining)] get => Realloc != null;
        }
    }
}
