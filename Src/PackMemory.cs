using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if UNITY_5_3_OR_NEWER
using AOT;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
#endif
#if UNITY_5_3_OR_NEWER && FFS_BURST
using Unity.Burst;
#endif
#if DEBUG || FFS_PACK_ENABLE_DEBUG
using System.Collections.Concurrent;
using System.Text;
#endif

namespace FFS.Libraries.StaticPack {
    /// <summary>
    /// Native memory backend for owned reader/writer buffers.
    /// Unity: <c>UnsafeUtility.Malloc/Free</c> (tracked variants in debug, feeding Unity's Native Leak Detection);
    /// .NET 6+: <c>NativeMemory</c>; netstandard2.1: <c>Marshal.AllocHGlobal</c>.
    /// Unity's <c>Allocator</c> exists only in the Unity compilation path: it is packed into
    /// <see cref="PackAllocator.State"/> of the Unity <see cref="Default(Allocator)"/> allocator.
    /// <para>The allocator function pointers are produced once and cached: under Unity+Burst via
    /// <c>BurstCompiler.CompileFunctionPointer</c> (so writers grow/dispose from inside Burst jobs); on
    /// .NET 6+ via <c>[UnmanagedCallersOnly]</c> + <c>&amp;Method</c> (no marshalling); otherwise via
    /// marshalled Cdecl thunks. Init is eager (<c>static readonly</c>) so the two pointers publish atomically.</para>
    /// </summary>
    #if UNITY_5_3_OR_NEWER && FFS_BURST
    [BurstCompile]
    #endif
    public static unsafe class PackMemory {
        /// <summary> The two Cdecl-callable function pointers of a native allocator backend. </summary>
        internal readonly struct Backend {
            #if UNITY_5_3_OR_NEWER
            [NativeDisableUnsafePtrRestriction]
            #endif
            public readonly delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*> Realloc;
            #if UNITY_5_3_OR_NEWER
            [NativeDisableUnsafePtrRestriction]
            #endif
            public readonly delegate* unmanaged[Cdecl]<void*, byte*, void> Free;

            public Backend(delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*> realloc, delegate* unmanaged[Cdecl]<void*, byte*, void> free) {
                Realloc = realloc;
                Free = free;
            }
        }

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        // SharedStatic so the cached pointers are readable from Burst-compiled code (a plain static field is
        // not). Burst-compiled via the auto-warmup below — that one CompileFunctionPointer step is the only
        // managed/main-thread action; afterwards Default/Create work from inside Burst jobs too.
        private struct Key { }
        private static readonly SharedStatic<(IntPtr, IntPtr)> _backend = SharedStatic<(IntPtr, IntPtr)>.GetOrCreate<(IntPtr, IntPtr), Key>();

        /// <summary>
        /// Produces (and caches) the allocator function pointers on the calling thread. Auto-invoked once on
        /// the main thread before the first scene loads and, in the Editor, before the first domain reload
        /// finishes; also call it manually after enabling Burst at runtime. Calling it up front is an
        /// optional front-load, not a hard prerequisite: <see cref="GetBackend"/> self-warms from managed
        /// code (Edit mode, EditMode tests, editor tooling included) the same way on first use.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void WarmUp() => EnsureBackendCreated();

        #if UNITY_EDITOR
        /// <summary> Mirrors <see cref="WarmUp"/> for Edit mode, which RuntimeInitializeOnLoadMethod does not reach. </summary>
        [UnityEditor.InitializeOnLoadMethod]
        private static void WarmUpInEditor() => EnsureBackendCreated();
        #endif

        [BurstDiscard]
        private static void EnsureBackendCreated() {
            if (_backend.Data.Item1 == default) {
                var backend = CreateBackend();
                _backend.Data = ((IntPtr)backend.Realloc, (IntPtr)backend.Free);
            }
        }

        [MethodImpl(AggressiveInlining)]
        private static Backend GetBackend() {
            EnsureBackendCreated();
            var (realloc, free) = _backend.Data;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (realloc == default) {
                throw new Exception("[StaticPack] allocator backend is not initialized - call PackMemory.WarmUp() on the main thread before using it from Burst-compiled code");
            }
            #endif
            return new Backend((delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*>)realloc, (delegate* unmanaged[Cdecl]<void*, byte*, void>)free);
        }
        #else
        private static readonly Backend _backend = CreateBackend();

        #if UNITY_5_3_OR_NEWER
        /// <summary> No-op without Burst: the backend is an eagerly-initialized static field. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void WarmUp() { }
        #endif

        [MethodImpl(AggressiveInlining)]
        private static Backend GetBackend() => _backend;
        #endif

        private static Backend CreateBackend() {
            #if UNITY_5_3_OR_NEWER && FFS_BURST
            var realloc = (delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*>) BurstCompiler.CompileFunctionPointer<PackAllocator.ReallocDelegate>(DefaultRealloc).Value;
            var free = (delegate* unmanaged[Cdecl]<void*, byte*, void>) BurstCompiler.CompileFunctionPointer<PackAllocator.FreeDelegate>(DefaultFree).Value;
            return new Backend(realloc, free);
            #elif NET6_0_OR_GREATER
            return new Backend(&DefaultRealloc, &DefaultFree);
            #else
            var allocator = PackAllocator.FromDelegates(DefaultRealloc, DefaultFree);
            return new Backend(allocator.Realloc, allocator.Free);
            #endif
        }

        /// <summary> The built-in backend as a <see cref="PackAllocator"/> (Persistent in Unity). </summary>
        [MethodImpl(AggressiveInlining)]
        public static PackAllocator Default() {
            #if UNITY_5_3_OR_NEWER
            return Default(Allocator.Persistent);
            #else
            var backend = GetBackend();
            return new PackAllocator(backend.Realloc, backend.Free);
            #endif
        }

        #if UNITY_5_3_OR_NEWER
        /// <summary> The built-in backend over a specific Unity allocator, packed into <see cref="PackAllocator.State"/>. </summary>
        [MethodImpl(AggressiveInlining)]
        public static PackAllocator Default(Allocator allocator) {
            var backend = GetBackend();
            return new PackAllocator(backend.Realloc, backend.Free, (void*) (nint) (int) allocator);
        }

        /// <summary>
        /// True when the writer's buffer can grow/free from inside Burst-compiled code, i.e. it is backed by
        /// the built-in Burst-compiled backend. Always false when Burst is not enabled.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static bool IsBurstCompatible(in BinaryPackWriter writer) {
            #if FFS_BURST
            // Compare as raw addresses: both sides are copies of the same cached pointer value, so address
            // equality is exactly what we want. The void* cast avoids CS8909 (function-pointer comparison warning).
            var realloc = (void*) writer.Allocator.Realloc;
            return realloc == (void*) GetBackend().Realloc || realloc == (void*) PackArenaAllocator.GetArenaBackend().Realloc;
            #else
            return false;
            #endif
        }
        #endif

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        [BurstCompile]
        [MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]
        #elif UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]
        #elif NET6_0_OR_GREATER
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        #endif
        private static byte* DefaultRealloc(void* state, byte* oldPtr, uint oldCapacity, uint usedBytes, uint newCapacity) {
            #if UNITY_5_3_OR_NEWER
            var newPtr = AllocRaw(newCapacity, (Allocator) (int) (nint) state);
            #else
            var newPtr = AllocRaw(newCapacity);
            #endif
            if (oldPtr != null) {
                if (usedBytes != 0) {
                    Copy(newPtr, oldPtr, usedBytes < newCapacity ? usedBytes : newCapacity);
                }

                #if UNITY_5_3_OR_NEWER
                FreeRaw(oldPtr, (Allocator) (int) (nint) state);
                #else
                FreeRaw(oldPtr);
                #endif
            }

            return newPtr;
        }

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        [BurstCompile]
        [MonoPInvokeCallback(typeof(PackAllocator.FreeDelegate))]
        #elif UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(PackAllocator.FreeDelegate))]
        #elif NET6_0_OR_GREATER
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        #endif
        private static void DefaultFree(void* state, byte* ptr) {
            #if UNITY_5_3_OR_NEWER
            FreeRaw(ptr, (Allocator) (int) (nint) state);
            #else
            FreeRaw(ptr);
            #endif
        }

        /// <summary>
        /// Raw native allocation. In debug it uses Unity's tracked variant (feeding Native Leak Detection);
        /// </summary>
        #if UNITY_5_3_OR_NEWER
        [MethodImpl(AggressiveInlining)]
        public static byte* AllocRaw(uint size, Allocator allocator = Allocator.Persistent) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            return (byte*) UnsafeUtility.MallocTracked(size, 16, allocator, 0);
            #else
            return (byte*) UnsafeUtility.Malloc(size, 16, allocator);
            #endif
        }

        /// <inheritdoc cref="AllocRaw"/>
        [MethodImpl(AggressiveInlining)]
        public static void FreeRaw(byte* ptr, Allocator allocator = Allocator.Persistent) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            UnsafeUtility.FreeTracked(ptr, allocator);
            #else
            UnsafeUtility.Free(ptr, allocator);
            #endif
        }
        #else
        [MethodImpl(AggressiveInlining)]
        public static byte* AllocRaw(uint size) {
            #if NET6_0_OR_GREATER
            return (byte*)NativeMemory.Alloc(size);
            #else
            return (byte*) Marshal.AllocHGlobal((IntPtr) size);
            #endif
        }

        /// <inheritdoc cref="AllocRaw"/>
        [MethodImpl(AggressiveInlining)]
        public static void FreeRaw(byte* ptr) {
            #if NET6_0_OR_GREATER
            NativeMemory.Free(ptr);
            #else
            Marshal.FreeHGlobal((IntPtr) ptr);
            #endif
        }
        #endif

        /// <summary> Non-overlapping copy. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void Copy(byte* dst, byte* src, uint size) {
            #if UNITY_5_3_OR_NEWER
            UnsafeUtility.MemCpy(dst, src, size);
            #else
            Buffer.MemoryCopy(src, dst, size, size);
            #endif
        }

        /// <summary> Fills the region with zeroes. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void Clear(byte* ptr, uint size) {
            #if UNITY_5_3_OR_NEWER
            UnsafeUtility.MemClear(ptr, size);
            #else
            while (size > 0) {
                var chunk = size > int.MaxValue ? int.MaxValue : (int)size;
                new Span<byte>(ptr, chunk).Clear();
                ptr += chunk;
                size -= (uint)chunk;
            }
            #endif
        }

        /// <summary> Overlap-safe copy (memmove semantics). </summary>
        [MethodImpl(AggressiveInlining)]
        public static void Move(byte* dst, byte* src, uint size) {
            #if UNITY_5_3_OR_NEWER
            UnsafeUtility.MemMove(dst, src, size);
            #else
            // Span.CopyTo is documented to work correctly for overlapping regions; chunks run away from the
            // destination so that a chunk never overwrites source bytes a later chunk still has to read.
            if (dst < src) {
                while (size > 0) {
                    var chunk = size > int.MaxValue ? int.MaxValue : (int)size;
                    new ReadOnlySpan<byte>(src, chunk).CopyTo(new Span<byte>(dst, chunk));
                    dst += chunk;
                    src += chunk;
                    size -= (uint)chunk;
                }
            } else {
                while (size > 0) {
                    var chunk = size > int.MaxValue ? int.MaxValue : (int)size;
                    size -= (uint)chunk;
                    new ReadOnlySpan<byte>(src + size, chunk).CopyTo(new Span<byte>(dst + size, chunk));
                }
            }
            #endif
        }

        /// <summary> Allocates <paramref name="count"/> elements, zeroed unless <paramref name="clear"/> is cleared. </summary>
        [MethodImpl(AggressiveInlining)]
        public static T* Alloc<T>(this PackAllocator alloc, long count, bool clear = true) where T : unmanaged {
            var bytes = ToBytes(sizeof(T) * count);
            var malloc = alloc.Realloc(alloc.State, null, 0, 0, bytes);
            if (clear) {
                Clear(malloc, bytes);
            }

            return (T*)malloc;
        }

        [MethodImpl(AggressiveInlining)]
        public static T** Alloc2D<T>(this PackAllocator alloc, long count, bool clear = true) where T : unmanaged {
            var bytes = ToBytes(sizeof(void*) * count);
            var malloc = alloc.Realloc(alloc.State, null, 0, 0, bytes);
            if (clear) {
                Clear(malloc, bytes);
            }

            return (T**)malloc;
        }

        [MethodImpl(AggressiveInlining)]
        public static T*** Alloc3D<T>(this PackAllocator alloc, long count, bool clear = true) where T : unmanaged {
            var bytes = ToBytes(sizeof(void*) * count);
            var malloc = alloc.Realloc(alloc.State, null, 0, 0, bytes);
            if (clear) {
                Clear(malloc, bytes);
            }

            return (T***)malloc;
        }

        [MethodImpl(AggressiveInlining)]
        public static void ReAlloc<T>(this PackAllocator alloc, ref T* old, long oldCapacity, long newCapacity, bool clear = true) where T : unmanaged {
            var oldBytes = ToBytes(sizeof(T) * oldCapacity);
            var newBytes = ToBytes(sizeof(T) * newCapacity);
            var malloc = alloc.Realloc(alloc.State, (byte*)old, oldBytes, oldBytes, newBytes);
            if (clear && newBytes > oldBytes) {
                Clear(malloc + oldBytes, newBytes - oldBytes);
            }

            old = (T*)malloc;
        }

        [MethodImpl(AggressiveInlining)]
        public static void ReAlloc<T>(this PackAllocator alloc, ref T** old, long oldCapacity, long newCapacity, bool clear = true) where T : unmanaged {
            var oldBytes = ToBytes(sizeof(void*) * oldCapacity);
            var newBytes = ToBytes(sizeof(void*) * newCapacity);
            var malloc = alloc.Realloc(alloc.State, (byte*)old, oldBytes, oldBytes, newBytes);
            if (clear && newBytes > oldBytes) {
                Clear(malloc + oldBytes, newBytes - oldBytes);
            }

            old = (T**)malloc;
        }

        [MethodImpl(AggressiveInlining)]
        public static void FreeSafe(this PackAllocator alloc, void* memory) {
            if (memory == null) {
                return;
            }

            alloc.Free(alloc.State, (byte*)memory);
        }

        [MethodImpl(AggressiveInlining)]
        public static void Clear<T>(T* ptr, int count) where T : unmanaged {
            Clear((byte*)ptr, ToBytes((long)count * sizeof(T)));
        }

        [MethodImpl(AggressiveInlining)]
        public static void Copy<T>(T* dst, T* src, int count) where T : unmanaged {
            Copy((byte*)dst, (byte*)src, ToBytes((long)count * sizeof(T)));
        }

        [MethodImpl(AggressiveInlining)]
        public static void Fill<T>(T* ptr, int count, T value) where T : unmanaged {
            #if FFS_BURST
            UnsafeUtility.MemCpyReplicate(ptr, &value, sizeof(T), count);
            #else
            new Span<T>(ptr, count).Fill(value);
            #endif
        }

        /// <summary> Type-erased pointer-array allocation (array of <c>count</c> pointers). </summary>
        [MethodImpl(AggressiveInlining)]
        public static void** Alloc2DRaw(this PackAllocator alloc, long count, bool clear = true) {
            var bytes = ToBytes(sizeof(void*) * count);
            var malloc = alloc.Realloc(alloc.State, null, 0, 0, bytes);
            if (clear) {
                Clear(malloc, bytes);
            }

            return (void**)malloc;
        }

        /// <summary> Type-erased reallocation of a pointer array. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void ReAllocRaw(this PackAllocator alloc, ref void** old, long oldCapacity, long newCapacity, bool clear = true) {
            var oldBytes = ToBytes(sizeof(void*) * oldCapacity);
            var newBytes = ToBytes(sizeof(void*) * newCapacity);
            var malloc = alloc.Realloc(alloc.State, (byte*)old, oldBytes, oldBytes, newBytes);
            if (clear && newBytes > oldBytes) {
                Clear(malloc + oldBytes, newBytes - oldBytes);
            }

            old = (void**)malloc;
        }

        /// <summary> Type-erased replicate-fill: copies <c>elemSize</c> bytes from <c>value</c> into each of <c>count</c> slots. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void FillRaw(byte* dst, byte* value, int elemSize, int count) {
            #if FFS_BURST
            UnsafeUtility.MemCpyReplicate(dst, value, elemSize, count);
            #else
            for (var i = 0; i < count; i++) {
                Copy(dst + (long)i * elemSize, value, (uint)elemSize);
            }
            #endif
        }

        [MethodImpl(AggressiveInlining)]
        private static uint ToBytes(long bytes) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if ((ulong)bytes > uint.MaxValue) {
                throw new Exception($"[StaticPack] allocation size {bytes} exceeds uint.MaxValue (PackAllocator capacity is uint)");
            }
            #endif
            return (uint)bytes;
        }
    }

    /// <summary>
    /// Tracking of owned native allocations. The tracking itself runs only when DEBUG / FFS_PACK_ENABLE_DEBUG
    /// is defined (e.g. the FFS.StaticPack.Debug assembly); in other builds the members below stay callable and
    /// report an empty state, so calls do not need to be wrapped in <c>#if</c>.
    /// Detects leaks (allocations never disposed, with the allocation stack trace) and
    /// double-Dispose — including Dispose of two copies of the same reader/writer struct,
    /// or Dispose of a stale copy after another copy resized the buffer.
    /// In Burst-compiled code the tracking calls are discarded ([BurstDiscard]); checks remain
    /// active in mono/editor runs, and Unity's own Native Leak Detection covers Burst paths
    /// via MallocTracked/FreeTracked.
    /// </summary>
    public static unsafe class BinaryPackLeakTracker {
        #if DEBUG || FFS_PACK_ENABLE_DEBUG
        private static readonly ConcurrentDictionary<uint, (IntPtr Ptr, string StackTrace)> _live = new();
        private static int _nextId;
        private static int _clearedThroughId;
        // ReSharper disable once UnusedMember.Local — finalizer reports leaks at domain unload.
        private static readonly LeakSentinel _sentinel = new();

        /// <summary>
        /// Whether <see cref="Report"/> should carry the allocation stack trace. Capturing it costs roughly
        /// 200 microseconds and a few kilobytes per allocation in the Unity Editor, so turn it off while
        /// profiling; the allocation itself stays tracked either way.
        /// </summary>
        public static bool CaptureStackTraces = true;

        private sealed class LeakSentinel {
            internal LeakSentinel() {
                // .NET Core runs no finalizer for a reachable object at process exit, so the finalizer below
                // never reports there. Subscribing from here instead of a static constructor keeps
                // BinaryPackLeakTracker beforefieldinit, so TrackAlloc/TrackFree stay free of a class-init check.
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
            }

            ~LeakSentinel() => Flush();
        }

        private static void Flush() {
            if (!_live.IsEmpty) {
                LogError(Report());
            }
        }

        private static void LogError(string message) {
            #if UNITY_5_3_OR_NEWER
            UnityEngine.Debug.LogError(message);
            #else
            Console.Error.WriteLine(message);
            #endif
        }

        #if UNITY_5_3_OR_NEWER
        /// <summary>
        /// With Domain Reload disabled the statics survive a Play session, so allocations leaked in the previous
        /// one would otherwise still be listed in the next. Report them once and start the session clean.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ReportAndResetBeforePlay() {
            if (!_live.IsEmpty) {
                LogError(Report());
                Clear();
            }
        }
        #endif

        /// <summary> Number of live (not yet freed) owned native allocations. </summary>
        public static int Count {
            [MethodImpl(AggressiveInlining)] get => _live.Count;
        }

        /// <summary> Human-readable report of live allocations with their allocation stack traces. </summary>
        public static string Report() {
            if (_live.IsEmpty)
                return "[StaticPack] No live native allocations";
            var sb = new StringBuilder();
            sb.Append("[StaticPack] Native memory leaks detected: ").Append(_live.Count).AppendLine(" allocation(s) were never disposed:");
            foreach (var kv in _live) {
                sb.Append(" - allocId ").Append(kv.Key).Append(", ptr 0x").Append(kv.Value.Ptr.ToString("X")).AppendLine(", allocated at:");
                sb.AppendLine(kv.Value.StackTrace);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Forgets all tracked allocations (does not free memory). Intended for tests: a later Dispose of a
        /// buffer allocated before this call is accepted instead of being reported as a double Dispose.
        /// </summary>
        public static void Clear() {
            _clearedThroughId = System.Threading.Volatile.Read(ref _nextId);
            _live.Clear();
        }

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        [BurstDiscard]
        #endif
        [MethodImpl(AggressiveInlining)]
        internal static void TrackAlloc(byte* ptr, ref uint allocId) {
            do {
                allocId = (uint)System.Threading.Interlocked.Increment(ref _nextId);
            } while (allocId == 0);

            _live[allocId] = ((IntPtr)ptr, CaptureStackTraces ? Environment.StackTrace : "<stack traces disabled>");
        }

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        [BurstDiscard]
        #endif
        [MethodImpl(AggressiveInlining)]
        internal static void TrackFree(uint allocId) {
            if (allocId != 0 && !_live.TryRemove(allocId, out _) && allocId > (uint)System.Threading.Volatile.Read(ref _clearedThroughId)) {
                throw new Exception(
                    "[StaticPack] Buffer is already freed: double Dispose — possibly Dispose of two copies of the same reader/writer struct, or Dispose of a stale copy after another copy resized the buffer");
            }
        }
        #else
        /// <summary> Whether a tracked allocation records its stack trace. No effect in a build without tracking. </summary>
        public static bool CaptureStackTraces;

        /// <summary> Number of live (not yet freed) owned native allocations; always 0 without tracking. </summary>
        public static int Count {
            [MethodImpl(AggressiveInlining)] get => 0;
        }

        /// <inheritdoc cref="Count"/>
        public static string Report() {
            return "[StaticPack] Leak tracking is compiled out of this build (define DEBUG or FFS_PACK_ENABLE_DEBUG to enable it)";
        }

        /// <inheritdoc cref="Count"/>
        public static void Clear() { }
        #endif
    }

    /// <summary>
    /// The low-level reinterpretation primitives, re-exported so that the rest of the FFS libraries reach them
    /// through StaticPack instead of referencing System.Runtime.CompilerServices.Unsafe themselves: only this
    /// package then ships and declares that assembly. Every member forwards directly and is inlined away.
    /// </summary>
    public static unsafe class PackUnsafe {
        /// <summary> Size of <typeparamref name="T"/> in bytes, for unconstrained <typeparamref name="T"/> as well. </summary>
        [MethodImpl(AggressiveInlining)]
        public static int SizeOf<T>() => Unsafe.SizeOf<T>();

        /// <summary> Reinterprets a reference to <typeparamref name="TFrom"/> as a reference to <typeparamref name="TTo"/>. </summary>
        [MethodImpl(AggressiveInlining)]
        public static ref TTo As<TFrom, TTo>(ref TFrom source) => ref Unsafe.As<TFrom, TTo>(ref source);

        /// <summary> Reinterprets <paramref name="source"/> as a reference to <typeparamref name="T"/>. </summary>
        [MethodImpl(AggressiveInlining)]
        public static ref T AsRef<T>(void* source) => ref Unsafe.AsRef<T>(source);

        /// <summary> Address of <paramref name="value"/>; the caller keeps it alive for the pointer's lifetime. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void* AsPointer<T>(ref T value) => Unsafe.AsPointer(ref value);

        /// <summary> The reference <paramref name="elementOffset"/> elements past <paramref name="source"/>. </summary>
        [MethodImpl(AggressiveInlining)]
        public static ref T Add<T>(ref T source, int elementOffset) => ref Unsafe.Add(ref source, elementOffset);
    }
}
