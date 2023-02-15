/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.CecilEx;
using Mono.CecilEx.Cil;
using Piot.Clog;
using Piot.Flood;

namespace Piot.Blitser.Generator
{
    public class DataTypeWriter
    {
        ModuleDefinition moduleDefinition;
        Func<string, MethodInfo> userProvidedWriteSerializer;
        readonly MethodReference writeBitsMethod;
        Dictionary<string, MethodDefinition> scannedWriteBitSerializers;

        public DataTypeWriter(ModuleDefinition moduleDefinition, Func<string, MethodInfo> userProvidedWriteSerializer, MethodReference writeBitsMethod)
        {
            this.moduleDefinition = moduleDefinition;
            this.userProvidedWriteSerializer = userProvidedWriteSerializer;
            this.writeBitsMethod = writeBitsMethod;
            scannedWriteBitSerializers = ScanForUserDefinedStaticBitWriterMethods();
        }

        public MethodReference WriteBitsMethod => writeBitsMethod;

        Dictionary<string, MethodDefinition> ScanForUserDefinedStaticBitWriterMethods()
        {
            var lookup = new Dictionary<string, MethodDefinition>();
            foreach (var module in moduleDefinition.Assembly.Modules)
            {
                foreach (var type in module.Types)
                {
                    if (!ScannerHelper.HasAttribute<BitSerializerAttribute>(type))
                    {
                        continue;
                    }

                    foreach (var method in type.Methods)
                    {
                        //method.IsSpecialName
                        if (method.IsStatic && method.IsPublic && method.Name == "Write" && method.Parameters.Count == 2 && method.Parameters[0].ParameterType.FullName == typeof(IBitWriter).FullName)
                        {
                            lookup.Add(method.Parameters[1].ParameterType.FullName, method);
                        }
                    }
                }
            }

            return lookup;
        }

        MethodReference FindStaticBitWriterMethod(TypeReference dataTypeReference)
        {
            var internalWriteMethod = userProvidedWriteSerializer(dataTypeReference.FullName);
            if (internalWriteMethod is not null)
            {
                return moduleDefinition.ImportReference(internalWriteMethod);
            }

            var userWriteMethod = scannedWriteBitSerializers.TryGetValue(dataTypeReference.FullName, out var foundValue);
            if (!userWriteMethod)
            {
                throw new($"couldn't find user provided bit writer for {dataTypeReference.FullName}");
            }

            return foundValue!;
        }

        void EmitWriteBits(ILProcessor processor, TypeReference fieldType, ILog log)
        {
            DataTypeSerialization.EmitBitCountDependingOnType(processor, fieldType, writeBitsMethod, log);
        }

        public void EmitDataTypeWriterFromStack(ILProcessor processor, TypeReference typeToWrite, ILog log)
        {
            if (TypeCheck.IsAllowedBlittablePrimitive(typeToWrite))
            {
                if (TypeCheck.IsBool(typeToWrite))
                {
                    var trueLabel = processor.Create(OpCodes.Ldc_I4_1);
                    processor.Emit(OpCodes.Brtrue, trueLabel);
                    processor.Emit(OpCodes.Ldc_I4_0); // False value
                    var endLabel = processor.Create(OpCodes.Nop);
                    processor.Emit(OpCodes.Br_S, endLabel);
                    processor.Append(trueLabel);
                    processor.Append(endLabel);
                }

                EmitWriteBits(processor, typeToWrite, log);
            }
            else
            {
                var foundMethod = FindStaticBitWriterMethod(typeToWrite);
                if (foundMethod is null)
                {
                    throw new($"couldn't find a bit serializer for {typeToWrite.FullName}");
                }

                processor.Emit(OpCodes.Call, foundMethod);
            }
        }
    }
}