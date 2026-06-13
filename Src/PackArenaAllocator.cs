using System;
using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if UNITY_5_3_OR_NEWER
using AOT;
using Unity.Collections.LowLevel.Unsafe;
#endif

#if UNITY_5_3_OR_NEWER && FFS_BURST
using Unity.Burst;
#endif

namespace FFS.Libraries.StaticPack {
    /// <summary> Overflow behavior of <see cref="PackArenaAllocator"/>. </summary>
    public enum PackArenaMode : byte {
        /// <summary> Overflow allocations fall back to the default allocator one by one; arena capacity never changes. </summary>
        SpillToDefault = 0,

        /// <summary>
        /// The arena grows: the current chunk is retired (its contents stay valid until Reset) and bumping
        /// continues in a new chunk of at least double capacity. Reset keeps the largest chunk, so the
        /// arena converges to the real per-frame demand and stops allocating.
        /// </summary>
        AutoResize = 1,
    }

    /// <summary>
    /// Frame-scoped bump allocator, usable as a <see cref="PackAllocator"/> for writers.
    /// <para>A native chunk is allocated up front; every allocation is a pointer bump, individual
    /// <c>Dispose</c> of a writer is a no-op (<see cref="PackAllocator.Free"/> does nothing), and all
    /// memory is reclaimed at once by <see cref="Reset"/> — typically at the end of a frame.</para>
    /// <para>Overflow behavior is selected by <see cref="PackArenaMode"/>. In both modes every pointer
    /// handed out stays valid until <see cref="Reset"/>/<see cref="Dispose"/> — nothing is moved or freed
    /// mid-frame, so the no-op Free never leaks.</para>
    /// <para>The backing memory always comes from the default allocator.</para>
    /// <para>Alignment: allocations are 16-aligned relative to the block base. The absolute 16-byte
    /// guarantee holds in Unity (explicit alignment) and on 64-bit .NET (malloc contract); on 32-bit
    /// netstandard2.1 runtimes the minimum is the allocator's natural alignment (8).</para>
    /// <para>Not thread-safe: use one arena per thread.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// // the arena lives across frames — no `using`, dispose it at the end of its lifecycle
    /// var arena = PackArenaAllocator.Create(64 * 1024, PackArenaMode.AutoResize);
    /// // each frame:
    /// var writer = arena.CreateWriter(1024);
    /// writer.WriteInt(42);            // grows inside the arena when needed
    /// // writer.Dispose() is optional and does nothing
    /// arena.Reset();                  // end of frame: all arena memory is reusable again
    /// // end of the arena's lifecycle (e.g. system/scene shutdown):
    /// arena.Dispose();
    /// </code>
    /// </example>
    #if UNITY_5_3_OR_NEWER && FFS_BURST
    [BurstCompile]
    #endif
    public unsafe struct PackArenaAllocator : IDisposable {
        private const uint ALIGN = 16;
        private const uint BLOCK_HEADER = 16; // stores the next-block pointer, keeps payload 16-aligned

        private struct State {
            public byte* Buffer;      // payload of the current bump chunk; its block starts at Buffer - BLOCK_HEADER
            public uint Capacity;     // capacity of the current chunk
            public uint Offset;       // bump cursor inside the current chunk
            public byte* RetiredHead; // linked blocks released on Reset: spill blocks (SpillToDefault) or outgrown chunks (AutoResize)
            public bool AutoResize;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            public int OwnerThreadId;
            #endif
        }

        #if UNITY_5_3_OR_NEWER
        [NativeDisableUnsafePtrRestriction]
        #endif
        private State* _state;
        #if DEBUG || FFS_PACK_ENABLE_DEBUG
        private uint _allocId; // debug id of the state block
        #endif

        [MethodImpl(AggressiveInlining)]
        private PackArenaAllocator(State* state) {
            _state = state;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            _allocId = 0;
            #endif
        }

        /// <summary> Allocates an arena with <paramref name="capacity"/> bytes of bump space. </summary>
        public static PackArenaAllocator Create(uint capacity, PackArenaMode mode = PackArenaMode.SpillToDefault) {
            // Unconditional: BLOCK_HEADER + capacity in AllocBlock must not wrap, otherwise a tiny block
            // would be paired with a huge Capacity and every fit check below would pass.
            if (capacity > uint.MaxValue - BLOCK_HEADER) {
                throw new Exception("[StaticPack] PackArenaAllocator: capacity is too large");
            }

            var buffer = AllocBlock(capacity);
            var stateBlock = PackMemory.AllocRaw((uint)sizeof(State));
            var state = (State*)stateBlock;
            state->Buffer = buffer;
            state->Capacity = capacity;
            state->Offset = 0;
            state->RetiredHead = null;
            state->AutoResize = mode == PackArenaMode.AutoResize;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            state->OwnerThreadId = Environment.CurrentManagedThreadId;
            #endif
            var arena = new PackArenaAllocator(state);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            BinaryPackLeakTracker.TrackAlloc(stateBlock, ref arena._allocId);
            #endif
            return arena;
        }

        public bool IsCreated {
            [MethodImpl(AggressiveInlining)] get => _state != null;
        }

        /// <summary> Bump capacity of the current chunk in bytes. </summary>
        public uint Capacity {
            [MethodImpl(AggressiveInlining)]
            get {
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                if (_state == null)
                    throw new Exception("[StaticPack] PackArenaAllocator is not created or already disposed");
                #endif
                return _state->Capacity;
            }
        }

        /// <summary> Bytes currently consumed from the current chunk. </summary>
        public uint Used {
            [MethodImpl(AggressiveInlining)]
            get {
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                if (_state == null)
                    throw new Exception("[StaticPack] PackArenaAllocator is not created or already disposed");
                #endif
                return _state->Offset;
            }
        }

        /// <summary> True when the arena overflowed since the last Reset (spilled or grew, depending on the mode). </summary>
        public bool Overflowed {
            [MethodImpl(AggressiveInlining)]
            get {
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                if (_state == null)
                    throw new Exception("[StaticPack] PackArenaAllocator is not created or already disposed");
                #endif
                return _state->RetiredHead != null;
            }
        }

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        // SharedStatic so AsPackAllocator/Create can run from inside Burst jobs (a plain static field is not
        // Burst-readable). Stores the pointers as IntPtr because SharedStatic<T> rejects a payload with
        // function-pointer fields. A distinct Key (vs PackMemory's) keeps the shared slot separate.
        private struct Key { }
        private static readonly SharedStatic<(IntPtr, IntPtr)> _arenaBackend = SharedStatic<(IntPtr, IntPtr)>.GetOrCreate<(IntPtr, IntPtr), Key>();

        /// <summary>
        /// Produces (and caches) the arena allocator function pointers on the calling thread. Auto-invoked
        /// once on the main thread before the first scene loads and, in the Editor, before the first domain
        /// reload finishes; also call it manually after enabling Burst at runtime. Calling it up front is an
        /// optional front-load, not a hard prerequisite: <see cref="GetArenaBackend"/> self-warms from
        /// managed code (Edit mode, EditMode tests, editor tooling included) the same way on first use.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void WarmUp() => EnsureArenaBackendCreated();

        #if UNITY_EDITOR
        /// <summary> Mirrors <see cref="WarmUp"/> for Edit mode, which RuntimeInitializeOnLoadMethod does not reach. </summary>
        [UnityEditor.InitializeOnLoadMethod]
        private static void WarmUpInEditor() => EnsureArenaBackendCreated();
        #endif

        [BurstDiscard]
        private static void EnsureArenaBackendCreated() {
            if (_arenaBackend.Data.Item1 == default) {
                var backend = MakeArenaBackend();
                _arenaBackend.Data = ((IntPtr) backend.Realloc, (IntPtr) backend.Free);
            }
        }

        [MethodImpl(AggressiveInlining)]
        internal static PackMemory.Backend GetArenaBackend() {
            EnsureArenaBackendCreated();
            var (realloc, free) = _arenaBackend.Data;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (realloc == default) {
                throw new Exception("[StaticPack] allocator backend is not initialized - call PackArenaAllocator.WarmUp() on the main thread before using it from Burst-compiled code");
            }
            #endif
            return new PackMemory.Backend((delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*>) realloc, (delegate* unmanaged[Cdecl]<void*, byte*, void>) free);
        }
        #else
        private static readonly PackMemory.Backend _arenaBackend = MakeArenaBackend();

        #if UNITY_5_3_OR_NEWER
        /// <summary> No-op without Burst: the backend is an eagerly-initialized static field. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void WarmUp() { }
        #endif

        [MethodImpl(AggressiveInlining)]
        internal static PackMemory.Backend GetArenaBackend() => _arenaBackend;
        #endif

        private static PackMemory.Backend MakeArenaBackend() {
            #if UNITY_5_3_OR_NEWER && FFS_BURST
            var realloc = (delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, byte*>) BurstCompiler.CompileFunctionPointer<PackAllocator.ReallocDelegate>(ArenaRealloc).Value;
            var free = (delegate* unmanaged[Cdecl]<void*, byte*, void>) BurstCompiler.CompileFunctionPointer<PackAllocator.FreeDelegate>(ArenaFree).Value;
            return new PackMemory.Backend(realloc, free);
            #elif NET6_0_OR_GREATER
            return new PackMemory.Backend(&ArenaRealloc, &ArenaFree);
            #else
            var allocator = PackAllocator.FromDelegates(ArenaRealloc, ArenaFree);
            return new PackMemory.Backend(allocator.Realloc, allocator.Free);
            #endif
        }

        /// <summary> This arena as a writer allocator. </summary>
        [MethodImpl(AggressiveInlining)]
        public PackAllocator AsPackAllocator() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (_state == null)
                throw new Exception("[StaticPack] PackArenaAllocator is not created or already disposed");
            AssertOwnerThread();
            #endif
            var backend = GetArenaBackend();
            return new PackAllocator(backend.Realloc, backend.Free, _state);
        }

        /// <inheritdoc cref="AsPackAllocator"/>
        [MethodImpl(AggressiveInlining)]
        public static implicit operator PackAllocator(PackArenaAllocator arena) {
            return arena.AsPackAllocator();
        }

        /// <summary>
        /// Creates a writer whose initial buffer and all further growth come from this arena. The writer belongs to
        /// the arena's thread: in an IJobParallelFor give every worker its own arena.
        /// Disposing the writer releases nothing (the memory is reclaimed by <see cref="Reset"/>), but it does
        /// invalidate the writer itself, so do not use the struct afterwards.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public BinaryPackWriter CreateWriter(uint capacity = 1024) {
            return BinaryPackWriter.CreateUntracked(capacity, AsPackAllocator());
        }

        #if DEBUG || FFS_PACK_ENABLE_DEBUG
        /// <summary>
        /// An arena belongs to the thread that created it. This catches the managed entry points only: growth
        /// requested from inside a Burst-compiled job goes through the unmanaged callbacks, which carry no
        /// thread identity and stay undiagnosed. <c>[BurstDiscard]</c> keeps <c>Environment.CurrentManagedThreadId</c>,
        /// a managed call, out of Burst-compiled allocation paths.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        #if UNITY_5_3_OR_NEWER && FFS_BURST
        [BurstDiscard]
        #endif
        private void AssertOwnerThread() {
            if (_state->OwnerThreadId != Environment.CurrentManagedThreadId) {
                throw new Exception("[StaticPack] PackArenaAllocator is used from a thread other than the one that created it - use one arena per thread");
            }
        }
        #endif

        /// <summary> Raw bump allocation (spills or grows when the arena is full, depending on the mode). </summary>
        [MethodImpl(AggressiveInlining)]
        public byte* Alloc(uint size) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (_state == null)
                throw new Exception("[StaticPack] PackArenaAllocator is not created or already disposed");
            AssertOwnerThread();
            #endif
            return AllocFrom(_state, size);
        }

        /// <summary>
        /// Allocates a single <typeparamref name="T"/> initialized to <c>default</c> and returns a reference to it.
        /// Valid until <see cref="Reset"/>/<see cref="Dispose"/>.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public ref T Alloc<T>() where T : unmanaged {
            var ptr = (T*)Alloc((uint)sizeof(T));
            *ptr = default;
            return ref *ptr;
        }

        /// <summary>
        /// Allocates space for <paramref name="count"/> elements of <typeparamref name="T"/> and returns the raw
        /// pointer. Memory is zeroed unless <paramref name="clear"/> is cleared. Valid until <see cref="Reset"/>/<see cref="Dispose"/>.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public T* AllocPtr<T>(int count = 1, bool clear = true) where T : unmanaged {
            if (count < 0)
                throw new Exception("[StaticPack] PackArenaAllocator: count is negative");
            var sizeLong = (ulong)count * (uint)sizeof(T);
            if (sizeLong > uint.MaxValue)
                throw new Exception("[StaticPack] PackArenaAllocator: allocation size exceeds uint.MaxValue");
            var size = (uint)sizeLong;
            var ptr = (T*)Alloc(size);
            if (clear) {
                PackMemory.Clear((byte*)ptr, size);
            }

            return ptr;
        }

        /// <summary>
        /// Allocates a <see cref="Span{T}"/> of <paramref name="count"/> elements. Memory is zeroed unless
        /// <paramref name="clear"/> is cleared. Valid until <see cref="Reset"/>/<see cref="Dispose"/>.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public Span<T> AllocSpan<T>(int count, bool clear = true) where T : unmanaged {
            return new Span<T>(AllocPtr<T>(count, clear), count);
        }

        /// <summary>
        /// Reclaims everything allocated through the arena since the previous Reset: rewinds the bump
        /// cursor and frees retired blocks. In AutoResize mode the current (largest) chunk is kept, so the
        /// grown capacity survives. All pointers handed out before become invalid - including writers, whose
        /// continued use after a Reset is not detected in any build: a stale writer can still satisfy the
        /// in-place growth check and take a range already handed to a newer one.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public void Reset() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (_state == null)
                throw new Exception("[StaticPack] PackArenaAllocator is not created or already disposed");
            AssertOwnerThread();
            #endif
            FreeRetired(_state);
            _state->Offset = 0;
        }

        /// <summary> Releases the arena itself together with all of its blocks. </summary>
        [MethodImpl(AggressiveInlining)]
        public void Dispose() {
            if (_state == null) {
                return;
            }

            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            // First: detects Dispose of a stale copy (the original was already disposed) BEFORE the
            // freed state memory below would be dereferenced.
            BinaryPackLeakTracker.TrackFree(_allocId);
            AssertOwnerThread();
            #endif
            FreeRetired(_state);
            PackMemory.FreeRaw(_state->Buffer - BLOCK_HEADER); // current chunk
            PackMemory.FreeRaw((byte*)_state);                 // state block
            _state = null;
        }

        /// <summary> Allocates a [header][payload] block and returns the payload pointer. </summary>
        [MethodImpl(AggressiveInlining)]
        private static byte* AllocBlock(uint payloadSize) {
            var block = PackMemory.AllocRaw(BLOCK_HEADER + payloadSize);
            *(byte**)block = null;
            return block + BLOCK_HEADER;
        }

        private static void FreeRetired(State* s) {
            var block = s->RetiredHead;
            while (block != null) {
                var next = *(byte**)block;
                PackMemory.FreeRaw(block);
                block = next;
            }

            s->RetiredHead = null;
        }

        [MethodImpl(AggressiveInlining)]
        private static void Retire(State* s, byte* block) {
            *(byte**)block = s->RetiredHead;
            s->RetiredHead = block;
        }

        private static byte* AllocFrom(State* state, uint size) {
            if (size > uint.MaxValue - BLOCK_HEADER) {
                throw new Exception("[StaticPack] PackArenaAllocator: allocation size is too large");
            }

            var offset = (state->Offset + (ALIGN - 1)) & ~(ALIGN - 1);
            if (offset <= state->Capacity && size <= state->Capacity - offset) {
                state->Offset = offset + size;
                return state->Buffer + offset;
            }

            if (state->AutoResize) {
                var doubled = (ulong)state->Capacity * 2;
                var ceiling = (ulong)uint.MaxValue - BLOCK_HEADER;
                var newCapacity = doubled > ceiling ? Math.Max(state->Capacity, size) : Math.Max((uint)doubled, size);
                if (newCapacity > ceiling) {
                    newCapacity = size;
                }

                Retire(state, state->Buffer - BLOCK_HEADER);
                state->Buffer = AllocBlock(newCapacity);
                state->Capacity = newCapacity;
                state->Offset = size;
                return state->Buffer;
            }

            var spill = AllocBlock(size);
            Retire(state, spill - BLOCK_HEADER);
            return spill;
        }

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        [BurstCompile]
        [MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]
        #elif UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]
        #elif NET6_0_OR_GREATER
        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        #endif
        private static byte* ArenaRealloc(void* stateRaw, byte* oldPtr, uint oldCapacity, uint usedBytes, uint newCapacity) {
            var state = (State*)stateRaw;

            if (oldPtr >= state->Buffer && oldPtr < state->Buffer + state->Capacity && oldPtr + oldCapacity == state->Buffer + state->Offset) {
                var start = (uint)(oldPtr - state->Buffer);
                // Subtraction instead of `start + newCapacity <= Capacity`: the sum can wrap for
                // newCapacity near uint.MaxValue; start < Capacity is guaranteed by the range check above.
                if (newCapacity <= state->Capacity - start) {
                    state->Offset = start + newCapacity;
                    return oldPtr;
                }
            }

            var newPtr = AllocFrom(state, newCapacity);
            if (usedBytes != 0) {
                PackMemory.Copy(newPtr, oldPtr, usedBytes < newCapacity ? usedBytes : newCapacity);
            }

            return newPtr;
        }

        #if UNITY_5_3_OR_NEWER && FFS_BURST
        [BurstCompile]
        [MonoPInvokeCallback(typeof(PackAllocator.FreeDelegate))]
        #elif UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(PackAllocator.FreeDelegate))]
        #elif NET6_0_OR_GREATER
        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        #endif
        private static void ArenaFree(void* state, byte* ptr) {
            // Memory is reclaimed by Reset/Dispose of the arena.
        }
    }
}
