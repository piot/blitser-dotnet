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
  
## Custom BitSerializer

Mark your custom type serialization class with `BitSerializer`.

```csharp
[BitSerializer]
public static class CustomHealthSerializer
{
    public static void Write(IBitWriter writer, CustomHealth health)
    {
        writer.WriteBits(health.Value, 5);
    }   
    public static CustomHealth Read(IBitReader reader)
    {
        new(reader.ReadBits(5));
    }
}

```
