/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using Mono.CecilEx;
using Mono.CecilEx.Cil;
using Piot.Clog;

namespace Piot.Blitser.Generator
{
    public static class DataTypeSerialization
    {
        static int CountToBits(uint count)
        {
            int i;

            if (count == 0)
            {
                return 0;
            }

            count--;
            for (i = -1; count != 0; i++)
                count >>= 1;

            return i == -1 ? 0 + 1 : i + 1;
        }
        
        static int BitCountFromType(TypeReference fieldType, ILog log)
        {
            // IBitReader.ReadBits argument
            var bitCount = 0;
            if (fieldType.IsPrimitive)
            {
                var fieldTypeName = fieldType.Name;
                switch (fieldTypeName)
                {
                    case nameof(Boolean):
                        bitCount = 1;
                        break;
                    case nameof(Byte):
                        bitCount = 8;
                        break;
                    case nameof(SByte):
                        bitCount = 8;
                        break;
                    case nameof(UInt16):
                        bitCount = 16;
                        break;
                    case nameof(Int16):
                        bitCount = 16;
                        break;
                    case nameof(UInt32):
                        bitCount = 32;
                        break;
                    case nameof(Int32):
                        bitCount = 32;
                        break;
                    default:
                        log.Error("Unknown {Primitive} type", fieldTypeName);
                        throw new($"Unknown primitive type {fieldTypeName}");
                }
            }
            else if (fieldType.Resolve().IsEnum)
            {
                var resolved = fieldType.Resolve();
                bitCount = CountToBits((uint)resolved.Fields.Count);
            }
            else
            {
                throw new Exception($"Unknown type {fieldType.FullName}");
            }

            return bitCount;
        }

        public static void EmitCallMethodWithBitCount(ILProcessor processor, MethodReference bitsMethod, int bitCount)
        {
            processor.Emit(OpCodes.Ldc_I4, bitCount);
            processor.Emit(OpCodes.Callvirt, bitsMethod);
        }

        public static void EmitBitCountDependingOnType(ILProcessor processor, TypeReference fieldType, MethodReference bitsMethod, ILog log)
        {
            var bitCount = BitCountFromType(fieldType, log);
            EmitCallMethodWithBitCount(processor, bitsMethod, bitCount);
        }
    }
}