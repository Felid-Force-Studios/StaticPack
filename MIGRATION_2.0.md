# StaticPack 1.x → 2.0.0 migration guide

This document describes every change between StaticPack **1.2.6** and **2.0.0** that affects code using the
library, and gives a deterministic procedure for updating a project. It is written to be executed either by a
person or by an automated agent: every item states how to find the affected code and what to replace it with.

- **Source version:** 1.2.6 (also covers 1.0.x–1.2.5, whose public API is a subset)
- **Target version:** 2.0.0
- **Namespace:** `FFS.Libraries.StaticPack` — unchanged, no `using` statement needs to change
- **Wire format:** unchanged for all valid inputs (see [§8](#8-data-compatibility))

---

## Contents

1. [Instructions for an automated agent](#1-instructions-for-an-automated-agent)
2. [What changed, in one paragraph](#2-what-changed-in-one-paragraph)
3. [Step 1 — update the package](#3-step-1--update-the-package)
4. [Step 2 — mechanical replacements](#4-step-2--mechanical-replacements)
5. [Step 3 — buffer ownership and Dispose](#5-step-3--buffer-ownership-and-dispose)
6. [Step 4 — silent behavior changes](#6-step-4--silent-behavior-changes)
7. [Step 5 — custom IPackArrayStrategy implementations](#7-step-5--custom-ipackarraystrategy-implementations)
8. [Data compatibility](#8-data-compatibility)
9. [Project configuration (Unity and .NET)](#9-project-configuration-unity-and-net)
10. [New in 2.0 — optional adoption](#10-new-in-20--optional-adoption)
11. [Troubleshooting by error message](#11-troubleshooting-by-error-message)
12. [Verification checklist](#12-verification-checklist)
13. [Complete API delta](#13-complete-api-delta)

---

## 1. Instructions for an automated agent

Work through sections 3 → 7 in order. Do not skip ahead: step 3 changes what compiles, and steps 4–7 are
easier to audit once the project builds again.

Two classes of change exist, and they need different handling:

- **Compiler-caught** (steps 2, 5, 7). If you miss one, the build fails and points at the line. Fixing until
  the build is green is sufficient.
- **Silent** (steps 3, 4, and two entries in step 2). The old code still compiles and does something
  different at runtime. The compiler will not help. **Every call site listed in those steps must be visited
  individually**, even if the project already builds.

Before reporting the migration as finished, run the checklist in [§12](#12-verification-checklist). It is
written as a set of searches that must return zero results.

Scope detection — find all code that touches the library:

```bash
rg -l "FFS\.Libraries\.StaticPack|BinaryPackWriter|BinaryPackReader|BinaryPack\b|IPackArrayStrategy" --glob '*.cs'
```

---

## 2. What changed, in one paragraph

In 1.x a writer and a reader wrapped a managed `byte[]`, optionally rented from `ArrayPool<byte>.Shared`. In
2.0 both wrap raw native memory (`byte* Buffer` + `uint Capacity`), so `BinaryPackWriter` and
`BinaryPackReader` are fully unmanaged structs and can live inside Unity Burst jobs. Almost every breaking
change follows from that one decision: the `byte[]`-typed factories are gone, `Rented` became `Owned`,
`Dispose` now releases native memory instead of returning an array to a pool, and a caller-provided buffer can
no longer grow itself. Independently of that, the top-level `BinaryPack` helpers stopped being extension
methods, the collection `Read*(ref …)` overloads now return the element count, and data-driven length
validation is enabled in Release builds by default.

---

## 3. Step 1 — update the package

### Unity (UPM)

Point the dependency at `2.0.0` — in `Packages/manifest.json` for a Git/registry dependency, or by replacing
the embedded copy under `Packages/` / `Assets/`.

`package.json` of the library declares `"unity": "2022.3"`, unchanged from 1.x.

### .NET (NuGet)

```bash
dotnet add package FFS.StaticPack --version 2.0.0
# debug build of the library, with leak tracking and validation:
dotnet add package FFS.StaticPack.Debug --version 2.0.0
```

Target frameworks are unchanged: `net10.0; net9.0; net8.0; net7.0; net6.0; netstandard2.1`. The single
dependency is unchanged as well: `System.Runtime.CompilerServices.Unsafe` 6.0.0, pulled in only for the
`netstandard2.1` target (it is part of the BCL on .NET 6+, and ships as a plugin inside the Unity package).

---

## 4. Step 2 — mechanical replacements

Every item in this section produces a compile error if it is missed, **except the two marked ⚠ SILENT**.

### 4.1 Renamed members

| 1.x | 2.0 | Note |
|---|---|---|
| `BinaryPackWriter.CreateFromPool(n)` | `BinaryPackWriter.Create(n)` | default capacity 512 → 1024; now owned native memory, see [§5](#5-step-3--buffer-ownership-and-dispose) |
| `BinaryPackReader.RentAndFillFromBytes(...)` | `BinaryPackReader.AllocAndFillFromBytes(...)` | ownership changed, see [§6.6](#66-allocandfillfrombytes-always-copies-and-always-owns) |
| `BinaryPackReader.RentAndFillFromFile(...)` | `BinaryPackReader.AllocAndFillFromFile(...)` | |
| `BinaryPackReader.RentAndFillFromStream(...)` | `BinaryPackReader.AllocAndFillFromStream(...)` | |
| `writer.Rented` / `reader.Rented` | `writer.Owned` / `reader.Owned` | |
| `writer.CurrentCapacity` (`int`) | `writer.Capacity` (`uint`) | now a field, not a property |
| `reader.ReadBytesAsMemory(count)` | `reader.ReadBytesAsSpan(count)` | `Memory<byte>` cannot wrap native memory without a managed allocation |
| `reader.RemainingAsMemory()` | `reader.RemainingAsSpan()` | |

Deprecated but still working (they compile with a warning; update them anyway):

| 1.x | 2.0 |
|---|---|
| `reader.ReadSByte()` | `reader.ReadSbyte()` |
| `reader.TryReadSByte(out v)` | `reader.TryReadSbyte(out v)` |
| `writer.WriteNotNullFlag(object value)` | `writer.WriteNotNullFlag<T>(T value) where T : class` — the `object` overload boxes value types |

### 4.2 The `BinaryPack` helpers are no longer extension methods

`ReadFromBytes` / `ReadFromFile` / `WriteToBytes` / `WriteToFile` are now plain static methods. Call them
through the class.

| 1.x | 2.0 |
|---|---|
| `bytes.ReadFromBytes<T>()` | `BinaryPack.ReadFromBytes<T>(bytes)` |
| `bytes.ReadFromBytes<T>(size, position)` | `BinaryPack.ReadFromBytes<T>(bytes, position, count)` — ⚠ **argument order changed**, see 4.3 |
| `filePath.ReadFromFile<T>()` | `BinaryPack.ReadFromFile<T>(filePath)` |
| `value.WriteToBytes()` | `BinaryPack.WriteToBytes(value)` |
| `value.WriteToBytes(ref result)` | `BinaryPack.WriteToBytes(value, ref result)` — now returns `int` (bytes written) instead of `void` |
| `value.WriteToFile(path)` | `BinaryPack.WriteToFile(value, path)` |

`Read<T>(this ref BinaryPackReader)` and `Write<T>(this ref BinaryPackWriter, in T)` **remain** extension
methods and are unchanged — `reader.Read<T>()` and `writer.Write(in value)` still work as before.

### 4.3 ⚠ SILENT — argument order in `ReadFromBytes`

```csharp
// 1.x — (size, position)
T value = bytes.ReadFromBytes<T>(size, position, gzip, byteSizeHint);

// 2.0 — (position, count)
T value = BinaryPack.ReadFromBytes<T>(bytes, position, count, gzip, byteSizeHint);
```

Both parameters are `uint`, so a call that was already written in static form
(`BinaryPack.ReadFromBytes<T>(bytes, a, b, …)`) keeps compiling with the two values swapped.

The meaning of the length parameter changed too. In 1.x `size` became the reader's `Size` field, which
`HasNext` compares against `Position` as an **absolute end offset**, so the readable region was
`[position, size)` — passing a non-zero `position` shortened the payload by that many bytes. In 2.0 `count`
is a genuine length and the region is `[position, position + count)`.

For the common case `position == 0` the two are identical and only the argument order needs fixing. For a
non-zero `position`, `count` must be what the old code *meant* — `size - position` if the old call was
working around the quirk, `size` if it was hitting it.

Find every occurrence:

```bash
rg -n "ReadFromBytes<" --glob '*.cs'
```

### 4.4 ⚠ SILENT — `byteSizeHint` / `gzip` order in `WriteToBytes`

```csharp
// 1.x
byte[] data = value.WriteToBytes(byteSizeHint: 8192, gzip: true);
// 2.0
byte[] data = BinaryPack.WriteToBytes(value, gzip: true, byteSizeHint: 8192);
```

A positional 1.x call does not compile against 2.0 (`uint` cannot convert to `bool`), so this is
compiler-caught — **unless** the original call used named arguments, which keep compiling and keep their
meaning. Listed here so the reorder is not mistaken for a behavior change.

### 4.5 Widened parameter types

| 1.x | 2.0 |
|---|---|
| `writer.WriteGzipData(byte[] data, int index, int count, uint bufferSize = 4096)` | `writer.WriteGzipData(byte[] data, uint index, uint count, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue)` |
| `writer.FlushToFile(path, offset, count, gzip, int bufferSize, flushToDisk, level)` | `… uint bufferSize …` |

Literal arguments keep compiling; `int` variables need a cast. `maxDecompressedSize` is a new optional
parameter on `WriteGzipData` and `WriteFromFile` — see [§10.4](#104-decompression-bounds).

### 4.6 Removed

| Removed | What to do |
|---|---|
| `BinaryPackWriter.Create(byte[] buffer, uint position = 0)` | use `Create(byte* buffer, uint capacity, uint position)` under `fixed`, `Create(uint capacity)`, or in Unity `Create(NativeArray<byte>)` — see the recipe below |
| `new BinaryPackReader(byte[] buffer, uint size, uint position)` | use `new BinaryPackReader(byte* buffer, uint size, uint position)` under `fixed`, `BinaryPackReader.Create(byte*, uint, uint)`, or in Unity the `NativeArray<byte>` constructor |
| `IPackArrayStrategy.IsUnmanaged()` | delete the implementation — see [§7](#7-step-5--custom-ipackarraystrategy-implementations) |
| `reader.ReadArrayUnmanagedPooled<T>(out handle)` | use `ReadArrayUnmanagedAsSpan<T>()` for a zero-copy view over the reader's buffer, `ReadArrayUnmanagedAsSpan<T>(in PackArenaAllocator)` for a 16-aligned copy with no managed allocation, or `ReadSpanUnmanaged<T>(Span<T>)` to read into your own storage — see [§10.6](#105-reading-without-allocating) |

Recipe for wrapping a managed array (the buffer must stay pinned for the lifetime of the reader/writer):

```csharp
// 1.x
var reader = new BinaryPackReader(bytes, (uint)bytes.Length, 0);
var value = reader.Read<MyType>();

// 2.0 — zero-copy over a pinned managed array
unsafe {
    fixed (byte* pointer = bytes) {
        var reader = new BinaryPackReader(pointer, (uint)bytes.Length, 0);
        var value = reader.Read<MyType>();   // must not escape the fixed block
    }
}

// 2.0 — or let the library own a native copy, with no unsafe code at the call site
var reader = BinaryPackReader.AllocAndFillFromBytes(bytes);
try {
    var value = reader.Read<MyType>();
}
finally {
    reader.Dispose();
}
```

(`using var reader = …` would be shorter but does not compile here: `Read<T>` is a `ref` extension method and
a `using` local cannot be passed by `ref` — CS1657. See [§5.2](#52-what-this-means-for-1x-code).)

---

## 5. Step 3 — buffer ownership and Dispose

This is the part that does not show up as a compile error and that leaks native memory when it is missed.

### 5.1 The three modes

A writer (and a reader) in 2.0 is in exactly one of three states:

1. **Owned** — the library allocated native memory. `Dispose` frees it, and writes can grow it.
   Created by `BinaryPackWriter.Create(capacity)`, `Create(capacity, allocator)`,
   `BinaryPackReader.AllocAndFill*`, and in Unity `Create(capacity, Allocator.…)`.
2. **User-provided** — you passed a `byte*` (or a `NativeArray<byte>`). The struct never frees it and throws
   on overflow.
3. **Not created** — `default`, or after `Dispose` / `AsReaderOwned` / `AsWriterCompact` handed the buffer
   away. `IsCreated` is false and the first write throws.

`IsCreated` tells modes 1–2 apart from 3; `Owned` is true only in mode 1.

### 5.2 What this means for 1.x code

| 1.x code | Disposal in 1.x | Disposal in 2.0 |
|---|---|---|
| `CreateFromPool(n)` → `Create(n)` | returns an array to `ArrayPool` — skipping it wasted pool capacity | **frees native memory — skipping it leaks, and the GC will never reclaim it** |
| `Create(byte[] buffer)` | no-op | replaced by user-provided memory: `Dispose` stays a no-op |
| `RentAndFillFromBytes` on a *non-gzip* array | no-op (`Rented = false`, the array was wrapped) | `AllocAndFillFromBytes` **always** owns: `Dispose` is now mandatory |
| `RentAndFillFrom*` on a gzip source | returned to the pool | frees native memory: mandatory |

Audit every construction site:

```bash
rg -n "BinaryPackWriter\.Create|AllocAndFillFrom|CreateWriter\(" --glob '*.cs'
```

and make sure each one is followed by a `Dispose` on every path, including exceptions:

```csharp
var writer = BinaryPackWriter.Create(1024);
try {
    writer.Write(in value);
    Send(writer.AsReader());
}
finally {
    writer.Dispose();
}
```

`using var` works too, **but only while the writer is used exclusively through its own instance methods**
(`writer.WriteInt(...)`). The moment a call goes through the `ref` extension methods `Write<T>` / `Read<T>`,
`using var` stops compiling with CS1657 (a `using` local cannot be passed by `ref`), which is why the
explicit `try`/`finally` above is the form used throughout the README.

### 5.3 Copies of the struct

Both structs are value types holding a raw pointer. A copy shares the pointer, so:

- pass writers and readers by `ref`, never by value, when the callee may grow or dispose them;
- resizing through one copy leaves every other copy pointing at freed memory;
- dispose exactly once. A second `Dispose` on the *same* variable is harmless (the pointer is nulled), but a
  `Dispose` through a second copy is a double free. Debug builds detect this and throw — see
  [§12](#12-verification-checklist).

### 5.4 Ownership transfer

Two methods move the buffer from one struct to the other and invalidate the source:

- `writer.AsReaderOwned()` — the reader takes over the buffer and frees it in its `Dispose`; the writer
  becomes not-created. Dispose the reader, not the writer.
- `reader.AsWriterCompact()` — the writer takes over; the reader becomes not-created.

`writer.AsReader()` is the non-owning form: it borrows the buffer, the writer keeps ownership and must still
be disposed, and the reader must not outlive it (nor survive a later growth of the writer).

---

## 6. Step 4 — silent behavior changes

Same signature, same call site, different runtime behavior. Each subsection gives the search that locates the
affected code.

### 6.1 The `ref` readers no longer null out the destination

Affected methods — all of them `ref` overloads:

```
ReadList  ReadDictionary  ReadHashSet  ReadQueue  ReadStack  ReadLinkedList
ReadArrayUnmanaged<T>(ref T[])        ReadArrayUnmanaged<T>(ref T[], int idx)
```

In 1.x a null flag in the stream assigned `result = null`. In 2.0 the method returns `-1` and **leaves
`result` exactly as it was**; a successful read returns the element count.

```csharp
// 1.x — after this, list is null when the stream held a null
reader.ReadList(ref list);

// 2.0 — equivalent behavior
if (reader.ReadList(ref list) < 0) {
    list = null;
}
```

If your code never serializes null collections, no change is needed. Otherwise every call site must be
updated, and the compiler will not flag any of them (ignoring a non-void return value is legal C#).

```bash
rg -n "\.Read(List|Dictionary|HashSet|Queue|Stack|LinkedList|ArrayUnmanaged)\s*\(\s*ref" --glob '*.cs'
```

`ReadArray<T>(ref T[])` and `ReadArray<T>(ref T[], int idx)` — the managed-element overloads — also changed
from `void` to `int`, but they already left the destination untouched on a null flag in 1.x, so for them only
the return value is new.

### 6.2 Over-long strings throw instead of being truncated

`WriteString8` and `WriteString16` in 1.x clamped the input to the first 255 / 65535 **characters**
(`Math.Min(value.Length, …)`) and silently wrote a truncated string. 2.0 throws when `value.Length` already
exceeds the limit, and the writer's `Position` is restored so the stream is not left half-written.

If 1.x code relied on the truncation, truncate explicitly before writing, or switch the field to
`WriteString32`. `WriteString32` was not truncating in 1.x and is unchanged.

### 6.3 User-provided buffers cannot grow

In 1.x `BinaryPackWriter.Create(byte[] buffer)` grew the buffer through `Array.Resize` when a write did not
fit. In 2.0 a writer over user-provided memory has no allocator and throws:

```
[StaticPack] Buffer overflow: externally provided memory cannot grow (no PackAllocator set)
```

Code that relied on implicit growth must either size the buffer up front, or switch to an owned writer
(`Create(capacity)`), or supply a `PackAllocator`.

### 6.4 `reader.AsWriter()` is positioned at `Size`

The returned writer covers `[0, Size)` and starts at `Size`, with no slack capacity and no allocator — the
first sequential `Write*` throws. It exists for patching data in place:

```csharp
var patcher = reader.AsWriter();
patcher.WriteIntAt(offset, newValue);   // in-place patch — fine
patcher.Position = 0;                   // or rewind and overwrite from the start
```

To append to a buffer, use `AsWriterCompact()` on a reader that owns its memory.

### 6.5 `reader.AsWriterCompact()` always invalidates the reader

In 1.x, when `Position == Size` the method returned a writer and left the reader's buffer in place. In 2.0 the
reader is always invalidated (its pointer is nulled), and the writer inherits ownership. The method also
requires `Owned == true`: over borrowed memory it would rewrite the owner's bytes in place, which debug builds
now reject with an exception.

### 6.6 `AllocAndFillFromBytes` always copies and always owns

1.x `RentAndFillFromBytes` wrapped a non-gzip array directly, with no copy and nothing to dispose. 2.0 copies
the payload into owned native memory in every case. Consequences:

- `Dispose` is mandatory (see [§5](#5-step-3--buffer-ownership-and-dispose));
- mutating the source `byte[]` afterwards no longer affects the reader;
- the copy costs one allocation and one memcpy. To keep the zero-copy behavior, pin the array yourself with
  `fixed` and use the `byte*` constructor (recipe in [§4.6](#46-removed)).

There is now also an explicit-gzip overload, `AllocAndFillFromBytes(data, gzip, headerSize, parseTotalSize)`,
for payloads whose first two bytes happen to be `0x1F 0x8B` and which the autodetecting overload would
otherwise mistake for a gzip stream. The same pair exists for `AllocAndFillFromFile`, and
`BinaryPackReader.IsGzip(byte[])` exposes the detection itself.

### 6.7 Length validation runs in Release builds

Reading now validates every length that comes from the data itself — element counts, byte sizes, array
dimensions, string and blob lengths — against the element size and the bytes left in the buffer, and throws
before a corrupt length reaches a raw copy. In 1.x these checks existed only in `DEBUG` /
`FFS_PACK_ENABLE_DEBUG` builds.

Two consequences: a corrupt or truncated payload that used to read garbage now throws, and each collection
read carries a few extra instructions. Define `FFS_PACK_DISABLE_MEMORY_CHECK` to compile the checks out, for
builds that only ever read data they produced themselves. `DEBUG` / `FFS_PACK_ENABLE_DEBUG` builds keep the
checks regardless of that define.

### 6.8 `ReadSpanUnmanaged` returns -1 for a stored null

In 1.x a null flag made `ReadSpanUnmanaged<T>(Span<T>)` return `0`, the same value an empty array produces. In 2.0
it returns `-1`, which lines it up with the `ref` readers of [§6.1](#61-the-ref-readers-no-longer-null-out-the-destination)
and makes null distinguishable from empty. Code that treated the result as a plain count is unaffected only while it
never serializes nulls; `if (reader.ReadSpanUnmanaged(destination) > 0)` keeps working, `for (var i = 0; i < read; i++)`
keeps working, and a comparison such as `read == 0` no longer catches the null case.

```bash
rg -n "ReadSpanUnmanaged" --glob '*.cs'
```

### 6.9 `AllocAndFillFromFile` opens the file with `FileShare.Read`

1.x used `FileShare.None`, which failed if another handle had the file open. 2.0 allows concurrent readers.

### 6.10 `WriteVarInt` with a negative argument

`WriteVarInt`/`WriteVarShort` are documented as taking non-negative values. In 1.x a negative argument threw
in `DEBUG` builds and wrote a single corrupt byte in Release. 2.0 encodes it correctly as the full 5-byte
(respectively 2-byte) form, which round-trips through `ReadVarInt`/`ReadVarShort`. This only changes the bytes
produced for inputs that were already invalid in 1.x.

### 6.11 `BinaryPack.Init()` is no longer load-bearing

`BinaryPack<T>` now forces the `BinaryPack` class constructor to run, so the built-in type registrations
happen on first use even if `Init()` was never called. `Init()` still exists and calling it is still correct —
existing code needs no change.

---

## 7. Step 5 — custom `IPackArrayStrategy` implementations

Only relevant if the project implements `IPackArrayStrategy<T>` itself (a custom strategy passed to
`BinaryPack.RegisterWithCollections`). Find them with:

```bash
rg -n "IPackArrayStrategy" --glob '*.cs'
```

Four changes, all compiler-caught:

1. **`IsUnmanaged()` is gone** from `IPackArrayStrategy`. Delete the implementation.
2. **`ReadArray` returns `int`:**
   ```csharp
   // 1.x
   public void ReadArray(ref BinaryPackReader reader, ref T[] result);
   public void ReadArray(ref BinaryPackReader reader, ref T[] result, int idx);
   // 2.0 — the number of elements read, or -1 when the null flag was read
   public int ReadArray(ref BinaryPackReader reader, ref T[] result);
   public int ReadArray(ref BinaryPackReader reader, ref T[] result, int idx);
   ```
   Forwarding implementations need no body change: `reader.ReadArray(ref result)` now returns that value
   already.
3. **The multi-dimensional members are unconditional.** In 1.x `ReadArray2D` / `ReadArray3D` /
   `WriteArray(T[,])` / `WriteArray(T[,,])` were wrapped in `#if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL`
   inside the interface. In 2.0 the interface always declares them, so a strategy must implement them even in
   builds where multi-dimensional arrays are compiled out. Follow what the built-in strategies do:
   ```csharp
   #if !FFS_PACK_DISABLE_MULTI_ARRAYS && !UNITY_WEBGL
   public T[,] ReadArray2D(ref BinaryPackReader reader) => reader.ReadArray2D<T>();
   #else
   public T[,] ReadArray2D(ref BinaryPackReader reader) {
       throw new NotSupportedException("multi-dimensional arrays are compiled out");
   }
   #endif
   ```
4. **Strategies must be structs.** `RegisterWithCollections<T, S>` gained a `struct` constraint:
   ```csharp
   // 1.x: where S : IPackArrayStrategy<T>
   // 2.0: where S : struct, IPackArrayStrategy<T>
   ```
   A strategy declared as a `class` no longer compiles at the registration site. Change it to a `struct`.

---

## 8. Data compatibility

**The wire format is unchanged.** A stream produced by 1.x reads back byte-for-byte identically under 2.0, and
vice versa. Verified against the 1.2.6 sources: the null flag, the `count` + `byteSize` array header, the
UTF-8 string prefixes (1/2/4 bytes), the `VarInt`/`VarShort` encodings, `Guid`, `DateTime.ToBinary()` and the
raw little-endian primitives are all emitted and parsed by the same code paths as before. The single exception
is the negative-`WriteVarInt` case in [§6.9](#610-writevarint-with-a-negative-argument), which produced an
unreadable stream in 1.x.

Saved files, network payloads and persisted blobs therefore need no conversion pass, and 1.x and 2.0 clients
can exchange data.

The format itself is documented in the README under **Wire format**: a flat, header-less, schema-less byte
stream in host byte order (little-endian on every supported platform).

---

## 9. Project configuration (Unity and .NET)

### 9.1 Unity — the library's own asmdef

`FFS.StaticPack.asmdef` changed, and the changes are picked up automatically with the package:

```jsonc
"references": ["Unity.Burst"],          // was: absent
"noEngineReferences": false,            // was: true
"versionDefines": [
    { "name": "com.unity.burst", "expression": "1.8.0", "define": "FFS_BURST" }
]
```

Burst stays **optional**. Unity silently drops an asmdef reference it cannot resolve, so a project without
`com.unity.burst` compiles unchanged and the library uses its managed path. When Burst 1.8.0 or newer is
present, `FFS_BURST` is defined (`"expression": "1.8.0"` is an inclusive lower bound, so any newer Burst also
matches) and the built-in allocators compile their function pointers through `BurstCompiler`, which is what
lets a writer grow and release its buffer from inside a job.

### 9.2 Unity — your own asmdefs

Add `"allowUnsafeCode": true` to an asmdef **only if your own code names a pointer type** — for example when
you call `BinaryPackWriter.Create(byte*, …)`, read `writer.Buffer`, or write a `fixed` block. Declaring locals
of type `BinaryPackWriter` / `BinaryPackReader` and calling their methods does not require it, even though the
structs contain pointer fields.

### 9.3 Unity — `System.Runtime.CompilerServices.Unsafe.dll`

The package ships this plugin, as it did in 1.x. Its importer settings changed from
`isExplicitlyReferenced: 1` to `isExplicitlyReferenced: 0`, which makes the assembly available to every
assembly definition in the project rather than only to those that list it in `precompiledReferences`.

If the console reports a duplicate assembly after upgrading, the project contains a second copy of
`System.Runtime.CompilerServices.Unsafe.dll` (commonly inside another third-party package). Unity deduplicates
managed plugins by **assembly name**, keeping the highest `Version` and resolving ties silently; the surviving
copy's importer settings then apply project-wide. Resolve it by deleting the redundant copy, or by disabling
one of them for all platforms in the importer.

### 9.4 .NET

Add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to the consuming project under the same condition as
§9.2 — only where your own code names a pointer type.

---

## 10. New in 2.0 — optional adoption

None of this is required to migrate; it is listed so an agent can recognize the new API and, where the project
asks for it, use it.

### 10.1 Custom allocators — `PackAllocator`

A pair of `delegate* unmanaged[Cdecl]` function pointers plus opaque state, which can own a writer's buffer
end-to-end:

```csharp
public delegate byte* ReallocDelegate(void* state, byte* oldPtr, uint oldCapacity, uint usedBytes, uint newCapacity);
public delegate void  FreeDelegate(void* state, byte* ptr);
```

`oldCapacity` is the size of the old block; `usedBytes` is how much of it must survive, so an implementation
copies `min(usedBytes, newCapacity)` rather than the whole capacity. The initial buffer is requested as
`Realloc(state, null, 0, 0, capacity)`.

Three ways to obtain the pointers: `PackAllocator.FromDelegates(realloc, free)` (marshalled thunks, works
under IL2CPP with `[AOT.MonoPInvokeCallback]`), `[UnmanagedCallersOnly(CallConvs = new[]{ typeof(CallConvCdecl) })]`
static methods with `&Method` on .NET 6+, or `BurstCompiler.CompileFunctionPointer` in Unity.

### 10.2 `PackArenaAllocator` — frame-scoped arena

Bump allocation, nothing is freed individually, everything is reclaimed by `Reset()`:

```csharp
var arena = PackArenaAllocator.Create(64 * 1024, PackArenaMode.AutoResize);
var writer = arena.CreateWriter(1024);
// … writer.Dispose() is optional and does nothing …
arena.Reset();   // end of frame
arena.Dispose(); // end of the arena's lifecycle
```

It doubles as a general-purpose frame allocator: `Alloc<T>()`, `AllocSpan<T>(count)`, `AllocPtr<T>(count)`,
`Alloc(bytes)`. `PackArenaMode.SpillToDefault` keeps the capacity fixed and falls back to the default
allocator on overflow; `AutoResize` retires the current chunk and continues in a larger one. Not thread-safe —
one arena per thread.

### 10.3 Burst

`BinaryPackWriter` and `BinaryPackReader` contain only unmanaged fields, so they are valid fields of a Burst
job. Primitives, var-ints, unmanaged spans and buffer growth are Burst-safe; strings, collections, gzip and
the file APIs remain managed-only. `PackMemory.WarmUp()` / `PackArenaAllocator.WarmUp()` front-load the
function-pointer compilation (both Unity-only, both already invoked before the first scene loads), and
`PackMemory.IsBurstCompatible(in writer)` reports whether a given writer can grow from Burst code.

### 10.4 Decompression bounds

`WriteGzipData` and `WriteFromFile` accept `maxDecompressedSize`. Without it a small gzip input can expand
until the buffer exhausts memory; pass a bound when decompressing data from an untrusted source.

### 10.5 Reading without allocating

Three ways to read an array of unmanaged elements without the library allocating anything:

```csharp
// a view over the reader's own buffer: no copy, no allocation
ReadOnlySpan<int> values = reader.ReadArrayUnmanagedAsSpan<int>();

// one copy into 16-aligned arena memory, reclaimed by arena.Reset()
Span<float4> vectors = reader.ReadArrayUnmanagedAsSpan<float4>(arena);

// into storage of your own, of any origin
if (reader.TryReadArrayHeader(out var count)) {
    Span<int> destination = stackalloc int[count];
    for (var i = 0; i < count; i++) {
        destination[i] = reader.ReadInt();
    }
}
```

The first one hands back the bytes where they lie, so the elements sit at whatever offset the payload has and are
generally not aligned to `sizeof(T)` — fine for byte-sized elements everywhere and for scalar structs on x64 and
ARM64, not for types that need real alignment such as SIMD vectors. Those take the arena overload, which copies into
16-aligned memory and still allocates nothing on the managed heap.

For elements that are not unmanaged there is `ReadSpan<T>(Span<T>)`, the counterpart of `WriteSpan<T>`.

### 10.6 Other additions

- `writer.WriteIntAt(offset, value)` and `writer.WriteLongAt(offset, value)` — the two `WriteXAt` overloads
  that were missing in 1.x, for patching a length written earlier via `MakePoint`.
- `writer.WriteArray2D(T[,])` / `WriteArray3D(T[,,])` and the `Unmanaged` variants — explicitly named
  aliases of the overloaded `WriteArray`.
- `reader.TryReadVarInt`, `TryReadVarShort`, `TryReadString8/16/32` — non-throwing reads.
- `reader.ReadNotNullFlag()` — the inverse of `ReadNullFlag()`.
- `BinaryPackReader.IsGzip(ReadOnlySpan<byte>)` and `AllocAndFillFromSpan(ReadOnlySpan<byte>)`.
- `writer.AsSpan()` / `AsSpan(offset, count)` — the written bytes without a copy — and `writer.CopyTo(Span<byte>)`.
- `BinaryPack.WriteToSpan<T>(value, Span<byte>)` and `BinaryPack.ReadFromSpan<T>(ReadOnlySpan<byte>)` — a round trip
  through caller memory with no allocation at all.
- `PackMemory` — native allocation, `Copy`, `Move`, `Clear`, `Fill` helpers over raw pointers.
- `PackUnsafe` — `SizeOf`, `As`, `AsRef`, `AsPointer`, `Add`, re-exported so that sibling libraries reach
  them through StaticPack instead of referencing `System.Runtime.CompilerServices.Unsafe` themselves.
- `BinaryPackLeakTracker` — see [§12](#12-verification-checklist).

---

## 11. Troubleshooting by error message

| Message | Cause | Fix |
|---|---|---|
| `CS1061: 'byte[]' does not contain a definition for 'ReadFromBytes'` | the helpers stopped being extension methods | [§4.2](#42-the-binarypack-helpers-are-no-longer-extension-methods) |
| `CS1061: … does not contain a definition for 'CreateFromPool' / 'RentAndFill…' / 'Rented' / 'CurrentCapacity'` | renamed | [§4.1](#41-renamed-members) |
| `CS1503: cannot convert from 'byte[]' to 'byte*'` | the `byte[]` factories are gone | [§4.6](#46-removed) |
| `CS0227: Unsafe code may only appear if compiling with /unsafe` | your code names a pointer type | [§9.2](#92-unity--your-own-asmdefs) / [§9.4](#94-net) |
| `CS1657: Cannot use 'writer' as a ref … because it is a 'using variable'` | `using var` plus the `ref` extension methods | use `try`/`finally`, [§5.2](#52-what-this-means-for-1x-code) |
| `CS0535: does not implement interface member 'IPackArrayStrategy<T>.ReadArray2D'` | the multi-dimensional members are now unconditional | [§7](#7-step-5--custom-ipackarraystrategy-implementations) |
| `CS0453: The type 'MyStrategy' must be a non-nullable value type` | `RegisterWithCollections` now requires `where S : struct` | [§7](#7-step-5--custom-ipackarraystrategy-implementations) |
| `[StaticPack] Buffer overflow: externally provided memory cannot grow (no PackAllocator set)` | a writer over user-provided memory ran out of room | [§6.3](#63-user-provided-buffers-cannot-grow) |
| `[StaticPack] Writer is not created: it is default, already disposed, or its buffer was transferred away by AsReaderOwned` | use after `Dispose` / `AsReaderOwned`, or a `default` struct | [§5.1](#51-the-three-modes) |
| `[StaticPack] Buffer is already freed: double Dispose` | two copies of the same struct were both disposed | [§5.3](#53-copies-of-the-struct) |
| `[StaticPack] AsWriterCompact on a reader over memory it does not own …` | `AsWriterCompact` now requires `Owned` | [§6.5](#65-readeraswritercompact-always-invalidates-the-reader) |
| `[ReadList] Corrupted payload …`, `[ReadArray] Corrupted payload …` | length validation now runs in Release | the payload really is corrupt, or was read with the wrong `position`/`count` — [§4.3](#43--silent--argument-order-in-readfrombytes), [§6.7](#67-length-validation-runs-in-release-builds) |
| `String length N chars already exceeds String8/String16 limit` | over-long strings throw instead of being truncated | [§6.2](#62-over-long-strings-throw-instead-of-being-truncated) |

---

## 12. Verification checklist

**1. These searches must return no results.**

```bash
rg -n "CreateFromPool|RentAndFillFrom|\.Rented\b|CurrentCapacity|ReadBytesAsMemory|RemainingAsMemory|IsUnmanaged\(|ReadArrayUnmanagedPooled" --glob '*.cs'
rg -n "ReadSByte" --glob '*.cs'          # deprecated casing, also matches TryReadSByte
rg -NP -n "(?<!BinaryPack)\.(ReadFromBytes|ReadFromFile|WriteToBytes|WriteToFile)\s*[<(]" --glob '*.cs'
```

The last one finds leftover extension-method call syntax; every surviving call to those four helpers must be
written as `BinaryPack.<Method>(…)`. It needs the PCRE2 engine (`-P`) for the lookbehind — without it, drop
the `(?<!BinaryPack)` group and confirm by hand that each hit is `BinaryPack.`-qualified.

**2. The project builds with no warnings from StaticPack.** The deprecated members
(`ReadSByte`, `TryReadSByte`, `WriteNotNullFlag(object)`) surface as `CS0618` warnings; a clean build means
none are left.

**3. Every call site listed by these searches has been visited individually.** The compiler cannot verify
them:

```bash
rg -n "\.Read(List|Dictionary|HashSet|Queue|Stack|LinkedList|ArrayUnmanaged)\s*\(\s*ref" --glob '*.cs'  # §6.1
rg -n "ReadFromBytes<"                                                --glob '*.cs'       # §4.3
rg -n "BinaryPackWriter\.Create|AllocAndFillFrom|CreateWriter\("      --glob '*.cs'       # §5
rg -n "AsWriter\(\)|AsWriterCompact\(\)"                              --glob '*.cs'       # §6.4, §6.5
rg -n "ReadSpanUnmanaged"                                             --glob '*.cs'       # §6.8
```

**4. No native memory leaks.** Run the test suite, or the application through a representative scenario,
against a debug build of the library — the `FFS.StaticPack.Debug` NuGet package, or any build defining
`DEBUG` or `FFS_PACK_ENABLE_DEBUG` — and assert at shutdown:

```csharp
if (BinaryPackLeakTracker.Count != 0) {
    throw new Exception(BinaryPackLeakTracker.Report());
}
```

`Report()` lists each live allocation with the stack trace of its allocation site, which points straight at
the missing `Dispose`. Leaks are also reported automatically on a Unity/Mono domain reload and on process
exit. The members stay callable in non-debug builds too (they report an empty state), so the check does not
need to be wrapped in `#if`. Set `BinaryPackLeakTracker.CaptureStackTraces = false` while profiling: stack
capture costs roughly 200 microseconds and a few kilobytes per allocation in the Unity Editor, and allocations
stay tracked without it.

In Unity, debug builds additionally route allocations through `UnsafeUtility.MallocTracked`, so Unity's own
Native Leak Detection covers them as well.

**5. Round-trip an existing payload.** Read a file or blob written by the 1.x build and compare the
deserialized value with the expected one. The format is unchanged, so this must pass without a conversion
step.

---

## 13. Complete API delta

Generated from the public members of the 1.2.6 and 2.0.0 sources.

### Removed

```
BinaryPackWriter.Create(byte[] buffer, uint position = 0)
BinaryPackWriter.CreateFromPool(uint minByteSize = 512)
BinaryPackWriter.CurrentCapacity                       → Capacity
BinaryPackWriter.Rented / BinaryPackReader.Rented      → Owned
BinaryPackReader(byte[] buffer, uint size, uint position)
BinaryPackReader.RentAndFillFromBytes(...)             → AllocAndFillFromBytes(...)
BinaryPackReader.RentAndFillFromFile(...)              → AllocAndFillFromFile(...)
BinaryPackReader.RentAndFillFromStream(...)            → AllocAndFillFromStream(...)
BinaryPackReader.ReadBytesAsMemory(uint count)         → ReadBytesAsSpan(uint count)
BinaryPackReader.RemainingAsMemory()                   → RemainingAsSpan()
BinaryPackReader.ReadArrayUnmanagedPooled<T>(out handle) → ReadArrayUnmanagedAsSpan<T>() / (in PackArenaAllocator)
IPackArrayStrategy.IsUnmanaged()
```

### Changed signatures

```
- public byte[] Buffer                                        (writer and reader)
+ public byte* Buffer   + public uint Capacity                 (writer)

- public static T ReadFromBytes<T>(this byte[] bytes, bool gzip = false, uint byteSizeHint = 4096)
+ public static T ReadFromBytes<T>(byte[] bytes, bool gzip = false, uint byteSizeHint = 4096)

- public static T ReadFromBytes<T>(this byte[] bytes, uint size, uint position, bool gzip = false, uint byteSizeHint = 4096)
+ public static T ReadFromBytes<T>(byte[] bytes, uint position, uint count, bool gzip = false, uint byteSizeHint = 4096)

- public static T ReadFromFile<T>(this string filePath, bool gzip = false, uint byteSizeHint = 4096)
+ public static T ReadFromFile<T>(string filePath, bool gzip = false, uint byteSizeHint = 4096)

- public static byte[] WriteToBytes<T>(this T value, uint byteSizeHint = 4096, bool gzip = false)
+ public static byte[] WriteToBytes<T>(T value, bool gzip = false, uint byteSizeHint = 4096)

- public static void WriteToBytes<T>(this T value, ref byte[] result, uint byteSizeHint = 4096, bool gzip = false)
+ public static int  WriteToBytes<T>(T value, ref byte[] result, bool gzip = false, uint byteSizeHint = 4096)

- public static void WriteToFile<T>(this T value, string filePath, bool gzip = false, bool flushToDisk = false, uint byteSizeHint = 4096)
+ public static void WriteToFile<T>(T value, string filePath, bool gzip = false, bool flushToDisk = false, uint byteSizeHint = 4096)

- public static void RegisterWithCollections<T, S>(...) where S : IPackArrayStrategy<T>
+ public static void RegisterWithCollections<T, S>(...) where S : struct, IPackArrayStrategy<T>

- public void ReadArray<T>(ref T[] result)                     + public int ReadArray<T>(ref T[] result)
- public void ReadArray<T>(ref T[] result, int idx)            + public int ReadArray<T>(ref T[] result, int idx)
- public void ReadArrayUnmanaged<T>(ref T[] result)            + public int ReadArrayUnmanaged<T>(ref T[] result)
- public void ReadArrayUnmanaged<T>(ref T[] result, int idx)   + public int ReadArrayUnmanaged<T>(ref T[] result, int idx)
- public void ReadList<T>(ref List<T> result)                  + public int ReadList<T>(ref List<T> result)
- public void ReadDictionary<K, V>(ref Dictionary<K, V>)       + public int ReadDictionary<TK, TV>(ref Dictionary<TK, TV>)
- public void ReadHashSet<T>(ref HashSet<T> result)            + public int ReadHashSet<T>(ref HashSet<T> result)
- public void ReadQueue<T>(ref Queue<T> result)                + public int ReadQueue<T>(ref Queue<T> result)
- public void ReadStack<T>(ref Stack<T> result)                + public int ReadStack<T>(ref Stack<T> result)
- public void ReadLinkedList<T>(ref LinkedList<T> result)      + public int ReadLinkedList<T>(ref LinkedList<T> result)

- public void WriteGzipData(byte[] data, int index, int count, uint bufferSize = 4096)
+ public void WriteGzipData(byte[] data, uint index, uint count, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue)
- public void WriteGzipData(byte[] data, uint bufferSize = 4096)
+ public void WriteGzipData(byte[] data, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue)

- public void WriteFromFile(string filePath, bool gzip = false, uint bufferSize = 4096)
+ public void WriteFromFile(string filePath, bool gzip = false, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue)

- public void FlushToFile(string filePath, uint offset, uint count, bool gzip, int  bufferSize, bool flushToDisk, CompressionLevel level)
+ public void FlushToFile(string filePath, uint offset, uint count, bool gzip, uint bufferSize, bool flushToDisk, CompressionLevel level)

- public interface IPackArrayStrategy<T>: ReadArray2D/ReadArray3D/WriteArray(T[,])/WriteArray(T[,,]) under #if
+ public interface IPackArrayStrategy<T>: the same four members declared unconditionally
```

### Deprecated (still compile, with a warning)

```
BinaryPackReader.ReadSByte()              → ReadSbyte()
BinaryPackReader.TryReadSByte(out sbyte)  → TryReadSbyte(out sbyte)
BinaryPackWriter.WriteNotNullFlag(object) → WriteNotNullFlag<T>(T) where T : class
```

### Added

```
BinaryPackWriter   Create(uint capacity = 1024), Create(uint, PackAllocator), Create(byte*, uint, uint)
                   Create(uint, Allocator), Create(NativeArray<byte>, uint)        [Unity]
                   IsCreated, Owned, Capacity, AsReaderOwned()
                   AsSpan(), AsSpan(uint, uint), CopyTo(Span<byte>)
                   WriteIntAt, WriteLongAt
                   WriteArray2D<T>, WriteArray3D<T>, WriteArrayUnmanaged2D<T>, WriteArrayUnmanaged3D<T>
                   WriteNotNullFlag<T>(T) where T : class

BinaryPackReader   BinaryPackReader(byte*, uint, uint), BinaryPackReader(NativeArray<byte>, uint, uint) [Unity]
                   Create(byte*, uint, uint), Create(NativeArray<byte>, uint)      [Unity]
                   IsCreated, Owned
                   IsGzip(ReadOnlySpan<byte>), AllocAndFillFromSpan(ReadOnlySpan<byte>)
                   ReadArrayUnmanagedAsSpan<T>(), ReadArrayUnmanagedAsSpan<T>(in PackArenaAllocator)
                   TryReadArrayHeader(out int), ReadSpan<T>(Span<T>)
                   AllocAndFillFromBytes(byte[], bool gzip, int, TotalSizeParser)
                   AllocAndFillFromFile(string, bool gzip, int, TotalSizeParser)
                   AllocAndFillFromStream(Stream, uint, PackAllocator)
                   AllocAndFillFromStream(Stream, uint, Allocator)                 [Unity]
                   ReadSbyte, TryReadSbyte, ReadNotNullFlag
                   TryReadVarInt, TryReadVarShort, TryReadString8, TryReadString16, TryReadString32

PackAllocator      readonly struct: Realloc, Free, State, ReallocPtr, FreePtr, IsCreated
                   FromDelegates(ReallocDelegate, FreeDelegate, void* state = null)
                   delegates ReallocDelegate, FreeDelegate
                   extensions: Alloc<T>, Alloc2D<T>, Alloc3D<T>, Alloc2DRaw, ReAlloc<T>, ReAllocRaw, FreeSafe

PackArenaAllocator Create(uint capacity, PackArenaMode mode = SpillToDefault)
                   CreateWriter(uint capacity = 1024), AsPackAllocator(), implicit operator PackAllocator
                   Alloc<T>(), AllocSpan<T>(int, bool), AllocPtr<T>(int, bool), Alloc(uint)
                   Reset(), Dispose(), Capacity, Used, Overflowed, OwnerThreadId
                   WarmUp()                                                        [Unity]
                   enum PackArenaMode { SpillToDefault, AutoResize }

PackMemory         Default(), AllocRaw, FreeRaw
                   Copy, Move, Clear, Fill, FillRaw
                   Default(Allocator), WarmUp(), IsBurstCompatible(in BinaryPackWriter)   [Unity]

PackUnsafe         SizeOf<T>, As<TFrom,TTo>, AsRef<T>(void*), AsPointer<T>, Add<T>

BinaryPack         WriteToSpan<T>(T, Span<byte>), ReadFromSpan<T>(ReadOnlySpan<byte>)

BinaryPackLeakTracker  Count, Report(), Clear(), CaptureStackTraces
```

### Compile-time defines

| Define | Effect | Status |
|---|---|---|
| `FFS_PACK_ENABLE_DEBUG` | caller-argument checks, allocation tracking, double-Dispose detection | unchanged from 1.x |
| `FFS_PACK_DISABLE_MULTI_ARRAYS` | compiles the `T[,]` / `T[,,]` API out | unchanged from 1.x |
| `FFS_PACK_DISABLE_MEMORY_CHECK` | compiles the data-driven length validation out | **new in 2.0** |
| `FFS_BURST` | set by the asmdef when `com.unity.burst` ≥ 1.8.0 is installed | **new in 2.0**, Unity only |
