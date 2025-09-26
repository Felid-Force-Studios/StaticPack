﻿using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace FFS.Libraries.StaticPack {
    public interface IPackArrayStrategy {
        public void Register();
        public bool IsUnmanaged();
    }

    public interface IPackArrayStrategy<T> : IPackArrayStrategy {
        public T[] ReadArray(ref BinaryPackReader reader);
        public void ReadArray(ref BinaryPackReader reader, ref T[] result);
        public T[,] ReadArray2D(ref BinaryPackReader reader);
        public T[,,] ReadArray3D(ref BinaryPackReader reader);
        public void WriteArray(ref BinaryPackWriter writer, T[] value);
        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count);
        public void WriteArray(ref BinaryPackWriter writer, T[,] value);
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value);
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public readonly struct UnmanagedPackArrayStrategy<T> : IPackArrayStrategy<T> where T : unmanaged {
        [MethodImpl(AggressiveInlining)]
        public T[] ReadArray(ref BinaryPackReader reader) => reader.ReadArrayUnmanaged<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2D(ref BinaryPackReader reader) => reader.ReadArray2DUnmanaged<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3D(ref BinaryPackReader reader) => reader.ReadArray3DUnmanaged<T>();

        [MethodImpl(AggressiveInlining)]
        public void ReadArray(ref BinaryPackReader reader, ref T[] result) => reader.ReadArrayUnmanaged(ref result);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value) => writer.WriteArrayUnmanaged(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count) => writer.WriteArrayUnmanaged(value, idx, count);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) => writer.WriteArrayUnmanaged(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) => writer.WriteArrayUnmanaged(value);

        [MethodImpl(AggressiveInlining)]
        public void Register() {
            BinaryPack<T?>.Register(static (ref BinaryPackWriter writer, in T? value) => writer.WriteNullable(in value), static (ref BinaryPackReader reader) => reader.ReadNullable<T>());
            BinaryPack<T[]>.Register(static (ref BinaryPackWriter writer, in T[] value) => writer.WriteArrayUnmanaged(value), static (ref BinaryPackReader reader) => reader.ReadArrayUnmanaged<T>());
            BinaryPack<T[,]>.Register(static (ref BinaryPackWriter writer, in T[,] value) => writer.WriteArrayUnmanaged(value), static (ref BinaryPackReader reader) => reader.ReadArray2DUnmanaged<T>());
            BinaryPack<T[,,]>.Register(static (ref BinaryPackWriter writer, in T[,,] value) => writer.WriteArrayUnmanaged(value), static (ref BinaryPackReader reader) => reader.ReadArray3DUnmanaged<T>());
        }

        [MethodImpl(AggressiveInlining)]
        public bool IsUnmanaged() => true;
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public readonly struct StructPackArrayStrategy<T> : IPackArrayStrategy<T> where T : struct {
        [MethodImpl(AggressiveInlining)]
        public T[] ReadArray(ref BinaryPackReader reader) => reader.ReadArray<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2D(ref BinaryPackReader reader) => reader.ReadArray2D<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3D(ref BinaryPackReader reader) => reader.ReadArray3D<T>();

        [MethodImpl(AggressiveInlining)]
        public void ReadArray(ref BinaryPackReader reader, ref T[] result) => reader.ReadArray(ref result);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count) => writer.WriteArray(value, idx, count);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void Register() {
            BinaryPack<T?>.Register(static (ref BinaryPackWriter writer, in T? value) => writer.WriteNullable(in value), static (ref BinaryPackReader reader) => reader.ReadNullable<T>());
            BinaryPack<T[]>.Register(static (ref BinaryPackWriter writer, in T[] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray<T>());
            BinaryPack<T[,]>.Register(static (ref BinaryPackWriter writer, in T[,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray2D<T>());
            BinaryPack<T[,,]>.Register(static (ref BinaryPackWriter writer, in T[,,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray3D<T>());
        }

        [MethodImpl(AggressiveInlining)]
        public bool IsUnmanaged() => false;
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public readonly struct ClassPackArrayStrategy<T> : IPackArrayStrategy<T> where T : class {
        [MethodImpl(AggressiveInlining)]
        public T[] ReadArray(ref BinaryPackReader reader) => reader.ReadArray<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2D(ref BinaryPackReader reader) => reader.ReadArray2D<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3D(ref BinaryPackReader reader) => reader.ReadArray3D<T>();

        [MethodImpl(AggressiveInlining)]
        public void ReadArray(ref BinaryPackReader reader, ref T[] result) => reader.ReadArray(ref result);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count) => writer.WriteArray(value, idx, count);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void Register() {
            BinaryPack<T[]>.Register(static (ref BinaryPackWriter writer, in T[] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray<T>());
            BinaryPack<T[,]>.Register(static (ref BinaryPackWriter writer, in T[,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray2D<T>());
            BinaryPack<T[,,]>.Register(static (ref BinaryPackWriter writer, in T[,,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray3D<T>());
        }

        [MethodImpl(AggressiveInlining)]
        public bool IsUnmanaged() => false;
    }
}