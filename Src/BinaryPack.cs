using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace FFS.Libraries.StaticPack {
    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    [Il2CppEagerStaticClassConstruction]
    #endif
    public static class BinaryPack {
        static BinaryPack() {
            RegisterWithCollections(static (ref BinaryPackWriter writer, in sbyte value) => writer.WriteSbyte(value), static (ref BinaryPackReader reader) => reader.ReadSbyte(), new UnmanagedPackArrayStrategy<sbyte>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in byte value) => writer.WriteByte(value), static (ref BinaryPackReader reader) => reader.ReadByte(), new UnmanagedPackArrayStrategy<byte>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in short value) => writer.WriteShort(value), static (ref BinaryPackReader reader) => reader.ReadShort(), new UnmanagedPackArrayStrategy<short>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in ushort value) => writer.WriteUshort(value), static (ref BinaryPackReader reader) => reader.ReadUshort(), new UnmanagedPackArrayStrategy<ushort>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in int value) => writer.WriteInt(value), static (ref BinaryPackReader reader) => reader.ReadInt(), new UnmanagedPackArrayStrategy<int>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in uint value) => writer.WriteUint(value), static (ref BinaryPackReader reader) => reader.ReadUint(), new UnmanagedPackArrayStrategy<uint>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in long value) => writer.WriteLong(value), static (ref BinaryPackReader reader) => reader.ReadLong(), new UnmanagedPackArrayStrategy<long>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in ulong value) => writer.WriteUlong(value), static (ref BinaryPackReader reader) => reader.ReadUlong(), new UnmanagedPackArrayStrategy<ulong>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in char value) => writer.WriteChar(value), static (ref BinaryPackReader reader) => reader.ReadChar(), new UnmanagedPackArrayStrategy<char>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in float value) => writer.WriteFloat(value), static (ref BinaryPackReader reader) => reader.ReadFloat(), new UnmanagedPackArrayStrategy<float>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in double value) => writer.WriteDouble(value), static (ref BinaryPackReader reader) => reader.ReadDouble(), new UnmanagedPackArrayStrategy<double>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in bool value) => writer.WriteBool(value), static (ref BinaryPackReader reader) => reader.ReadBool(), new UnmanagedPackArrayStrategy<bool>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in DateTime value) => writer.WriteDateTime(value), static (ref BinaryPackReader reader) => reader.ReadDateTime(), new StructPackArrayStrategy<DateTime>());
            RegisterWithCollections(static (ref BinaryPackWriter writer, in Guid value) => writer.WriteGuid(in value), static (ref BinaryPackReader reader) => reader.ReadGuid(), new UnmanagedPackArrayStrategy<Guid>());

            RegisterWithCollections(static (ref BinaryPackWriter writer, in string value) => writer.WriteString16(value), static (ref BinaryPackReader reader) => reader.ReadString16(), new ClassPackArrayStrategy<string>());
        }

        public static void Init() {
            // Static constructor
        }

        [MethodImpl(AggressiveInlining)]
        public static int SizeOf<T>() => Unsafe.SizeOf<T>();

        [MethodImpl(AggressiveInlining)]
        public static void RegisterWithCollections<T, S>(BinaryWriter<T> writer, BinaryReader<T> reader, S strategy = default) where S : struct, IPackArrayStrategy<T> {
            BinaryPack<T>.Register(writer, reader);
            strategy.Register();
            BinaryPack<T[][]>.Register(static (ref BinaryPackWriter writer, in T[][] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray<T[]>());
            BinaryPack<T[][][]>.Register(static (ref BinaryPackWriter writer, in T[][][] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray<T[][]>());
            BinaryPack<List<T>>.Register(static (ref BinaryPackWriter writer, in List<T> value) => writer.WriteList(value), static (ref BinaryPackReader reader) => reader.ReadList<T>());
            BinaryPack<LinkedList<T>>.Register(static (ref BinaryPackWriter writer, in LinkedList<T> value) => writer.WriteLinkedList(value), static (ref BinaryPackReader reader) => reader.ReadLinkedList<T>());
            BinaryPack<Queue<T>>.Register(static (ref BinaryPackWriter writer, in Queue<T> value) => writer.WriteQueue(value), static (ref BinaryPackReader reader) => reader.ReadQueue<T>());
            BinaryPack<Stack<T>>.Register(static (ref BinaryPackWriter writer, in Stack<T> value) => writer.WriteStack(value), static (ref BinaryPackReader reader) => reader.ReadStack<T>());
            BinaryPack<HashSet<T>>.Register(static (ref BinaryPackWriter writer, in HashSet<T> value) => writer.WriteHashSet(value), static (ref BinaryPackReader reader) => reader.ReadHashSet<T>());
        }

        [MethodImpl(AggressiveInlining)]
        public static void Register<T>(BinaryWriter<T> writer, BinaryReader<T> reader) {
            BinaryPack<T>.Register(writer, reader);
        }

        [MethodImpl(AggressiveInlining)]
        public static bool IsRegistered<T>() {
            return BinaryPack<T>.IsRegistered();
        }

        /// <summary> Dispatches on the static type <typeparamref name="T"/>, not on the runtime type of the value. </summary>
        [MethodImpl(AggressiveInlining)]
        public static T Read<T>(this ref BinaryPackReader reader) => BinaryPack<T>.Read(ref reader);

        [MethodImpl(AggressiveInlining)]
        public static T ReadFromBytes<T>(byte[] bytes, bool gzip = false, uint byteSizeHint = 4096) {
            return ReadFromBytes<T>(bytes, 0, (uint)bytes.Length, gzip, byteSizeHint);
        }

        /// <summary>
        /// Reads a value from the <paramref name="count"/> bytes of <paramref name="bytes"/> starting at
        /// <paramref name="position"/>, in both the plain and the <paramref name="gzip"/> path.
        /// <paramref name="byteSizeHint"/> sizes the scratch writer of the gzip path and is unused otherwise.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static T ReadFromBytes<T>(byte[] bytes, uint position, uint count, bool gzip = false, uint byteSizeHint = 4096) {
            if (gzip) {
                var writer = BinaryPackWriter.Create(byteSizeHint);
                try {
                    writer.WriteGzipData(bytes, position, count);
                    var reader = writer.AsReader();
                    return BinaryPack<T>.Read(ref reader);
                }
                finally {
                    writer.Dispose();
                }
            }

            if ((ulong)position + count > (uint)bytes.Length) {
                throw new Exception("[StaticPack] incorrect position or count");
            }

            if (count == 0) {
                throw new Exception("[StaticPack] ReadFromBytes: the payload is empty");
            }

            unsafe {
                fixed (byte* ptr = bytes) {
                    var reader = new BinaryPackReader(ptr, position + count, position);
                    return BinaryPack<T>.Read(ref reader);
                }
            }
        }

        /// <summary>
        /// Reads a value from <paramref name="bytes"/> without copying it: the span is read in place. The payload
        /// is taken as-is, a gzip stream is not detected and not decompressed.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static T ReadFromSpan<T>(ReadOnlySpan<byte> bytes) {
            if (bytes.Length == 0) {
                throw new Exception("[StaticPack] ReadFromSpan: the payload is empty");
            }

            unsafe {
                fixed (byte* ptr = bytes) {
                    var reader = new BinaryPackReader(ptr, (uint)bytes.Length, 0);
                    return BinaryPack<T>.Read(ref reader);
                }
            }
        }

        [MethodImpl(AggressiveInlining)]
        public static T ReadFromFile<T>(string filePath, bool gzip = false, uint byteSizeHint = 4096) {
            var writer = BinaryPackWriter.Create(byteSizeHint);
            try {
                writer.WriteFromFile(filePath, gzip);
                var reader = writer.AsReader();
                return BinaryPack<T>.Read(ref reader);
            }
            finally {
                writer.Dispose();
            }
        }

        /// <summary> Dispatches on the static type <typeparamref name="T"/>, not on the runtime type of <paramref name="value"/>. </summary>
        [MethodImpl(AggressiveInlining)]
        public static void Write<T>(this ref BinaryPackWriter writer, in T value) => BinaryPack<T>.Write(ref writer, in value);

        [MethodImpl(AggressiveInlining)]
        public static byte[] WriteToBytes<T>(T value, bool gzip = false, uint byteSizeHint = 4096) {
            var writer = BinaryPackWriter.Create(byteSizeHint);
            try {
                BinaryPack<T>.Write(ref writer, in value);
                return writer.CopyToBytes(gzip);
            }
            finally {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Writes into <paramref name="result"/>, growing it only when it is too small, and returns the number of
        /// bytes written: the tail beyond that length keeps whatever the array held before.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static int WriteToBytes<T>(T value, ref byte[] result, bool gzip = false, uint byteSizeHint = 4096) {
            var writer = BinaryPackWriter.Create(byteSizeHint);
            try {
                BinaryPack<T>.Write(ref writer, in value);
                return writer.CopyToBytes(ref result, gzip);
            }
            finally {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Writes straight into <paramref name="destination"/> and returns the number of bytes written. Nothing is
        /// allocated: <paramref name="destination"/> has to be large enough, otherwise the write throws.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public static int WriteToSpan<T>(T value, Span<byte> destination) {
            unsafe {
                fixed (byte* ptr = destination) {
                    var writer = BinaryPackWriter.Create(ptr, (uint)destination.Length);
                    BinaryPack<T>.Write(ref writer, in value);
                    return (int)writer.Position;
                }
            }
        }

        [MethodImpl(AggressiveInlining)]
        public static void WriteToFile<T>(T value, string filePath, bool gzip = false, bool flushToDisk = false, uint byteSizeHint = 4096) {
            var writer = BinaryPackWriter.Create(byteSizeHint);
            try {
                BinaryPack<T>.Write(ref writer, in value);
                writer.FlushToFile(filePath, gzip, flushToDisk);
            }
            finally {
                writer.Dispose();
            }
        }
    }

    public delegate T BinaryReader<T>(ref BinaryPackReader reader);

    public delegate void BinaryWriter<T>(ref BinaryPackWriter writer, in T value);

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    [Il2CppEagerStaticClassConstruction]
    #endif
    internal static class BinaryPack<T> {
        private static BinaryReader<T> _reader;
        private static BinaryWriter<T> _writer;

        static BinaryPack() {
            RuntimeHelpers.RunClassConstructor(typeof(BinaryPack).TypeHandle);
        }

        [MethodImpl(AggressiveInlining)]
        internal static T Read(ref BinaryPackReader reader) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (_reader == null) {
                throw new Exception($"Reader for type {typeof(T)} not defined");
            }
            #endif
            return _reader(ref reader);
        }

        [MethodImpl(AggressiveInlining)]
        internal static void Write(ref BinaryPackWriter writer, in T value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (_writer == null) {
                throw new Exception($"Writer for type {typeof(T)} not defined");
            }
            #endif
            _writer(ref writer, in value);
        }

        [MethodImpl(AggressiveInlining)]
        internal static bool IsRegistered() {
            return _reader != null && _writer != null;
        }

        [MethodImpl(AggressiveInlining)]
        internal static void Register(BinaryWriter<T> writer, BinaryReader<T> reader) {
            if (reader == null) {
                throw new Exception($"Reader for type {typeof(T)} is null");
            }
            if (writer == null) {
                throw new Exception($"Writer for type {typeof(T)} is null");
            }
            _reader = reader;
            _writer = writer;
        }
    }
}

#if ENABLE_IL2CPP
namespace Unity.IL2CPP.CompilerServices {
    using System;

    internal enum Option {
        NullChecks = 1,
        ArrayBoundsChecks = 2,
        DivideByZeroChecks = 3
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true)]
    internal class Il2CppSetOptionAttribute : Attribute {
        public Option Option { get; }
        public object Value { get; }

        public Il2CppSetOptionAttribute(Option option, object value) {
            Option = option;
            Value = value;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    internal class Il2CppEagerStaticClassConstructionAttribute : Attribute { }
}
#endif
