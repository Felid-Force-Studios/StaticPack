using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

#if UNITY_5_3_OR_NEWER
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
#endif

namespace FFS.Libraries.StaticPack {
    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public unsafe struct BinaryPackWriter : IDisposable {
        internal PackAllocator Allocator;
        #if UNITY_5_3_OR_NEWER
        [NativeDisableUnsafePtrRestriction]
        #endif
        public byte* Buffer;
        public uint Position;
        public uint Capacity;
        #if DEBUG || FFS_PACK_ENABLE_DEBUG
        internal uint AllocId;
        #endif

        /// <summary> True while this writer holds a buffer: false for <c>default</c> and after Dispose or AsReaderOwned. </summary>
        public bool IsCreated {
            [MethodImpl(AggressiveInlining)] get => Buffer != null;
        }

        /// <summary> True when this writer holds a buffer of its own: Dispose releases it and Resize can grow it. </summary>
        public bool Owned {
            [MethodImpl(AggressiveInlining)] get => Buffer != null && Allocator.IsCreated;
        }

        /// <summary>
        /// Wraps user-provided memory. The writer never frees it and throws on overflow;
        /// for growable buffers use <c>Create(capacity, allocator)</c>.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackWriter Create(byte* buffer, uint capacity, uint position = 0) {
            return new BinaryPackWriter(buffer, capacity, position, default);
        }

        /// <summary>
        /// The custom allocator owns the buffer end-to-end: the initial buffer is requested via
        /// <c>Realloc(state, null, 0, 0, capacity)</c>, growth goes through <see cref="PackAllocator.Realloc"/>
        /// and release through <see cref="PackAllocator.Free"/> on <see cref="Dispose"/>.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackWriter Create(uint capacity, PackAllocator allocator) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!allocator.IsCreated)
                throw new Exception("[StaticPack] PackAllocator is not created");
            #endif
            return CreateOwned(capacity, allocator, true);
        }

        /// <summary>
        /// Same as <see cref="Create(uint, PackAllocator)"/> for allocators that reclaim their memory as a whole,
        /// so the buffer is deliberately left out of the leak tracker.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        internal static BinaryPackWriter CreateUntracked(uint capacity, PackAllocator allocator) {
            return CreateOwned(capacity, allocator, false);
        }

        /// <summary> Allocates an owned native buffer; must be released via <see cref="Dispose"/>. </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackWriter Create(uint capacity = 1024) {
            return CreateOwned(capacity, PackMemory.Default(), true);
        }

        [MethodImpl(AggressiveInlining)]
        private static BinaryPackWriter CreateOwned(uint capacity, PackAllocator allocator, bool tracked) {
            var writer = new BinaryPackWriter(allocator.Realloc(allocator.State, null, 0, 0, capacity), capacity, 0, allocator);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (tracked) {
                BinaryPackLeakTracker.TrackAlloc(writer.Buffer, ref writer.AllocId);
            }
            #endif
            return writer;
        }

        #if UNITY_5_3_OR_NEWER
        /// <summary>
        /// Allocates an owned native buffer with the given Unity allocator; must be released via <see cref="Dispose"/>.
        /// <para><c>Allocator.Temp</c> and <c>Allocator.TempJob</c> buffers live for a frame or a job and are left out
        /// of <c>BinaryPackLeakTracker</c> on purpose - Unity reclaims them itself and reports its own unfreed-allocation
        /// warnings. Prefer <c>Allocator.Persistent</c> for buffers of several megabytes or for anything outliving a frame.</para>
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackWriter Create(uint capacity, Allocator allocator) {
            return CreateOwned(capacity, PackMemory.Default(allocator), allocator != Unity.Collections.Allocator.Temp && allocator != Unity.Collections.Allocator.TempJob);
        }

        /// <summary> Wraps the memory of a NativeArray (user-memory mode: no growth, no free). </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackWriter Create(NativeArray<byte> buffer, uint position = 0) {
            return Create((byte*)buffer.GetUnsafePtr(), (uint)buffer.Length, position);
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        internal BinaryPackWriter(byte* buffer, uint capacity, uint position, PackAllocator allocator) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (capacity > int.MaxValue)
                throw new Exception("[StaticPack] capacity exceeds int.MaxValue");
            #endif
            Buffer = buffer;
            Capacity = capacity;
            Position = position;
            Allocator = allocator;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            AllocId = 0;
            #endif
        }

        [MethodImpl(AggressiveInlining)]
        public byte[] CopyToBytes(bool gzip = false) {
            if (gzip) {
                return Gzip(0, Position);
            }

            var bytes = new byte[Position];
            new ReadOnlySpan<byte>(Buffer, (int)Position).CopyTo(bytes);
            return bytes;
        }

        [MethodImpl(AggressiveInlining)]
        public int CopyToBytes(ref byte[] result, bool gzip = false) {
            if (gzip) {
                return Gzip(ref result, 0, Position);
            }

            if (result == null || result.Length < Position) {
                result = new byte[Position];
            }

            new ReadOnlySpan<byte>(Buffer, (int)Position).CopyTo(result);
            return (int)Position;
        }

        /// <summary>
        /// The written bytes as a view over the buffer, without copying. Valid while the writer holds that
        /// buffer: <see cref="Dispose"/>, <see cref="AsReaderOwned"/> and any write that grows the buffer
        /// invalidate it.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public ReadOnlySpan<byte> AsSpan() {
            return new ReadOnlySpan<byte>(Buffer, (int)Position);
        }

        /// <inheritdoc cref="AsSpan()"/>
        [MethodImpl(AggressiveInlining)]
        public ReadOnlySpan<byte> AsSpan(uint offset, uint count) {
            if ((ulong)offset + count > Position) {
                throw new Exception("[StaticPack] AsSpan: range is outside the written bytes");
            }

            return new ReadOnlySpan<byte>(Buffer + offset, (int)count);
        }

        /// <summary> Copies the written bytes into <paramref name="destination"/> and returns their count. </summary>
        [MethodImpl(AggressiveInlining)]
        public int CopyTo(Span<byte> destination) {
            if (destination.Length < Position) {
                throw new Exception("[StaticPack] CopyTo: destination is smaller than the written bytes");
            }

            new ReadOnlySpan<byte>(Buffer, (int)Position).CopyTo(destination);
            return (int)Position;
        }

        /// <summary>
        /// Wraps the written data as a non-owning reader; the writer keeps ownership and must still be disposed.
        /// The reader is valid only while the writer's buffer lives (and may dangle after a later Resize).
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public BinaryPackReader AsReader() {
            return new BinaryPackReader(Buffer, Position, 0);
        }

        /// <summary>
        /// Wraps the written data as a reader and transfers buffer ownership (the allocator travels with it):
        /// the reader releases the buffer in its Dispose, the writer is invalidated (its pointer is nulled) —
        /// dispose the reader instead of the writer afterwards.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public BinaryPackReader AsReaderOwned() {
            var reader = new BinaryPackReader(Buffer, Position, 0, Allocator);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            reader.AllocId = AllocId;
            AllocId = 0;
            #endif
            Buffer = null;
            Capacity = 0;
            Position = 0;
            Allocator = default;
            return reader;
        }

        [MethodImpl(AggressiveInlining)]
        public void EnsureSize(uint size) {
            var required = (ulong)Position + size;
            if (required > Capacity) {
                if (required > int.MaxValue)
                    throw new Exception("[StaticPack] Required buffer size exceeds int.MaxValue");
                Resize((uint)required);
            }
        }

        [MethodImpl(AggressiveInlining)]
        public uint MakePoint(uint size) {
            var position = Position;
            EnsureSize(size);
            Position += size;
            return position;
        }

        private void Resize(uint size) {
            size--;
            size |= size >> 1;
            size |= size >> 2;
            size |= size >> 4;
            size |= size >> 8;
            size |= size >> 16;
            size++;
            if (size == 0 || size > int.MaxValue) {
                size = int.MaxValue; // power-of-two round-up left the addressable range: clamp to the cap
            }

            if (!Allocator.IsCreated) {
                if (Buffer == null) {
                    throw new Exception("[StaticPack] Writer is not created: it is default, already disposed, or its buffer was transferred away by AsReaderOwned");
                }

                throw new Exception("[StaticPack] Buffer overflow: externally provided memory cannot grow (no PackAllocator set)");
            }

            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (size < Capacity)
                throw new Exception("[StaticPack] Resize: new capacity is smaller than the current one");
            var tracked = AllocId != 0;
            #endif
            var newBuffer = Allocator.Realloc(Allocator.State, Buffer, Capacity, Position, size);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            BinaryPackLeakTracker.TrackFree(AllocId);
            #endif
            Buffer = newBuffer;
            Capacity = size;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (tracked) {
                // No AllocId = 0 here: inside Burst TrackAlloc is discarded and the id must stay the one
                // the managed Dispose will hand to TrackFree.
                BinaryPackLeakTracker.TrackAlloc(Buffer, ref AllocId);
            }
            #endif
        }

        [MethodImpl(AggressiveInlining)]
        public void Dispose() {
            if (Buffer != null && Allocator.IsCreated) {
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                BinaryPackLeakTracker.TrackFree(AllocId); // no-op when _allocId == 0 (untracked allocators)
                #endif
                Allocator.Free(Allocator.State, Buffer);
            }

            Buffer = null;
            Capacity = 0;
            Position = 0;
            Allocator = default;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            AllocId = 0;
            #endif
        }

        [MethodImpl(AggressiveInlining)]
        public void Skip(uint bytesCount) {
            EnsureSize(bytesCount);
            Position += bytesCount;
        }

        [MethodImpl(AggressiveInlining)]
        public void Skip() {
            EnsureSize(1);
            Position++;
        }

        [MethodImpl(AggressiveInlining)]
        private void ValidatePosition(uint position, uint size) {
            if (position > Position)
                throw new Exception("[StaticPack] Position is more than current offset");
            if ((ulong)position + size > Position)
                throw new Exception("[StaticPack] Position + size is more than written data");
        }

        /// <summary> Writes 1 for a non-null <paramref name="value"/> and returns true, otherwise writes 0 and returns false. </summary>
        [MethodImpl(AggressiveInlining)]
        public bool WriteNotNullFlag<T>(T value) where T : class {
            EnsureSize(sizeof(byte));
            if (value == null) {
                Buffer[Position++] = 0;
                return false;
            }

            Buffer[Position++] = 1;
            return true;
        }

        [Obsolete("Use WriteNotNullFlag<T>(T) for reference types or WriteNotNullFlag() for a value that is never null: this overload boxes value types.")]
        [MethodImpl(AggressiveInlining)]
        public bool WriteNotNullFlag(object value) {
            EnsureSize(sizeof(byte));
            if (value == null) {
                Buffer[Position++] = 0;
                return false;
            }

            Buffer[Position++] = 1;
            return true;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteNotNullFlag() {
            EnsureSize(sizeof(byte));
            Buffer[Position++] = 1;
        }

        #region PRIMITIVES
        [MethodImpl(AggressiveInlining)]
        public void WriteByte(byte value) {
            EnsureSize(sizeof(byte));
            Buffer[Position] = value;
            Position += 1;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteByteAt(uint offset, byte value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(byte));
            #endif
            Buffer[offset] = value;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteSbyte(sbyte value) {
            EnsureSize(sizeof(sbyte));
            Buffer[Position] = (byte)value;
            Position += 1;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteSbyteAt(uint offset, sbyte value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(sbyte));
            #endif
            Buffer[offset] = (byte)value;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteBool(bool value) {
            EnsureSize(sizeof(byte));
            Buffer[Position++] = (byte)(value ? 1 : 0);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteBoolAt(uint offset, bool value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(byte));
            #endif
            Buffer[offset] = (byte)(value ? 1 : 0);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteShort(short value) {
            EnsureSize(sizeof(short));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 2;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteShortAt(uint offset, short value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(short));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUshort(ushort value) {
            EnsureSize(sizeof(ushort));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 2;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUshortAt(uint offset, ushort value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(ushort));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteChar(char value) {
            EnsureSize(sizeof(char));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 2;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteCharAt(uint offset, char value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(char));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteInt(int value) {
            EnsureSize(sizeof(int));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUint(uint value) {
            EnsureSize(sizeof(uint));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUintAt(uint offset, uint value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(uint));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteIntAt(uint offset, int value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(int));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        /// <summary>
        /// Writes 1-5 bytes, 7 payload bits per byte. Negative values always take the full 5 bytes
        /// (the branch is chosen by the unsigned magnitude, so a negative value never takes the
        /// shorter branches meant for small non-negative ones) and round-trip through <see cref="BinaryPackReader.ReadVarInt"/> unchanged.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteVarInt(int value) {
            var unsigned = (uint)value;
            if ((ulong)Position + 5 > Capacity) {
                EnsureSize(unsigned < 0x80 ? 1u : unsigned < 0x4000 ? 2u : unsigned < 0x200000 ? 3u : unsigned < 0x10000000 ? 4u : 5u);
            }

            if (unsigned < 0x80) {
                Buffer[Position] = (byte)value;
                Position += 1;
            } else if (unsigned < 0x4000) {
                Buffer[Position] = (byte)(value | 0x80);
                Buffer[Position + 1] = (byte)(value >> 7);
                Position += 2;
            } else if (unsigned < 0x200000) {
                Buffer[Position] = (byte)(value | 0x80);
                Buffer[Position + 1] = (byte)((value >> 7) | 0x80);
                Buffer[Position + 2] = (byte)(value >> 14);
                Position += 3;
            } else if (unsigned < 0x10000000) {
                Buffer[Position] = (byte)(value | 0x80);
                Buffer[Position + 1] = (byte)((value >> 7) | 0x80);
                Buffer[Position + 2] = (byte)((value >> 14) | 0x80);
                Buffer[Position + 3] = (byte)(value >> 21);
                Position += 4;
            } else {
                Buffer[Position] = (byte)(value | 0x80);
                Buffer[Position + 1] = (byte)((value >> 7) | 0x80);
                Buffer[Position + 2] = (byte)((value >> 14) | 0x80);
                Buffer[Position + 3] = (byte)((value >> 21) | 0x80);
                Buffer[Position + 4] = (byte)(value >> 28);
                Position += 5;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteVarShort(short value) {
            if (value < 0)
                throw new Exception("[StaticPack] VarShort value is less than 0");
            if ((ulong)Position + sizeof(short) > Capacity) {
                EnsureSize(value < 0b10000000 ? 1u : 2u);
            }

            if (value < 0b10000000) {
                Buffer[Position] = (byte)value;
                Position += 1;
            } else {
                Buffer[Position] = (byte)(value | 0b10000000);
                Buffer[Position + 1] = (byte)(value >> 7);
                Position += 2;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteLong(long value) {
            EnsureSize(sizeof(long));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 8;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUlong(ulong value) {
            EnsureSize(sizeof(ulong));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 8;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUlongAt(uint offset, ulong value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(ulong));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteLongAt(uint offset, long value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(long));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteFloat(float value) {
            EnsureSize(sizeof(float));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteFloatAt(uint offset, float value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(float));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDouble(double value) {
            EnsureSize(sizeof(double));
            Unsafe.WriteUnaligned(ref Buffer[Position], value);
            Position += 8;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDoubleAt(uint offset, double value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(double));
            #endif
            Unsafe.WriteUnaligned(ref Buffer[offset], value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteByte(byte v0, byte v1) {
            EnsureSize(2);
            var pos = Position;
            Position += 2;
            WriteByteAt(pos, v0);
            WriteByteAt(pos + 1, v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteByte(byte v0, byte v1, byte v2) {
            EnsureSize(3);
            var pos = Position;
            Position += 3;
            WriteByteAt(pos, v0);
            WriteByteAt(pos + 1, v1);
            WriteByteAt(pos + 2, v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteByte(byte v0, byte v1, byte v2, byte v3) {
            EnsureSize(4);
            var pos = Position;
            Position += 4;
            WriteByteAt(pos, v0);
            WriteByteAt(pos + 1, v1);
            WriteByteAt(pos + 2, v2);
            WriteByteAt(pos + 3, v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteShort(short v0, short v1) {
            EnsureSize(sizeof(short) * 2);
            var pos = Position;
            Position += sizeof(short) * 2;
            WriteShortAt(pos, v0);
            WriteShortAt(pos + 2, v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteShort(short v0, short v1, short v2) {
            EnsureSize(sizeof(short) * 3);
            var pos = Position;
            Position += sizeof(short) * 3;
            WriteShortAt(pos, v0);
            WriteShortAt(pos + 2, v1);
            WriteShortAt(pos + 4, v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteShort(short v0, short v1, short v2, short v3) {
            EnsureSize(sizeof(short) * 4);
            var pos = Position;
            Position += sizeof(short) * 4;
            WriteShortAt(pos, v0);
            WriteShortAt(pos + 2, v1);
            WriteShortAt(pos + 4, v2);
            WriteShortAt(pos + 6, v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUshort(ushort v0, ushort v1) {
            EnsureSize(sizeof(ushort) * 2);
            var pos = Position;
            Position += sizeof(ushort) * 2;
            WriteUshortAt(pos, v0);
            WriteUshortAt(pos + 2, v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUshort(ushort v0, ushort v1, ushort v2) {
            EnsureSize(sizeof(ushort) * 3);
            var pos = Position;
            Position += sizeof(ushort) * 3;
            WriteUshortAt(pos, v0);
            WriteUshortAt(pos + 2, v1);
            WriteUshortAt(pos + 4, v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUshort(ushort v0, ushort v1, ushort v2, ushort v3) {
            EnsureSize(sizeof(ushort) * 4);
            var pos = Position;
            Position += sizeof(ushort) * 4;
            WriteUshortAt(pos, v0);
            WriteUshortAt(pos + 2, v1);
            WriteUshortAt(pos + 4, v2);
            WriteUshortAt(pos + 6, v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteInt(int v0, int v1) {
            EnsureSize(sizeof(int) * 2);
            var pos = Position;
            Position += sizeof(int) * 2;
            WriteUintAt(pos, (uint)v0);
            WriteUintAt(pos + 4, (uint)v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteInt(int v0, int v1, int v2) {
            EnsureSize(sizeof(int) * 3);
            var pos = Position;
            Position += sizeof(int) * 3;
            WriteUintAt(pos, (uint)v0);
            WriteUintAt(pos + 4, (uint)v1);
            WriteUintAt(pos + 8, (uint)v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteInt(int v0, int v1, int v2, int v3) {
            EnsureSize(sizeof(int) * 4);
            var pos = Position;
            Position += sizeof(int) * 4;
            WriteUintAt(pos, (uint)v0);
            WriteUintAt(pos + 4, (uint)v1);
            WriteUintAt(pos + 8, (uint)v2);
            WriteUintAt(pos + 12, (uint)v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUint(uint v0, uint v1) {
            EnsureSize(sizeof(uint) * 2);
            var pos = Position;
            Position += sizeof(uint) * 2;
            WriteUintAt(pos, v0);
            WriteUintAt(pos + 4, v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUint(uint v0, uint v1, uint v2) {
            EnsureSize(sizeof(uint) * 3);
            var pos = Position;
            Position += sizeof(uint) * 3;
            WriteUintAt(pos, v0);
            WriteUintAt(pos + 4, v1);
            WriteUintAt(pos + 8, v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUint(uint v0, uint v1, uint v2, uint v3) {
            EnsureSize(sizeof(uint) * 4);
            var pos = Position;
            Position += sizeof(uint) * 4;
            WriteUintAt(pos, v0);
            WriteUintAt(pos + 4, v1);
            WriteUintAt(pos + 8, v2);
            WriteUintAt(pos + 12, v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteFloat(float v0, float v1) {
            EnsureSize(sizeof(float) * 2);
            var pos = Position;
            Position += sizeof(float) * 2;
            WriteFloatAt(pos, v0);
            WriteFloatAt(pos + 4, v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteFloat(float v0, float v1, float v2) {
            EnsureSize(sizeof(float) * 3);
            var pos = Position;
            Position += sizeof(float) * 3;
            WriteFloatAt(pos, v0);
            WriteFloatAt(pos + 4, v1);
            WriteFloatAt(pos + 8, v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteFloat(float v0, float v1, float v2, float v3) {
            EnsureSize(sizeof(float) * 4);
            var pos = Position;
            Position += sizeof(float) * 4;
            WriteFloatAt(pos, v0);
            WriteFloatAt(pos + 4, v1);
            WriteFloatAt(pos + 8, v2);
            WriteFloatAt(pos + 12, v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteLong(long v0, long v1) {
            EnsureSize(sizeof(long) * 2);
            var pos = Position;
            Position += sizeof(long) * 2;
            WriteUlongAt(pos, (ulong)v0);
            WriteUlongAt(pos + 8, (ulong)v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteLong(long v0, long v1, long v2) {
            EnsureSize(sizeof(long) * 3);
            var pos = Position;
            Position += sizeof(long) * 3;
            WriteUlongAt(pos, (ulong)v0);
            WriteUlongAt(pos + 8, (ulong)v1);
            WriteUlongAt(pos + 16, (ulong)v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteLong(long v0, long v1, long v2, long v3) {
            EnsureSize(sizeof(long) * 4);
            var pos = Position;
            Position += sizeof(long) * 4;
            WriteUlongAt(pos, (ulong)v0);
            WriteUlongAt(pos + 8, (ulong)v1);
            WriteUlongAt(pos + 16, (ulong)v2);
            WriteUlongAt(pos + 24, (ulong)v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUlong(ulong v0, ulong v1) {
            EnsureSize(sizeof(ulong) * 2);
            var pos = Position;
            Position += sizeof(ulong) * 2;
            WriteUlongAt(pos, v0);
            WriteUlongAt(pos + 8, v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUlong(ulong v0, ulong v1, ulong v2) {
            EnsureSize(sizeof(ulong) * 3);
            var pos = Position;
            Position += sizeof(ulong) * 3;
            WriteUlongAt(pos, v0);
            WriteUlongAt(pos + 8, v1);
            WriteUlongAt(pos + 16, v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUlong(ulong v0, ulong v1, ulong v2, ulong v3) {
            EnsureSize(sizeof(ulong) * 4);
            var pos = Position;
            Position += sizeof(ulong) * 4;
            WriteUlongAt(pos, v0);
            WriteUlongAt(pos + 8, v1);
            WriteUlongAt(pos + 16, v2);
            WriteUlongAt(pos + 24, v3);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDouble(double v0, double v1) {
            EnsureSize(sizeof(double) * 2);
            var pos = Position;
            Position += sizeof(double) * 2;
            WriteDoubleAt(pos, v0);
            WriteDoubleAt(pos + 8, v1);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDouble(double v0, double v1, double v2) {
            EnsureSize(sizeof(double) * 3);
            var pos = Position;
            Position += sizeof(double) * 3;
            WriteDoubleAt(pos, v0);
            WriteDoubleAt(pos + 8, v1);
            WriteDoubleAt(pos + 16, v2);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDouble(double v0, double v1, double v2, double v3) {
            EnsureSize(sizeof(double) * 4);
            var pos = Position;
            Position += sizeof(double) * 4;
            WriteDoubleAt(pos, v0);
            WriteDoubleAt(pos + 8, v1);
            WriteDoubleAt(pos + 16, v2);
            WriteDoubleAt(pos + 24, v3);
        }
        #endregion

        #region BASE_VALUE_TYPES
        [MethodImpl(AggressiveInlining)]
        public void WriteNullable<T>(in T? value) where T : struct {
            if (value.HasValue) {
                EnsureSize(sizeof(byte));
                Buffer[Position++] = 1;
                BinaryPack<T>.Write(ref this, value.Value);
            } else {
                EnsureSize(sizeof(byte));
                Buffer[Position++] = 0;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDateTime(DateTime value) {
            WriteLong(value.ToBinary());
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDateTimeAt(uint offset, DateTime value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, sizeof(ulong));
            #endif
            WriteUlongAt(offset, (ulong)value.ToBinary());
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteGuid(in Guid value) {
            EnsureSize(16);
            var position = Position;
            Position += 16;
            WriteGuidAt(position, value);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteGuidAt(uint offset, Guid value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            ValidatePosition(offset, 16);
            #endif
            Unsafe.WriteUnaligned(Buffer + offset, value);
        }
        #endregion

        #region STRING
        /// <summary>
        /// Reserves the worst-case UTF-8 size, falling back to the exact size when that does not fit a buffer
        /// which cannot grow.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        private int EnsureStringSize(string value, uint prefixSize) {
            var maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
            if (!Allocator.IsCreated && (ulong)Position + (uint)maxByteCount + prefixSize > Capacity) {
                maxByteCount = Encoding.UTF8.GetByteCount(value);
            }

            EnsureSize((uint)maxByteCount + prefixSize);
            return maxByteCount;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteString32(string value) {
            if (WriteNotNullFlag(value)) {
                var maxByteCount = EnsureStringSize(value, sizeof(int));
                var bytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(), new Span<byte>(Buffer + Position + sizeof(int), maxByteCount));
                WriteInt(bytesWritten);
                Position += (uint)bytesWritten;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteString16(string value) {
            var startPosition = Position;
            if (WriteNotNullFlag(value)) {
                if (value.Length > ushort.MaxValue) {
                    Position = startPosition;
                    throw new Exception($"String length {value.Length} chars already exceeds String16 limit {ushort.MaxValue} bytes");
                }

                var maxByteCount = EnsureStringSize(value, sizeof(ushort));
                var bytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(), new Span<byte>(Buffer + Position + sizeof(ushort), maxByteCount));
                if (bytesWritten > ushort.MaxValue) {
                    Position = startPosition;
                    throw new Exception($"String UTF-8 byte length {bytesWritten} exceeds String16 limit {ushort.MaxValue}");
                }

                WriteUshort((ushort)bytesWritten);
                Position += (uint)bytesWritten;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteString8(string value) {
            var startPosition = Position;
            if (WriteNotNullFlag(value)) {
                if (value.Length > byte.MaxValue) {
                    Position = startPosition;
                    throw new Exception($"String length {value.Length} chars already exceeds String8 limit {byte.MaxValue} bytes");
                }

                var maxByteCount = EnsureStringSize(value, sizeof(byte));
                var bytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(), new Span<byte>(Buffer + Position + sizeof(byte), maxByteCount));
                if (bytesWritten > byte.MaxValue) {
                    Position = startPosition;
                    throw new Exception($"String UTF-8 byte length {bytesWritten} exceeds String8 limit {byte.MaxValue}");
                }

                WriteByte((byte)bytesWritten);
                Position += (uint)bytesWritten;
            }
        }
        #endregion

        #region COLLECTIONS
        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        /// <summary> Same as the <c>T[,]</c> overload of <c>WriteArray</c>, named to mirror <c>ReadArray2D</c>. </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteArray2D<T>(T[,] value) {
            WriteArray(value);
        }

        /// <summary> Same as the <c>T[,,]</c> overload of <c>WriteArray</c>, named to mirror <c>ReadArray3D</c>. </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteArray3D<T>(T[,,] value) {
            WriteArray(value);
        }

        /// <summary> Same as the <c>T[,]</c> overload of <c>WriteArrayUnmanaged</c>, named to mirror <c>ReadArray2D</c>. </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteArrayUnmanaged2D<T>(T[,] value) where T : unmanaged {
            WriteArrayUnmanaged(value);
        }

        /// <summary> Same as the <c>T[,,]</c> overload of <c>WriteArrayUnmanaged</c>, named to mirror <c>ReadArray3D</c>. </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteArrayUnmanaged3D<T>(T[,,] value) where T : unmanaged {
            WriteArrayUnmanaged(value);
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void WriteArrayUnmanaged<T>(T[] value) where T : unmanaged {
            if (value == null) {
                WriteNotNullFlag(value);
                return;
            }

            WriteArrayUnmanaged(value, 0, value.Length);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteArrayUnmanaged<T>(T[] value, int idx, int count) where T : unmanaged {
            if (WriteNotNullFlag(value)) {
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                if ((uint)idx > (uint)value.Length || (uint)count > (uint)(value.Length - idx)) {
                    throw new Exception($"[WriteArrayUnmanaged<{typeof(T)}>] idx {idx} and count {count} are out of range for an array of length {value.Length}");
                }
                #endif

                WriteInt(count);
                var position = MakePoint(sizeof(uint));
                if (count > 0) {
                    var byteCount = (ulong)count * (uint)sizeof(T);
                    if (byteCount > int.MaxValue) {
                        throw new Exception("[StaticPack] payload byte size exceeds int.MaxValue");
                    }

                    var size = (uint)byteCount;
                    EnsureSize(size);
                    fixed (void* dataPtr = &value[idx]) {
                        PackMemory.Copy(Buffer + Position, (byte*)dataPtr, size);
                    }

                    Position += size;
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public void WriteArrayUnmanaged<T>(T[,] value) where T : unmanaged {
            if (WriteNotNullFlag(value)) {
                var dim0 = value.GetLength(0);
                var dim1 = value.GetLength(1);

                WriteInt(dim0);
                WriteInt(dim1);
                var position = MakePoint(sizeof(uint));

                if (dim0 != 0 && dim1 != 0) {
                    var totalLength = dim0 * dim1;
                    var byteCount = (ulong)totalLength * (uint)sizeof(T);
                    if (byteCount > int.MaxValue) {
                        throw new Exception("[StaticPack] payload byte size exceeds int.MaxValue");
                    }

                    var size = (uint)byteCount;
                    EnsureSize(size);

                    fixed (T* dataPtr = &value[0, 0]) {
                        PackMemory.Copy(Buffer + Position, (byte*)dataPtr, size);
                    }

                    Position += size;
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteArrayUnmanaged<T>(T[,,] value) where T : unmanaged {
            if (WriteNotNullFlag(value)) {
                var dim0 = value.GetLength(0);
                var dim1 = value.GetLength(1);
                var dim2 = value.GetLength(2);

                WriteInt(dim0);
                WriteInt(dim1);
                WriteInt(dim2);
                var position = MakePoint(sizeof(uint));

                if (dim0 != 0 && dim1 != 0 && dim2 != 0) {
                    var totalLength = dim0 * dim1 * dim2;
                    var byteCount = (ulong)totalLength * (uint)sizeof(T);
                    if (byteCount > int.MaxValue) {
                        throw new Exception("[StaticPack] payload byte size exceeds int.MaxValue");
                    }

                    var size = (uint)byteCount;
                    EnsureSize(size);

                    fixed (T* dataPtr = &value[0, 0, 0]) {
                        PackMemory.Copy(Buffer + Position, (byte*)dataPtr, size);
                    }

                    Position += size;
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void WriteArray<T>(T[] value) {
            if (value == null) {
                WriteNotNullFlag(value);
                return;
            }

            WriteArray(value, 0, value.Length);
        }

        public delegate void WriteCollectionDelegate(ref BinaryPackWriter writer, int idx);

        /// <summary> Writes the same layout as <c>WriteArray&lt;T&gt;</c>; read it back with <c>ReadArray&lt;T&gt;</c>. </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteCollection(int idx, int count, WriteCollectionDelegate @delegate) {
            WriteNotNullFlag();
            WriteInt(count);
            var position = MakePoint(sizeof(uint));

            for (var i = idx; i < idx + count; i++) {
                @delegate(ref this, i);
            }

            WriteUintAt(position, Position - (position + sizeof(uint)));
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteArray<T>(T[] value, int idx, int count) {
            if (WriteNotNullFlag(value)) {
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                if ((uint)idx > (uint)value.Length || (uint)count > (uint)(value.Length - idx)) {
                    throw new Exception($"[WriteArray<{typeof(T)}>] idx {idx} and count {count} are out of range for an array of length {value.Length}");
                }
                #endif

                WriteInt(count);
                var position = MakePoint(sizeof(uint));

                for (var i = idx; i < idx + count; i++) {
                    BinaryPack<T>.Write(ref this, in value[i]);
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public void WriteArray<T>(T[,] value) {
            if (WriteNotNullFlag(value)) {
                var dim0 = value.GetLength(0);
                var dim1 = value.GetLength(1);

                WriteInt(dim0);
                WriteInt(dim1);
                var position = MakePoint(sizeof(uint));

                for (var i0 = 0; i0 < dim0; i0++) {
                    for (var i1 = 0; i1 < dim1; i1++) {
                        BinaryPack<T>.Write(ref this, in value[i0, i1]);
                    }
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteArray<T>(T[,,] value) {
            if (WriteNotNullFlag(value)) {
                var dim0 = value.GetLength(0);
                var dim1 = value.GetLength(1);
                var dim2 = value.GetLength(2);

                WriteInt(dim0);
                WriteInt(dim1);
                WriteInt(dim2);
                var position = MakePoint(sizeof(uint));

                for (var i0 = 0; i0 < dim0; i0++) {
                    for (var i1 = 0; i1 < dim1; i1++) {
                        for (var i2 = 0; i2 < dim2; i2++) {
                            BinaryPack<T>.Write(ref this, in value[i0, i1, i2]);
                        }
                    }
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void WriteList<T>(List<T> value, int count = -1) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (value != null && count > value.Count)
                throw new Exception($"[StaticPack] WriteList: count {count} exceeds list size {value.Count}");
            #endif
            if (WriteNotNullFlag(value)) {
                var len = count >= 0 ? count : value.Count;
                WriteInt(len);
                var position = MakePoint(sizeof(uint));
                for (var i = 0; i < len; i++) {
                    BinaryPack<T>.Write(ref this, value[i]);
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteQueue<T>(Queue<T> value) {
            if (WriteNotNullFlag(value)) {
                WriteInt(value.Count);
                var position = MakePoint(sizeof(uint));
                foreach (var val in value) {
                    BinaryPack<T>.Write(ref this, in val);
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteStack<T>(Stack<T> value) {
            if (WriteNotNullFlag(value)) {
                var count = value.Count;
                WriteInt(count);
                var position = MakePoint(sizeof(uint));
                if (count > 0) {
                    var buffer = ArrayPool<T>.Shared.Rent(count);
                    try {
                        value.CopyTo(buffer, 0);
                        for (var i = count - 1; i >= 0; i--) {
                            BinaryPack<T>.Write(ref this, in buffer[i]);
                        }
                    }
                    finally {
                        ArrayPool<T>.Shared.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
                    }
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteLinkedList<T>(LinkedList<T> value) {
            if (WriteNotNullFlag(value)) {
                WriteInt(value.Count);
                var position = MakePoint(sizeof(uint));
                foreach (var val in value) {
                    BinaryPack<T>.Write(ref this, in val);
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteHashSet<T>(HashSet<T> value) {
            if (WriteNotNullFlag(value)) {
                WriteInt(value.Count);
                var position = MakePoint(sizeof(uint));

                foreach (var val in value) {
                    BinaryPack<T>.Write(ref this, in val);
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteDictionary<K, V>(Dictionary<K, V> value) {
            if (WriteNotNullFlag(value)) {
                WriteInt(value.Count);
                var position = MakePoint(sizeof(uint));
                foreach (var (key, val) in value) {
                    BinaryPack<K>.Write(ref this, in key);
                    BinaryPack<V>.Write(ref this, in val);
                }

                WriteUintAt(position, Position - (position + sizeof(uint)));
            }
        }
        #endregion

        #region SPAN
        [MethodImpl(AggressiveInlining)]
        public void WriteBytes(ReadOnlySpan<byte> value) {
            var count = (uint)value.Length;
            EnsureSize(count);
            value.CopyTo(new Span<byte>(Buffer + Position, (int)count));
            Position += count;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteBytes(ReadOnlyMemory<byte> value) {
            WriteBytes(value.Span);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteBytes(in ReadOnlySequence<byte> value) {
            var length = (uint)value.Length;
            EnsureSize(length);
            value.CopyTo(new Span<byte>(Buffer + Position, (int)length));
            Position += length;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged {
            if (value.Length == 0)
                return;
            var byteCount = (ulong)value.Length * (uint)sizeof(T);
            if (byteCount > int.MaxValue) {
                throw new Exception("[StaticPack] payload byte size exceeds int.MaxValue");
            }

            var size = (uint)byteCount;
            EnsureSize(size);
            fixed (T* src = value) {
                PackMemory.Copy(Buffer + Position, (byte*)src, size);
            }

            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T>(ReadOnlyMemory<T> value) where T : unmanaged {
            WriteUnmanaged(value.Span);
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteSpanUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged {
            WriteNotNullFlag();
            WriteInt(value.Length);
            var position = MakePoint(sizeof(uint));
            if (value.Length > 0) {
                var byteCount = (ulong)value.Length * (uint)sizeof(T);
                if (byteCount > int.MaxValue) {
                    throw new Exception("[StaticPack] payload byte size exceeds int.MaxValue");
                }

                var size = (uint)byteCount;
                EnsureSize(size);
                fixed (T* src = value) {
                    PackMemory.Copy(Buffer + Position, (byte*)src, size);
                }

                Position += size;
            }

            WriteUintAt(position, Position - (position + sizeof(uint)));
        }

        /// <summary> Writes the same layout as <c>WriteArray&lt;T&gt;</c>; read it back with <c>ReadArray&lt;T&gt;</c>. </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteSpan<T>(ReadOnlySpan<T> value) {
            WriteNotNullFlag();
            WriteInt(value.Length);
            var position = MakePoint(sizeof(uint));
            for (var i = 0; i < value.Length; i++) {
                BinaryPack<T>.Write(ref this, value[i]);
            }

            WriteUintAt(position, Position - (position + sizeof(uint)));
        }
        #endregion

        #region UNMANAGED_GENERIC
        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1>(in T1 v1)
            where T1 : unmanaged {
            var size = (uint)Unsafe.SizeOf<T1>();
            EnsureSize(size);
            Unsafe.WriteUnaligned(ref Buffer[Position], v1);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1, T2>(in T1 v1, in T2 v2)
            where T1 : unmanaged where T2 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1, T2, T3>(in T1 v1, in T2 v2, in T3 v3)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1, T2, T3, T4>(in T1 v1, in T2 v2, in T3 v3, in T4 v4)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1, T2, T3, T4, T5>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1, T2, T3, T4, T5, T6>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1, T2, T3, T4, T5, T6, T7>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanaged<T1, T2, T3, T4, T5, T6, T7, T8>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7, in T8 v8)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                              Unsafe.SizeOf<T8>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()), v8);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1>(in T1 v1)
            where T1 : unmanaged {
            var payload = (uint)Unsafe.SizeOf<T1>();
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1, T2>(in T1 v1, in T2 v2)
            where T1 : unmanaged where T2 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1, T2, T3>(in T1 v1, in T2 v2, in T3 v3)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1, T2, T3, T4>(in T1 v1, in T2 v2, in T3 v3, in T4 v4)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1, T2, T3, T4, T5>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1, T2, T3, T4, T5, T6>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1, T2, T3, T4, T5, T6, T7>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteUnmanagedSized<T1, T2, T3, T4, T5, T6, T7, T8>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7, in T8 v8)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                                 Unsafe.SizeOf<T8>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()),
                v8);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1>(in T1 v1) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            #endif
            var size = (uint)Unsafe.SizeOf<T1>();
            EnsureSize(size);
            Unsafe.WriteUnaligned(ref Buffer[Position], v1);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1, T2>(in T1 v1, in T2 v2) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T2)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1, T2, T3>(in T1 v1, in T2 v2, in T3 v3) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T3)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1, T2, T3, T4>(in T1 v1, in T2 v2, in T3 v3, in T4 v4) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T4)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1, T2, T3, T4, T5>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T5)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1, T2, T3, T4, T5, T6>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T6)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1, T2, T3, T4, T5, T6, T7>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T7)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanaged<T1, T2, T3, T4, T5, T6, T7, T8>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7, in T8 v8) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T7)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T8>())
                throw new Exception($"[ForceWriteUnmanaged] Type {typeof(T8)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                              Unsafe.SizeOf<T8>());
            EnsureSize(size);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref dst, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()), v8);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1>(in T1 v1) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            #endif
            var payload = (uint)Unsafe.SizeOf<T1>();
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1, T2>(in T1 v1, in T2 v2) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T2)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1, T2, T3>(in T1 v1, in T2 v2, in T3 v3) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T3)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1, T2, T3, T4>(in T1 v1, in T2 v2, in T3 v3, in T4 v4) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T4)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1, T2, T3, T4, T5>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T5)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1, T2, T3, T4, T5, T6>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T6)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1, T2, T3, T4, T5, T6, T7>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T7)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Position += payload + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceWriteUnmanagedSized<T1, T2, T3, T4, T5, T6, T7, T8>(in T1 v1, in T2 v2, in T3 v3, in T4 v4, in T5 v5, in T6 v6, in T7 v7, in T8 v8) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T7)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T8>())
                throw new Exception($"[ForceWriteUnmanagedSized] Type {typeof(T8)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                                 Unsafe.SizeOf<T8>());
            EnsureSize(payload + 4);
            ref var dst = ref Buffer[Position];
            Unsafe.WriteUnaligned(ref dst, payload);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), v1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>()), v2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()), v3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()), v4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()), v5);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()), v6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()), v7);
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref dst, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()),
                v8);
            Position += payload + 4;
        }
        #endregion

        #region OTHER
        [MethodImpl(AggressiveInlining)]
        public void WriteIntPtr(IntPtr value, uint len) {
            EnsureSize(len);
            PackMemory.Copy(Buffer + Position, (byte*)value.ToPointer(), len);
            Position += len;
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteArraySegment(ArraySegment<byte> value) {
            WriteBytes(new ReadOnlySpan<byte>(value.Array!, value.Offset, value.Count));
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteBytes(byte[] value, uint index, uint count) {
            WriteBytes(new ReadOnlySpan<byte>(value, (int)index, (int)count));
        }

        [MethodImpl(AggressiveInlining)]
        public void WriteBytes(byte[] value) {
            WriteBytes(new ReadOnlySpan<byte>(value));
        }
        #endregion

        /// <summary>
        /// Appends the contents of a file to the buffer, decompressing it when <paramref name="gzip"/> is set.
        /// <paramref name="maxDecompressedSize"/> caps how much the gzip branch is allowed to append.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteFromFile(string filePath, bool gzip = false, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue) {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: (int)bufferSize);
            if (gzip) {
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress, false);
                AppendDecompressed(gzipStream, bufferSize, maxDecompressedSize);
            } else {
                var streamLength = fileStream.Length;
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                if (streamLength > int.MaxValue) {
                    throw new Exception("Stream length more than " + int.MaxValue);
                }
                #endif
                EnsureSize((uint)streamLength);
                BinaryPackReader.ReadExactly(fileStream, new Span<byte>(Buffer + Position, (int)streamLength));
                Skip((uint)streamLength);
            }
        }

        private static void WriteToStream(Stream destination, byte* source, uint count) {
            #if NET6_0_OR_GREATER
            destination.Write(new ReadOnlySpan<byte>(source, (int)count));
            #else
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, BinaryPackReader.STREAM_COPY_CHUNK_SIZE));
            try {
                while (count > 0) {
                    var chunk = (int)Math.Min(count, (uint)buffer.Length);
                    new ReadOnlySpan<byte>(source, chunk).CopyTo(buffer);
                    destination.Write(buffer, 0, chunk);
                    source += chunk;
                    count -= (uint)chunk;
                }
            }
            finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            #endif
        }

        [MethodImpl(AggressiveInlining)]
        public void FlushToFile(string filePath, bool gzip = false, bool flushToDisk = false) {
            FlushToFile(filePath, 0, Position, gzip, flushToDisk: flushToDisk);
        }

        [MethodImpl(AggressiveInlining)]
        public void FlushToFile(string filePath, uint offset, uint count, bool gzip = false, uint bufferSize = 4096, bool flushToDisk = false, CompressionLevel level = CompressionLevel.Fastest) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if ((ulong)offset + count > Position) {
                throw new Exception("offset + count exceeds buffer length");
            }
            #endif

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }

            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: (int)Math.Max(1, Math.Min(bufferSize, count)));
            if (gzip) {
                using (var gzipStream = new GZipStream(fileStream, level, leaveOpen: true)) {
                    WriteToStream(gzipStream, Buffer + offset, count);
                }

                fileStream.Flush(flushToDisk);
            } else {
                WriteToStream(fileStream, Buffer + offset, count);
                fileStream.Flush(flushToDisk);
            }
        }

        /// <summary> Compresses <paramref name="count"/> bytes of the buffer into <paramref name="result"/>. </summary>
        [MethodImpl(AggressiveInlining)]
        public int Gzip(ref byte[] result, uint offset, uint count, CompressionLevel level = CompressionLevel.Fastest) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if ((ulong)offset + count > Position) {
                throw new Exception("offset + count exceeds buffer length");
            }
            #endif

            using var outputStream = new MemoryStream(Math.Min((int)count / 2 + 64, 64 * 1024));
            using (var gzipStream = new GZipStream(outputStream, level, leaveOpen: true)) {
                WriteToStream(gzipStream, Buffer + offset, count);
            }

            var length = (int)outputStream.Length;
            if (result == null || result.Length < length) {
                result = new byte[length];
            }

            System.Buffer.BlockCopy(outputStream.GetBuffer(), 0, result, 0, length);
            return length;
        }

        [MethodImpl(AggressiveInlining)]
        public byte[] Gzip(uint offset, uint count, CompressionLevel level = CompressionLevel.Fastest) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if ((ulong)offset + count > Position) {
                throw new Exception("offset + count exceeds buffer length");
            }
            #endif

            using var outputStream = new MemoryStream(Math.Min((int)count / 2 + 64, 64 * 1024));
            using (var gzipStream = new GZipStream(outputStream, level, leaveOpen: true)) {
                WriteToStream(gzipStream, Buffer + offset, count);
            }

            var length = (int)outputStream.Length;
            var result = new byte[length];
            System.Buffer.BlockCopy(outputStream.GetBuffer(), 0, result, 0, length);
            return result;
        }

        /// <summary> Decompresses a whole gzip byte array into the buffer. </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteGzipData(byte[] data, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue) {
            WriteGzipData(data, 0, (uint)data.Length, bufferSize, maxDecompressedSize);
        }

        /// <summary>
        /// Decompresses <paramref name="count"/> gzip bytes of <paramref name="data"/> into the buffer.
        /// <paramref name="maxDecompressedSize"/> caps how much is allowed to be appended.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public void WriteGzipData(byte[] data, uint index, uint count, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if ((ulong)index + count > (uint)data.Length) {
                throw new Exception("[StaticPack] incorrect index or count");
            }
            #endif
            using var memoryStream = new MemoryStream(data, (int)index, (int)count);
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress, false);
            AppendDecompressed(gzipStream, bufferSize, maxDecompressedSize);
        }

        private void AppendDecompressed(Stream source, uint bufferSize, uint maxDecompressedSize) {
            var startPosition = Position;
            EnsureSize(bufferSize);
            int bytesRead;
            while ((bytesRead = source.Read(new Span<byte>(Buffer + Position, (int)(Capacity - Position)))) > 0) {
                Skip((uint)bytesRead);
                if (Position - startPosition > maxDecompressedSize) {
                    throw new Exception($"[StaticPack] decompressed size exceeds the {maxDecompressedSize} byte limit");
                }

                EnsureSize(bufferSize);
            }
        }
    }
}
