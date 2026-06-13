<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-2.0.0-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticPack/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticPack-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
  <br><br>
  <a href="https://github.com/Felid-Force-Studios/StaticPack/blob/master/MIGRATION_2.0.md"><img src="https://img.shields.io/badge/Migration_guide-2.0.0-red?style=for-the-badge" alt="Migration guide"></a>
</p>

# Static Pack - C# Simple binary serialization library
- Lightweight
- Performance
- One dependency: `System.Runtime.CompilerServices.Unsafe` (shipped with the Unity package, a NuGet dependency for netstandard2.1, part of the BCL on .NET 6+)
- No reflections
- No codegen
- No scheme
- Batch primitive operations
- Span / Memory / ReadOnlySequence support
- Compatible with Unity and other C# engines

#### Limitations and Features:
> - Polymorphic types require custom implementation
> - Cyclic references require custom implementation

## Table of Contents
* [Contacts](#contacts)
* [Support the project](#support-the-project)
* [Requirements](#requirements)
* [Installation](#installation)
* [Concept](#concept)
* [Wire format](#wire-format)
* [Memory model](#memory-model)
* [Quick start](#quick-start)
* [API](#api)
  * [BinaryPackWriter](#binarypackwriter)
  * [BinaryPackReader](#binarypackreader)
  * [BinaryPack](#binarypack)
  * [Array strategies](#array-serialization-strategies)
* [Custom types](#registration-of-custom-types)
* [License](#license)

# Contacts
* [felid.force.studios@gmail.com](mailto:felid.force.studios@gmail.com)
* [Telegram](https://t.me/felid_force_studios)

# Support the project
If you like Static Pack and it helps your project, you can support its development:

<a href="https://www.buymeacoffee.com/felid.force.studios" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="60"></a>

# Requirements
* **Unity** 2022.3 or newer, API Compatibility Level .NET Standard 2.1 or .NET Framework.
  `com.unity.burst` is optional. Without it everything works, but a writer can only grow its buffer from
  managed code. With it (1.8.0 or newer) the built-in allocators obtain their `Realloc`/`Free` through
  `BurstCompiler.CompileFunctionPointer`, so those calls lead to natively compiled code and a writer can
  grow and release its buffer from inside a Burst job. `PackMemory.IsBurstCompatible(in writer)` reports
  which of the two a given writer is in.
* **.NET** 6.0 or newer, or netstandard2.1.
* `System.Runtime.CompilerServices.Unsafe` — shipped inside the Unity package, pulled from NuGet for
  netstandard2.1, already part of the BCL on .NET 6+.

# Installation
* ### As source code
  From the release page or as an archive from the branch. In the `master` branch there is a stable tested version
* ### Installation for Unity
  git module `https://github.com/Felid-Force-Studios/StaticPack.git` in Unity PackageManager
  or adding it to `Packages/manifest.json` `"com.felid-force-studios.static-pack": "https://github.com/Felid-Force-Studios/StaticPack.git"`
* ### NuGet
  ```
  dotnet add package FFS.StaticPack
  ```
  For debug build with assertions:
  ```
  dotnet add package FFS.StaticPack.Debug
  ```
  Packages: [FFS.StaticPack](https://www.nuget.org/packages/FFS.StaticPack/) · [FFS.StaticPack.Debug](https://www.nuget.org/packages/FFS.StaticPack.Debug/)

# Concept
The library provides high-performance tools for binary serialization with support for:
> - Primitive types and arrays
> - Multidimensional arrays
> - Collections (lists, queues, dictionaries, etc.)
> - Custom types
> - Batch primitive writes/reads (2, 3, 4 values at once)
> - Span, Memory, ReadOnlySequence for zero-copy operations
> - Direct file reading/writing
> - Data compression
> - Native (unmanaged) buffers: `BinaryPackWriter`/`BinaryPackReader` are fully unmanaged structs usable inside Unity Burst jobs

# Wire format
A flat byte stream without a header or a schema, written and read in **host byte order** — little-endian on
every platform the library targets, so a stream produced on a big-endian host does not read back on a
little-endian one.

> - Integers and floating-point values are the raw in-memory bytes: IEEE-754 bit patterns, with NaN payloads, signed zero and infinities preserved
> - `Guid` is its in-memory layout, which equals `Guid.ToByteArray()` on little-endian
> - `DateTime` is the `long` returned by `DateTime.ToBinary()`, so `Kind` survives the round trip
> - Strings are UTF-8 with a byte-length prefix: 1 byte for `String8`, 2 for `String16`, 4 for `String32`
> - Reference values, nullables and collections are preceded by a single-byte null flag
> - Arrays and collections store the element count as `int`, then the payload byte size as `uint`, then the elements
> - `VarInt` is 1..5 bytes and `VarShort` 1..2 bytes, seven payload bits per byte, low group first

# Memory model

The buffer is raw native memory (`byte* Buffer`), never a managed array. A writer works in one of three modes:

1. **Owned** — the library allocates native memory (`UnsafeUtility.Malloc` in Unity, `NativeMemory`/`AllocHGlobal` elsewhere), grows it on demand and frees it in `Dispose`.
2. **Allocator-owned** — a custom `PackAllocator` (a pair of `delegate*` function pointers) owns the buffer end-to-end: the initial allocation, growth and release all go through it. Both pointers are required; pass an empty `Free` method when there is nothing to release on `Dispose`.
3. **User-provided** — you pass a pointer and a capacity. The struct never frees it and throws on overflow.

`IsCreated == true` while the struct holds a buffer: it turns false for `default`, after `Dispose`, and after the buffer has been handed over by `AsReaderOwned`/`AsWriterCompact`. `Owned == true` while it holds a buffer of its own (modes 1 and 2): `Dispose` releases that buffer and writes can grow it.

```csharp
// owned native buffer (must be disposed):
using var writer = BinaryPackWriter.Create(1024);

// user-provided memory (never freed by the writer, throws on overflow):
byte* ptr = ...;
var writer = BinaryPackWriter.Create(ptr, capacity: 1024);

// custom allocator owns the buffer end-to-end:
// Realloc(state, oldPtr, oldCapacity, usedBytes, newCapacity): oldCapacity is the old block size,
// usedBytes is how much of it must survive — copy min(usedBytes, newCapacity). The initial buffer
// arrives as Realloc(state, null, 0, 0, capacity); Free is called on Dispose.
var alloc = PackAllocator.FromDelegates(MyRealloc, MyFree); // managed methods marshalled to Cdecl thunks
var writer = BinaryPackWriter.Create(1024, alloc);

// Unity: choose the allocator / wrap a NativeArray
using var writer = BinaryPackWriter.Create(1024, Allocator.TempJob);
var writer = BinaryPackWriter.Create(nativeArray); // user-memory mode
var reader = BinaryPackReader.Create(nativeArray);
```

`using var` is fine as long as the writer is only ever used through its instance methods (`writer.WriteInt(...)`, etc.). It stops compiling the moment you call the ref-extension `Write<T>`/`Read<T>` (CS1657, a `using` local cannot be passed by `ref`) — see the explicit `try`/`finally` in Quick start below.

`PackAllocator` holds two `delegate* unmanaged[Cdecl]` function pointers (`Realloc`, `Free`). Three ways to produce them:
- `PackAllocator.FromDelegates(realloc, free)` — marshals managed methods into Cdecl thunks (rooted for the process lifetime). Callable from regular C# and IL2CPP, but **not** Burst-optimized — for long-lived allocators. On IL2CPP the target methods must be `static` and carry `[AOT.MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]` / `FreeDelegate`.
- `[UnmanagedCallersOnly(CallConvs = new[]{ typeof(CallConvCdecl) })]` static methods + `&Method` (.NET 6+) — raw native pointers, no marshalling, passed to the `new PackAllocator(realloc, free, state)` constructor.
- `(delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, byte*>) BurstCompiler.CompileFunctionPointer<PackAllocator.ReallocDelegate>(MyRealloc).Value` (Unity + Burst) — Burst-compiled native code, callable from inside Burst jobs. `MyRealloc` must be `static` and carry `[BurstCompile]` plus `[AOT.MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]`; the cast is required because `.Value` is an `IntPtr`.

Both structs contain only unmanaged fields, so they are valid fields of a Burst job; primitives, var-ints, unmanaged spans and buffer growth are Burst-safe. Strings, collections, gzip and file APIs remain managed-only conveniences.

**Built-in allocators and Burst.** When the `com.unity.burst` package is present, the built-in backends (`PackMemory.Default(...)`, `PackArenaAllocator`) automatically produce their function pointers via `BurstCompiler.CompileFunctionPointer`, so a writer backed by them grows and disposes **fully inside a Burst job**. Call `PackMemory.WarmUp()` and `PackArenaAllocator.WarmUp()` (both Unity only, and both already invoked automatically before the first scene loads) to front-load that compilation — it also self-warms on first managed-code use (Edit mode, EditMode tests and editor tooling included), so this is an optimization, not a prerequisite, except for the very first call happening from inside Burst-compiled code itself, which cannot compile a function pointer and must be warmed up from managed code beforehand. `PackMemory.IsBurstCompatible(in writer)` (Unity only) reports whether a given writer's buffer can grow from Burst code, for both built-in backends. Without Burst (or on plain .NET) the built-ins use `[UnmanagedCallersOnly]`/marshalled pointers — fine for managed-side growth.

## PackArenaAllocator — frame-scoped arena

A built-in `PackAllocator` implementation: every allocation is a pointer bump, disposing an individual writer frees **nothing** (though it does invalidate that writer, so do not use the struct afterwards), and everything is reclaimed at once by `Reset()` — typically at the end of a frame.

Overflow behavior is selected by `PackArenaMode`:

- `SpillToDefault` (default) — capacity stays fixed; overflow allocations fall back to the default native allocator one by one. Spill blocks are linked to the arena and released by `Reset()`/`Dispose()`, so nothing leaks.
- `AutoResize` — the arena grows: the current chunk is retired (its contents stay valid until `Reset()`) and bumping continues in a new chunk of at least double capacity, up to the `uint` capacity ceiling. `Reset()` keeps the largest chunk, so the arena converges to the real per-frame demand and stops allocating entirely after the first frames.

In both modes every pointer handed out stays valid until `Reset()`/`Dispose()` — nothing moves or is freed mid-frame.

```csharp
// the arena lives across frames — no `using`, dispose it at the end of its lifecycle
var arena = PackArenaAllocator.Create(64 * 1024, PackArenaMode.AutoResize);

// each frame:
var writer = arena.CreateWriter(1024); // initial buffer and all growth come from the arena
writer.WriteInt(42);
Send(writer.AsReader());
// writer.Dispose() is optional and does nothing

arena.Reset(); // end of frame: every pointer handed out becomes invalid, memory is reusable

// diagnostics: arena.Capacity, arena.Used, arena.Overflowed

// end of the arena's lifecycle (e.g. system/scene shutdown):
arena.Dispose();
```

The arena is also a general-purpose frame allocator for unmanaged data, not just writers:

```csharp
ref var state = ref arena.Alloc<MyStruct>();          // single struct, default-initialized
Span<float> temp = arena.AllocSpan<float>(1024);      // span, zeroed (clear: false to skip)
MyStruct* raw = arena.AllocPtr<MyStruct>(16);         // raw pointer to 16 elements, zeroed
byte* bytes = arena.Alloc(256);                       // raw bytes
// all of it lives until arena.Reset() — no individual frees
```

The arena is not thread-safe — use one per thread. After `Reset()` all previously created writers/readers over arena memory are invalid.

**Lifetime rules:**
- Copies of a struct share the same pointer: resizing one copy invalidates the others; pass writers by `ref` and dispose exactly once.
- `default(BinaryPackWriter)` is not usable (null buffer, zero capacity — the first write throws).
- In Unity, do not let a writer created with `Allocator.Temp` outlive the frame/job.

**Memory checks** (all builds):
- Reading validates every length that comes from the data itself — element counts, byte sizes, array dimensions, string and blob lengths — against the element size and the bytes left in the buffer, and throws before a corrupt length reaches a raw copy. `ReadSpanUnmanaged` additionally checks that the destination holds the stored count.
- `FFS_PACK_DISABLE_MEMORY_CHECK` compiles these checks out for builds that only ever read data they produced themselves and need the last few instructions per read. `DEBUG` / `FFS_PACK_ENABLE_DEBUG` builds keep the checks regardless of that define.
- `FFS_PACK_DISABLE_MULTI_ARRAYS` compiles the `T[,]` / `T[,,]` API out, and `UNITY_WEBGL` does the same; the array strategies keep those members and throw `NotSupportedException` instead.
- Decompression has no size limit of its own: a small gzip input can expand until the buffer fills memory. Pass `maxDecompressedSize` to `WriteGzipData` / `WriteFromFile` to bound it.

**Debug checks** (`DEBUG` / `FFS_PACK_ENABLE_DEBUG` builds, e.g. the `FFS.StaticPack.Debug` assembly):
- Every owned allocation is tracked with its stack trace. `BinaryPackLeakTracker.Count` / `Report()` show live allocations. Leaked ones are additionally reported on a Unity/Mono domain reload and on process exit (`Debug.LogError` in Unity, `Console.Error` elsewhere). Capturing the stack trace costs roughly 200 microseconds and a few kilobytes per allocation in the Editor — set `BinaryPackLeakTracker.CaptureStackTraces = false` while profiling and allocations stay tracked without it. `BinaryPackLeakTracker.Clear()` forgets every allocation made so far, and disposing those buffers afterwards is accepted rather than reported as a double Dispose. The type stays callable in builds without tracking, where it reports an empty state, so references to it need no `#if` guard. In Unity, allocations additionally go through `UnsafeUtility.MallocTracked`, feeding Unity's Native Leak Detection.
- Double `Dispose` through two copies of the same struct — or `Dispose` of a stale copy after another copy resized the buffer — throws instead of corrupting memory.
- `FFS.StaticPack` and `FFS.StaticPack.Debug` do not share a struct layout: the debug builds add a tracking field to the writer and the reader. Do not pass those structs between assemblies compiled against different ones.

# Quick start
```csharp
using FFS.Libraries.StaticPack;

BinaryPack.Init();

// owned native buffer, freed by Dispose
var writer = BinaryPackWriter.Create(1024);
try {
    // Base types:
    writer.WriteInt(123);
    writer.WriteString16("Hello world");
    writer.WriteArray(new short[] { 1, 2, 3 });
    writer.WriteDictionary(new Dictionary<string, DateTime> { { "today", DateTime.Today }, { "tomorrow", DateTime.Today.AddDays(1) } });

    // Batch writes (single EnsureSize call):
    writer.WriteFloat(1.0f, 2.0f, 3.0f);  // x, y, z
    writer.WriteInt(10, 20, 30, 40);       // 4 values at once

    // Span writes:
    Span<float> positions = stackalloc float[] { 1f, 2f, 3f };
    writer.WriteUnmanaged<float>(positions);

    // Custom types (registered once, written last):
    BinaryPack.RegisterWithCollections<Person, StructPackArrayStrategy<Person>>(Person.Write, Person.Read);
    writer.Write(new Person { Name = "Alice", Age = 20, BirthDate = DateTime.Now });

    // Take the reader only after every write: it snapshots writer.Position as its Size, and a write
    // made afterward that grows the buffer (Resize) would leave it pointing at freed memory.
    var reader = writer.AsReader(); // new BinaryPackReader(writer.Buffer, writer.Position, 0)

    var readInt = reader.ReadInt();                           // 123
    var readString = reader.ReadString16();                   // "Hello world"
    var readArray = reader.ReadArray<short>();                // [ 1, 2, 3 ]
    var readDict = reader.ReadDictionary<string, DateTime>(); // { "today", ... }, { "tomorrow", ... }

    // Batch reads:
    reader.ReadFloat(out var x, out var y, out var z);
    reader.ReadInt(out var a, out var b, out var c, out var d);

    // Span reads:
    Span<float> dest = stackalloc float[3];
    reader.ReadUnmanaged(dest);

    var person = reader.Read<Person>();
} finally {
    writer.Dispose();
}

public struct Person {
    public string Name;
    public int Age;
    public DateTime BirthDate;
    
    public static void Write(ref BinaryPackWriter writer, in Person value) {
        writer.WriteString16(value.Name);
        writer.WriteInt(value.Age);
        writer.WriteDateTime(value.BirthDate);
    }
    
    public static Person Read(ref BinaryPackReader reader) {
        return new Person {
            Name = reader.ReadString16(),
            Age = reader.ReadInt(),
            BirthDate = reader.ReadDateTime()
        };
    }
}
```


## API

### BinaryPackWriter
Structure for writing binary data

#### Buffer management
```csharp
static BinaryPackWriter Create(uint capacity = 1024);                                 // owned native memory
static BinaryPackWriter Create(byte* buffer, uint capacity, uint position = 0);       // user memory
static BinaryPackWriter Create(uint capacity, PackAllocator allocator);  // allocator owns the buffer end-to-end
// Unity only:
static BinaryPackWriter Create(uint capacity, Allocator allocator);
static BinaryPackWriter Create(NativeArray<byte> buffer, uint position = 0);

void EnsureSize(uint size);
byte[] CopyToBytes(bool gzip = false);
int CopyToBytes(ref byte[] result, bool gzip = false);
ReadOnlySpan<byte> AsSpan();                        // the written bytes, no copy
ReadOnlySpan<byte> AsSpan(uint offset, uint count);
int CopyTo(Span<byte> destination);
uint MakePoint(uint size);
BinaryPackReader AsReader();      // non-owning view, the writer must still be disposed
BinaryPackReader AsReaderOwned(); // transfers buffer ownership to the reader and invalidates the writer
bool IsCreated;  // false for default, after Dispose, and after AsReaderOwned
bool Owned;      // holds a buffer of its own
void Skip(uint bytesCount);
void Dispose(); // frees the buffer in owned mode / via PackAllocator.Free
```

#### Primitives
```csharp
void WriteByte(byte value);
void WriteSbyte(sbyte value);
void WriteBool(bool value);
void WriteShort(short value);
void WriteUshort(ushort value);
void WriteChar(char value);
void WriteInt(int value);
void WriteUint(uint value);
void WriteLong(long value);
void WriteUlong(ulong value);
void WriteFloat(float value);
void WriteDouble(double value);
void WriteVarInt(int value);   // 1-5 bytes; negative values always take 5 bytes and round-trip correctly
void WriteVarShort(short value); // 1-2 bytes, non-negative only - throws on a negative value (too few bits to round-trip)
// WriteXAt(uint offset, X value) patches an already written value in place, for every type above
```

#### Batch primitives
```csharp
// Available for: byte, short, ushort, int, uint, float, long, ulong, double
// Single EnsureSize call for all values
void WriteInt(int v0, int v1);
void WriteInt(int v0, int v1, int v2);
void WriteInt(int v0, int v1, int v2, int v3);
void WriteFloat(float v0, float v1);
void WriteFloat(float v0, float v1, float v2);
void WriteFloat(float v0, float v1, float v2, float v3);
// ... same pattern for all types
```

#### Special types
```csharp
void WriteNullable<T>(in T? value) where T : struct;
void WriteDateTime(DateTime value); // via DateTime.ToBinary()/FromBinary(): Kind and ticks round-trip exactly, Local converts to the reading machine's zone
void WriteGuid(in Guid value);
void WriteString32(string value);
void WriteString16(string value); // max ushort.MaxValue UTF-8 bytes, throws if the string does not fit
void WriteString8(string value);  // max byte.MaxValue UTF-8 bytes, throws if the string does not fit
```

#### Collections
```csharp
void WriteArrayUnmanaged<T>(T[] value) where T : unmanaged; // memcpy
void WriteArray<T>(T[] value);                                // per-element
void WriteArrayUnmanaged<T>(T[,] value) where T : unmanaged;  // also T[,,]; WriteArrayUnmanaged2D/3D are aliases
void WriteArray<T>(T[,] value);                               // also T[,,]; WriteArray2D/3D are aliases
void WriteList<T>(List<T> value, int count = -1);
void WriteQueue<T>(Queue<T> value);
void WriteStack<T>(Stack<T> value);
void WriteLinkedList<T>(LinkedList<T> value);
void WriteHashSet<T>(HashSet<T> value);
void WriteDictionary<K, V>(Dictionary<K, V> value);
void WriteCollection(int idx, int count, WriteCollectionDelegate @delegate); // same layout as WriteArray<T>
```

#### Unmanaged tuples
```csharp
// 1..8 values of independent unmanaged types, one EnsureSize for the group:
void WriteUnmanaged<T1, T2>(in T1 v1, in T2 v2) where T1 : unmanaged where T2 : unmanaged;
void ReadUnmanaged<T1, T2>(out T1 v1, out T2 v2) where T1 : unmanaged where T2 : unmanaged;
// *Sized variants prefix the group with its byte size, so a reader compiled against a different
// layout can skip it instead of desynchronising:
void WriteUnmanagedSized<T1, T2>(in T1 v1, in T2 v2) where T1 : unmanaged where T2 : unmanaged;
void ReadUnmanagedSized<T1, T2>(out T1 v1, out T2 v2) where T1 : unmanaged where T2 : unmanaged;
// Force* drop the unmanaged constraint for generic contexts that cannot state it; a debug build
// rejects a type that turns out to contain references:
void ForceWriteUnmanaged<T1>(in T1 v1);
void ForceReadUnmanaged<T1>(out T1 v1);
void ForceWriteUnmanagedSized<T1>(in T1 v1);
void ForceReadUnmanagedSized<T1>(out T1 v1);
```

#### Span / Memory / Sequence
```csharp
void WriteBytes(ReadOnlySpan<byte> value);
void WriteBytes(ReadOnlyMemory<byte> value);
void WriteBytes(in ReadOnlySequence<byte> value);
void WriteUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged;  // raw memcpy
void WriteUnmanaged<T>(ReadOnlyMemory<T> value) where T : unmanaged;
void WriteSpanUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged; // with array headers
void WriteSpan<T>(ReadOnlySpan<T> value); // per-element with headers
```

#### File handling
```csharp
void WriteFromFile(string filePath, bool gzip = false, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue); // appends file contents, decompressing when gzip is set
void FlushToFile(string filePath, bool gzip = false, bool flushToDisk = false); // writes the buffer out, compressing when gzip is set
void WriteGzipData(byte[] data, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue); // appends decompressed gzip bytes
```

### BinaryPackReader
Structure for reading binary data

#### Creation
```csharp
BinaryPackReader(byte* buffer, uint size, uint position);                    // user memory
BinaryPackReader(NativeArray<byte> buffer, uint size, uint position);        // Unity only
static BinaryPackReader Create(byte* buffer, uint size, uint position = 0);  // user memory
static BinaryPackReader Create(NativeArray<byte> buffer, uint position = 0); // Unity only
static BinaryPackReader AllocAndFillFromStream(Stream source, uint size);    // owned native buffer
static BinaryPackReader AllocAndFillFromStream(Stream source, uint size, PackAllocator allocator);
static BinaryPackReader AllocAndFillFromBytes(byte[] data, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromFile(string filePath, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromBytes(byte[] data, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromFile(string filePath, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromSpan(ReadOnlySpan<byte> data); // owned native copy, no gzip detection
static bool IsGzip(ReadOnlySpan<byte> data);
BinaryPackWriter AsWriter();        // non-owning writer over [0, Size), positioned at Size
BinaryPackWriter AsWriterCompact(); // moves the unread tail to the start and takes ownership
BinaryPackReader AsReader(uint position);
bool IsCreated;  // false for default, after Dispose, and after AsWriterCompact
bool Owned;      // holds a buffer of its own
bool HasNext();  bool HasNext(uint bytesCount);
void SkipNext(); void SkipNext(uint bytesCount);
bool TryReadArrayHeader(out int count); // false for a stored null; leaves Position on the first element
ArraySegment<T> ReadArrayPooled<T>(out ArrayPoolHandle<T> poolHandle); // release via poolHandle.Return()
void Dispose(); // frees the buffer when Owned
```

`ReadArrayUnmanagedAsSpan<T>()` hands back a view over the reader's own buffer, so it neither copies nor
allocates. The payload sits at an arbitrary byte offset, which means the elements are generally not aligned to
`sizeof(T)` — fine for byte-sized elements everywhere and for scalar structs on x64 and ARM64. Element types
that need real alignment, SIMD vectors among them, go through the `PackArenaAllocator` overload, which copies
into 16-aligned arena memory and still allocates nothing on the managed heap.

The `AllocAndFill*` overloads without the `gzip` flag autodetect gzip from the RFC 1952 magic bytes `0x1F 0x8B` at offset 0,
so a plain payload whose first two bytes happen to be `1F 8B` is taken for a gzip stream: read such data through the
overload with the explicit `gzip` flag. For a gzip source `headerSize` (1..256) and `parseTotalSize` are required in
every build, and a `parseTotalSize` result above what DEFLATE can expand the source to is rejected before allocating.

#### Primitives
```csharp
byte ReadByte();
sbyte ReadSbyte();
bool ReadBool();
short ReadShort();
ushort ReadUshort();
char ReadChar();
int ReadInt();
uint ReadUint();
long ReadLong();
ulong ReadUlong();
float ReadFloat();
double ReadDouble();
int ReadVarInt();
short ReadVarShort();
// TryRead* variants return bool: also TryReadVarInt / TryReadVarShort / TryReadString8 / 16 / 32
```

#### Batch primitives
```csharp
// Available for: byte, short, ushort, int, uint, float, long, ulong, double
void ReadInt(out int v0, out int v1);
void ReadInt(out int v0, out int v1, out int v2);
void ReadInt(out int v0, out int v1, out int v2, out int v3);
void ReadFloat(out float v0, out float v1, out float v2);
// ... same pattern for all types
```

#### Span / Memory
```csharp
void ReadBytes(Span<byte> destination);
ReadOnlySpan<byte> ReadBytesAsSpan(uint count);     // zero-copy
ReadOnlySpan<byte> RemainingAsSpan();
void ReadUnmanaged<T>(Span<T> destination) where T : unmanaged;      // raw memcpy
int ReadSpanUnmanaged<T>(Span<T> destination) where T : unmanaged;   // with array headers, -1 for a stored null
int ReadSpan<T>(Span<T> destination);                                // per-element, -1 for a stored null
ReadOnlySpan<T> ReadArrayUnmanagedAsSpan<T>() where T : unmanaged;   // zero-copy view over the buffer
Span<T> ReadArrayUnmanagedAsSpan<T>(in PackArenaAllocator arena) where T : unmanaged; // 16-aligned arena copy
```

#### Collections
```csharp
T[] ReadArrayUnmanaged<T>() where T : unmanaged;
T[] ReadArray<T>();
T[,] ReadArray2D<T>();   T[,,] ReadArray3D<T>();
T[,] ReadArray2DUnmanaged<T>() where T : unmanaged;   T[,,] ReadArray3DUnmanaged<T>() where T : unmanaged;
List<T> ReadList<T>();
Queue<T> ReadQueue<T>();
Stack<T> ReadStack<T>();
LinkedList<T> ReadLinkedList<T>();
HashSet<T> ReadHashSet<T>();
Dictionary<K, V> ReadDictionary<K, V>();
// ReadX<T>(ref X result) variants for reuse: return int (elements read, -1 when the null flag was read - result is left untouched then)
// SkipArray(), SkipList(), etc. for skipping
```

### BinaryPack
Central registry of serializers

```csharp
static void Init();
static void RegisterWithCollections<T, S>(BinaryWriter<T> writer, BinaryReader<T> reader, S strategy = default)
    where S : struct, IPackArrayStrategy<T>;
static void Register<T>(BinaryWriter<T> writer, BinaryReader<T> reader);
static T Read<T>(this ref BinaryPackReader reader);
static void Write<T>(this ref BinaryPackWriter writer, in T value);
static bool IsRegistered<T>();
static int SizeOf<T>();

// one-call round trips, each creating and disposing a writer/reader of its own:
static byte[] WriteToBytes<T>(T value, bool gzip = false, uint byteSizeHint = 4096);
static int WriteToBytes<T>(T value, ref byte[] result, bool gzip = false, uint byteSizeHint = 4096);
static void WriteToFile<T>(T value, string filePath, bool gzip = false, bool flushToDisk = false, uint byteSizeHint = 4096);
static T ReadFromBytes<T>(byte[] bytes, bool gzip = false, uint byteSizeHint = 4096);
static T ReadFromBytes<T>(byte[] bytes, uint position, uint count, bool gzip = false, uint byteSizeHint = 4096);
static T ReadFromFile<T>(string filePath, bool gzip = false, uint byteSizeHint = 4096);

// straight into and out of caller memory, allocating nothing:
static int WriteToSpan<T>(T value, Span<byte> destination);
static T ReadFromSpan<T>(ReadOnlySpan<byte> bytes);
```

`RegisterWithCollections<T, S>` registers `T` plus `T?`, `T[]`, `T[,]`, `T[,,]`, `T[][]`, `T[][][]`, `List<T>`,
`LinkedList<T>`, `Queue<T>`, `Stack<T>`, `HashSet<T>`. Anything else — `Dictionary<K, V>`, `T?[]`, `List<T?>`,
`List<T[]>` — needs its own `Register<T>` before `Write<T>`/`Read<T>` can dispatch to it; the direct
`WriteDictionary`/`ReadDictionary` and `WriteArray`/`ReadArray` calls work without any registration.

### Array serialization strategies
1. `UnmanagedPackArrayStrategy<T>` - for `unmanaged` types, direct memory copy
2. `StructPackArrayStrategy<T>` - for structures, element-by-element serialization
3. `ClassPackArrayStrategy<T>` - for classes, element-by-element serialization

## Registration of custom types

### Example: Simple structure
```csharp
public struct Vector3 {
    public float X, Y, Z;
}

BinaryPack.RegisterWithCollections(
    (ref BinaryPackWriter writer, in Vector3 v) => {
        writer.WriteFloat(v.X, v.Y, v.Z); // batch write
    },
    (ref BinaryPackReader reader) => {
        reader.ReadFloat(out var x, out var y, out var z); // batch read
        return new Vector3 { X = x, Y = y, Z = z };
    },
    new UnmanagedPackArrayStrategy<Vector3>()
);
```

### Example: Complex nested type
```csharp
public class GameState {
    public Player[] Players;
    public Dictionary<int, Item> Inventory;
    public int Level;
}

public struct Player {
    public string Name;
    public Vector3 Position;
}

public struct Item {
    public int Id;
    public float Durability;
}

public static class GameSerializers {
    public static void RegisterAll() {
        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in Vector3 v) => writer.WriteFloat(v.X, v.Y, v.Z),
            (ref BinaryPackReader reader) => {
                reader.ReadFloat(out var x, out var y, out var z);
                return new Vector3 { X = x, Y = y, Z = z };
            },
            new UnmanagedPackArrayStrategy<Vector3>());

        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in Item item) => writer.WriteInt(item.Id),
            (ref BinaryPackReader reader) => new Item { Id = reader.ReadInt() },
            new UnmanagedPackArrayStrategy<Item>());

        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in Player player) => {
                writer.WriteString16(player.Name);
                writer.Write(player.Position);
            },
            (ref BinaryPackReader reader) => new Player {
                Name = reader.ReadString16(),
                Position = reader.Read<Vector3>()
            },
            new StructPackArrayStrategy<Player>());

        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in GameState state) => {
                writer.WriteInt(state.Level);
                writer.WriteArray(state.Players);
                writer.WriteDictionary(state.Inventory);
            },
            (ref BinaryPackReader reader) => new GameState {
                Level = reader.ReadInt(),
                Players = reader.ReadArray<Player>(),
                Inventory = reader.ReadDictionary<int, Item>()
            },
            new ClassPackArrayStrategy<GameState>());
    }
}
```

# License
[MIT license](./LICENSE.md)