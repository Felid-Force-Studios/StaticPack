<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-2.0.0-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticPack/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticPack-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
  <br><br>
  <a href="https://github.com/Felid-Force-Studios/StaticPack/blob/master/MIGRATION_2.0.md"><img src="https://img.shields.io/badge/Гайд_по_миграции-2.0.0-red?style=for-the-badge" alt="Гайд по миграции"></a>
</p>

# Static Pack - C# библиотека бинарной сериализации
- Легковесность
- Производительность
- Одна зависимость: `System.Runtime.CompilerServices.Unsafe` (в Unity-пакете поставляется вместе с ним, для netstandard2.1 — NuGet-зависимость, на .NET 6+ входит в BCL)
- Без рефлексии
- Без кодогенерации
- Без схемы данных
- Батч-операции для примитивов
- Поддержка Span / Memory / ReadOnlySequence
- Совместимость с Unity и другими C# движками

#### Ограничения и особенности:
> - Полиморфные типы требуют ручной реализации
> - Циклические ссылки требуют ручной реализации

## Оглавление
* [Контакты](#контакты)
* [Поддержать проект](#поддержать-проект)
* [Требования](#требования)
* [Установка](#установка)
* [Концепция](#концепция)
* [Формат данных](#формат-данных)
* [Модель памяти](#модель-памяти)
* [Быстрый старт](#быстрый-старт)
* [API](#api)
  * [BinaryPackWriter](#binarypackwriter)
  * [BinaryPackReader](#binarypackreader)
  * [BinaryPack](#binarypack)
  * [Стратегии массивов](#стратегии-сериализации-массивов)
* [Пользовательские типы](#регистрация-пользовательских-типов)
* [Лицензия](#лицензия)

# Контакты
* [felid.force.studios@gmail.com](mailto:felid.force.studios@gmail.com)
* [Telegram](https://t.me/felid_force_studios)

# Поддержать проект
Если вам нравится Static Pack и он помогает вашему проекту, вы можете поддержать разработку:

<a href="https://www.buymeacoffee.com/felid.force.studios" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="60"></a>

# Требования
* **Unity** 2022.3 или новее, API Compatibility Level .NET Standard 2.1 или .NET Framework.
  `com.unity.burst` необязателен. Без него всё работает, но растить буфер writer может только из
  managed-кода. С ним (1.8.0 или новее) встроенные аллокаторы получают свои `Realloc`/`Free` через
  `BurstCompiler.CompileFunctionPointer`, то есть указатели ведут в нативно скомпилированный код, и
  writer может растить и освобождать буфер прямо внутри Burst-джоба. Что именно получилось у
  конкретного writer, сообщает `PackMemory.IsBurstCompatible(in writer)`.
* **.NET** 6.0 или новее, либо netstandard2.1.
* `System.Runtime.CompilerServices.Unsafe` — поставляется внутри Unity-пакета, для netstandard2.1
  подтягивается из NuGet, на .NET 6+ уже входит в BCL.

# Установка
* ### В виде исходников
  Со страницы релизов или как архив из нужной ветки. В ветке `master` стабильная проверенная версия
* ### Установка для Unity
  git модуль `https://github.com/Felid-Force-Studios/StaticPack.git` в Unity PackageManager
  или добавление в `Packages/manifest.json` `"com.felid-force-studios.static-pack": "https://github.com/Felid-Force-Studios/StaticPack.git"`
* ### NuGet
  ```
  dotnet add package FFS.StaticPack
  ```
  Для debug-сборки с проверками:
  ```
  dotnet add package FFS.StaticPack.Debug
  ```
  Пакеты: [FFS.StaticPack](https://www.nuget.org/packages/FFS.StaticPack/) · [FFS.StaticPack.Debug](https://www.nuget.org/packages/FFS.StaticPack.Debug/)

# Концепция
Библиотека предоставляет высокопроизводительные инструменты для бинарной сериализации с поддержкой:
> - Примитивных типов и массивов
> - Многомерных массивов
> - Коллекций (списки, очереди, словари и т.д.)
> - Пользовательских типов
> - Батч-записи/чтения примитивов (2, 3, 4 значения за раз)
> - Span, Memory, ReadOnlySequence для zero-copy операций
> - Прямого чтения/записи файлов
> - Сжатия данных
> - Нативных (unmanaged) буферов: `BinaryPackWriter`/`BinaryPackReader` — полностью unmanaged структуры, пригодные для использования внутри Unity Burst-джобов

# Формат данных
Плоский поток байт без заголовка и схемы, записывается и читается в **порядке байт хоста** — little-endian на
всех целевых платформах библиотеки, поэтому поток, созданный на big-endian хосте, на little-endian не прочитается.

> - Целые и вещественные значения — сырые байты из памяти: битовые представления IEEE-754 с сохранением NaN-payload, знакового нуля и бесконечностей
> - `Guid` — его layout в памяти, на little-endian совпадает с `Guid.ToByteArray()`
> - `DateTime` — `long`, возвращаемый `DateTime.ToBinary()`, поэтому `Kind` переживает round-trip
> - Строки — UTF-8 с префиксом длины в байтах: 1 байт для `String8`, 2 для `String16`, 4 для `String32`
> - Ссылочным значениям, nullable и коллекциям предшествует однобайтовый null-флаг
> - Массивы и коллекции хранят количество элементов как `int`, затем размер полезных данных как `uint`, затем сами элементы
> - `VarInt` занимает 1..5 байт, `VarShort` — 1..2 байта, по семь бит полезных данных на байт, младшая группа первой

# Модель памяти

Буфер — это сырая нативная память (`byte* Buffer`), а не управляемый массив. Writer работает в одном из трёх режимов:

1. **Owned (владеющий)** — библиотека сама выделяет нативную память (`UnsafeUtility.Malloc` в Unity, `NativeMemory`/`AllocHGlobal` в остальных случаях), при необходимости увеличивает её и освобождает в `Dispose`.
2. **Allocator-owned (кастомный аллокатор)** — буфером целиком владеет ваш `PackAllocator` (пара `delegate*` указателей на функции): начальная аллокация, рост и освобождение идут через него. Оба указателя обязательны; передайте пустой метод `Free`, если при `Dispose` освобождать нечего.
3. **User-provided (пользовательская память)** — вы передаёте указатель и ёмкость. Структура никогда не освобождает её, а при переполнении бросает исключение.

`IsCreated == true`, пока структура держит буфер: становится false для `default`, после `Dispose` и после передачи буфера через `AsReaderOwned`/`AsWriterCompact`. `Owned == true`, пока она держит собственный буфер (режимы 1 и 2): `Dispose` его освобождает, а запись может его растить.

```csharp
// владеющий нативный буфер (необходимо вызвать Dispose):
using var writer = BinaryPackWriter.Create(1024);

// пользовательская память (writer её не освобождает, бросает при переполнении):
byte* ptr = ...;
var writer = BinaryPackWriter.Create(ptr, capacity: 1024);

// кастомный аллокатор владеет буфером целиком:
// Realloc(state, oldPtr, oldCapacity, usedBytes, newCapacity): oldCapacity — размер старого блока,
// usedBytes — сколько из него должно выжить, копируйте min(usedBytes, newCapacity). Начальный буфер
// приходит как Realloc(state, null, 0, 0, capacity); Free вызывается в Dispose.
var alloc = PackAllocator.FromDelegates(MyRealloc, MyFree); // managed-методы маршалятся в Cdecl-переходники (thunks)
var writer = BinaryPackWriter.Create(1024, alloc);

// Unity: выбор аллокатора / обёртка над NativeArray
using var writer = BinaryPackWriter.Create(1024, Allocator.TempJob);
var writer = BinaryPackWriter.Create(nativeArray); // режим пользовательской памяти
var reader = BinaryPackReader.Create(nativeArray);
```

`using var` годится, пока writer используется только через свои методы-члены (`writer.WriteInt(...)` и т.п.). Он перестаёт компилироваться, как только вы вызываете ref-расширения `Write<T>`/`Read<T>` (CS1657 — using-переменную нельзя передать по `ref`) — см. явный `try`/`finally` в разделе «Быстрый старт» ниже.

`PackAllocator` хранит два `delegate* unmanaged[Cdecl]`-указателя (`Realloc`, `Free`). Три способа их получить:
- `PackAllocator.FromDelegates(realloc, free)` — маршалит managed-методы в Cdecl-переходники (thunks) (закреплены на время жизни процесса). Вызываемы из обычного C# и IL2CPP, но **не** Burst-оптимизированы — для долгоживущих аллокаторов. На IL2CPP целевые методы обязаны быть `static` и помечены `[AOT.MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]` / `FreeDelegate`.
- статические методы с `[UnmanagedCallersOnly(CallConvs = new[]{ typeof(CallConvCdecl) })]` + `&Method` (.NET 6+) — сырые нативные указатели без маршалинга, передаются в конструктор `new PackAllocator(realloc, free, state)`.
- `(delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, byte*>) BurstCompiler.CompileFunctionPointer<PackAllocator.ReallocDelegate>(MyRealloc).Value` (Unity + Burst) — Burst-скомпилированный нативный код, вызываемый изнутри Burst-джобов. `MyRealloc` обязан быть `static` и помечен `[BurstCompile]` и `[AOT.MonoPInvokeCallback(typeof(PackAllocator.ReallocDelegate))]`; каст обязателен, потому что `.Value` — это `IntPtr`.

Обе структуры содержат только unmanaged-поля, поэтому являются допустимыми полями Burst-джоба; примитивы, var-int'ы, unmanaged-спаны и рост буфера Burst-безопасны. Строки, коллекции, gzip и файловые API остаются managed-only удобствами.

**Встроенные аллокаторы и Burst.** Когда установлен пакет `com.unity.burst`, встроенные бэкенды (`PackMemory.Default(...)`, `PackArenaAllocator`) автоматически получают свои указатели через `BurstCompiler.CompileFunctionPointer`, поэтому writer на их основе растёт и освобождается **полностью внутри Burst-джоба**. Вызовите `PackMemory.WarmUp()` и `PackArenaAllocator.WarmUp()` (оба только для Unity и оба уже вызываются автоматически перед загрузкой первой сцены), чтобы вынести эту компиляцию из первого кадра — самопрогрев также срабатывает при первом обращении из managed-кода (включая Edit mode, EditMode-тесты и редакторские инструменты), так что это оптимизация, а не обязательное условие, за исключением самого первого вызова изнутри Burst-скомпилированного кода — там скомпилировать указатель на функцию нельзя, и прогрев нужно сделать из managed-кода заранее. `PackMemory.IsBurstCompatible(in writer)` (только Unity) сообщает, может ли буфер конкретного writer'а расти из Burst-кода, и распознаёт оба встроенных бэкенда. Без Burst (или на чистом .NET) встроенные бэкенды используют `[UnmanagedCallersOnly]`/маршалированные указатели — этого достаточно для роста на managed-стороне.

## PackArenaAllocator — арена в рамках кадра

Встроенная реализация `PackAllocator`: каждое выделение — это сдвиг указателя, `Dispose` отдельного writer'а **не освобождает ничего** (но делает сам writer непригодным — после него структуру использовать нельзя), а вся память возвращается разом через `Reset()` — обычно в конце кадра.

Поведение при переполнении выбирается через `PackArenaMode`:

- `SpillToDefault` (по умолчанию) — ёмкость остаётся фиксированной; выделения при переполнении по одному уходят в дефолтный нативный аллокатор. Spill-блоки связаны с ареной и освобождаются через `Reset()`/`Dispose()`, поэтому ничего не утекает.
- `AutoResize` — арена растёт: текущий чанк выводится из обращения (его содержимое остаётся валидным до `Reset()`), а сдвиг продолжается в новом чанке как минимум двойной ёмкости. `Reset()` сохраняет крупнейший чанк, поэтому арена сходится к реальному спросу на кадр и полностью перестаёт выделять память после первых кадров.

В обоих режимах каждый выданный указатель остаётся валидным до `Reset()`/`Dispose()` — ничто не перемещается и не освобождается в середине кадра.

```csharp
// арена живёт несколько кадров — без `using`, освобождайте её в конце жизненного цикла
var arena = PackArenaAllocator.Create(64 * 1024, PackArenaMode.AutoResize);

// каждый кадр:
var writer = arena.CreateWriter(1024); // начальный буфер и весь рост берутся из арены
writer.WriteInt(42);
Send(writer.AsReader());
// writer.Dispose() опционален и ничего не делает

arena.Reset(); // конец кадра: все выданные указатели становятся невалидными, память можно переиспользовать

// диагностика: arena.Capacity, arena.Used, arena.Overflowed

// конец жизненного цикла арены (например, выгрузка системы/сцены):
arena.Dispose();
```

Арена — это также аллокатор кадра общего назначения для unmanaged-данных, не только для writer'ов:

```csharp
ref var state = ref arena.Alloc<MyStruct>();          // одна структура, инициализированная значением по умолчанию
Span<float> temp = arena.AllocSpan<float>(1024);      // спан, обнулённый (clear: false чтобы пропустить)
MyStruct* raw = arena.AllocPtr<MyStruct>(16);         // сырой указатель на 16 элементов, обнулённый
byte* bytes = arena.Alloc(256);                       // сырые байты
// всё это живёт до arena.Reset() — без индивидуальных освобождений
```

Арена не потокобезопасна — используйте по одной на поток. После `Reset()` все ранее созданные writer'ы/reader'ы над памятью арены становятся невалидными.

**Правила времени жизни:**
- Копии структуры разделяют один и тот же указатель: ресайз одной копии инвалидирует остальные; передавайте writer'ы по `ref` и вызывайте Dispose ровно один раз.
- `default(BinaryPackWriter)` непригоден к использованию (null-буфер, нулевая ёмкость — первая запись бросит исключение).
- В Unity не позволяйте writer'у, созданному с `Allocator.Temp`, пережить кадр/задачу.

**Проверки памяти** (все сборки):
- При чтении каждая длина, пришедшая из самих данных — количество элементов, размер в байтах, размерности массивов, длины строк и блобов — сверяется с размером элемента и остатком буфера, и исключение бросается до того, как повреждённая длина дойдёт до сырого копирования. `ReadSpanUnmanaged` дополнительно проверяет, что destination вмещает записанное количество элементов.
- `FFS_PACK_DISABLE_MEMORY_CHECK` убирает эти проверки из сборки — для случаев, когда читаются только собственные данные и нужны последние такты на чтении. В сборках `DEBUG` / `FFS_PACK_ENABLE_DEBUG` проверки остаются независимо от этого дефайна.
- `FFS_PACK_DISABLE_MULTI_ARRAYS` убирает из сборки API для `T[,]` / `T[,,]`, и `UNITY_WEBGL` делает то же самое; у стратегий массивов эти члены остаются и бросают `NotSupportedException`.
- У распаковки нет собственного предела размера: небольшой gzip-вход может раздуться, пока буфер не займёт всю память. Ограничьте его параметром `maxDecompressedSize` у `WriteGzipData` / `WriteFromFile`.

**Debug-проверки** (сборки `DEBUG` / `FFS_PACK_ENABLE_DEBUG`, например сборка `FFS.StaticPack.Debug`):
- Каждое владеющее выделение отслеживается вместе со своим стек-трейсом. `BinaryPackLeakTracker.Count` / `Report()` показывают активные выделения. Утёкшие дополнительно сообщаются при domain reload в Unity/Mono и при завершении процесса (`Debug.LogError` в Unity, `Console.Error` в остальных случаях). Захват стек-трейса стоит примерно 200 микросекунд и несколько килобайт на выделение в редакторе — на время профилирования поставьте `BinaryPackLeakTracker.CaptureStackTraces = false`, само отслеживание при этом сохраняется. `BinaryPackLeakTracker.Clear()` забывает все сделанные до него выделения, и последующий `Dispose` этих буферов принимается, а не считается двойным. Сам тип остаётся вызываемым и в сборках без трекинга, где сообщает о пустом состоянии, так что оборачивать ссылки на него в `#if` не нужно. В Unity выделения дополнительно проходят через `UnsafeUtility.MallocTracked`, и попадают в Unity Native Leak Detection.
- Двойной `Dispose` через две копии одной структуры — или `Dispose` устаревшей копии после того, как другая копия выполнила ресайз буфера — бросает исключение вместо повреждения памяти.
- У `FFS.StaticPack` и `FFS.StaticPack.Debug` разный layout структур: отладочные сборки добавляют в writer и reader поле трекера. Не передавайте эти структуры между сборками, собранными против разных вариантов.

# Быстрый старт
```csharp
using FFS.Libraries.StaticPack;

BinaryPack.Init();

// владеющий нативный буфер, освобождается через Dispose
var writer = BinaryPackWriter.Create(1024);
try {
    // Базовые типы:
    writer.WriteInt(123);
    writer.WriteString16("Привет мир");
    writer.WriteArray(new short[] { 1, 2, 3 });
    writer.WriteDictionary(new Dictionary<string, DateTime> { { "сегодня", DateTime.Today }, { "завтра", DateTime.Today.AddDays(1) } });

    // Батч-запись (один вызов EnsureSize):
    writer.WriteFloat(1.0f, 2.0f, 3.0f);  // x, y, z
    writer.WriteInt(10, 20, 30, 40);       // 4 значения за раз

    // Запись через Span:
    Span<float> positions = stackalloc float[] { 1f, 2f, 3f };
    writer.WriteUnmanaged<float>(positions);

    // Пользовательские типы (регистрируются один раз, пишутся последними):
    BinaryPack.RegisterWithCollections<Person, StructPackArrayStrategy<Person>>(Person.Write, Person.Read);
    writer.Write(new Person { Name = "Alice", Age = 20, BirthDate = DateTime.Now });

    // Reader берётся только после всех записей: он фиксирует writer.Position как свой Size, а запись,
    // сделанная позже и вырастившая буфер (Resize), оставила бы его указывающим на освобождённую память.
    var reader = writer.AsReader(); // new BinaryPackReader(writer.Buffer, writer.Position, 0)

    var readInt = reader.ReadInt();                           // 123
    var readString = reader.ReadString16();                   // "Привет мир"
    var readArray = reader.ReadArray<short>();                // [ 1, 2, 3 ]
    var readDict = reader.ReadDictionary<string, DateTime>(); // { "сегодня", ... }, { "завтра", ... }

    // Батч-чтение:
    reader.ReadFloat(out var x, out var y, out var z);
    reader.ReadInt(out var a, out var b, out var c, out var d);

    // Чтение через Span:
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
Структура для записи бинарных данных

#### Управление буфером
```csharp
static BinaryPackWriter Create(uint capacity = 1024);                                 // владеющая нативная память
static BinaryPackWriter Create(byte* buffer, uint capacity, uint position = 0);       // пользовательская память
static BinaryPackWriter Create(uint capacity, PackAllocator allocator);  // аллокатор владеет буфером целиком
// Только Unity:
static BinaryPackWriter Create(uint capacity, Allocator allocator);
static BinaryPackWriter Create(NativeArray<byte> buffer, uint position = 0);

void EnsureSize(uint size);
byte[] CopyToBytes(bool gzip = false);
int CopyToBytes(ref byte[] result, bool gzip = false);
ReadOnlySpan<byte> AsSpan();                        // записанные байты, без копии
ReadOnlySpan<byte> AsSpan(uint offset, uint count);
int CopyTo(Span<byte> destination);
uint MakePoint(uint size);
BinaryPackReader AsReader();      // невладеющее представление, writer всё ещё нужно освобождать
BinaryPackReader AsReaderOwned(); // передаёт владение буфером reader'у и инвалидирует writer
bool IsCreated;  // false для default, после Dispose и после AsReaderOwned
bool Owned;      // держит собственный буфер
void Skip(uint bytesCount);
void Dispose(); // освобождает буфер в режиме owned / через PackAllocator.Free
```

#### Примитивы
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
void WriteVarInt(int value);   // 1-5 байт; отрицательные значения всегда занимают 5 байт и восстанавливаются корректно
void WriteVarShort(short value); // 1-2 байта, только неотрицательные - на отрицательном значении бросает исключение (не хватает бит для round-trip)
// WriteXAt(uint offset, X value) правит уже записанное значение на месте - есть для каждого типа выше
```

#### Батч-примитивы
```csharp
// Доступны для: byte, short, ushort, int, uint, float, long, ulong, double
// Один вызов EnsureSize для всех значений
void WriteInt(int v0, int v1);
void WriteInt(int v0, int v1, int v2);
void WriteInt(int v0, int v1, int v2, int v3);
void WriteFloat(float v0, float v1);
void WriteFloat(float v0, float v1, float v2);
void WriteFloat(float v0, float v1, float v2, float v3);
// ... аналогично для всех типов
```

#### Специальные типы
```csharp
void WriteNullable<T>(in T? value) where T : struct;
void WriteDateTime(DateTime value); // через DateTime.ToBinary()/FromBinary(): Kind и тики восстанавливаются побитно, Local пересчитывается в зону читающей машины
void WriteGuid(in Guid value);
void WriteString32(string value);
void WriteString16(string value); // макс. ushort.MaxValue байт UTF-8, бросает исключение, если строка не влезает
void WriteString8(string value);  // макс. byte.MaxValue байт UTF-8, бросает исключение, если строка не влезает
```

#### Коллекции
```csharp
void WriteArrayUnmanaged<T>(T[] value) where T : unmanaged; // memcpy
void WriteArray<T>(T[] value);                                // поэлементно
void WriteArrayUnmanaged<T>(T[,] value) where T : unmanaged;  // и T[,,]; WriteArrayUnmanaged2D/3D — синонимы
void WriteArray<T>(T[,] value);                               // и T[,,]; WriteArray2D/3D — синонимы
void WriteList<T>(List<T> value, int count = -1);
void WriteQueue<T>(Queue<T> value);
void WriteStack<T>(Stack<T> value);
void WriteLinkedList<T>(LinkedList<T> value);
void WriteHashSet<T>(HashSet<T> value);
void WriteDictionary<K, V>(Dictionary<K, V> value);
void WriteCollection(int idx, int count, WriteCollectionDelegate @delegate); // тот же layout, что у WriteArray<T>
```

#### Наборы unmanaged-значений
```csharp
// 1..8 значений независимых unmanaged-типов, один EnsureSize на всю группу:
void WriteUnmanaged<T1, T2>(in T1 v1, in T2 v2) where T1 : unmanaged where T2 : unmanaged;
void ReadUnmanaged<T1, T2>(out T1 v1, out T2 v2) where T1 : unmanaged where T2 : unmanaged;
// Варианты *Sized пишут перед группой её размер в байтах, поэтому reader, собранный с другим
// layout, может пропустить её, а не рассинхронизировать поток:
void WriteUnmanagedSized<T1, T2>(in T1 v1, in T2 v2) where T1 : unmanaged where T2 : unmanaged;
void ReadUnmanagedSized<T1, T2>(out T1 v1, out T2 v2) where T1 : unmanaged where T2 : unmanaged;
// Force* снимают ограничение unmanaged для обобщённого кода, который не может его выразить;
// отладочная сборка отвергает тип, содержащий ссылки:
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
void WriteUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged;  // прямой memcpy
void WriteUnmanaged<T>(ReadOnlyMemory<T> value) where T : unmanaged;
void WriteSpanUnmanaged<T>(ReadOnlySpan<T> value) where T : unmanaged; // с заголовками массива
void WriteSpan<T>(ReadOnlySpan<T> value); // поэлементно с заголовками
```

#### Работа с файлами
```csharp
void WriteFromFile(string filePath, bool gzip = false, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue); // дописывает содержимое файла, при gzip — распаковывая его
void FlushToFile(string filePath, bool gzip = false, bool flushToDisk = false); // пишет буфер в файл, при gzip — сжимая его
void WriteGzipData(byte[] data, uint bufferSize = 4096, uint maxDecompressedSize = uint.MaxValue); // дописывает распакованные gzip-байты
```

### BinaryPackReader
Структура для чтения бинарных данных

#### Создание
```csharp
BinaryPackReader(byte* buffer, uint size, uint position);                    // пользовательская память
BinaryPackReader(NativeArray<byte> buffer, uint size, uint position);        // только Unity
static BinaryPackReader Create(byte* buffer, uint size, uint position = 0);  // пользовательская память
static BinaryPackReader Create(NativeArray<byte> buffer, uint position = 0); // только Unity
static BinaryPackReader AllocAndFillFromStream(Stream source, uint size);    // владеющий нативный буфер
static BinaryPackReader AllocAndFillFromStream(Stream source, uint size, PackAllocator allocator);
static BinaryPackReader AllocAndFillFromBytes(byte[] data, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromFile(string filePath, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromBytes(byte[] data, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromFile(string filePath, bool gzip, int headerSize = 0, TotalSizeParser parseTotalSize = null);
static BinaryPackReader AllocAndFillFromSpan(ReadOnlySpan<byte> data); // владеющая нативная копия, без детекта gzip
static bool IsGzip(ReadOnlySpan<byte> data);
BinaryPackWriter AsWriter();        // невладеющий writer над [0, Size), позиция Size
BinaryPackWriter AsWriterCompact(); // переносит непрочитанный хвост в начало и забирает владение
BinaryPackReader AsReader(uint position);
bool IsCreated;  // false для default, после Dispose и после AsWriterCompact
bool Owned;      // держит собственный буфер
bool HasNext();  bool HasNext(uint bytesCount);
void SkipNext(); void SkipNext(uint bytesCount);
bool TryReadArrayHeader(out int count); // false для сохранённого null; оставляет Position на первом элементе
ArraySegment<T> ReadArrayPooled<T>(out ArrayPoolHandle<T> poolHandle); // освобождать через poolHandle.Return()
void Dispose(); // освобождает буфер, когда Owned
```

`ReadArrayUnmanagedAsSpan<T>()` возвращает представление поверх собственного буфера reader'а, то есть не
копирует и не аллоцирует. Полезные данные лежат по произвольному смещению, поэтому элементы, как правило, не
выровнены по `sizeof(T)` — это безопасно для однобайтовых элементов везде и для скалярных структур на x64 и
ARM64. Типы, которым нужно настоящее выравнивание, включая SIMD-векторы, читаются перегрузкой с
`PackArenaAllocator`: она копирует в 16-выровненную память арены и тоже ничего не аллоцирует в managed-куче.

Перегрузки `AllocAndFill*` без флага `gzip` определяют gzip по magic-байтам RFC 1952 `0x1F 0x8B` в начале данных, поэтому
обычные данные, первые два байта которых равны `1F 8B`, принимаются за gzip-поток: такие данные нужно читать через
перегрузку с явным флагом `gzip`. Для gzip-источника `headerSize` (1..256) и `parseTotalSize` обязательны во всех
сборках, а результат `parseTotalSize` больше предела распаковки DEFLATE отвергается до выделения памяти.

#### Примитивы
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
// Варианты TryRead* возвращают bool: в том числе TryReadVarInt / TryReadVarShort / TryReadString8 / 16 / 32
```

#### Батч-примитивы
```csharp
// Доступны для: byte, short, ushort, int, uint, float, long, ulong, double
void ReadInt(out int v0, out int v1);
void ReadInt(out int v0, out int v1, out int v2);
void ReadInt(out int v0, out int v1, out int v2, out int v3);
void ReadFloat(out float v0, out float v1, out float v2);
// ... аналогично для всех типов
```

#### Span / Memory
```csharp
void ReadBytes(Span<byte> destination);
ReadOnlySpan<byte> ReadBytesAsSpan(uint count);     // zero-copy
ReadOnlySpan<byte> RemainingAsSpan();
void ReadUnmanaged<T>(Span<T> destination) where T : unmanaged;      // прямой memcpy
int ReadSpanUnmanaged<T>(Span<T> destination) where T : unmanaged;   // с заголовками массива, -1 для сохранённого null
int ReadSpan<T>(Span<T> destination);                                // поэлементно, -1 для сохранённого null
ReadOnlySpan<T> ReadArrayUnmanagedAsSpan<T>() where T : unmanaged;   // zero-copy поверх буфера
Span<T> ReadArrayUnmanagedAsSpan<T>(in PackArenaAllocator arena) where T : unmanaged; // 16-выровненная копия в арену
```

#### Коллекции
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
// ReadX<T>(ref X result) варианты для переиспользования: возвращают int (число прочитанных элементов, -1 при null-флаге - result в этом случае не трогается)
// SkipArray(), SkipList() и т.д. для пропуска
```

### BinaryPack
Центральный реестр сериализаторов

```csharp
static void Init();
static void RegisterWithCollections<T, S>(BinaryWriter<T> writer, BinaryReader<T> reader, S strategy = default)
    where S : struct, IPackArrayStrategy<T>;
static void Register<T>(BinaryWriter<T> writer, BinaryReader<T> reader);
static T Read<T>(this ref BinaryPackReader reader);
static void Write<T>(this ref BinaryPackWriter writer, in T value);
static bool IsRegistered<T>();
static int SizeOf<T>();

// круговые операции одним вызовом, каждая создаёт и освобождает собственный writer/reader:
static byte[] WriteToBytes<T>(T value, bool gzip = false, uint byteSizeHint = 4096);
static int WriteToBytes<T>(T value, ref byte[] result, bool gzip = false, uint byteSizeHint = 4096);
static void WriteToFile<T>(T value, string filePath, bool gzip = false, bool flushToDisk = false, uint byteSizeHint = 4096);
static T ReadFromBytes<T>(byte[] bytes, bool gzip = false, uint byteSizeHint = 4096);
static T ReadFromBytes<T>(byte[] bytes, uint position, uint count, bool gzip = false, uint byteSizeHint = 4096);
static T ReadFromFile<T>(string filePath, bool gzip = false, uint byteSizeHint = 4096);

// напрямую в память вызывающего и обратно, без единой аллокации:
static int WriteToSpan<T>(T value, Span<byte> destination);
static T ReadFromSpan<T>(ReadOnlySpan<byte> bytes);
```

`RegisterWithCollections<T, S>` регистрирует `T`, а также `T?`, `T[]`, `T[,]`, `T[,,]`, `T[][]`, `T[][][]`,
`List<T>`, `LinkedList<T>`, `Queue<T>`, `Stack<T>`, `HashSet<T>`. Остальное — `Dictionary<K, V>`, `T?[]`,
`List<T?>`, `List<T[]>` — требует собственного `Register<T>`, иначе `Write<T>`/`Read<T>` не найдут сериализатор;
прямые вызовы `WriteDictionary`/`ReadDictionary` и `WriteArray`/`ReadArray` работают без регистрации.

### Стратегии сериализации массивов
1. `UnmanagedPackArrayStrategy<T>` - для `unmanaged` типов, прямое копирование памяти
2. `StructPackArrayStrategy<T>` - для структур, поэлементная сериализация
3. `ClassPackArrayStrategy<T>` - для классов, поэлементная сериализация

## Регистрация пользовательских типов

### Пример: Простая структура
```csharp
public struct Vector3 {
    public float X, Y, Z;
}

BinaryPack.RegisterWithCollections(
    (ref BinaryPackWriter writer, in Vector3 v) => {
        writer.WriteFloat(v.X, v.Y, v.Z); // батч-запись
    },
    (ref BinaryPackReader reader) => {
        reader.ReadFloat(out var x, out var y, out var z); // батч-чтение
        return new Vector3 { X = x, Y = y, Z = z };
    },
    new UnmanagedPackArrayStrategy<Vector3>()
);
```

### Пример: Сложный вложенный тип
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

# Лицензия
[MIT license](./LICENSE.md)
