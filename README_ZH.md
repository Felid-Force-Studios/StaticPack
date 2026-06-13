<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-2.0.0-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticPack/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticPack-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
  <br><br>
  <a href="https://github.com/Felid-Force-Studios/StaticPack/blob/master/MIGRATION_2.0.md"><img src="https://img.shields.io/badge/迁移指南-2.0.0-red?style=for-the-badge" alt="迁移指南"></a>
</p>

# Static Pack - C# 简洁二进制序列化库
- 轻量级
- 高性能
- 仅一个依赖：`System.Runtime.CompilerServices.Unsafe`（Unity 包中随包提供，netstandard2.1 下为 NuGet 依赖，.NET 6+ 已包含在 BCL 中）
- 无反射
- 无代码生成
- 无数据模式
- 批量原始类型操作
- 支持 Span / Memory / ReadOnlySequence
- 兼容 Unity 及其他 C# 引擎

#### 限制与特性：
> - 多态类型需要自定义实现
> - 循环引用需要自定义实现

## 目录
* [联系方式](#联系方式)
* [支持项目](#支持项目)
* [环境要求](#环境要求)
* [安装](#安装)
* [概念](#概念)
* [数据格式](#数据格式)
* [内存模型](#内存模型)
* [快速开始](#快速开始)
* [API](#api)
  * [BinaryPackWriter](#binarypackwriter)
  * [BinaryPackReader](#binarypackreader)
  * [BinaryPack](#binarypack)
  * [数组策略](#数组序列化策略)
* [自定义类型](#自定义类型注册)
* [许可证](#许可证)

# 联系方式
* [felid.force.studios@gmail.com](mailto:felid.force.studios@gmail.com)
* [Telegram](https://t.me/felid_force_studios)

# 支持项目
如果您喜欢 Static Pack 并且它对您的项目有所帮助，您可以支持开发：

<a href="https://www.buymeacoffee.com/felid.force.studios" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="60"></a>

# 环境要求
* **Unity** 2022.3 或更高版本，API Compatibility Level 为 .NET Standard 2.1 或 .NET Framework。
  `com.unity.burst` 为可选。没有它时一切照常工作，但 writer 只能从托管代码里扩容缓冲区。有它时
  （1.8.0 或更高版本），内置分配器通过 `BurstCompiler.CompileFunctionPointer` 取得自己的 `Realloc`/`Free`，
  即这些指针指向原生编译后的代码，于是 writer 可以直接在 Burst job 内部扩容并释放缓冲区。
  某个 writer 属于哪一种，可用 `PackMemory.IsBurstCompatible(in writer)` 查询。
* **.NET** 6.0 或更高版本，或 netstandard2.1。
* `System.Runtime.CompilerServices.Unsafe` —— Unity 包内随包提供，netstandard2.1 从 NuGet 拉取，
  .NET 6+ 已包含在 BCL 中。

# 安装
* ### 以源代码形式
  从发布页面或从分支下载归档文件。`master` 分支包含稳定测试版本
* ### Unity 安装
  通过 Unity PackageManager 的 git 模块 `https://github.com/Felid-Force-Studios/StaticPack.git`
  或添加到 `Packages/manifest.json` `"com.felid-force-studios.static-pack": "https://github.com/Felid-Force-Studios/StaticPack.git"`
* ### NuGet
  ```
  dotnet add package FFS.StaticPack
  ```
  用于带断言的调试构建：
  ```
  dotnet add package FFS.StaticPack.Debug
  ```
  包：[FFS.StaticPack](https://www.nuget.org/packages/FFS.StaticPack/) · [FFS.StaticPack.Debug](https://www.nuget.org/packages/FFS.StaticPack.Debug/)

# 概念
本库提供高性能的二进制序列化工具，支持：
> - 原始类型和数组
> - 多维数组
> - 集合（列表、队列、字典等）
> - 自定义类型
> - 批量原始类型读写（一次 2、3、4 个值）
> - Span、Memory、ReadOnlySequence 零拷贝操作
> - 直接文件读写
> - 数据压缩
> - 原生（非托管）缓冲区：`BinaryPackWriter`/`BinaryPackReader` 是完全非托管的结构体，可在 Unity Burst 任务（job）中使用

# 数据格式
不带头部和 schema 的扁平字节流，按**宿主字节序**写入和读取 —— 库的所有目标平台都是小端序，因此在大端序主机上
生成的字节流无法在小端序主机上读回。

> - 整数和浮点数就是内存中的原始字节：IEEE-754 位模式，保留 NaN payload、带符号零和无穷大
> - `Guid` 为其内存布局，在小端序上等同于 `Guid.ToByteArray()`
> - `DateTime` 为 `DateTime.ToBinary()` 返回的 `long`，因此 `Kind` 可以完整往返
> - 字符串为 UTF-8，带字节长度前缀：`String8` 为 1 字节，`String16` 为 2 字节，`String32` 为 4 字节
> - 引用值、可空值和集合前面都有一个单字节的 null 标志
> - 数组和集合先存 `int` 元素数量，再存 `uint` 负载字节大小，然后是元素本身
> - `VarInt` 占 1..5 字节，`VarShort` 占 1..2 字节，每字节七位有效数据，低位组在前

# 内存模型

缓冲区是原始原生内存（`byte* Buffer`），不再是托管数组。writer 以三种模式之一工作：

1. **Owned（自有）** — 由库分配原生内存（Unity 中使用 `UnsafeUtility.Malloc`，其他环境使用 `NativeMemory`/`AllocHGlobal`），按需增长，并在 `Dispose` 时释放。
2. **Allocator-owned（自定义分配器）** — 缓冲区全程由您的 `PackAllocator`（一对 `delegate*` 函数指针）拥有：初始分配、增长和释放都经由它完成。两个指针都是必需的；如果在 `Dispose` 时没有任何需要释放的内容，请传入一个空的 `Free` 方法。
3. **User-provided（用户提供）** — 您传入一个指针和容量。结构体永远不会释放它，溢出时会抛出异常。

只要结构体持有缓冲区，`IsCreated == true`：对于 `default`、在 `Dispose` 之后，以及缓冲区经 `AsReaderOwned`/`AsWriterCompact` 转交之后，它都会变为 false。只要结构体持有自己的缓冲区（模式 1 和 2），`Owned == true`：`Dispose` 会释放该缓冲区，写入时可以增长它。

```csharp
// 自有原生缓冲区（必须释放）：
using var writer = BinaryPackWriter.Create(1024);

// 用户提供的内存（writer 永远不会释放它，溢出时抛出异常）：
byte* ptr = ...;
var writer = BinaryPackWriter.Create(ptr, capacity: 1024);

// 自定义分配器全程拥有缓冲区：
// Realloc(state, oldPtr, oldCapacity, usedBytes, newCapacity)：oldCapacity 是旧块大小，
// usedBytes 是其中必须保留的字节数 —— 请复制 min(usedBytes, newCapacity)。初始缓冲区以
// Realloc(state, null, 0, 0, capacity) 的形式到来；Free 在 Dispose 时调用。
var alloc = PackAllocator.FromDelegates(MyRealloc, MyFree); // 托管方法被编组为 Cdecl thunk
var writer = BinaryPackWriter.Create(1024, alloc);

// Unity：选择分配器 / 包装 NativeArray
using var writer = BinaryPackWriter.Create(1024, Allocator.TempJob);
var writer = BinaryPackWriter.Create(nativeArray); // 用户内存模式
var reader = BinaryPackReader.Create(nativeArray);
```

只要 writer 只通过其实例方法使用（`writer.WriteInt(...)` 等），`using var` 就没问题。一旦调用 ref 扩展方法 `Write<T>`/`Read<T>`（CS1657 —— using 变量不能按 `ref` 传递），它就无法编译 —— 参见下方“快速开始”中显式的 `try`/`finally`。

`PackAllocator` 持有两个 `delegate* unmanaged[Cdecl]` 函数指针（`Realloc`、`Free`）。三种生成方式：
- `PackAllocator.FromDelegates(realloc, free)` —— 将托管方法编组为 Cdecl thunk（在进程生命周期内保持根引用）。可从普通 C# 和 IL2CPP 调用，但**不是** Burst 优化的 —— 适用于长生命周期分配器。在 IL2CPP 上，目标方法必须是 `static` 并标注 `[AOT.MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]` / `FreeDelegate`。
- 带 `[UnmanagedCallersOnly(CallConvs = new[]{ typeof(CallConvCdecl) })]` 的静态方法 + `&Method`（.NET 6+）—— 无需编组的原生指针，传入 `new PackAllocator(realloc, free, state)` 构造函数。
- `BurstCompiler.CompileFunctionPointer<PackAllocator.ReallocDelegate>(MyRealloc).Value`（Unity + Burst）—— Burst 编译的原生代码，可在 Burst job 内部调用。

两个结构体都只包含非托管字段，因此它们可以作为 Burst job 的有效字段；原始类型、变长整数、非托管 span 以及缓冲区增长都是 Burst 安全的。字符串、集合、gzip 和文件 API 仍然是仅限托管环境的便捷功能。

**内置分配器与 Burst。** 当存在 `com.unity.burst` 包时，内置后端（`PackMemory.Default(...)`、`PackArenaAllocator`）会自动通过 `BurstCompiler.CompileFunctionPointer` 生成其函数指针，因此基于它们的 writer 可以**完全在 Burst job 内部**增长和释放。在初始化时于主线程调用一次 `PackMemory.WarmUp()`，将该编译开销移出首帧 —— 首次从托管代码使用时（包括 Edit 模式、EditMode 测试与编辑器工具）也会自动完成这次预热，因此这是一项优化而非前提条件，唯一的例外是最早的调用直接发生在 Burst 编译代码内部：那里无法编译函数指针，必须提前从托管代码完成预热。`PackMemory.IsBurstCompatible(in writer)` 报告给定 writer 的缓冲区是否能从 Burst 代码增长。没有 Burst（或在纯 .NET 上）时，内置后端使用 `[UnmanagedCallersOnly]`/编组指针 —— 对于托管侧增长已足够。

## PackArenaAllocator — 帧级 arena 分配器

一个内置的 `PackAllocator` 实现：每次分配只是一次指针递增（pointer bump），对单个 writer 调用 `Dispose` **不会释放任何内存**（但会让该 writer 失效，之后不可再使用该结构体），所有内存通过 `Reset()` 一次性回收 —— 通常在帧结束时调用。

溢出行为由 `PackArenaMode` 选择：

- `SpillToDefault`（默认）— 容量保持固定；溢出的分配会逐个回退到默认的原生分配器。溢出块（spill block）会链接到 arena 上，并由 `Reset()`/`Dispose()` 释放，因此不会泄漏任何内存。
- `AutoResize` — arena 会增长：当前块被退役（其内容在 `Reset()` 之前仍然有效），指针递增在一个至少两倍容量的新块中继续。`Reset()` 会保留最大的块，因此 arena 会收敛到实际的每帧需求量，并在最初几帧之后完全停止分配。

在这两种模式下，所有已分发的指针在 `Reset()`/`Dispose()` 之前都保持有效 —— 帧中途不会有任何内存被移动或释放。

```csharp
// arena 跨多个帧存活 —— 不要用 `using`，在其生命周期结束时释放
var arena = PackArenaAllocator.Create(64 * 1024, PackArenaMode.AutoResize);

// 每帧：
var writer = arena.CreateWriter(1024); // 初始缓冲区及所有增长都来自 arena
writer.WriteInt(42);
Send(writer.AsReader());
// writer.Dispose() 是可选的，并且不做任何事情

arena.Reset(); // 帧结束：所有已分发的指针失效，内存可复用

// 诊断信息：arena.Capacity、arena.Used、arena.Overflowed

// arena 生命周期结束时（例如系统/场景卸载）：
arena.Dispose();
```

arena 也是一个通用的非托管数据帧分配器，不仅限于 writer：

```csharp
ref var state = ref arena.Alloc<MyStruct>();          // 单个结构体，默认初始化
Span<float> temp = arena.AllocSpan<float>(1024);      // span，已清零（clear: false 可跳过）
MyStruct* raw = arena.AllocPtr<MyStruct>(16);         // 指向 16 个元素的原始指针，已清零
byte* bytes = arena.Alloc(256);                       // 原始字节
// 所有这些都存活到 arena.Reset() —— 无需逐个释放
```

arena 不是线程安全的 —— 每个线程使用一个。`Reset()` 之后，所有先前基于 arena 内存创建的 writer/reader 都将失效。

**生命周期规则：**
- 结构体的副本共享同一个指针：对一个副本进行扩容会使其他副本失效；请通过 `ref` 传递 writer，并且只释放一次。
- `default(BinaryPackWriter)` 不可使用（空缓冲区、零容量 —— 第一次写入即抛出异常）。
- 在 Unity 中，不要让使用 `Allocator.Temp` 创建的 writer 存活超过当前帧/job。

**内存检查**（所有构建）：
- 读取时，所有来自数据本身的长度 —— 元素数量、字节大小、数组维度、字符串与二进制块长度 —— 都会与元素大小和缓冲区剩余字节进行校验，在损坏的长度到达原始拷贝之前抛出异常。`ReadSpanUnmanaged` 还会检查 destination 能否容纳存储的元素数量。
- `FFS_PACK_DISABLE_MEMORY_CHECK` 会将这些检查从构建中移除 —— 适用于只读取自己产生的数据、并且需要省下读取路径上最后几条指令的场景。`DEBUG` / `FFS_PACK_ENABLE_DEBUG` 构建无论是否定义该宏都会保留这些检查。
- `FFS_PACK_DISABLE_MULTI_ARRAYS` 会将 `T[,]` / `T[,,]` 的 API 从构建中移除，`UNITY_WEBGL` 同样如此；数组策略仍保留这些成员，但会抛出 `NotSupportedException`。
- 解压本身没有大小上限：很小的 gzip 输入可能一直膨胀到缓冲区耗尽内存。请用 `WriteGzipData` / `WriteFromFile` 的 `maxDecompressedSize` 参数加以限制。

**调试检查**（`DEBUG` / `FFS_PACK_ENABLE_DEBUG` 构建，例如 `FFS.StaticPack.Debug` 程序集）：
- 每个自有分配都会连同其堆栈跟踪一起被追踪。`BinaryPackLeakTracker.Count` / `Report()` 显示存活的分配。泄漏的分配还会在 Unity/Mono 的 domain reload 时以及进程退出时被报告（Unity 中通过 `Debug.LogError`，其他环境通过 `Console.Error`）。在编辑器中，捕获堆栈跟踪每次分配大约要花 200 微秒和几 KB —— 分析性能时可设置 `BinaryPackLeakTracker.CaptureStackTraces = false`，分配本身仍然会被追踪。`BinaryPackLeakTracker.Clear()` 会忘记此前的所有分配，之后再对这些缓冲区调用 `Dispose` 会被接受，而不会被报告为双重 Dispose。在没有追踪的构建中该类型依然可以调用，只是报告空状态，因此引用它无需 `#if` 保护。在 Unity 中，分配还会通过 `UnsafeUtility.MallocTracked` 进行，从而接入 Unity 的 Native Leak Detection（原生泄漏检测）。
- 通过同一结构体的两个副本进行双重 `Dispose` —— 或者在另一个副本扩容缓冲区之后对过期副本进行 `Dispose` —— 会抛出异常，而不是破坏内存。
- `FFS.StaticPack` 与 `FFS.StaticPack.Debug` 的结构体布局并不相同：调试构建会给 writer 和 reader 增加一个追踪字段。不要在针对不同变体编译的程序集之间传递这些结构体。

# 快速开始
```csharp
using FFS.Libraries.StaticPack;

BinaryPack.Init();

// 自有原生缓冲区，由 Dispose 释放。不用 `using var`：Write<T>/Read<T> 以 ref 接收 writer/reader，
// 而编译器不允许将 using 变量按 ref 传递（CS1657）——因此改为显式调用 Dispose。
var writer = BinaryPackWriter.Create(1024);
try {
    // 基本类型：
    writer.WriteInt(123);
    writer.WriteString16("Hello world");
    writer.WriteArray(new short[] { 1, 2, 3 });
    writer.WriteDictionary(new Dictionary<string, DateTime> { { "today", DateTime.Today }, { "tomorrow", DateTime.Today.AddDays(1) } });

    // 批量写入（单次 EnsureSize 调用）：
    writer.WriteFloat(1.0f, 2.0f, 3.0f);  // x, y, z
    writer.WriteInt(10, 20, 30, 40);       // 一次写入 4 个值

    // Span 写入：
    Span<float> positions = stackalloc float[] { 1f, 2f, 3f };
    writer.WriteUnmanaged<float>(positions);

    // 自定义类型（只注册一次，最后写入）：
    BinaryPack.RegisterWithCollections<Person, StructPackArrayStrategy<Person>>(Person.Write, Person.Read);
    writer.Write(new Person { Name = "Alice", Age = 20, BirthDate = DateTime.Now });

    // reader 只能在所有写入之后获取：它把 writer.Position 记为自己的 Size，之后若再写入导致缓冲区
    // 增长（Resize），reader 就会指向已释放的内存。
    var reader = writer.AsReader(); // new BinaryPackReader(writer.Buffer, writer.Position, 0)

    var readInt = reader.ReadInt();                           // 123
    var readString = reader.ReadString16();                   // "Hello world"
    var readArray = reader.ReadArray<short>();                // [ 1, 2, 3 ]
    var readDict = reader.ReadDictionary<string, DateTime>(); // { "today", ... }, { "tomorrow", ... }

    // 批量读取：
    reader.ReadFloat(out var x, out var y, out var z);
    reader.ReadInt(out var a, out var b, out var c, out var d);

    // Span 读取：
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
用于写入二进制数据的结构体

#### 缓冲区管理
```csharp
static BinaryPackWriter Create(uint capacity = 1024);                                 // 自有原生内存
static BinaryPackWriter Create(byte* buffer, uint capacity, uint position = 0);       // 用户内存
static BinaryPackWriter Create(uint capacity, PackAllocator allocator);  // 分配器全程拥有缓冲区
// 仅限 Unity：
static BinaryPackWriter Create(uint capacity, Allocator allocator);
static BinaryPackWriter Create(NativeArray<byte> buffer, uint position = 0);

void EnsureSize(uint size);
byte[] CopyToBytes(bool gzip = false);
int CopyToBytes(ref byte[] result, bool gzip = false);
ReadOnlySpan<byte> AsSpan();                        // 已写入的字节，无复制
ReadOnlySpan<byte> AsSpan(uint offset, uint count);
int CopyTo(Span<byte> destination);
uint MakePoint(uint size);
BinaryPackReader AsReader();      // 非拥有视图，writer 仍需释放
BinaryPackReader AsReaderOwned(); // 将缓冲区所有权转移给 reader 并使 writer 失效
bool IsCreated;  // 对于 default、Dispose 之后以及 AsReaderOwned 之后为 false
bool Owned;      // 持有自己的缓冲区
void Skip(uint bytesCount);
void Dispose(); // 在自有模式下释放缓冲区 / 通过 PackAllocator.Free 释放
```

#### 原始类型
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
void WriteVarInt(int value);   // 1-5 字节；负数总是占用 5 字节，并能正确往返
void WriteVarShort(short value); // 1-2 字节，仅限非负数 —— 负数会抛出异常（位数不足以往返）
// WriteXAt(uint offset, X value) 就地修改已写入的值 —— 上面每种类型都有对应方法
```

#### 批量原始类型
```csharp
// 可用类型：byte, short, ushort, int, uint, float, long, ulong, double
// 所有值共用一次 EnsureSize 调用
void WriteInt(int v0, int v1);
void WriteInt(int v0, int v1, int v2);
void WriteInt(int v0, int v1, int v2, int v3);
void WriteFloat(float v0, float v1);
void WriteFloat(float v0, float v1, float v2);
void WriteFloat(float v0, float v1, float v2, float v3);
// ... 其他类型同理
```

#### 特殊类型
```csharp
void WriteNullable<T>(in T? value) where T : struct;
void WriteDateTime(DateTime value); // 通过 DateTime.ToBinary()/FromBinary()：Kind 与 ticks 精确往返，Local 会换算到读取端所在的时区
void WriteGuid(in Guid value);
void WriteString32(string value);
void WriteString16(string value); // 最大 ushort.MaxValue 字节 UTF-8，超出时抛出异常，不会截断
void WriteString8(string value);  // 最大 byte.MaxValue 字节 UTF-8，超出时抛出异常，不会截断
```

#### 集合
```csharp
void WriteArrayUnmanaged<T>(T[] value) where T : unmanaged; // memcpy
void WriteArray<T>(T[] value);                                // 逐元素
void WriteArrayUnmanaged<T>(T[,] value) where T : unmanaged;  // 以及 T[,,]；WriteArrayUnmanaged2D/3D 为同名方法
void WriteArray<T>(T[,] value);                               // 以及 T[,,]；WriteArray2D/3D 为同名方法
void WriteList<T>(List<T> value, int count = -1);
void WriteQueue<T>(Queue<T> value);
void WriteStack<T>(Stack<T> value);
void WriteLinkedList<T>(LinkedList<T> value);
void WriteHashSet<T>(HashSet<T> value);
void WriteDictionary<K, V>(Dictionary<K, V> value);
void WriteCollection(int idx, int count, WriteCollectionDelegate @delegate); // 与 WriteArray<T> 相同的布局
```

#### unmanaged 值组
```csharp
// 1..8 个独立 unmanaged 类型的值，整组只做一次 EnsureSize：
void WriteUnmanaged<T1, T2>(in T1 v1, in T2 v2) where T1 : unmanaged where T2 : unmanaged;
void ReadUnmanaged<T1, T2>(out T1 v1, out T2 v2) where T1 : unmanaged where T2 : unmanaged;
// *Sized 变体会在组前写入其字节大小，因此按不同 layout 编译的 reader 可以跳过它，而不会让流失步：
void WriteUnmanagedSized<T1, T2>(in T1 v1, in T2 v2) where T1 : unmanaged where T2 : unmanaged;
void ReadUnmanagedSized<T1, T2>(out T1 v1, out T2 v2) where T1 : unmanaged where T2 : unmanaged;
// Force* 去掉 unmanaged 约束，供无法表达该约束的泛型代码使用；调试构建会拒绝含引用的类型：
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
void WriteUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged;  // 直接 memcpy
void WriteUnmanaged<T>(ReadOnlyMemory<T> value) where T : unmanaged;
void WriteSpanUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged; // 带数组头
void WriteSpan<T>(ReadOnlySpan<T> value); // 带头逐元素
```

#### 文件操作
```csharp
void WriteFromFile(string filePath, bool gzip = false, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue); // 追加文件内容，gzip 为 true 时先解压
void FlushToFile(string filePath, bool gzip = false, bool flushToDisk = false); // 将缓冲区写入文件，gzip 为 true 时先压缩
void WriteGzipData(byte[] data, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue); // 追加解压后的 gzip 字节
```

### BinaryPackReader
用于读取二进制数据的结构体

#### 创建
```csharp
BinaryPackReader(byte* buffer, uint size, uint position);                    // 用户内存
BinaryPackReader(NativeArray<byte> buffer, uint size, uint position);        // 仅限 Unity
static BinaryPackReader Create(byte* buffer, uint size, uint position = 0);  // 用户内存
static BinaryPackReader Create(NativeArray<byte> buffer, uint position = 0); // 仅限 Unity
static BinaryPackReader AllocAndFillFromStream(Stream source, uint size);    // 自有原生缓冲区
static BinaryPackReader AllocAndFillFromStream(Stream source, uint size, PackAllocator allocator);
static BinaryPackReader AllocAndFillFromBytes(byte[] data, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromFile(string filePath, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromBytes(byte[] data, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromFile(string filePath, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromSpan(ReadOnlySpan<byte> data); // 自有原生副本，不检测 gzip
static bool IsGzip(ReadOnlySpan<byte> data);
BinaryPackWriter AsWriter();        // 覆盖 [0, Size) 的非拥有 writer，位置为 Size
BinaryPackWriter AsWriterCompact(); // 把未读取的尾部移到开头并接管所有权
BinaryPackReader AsReader(uint position);
bool IsCreated;  // 对于 default、Dispose 之后以及 AsWriterCompact 之后为 false
bool Owned;      // 持有自己的缓冲区
bool HasNext();  bool HasNext(uint bytesCount);
void SkipNext(); void SkipNext(uint bytesCount);
bool TryReadArrayHeader(out int count); // 存储值为 null 时返回 false；Position 停在第一个元素上
ArraySegment<T> ReadArrayPooled<T>(out ArrayPoolHandle<T> poolHandle); // 通过 poolHandle.Return() 归还
void Dispose(); // 当 Owned 时释放缓冲区
```

`ReadArrayUnmanagedAsSpan<T>()` 返回的是 reader 自身缓冲区上的视图，既不复制也不分配。负载位于任意字节偏移处，
因此元素通常不会按 `sizeof(T)` 对齐 —— 对单字节元素在任何平台上都安全，对标量结构体在 x64 和 ARM64 上也安全。
需要真实对齐的元素类型（包括 SIMD 向量）请使用带 `PackArenaAllocator` 的重载：它复制到 16 字节对齐的 arena
内存中，同样不会在托管堆上分配。

不带 `gzip` 标志的 `AllocAndFill*` 重载会根据数据开头的 RFC 1952 魔数 `0x1F 0x8B` 自动检测 gzip，因此前两个字节恰好是 `1F 8B`
的普通数据会被当作 gzip 流：这类数据请通过带显式 `gzip` 标志的重载读取。对于 gzip 源，`headerSize`（1..256）和
`parseTotalSize` 在所有构建中都是必需的，超出 DEFLATE 解压上限的 `parseTotalSize` 结果会在分配内存前被拒绝。

#### 原始类型
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
// TryRead* 变体返回 bool：也包括 TryReadVarInt / TryReadVarShort / TryReadString8 / 16 / 32
```

#### 批量原始类型
```csharp
// 可用类型：byte, short, ushort, int, uint, float, long, ulong, double
void ReadInt(out int v0, out int v1);
void ReadInt(out int v0, out int v1, out int v2);
void ReadInt(out int v0, out int v1, out int v2, out int v3);
void ReadFloat(out float v0, out float v1, out float v2);
// ... 其他类型同理
```

#### Span / Memory
```csharp
void ReadBytes(Span<byte> destination);
ReadOnlySpan<byte> ReadBytesAsSpan(uint count);     // 零拷贝
ReadOnlySpan<byte> RemainingAsSpan();
void ReadUnmanaged<T>(Span<T> destination) where T : unmanaged;      // 直接 memcpy
int ReadSpanUnmanaged<T>(Span<T> destination) where T : unmanaged;   // 带数组头，存储值为 null 时返回 -1
int ReadSpan<T>(Span<T> destination);                                // 逐元素，存储值为 null 时返回 -1
ReadOnlySpan<T> ReadArrayUnmanagedAsSpan<T>() where T : unmanaged;   // 缓冲区上的零拷贝视图
Span<T> ReadArrayUnmanagedAsSpan<T>(in PackArenaAllocator arena) where T : unmanaged; // 复制到 16 字节对齐的 arena
```

#### 集合
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
// ReadX<T>(ref X result) 变体用于复用：返回 int（读取的元素数，读到 null 标志时为 -1 —— 此时 result 保持不变）
// SkipArray(), SkipList() 等用于跳过
```

### BinaryPack
序列化器中央注册表

```csharp
static void Init();
static void RegisterWithCollections<T, S>(BinaryWriter<T> writer, BinaryReader<T> reader, S strategy = default)
    where S : struct, IPackArrayStrategy<T>;
static void Register<T>(BinaryWriter<T> writer, BinaryReader<T> reader);
static T Read<T>(this ref BinaryPackReader reader);
static void Write<T>(this ref BinaryPackWriter writer, in T value);
static bool IsRegistered<T>();
static int SizeOf<T>();

// 单次调用的往返操作，每个都会自行创建并释放 writer/reader：
static byte[] WriteToBytes<T>(T value, bool gzip = false, uint byteSizeHint = 4096);
static int WriteToBytes<T>(T value, ref byte[] result, bool gzip = false, uint byteSizeHint = 4096);
static void WriteToFile<T>(T value, string filePath, bool gzip = false, bool flushToDisk = false, uint byteSizeHint = 4096);
static T ReadFromBytes<T>(byte[] bytes, bool gzip = false, uint byteSizeHint = 4096);
static T ReadFromBytes<T>(byte[] bytes, uint position, uint count, bool gzip = false, uint byteSizeHint = 4096);
static T ReadFromFile<T>(string filePath, bool gzip = false, uint byteSizeHint = 4096);

// 直接写入调用方内存并从中读回，不做任何分配：
static int WriteToSpan<T>(T value, Span<byte> destination);
static T ReadFromSpan<T>(ReadOnlySpan<byte> bytes);
```

`RegisterWithCollections<T, S>` 会注册 `T` 以及 `T?`、`T[]`、`T[,]`、`T[,,]`、`T[][]`、`T[][][]`、`List<T>`、
`LinkedList<T>`、`Queue<T>`、`Stack<T>`、`HashSet<T>`。其余类型 —— `Dictionary<K, V>`、`T?[]`、`List<T?>`、
`List<T[]>` —— 需要自行 `Register<T>`，否则 `Write<T>`/`Read<T>` 找不到序列化器；直接调用
`WriteDictionary`/`ReadDictionary` 和 `WriteArray`/`ReadArray` 则无需注册。

### 数组序列化策略
1. `UnmanagedPackArrayStrategy<T>` - 用于 `unmanaged` 类型，直接内存复制
2. `StructPackArrayStrategy<T>` - 用于结构体，逐元素序列化
3. `ClassPackArrayStrategy<T>` - 用于类，逐元素序列化

## 自定义类型注册

### 示例：简单结构体
```csharp
public struct Vector3 {
    public float X, Y, Z;
}

BinaryPack.RegisterWithCollections(
    (ref BinaryPackWriter writer, in Vector3 v) => {
        writer.WriteFloat(v.X, v.Y, v.Z); // 批量写入
    },
    (ref BinaryPackReader reader) => {
        reader.ReadFloat(out var x, out var y, out var z); // 批量读取
        return new Vector3 { X = x, Y = y, Z = z };
    },
    new UnmanagedPackArrayStrategy<Vector3>()
);
```

### 示例：复杂嵌套类型
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

# 许可证
[MIT license](./LICENSE.md)
