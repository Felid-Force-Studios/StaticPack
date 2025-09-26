![Version](https://img.shields.io/badge/version-1.0.3-blue.svg?style=for-the-badge)

# Static Pack - C# Simple binary serialization library
- Lightweight
- Performance
- No dependencies
- No reflections
- No codegen
- No scheme
- Compatible with Unity and other C# engines

#### Limitations and Features:
> - Polymorphic types require custom implementation
> - Cyclic references require custom implementation

## Table of Contents
* [Installation](#installation)
* [Concept](#concept)
* [Quick start](#quick-start)
* [License](#license)

# Installation
* ### As source code
  From the release page or as an archive from the branch. In the `master` branch there is a stable tested version
* ### Installation for Unity
  git module `https://github.com/Felid-Force-Studios/StaticPack.git` in Unity PackageManager  
  or adding it to `Packages/manifest.json` `"com.felid-force-studios.static-pack": "https://github.com/Felid-Force-Studios/StaticPack.git"`

# Concept
The library provides high-performance tools for binary serialization with support for:
> - Primitive types and arrays
> - Multidimensional arrays
> - Collections (lists, queues, dictionaries, etc.)
> - Custom types
> - Direct file reading/writing
> - Data compression

# Quick start
```csharp
using FFS.Libraries.StaticPack;

BinaryPack.Init();

var buffer = new byte[1024];
var writer = BinaryPackWriter.Create(buffer);

// or create pooled writer
using var writer = BinaryPackWriter.CreateFromPool(1024);

// Base types:
writer.WriteInt(123);
writer.WriteString16("Hello world");
writer.WriteArray(new short[] { 1, 2, 3 });
writer.WriteDictionary(new Dictionary<string, DateTime> { { "today", DateTime.Today }, { "tomorrow", DateTime.Today.AddDays(1) } });

var reader = writer.AsReader(); // new BinaryPackReader(buffer: buffer, size: writer.Position, position: 0)

var readInt = reader.ReadInt();                           // 123
var readString = reader.ReadString16();                   // "Hello world"
var readArray = reader.ReadArray<short>();                // [ 1, 2, 3 ]
var readDict = reader.ReadDictionary<string, DateTime>(); // { "today", ... }, { "tomorrow", ... }

// Custom types:
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

BinaryPack.RegisterWithCollections<Person, StructPackArrayStrategy<Person>>(Person.Write, Person.Read);

writer.Write(new Person { Name = "Alice", Age = 20, BirthDate = DateTime.Now });

var person = reader.Read<Person>();
```


## Main components

### BinaryPackWriter
Structure for writing binary data

**Methods:**

#### Buffer management
```csharp
// Guarantee an available size
void EnsureSize(uint size);

// Reserving a position with position return
uint MakePoint(uint size);

// Copy the written data into a new byte array (optionally compress)
byte[] CopyToBytes(bool gzip = false);

// Copy the written data into a existing byte array (optionally compress) and return len
int CopyToBytes(ref byte[] result, bool gzip = false);
```

#### Data writing
```csharp
// Primitives
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

// Variant formats
void WriteVarInt(int value); // writes from 1-5 bytes depending on the number (positive numbers only)
void WriteVarShort(short value); // writes from 1-2 bytes depending on the number (positive numbers only)

// Special types
void WriteNullable<T>(in T? value) where T : struct;
void WriteDateTime(DateTime value);
void WriteGuid(in Guid value);

// Strings
void WriteString32(string value);
void WriteString16(string value);  // String length is encoded in 2 bytes, maximum byte size ushort.MaxValue
void WriteString8(string value);   // String length is encoded by 1 byte, maximum byte size byte.MaxValue
```

#### Collections
```csharp
// Arrays
void WriteArrayUnmanaged<T>(T[] value) where T : unmanaged; // Writes all of array elements to memory via memory byte copying
void WriteArrayUnmanaged<T>(T[] value, int idx, int count) where T : unmanaged; // Writes the “count” of array elements from "idx" to memory via memory byte copying
void WriteArray<T>(T[] value); // Writes all of array elements to memory per element
void WriteArray<T>(T[] value, int idx, int count); // Writes the “count” of array elements from "idx" to memory per element

// Multi-dimensional arrays
void WriteArrayUnmanaged<T>(T[,] value) where T : unmanaged; // Writes array to memory via memory byte copying
void WriteArray<T>(T[,] value); // Writes array to memory per element
void WriteArrayUnmanaged<T>(T[,,] value) where T : unmanaged; // Writes array to memory via memory byte copying
void WriteArray<T>(T[,,] value); // Writes array to memory per element

// Collections
void WriteList<T>(List<T> value, int count = -1); // Writes the “count” (all at -1) of list elements to memory per element
void WriteQueue<T>(Queue<T> value);
void WriteStack<T>(Stack<T> value);
void WriteLinkedList<T>(LinkedList<T> value);
void WriteHashSet<T>(HashSet<T> value);
void WriteDictionary<K, V>(Dictionary<K, V> value);
```

#### File handling
```csharp
// Reading from file to buffer
void WriteFromFile(string filePath, bool gzip = false, uint bufferSize = 4096);

// Writing buffer to file
void FlushToFile(string filePath, bool gzip = false, bool flushToDisk = false, CompressionLevel level = CompressionLevel.Fastest);
void FlushToFile(string filePath, uint offset, uint count, bool gzip = false, int bufferSize = 4096, bool flushToDisk = false, CompressionLevel level = CompressionLevel.Fastest);
```

### BinaryPackReader
Structure for reading binary data

**Methods:**

#### Basic operations
```csharp
// Checking data availability
bool HasNext();
bool HasNext(uint bytesCount);

// Data skipping
void SkipNext();
void SkipNext(uint bytesCount);

// Reading the null flag (true = null)
bool ReadNullFlag();
```

#### Data reading
```csharp
// Primitives
byte ReadByte();
sbyte ReadSByte();
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

// Variant formats
int ReadVarInt();
short ReadVarShort();

// Special types
Guid ReadGuid();
DateTime ReadDateTime();
T? ReadNullable<T>() where T : struct;

// Strings
string ReadString32();
string ReadString16();
string ReadString8(); 
```

#### Collections
```csharp
// Arrays
T[] ReadArrayUnmanaged<T>() where T : unmanaged;
void ReadArrayUnmanaged<T>(ref T[] result);
T[] ReadArray<T>();
void ReadArray<T>(ref T[] result);

// Multidimensional arrays
T[,] ReadArray2DUnmanaged<T>() where T : unmanaged;
T[,,] ReadArray3DUnmanaged<T>() where T : unmanaged;
T[,] ReadArray2D<T>();
T[,,] ReadArray3D<T>();

// Collections
List<T> ReadList<T>();
void ReadList<T>(ref List<T> result);
Queue<T> ReadQueue<T>();
Stack<T> ReadStack<T>();
LinkedList<T> ReadLinkedList<T>();
HashSet<T> ReadHashSet<T>();
Dictionary<K, V> ReadDictionary<K, V>();

// Skip (Skips the required number of bytes)
void SkipArray();
void SkipArray2();
void SkipArray3();
void SkipList();
// ... and others
```

### BinaryPack
Central registry of serializers

**Methods:**
```csharp
// Initialization of context
static void Init();

// Registration of custom serializers with base collection types
static void RegisterWithCollections<T, S>(BinaryWriter<T> writer, BinaryReader<T> reader, S strategy = default)
    where S : IPackArrayStrategy<T>;

// Registration of custom serializers without base collection types
static void Register<T>(BinaryWriter<T> writer, BinaryReader<T> reader);

// Extension methods for Reader/Writer
static T Read<T>(this ref BinaryPackReader reader);
static void Write<T>(this ref BinaryPackWriter writer, in T value);
```

### Array serialization strategies (PackArrayStrategy)
Optimized handlers for different types of arrays:

**Types of strategies:**
1. `UnmanagedPackArrayStrategy<T>` - for `unmanaged` types
    - Uses direct memory copying
    - Maximum performance

2. `StructPackArrayStrategy<T>` - for structures
    - Element-by-element serialization
    - Supports nullable structures

3. `ClassPackArrayStrategy<T>` - for classes
    - Element-by-element serialization

## Registration of custom types

### Example 1: Simple structure
```csharp
public struct Vector3 {
    public float X, Y, Z;
}

// Registration
BinaryPack.RegisterWithCollections(
    (ref BinaryPackWriter writer, in Vector3 v) => {
        writer.WriteFloat(v.X);
        writer.WriteFloat(v.Y);
        writer.WriteFloat(v.Z);
    },
    (ref BinaryPackReader reader) => new Vector3 {
        X = reader.ReadFloat(),
        Y = reader.ReadFloat(),
        Z = reader.ReadFloat()
    },
    new UnmanagedPackArrayStrategy<Vector3>()  // Using an unmanaged strategy
);
```

### Example 2: Complex nested type
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
    
    // Registration (The order of registration is not important)
    public static void RegisterAll() {
        RegisterVector3Serializers();
        RegisterGameStateSerializers();
        RegisterPlayerSerializers();
        RegisterItemSerializers();
    }
    
    private static void RegisterGameStateSerializers() {
        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in GameState state) => 
                writer.WriteInt(state.Level);
                writer.WriteArray(state.Players);
                writer.WriteDictionary(state.Inventory);
            },
            (ref BinaryPackReader reader) => new GameState {
                Level = reader.ReadInt(),
                Players = reader.ReadArray<Player>(),
                Inventory = reader.ReadDictionary<int, Item>()
            },
            new ClassPackArrayStrategy<GameState>()
        );
    }

    private static void RegisterPlayerSerializers() {
        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in Player player) => {
                writer.WriteString16(player.Name);
                writer.Write(player.Position); // Uses a registered Vector3 serializer
            },
            (ref BinaryPackReader reader) => new Player {
                Name = reader.ReadString16(),
                Position = reader.Read<Vector3>() // Uses a registered Vector3 serializer
            },
            new StructPackArrayStrategy<Player>()
        );
    }
    
    private static void RegisterItemSerializers() {
        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in Item item) => {
                writer.WriteInt(item.Id);
                writer.WriteFloat(item.Durability);
            },
            (ref BinaryPackReader reader) => new Item {
                Id = reader.ReadInt(),
                Durability = reader.ReadFloat()
            },
            new UnmanagedPackArrayStrategy<Item>()
        );
    }
    
    private static void RegisterVector3Serializers() {
        BinaryPack.RegisterWithCollections(
            (ref BinaryPackWriter writer, in Vector3 vec) => {
                writer.WriteFloat(vec.X);
                writer.WriteFloat(vec.Y);
                writer.WriteFloat(vec.Z);
            },
            (ref BinaryPackReader reader) => new Vector3 {
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat()
            },
            new UnmanagedPackArrayStrategy<Vector3>()
        );
    }
}
```

# License
[MIT license](./LICENSE.md)