using System;
using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace FFS.Libraries.StaticPack {
    public interface IPackArrayStrategy {
        public void Register();
    }

    public interface IPackArrayStrategy<T> : IPackArrayStrategy {
        public T[] ReadArray(ref BinaryPackReader reader);

        /// <summary> Returns the number of elements read into <paramref name="result"/>, or -1 with <paramref name="result"/> left untouched when the null flag was read. </summary>
        public int ReadArray(ref BinaryPackReader reader, ref T[] result);

        /// <summary>
        /// Returns the number of elements read into <paramref name="result"/> starting at <paramref name="idx"/>,
        /// or -1 when the null flag was read - <paramref name="result"/> is left untouched in that case.
        /// </summary>
        public int ReadArray(ref BinaryPackReader reader, ref T[] result, int idx);

        /// <summary> Throws when multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS, UNITY_WEBGL). </summary>
        public T[,] ReadArray2D(ref BinaryPackReader reader);

        /// <inheritdoc cref="ReadArray2D"/>
        public T[,,] ReadArray3D(ref BinaryPackReader reader);

        public void WriteArray(ref BinaryPackWriter writer, T[] value);

        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count);

        /// <inheritdoc cref="ReadArray2D"/>
        public void WriteArray(ref BinaryPackWriter writer, T[,] value);

        /// <inheritdoc cref="ReadArray2D"/>
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value);
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public readonly struct UnmanagedPackArrayStrategy<T> : IPackArrayStrategy<T> where T : unmanaged {
        [MethodImpl(AggressiveInlining)]
        public T[] ReadArray(ref BinaryPackReader reader) => reader.ReadArrayUnmanaged<T>();

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2D(ref BinaryPackReader reader) => reader.ReadArray2DUnmanaged<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3D(ref BinaryPackReader reader) => reader.ReadArray3DUnmanaged<T>();
        #else
        public T[,] ReadArray2D(ref BinaryPackReader reader) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }

        public T[,,] ReadArray3D(ref BinaryPackReader reader) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public int ReadArray(ref BinaryPackReader reader, ref T[] result) => reader.ReadArrayUnmanaged(ref result);

        [MethodImpl(AggressiveInlining)]
        public int ReadArray(ref BinaryPackReader reader, ref T[] result, int idx) => reader.ReadArrayUnmanaged(ref result, idx);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value) => writer.WriteArrayUnmanaged(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count) => writer.WriteArrayUnmanaged(value, idx, count);

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) => writer.WriteArrayUnmanaged(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) => writer.WriteArrayUnmanaged(value);
        #else
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }

        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void Register() {
            BinaryPack<T?>.Register(static (ref BinaryPackWriter writer, in T? value) => writer.WriteNullable(in value), static (ref BinaryPackReader reader) => reader.ReadNullable<T>());
            BinaryPack<T[]>.Register(static (ref BinaryPackWriter writer, in T[] value) => writer.WriteArrayUnmanaged(value), static (ref BinaryPackReader reader) => reader.ReadArrayUnmanaged<T>());
            #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
            BinaryPack<T[,]>.Register(static (ref BinaryPackWriter writer, in T[,] value) => writer.WriteArrayUnmanaged(value),
                static (ref BinaryPackReader reader) => reader.ReadArray2DUnmanaged<T>());
            BinaryPack<T[,,]>.Register(static (ref BinaryPackWriter writer, in T[,,] value) => writer.WriteArrayUnmanaged(value),
                static (ref BinaryPackReader reader) => reader.ReadArray3DUnmanaged<T>());
            #endif
        }
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public readonly struct StructPackArrayStrategy<T> : IPackArrayStrategy<T> where T : struct {
        [MethodImpl(AggressiveInlining)]
        public T[] ReadArray(ref BinaryPackReader reader) => reader.ReadArray<T>();

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2D(ref BinaryPackReader reader) => reader.ReadArray2D<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3D(ref BinaryPackReader reader) => reader.ReadArray3D<T>();
        #else
        public T[,] ReadArray2D(ref BinaryPackReader reader) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }

        public T[,,] ReadArray3D(ref BinaryPackReader reader) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public int ReadArray(ref BinaryPackReader reader, ref T[] result) => reader.ReadArray(ref result);

        [MethodImpl(AggressiveInlining)]
        public int ReadArray(ref BinaryPackReader reader, ref T[] result, int idx) => reader.ReadArray(ref result, idx);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count) => writer.WriteArray(value, idx, count);

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) => writer.WriteArray(value);
        #else
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }

        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void Register() {
            BinaryPack<T?>.Register(static (ref BinaryPackWriter writer, in T? value) => writer.WriteNullable(in value), static (ref BinaryPackReader reader) => reader.ReadNullable<T>());
            BinaryPack<T[]>.Register(static (ref BinaryPackWriter writer, in T[] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray<T>());
            #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
            BinaryPack<T[,]>.Register(static (ref BinaryPackWriter writer, in T[,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray2D<T>());
            BinaryPack<T[,,]>.Register(static (ref BinaryPackWriter writer, in T[,,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray3D<T>());
            #endif
        }
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public readonly struct ClassPackArrayStrategy<T> : IPackArrayStrategy<T> where T : class {
        [MethodImpl(AggressiveInlining)]
        public T[] ReadArray(ref BinaryPackReader reader) => reader.ReadArray<T>();

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public T[,] ReadArray2D(ref BinaryPackReader reader) => reader.ReadArray2D<T>();

        [MethodImpl(AggressiveInlining)]
        public T[,,] ReadArray3D(ref BinaryPackReader reader) => reader.ReadArray3D<T>();
        #else
        public T[,] ReadArray2D(ref BinaryPackReader reader) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }

        public T[,,] ReadArray3D(ref BinaryPackReader reader) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public int ReadArray(ref BinaryPackReader reader, ref T[] result) => reader.ReadArray(ref result);

        [MethodImpl(AggressiveInlining)]
        public int ReadArray(ref BinaryPackReader reader, ref T[] result, int idx) => reader.ReadArray(ref result, idx);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count) => writer.WriteArray(value, idx, count);

        #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) => writer.WriteArray(value);

        [MethodImpl(AggressiveInlining)]
        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) => writer.WriteArray(value);
        #else
        public void WriteArray(ref BinaryPackWriter writer, T[,] value) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }

        public void WriteArray(ref BinaryPackWriter writer, T[,,] value) {
            throw new NotSupportedException("[StaticPack] multi-dimensional arrays are compiled out (FFS_PACK_DISABLE_MULTI_ARRAYS / UNITY_WEBGL)");
        }
        #endif

        [MethodImpl(AggressiveInlining)]
        public void Register() {
            BinaryPack<T[]>.Register(static (ref BinaryPackWriter writer, in T[] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray<T>());
            #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
            BinaryPack<T[,]>.Register(static (ref BinaryPackWriter writer, in T[,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray2D<T>());
            BinaryPack<T[,,]>.Register(static (ref BinaryPackWriter writer, in T[,,] value) => writer.WriteArray(value), static (ref BinaryPackReader reader) => reader.ReadArray3D<T>());
            #endif
        }
    }
}
