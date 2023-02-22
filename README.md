# Blitser for .NET

_Copyright Peter Bjorklund, All rights reserved._

Blittable struct serialization and deserialization.

## General

* Generates CIL methods for serialize, deserialize and diff. It supports .NET blittable primitives as well as user defined blittable structs and enums.

### Limitations

* A struct must have less than or equal to 32 public fields.
* It is strongly recommended that all fields are public.
* A field can be of a custom small blittable type, but that custom struct in turn, should not contain custom types.

## Usage

Structs must be blittable and have one of the following attributes:

* `Logic`. These are used for data that is from the authoritative host to a client that is predicting that entity.
* `Ghost`.  Structs sent from Authoritative Host to clients for entities that are not predicted.
* `Input`. Structs that are sent from a client to the Authoritative Host.
* `ShortLivedEvent`. Used for structs that are sent as events from Host to Client, but used for components that either just lives for one tick (simulation frame), or values that are only set for one tick, but we want to increase the chances that they are not missed.

### DataWriter and DataReader

Use DataWriter and DataReader to serialize any blittable struct to and from a bitstream.

```csharp
public static class DataStreamWriter
{
    public static void Write<T>(T data, IBitWriter bitWriter) where T : struct;
    public static void Write<T>(in T data, IBitWriter bitWriter, uint mask) where T : struct;
}
```

```csharp
public static class DataStreamReader
{
    public static T CreateAndRead<T>(IBitReader bitReader) where T : struct;
    public static uint ReadMask<T>(IBitReader bitReader, ref T data) where T : struct;
}
```

### Unique Data Type ID

To get the unique data type ID for a type use:

```csharp
    public static class DataIdFetcher
    {
        public static ushort Id<T>() where T : struct;
    }
```

### Difference between two blittable structs

```csharp
    public static class DataDiff
    {
        public static uint Diff<T>(in T a, in T b) where T : struct;
    }
```

### Data Stream Receiver

DataStreamReceiver is useful for deserializing a bitstream. You need to have own deserialization of `entityId` and `dataTypeId` before calling any of these methods.

A class implementing `IDataReceiver` will get the calls with correct types.

```csharp
public interface IDataReceiver
{
    public void ReceiveNew<T>(uint entityId, T data) where T : struct;
    public void Update<T>(uint mask, uint entityId, T data) where T : struct;
    public void DestroyComponent<T>(uint entityId) where T : struct;

    public T GrabOrCreate<T>(uint entityId) where T : struct; // This is used by the ReceiveUpdate() method.
}
```

```csharp
public static class DataStreamReceiver
{
    public static void ReceiveNew(
        IBitReader reader,
        uint entityId,
        uint dataTypeId,
        IDataReceiver receiver);

    public static void ReceiveUpdate(
        IBitReader reader,
        uint entityId,
        uint dataTypeId,
        IDataReceiver receiver);

    public static void ReceiveDestroy(uint entityId, uint dataTypeId, IDataReceiver dataReceiver);
}

```

## Example Generated CIL code

Given the following example structs:

```csharp
public enum TestEnum
{
    Idle,
    Running,
}

public struct Position3
{
    public short x;
    public short y;
    public short z;
}


[Ghost]
public struct TestData
{
    public int counter;
    public TestEnum ability;
    public Position3 position;
}
```

It generates the following CIL (shown as CIL-to-C# converted code to be easier to read):

### Complete Serialization

```csharp
public static void SerializeFull_TestData(IBitWriter writer, TestData data)
{
    writer.WriteBits((uint) data.counter, 32);
    writer.WriteBits((uint) data.ability, 2);
    Position3Serializer.Write(writer, data.position);
}

public static TestData Deserialize_TestData([In] IBitReader reader)
{
    TestData testData = new TestData();
    testData.counter = (int) reader.ReadBits(32);
    testData.ability = (TestEnum) reader.ReadBits(2);
    Position3Serializer.Read(reader, out testData.position);
    return testData;
}

public static void DeserializeAllRef_TestData([In] IBitReader reader, ref TestData data)
{
    data.counter = (int) reader.ReadBits(32);
    data.ability = (TestEnum) reader.ReadBits(2);
    Position3Serializer.Read(reader, out data.position);
}

```

### Serialization with a mask

```csharp

public static uint DeserializeMaskRef_TestData(IBitReader reader, ref TestData data)
{
    int num = (int) reader.ReadBits(3);

    if ((num & 1) != 0)
        data.counter = (int) reader.ReadBits(32);

    if ((num & 2) != 0)
        data.ability = (TestEnum) reader.ReadBits(2);

    if ((num & 4) == 0)
        return (uint) num;

    Position3Serializer.Read(reader, out data.position);

    return (uint) num;
}

public static void SerializeMaskRef_TestData(IBitWriter writer, TestData data, uint fieldMask)
{
    writer.WriteBits(fieldMask, 3);

    if (((int) fieldMask & 1) != 0)
        writer.WriteBits((uint) data.counter, 32);

    if (((int) fieldMask & 2) != 0)
        writer.WriteBits((uint) data.ability, 2);

    if (((int) fieldMask & 4) == 0)
        return;

    Position3Serializer.Write(writer, data.position);
}
```

### DataStreamReceiver - Receive New

```csharp
public static void DataReceiveNew(
    IBitReader reader,
    uint entityId,
    uint dataTypeId,
    IDataReceiver receiver)
{
    switch (dataTypeId)
    {
    case 1:
        receiver.ReceiveNew<TestLogic>(entityId, DataStreamReader.CreateAndRead<TestLogic>(reader));
        break;
    case 2:
        receiver.ReceiveNew<TestData>(entityId, DataStreamReader.CreateAndRead<TestData>(reader));
        break;
    case 3:
        receiver.ReceiveNew<TestInput>(entityId, DataStreamReader.CreateAndRead<TestInput>(reader));
        break;
    }
}
```

### DataStreamReceiver - Receive Destroy

```csharp
public static void DataReceiveDestroy(uint entityId, uint dataTypeId, IDataReceiver receiver)
{
    switch (dataTypeId)
    {
    case 1:
        receiver.DestroyComponent<TestLogic>(entityId);
        break;
    case 2:
        receiver.DestroyComponent<TestData>(entityId);
        break;
    case 3:
        receiver.DestroyComponent<TestInput>(entityId);
        break;
    }
}
```

### DataStreamReceiver - Receive Update

```csharp
public static void DataReceiveUpdate(
    IBitReader reader,
    uint entityId,
    uint dataTypeId,
    IDataReceiver receiver)
{
    switch (dataTypeId)
    {
    case 1:
        TestLogic data1 = receiver.GrabOrCreate<TestLogic>(entityId);
        receiver.Update<TestLogic>(DataStreamReader.ReadMask<TestLogic>(reader, ref data1), entityId, data1);
        break;
    case 2:
        TestData data2 = receiver.GrabOrCreate<TestData>(entityId);
        receiver.Update<TestData>(DataStreamReader.ReadMask<TestData>(reader, ref data2), entityId, data2);
        break;
    case 3:
        TestInput data3 = receiver.GrabOrCreate<TestInput>(entityId);
        receiver.Update<TestInput>(DataStreamReader.ReadMask<TestInput>(reader, ref data3), entityId, data3);
        break;
    }
}

```

## Custom Type BitSerializer

Mark your custom type serialization class with `BitSerializer`.

```csharp
[BitSerializer]
public static class CustomHealthSerializer
{
    public static void Write(IBitWriter writer, in CustomHealth health)
    {
        writer.WriteBits(health.Value, 5);
    }

    public static void Read(IBitReader reader, out CustomHealth health)
    {
        health = new(reader.ReadBits(5));
    }
}

```
