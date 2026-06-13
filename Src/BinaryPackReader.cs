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
    /// <summary>
    /// Parses a format-specific header and returns the total payload size in bytes
    /// (header + payload). Passed to <see cref="BinaryPackReader.AllocAndFillFromBytes"/> /
    /// <see cref="BinaryPackReader.AllocAndFillFromFile"/> for cases when the source is gzip-compressed
    /// and the exact size can only be determined by decompressing the first bytes.
    /// </summary>
    public delegate uint TotalSizeParser(ReadOnlySpan<byte> header);

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public unsafe struct BinaryPackReader : IDisposable {
        private const uint ARRAY_INFO_BYTES = sizeof(int) + sizeof(int);      // count + byteSize
        private const uint ARRAY2_INFO_BYTES = sizeof(int) * 2 + sizeof(int); // count x2 + byteSize
        private const uint ARRAY3_INFO_BYTES = sizeof(int) * 3 + sizeof(int); // count x3 + byteSize

        internal PackAllocator Allocator;
        #if UNITY_5_3_OR_NEWER
        [NativeDisableUnsafePtrRestriction]
        #endif
        public byte* Buffer;
        public uint Position;
        public readonly uint Size;
        #if DEBUG || FFS_PACK_ENABLE_DEBUG
        internal uint AllocId;
        #endif

        /// <summary> True while this reader holds a buffer: false for <c>default</c> and after Dispose or AsWriterCompact. </summary>
        public bool IsCreated {
            [MethodImpl(AggressiveInlining)] get => Buffer != null;
        }

        /// <summary> True when this reader holds a buffer of its own: Dispose releases it. </summary>
        public bool Owned {
            [MethodImpl(AggressiveInlining)] get => Buffer != null && Allocator.IsCreated;
        }

        /// <summary> Wraps user-provided memory; the reader never frees it. </summary>
        [MethodImpl(AggressiveInlining)]
        public BinaryPackReader(byte* buffer, uint size, uint position) : this(buffer, size, position, default) { }

        #if UNITY_5_3_OR_NEWER
        /// <summary> Wraps the memory of a NativeArray (user-memory mode: no free). </summary>
        [MethodImpl(AggressiveInlining)]
        public BinaryPackReader(NativeArray<byte> buffer, uint size, uint position)
            : this((byte*)buffer.GetUnsafeReadOnlyPtr(), size, position, default) { }
        #endif

        /// <summary> Wraps user-provided memory; the reader never frees it. </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackReader Create(byte* buffer, uint size, uint position = 0) {
            return new BinaryPackReader(buffer, size, position, default);
        }

        #if UNITY_5_3_OR_NEWER
        /// <summary> Wraps the whole memory of a NativeArray (user-memory mode: no free). </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackReader Create(NativeArray<byte> buffer, uint position = 0) {
            return Create((byte*)buffer.GetUnsafeReadOnlyPtr(), (uint)buffer.Length, position);
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        internal BinaryPackReader(byte* buffer, uint size, uint position, PackAllocator allocator) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (buffer == null)
                throw new Exception("[StaticPack] buffer is null");
            if (position > size)
                throw new Exception("[StaticPack] incorrect position or size");
            if (size > int.MaxValue)
                throw new Exception("[StaticPack] size exceeds int.MaxValue");
            #endif
            Buffer = buffer;
            Position = position;
            Size = size;
            Allocator = allocator;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            AllocId = 0;
            #endif
        }

        private const int MAX_PEEK_HEADER_SIZE = 256;

        // RFC 1952: every gzip stream starts with 0x1F 0x8B. Used to autodetect gzip-compressed sources
        // without requiring the caller to pass a flag.
        private const byte GZIP_MAGIC_0 = 0x1F;
        private const byte GZIP_MAGIC_1 = 0x8B;

        [MethodImpl(AggressiveInlining)]
        private static bool IsGzipMagic(byte b0, byte b1) {
            return b0 == GZIP_MAGIC_0 && b1 == GZIP_MAGIC_1;
        }

        /// <summary> True when <paramref name="data"/> starts with the RFC 1952 gzip magic bytes. </summary>
        [MethodImpl(AggressiveInlining)]
        public static bool IsGzip(ReadOnlySpan<byte> data) {
            return data.Length >= 2 && IsGzipMagic(data[0], data[1]);
        }

        /// <summary>
        /// Allocates an owned native buffer of exactly <paramref name="size"/> bytes and reads that many
        /// bytes from <paramref name="source"/> into it. The returned reader has <c>Owned = true</c> and
        /// must be released via <see cref="Dispose"/>.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackReader AllocAndFillFromStream(Stream source, uint size) {
            return AllocAndFillFromStream(source, size, PackMemory.Default(), true);
        }

        #if UNITY_5_3_OR_NEWER
        /// <inheritdoc cref="AllocAndFillFromStream(Stream, uint)"/>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackReader AllocAndFillFromStream(Stream source, uint size, Allocator allocator) {
            return AllocAndFillFromStream(source, size, PackMemory.Default(allocator), allocator != Unity.Collections.Allocator.Temp && allocator != Unity.Collections.Allocator.TempJob);
        }
        #endif

        /// <inheritdoc cref="AllocAndFillFromStream(Stream, uint)"/>
        [MethodImpl(AggressiveInlining)]
        public static BinaryPackReader AllocAndFillFromStream(Stream source, uint size, PackAllocator allocator) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!allocator.IsCreated)
                throw new Exception("[StaticPack] PackAllocator is not created");
            #endif
            return AllocAndFillFromStream(source, size, allocator, true);
        }

        [MethodImpl(AggressiveInlining)]
        private static BinaryPackReader AllocAndFillFromStream(Stream source, uint size, PackAllocator allocator, bool tracked) {
            if (size > int.MaxValue) {
                throw new Exception("[StaticPack] size exceeds int.MaxValue");
            }

            var buffer = allocator.Realloc(allocator.State, null, 0, 0, size);
            try {
                ReadExactly(source, new Span<byte>(buffer, (int)size));
            }
            catch {
                allocator.Free(allocator.State, buffer);
                throw;
            }

            var reader = new BinaryPackReader(buffer, size, 0, allocator);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (tracked) {
                BinaryPackLeakTracker.TrackAlloc(buffer, ref reader.AllocId);
            }
            #endif
            return reader;
        }

        /// <summary>
        /// Creates a reader over the contents of <paramref name="data"/>. Gzip compression is autodetected
        /// from the standard RFC 1952 magic bytes (<c>0x1F 0x8B</c>) at offset 0.
        /// <para>When <paramref name="data"/> is not gzip-compressed its contents are copied into an owned
        /// native buffer (<c>Owned = true</c>); <paramref name="headerSize"/> and <paramref name="parseTotalSize"/> are ignored.</para>
        /// <para>When <paramref name="data"/> is gzip-compressed the method opens a decompression stream,
        /// peeks the first <paramref name="headerSize"/> bytes, passes them to <paramref name="parseTotalSize"/>,
        /// allocates a native buffer of exactly the returned size, copies the header and reads the remaining
        /// payload (<c>Owned = true</c>).</para>
        /// <para>Either way the reader must be released via <see cref="Dispose"/>.</para>
        /// </summary>
        public static BinaryPackReader AllocAndFillFromBytes(byte[] data, int headerSize = 0, TotalSizeParser parseTotalSize = null) {
            return AllocAndFillFromBytes(data, IsGzip(data), headerSize, parseTotalSize);
        }

        /// <summary>
        /// Creates a reader over the contents of <paramref name="data"/>, treating it as gzip-compressed exactly
        /// when <paramref name="gzip"/> says so. Use this overload for plain payloads whose first two bytes happen
        /// to be <c>0x1F 0x8B</c>, which the autodetecting overload takes for a gzip stream.
        /// <para><paramref name="headerSize"/> (1..256) and <paramref name="parseTotalSize"/> are required when
        /// <paramref name="gzip"/> is true and ignored otherwise. The reader must be released via <see cref="Dispose"/>.</para>
        /// </summary>
        public static BinaryPackReader AllocAndFillFromBytes(byte[] data, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null) {
            if (!gzip) {
                var allocator = PackMemory.Default();
                var buffer = allocator.Realloc(allocator.State, null, 0, 0, (uint)data.Length);
                new ReadOnlySpan<byte>(data).CopyTo(new Span<byte>(buffer, data.Length));
                var reader = new BinaryPackReader(buffer, (uint)data.Length, 0, allocator);
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                BinaryPackLeakTracker.TrackAlloc(buffer, ref reader.AllocId);
                #endif
                return reader;
            }

            using var ms = new MemoryStream(data, writable: false);
            using var gz = new GZipStream(ms, CompressionMode.Decompress, false);
            return AllocAndFillWithHeaderPeek(gz, headerSize, parseTotalSize, (ulong)data.Length);
        }

        /// <summary>
        /// Copies <paramref name="data"/> into an owned native buffer (<c>Owned = true</c>); the reader must be
        /// released via <see cref="Dispose"/>. The payload is taken as-is: a gzip stream is not detected and not
        /// decompressed, use <see cref="AllocAndFillFromBytes(byte[], int, TotalSizeParser)"/> for that.
        /// </summary>
        public static BinaryPackReader AllocAndFillFromSpan(ReadOnlySpan<byte> data) {
            var allocator = PackMemory.Default();
            var buffer = allocator.Realloc(allocator.State, null, 0, 0, (uint)data.Length);
            data.CopyTo(new Span<byte>(buffer, data.Length));
            var reader = new BinaryPackReader(buffer, (uint)data.Length, 0, allocator);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            BinaryPackLeakTracker.TrackAlloc(buffer, ref reader.AllocId);
            #endif
            return reader;
        }

        /// <summary>
        /// Creates a reader over the contents of a file. Gzip compression is autodetected from the
        /// standard RFC 1952 magic bytes (<c>0x1F 0x8B</c>) at offset 0.
        /// <para>When the file is not gzip-compressed it is read into an owned native buffer of exactly its
        /// length (<c>Owned = true</c>); <paramref name="headerSize"/> and <paramref name="parseTotalSize"/> are ignored.</para>
        /// <para>When the file is gzip-compressed a decompression stream is opened, the first
        /// <paramref name="headerSize"/> bytes are peeked and passed to <paramref name="parseTotalSize"/>,
        /// a native buffer of exactly the returned size is allocated, the header is copied in and the
        /// remaining payload is read (<c>Owned = true</c>).</para>
        /// <para>Either way the reader must be released via <see cref="Dispose"/>.</para>
        /// </summary>
        public static BinaryPackReader AllocAndFillFromFile(string filePath, int headerSize = 0, TotalSizeParser parseTotalSize = null) {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            Span<byte> magic = stackalloc byte[2];
            var magicRead = fs.Read(magic);
            fs.Seek(0, SeekOrigin.Begin);
            return AllocAndFillFromOpenFile(fs, magicRead >= 2 && IsGzipMagic(magic[0], magic[1]), headerSize, parseTotalSize);
        }

        /// <summary>
        /// Creates a reader over the contents of a file, treating it as gzip-compressed exactly when
        /// <paramref name="gzip"/> says so. Use this overload for plain files whose first two bytes happen to be
        /// <c>0x1F 0x8B</c>, which the autodetecting overload takes for a gzip stream.
        /// <para><paramref name="headerSize"/> (1..256) and <paramref name="parseTotalSize"/> are required when
        /// <paramref name="gzip"/> is true and ignored otherwise. The reader must be released via <see cref="Dispose"/>.</para>
        /// </summary>
        public static BinaryPackReader AllocAndFillFromFile(string filePath, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null) {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            return AllocAndFillFromOpenFile(fs, gzip, headerSize, parseTotalSize);
        }

        private static BinaryPackReader AllocAndFillFromOpenFile(FileStream source, bool gzip, int headerSize, TotalSizeParser parseTotalSize) {
            var length = source.Length;
            if (!gzip) {
                if (length > int.MaxValue) {
                    throw new Exception("[StaticPack] file length exceeds int.MaxValue");
                }

                return AllocAndFillFromStream(source, (uint)length);
            }

            using var gz = new GZipStream(source, CompressionMode.Decompress, false);
            return AllocAndFillWithHeaderPeek(gz, headerSize, parseTotalSize, (ulong)length);
        }

        private const ulong MAX_GZIP_EXPANSION_RATIO = 1032;

        private static BinaryPackReader AllocAndFillWithHeaderPeek(Stream source, int headerSize, TotalSizeParser parseTotalSize, ulong compressedLength) {
            if (headerSize <= 0 || headerSize > MAX_PEEK_HEADER_SIZE)
                throw new Exception($"[StaticPack] gzip source detected: headerSize must be in 1..{MAX_PEEK_HEADER_SIZE}");
            if (parseTotalSize == null)
                throw new Exception("[StaticPack] gzip source detected: parseTotalSize is required");
            Span<byte> headerBuffer = stackalloc byte[MAX_PEEK_HEADER_SIZE];
            var header = headerBuffer.Slice(0, headerSize);
            ReadExactly(source, header);
            var totalSize = parseTotalSize(header);
            // Unconditional: totalSize comes from (possibly corrupt) payload data; a value below headerSize
            // would otherwise overrun the exact-sized native allocation during the header copy.
            if (totalSize < headerSize)
                throw new Exception("[StaticPack] parseTotalSize returned less than headerSize");
            if (totalSize > int.MaxValue)
                throw new Exception("[StaticPack] parseTotalSize exceeds int.MaxValue");
            if (totalSize > compressedLength * MAX_GZIP_EXPANSION_RATIO + MAX_PEEK_HEADER_SIZE)
                throw new Exception("[StaticPack] parseTotalSize exceeds the maximum gzip expansion of the source");
            var allocator = PackMemory.Default();
            var buffer = allocator.Realloc(allocator.State, null, 0, 0, totalSize);
            header.CopyTo(new Span<byte>(buffer, headerSize));
            try {
                ReadExactly(source, new Span<byte>(buffer + headerSize, (int)totalSize - headerSize));
            }
            catch {
                allocator.Free(allocator.State, buffer);
                throw;
            }

            var reader = new BinaryPackReader(buffer, totalSize, 0, allocator);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            BinaryPackLeakTracker.TrackAlloc(buffer, ref reader.AllocId);
            #endif
            return reader;
        }

        /// <summary>
        /// Frees the owned native buffer when the reader was created via one of the <c>AllocAndFill*</c>
        /// factories. For readers that wrap externally owned memory this only nulls the pointer.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public void Dispose() {
            if (Buffer != null && Allocator.IsCreated) {
                #if DEBUG || FFS_PACK_ENABLE_DEBUG
                BinaryPackLeakTracker.TrackFree(AllocId); // no-op when _allocId == 0 (untracked allocators)
                #endif
                Allocator.Free(Allocator.State, Buffer);
            }

            Buffer = null;
            Position = 0;
            Allocator = default;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            AllocId = 0;
            #endif
        }

        /// <summary>
        /// Reads exactly <c>count</c> bytes in a loop: <see cref="Stream.Read(byte[], int, int)"/> may return
        /// fewer bytes than requested. <c>net7.0+</c> ships its own <c>Stream.ReadExactly</c>; this is a single
        /// wrapper that works on every target framework.
        /// </summary>
        public static void ReadExactly(Stream source, byte[] buffer, int offset, int count) {
            while (count > 0) {
                var n = source.Read(buffer, offset, count);
                if (n == 0) {
                    throw new EndOfStreamException();
                }

                offset += n;
                count -= n;
            }
        }

        internal const int STREAM_COPY_CHUNK_SIZE = 1 << 20;

        /// <inheritdoc cref="ReadExactly(Stream, byte[], int, int)"/>
        public static void ReadExactly(Stream source, Span<byte> destination) {
            #if NET6_0_OR_GREATER
            while (!destination.IsEmpty) {
                var n = source.Read(destination);
                if (n == 0) {
                    throw new EndOfStreamException();
                }

                destination = destination.Slice(n);
            }
            #else
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(destination.Length, STREAM_COPY_CHUNK_SIZE));
            try {
                while (!destination.IsEmpty) {
                    var chunk = Math.Min(destination.Length, buffer.Length);
                    ReadExactly(source, buffer, 0, chunk);
                    new ReadOnlySpan<byte>(buffer, 0, chunk).CopyTo(destination);
                    destination = destination.Slice(chunk);
                }
            }
            finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            #endif
        }

        /// <summary>
        /// Wraps the same buffer as a non-owning writer over [0, <see cref="Size"/>) positioned at
        /// <see cref="Size"/>. The writer has no allocator and no slack capacity, so a sequential <c>Write*</c>
        /// from that position throws on the first byte: rewind <c>Position</c> or use the <c>WriteXAt</c> methods
        /// to patch data in place. To append to an owned buffer use <see cref="AsWriterCompact"/> on a reader
        /// with <c>Position == 0</c>.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public BinaryPackWriter AsWriter() {
            return BinaryPackWriter.Create(Buffer, Size, Size);
        }

        [MethodImpl(AggressiveInlining)]
        public BinaryPackReader AsReader(uint position) {
            return new BinaryPackReader(Buffer, Size, position);
        }

        /// <summary>
        /// Moves the unread tail to the start of the buffer and returns a writer over it.
        /// The writer inherits buffer ownership; the reader is invalidated (its pointer is nulled),
        /// so dispose the writer instead of the reader afterwards. Only for a reader that owns its buffer:
        /// over borrowed memory this would rewrite the owner's bytes in place.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public BinaryPackWriter AsWriterCompact() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!Owned)
                throw new Exception("[StaticPack] AsWriterCompact on a reader over memory it does not own would rewrite that memory in place");
            #endif
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (Position > Size)
                throw new Exception("[StaticPack] ByteReader, Out of bound");
            #endif
            var count = Size - Position;
            if (count != 0 && Position != 0) {
                PackMemory.Move(Buffer, Buffer + Position, count);
            }

            var writer = new BinaryPackWriter(Buffer, Size, count, Allocator);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            writer.AllocId = AllocId;
            AllocId = 0;
            #endif
            Buffer = null;
            Position = 0;
            Allocator = default;
            return writer;
        }

        [MethodImpl(AggressiveInlining)]
        public bool HasNext() {
            return Position < Size;
        }

        [MethodImpl(AggressiveInlining)]
        public bool HasNext(uint bytesCount) {
            // ulong math: Position + bytesCount must not wrap for counts coming from corrupt payload data.
            return (ulong)Position + bytesCount <= Size;
        }

        #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
        [MethodImpl(AggressiveInlining)]
        private bool IsUnmanagedPayloadValid(int elementCount, int elementSize, uint byteSize) {
            return elementCount >= 0 && (ulong)elementCount * (ulong)elementSize == byteSize && HasNext(byteSize);
        }

        [MethodImpl(AggressiveInlining)]
        private bool IsElementCountValid(long elementCount, uint byteSize) {
            return elementCount >= 0 && HasNext(byteSize) && (ulong)elementCount + Position <= Size;
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void SkipNext() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(byte)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            Position++;
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipNext(uint bytesCount) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(bytesCount))
                throw new Exception("ByteReader, Out of bound");
            #endif
            Position += bytesCount;
        }

        /// <summary> Reads the flag written by <c>WriteNotNullFlag</c>: true when a value follows. </summary>
        [MethodImpl(AggressiveInlining)]
        public bool ReadNotNullFlag() {
            return !ReadNullFlag();
        }

        [MethodImpl(AggressiveInlining)]
        public bool ReadNullFlag() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(byte)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            return Buffer[Position++] == 0;
        }

        #region PRIMITIVES
        [MethodImpl(AggressiveInlining)]
        public byte ReadByte() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(byte)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            return Buffer[Position++];
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadByte(out byte value) {
            if (HasNext(sizeof(byte))) {
                value = Buffer[Position++];
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public sbyte ReadSbyte() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(byte)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            return (sbyte)Buffer[Position++];
        }

        [Obsolete("Renamed to ReadSbyte to match WriteSbyte and the Ushort/Uint/Ulong casing.")]
        [MethodImpl(AggressiveInlining)]
        public sbyte ReadSByte() {
            return ReadSbyte();
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadSbyte(out sbyte value) {
            if (HasNext(sizeof(sbyte))) {
                value = (sbyte)Buffer[Position++];
                return true;
            }

            value = default;
            return false;
        }

        [Obsolete("Renamed to TryReadSbyte to match WriteSbyte and the Ushort/Uint/Ulong casing.")]
        [MethodImpl(AggressiveInlining)]
        public bool TryReadSByte(out sbyte value) {
            return TryReadSbyte(out value);
        }

        [MethodImpl(AggressiveInlining)]
        public bool ReadBool() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(bool)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            return Buffer[Position++] != 0;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadBool(out bool value) {
            if (HasNext(sizeof(bool))) {
                value = Buffer[Position++] != 0;
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public short ReadShort() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(short)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<short>(ref Buffer[Position]);
            Position += 2;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadShort(out short value) {
            if (HasNext(sizeof(short))) {
                value = ReadShort();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public ushort ReadUshort() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(ushort)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<ushort>(ref Buffer[Position]);
            Position += 2;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadUshort(out ushort value) {
            if (HasNext(sizeof(ushort))) {
                value = ReadUshort();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public char ReadChar() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(char)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<char>(ref Buffer[Position]);
            Position += 2;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadChar(out char value) {
            if (HasNext(sizeof(char))) {
                value = ReadChar();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public int ReadInt() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(int)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<int>(ref Buffer[Position]);
            Position += 4;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadInt(out int value) {
            if (HasNext(sizeof(int))) {
                value = ReadInt();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public uint ReadUint() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(uint)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<uint>(ref Buffer[Position]);
            Position += 4;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadUint(out uint value) {
            if (HasNext(sizeof(uint))) {
                value = ReadUint();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public int ReadVarInt() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext())
                throw new Exception("ByteReader, Out of bound");
            #endif
            var b0 = Buffer[Position++];
            if ((b0 & 0x80) == 0)
                return b0;

            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext())
                throw new Exception("ByteReader, Out of bound");
            #endif
            var b1 = Buffer[Position++];
            if ((b1 & 0x80) == 0)
                return (b0 & 0x7F) | (b1 << 7);

            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext())
                throw new Exception("ByteReader, Out of bound");
            #endif
            var b2 = Buffer[Position++];
            if ((b2 & 0x80) == 0)
                return (b0 & 0x7F) | ((b1 & 0x7F) << 7) | (b2 << 14);

            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext())
                throw new Exception("ByteReader, Out of bound");
            #endif
            var b3 = Buffer[Position++];
            if ((b3 & 0x80) == 0)
                return (b0 & 0x7F) | ((b1 & 0x7F) << 7) | ((b2 & 0x7F) << 14) | (b3 << 21);

            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext())
                throw new Exception("ByteReader, Out of bound");
            #endif
            var b4 = Buffer[Position++];
            return (b0 & 0x7F) | ((b1 & 0x7F) << 7) | ((b2 & 0x7F) << 14) | ((b3 & 0x7F) << 21) | (b4 << 28);
        }

        /// <summary> Reads a VarInt when the whole encoding is available, leaving Position untouched otherwise. </summary>
        [MethodImpl(AggressiveInlining)]
        public bool TryReadVarInt(out int value) {
            var startPosition = Position;
            value = 0;
            for (var shift = 0; shift <= 28; shift += 7) {
                if (!HasNext()) {
                    Position = startPosition;
                    value = default;
                    return false;
                }

                var current = Buffer[Position++];
                if (shift == 28 || (current & 0x80) == 0) {
                    value |= current << shift;
                    return true;
                }

                value |= (current & 0x7F) << shift;
            }

            Position = startPosition;
            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public short ReadVarShort() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext())
                throw new Exception("ByteReader, Out of bound");
            #endif
            var b0 = Buffer[Position++];
            if ((b0 & 0b10000000) == 0) {
                return b0;
            }

            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext())
                throw new Exception("ByteReader, Out of bound");
            #endif
            var b1 = Buffer[Position++];
            return (short)((b0 & 0b1111111) | (b1 << 7));
        }

        /// <summary> Reads a VarShort when the whole encoding is available, leaving Position untouched otherwise. </summary>
        [MethodImpl(AggressiveInlining)]
        public bool TryReadVarShort(out short value) {
            var startPosition = Position;
            if (HasNext()) {
                var b0 = Buffer[Position++];
                if ((b0 & 0b10000000) == 0) {
                    value = b0;
                    return true;
                }

                if (HasNext()) {
                    var b1 = Buffer[Position++];
                    value = (short)((b0 & 0b1111111) | (b1 << 7));
                    return true;
                }
            }

            Position = startPosition;
            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public long ReadLong() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(long)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<long>(ref Buffer[Position]);
            Position += 8;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadLong(out long value) {
            if (HasNext(sizeof(long))) {
                value = ReadLong();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public ulong ReadUlong() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(ulong)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<ulong>(ref Buffer[Position]);
            Position += 8;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadUlong(out ulong value) {
            if (HasNext(sizeof(ulong))) {
                value = ReadUlong();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public float ReadFloat() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(float)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<float>(ref Buffer[Position]);
            Position += 4;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadFloat(out float value) {
            if (HasNext(sizeof(float))) {
                value = ReadFloat();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public double ReadDouble() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(double)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var value = Unsafe.ReadUnaligned<double>(ref Buffer[Position]);
            Position += 8;
            return value;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadDouble(out double value) {
            if (HasNext(sizeof(double))) {
                value = ReadDouble();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadByte(out byte v0, out byte v1) {
            v0 = ReadByte();
            v1 = ReadByte();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadByte(out byte v0, out byte v1, out byte v2) {
            v0 = ReadByte();
            v1 = ReadByte();
            v2 = ReadByte();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadByte(out byte v0, out byte v1, out byte v2, out byte v3) {
            v0 = ReadByte();
            v1 = ReadByte();
            v2 = ReadByte();
            v3 = ReadByte();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadShort(out short v0, out short v1) {
            v0 = ReadShort();
            v1 = ReadShort();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadShort(out short v0, out short v1, out short v2) {
            v0 = ReadShort();
            v1 = ReadShort();
            v2 = ReadShort();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadShort(out short v0, out short v1, out short v2, out short v3) {
            v0 = ReadShort();
            v1 = ReadShort();
            v2 = ReadShort();
            v3 = ReadShort();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUshort(out ushort v0, out ushort v1) {
            v0 = ReadUshort();
            v1 = ReadUshort();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUshort(out ushort v0, out ushort v1, out ushort v2) {
            v0 = ReadUshort();
            v1 = ReadUshort();
            v2 = ReadUshort();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUshort(out ushort v0, out ushort v1, out ushort v2, out ushort v3) {
            v0 = ReadUshort();
            v1 = ReadUshort();
            v2 = ReadUshort();
            v3 = ReadUshort();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadInt(out int v0, out int v1) {
            v0 = ReadInt();
            v1 = ReadInt();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadInt(out int v0, out int v1, out int v2) {
            v0 = ReadInt();
            v1 = ReadInt();
            v2 = ReadInt();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadInt(out int v0, out int v1, out int v2, out int v3) {
            v0 = ReadInt();
            v1 = ReadInt();
            v2 = ReadInt();
            v3 = ReadInt();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUint(out uint v0, out uint v1) {
            v0 = ReadUint();
            v1 = ReadUint();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUint(out uint v0, out uint v1, out uint v2) {
            v0 = ReadUint();
            v1 = ReadUint();
            v2 = ReadUint();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUint(out uint v0, out uint v1, out uint v2, out uint v3) {
            v0 = ReadUint();
            v1 = ReadUint();
            v2 = ReadUint();
            v3 = ReadUint();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadFloat(out float v0, out float v1) {
            v0 = ReadFloat();
            v1 = ReadFloat();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadFloat(out float v0, out float v1, out float v2) {
            v0 = ReadFloat();
            v1 = ReadFloat();
            v2 = ReadFloat();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadFloat(out float v0, out float v1, out float v2, out float v3) {
            v0 = ReadFloat();
            v1 = ReadFloat();
            v2 = ReadFloat();
            v3 = ReadFloat();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadLong(out long v0, out long v1) {
            v0 = ReadLong();
            v1 = ReadLong();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadLong(out long v0, out long v1, out long v2) {
            v0 = ReadLong();
            v1 = ReadLong();
            v2 = ReadLong();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadLong(out long v0, out long v1, out long v2, out long v3) {
            v0 = ReadLong();
            v1 = ReadLong();
            v2 = ReadLong();
            v3 = ReadLong();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUlong(out ulong v0, out ulong v1) {
            v0 = ReadUlong();
            v1 = ReadUlong();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUlong(out ulong v0, out ulong v1, out ulong v2) {
            v0 = ReadUlong();
            v1 = ReadUlong();
            v2 = ReadUlong();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUlong(out ulong v0, out ulong v1, out ulong v2, out ulong v3) {
            v0 = ReadUlong();
            v1 = ReadUlong();
            v2 = ReadUlong();
            v3 = ReadUlong();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadDouble(out double v0, out double v1) {
            v0 = ReadDouble();
            v1 = ReadDouble();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadDouble(out double v0, out double v1, out double v2) {
            v0 = ReadDouble();
            v1 = ReadDouble();
            v2 = ReadDouble();
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadDouble(out double v0, out double v1, out double v2, out double v3) {
            v0 = ReadDouble();
            v1 = ReadDouble();
            v2 = ReadDouble();
            v3 = ReadDouble();
        }
        #endregion

        #region BASE_VALUE_TYPES
        [MethodImpl(AggressiveInlining)]
        public T? ReadNullable<T>() where T : struct {
            if (ReadNullFlag())
                return null;

            return BinaryPack<T>.Read(ref this);
        }

        [MethodImpl(AggressiveInlining)]
        public Guid ReadGuid() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(16))
                throw new Exception("ByteReader, Out of bound");
            #endif
            var guid = Unsafe.ReadUnaligned<Guid>(Buffer + Position);
            Position += 16;
            return guid;
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadGuid(out Guid value) {
            if (HasNext(16)) {
                value = ReadGuid();
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(AggressiveInlining)]
        public DateTime ReadDateTime() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(sizeof(long)))
                throw new Exception("ByteReader, Out of bound");
            #endif
            return DateTime.FromBinary(ReadLong());
        }

        [MethodImpl(AggressiveInlining)]
        public bool TryReadDateTime(out DateTime value) {
            if (HasNext(sizeof(long))) {
                value = ReadDateTime();
                return true;
            }

            value = default;
            return false;
        }
        #endregion

        #region STRING
        [MethodImpl(AggressiveInlining)]
        public string ReadString32() {
            if (ReadNullFlag())
                return null;

            var byteCount = ReadInt();
            return Encoding.UTF8.GetString(ReadBytesAsSpan((uint)byteCount));
        }

        [MethodImpl(AggressiveInlining)]
        public string ReadString16() {
            if (ReadNullFlag())
                return null;

            var byteCount = ReadUshort();
            return Encoding.UTF8.GetString(ReadBytesAsSpan(byteCount));
        }

        [MethodImpl(AggressiveInlining)]
        public string ReadString8() {
            if (ReadNullFlag())
                return null;

            var byteCount = ReadByte();
            return Encoding.UTF8.GetString(ReadBytesAsSpan(byteCount));
        }

        /// <summary> Reads the string when flag, length and payload are all available, leaving Position untouched otherwise. </summary>
        [MethodImpl(AggressiveInlining)]
        public bool TryReadString8(out string value) {
            return TryReadString(sizeof(byte), out value);
        }

        /// <inheritdoc cref="TryReadString8"/>
        [MethodImpl(AggressiveInlining)]
        public bool TryReadString16(out string value) {
            return TryReadString(sizeof(ushort), out value);
        }

        /// <inheritdoc cref="TryReadString8"/>
        [MethodImpl(AggressiveInlining)]
        public bool TryReadString32(out string value) {
            return TryReadString(sizeof(int), out value);
        }

        private bool TryReadString(uint prefixSize, out string value) {
            var startPosition = Position;
            if (HasNext(sizeof(byte) + prefixSize)) {
                if (ReadNullFlag()) {
                    value = null;
                    return true;
                }

                long byteCount;
                if (prefixSize == sizeof(byte)) {
                    byteCount = ReadByte();
                } else if (prefixSize == sizeof(ushort)) {
                    byteCount = ReadUshort();
                } else {
                    byteCount = ReadInt();
                }

                if (byteCount >= 0 && HasNext((uint)byteCount)) {
                    value = Encoding.UTF8.GetString(ReadBytesAsSpan((uint)byteCount));
                    return true;
                }
            }

            Position = startPosition;
            value = default;
            return false;
        }
        #endregion

        #region COLLECTIONS
        [MethodImpl(AggressiveInlining)]
        public T[] ReadArrayUnmanaged<T>() where T : unmanaged {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsUnmanagedPayloadValid(count, sizeof(T), byteSize)) {
                throw new Exception($"[ReadArrayUnmanaged<{typeof(T)}>] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var result = new T[count];

            if (count > 0) {
                fixed (void* dataPtr = &result[0]) {
                    PackMemory.Copy((byte*)dataPtr, Buffer + Position, byteSize);
                }

                Position += byteSize;
            }

            return result;
        }

        /// <summary>
        /// Returns the stored elements as a view over the reader's own buffer: no copy and no allocation.
        /// An empty span is returned both for a stored null and for a stored empty array.
        /// <para>The payload sits at an arbitrary byte offset, so the elements are generally not aligned to
        /// <c>sizeof(T)</c> - the reader accesses its buffer through unaligned loads throughout. This is fine for
        /// byte-sized elements everywhere, and for scalar structs on x64 and ARM64. For types that need real
        /// alignment, such as SIMD vector types, take the <see cref="PackArenaAllocator"/> overload, which copies
        /// into 16-aligned arena memory.</para>
        /// <para>The span stays valid while the reader holds its buffer.</para>
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public ReadOnlySpan<T> ReadArrayUnmanagedAsSpan<T>() where T : unmanaged {
            if (ReadNullFlag())
                return default;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsUnmanagedPayloadValid(count, sizeof(T), byteSize))
                throw new Exception("[StaticPack] ReadArrayUnmanagedAsSpan: corrupted payload");
            #endif
            var span = new ReadOnlySpan<T>(Buffer + Position, count);
            Position += byteSize;
            return span;
        }

        /// <summary>
        /// Copies the stored elements into <paramref name="arena"/> and returns them as a span. Nothing is
        /// allocated on the managed heap and the memory is 16-aligned, so this is the overload for element types
        /// that require alignment. The span stays valid until the arena is reset or disposed.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public Span<T> ReadArrayUnmanagedAsSpan<T>(in PackArenaAllocator arena) where T : unmanaged {
            if (ReadNullFlag())
                return default;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsUnmanagedPayloadValid(count, sizeof(T), byteSize))
                throw new Exception("[StaticPack] ReadArrayUnmanagedAsSpan: corrupted payload");
            #endif
            var destination = arena.AllocPtr<T>(count, false);
            PackMemory.Copy((byte*)destination, Buffer + Position, byteSize);
            Position += byteSize;
            return new Span<T>(destination, count);
        }

        /// <summary>
        /// Reads the header an array or a collection was written with and leaves <c>Position</c> on the first
        /// element, so the elements can be read one by one into storage of the caller's choosing. Returns false
        /// when the stored value was null.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public bool TryReadArrayHeader(out int count) {
            if (ReadNullFlag()) {
                count = 0;
                return false;
            }

            count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize))
                throw new Exception("[StaticPack] TryReadArrayHeader: corrupted payload");
            #endif
            return true;
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadArrayUnmanaged<T>(ref T[] result) where T : unmanaged {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsUnmanagedPayloadValid(count, sizeof(T), byteSize)) {
                throw new Exception($"[ReadArrayUnmanaged<{typeof(T)}>] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null || count > result.Length) {
                result = new T[count];
            }

            if (count > 0) {
                fixed (void* dataPtr = &result[0]) {
                    PackMemory.Copy((byte*)dataPtr, Buffer + Position, byteSize);
                }

                Position += byteSize;
            }

            return count;
        }

        /// <summary>
        /// Returns the number of elements read into <paramref name="result"/> starting at <paramref name="idx"/>,
        /// or -1 when the null flag was read - <paramref name="result"/> is left untouched in that case, so a
        /// null segment written into a shared buffer does not erase the rest of that buffer's content.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadArrayUnmanaged<T>(ref T[] result, int idx) where T : unmanaged {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            var required = (long)count + idx;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (idx < 0 || required > int.MaxValue || !IsUnmanagedPayloadValid(count, sizeof(T), byteSize)) {
                throw new Exception($"[ReadArrayUnmanaged<{typeof(T)}>] Corrupted payload - count {count}, stored byte size {byteSize}, offset {idx}, bytes left {Size - Position}");
            }
            #endif

            if (result == null || required > result.Length) {
                Array.Resize(ref result, (int)required);
            }

            if (count > 0) {
                fixed (void* dataPtr = &result[idx]) {
                    PackMemory.Copy((byte*)dataPtr, Buffer + Position, byteSize);
                }

                Position += byteSize;
            }

            return count;
        }

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2DUnmanaged<T>() where T : unmanaged {
            if (ReadNullFlag())
                return null;

            var dim0 = ReadInt();
            var dim1 = ReadInt();
            var byteSize = ReadUint();

            var res = new T[dim0, dim1];

            if (dim0 == 0 || dim1 == 0) {
                return res;
            }

            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsUnmanagedPayloadValid(res.Length, sizeof(T), byteSize)) {
                throw new Exception($"[ReadArray2DUnmanaged<{typeof(T)}>] Corrupted payload - dimensions {dim0}x{dim1}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            fixed (void* dataPtr = &res[0, 0]) {
                PackMemory.Copy((byte*)dataPtr, Buffer + Position, byteSize);
            }

            Position += byteSize;

            return res;
        }

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3DUnmanaged<T>() where T : unmanaged {
            if (ReadNullFlag())
                return null;

            var dim0 = ReadInt();
            var dim1 = ReadInt();
            var dim2 = ReadInt();
            var byteSize = ReadUint();

            var res = new T[dim0, dim1, dim2];

            if (dim0 == 0 || dim1 == 0 || dim2 == 0) {
                return res;
            }

            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsUnmanagedPayloadValid(res.Length, sizeof(T), byteSize)) {
                throw new Exception($"[ReadArray3DUnmanaged<{typeof(T)}>] Corrupted payload - dimensions {dim0}x{dim1}x{dim2}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            fixed (void* dataPtr = &res[0, 0, 0]) {
                PackMemory.Copy((byte*)dataPtr, Buffer + Position, byteSize);
            }

            Position += byteSize;

            return res;
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public T[] ReadArray<T>() {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadArray] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var res = new T[count];
            for (var i = 0; i < count; i++) {
                res[i] = BinaryPack<T>.Read(ref this);
            }

            return res;
        }

        /// <summary>
        /// Reads into an array rented from <see cref="ArrayPool{T}"/>. Use the segment itself or its
        /// <c>Count</c>: the underlying <c>Array</c> is longer than the payload and its tail holds whatever
        /// an earlier rent left there. Release it through <paramref name="poolHandle"/> exactly once.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public ArraySegment<T> ReadArrayPooled<T>(out ArrayPoolHandle<T> poolHandle) {
            poolHandle = default;
            if (ReadNullFlag())
                return default;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadArrayPooled] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            T[] result;

            if (count > 0) {
                result = ArrayPool<T>.Shared.Rent(count);
                poolHandle = new ArrayPoolHandle<T>(result);
                try {
                    for (var i = 0; i < count; i++) {
                        result[i] = BinaryPack<T>.Read(ref this);
                    }
                }
                catch {
                    // The out parameter never reaches the caller when the method throws, so nobody else can return it.
                    poolHandle.Return();
                    poolHandle = default;
                    throw;
                }
            } else {
                result = Array.Empty<T>();
            }

            return new ArraySegment<T>(result, 0, count);
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadArray<T>(ref T[] result) {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadArray] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null || count > result.Length) {
                result = new T[count];
            }

            for (var i = 0; i < count; i++) {
                result[i] = BinaryPack<T>.Read(ref this);
            }

            return count;
        }

        /// <summary>
        /// Returns the number of elements read into <paramref name="result"/> starting at <paramref name="idx"/>,
        /// or -1 when the null flag was read - <paramref name="result"/> is left untouched in that case, so a
        /// null segment written into a shared buffer does not erase the rest of that buffer's content.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadArray<T>(ref T[] result, int idx) {
            if (ReadNullFlag())
                return -1;

            var count = ReadInt();
            var byteSize = ReadUint();
            var required = (long)count + idx;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (idx < 0 || required > int.MaxValue || !IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadArray] Corrupted payload - count {count}, stored byte size {byteSize}, offset {idx}, bytes left {Size - Position}");
            }
            #endif

            if (result == null || required > result.Length) {
                Array.Resize(ref result, (int)required);
            }

            for (var i = 0; i < count; i++) {
                result[i + idx] = BinaryPack<T>.Read(ref this);
            }

            return count;
        }

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2D<T>() {
            if (ReadNullFlag())
                return null;

            var dim0 = ReadInt();
            var dim1 = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (dim0 < 0 || dim1 < 0 || !IsElementCountValid((long)dim0 * dim1, byteSize)) {
                throw new Exception($"[ReadArray2D<{typeof(T)}>] Corrupted payload - dimensions {dim0}x{dim1}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var res = new T[dim0, dim1];
            for (var i0 = 0; i0 < dim0; i0++) {
                for (var i1 = 0; i1 < dim1; i1++) {
                    res[i0, i1] = BinaryPack<T>.Read(ref this);
                }
            }

            return res;
        }

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3D<T>() {
            if (ReadNullFlag())
                return null;

            var dim0 = ReadInt();
            var dim1 = ReadInt();
            var dim2 = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (dim0 < 0 || dim1 < 0 || dim2 < 0 || !IsElementCountValid((long)dim0 * dim1, byteSize) || !IsElementCountValid((long)dim0 * dim1 * dim2, byteSize)) {
                throw new Exception($"[ReadArray3D<{typeof(T)}>] Corrupted payload - dimensions {dim0}x{dim1}x{dim2}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var res = new T[dim0, dim1, dim2];
            for (var i0 = 0; i0 < dim0; i0++) {
                for (var i1 = 0; i1 < dim1; i1++) {
                    for (var i2 = 0; i2 < dim2; i2++) {
                        res[i0, i1, i2] = BinaryPack<T>.Read(ref this);
                    }
                }
            }

            return res;
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void SkipArrayHeaders() {
            if (!ReadNullFlag()) {
                Position += ARRAY_INFO_BYTES;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipArray2DHeaders() {
            if (!ReadNullFlag()) {
                Position += ARRAY2_INFO_BYTES;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipArray3DHeaders() {
            if (!ReadNullFlag()) {
                Position += ARRAY3_INFO_BYTES;
            }
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipArray() {
            if (ReadNullFlag())
                return;

            Position += sizeof(int); // count
            var byteSize = ReadUint();
            SkipNext(byteSize);
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipArray2D() {
            if (ReadNullFlag())
                return;

            Position += sizeof(int); // count 0
            Position += sizeof(int); // count 1
            var byteSize = ReadUint();
            SkipNext(byteSize);
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipArray3D() {
            if (ReadNullFlag())
                return;

            Position += sizeof(int); // count 0
            Position += sizeof(int); // count 1
            Position += sizeof(int); // count 2
            var byteSize = ReadUint();
            SkipNext(byteSize);
        }

        [MethodImpl(AggressiveInlining)]
        public List<T> ReadList<T>() {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadList] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var result = new List<T>(count);
            for (var i = 0; i < count; i++) {
                result.Add(BinaryPack<T>.Read(ref this));
            }

            return result;
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadList<T>(ref List<T> result) {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadList] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null) {
                result = new List<T>(count);
            } else {
                result.Clear();
            }

            for (var i = 0; i < count; i++) {
                result.Add(BinaryPack<T>.Read(ref this));
            }

            return count;
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipListHeaders() {
            SkipArrayHeaders();
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipList() {
            SkipArray();
        }

        [MethodImpl(AggressiveInlining)]
        public Queue<T> ReadQueue<T>() {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadQueue] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var result = new Queue<T>(count);
            for (var i = 0; i < count; i++) {
                result.Enqueue(BinaryPack<T>.Read(ref this));
            }

            return result;
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadQueue<T>(ref Queue<T> result) {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadQueue] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null) {
                result = new Queue<T>(count);
            } else {
                result.Clear();
            }

            for (var i = 0; i < count; i++) {
                result.Enqueue(BinaryPack<T>.Read(ref this));
            }

            return count;
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipQueueHeaders() {
            SkipArrayHeaders();
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipQueue() {
            SkipArray();
        }

        [MethodImpl(AggressiveInlining)]
        public Stack<T> ReadStack<T>() {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadStack] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var result = new Stack<T>(count);
            for (var i = 0; i < count; i++) {
                result.Push(BinaryPack<T>.Read(ref this));
            }

            return result;
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadStack<T>(ref Stack<T> result) {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadStack] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null) {
                result = new Stack<T>(count);
            } else {
                result.Clear();
            }

            for (var i = 0; i < count; i++) {
                result.Push(BinaryPack<T>.Read(ref this));
            }

            return count;
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipStackHeaders() {
            SkipArrayHeaders();
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipStack() {
            SkipArray();
        }

        [MethodImpl(AggressiveInlining)]
        public LinkedList<T> ReadLinkedList<T>() {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadLinkedList] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var result = new LinkedList<T>();
            for (var i = 0; i < count; i++) {
                result.AddLast(BinaryPack<T>.Read(ref this));
            }

            return result;
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadLinkedList<T>(ref LinkedList<T> result) {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadLinkedList] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null) {
                result = new LinkedList<T>();
            } else {
                result.Clear();
            }

            for (var i = 0; i < count; i++) {
                result.AddLast(BinaryPack<T>.Read(ref this));
            }

            return count;
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipLinkedListHeaders() {
            SkipArrayHeaders();
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipLinkedList() {
            SkipArray();
        }

        [MethodImpl(AggressiveInlining)]
        public HashSet<T> ReadHashSet<T>() {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadHashSet] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var res = new HashSet<T>(count);
            for (var i = 0; i < count; i++) {
                res.Add(BinaryPack<T>.Read(ref this));
            }

            return res;
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadHashSet<T>(ref HashSet<T> result) {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadHashSet] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null) {
                result = new HashSet<T>(count);
            } else {
                result.Clear();
            }

            for (var i = 0; i < count; i++) {
                result.Add(BinaryPack<T>.Read(ref this));
            }

            return count;
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipHashSetHeaders() {
            SkipArrayHeaders();
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipHashSet() {
            SkipArray();
        }

        [MethodImpl(AggressiveInlining)]
        public Dictionary<TK, TV> ReadDictionary<TK, TV>() {
            if (ReadNullFlag())
                return null;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadDictionary] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            var result = new Dictionary<TK, TV>(count);
            for (var i = 0; i < count; i++) {
                var key = BinaryPack<TK>.Read(ref this);
                var val = BinaryPack<TV>.Read(ref this);
                result[key] = val;
            }

            return result;
        }

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadDictionary<TK, TV>(ref Dictionary<TK, TV> result) {
            if (ReadNullFlag()) {
                return -1;
            }

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize)) {
                throw new Exception($"[ReadDictionary] Corrupted payload - count {count}, stored byte size {byteSize}, bytes left {Size - Position}");
            }
            #endif

            if (result == null) {
                result = new Dictionary<TK, TV>(count);
            } else {
                result.Clear();
            }

            for (var i = 0; i < count; i++) {
                var key = BinaryPack<TK>.Read(ref this);
                var val = BinaryPack<TV>.Read(ref this);
                result[key] = val;
            }

            return count;
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipDictionaryHeaders() {
            SkipArrayHeaders();
        }

        [MethodImpl(AggressiveInlining)]
        public void SkipDictionary() {
            SkipArray();
        }
        #endregion

        #region SPAN
        [MethodImpl(AggressiveInlining)]
        public void ReadBytes(Span<byte> destination) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext((uint)destination.Length))
                throw new Exception("ByteReader, Out of bound");
            #endif
            new ReadOnlySpan<byte>(Buffer + Position, destination.Length).CopyTo(destination);
            Position += (uint)destination.Length;
        }

        [MethodImpl(AggressiveInlining)]
        public ReadOnlySpan<byte> ReadBytesAsSpan(uint count) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!HasNext(count))
                throw new Exception("[StaticPack] ByteReader, Out of bound");
            #endif
            var span = new ReadOnlySpan<byte>(Buffer + Position, (int)count);
            Position += count;
            return span;
        }

        [MethodImpl(AggressiveInlining)]
        public ReadOnlySpan<byte> RemainingAsSpan() {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (Position > Size)
                throw new Exception("[StaticPack] ByteReader, Out of bound");
            #endif
            return new ReadOnlySpan<byte>(Buffer + Position, (int)(Size - Position));
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T>(Span<T> destination) where T : unmanaged {
            if (destination.Length == 0)
                return;
            var byteCount = (ulong)destination.Length * (uint)sizeof(T);
            if (byteCount > int.MaxValue) {
                throw new Exception("[StaticPack] payload byte size exceeds int.MaxValue");
            }

            var size = (uint)byteCount;
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            fixed (T* dest = destination) {
                PackMemory.Copy((byte*)dest, Buffer + Position, size);
            }

            Position += size;
        }

        /// <summary>
        /// Reads the elements into <paramref name="destination"/> and returns how many were read, or -1 when the
        /// stored value was null.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadSpanUnmanaged<T>(Span<T> destination) where T : unmanaged {
            if (ReadNullFlag())
                return -1;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsUnmanagedPayloadValid(count, sizeof(T), byteSize))
                throw new Exception("[StaticPack] ReadSpanUnmanaged: corrupted payload");
            if (count > destination.Length)
                throw new Exception("[StaticPack] ReadSpanUnmanaged: destination too small");
            #endif

            if (count > 0) {
                fixed (T* dest = destination) {
                    PackMemory.Copy((byte*)dest, Buffer + Position, byteSize);
                }

                Position += byteSize;
            }

            return count;
        }
        /// <summary>
        /// Reads the elements into <paramref name="destination"/> and returns how many were read, or -1 when the
        /// stored value was null. The counterpart of <c>WriteSpan&lt;T&gt;</c>; nothing is allocated.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public int ReadSpan<T>(Span<T> destination) {
            if (ReadNullFlag())
                return -1;

            var count = ReadInt();
            var byteSize = ReadUint();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG || !FFS_PACK_DISABLE_MEMORY_CHECK
            if (!IsElementCountValid(count, byteSize))
                throw new Exception("[StaticPack] ReadSpan: corrupted payload");
            if (count > destination.Length)
                throw new Exception("[StaticPack] ReadSpan: destination too small");
            #endif
            for (var i = 0; i < count; i++) {
                destination[i] = BinaryPack<T>.Read(ref this);
            }

            return count;
        }
        #endregion

        #region UNMANAGED_GENERIC
        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1>(out T1 v1)
            where T1 : unmanaged {
            var size = (uint)Unsafe.SizeOf<T1>();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Buffer[Position]);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1, T2>(out T1 v1, out T2 v2)
            where T1 : unmanaged where T2 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1, T2, T3>(out T1 v1, out T2 v2, out T3 v3)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1, T2, T3, T4>(out T1 v1, out T2 v2, out T3 v3, out T4 v4)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1, T2, T3, T4, T5>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1, T2, T3, T4, T5, T6>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1, T2, T3, T4, T5, T6, T7>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanaged<T1, T2, T3, T4, T5, T6, T7, T8>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7, out T8 v8)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged {
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                              Unsafe.SizeOf<T8>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            v8 = Unsafe.ReadUnaligned<T8>(ref Unsafe.Add(ref src,
                Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1>(out T1 v1)
            where T1 : unmanaged {
            var payload = (uint)Unsafe.SizeOf<T1>();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1, T2>(out T1 v1, out T2 v2)
            where T1 : unmanaged where T2 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1, T2, T3>(out T1 v1, out T2 v2, out T3 v3)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1, T2, T3, T4>(out T1 v1, out T2 v2, out T3 v3, out T4 v4)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1, T2, T3, T4, T5>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1, T2, T3, T4, T5, T6>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1, T2, T3, T4, T5, T6, T7>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(
                ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ReadUnmanagedSized<T1, T2, T3, T4, T5, T6, T7, T8>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7, out T8 v8)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged {
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                                 Unsafe.SizeOf<T8>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(
                ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            v8 = Unsafe.ReadUnaligned<T8>(ref Unsafe.Add(ref src,
                4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1>(out T1 v1) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            #endif
            var size = (uint)Unsafe.SizeOf<T1>();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Buffer[Position]);
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1, T2>(out T1 v1, out T2 v2) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T2)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1, T2, T3>(out T1 v1, out T2 v2, out T3 v3) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T3)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1, T2, T3, T4>(out T1 v1, out T2 v2, out T3 v3, out T4 v4) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T4)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1, T2, T3, T4, T5>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T5)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1, T2, T3, T4, T5, T6>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T6)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1, T2, T3, T4, T5, T6, T7>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T7)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanaged<T1, T2, T3, T4, T5, T6, T7, T8>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7, out T8 v8) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T7)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T8>())
                throw new Exception($"[ForceReadUnmanaged] Type {typeof(T8)} contains references");
            #endif
            var size = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                              Unsafe.SizeOf<T8>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(size))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            v1 = Unsafe.ReadUnaligned<T1>(ref src);
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(ref Unsafe.Add(ref src, Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            v8 = Unsafe.ReadUnaligned<T8>(ref Unsafe.Add(ref src,
                Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()));
            Position += size;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1>(out T1 v1) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            #endif
            var payload = (uint)Unsafe.SizeOf<T1>();
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1, T2>(out T1 v1, out T2 v2) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T2)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1, T2, T3>(out T1 v1, out T2 v2, out T3 v3) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T3)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1, T2, T3, T4>(out T1 v1, out T2 v2, out T3 v3, out T4 v4) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T4)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1, T2, T3, T4, T5>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T5)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1, T2, T3, T4, T5, T6>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T6)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1, T2, T3, T4, T5, T6, T7>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T7)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(
                ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            Position += stored + 4;
        }

        [MethodImpl(AggressiveInlining)]
        public void ForceReadUnmanagedSized<T1, T2, T3, T4, T5, T6, T7, T8>(out T1 v1, out T2 v2, out T3 v3, out T4 v4, out T5 v5, out T6 v6, out T7 v7, out T8 v8) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T1>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T1)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T2>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T2)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T3>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T3)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T4>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T4)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T5>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T5)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T6>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T6)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T7>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T7)} contains references");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T8>())
                throw new Exception($"[ForceReadUnmanagedSized] Type {typeof(T8)} contains references");
            #endif
            var payload = (uint)(Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>() +
                                 Unsafe.SizeOf<T8>());
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (!HasNext(payload + 4))
                throw new Exception("ByteReader, Out of bound");
            #endif
            ref var src = ref Buffer[Position];
            var stored = Unsafe.ReadUnaligned<uint>(ref src);
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (stored != payload)
                throw new Exception($"[ForceReadUnmanagedSized] Size mismatch - stored {stored}, expected {payload}");
            #endif
            v1 = Unsafe.ReadUnaligned<T1>(ref Unsafe.Add(ref src, 4));
            v2 = Unsafe.ReadUnaligned<T2>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>()));
            v3 = Unsafe.ReadUnaligned<T3>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>()));
            v4 = Unsafe.ReadUnaligned<T4>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>()));
            v5 = Unsafe.ReadUnaligned<T5>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>()));
            v6 = Unsafe.ReadUnaligned<T6>(ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>()));
            v7 = Unsafe.ReadUnaligned<T7>(
                ref Unsafe.Add(ref src, 4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>()));
            v8 = Unsafe.ReadUnaligned<T8>(ref Unsafe.Add(ref src,
                4 + Unsafe.SizeOf<T1>() + Unsafe.SizeOf<T2>() + Unsafe.SizeOf<T3>() + Unsafe.SizeOf<T4>() + Unsafe.SizeOf<T5>() + Unsafe.SizeOf<T6>() + Unsafe.SizeOf<T7>()));
            Position += stored + 4;
        }
        #endregion
    }

    /// <summary>
    /// Single owner of one rented array: copying the handle and calling <see cref="Return"/> on both copies
    /// hands the same array to two later renters.
    /// </summary>
    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public struct ArrayPoolHandle<T> {
        private T[] _value;

        public ArrayPoolHandle(T[] value) {
            _value = value;
        }

        [MethodImpl(AggressiveInlining)]
        public void Return() {
            if (_value != null) {
                ArrayPool<T>.Shared.Return(_value, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
                _value = null;
            }
        }
    }
}
