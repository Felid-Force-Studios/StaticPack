using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    public static class BinaryPackContext {
        static BinaryPackContext() {
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadSByte(), new UnmanagedPackArrayStrategy<sbyte>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadByte(), new UnmanagedPackArrayStrategy<byte>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadShort(), new UnmanagedPackArrayStrategy<short>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadUshort(), new UnmanagedPackArrayStrategy<ushort>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadInt(), new UnmanagedPackArrayStrategy<int>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadUint(), new UnmanagedPackArrayStrategy<uint>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadLong(), new UnmanagedPackArrayStrategy<long>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadUlong(), new UnmanagedPackArrayStrategy<ulong>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadChar(), new UnmanagedPackArrayStrategy<char>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadFloat(), new UnmanagedPackArrayStrategy<float>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadDouble(), new UnmanagedPackArrayStrategy<double>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadBool(), new UnmanagedPackArrayStrategy<bool>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadDateTime(), new UnmanagedPackArrayStrategy<DateTime>());
            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadGuid(), new UnmanagedPackArrayStrategy<Guid>());

            RegisterAllReaders(static (ref BinaryPackReader reader) => reader.ReadString16(), new ClassPackArrayStrategy<string>());

            RegisterAllWriters(static (ref BinaryPackWriter writer, in sbyte value) => writer.WriteSbyte(value), new UnmanagedPackArrayStrategy<sbyte>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in byte value) => writer.WriteByte(value), new UnmanagedPackArrayStrategy<byte>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in short value) => writer.WriteShort(value), new UnmanagedPackArrayStrategy<short>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in ushort value) => writer.WriteUshort(value), new UnmanagedPackArrayStrategy<ushort>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in int value) => writer.WriteInt(value), new UnmanagedPackArrayStrategy<int>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in uint value) => writer.WriteUint(value), new UnmanagedPackArrayStrategy<uint>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in long value) => writer.WriteLong(value), new UnmanagedPackArrayStrategy<long>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in ulong value) => writer.WriteUlong(value), new UnmanagedPackArrayStrategy<ulong>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in char value) => writer.WriteChar(value), new UnmanagedPackArrayStrategy<char>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in float value) => writer.WriteFloat(value), new UnmanagedPackArrayStrategy<float>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in double value) => writer.WriteDouble(value), new UnmanagedPackArrayStrategy<double>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in bool value) => writer.WriteBool(value), new UnmanagedPackArrayStrategy<bool>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in DateTime value) => writer.WriteDateTime(value), new UnmanagedPackArrayStrategy<DateTime>());
            RegisterAllWriters(static (ref BinaryPackWriter writer, in Guid value) => writer.WriteGuid(in value), new UnmanagedPackArrayStrategy<Guid>());

            RegisterAllWriters(static (ref BinaryPackWriter writer, in string value) => writer.WriteString16(value), new ClassPackArrayStrategy<string>());
        }

        public static void Init() {
            // Static constructor
        }
        
        [MethodImpl(AggressiveInlining)]
        public static void RegisterAllReaders<T, S>(BinaryReader<T> reader, S strategy = default) where S : IPackArrayStrategy<T> {
            BinaryPackContext<T>.RegisterReader(reader);
            strategy.RegisterReader();
            BinaryPackContext<T[][]>.RegisterReader(static (ref BinaryPackReader reader) => reader.ReadArray<T[]>());
            BinaryPackContext<T[][][]>.RegisterReader(static (ref BinaryPackReader reader) => reader.ReadArray<T[][]>());
            BinaryPackContext<List<T>>.RegisterReader(static (ref BinaryPackReader reader) => reader.ReadList<T>());
            BinaryPackContext<LinkedList<T>>.RegisterReader(static (ref BinaryPackReader reader) => reader.ReadLinkedList<T>());
            BinaryPackContext<Queue<T>>.RegisterReader(static (ref BinaryPackReader reader) => reader.ReadQueue<T>());
            BinaryPackContext<Stack<T>>.RegisterReader(static (ref BinaryPackReader reader) => reader.ReadStack<T>());
            BinaryPackContext<HashSet<T>>.RegisterReader(static (ref BinaryPackReader reader) => reader.ReadHashSet<T>());
        }

        [MethodImpl(AggressiveInlining)]
        public static void RegisterAllWriters<T, S>(BinaryWriter<T> writer, S strategy = default) where S : IPackArrayStrategy<T> {
            BinaryPackContext<T>.RegisterWriter(writer);
            strategy.RegisterWriters();
            BinaryPackContext<T[][]>.RegisterWriter(static (ref BinaryPackWriter writer, in T[][] value) => writer.WriteArray(value));
            BinaryPackContext<T[][][]>.RegisterWriter(static (ref BinaryPackWriter writer, in T[][][] value) => writer.WriteArray(value)); 
            BinaryPackContext<List<T>>.RegisterWriter(static (ref BinaryPackWriter writer, in List<T> value) => writer.WriteList(value));
            BinaryPackContext<LinkedList<T>>.RegisterWriter(static (ref BinaryPackWriter writer, in LinkedList<T> value) => writer.WriteLinkedList(value));
            BinaryPackContext<Queue<T>>.RegisterWriter(static (ref BinaryPackWriter writer, in Queue<T> value) => writer.WriteQueue(value));
            BinaryPackContext<Stack<T>>.RegisterWriter(static (ref BinaryPackWriter writer, in Stack<T> value) => writer.WriteStack(value));
            BinaryPackContext<HashSet<T>>.RegisterWriter(static (ref BinaryPackWriter writer, in HashSet<T> value) => writer.WriteHashSet(value));
        }

        [MethodImpl(AggressiveInlining)]
        public static T Read<T>(this ref BinaryPackReader reader) => BinaryPackContext<T>.Read(ref reader);

        [MethodImpl(AggressiveInlining)]
        public static void Write<T>(this ref BinaryPackWriter writer, in T value) => BinaryPackContext<T>.Write(ref writer, in value);
        
        [MethodImpl(AggressiveInlining)]
        public static void Write<T>(this ref BinaryPackWriter writer, T value) => BinaryPackContext<T>.Write(ref writer, in value);
    }

    public delegate T BinaryReader<T>(ref BinaryPackReader reader);

    public delegate void BinaryWriter<T>(ref BinaryPackWriter writer, in T value);

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    [Il2CppEagerStaticClassConstruction]
    #endif
    public static class BinaryPackContext<T> {
        private static BinaryReader<T> _reader;
        private static BinaryWriter<T> _writer;

        [MethodImpl(AggressiveInlining)]
        public static T Read(ref BinaryPackReader reader) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (_reader == null) {
                throw new Exception($"Reader for type {typeof(T)} not defined");
            }
            #endif
            return _reader(ref reader);
        }

        [MethodImpl(AggressiveInlining)]
        public static void Write(ref BinaryPackWriter writer, in T value) {
            #if DEBUG || FFS_PACK_ENABLE_DEBUG
            if (_writer == null) {
                throw new Exception($"Writer for type {typeof(T)} not defined");
            }
            #endif
            _writer(ref writer, in value);
        }

        [MethodImpl(AggressiveInlining)]
        public static bool HasWriter() {
            return _writer != null;
        }

        [MethodImpl(AggressiveInlining)]
        public static bool HasReader() {
            return _reader != null;
        }

        [MethodImpl(AggressiveInlining)]
        public static void RegisterReader(BinaryReader<T> reader) {
            _reader = reader ?? throw new Exception($"Reader for type {typeof(T)} is null");
        }

        [MethodImpl(AggressiveInlining)]
        public static void RegisterWriter(BinaryWriter<T> writer) {
            _writer = writer ?? throw new Exception($"Writer for type {typeof(T)} is null");
        }
    }
    
    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    [StructLayout(LayoutKind.Explicit)]
    internal struct Union4 {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint Uint;
        [FieldOffset(0)] public byte Byte0;
        [FieldOffset(1)] public byte Byte1;
        [FieldOffset(2)] public byte Byte2;
        [FieldOffset(3)] public byte Byte3;
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    [StructLayout(LayoutKind.Explicit)]
    internal struct Union8 {
        [FieldOffset(0)] public double Double;
        [FieldOffset(0)] public ulong Ulong;
        [FieldOffset(0)] public byte Byte0;
        [FieldOffset(1)] public byte Byte1;
        [FieldOffset(2)] public byte Byte2;
        [FieldOffset(3)] public byte Byte3;
        [FieldOffset(4)] public byte Byte4;
        [FieldOffset(5)] public byte Byte5;
        [FieldOffset(6)] public byte Byte6;
        [FieldOffset(7)] public byte Byte7;
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