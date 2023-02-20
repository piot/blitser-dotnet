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
    public class DataTypeReader
    {
        ModuleDefinition moduleDefinition;
        readonly MethodReference readBitsMethod;
        Func<string, MethodInfo> readSerializer;


        public DataTypeReader(ModuleDefinition moduleDefinition, MethodReference readBitsMethod, Func<string, MethodInfo> readSerializer)
        {
            this.moduleDefinition = moduleDefinition;
            this.readBitsMethod = readBitsMethod;
            this.readSerializer = readSerializer;
        }

        public MethodReference ReadBitsMethod => readBitsMethod;

        void EmitReadBits(ILProcessor processor, TypeReference fieldType, ILog log)
        {
            DataTypeSerialization.EmitBitCountDependingOnType(processor, fieldType, readBitsMethod, log);
        }

        MethodDefinition? CheckIfUserDefinedStaticBitReaderMethodExists(TypeReference dataTypeReference)
        {
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
                        if (method.IsStatic && method.IsPublic && method.Name == "Read" && method.Parameters.Count == 2 && method.Parameters[0].ParameterType.FullName == typeof(IBitReader).FullName &&
                            method.Parameters[1].ParameterType.FullName == dataTypeReference.FullName)
                        {
                            return method;
                        }
                    }
                }
            }

            return null;
        }

        MethodDefinition? CheckIfUserDefinedStaticBitReaderOnStackMethodExists(TypeReference dataTypeReference)
        {
            foreach (var type in moduleDefinition.Types)
            {
                    if (!ScannerHelper.HasAttribute<BitSerializerAttribute>(type))
                    {
                        continue;
                    }

                    foreach (var method in type.Methods)
                    {
                        //method.IsSpecialName
                        if (method.IsStatic && method.IsPublic && method.Name == "Read" && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == typeof(IBitReader).FullName &&
                            method.ReturnType.FullName == dataTypeReference.FullName)
                        {
                            return method;
                        }
                    }
            }

            return null;
        }

        static void EmitConversionFromUInt32DependingOnType(ILProcessor processor, TypeReference fieldType, ILog log)
        {
            if (!fieldType.IsPrimitive)
            {
                return;
            }

            var fieldTypeName = fieldType.Name;
            switch (fieldTypeName)
            {
                case nameof(Byte):
                    processor.Emit(OpCodes.Conv_U1);
                    break;
                case nameof(SByte):
                    processor.Emit(OpCodes.Conv_U1);
                    break;
                case nameof(Boolean):
                    processor.Emit(OpCodes.Ldc_I4_0); // Compare with zero
                    processor.Emit(OpCodes.Cgt_Un);
                    break;
                case nameof(UInt16):
                    break;
                case nameof(Int16):
                    break;
                case nameof(UInt32):
                    break;
                case nameof(Int32):
                    break;
                default:
                    log.Error("Unknown {Primitive} type", fieldTypeName);
                    throw new($"Unknown primitive type {fieldTypeName}");
            }
        }

        MethodReference FindStaticBitReaderMethod(TypeReference dataTypeReference)
        {
            var internalWriteMethod = readSerializer(dataTypeReference.FullName);
            if (internalWriteMethod is not null)
            {
                return moduleDefinition.ImportReference(internalWriteMethod);
            }

            var userWriteMethod = CheckIfUserDefinedStaticBitReaderMethodExists(dataTypeReference);
            if (userWriteMethod is null)
            {
                throw new Exception($"unknown bit reader method for {dataTypeReference}");
            }

            return moduleDefinition.ImportReference(userWriteMethod);
        }

        MethodReference FindStaticBitReaderMethodReturnOnStack(TypeReference dataTypeReference)
        {
            var internalWriteMethod = readSerializer(dataTypeReference.FullName);
            if (internalWriteMethod is not null)
            {
                return moduleDefinition.ImportReference(internalWriteMethod);
            }

            var userWriteMethod = CheckIfUserDefinedStaticBitReaderOnStackMethodExists(dataTypeReference);
            if (userWriteMethod is null)
            {
                throw new Exception($"unknown bit reader method for {dataTypeReference}");
            }

            return moduleDefinition.ImportReference(userWriteMethod);
        }


        /// <summary>
        /// Reads data type and pushes result on stack. IBitReader interface must have been pushed on stack earlier
        /// </summary>
        /// <param name="processor"></param>
        /// <param name="fieldReference"></param>
        /// <param name="log"></param>
        /// <exception cref="Exception"></exception>
        public void EmitDataTypeReaderOnStack(ILProcessor processor, TypeReference fieldType, ILog log)
        {
           // NOTE: It is assumed that IBitReader is already on the stack

            if (TypeCheck.IsAllowedBlittablePrimitive(fieldType))
            {
                log.Debug("Blittable type {FieldType}", fieldType.FullName);
                EmitReadBits(processor, fieldType, log);
                // Cast To type
                EmitConversionFromUInt32DependingOnType(processor, fieldType, log);
            }
            else
            {
                log.Debug("reader method on stack {FieldType}", fieldType.FullName);
                var foundMethod = FindStaticBitReaderMethodReturnOnStack(fieldType);
                if (foundMethod is null)
                {
                    throw new($"couldn't find a bit serializer for {fieldType.FullName}");
                }

                // Call the data specific reader with a `ref`
                processor.Emit(OpCodes.Call, foundMethod);
            }
        }

        public void EmitDataTypeReader(ILProcessor processor, FieldDefinition fieldReference, bool useLocal, ILog log)
        {
            var fieldType = fieldReference.FieldType;

            if (TypeCheck.IsAllowedBlittablePrimitive(fieldType))
            {
                if (useLocal)
                {
                    // Load data type instance (used later for Stfld)
                    processor.Emit(OpCodes.Ldloca_S, (byte)0);

                    // IBitReader
                    processor.Emit(OpCodes.Ldarg_0);
                }
                else
                {
                    // Load data type instance reference (prepare for later Stfld)
                    processor.Emit(OpCodes.Ldarg_1);

                    // IBitReader
                    processor.Emit(OpCodes.Ldarg_0);
                }

                EmitReadBits(processor, fieldType, log);
                // Cast To type
                EmitConversionFromUInt32DependingOnType(processor, fieldType, log);
                processor.Emit(OpCodes.Stfld, fieldReference);
            }
            else
            {
                var foundMethod = FindStaticBitReaderMethod(fieldType);
                if (foundMethod is null)
                {
                    throw new($"couldnt find a bit serializer for {fieldType.FullName} {fieldReference.Name}");
                }

                // IBitReader
                processor.Emit(OpCodes.Ldarg_0);
                if (useLocal)
                {
                    // Load data type instance reference
                    processor.Emit(OpCodes.Ldloca_S, (byte)0);
                }
                else
                {
                    // Load data type instance reference
                    processor.Emit(OpCodes.Ldarg_1);
                }

                // Get reference of the field of the data type instance
                processor.Emit(OpCodes.Ldflda, fieldReference);

                // Call the data specific reader with a `ref`
                processor.Emit(OpCodes.Call, foundMethod);
            }
        }

        public void EmitDataTypeStructReader(ILProcessor processor, IEnumerable<FieldDefinition> fields, bool useLocal, ILog log)
        {
            foreach (var field in fields)
            {
                EmitDataTypeReader(processor, field, useLocal, log);

                moduleDefinition.ImportReference(field);
            }
        }
    }
}