/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.CecilEx;
using Mono.CecilEx.Cil;
using Mono.CecilEx.Rocks;
using Piot.Clog;
using Piot.Flood;
using MethodAttributes = Mono.CecilEx.MethodAttributes;
using OpCode = Mono.CecilEx.Cil.OpCode;
using OpCodes = Mono.CecilEx.Cil.OpCodes;
using ParameterAttributes = Mono.CecilEx.ParameterAttributes;
using TypeAttributes = Mono.CecilEx.TypeAttributes;

namespace Piot.Blitser.Generator
{
    public class CilGenerator
    {
        const string GeneratedNamespace = "Piot.Blitser.Generated";
        const string GeneratedClassName = "GeneratedBlitser";

        readonly TypeReference bitReaderInterfaceReference;
        readonly TypeReference bitWriterInterfaceReference;
        readonly MethodReference dataReceiverCreateNewMethodReference;
        readonly MethodReference dataReceiverDestroyMethodReference;
        readonly MethodReference dataReceiverGrabOrCreateMethodReference;

        readonly TypeReference dataReceiverInterfaceReference;
        readonly MethodReference dataReceiverUpdateMethodReference;
        readonly TypeDefinition generatedStaticClass;
        readonly MethodReference genericDataStreamReaderCreateAndReadReference;
        readonly MethodReference genericDataStreamReaderReadMaskMethodReference;

        readonly CustomAttribute initializeOnLoadCustomAttribute;
        readonly ModuleDefinition moduleDefinition;

        DataClassMeta? currentData;

        public readonly List<DataClassMeta> dataTypeInfos = new();
        IEnumerable<DataClassMeta> logics = ArraySegment<DataClassMeta>.Empty;
        IEnumerable<DataClassMeta> ghosts = ArraySegment<DataClassMeta>.Empty;
        IEnumerable<DataClassMeta> inputs = ArraySegment<DataClassMeta>.Empty;

        public MethodReference? generatedDataReceiverDestroyMethod;

        public MethodReference? generatedDataReceiverNewMethod;
        public MethodReference? generatedDataReceiverUpdateMethod;

        uint lastUniqueId;

        DataTypeWriter dataTypeWriter;
        DataTypeReader dataTypeReader;

        public CilGenerator(ModuleDefinition moduleDefinition, CustomAttribute runtimeInitializeOnLoad, Func<string,MethodInfo> writeSerializer, Func<string,MethodInfo> readSerializer)
        {
            initializeOnLoadCustomAttribute = runtimeInitializeOnLoad;
            this.moduleDefinition = moduleDefinition;
            generatedStaticClass = CreateRootClass(moduleDefinition);
            moduleDefinition.Types.Add(generatedStaticClass);

            var readBitsMethodInfo = typeof(IBitReader).GetMethod(nameof(IBitReader.ReadBits));
            var readBitsMethod = moduleDefinition.ImportReference(readBitsMethodInfo);

            bitReaderInterfaceReference = readBitsMethod.DeclaringType;

            if (readBitsMethod is null)
            {
                throw new("Internal error. Can not find ReadBits");
            }

            var writeBitsMethodInfo = typeof(IBitWriter).GetMethod(nameof(IBitWriter.WriteBits));
            var writeBitsMethod = moduleDefinition.ImportReference(writeBitsMethodInfo);
            if (writeBitsMethod is null)
            {
                throw new("Internal error. Can not find WriteBits");
            }

            dataTypeWriter = new (moduleDefinition, writeSerializer, writeBitsMethod);
            dataTypeReader = new(moduleDefinition, readBitsMethod, readSerializer);

            bitWriterInterfaceReference = writeBitsMethod.DeclaringType;


            var dataReceiverCreateNewMethodInfo = typeof(IDataReceiver).GetMethod(nameof(IDataReceiver.ReceiveNew));
            dataReceiverCreateNewMethodReference = moduleDefinition.ImportReference(dataReceiverCreateNewMethodInfo);
            dataReceiverInterfaceReference = dataReceiverCreateNewMethodReference.DeclaringType;


            var dataReceiverUpdateMethodInfo = typeof(IDataReceiver).GetMethod(nameof(IDataReceiver.Update));
            dataReceiverUpdateMethodReference = moduleDefinition.ImportReference(dataReceiverUpdateMethodInfo);

            var dataReceiverDestroyMethodInfo = typeof(IDataReceiver).GetMethod(nameof(IDataReceiver.DestroyComponent));
            dataReceiverDestroyMethodReference = moduleDefinition.ImportReference(dataReceiverDestroyMethodInfo);


            var dataReceiverGetMethodInfo = typeof(IDataReceiver).GetMethod(nameof(IDataReceiver.GrabOrCreate));
            dataReceiverGrabOrCreateMethodReference = moduleDefinition.ImportReference(dataReceiverGetMethodInfo);


            var dataStreamReaderCreateAndReadMethodInfo = typeof(DataStreamReader).GetMethod(nameof(DataStreamReader.CreateAndRead));
            genericDataStreamReaderCreateAndReadReference = moduleDefinition.ImportReference(dataStreamReaderCreateAndReadMethodInfo);


            var dataStreamReaderReadMaskMethodInfo = typeof(DataStreamReader).GetMethod(nameof(DataStreamReader.ReadMask));
            genericDataStreamReaderReadMaskMethodReference = moduleDefinition.ImportReference(dataStreamReaderReadMaskMethodInfo);
        }


        public void GenerateAll(AssemblyDefinition compiledAssembly, ILog log)
        {

            var logics = AttributeScanner.ScanForStructWithAttribute(log, new[] { compiledAssembly }, typeof(LogicAttribute));
            if (!logics.Any())
            {
                log.Debug($"Skip {compiledAssembly.MainModule.Name}, since it has no references to Logics");
                //throw new Exception("skip {compiledAssembly.MainModule.Name} no logics found");
            }
            var ghosts = AttributeScanner.ScanForStructWithAttribute(log, new[] { compiledAssembly }, typeof(GhostAttribute));
            if (!ghosts.Any())
            {
                log.Debug($"Skip {compiledAssembly.MainModule.Name}, since it has no references to Ghosts");
                //throw new Exception("skip {compiledAssembly.MainModule.Name} no ghosts found");
            }

            var inputs = AttributeScanner.ScanForStructWithAttribute(log, new[] { compiledAssembly }, typeof(InputAttribute));
            if (!inputs.Any())
            {
                //log.Debug($"Skip {compiledAssembly.MainModule.Name}, since it has no references to Inputs");
                throw new Exception("skip {compiledAssembly.MainModule.Name} no Inputs found");
            }

            GenerateDataTypes(logics, ghosts, inputs, log);
        }

        public void GenerateDataTypes(IEnumerable<TypeDefinition> logics, IEnumerable<TypeDefinition> ghosts, IEnumerable<TypeDefinition> inputs, ILog log)
        {
            this.logics = GenerateDataTypes(logics, log);
            this.ghosts = GenerateDataTypes(ghosts, log);
            this.inputs = GenerateDataTypes(inputs, log);

            CreateDataReceiveNew(dataTypeInfos, log);
            CreateDataReceiveUpdate(dataTypeInfos, log);
            CreateDataReceiveDestroy(dataTypeInfos, log);
        }

        public IEnumerable<DataClassMeta> GenerateDataTypes(IEnumerable<TypeDefinition> dataTypeReferences, ILog log)
        {
            var metas = new List<DataClassMeta>();
            foreach (var typedef in dataTypeReferences)
            {
                var dataClassMeta = GenerateDataType(typedef, log);
                metas.Add(dataClassMeta);
            }

            return metas;
        }

        DataClassMeta GenerateDataType(TypeDefinition dataTypeReference, ILog log)
        {
            var dataTypeInfo = CreateDataClassMeta(dataTypeReference, log);

            CreateDeserializeAllMethod(dataTypeInfo, log);
            CreateDeserializeAllRefMethod(dataTypeInfo, log);
            CreateDeserializeMaskRefMethod(dataTypeInfo, log);

            CreateSerializeMaskRefMethod(dataTypeInfo, log);
            CreateSerializeFullMethod(dataTypeInfo, log);

            CreateDifferMethod(dataTypeInfo, log);

            return dataTypeInfo;
        }

        public void Close()
        {
            CreateInitOnLoadMethod();
        }

        public static string ExecutingEngineInternalDllDirectory()
        {
            var directoryName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (directoryName is null)
            {
                throw new("internal error");
            }

            return directoryName.Replace(@"file:\", "");
        }

        static TypeDefinition CreateRootClass(ModuleDefinition moduleDefinition)
        {
            return new(GeneratedNamespace, GeneratedClassName,
                TypeAttributes.BeforeFieldInit // Static class
                | TypeAttributes.Class
                | TypeAttributes.Public
                | TypeAttributes.Sealed
                | TypeAttributes.Abstract // Needed for static classes
                | TypeAttributes.AutoClass // String stuff
                | TypeAttributes.AnsiClass, // Ansi Strings // TODO: fix it?
                moduleDefinition.ImportReference(typeof(object)));
        }

        public static bool ContainsGeneratedStaticClass(ModuleDefinition moduleDefinition)
        {
            return moduleDefinition.GetTypes().Any(td =>
                td.Namespace == GeneratedNamespace && td.Name == GeneratedClassName);
        }

        public static void VerifyDataClassType(TypeDefinition resolvedDataStructType, ILog log)
        {
            if (!resolvedDataStructType.IsValueType)
            {
                log.Error("The type {Type} is not a valid Data Type (must be struct value type)", resolvedDataStructType.Name);
                throw new("The type {Type} is not a valid Data Type (must be struct value type)");
            }

            foreach (var field in resolvedDataStructType.Fields)
            {
                if (field.IsNotSerialized || field.IsStatic || field.IsPrivate)
                {
                    log.Notice("Can not serialize field {FieldName} in type {TypeName}. Must be public, instance field and not marked as [NotSerialized]", field.Name, resolvedDataStructType.Name);
                    throw new($"Can not serialize field {field.Name} in {resolvedDataStructType.Name}");
                }
            }
        }

        public DataClassMeta CreateDataClassMeta(TypeReference dataClassType, ILog log)
        {
            var resolvedDataStructType = dataClassType.Resolve();
            VerifyDataClassType(resolvedDataStructType, log);
            moduleDefinition.ImportReference(dataClassType);

            lastUniqueId++;

            currentData = new(lastUniqueId, dataClassType, resolvedDataStructType);

            dataTypeInfos.Add(currentData);

            return currentData;
        }

        static string GenerateSafeFullName(TypeDefinition typeDefinition)
        {
            return typeDefinition.FullName.Replace(".", "_");
        }


        static string GenerateSafeFullNameWithPrefix(string prefix, TypeDefinition typeDefinition)
        {
            return $"{prefix}_{GenerateSafeFullName(typeDefinition)}";
        }

        static MethodDefinition CreatePublicStaticMethodForType(string prefix, TypeDefinition dataStructType)
        {
            var generatedMethodName = GenerateSafeFullNameWithPrefix(prefix, dataStructType);
            return new(generatedMethodName,
                MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.HideBySig,
                dataStructType);
        }

        MethodDefinition CreatePublicStaticMethodForTypeVoidReturn(string prefix, TypeDefinition dataStructType)
        {
            var generatedMethodName = GenerateSafeFullNameWithPrefix(prefix, dataStructType);
            return new(generatedMethodName,
                MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.HideBySig,
                moduleDefinition.ImportReference(typeof(void)));
        }

        MethodDefinition CreatePublicStaticMethodForTypeUInt32Return(string prefix, TypeDefinition dataStructType)
        {
            var generatedMethodName = GenerateSafeFullNameWithPrefix(prefix, dataStructType);
            return new(generatedMethodName,
                MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.HideBySig,
                moduleDefinition.ImportReference(typeof(uint)));
        }

        MethodDefinition CreatePublicStaticInitOnLoadMethod()
        {
            return new("InitOnLoad", MethodAttributes.Public |
                                     MethodAttributes.Static,
                moduleDefinition.ImportReference(typeof(void)));

        }

        public MethodDefinition CreateDeserializeAllMethod(DataClassMeta dataTypeInfo, ILog log)
        {
            var deserializeMethod = CreatePublicStaticMethodForType("Deserialize", dataTypeInfo.resolvedDataStructType);

            deserializeMethod.Parameters.Add(new("reader", ParameterAttributes.In, bitReaderInterfaceReference));
            deserializeMethod.Body.InitLocals = true;

            var variableForNewType = new VariableDefinition(dataTypeInfo.dataStructTypeReference);
            deserializeMethod.Body.Variables.Add(variableForNewType);

            var processor = deserializeMethod.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldloca_S, (byte)0);
            processor.Emit(OpCodes.Initobj, dataTypeInfo.resolvedDataStructType);

            dataTypeReader.EmitDataTypeStructReader(processor, dataTypeInfo.resolvedDataStructType.Fields, true, log);

            // Load back our instance data type and return it
            processor.Emit(OpCodes.Ldloc_0);
            processor.Emit(OpCodes.Ret);

            AddGeneratedMethod(deserializeMethod);

            currentData!.readFullMethodReference = deserializeMethod;

            return deserializeMethod;
        }

        public MethodDefinition CreateDeserializeAllRefMethod(DataClassMeta dataTypeInfo, ILog log)
        {
            var deserializeMethod = CreatePublicStaticMethodForTypeVoidReturn("DeserializeAllRef", dataTypeInfo.resolvedDataStructType);

            deserializeMethod.Parameters.Add(new("reader", ParameterAttributes.In, bitReaderInterfaceReference));
            var dataReferenceParameter = new ParameterDefinition("data", ParameterAttributes.None, dataTypeInfo.resolvedDataStructTypeByReference);

            //Assert.IsTrue(dataReferenceParameter.ParameterType.IsByReference);
            deserializeMethod.Parameters.Add(dataReferenceParameter);
            deserializeMethod.Body.InitLocals = true;

            var processor = deserializeMethod.Body.GetILProcessor();

            dataTypeReader.EmitDataTypeStructReader(processor, dataTypeInfo.resolvedDataStructType.Fields, false, log);

            processor.Emit(OpCodes.Ret);

            AddGeneratedMethod(deserializeMethod);

            return deserializeMethod;
        }


        public MethodDefinition CreateDeserializeMaskRefMethod(DataClassMeta dataTypeInfo, ILog log)
        {
            var deserializeMethod = CreatePublicStaticMethodForTypeUInt32Return("DeserializeMaskRef", dataTypeInfo.resolvedDataStructType);

            deserializeMethod.Parameters.Add(new("reader", ParameterAttributes.None, bitReaderInterfaceReference));
            var dataReferenceParameter = new ParameterDefinition("data", ParameterAttributes.None, dataTypeInfo.resolvedDataStructTypeByReference);

            //Assert.IsTrue(dataReferenceParameter.ParameterType.IsByReference);
            deserializeMethod.Parameters.Add(dataReferenceParameter);
            deserializeMethod.Body.InitLocals = true;

            var processor = deserializeMethod.Body.GetILProcessor();

            var maskBitCount = dataTypeInfo.resolvedDataStructType.Fields.Count;
            var shouldUseBitMaskChecks = maskBitCount > 1;

            if (shouldUseBitMaskChecks)
            {
                processor.Emit(OpCodes.Ldarg_0);
                DataTypeSerialization.EmitCallMethodWithBitCount(processor, dataTypeReader.ReadBitsMethod, maskBitCount);
            }
            else
            {
                // mask = 0/1 for return later on
                processor.Emit(maskBitCount == 0 ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1);
            }

            var index = 0;
            Instruction? skipLabel = null;
            foreach (var field in dataTypeInfo.resolvedDataStructType.Fields)
            {
                if (shouldUseBitMaskChecks && skipLabel is not null)
                {
                    processor.Append(skipLabel);
                }

                if (shouldUseBitMaskChecks)
                {
                    // Duplicate mask value since it is removed by And operation and needed for next check
                    processor.Emit(OpCodes.Dup);

                    var valueToCheck = 1 << index;
                    processor.Emit(OpCodes.Ldc_I4, valueToCheck);
                    processor.Emit(OpCodes.And);
                    skipLabel = processor.Create(OpCodes.Nop);
                    processor.Emit(OpCodes.Brfalse_S, skipLabel);
                }

                dataTypeReader.EmitDataTypeReader(processor, field, false, log);

                moduleDefinition.ImportReference(field);
                index++;
            }

            if (skipLabel is not null)
            {
                processor.Append(skipLabel);
            }

            processor.Emit(OpCodes.Ret);

            AddGeneratedMethod(deserializeMethod);

            currentData!.readMaskMethodReference = deserializeMethod;

            return deserializeMethod;
        }

        void AddGeneratedMethod(MethodDefinition methodDefinition)
        {
            generatedStaticClass.Methods.Add(methodDefinition);
        }

        FieldReference SpecializeField(FieldReference self, GenericInstanceType instanceType)
        {
            var reference = new FieldReference(self.Name, self.FieldType, instanceType);
            return moduleDefinition.ImportReference(reference);
        }

        MethodReference SpecializeMethod(MethodReference method, TypeReference specializeForType)
        {
            var instance = new GenericInstanceMethod(method);
            instance.GenericArguments.Add(specializeForType);

            return moduleDefinition.ImportReference(instance);
        }

        MethodReference SpecializeInstanceGenericIntoDeclaringType(MethodReference method, GenericInstanceType instanceType)
        {
            var methodReference = new MethodReference(method.Name, method.ReturnType, instanceType)
            {
                CallingConvention = method.CallingConvention, HasThis = method.HasThis, ExplicitThis = method.ExplicitThis
            };

            foreach (var parameter in method.Parameters)
            {
                methodReference.Parameters.Add(new(parameter.ParameterType));
            }

            foreach (var generic_parameter in method.GenericParameters)
            {
                methodReference.GenericParameters.Add(new(generic_parameter.Name, methodReference));
            }

            return moduleDefinition.ImportReference(methodReference);
        }

        void CreateFuncDelegateBitReaderToDataType(ILProcessor processor, MethodReference functionToBind, TypeReference dataTypeReference)
        {
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, functionToBind);

            var funcWithTwoParametersDelegateReference = moduleDefinition.ImportReference(typeof(Func<,>));
            var funcWithBitReaderAndDataTypeReference = funcWithTwoParametersDelegateReference.MakeGenericInstanceType(bitReaderInterfaceReference, dataTypeReference);

            var funcWithTwoParametersDelegateConstructorReference = moduleDefinition.ImportReference(typeof(Func<,>).GetConstructors()[0]);
            var funcConstructorInstance = SpecializeInstanceGenericIntoDeclaringType(funcWithTwoParametersDelegateConstructorReference, funcWithBitReaderAndDataTypeReference);
            processor.Emit(OpCodes.Newobj, funcConstructorInstance);
        }

        void CreateFuncDelegateBitReaderToDataTypeAndMask(ILProcessor processor, MethodReference functionToBind, TypeReference dataTypeReference, ByReferenceType byReferenceType)
        {
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, functionToBind);


            var genericReadMaskDelegateTypeReference = moduleDefinition.ImportReference(typeof(DataReader<>.ReadMaskDelegate));
            var specializedReadMaskDelegateTypeReference = genericReadMaskDelegateTypeReference.MakeGenericInstanceType(dataTypeReference);


            var genericReadMaskDelegateConstructorReference = moduleDefinition.ImportReference(typeof(DataReader<>.ReadMaskDelegate).GetConstructors()[0]);
            var specializedDelegateConstructorInstance = SpecializeInstanceGenericIntoDeclaringType(genericReadMaskDelegateConstructorReference, specializedReadMaskDelegateTypeReference);

            processor.Emit(OpCodes.Newobj, specializedDelegateConstructorInstance);
        }

        void CreateDataDifferDelegateInstance(ILProcessor processor, MethodReference functionToBind, TypeReference dataTypeReference)
        {
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, functionToBind);


            var genericDiffDelegateTypeReference = moduleDefinition.ImportReference(typeof(DataDiffer<>.DiffDelegate));
            var specializedDiffDelegateTypeReference = genericDiffDelegateTypeReference.MakeGenericInstanceType(dataTypeReference);


            var genericDiffDelegateConstructor = moduleDefinition.ImportReference(typeof(DataDiffer<>.DiffDelegate).GetConstructors()[0]);
            var specializedDelegateConstructorInstance = SpecializeInstanceGenericIntoDeclaringType(genericDiffDelegateConstructor, specializedDiffDelegateTypeReference);

            processor.Emit(OpCodes.Newobj, specializedDelegateConstructorInstance);
        }

        void CreateActionDelegateBitWriterToDataType(ILProcessor processor, MethodReference functionToBind, TypeReference dataTypeReference)
        {
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, functionToBind);

            var funcWithTwoParametersDelegateReference = moduleDefinition.ImportReference(typeof(Action<,>));
            var funcWithBitReaderAndDataTypeReference = funcWithTwoParametersDelegateReference.MakeGenericInstanceType(bitWriterInterfaceReference, dataTypeReference);

            var funcWithTwoParametersDelegateConstructorReference = moduleDefinition.ImportReference(typeof(Action<,>).GetConstructors()[0]);
            var funcConstructorInstance = SpecializeInstanceGenericIntoDeclaringType(funcWithTwoParametersDelegateConstructorReference, funcWithBitReaderAndDataTypeReference);
            processor.Emit(OpCodes.Newobj, funcConstructorInstance);
        }

        void CreateActionDelegateBitWriterToDataTypeAndMask(ILProcessor processor, MethodReference functionToBind, TypeReference dataTypeReference)
        {
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, functionToBind);

            var funcWithTwoParametersAndReturnParametersDelegateReference = moduleDefinition.ImportReference(typeof(Action<,,>));
            var uint32Reference = moduleDefinition.ImportReference(typeof(uint));
            var funcWithBitReaderAndDataTypeReference = funcWithTwoParametersAndReturnParametersDelegateReference.MakeGenericInstanceType(bitWriterInterfaceReference, dataTypeReference, uint32Reference);

            var funcWithTwoParametersAndReturnParametersDelegateConstructorReference = moduleDefinition.ImportReference(typeof(Action<,,>).GetConstructors()[0]);
            var funcConstructorInstance = SpecializeInstanceGenericIntoDeclaringType(funcWithTwoParametersAndReturnParametersDelegateConstructorReference, funcWithBitReaderAndDataTypeReference);
            processor.Emit(OpCodes.Newobj, funcConstructorInstance);
        }

        // Action<IBitReader, uint, uint, IDataReceiver>
        void CreateActionDelegateBitWriterUIntUIntDataReceiver(ILProcessor processor, MethodReference functionToBind)
        {
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, functionToBind);

            var funcWithTwoParametersAndReturnParametersDelegateReference = moduleDefinition.ImportReference(typeof(Action<,,,>));
            var uint32Reference = moduleDefinition.ImportReference(typeof(uint));
            var funcWithBitReaderAndDataTypeReference = funcWithTwoParametersAndReturnParametersDelegateReference.MakeGenericInstanceType(bitReaderInterfaceReference, uint32Reference, uint32Reference, dataReceiverInterfaceReference);

            var funcWithTwoParametersAndReturnParametersDelegateConstructorReference = moduleDefinition.ImportReference(typeof(Action<,,,>).GetConstructors()[0]);
            var funcConstructorInstance = SpecializeInstanceGenericIntoDeclaringType(funcWithTwoParametersAndReturnParametersDelegateConstructorReference, funcWithBitReaderAndDataTypeReference);
            processor.Emit(OpCodes.Newobj, funcConstructorInstance);
        }

        // Action<uint, uint>
        void CreateActionDelegateUIntUIntDataReceiver(ILProcessor processor, MethodReference functionToBind)
        {
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, functionToBind);

            var funcWithTwoParametersAndReturnParametersDelegateReference = moduleDefinition.ImportReference(typeof(Action<,,>));
            var uint32Reference = moduleDefinition.ImportReference(typeof(uint));
            var funcWithBitReaderAndDataTypeReference = funcWithTwoParametersAndReturnParametersDelegateReference.MakeGenericInstanceType(uint32Reference, uint32Reference, dataReceiverInterfaceReference);

            var funcWithTwoParametersAndReturnParametersDelegateConstructorReference = moduleDefinition.ImportReference(typeof(Action<,,>).GetConstructors()[0]);
            var funcConstructorInstance = SpecializeInstanceGenericIntoDeclaringType(funcWithTwoParametersAndReturnParametersDelegateConstructorReference, funcWithBitReaderAndDataTypeReference);
            processor.Emit(OpCodes.Newobj, funcConstructorInstance);
        }


        void CreateInitOnLoadMethod()
        {
            var initOnLoadMethod = CreatePublicStaticInitOnLoadMethod();
            initOnLoadMethod.CustomAttributes.Add(initializeOnLoadCustomAttribute);

            var processor = initOnLoadMethod.Body.GetILProcessor();

            var genericDataIdLookup = moduleDefinition.ImportReference(typeof(DataIdLookup<>));
            if (genericDataIdLookup is null)
            {
                throw new("internal error. can not find DataIdLookup");
            }

            var genericDataIdLookupDef = genericDataIdLookup.Resolve();

            var targetValueFieldInfo = typeof(DataIdLookup<>).GetField("value");
            if (targetValueFieldInfo is null)
            {
                throw new("internal error. can not find DataIdLookup");
            }

            var genericValueFieldRef = moduleDefinition.ImportReference(targetValueFieldInfo);

            foreach (var dataMeta in dataTypeInfos)
            {
                processor.Emit(OpCodes.Ldc_I4, (int)dataMeta.uniqueId);
                var specializedDataIdInstanceType = genericDataIdLookupDef.MakeGenericInstanceType(dataMeta.dataStructTypeReference);
                var specializeField = SpecializeField(genericValueFieldRef, specializedDataIdInstanceType);
                processor.Emit(OpCodes.Stsfld, specializeField);
            }

            var genericDataReaderStaticClassReference = moduleDefinition.ImportReference(typeof(DataReader<>));
            var readDelegateFieldInfo = typeof(DataReader<>).GetField(nameof(DataReader<AnyStruct>.read));
            var readDelegateFieldReference = moduleDefinition.ImportReference(readDelegateFieldInfo);

            var readMaskDelegateFieldInfo = typeof(DataReader<>).GetField(nameof(DataReader<AnyStruct>.readMask));
            var readMaskDelegateFieldReference = moduleDefinition.ImportReference(readMaskDelegateFieldInfo);


            var genericDataWriterStaticClassReference = moduleDefinition.ImportReference(typeof(DataWriter<>));

            var writeFullDelegateFieldInfo = typeof(DataWriter<>).GetField(nameof(DataWriter<AnyStruct>.write));
            var writeFullDelegateFieldReference = moduleDefinition.ImportReference(writeFullDelegateFieldInfo);

            var writeMaskDelegateFieldInfo = typeof(DataWriter<>).GetField(nameof(DataWriter<AnyStruct>.writeMask));
            var writeMaskDelegateFieldReference = moduleDefinition.ImportReference(writeMaskDelegateFieldInfo);


            var genericDataDifferStaticClassReference = moduleDefinition.ImportReference(typeof(DataDiffer<>));
            var diffDelegateFieldInfo = typeof(DataDiffer<>).GetField(nameof(DataDiffer<AnyStruct>.diff));
            var diffDelegateFieldReference = moduleDefinition.ImportReference(diffDelegateFieldInfo);

            foreach (var dataMeta in dataTypeInfos)
            {
                {
                    CreateFuncDelegateBitReaderToDataType(processor, dataMeta.readFullMethodReference!, dataMeta.dataStructTypeReference);

                    var specializedDataReaderForDataType = genericDataReaderStaticClassReference.MakeGenericInstanceType(dataMeta.dataStructTypeReference);
                    var specializedDataReaderField = SpecializeField(readDelegateFieldReference, specializedDataReaderForDataType);
                    processor.Emit(OpCodes.Stsfld, specializedDataReaderField);
                }

                {
                    CreateFuncDelegateBitReaderToDataTypeAndMask(processor, dataMeta.readMaskMethodReference!, dataMeta.dataStructTypeReference, dataMeta.resolvedDataStructTypeByReference);

                    var specializedDataReaderForDataType = genericDataReaderStaticClassReference.MakeGenericInstanceType(dataMeta.dataStructTypeReference);
                    var specializedDataReaderField = SpecializeField(readMaskDelegateFieldReference, specializedDataReaderForDataType);
                    processor.Emit(OpCodes.Stsfld, specializedDataReaderField);
                }

                {
                    CreateActionDelegateBitWriterToDataType(processor, dataMeta.writeFullMethodReference!, dataMeta.dataStructTypeReference);

                    var specializedDataWriterForDataType = genericDataWriterStaticClassReference.MakeGenericInstanceType(dataMeta.dataStructTypeReference);
                    var specializedDataWriterField = SpecializeField(writeFullDelegateFieldReference, specializedDataWriterForDataType);
                    processor.Emit(OpCodes.Stsfld, specializedDataWriterField);
                }

                {
                    CreateActionDelegateBitWriterToDataTypeAndMask(processor, dataMeta.writeMaskMethodReference!, dataMeta.dataStructTypeReference);

                    var specializedDataWriterForDataType = genericDataWriterStaticClassReference.MakeGenericInstanceType(dataMeta.dataStructTypeReference);
                    var specializedDataWriteMaskField = SpecializeField(writeMaskDelegateFieldReference, specializedDataWriterForDataType);
                    processor.Emit(OpCodes.Stsfld, specializedDataWriteMaskField);
                }
            }

            foreach (var dataMeta in dataTypeInfos)
            {
                CreateDataDifferDelegateInstance(processor, dataMeta.diffMethodReference!, dataMeta.dataStructTypeReference);
                var specializedDataDifferForDataType = genericDataDifferStaticClassReference.MakeGenericInstanceType(dataMeta.dataStructTypeReference);
                var specializedDataReaderField = SpecializeField(diffDelegateFieldReference, specializedDataDifferForDataType);
                processor.Emit(OpCodes.Stsfld, specializedDataReaderField);
            }

            {
                CreateActionDelegateBitWriterUIntUIntDataReceiver(processor, generatedDataReceiverNewMethod!);

                var dataStreamReceiverReceiveNewFieldInfo = typeof(DataStreamReceiver).GetField(nameof(DataStreamReceiver.receiveNew));
                var dataStreamReceiverReceiveNewField = moduleDefinition.ImportReference(dataStreamReceiverReceiveNewFieldInfo);
                processor.Emit(OpCodes.Stsfld, dataStreamReceiverReceiveNewField);
            }

            {
                CreateActionDelegateBitWriterUIntUIntDataReceiver(processor, generatedDataReceiverUpdateMethod!);

                var dataStreamReceiverReceiveUpdateFieldInfo = typeof(DataStreamReceiver).GetField(nameof(DataStreamReceiver.receiveUpdate));
                var dataStreamReceiverReceiveUpdateField = moduleDefinition.ImportReference(dataStreamReceiverReceiveUpdateFieldInfo);
                processor.Emit(OpCodes.Stsfld, dataStreamReceiverReceiveUpdateField);
            }

            {
                CreateActionDelegateUIntUIntDataReceiver(processor, generatedDataReceiverDestroyMethod!);

                var dataStreamReceiverReceiveDestroyFieldInfo = typeof(DataStreamReceiver).GetField(nameof(DataStreamReceiver.receiveDestroy));
                var dataStreamReceiverReceiveDestroyField = moduleDefinition.ImportReference(dataStreamReceiverReceiveDestroyFieldInfo);
                processor.Emit(OpCodes.Stsfld, dataStreamReceiverReceiveDestroyField);
            }

            GenerateInputIdArrays(processor);
            GenerateGhostIdArrays(processor);
            GenerateLogicsIdArrays(processor);

            processor.Emit(OpCodes.Ret);

            AddGeneratedMethod(initOnLoadMethod);
        }

        void GenerateInputIdArrays(ILProcessor processor)
        {
            GenerateIdArrayAndSet(processor, typeof(DataInfo).GetField(nameof(DataInfo.inputComponentTypeIds))!,
                inputs.Select(dataClassMeta => dataClassMeta.uniqueId).ToArray());
        }

        void GenerateGhostIdArrays(ILProcessor processor)
        {
            GenerateIdArrayAndSet(processor, typeof(DataInfo).GetField(nameof(DataInfo.ghostComponentTypeIds))!,
                ghosts.Select(dataClassMeta => dataClassMeta.uniqueId).ToArray());
        }

        void GenerateLogicsIdArrays(ILProcessor processor)
        {
            GenerateIdArrayAndSet(processor, typeof(DataInfo).GetField(nameof(DataInfo.logicComponentTypeIds))!,
                logics.Select(dataClassMeta => dataClassMeta.uniqueId).ToArray());
        }

        void GenerateIdArrayAndSet(ILProcessor processor, FieldInfo fieldInfo, IReadOnlyCollection<uint> dataIds)
        {
            if (dataIds.Count == 0)
            {
                throw new Exception("No dataids to set!");
            }
            AddDataIdsArray(processor, dataIds);

            var fieldReference = moduleDefinition.ImportReference(fieldInfo);
            processor.Emit(OpCodes.Stsfld, fieldReference);
        }

        void AddDataIdsArray(ILProcessor processor, IReadOnlyCollection<uint> dataIds)
        {
            processor.Emit(OpCodes.Ldc_I4, dataIds.Count);
            processor.Emit(OpCodes.Newarr, moduleDefinition.TypeSystem.UInt32);

            var index = 0;
            foreach (var dataId in dataIds)
            {
                processor.Emit(OpCodes.Dup);
                processor.Emit(OpCodes.Ldc_I4, index);
                processor.Emit(OpCodes.Ldc_I4, (int)dataId);
                processor.Emit(OpCodes.Stelem_Any, moduleDefinition.TypeSystem.UInt32);
                index++;
            }
        }

        public MethodDefinition CreateSerializeMaskRefMethod(DataClassMeta dataTypeInfo, ILog log)
        {
            var serializeMaskMethod = CreatePublicStaticMethodForTypeVoidReturn("SerializeMaskRef", dataTypeInfo.resolvedDataStructType);

            serializeMaskMethod.Parameters.Add(new("writer", ParameterAttributes.None, bitWriterInterfaceReference));
            var dataReferenceParameter = new ParameterDefinition("data", ParameterAttributes.None, dataTypeInfo.resolvedDataStructType);
            serializeMaskMethod.Parameters.Add(dataReferenceParameter);

            var uint32Reference = moduleDefinition.ImportReference(typeof(uint));
            var fieldMaskParameter = new ParameterDefinition("fieldMask", ParameterAttributes.None, uint32Reference);
            serializeMaskMethod.Parameters.Add(fieldMaskParameter);

            serializeMaskMethod.Body.InitLocals = true;

            var processor = serializeMaskMethod.Body.GetILProcessor();

            var maskBitCount = dataTypeInfo.resolvedDataStructType.Fields.Count;

            var shouldUseBitMaskChecks = maskBitCount > 1;

            if (shouldUseBitMaskChecks)
            {
                processor.Emit(OpCodes.Ldarg_0);
                processor.Emit(OpCodes.Ldarg_2);
                processor.Emit(OpCodes.Ldc_I4, maskBitCount);
                processor.Emit(OpCodes.Callvirt, dataTypeWriter.WriteBitsMethod);
            }

            var index = 0;
            Instruction? skipLabel = null;
            foreach (var field in dataTypeInfo.resolvedDataStructType.Fields)
            {
                if (shouldUseBitMaskChecks && skipLabel is not null)
                {
                    processor.Append(skipLabel);
                }

                if (shouldUseBitMaskChecks)
                {
                    // Load fieldMask
                    processor.Emit(OpCodes.Ldarg_2);

                    var valueToCheck = 1 << index;
                    processor.Emit(OpCodes.Ldc_I4, valueToCheck);
                    processor.Emit(OpCodes.And);
                    skipLabel = processor.Create(OpCodes.Nop);
                    processor.Emit(OpCodes.Brfalse, skipLabel);
                }


                // IBitWriter
                processor.Emit(OpCodes.Ldarg_0);
                // Data
                processor.Emit(OpCodes.Ldarg_1);

                // Load field
                var fieldReference = moduleDefinition.ImportReference(field);
                processor.Emit(OpCodes.Ldfld, fieldReference);

                var fieldType = field.FieldType;

                dataTypeWriter.EmitDataTypeWriterFromStack(processor, fieldType, log);

                index++;
            }

            if (skipLabel is not null)
            {
                processor.Append(skipLabel);
            }

            processor.Emit(OpCodes.Ret);

            AddGeneratedMethod(serializeMaskMethod);

            currentData!.writeMaskMethodReference = serializeMaskMethod;

            return serializeMaskMethod;
        }

        MethodDefinition CreateSerializeFullMethod(DataClassMeta dataTypeInfo, ILog log)
        {
            var serializeFullMethod = CreatePublicStaticMethodForTypeVoidReturn("SerializeFull", dataTypeInfo.resolvedDataStructType);

            serializeFullMethod.Parameters.Add(new("writer", ParameterAttributes.None, bitWriterInterfaceReference));
            var dataReferenceParameter = new ParameterDefinition("data", ParameterAttributes.None, dataTypeInfo.resolvedDataStructType);
            serializeFullMethod.Parameters.Add(dataReferenceParameter);

            serializeFullMethod.Body.InitLocals = true;

            var processor = serializeFullMethod.Body.GetILProcessor();

            VerifyDataStruct(dataTypeInfo.resolvedDataStructType, log);

            var index = 0;
            foreach (var field in dataTypeInfo.resolvedDataStructType.Fields)
            {
                // IBitReader
                processor.Emit(OpCodes.Ldarg_0);
                // Data
                processor.Emit(OpCodes.Ldarg_1);

                // Load field
                var fieldReference = moduleDefinition.ImportReference(field);
                processor.Emit(OpCodes.Ldfld, fieldReference);

                var fieldType = field.FieldType;


                dataTypeWriter.EmitDataTypeWriterFromStack(processor, fieldType, log);


                index++;
            }

            processor.Emit(OpCodes.Ret);

            AddGeneratedMethod(serializeFullMethod);

            currentData!.writeFullMethodReference = serializeFullMethod;

            return serializeFullMethod;
        }

        void VerifyDataStruct(TypeDefinition typeRef, ILog log)
        {
            if (typeRef.HasProperties)
            {
                log.Notice("We discourage having properties on {Struct}", typeRef.FullName);
            }

            if (typeRef.HasEvents)
            {
                log.Error("Not allowed to have events {Struct}", typeRef.FullName);
                throw new Exception($"not allowed to have events {typeRef.FullName}");
            }

            if (typeRef.HasMethods)
            {
                log.Notice("we discourage having methods on a data struct {Struct}", typeRef.FullName);
            }

            if (!typeRef.IsSealed)
            {
                log.Debug("we recommend having structs sealed");
            }
        }

        void EmitCompareStructFields(ILProcessor processor, FieldReference getRootFieldReference, Instruction modifyMaskLabel, ILog log)
        {
            var rootFieldTypeReference = moduleDefinition.ImportReference(getRootFieldReference.FieldType);
            var resolvedFieldType = rootFieldTypeReference.Resolve();
            var writtenFirst = false;

            VerifyDataStruct(resolvedFieldType, log);

            foreach (var fieldDefinition in resolvedFieldType.Fields)
            {
                if (!VerifyDataField(fieldDefinition, log))
                {
                    continue;
                }


                if (!TypeCheck.IsAllowedBlittablePrimitive(fieldDefinition.FieldType))
                {
                    throw new($"Illegal type layers of structs are not allowed! {resolvedFieldType.FullName} {fieldDefinition.FieldType.FullName} {fieldDefinition.Name} {fieldDefinition.IsDefinition} {fieldDefinition.IsSpecialName}");
                }

                var fieldReference = moduleDefinition.ImportReference(fieldDefinition);

                if (writtenFirst)
                {
                    processor.Emit(OpCodes.Bne_Un_S, modifyMaskLabel);
                    // DataType a;
                    processor.Emit(OpCodes.Ldarg_0);
                }

                writtenFirst = true;

                processor.Emit(OpCodes.Ldfld, getRootFieldReference);
                processor.Emit(OpCodes.Ldfld, fieldReference);

                // DataType b;
                processor.Emit(OpCodes.Ldarg_1);
                processor.Emit(OpCodes.Ldfld, getRootFieldReference);
                processor.Emit(OpCodes.Ldfld, fieldReference);

            }
        }

        public MethodDefinition CreateDifferMethod(DataClassMeta dataTypeInfo, ILog log)
        {
            var uint32Reference = moduleDefinition.ImportReference(typeof(uint));
            var generatedMethodName = GenerateSafeFullNameWithPrefix("Differ", dataTypeInfo.resolvedDataStructType);
            var diffMethod = new MethodDefinition(generatedMethodName,
                MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.HideBySig,
                uint32Reference);

            var firstDataReferenceParameter = new ParameterDefinition("a", ParameterAttributes.In, dataTypeInfo.resolvedDataStructType);
            diffMethod.Parameters.Add(firstDataReferenceParameter);
            var secondDataReferenceParameter = new ParameterDefinition("b", ParameterAttributes.In, dataTypeInfo.resolvedDataStructType);
            diffMethod.Parameters.Add(secondDataReferenceParameter);

            var maskVariable = new VariableDefinition(uint32Reference);
            diffMethod.Body.Variables.Add(maskVariable);
            diffMethod.Body.InitLocals = true;

            var processor = diffMethod.Body.GetILProcessor();

            // generate: uint mask = 0;
            processor.Emit(OpCodes.Ldc_I4_0);

            VerifyDataStruct(dataTypeInfo.resolvedDataStructType, log);

            if (dataTypeInfo.resolvedDataStructType.Fields.Count != 0)
            {
                processor.Emit(OpCodes.Stloc_0);

                var index = 0;

                var skipLabel = processor.Create(OpCodes.Ldarg_0);
                foreach (var field in dataTypeInfo.resolvedDataStructType.Fields)
                {
                    processor.Append(skipLabel);


                    if (!VerifyDataField(field, log))
                    {
                        continue;
                    }

                    var modifyMaskLabel = processor.Create(OpCodes.Ldloc_0);
                    if (TypeCheck.IsAllowedBlittablePrimitive(field.FieldType))
                    {
                        processor.Emit(OpCodes.Ldfld, field);

                        processor.Emit(OpCodes.Ldarg_1);
                        processor.Emit(OpCodes.Ldfld, field);

                    }
                    else
                    {
                        EmitCompareStructFields(processor, field, modifyMaskLabel, log);
                    }

                    var isLastField = index == dataTypeInfo.resolvedDataStructType.Fields.Count - 1;
                    skipLabel = processor.Create(isLastField ? OpCodes.Ldloc_0 : OpCodes.Ldarg_0);

                    processor.Emit(OpCodes.Beq_S, skipLabel);

                    var maskValue = 1 << index;
                    processor.Append(modifyMaskLabel);
                    processor.Emit(OpCodes.Ldc_I4, maskValue);
                    processor.Emit(OpCodes.Or);
                    processor.Emit(OpCodes.Stloc_0);

                    index++;
                }

                processor.Append(skipLabel);
            }

            processor.Emit(OpCodes.Ret);

            currentData!.diffMethodReference = diffMethod;

            AddGeneratedMethod(diffMethod);

            return diffMethod;
        }
        bool VerifyDataField(FieldDefinition field, ILog log)
        {
            if (field.IsLiteral && field.IsStatic)
            {
                log.Debug("literal is discouraged, but works for now {Field}", field.FullName);
                return false;
            }

            if (!field.IsDefinition)
            {
                log.Debug("this is not a definition {Field}", field.FullName);
                return false;
            }

            if (field.IsStatic)
            {
                log.Debug($"static fields are discouraged, but works for now {field.FullName}");
                return false;
            }


            if (field.IsPrivate)
            {
                log.Error("can not have private fields {Field}", field.FullName);
                throw new Exception($"not allowed to have private fields {field.FullName}");
            }

            return field.IsPublic;
        }

        public MethodDefinition CreateCommonDataReceiveMethod(string generatedMethodName, ILog log)
        {
            log.Debug("create common data receive {Name}", generatedMethodName);
            var uint32Reference = moduleDefinition.ImportReference(typeof(uint));
            var voidReturnReference = moduleDefinition.ImportReference(typeof(void));
            var dataReceiveNewMethod = new MethodDefinition(generatedMethodName,
                MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.HideBySig,
                voidReturnReference);

            var bitReaderParameter = new ParameterDefinition("reader", ParameterAttributes.None, bitReaderInterfaceReference);
            dataReceiveNewMethod.Parameters.Add(bitReaderParameter);
            var entityIdParameter = new ParameterDefinition("entityId", ParameterAttributes.None, uint32Reference);
            dataReceiveNewMethod.Parameters.Add(entityIdParameter);
            var dataTypeIdParameter = new ParameterDefinition("dataTypeId", ParameterAttributes.None, uint32Reference);
            dataReceiveNewMethod.Parameters.Add(dataTypeIdParameter);
            var dataReceiverInterfaceParameter = new ParameterDefinition("receiver", ParameterAttributes.None, dataReceiverInterfaceReference);
            dataReceiveNewMethod.Parameters.Add(dataReceiverInterfaceParameter);

            dataReceiveNewMethod.Body.InitLocals = false;

            return dataReceiveNewMethod;
        }

        public MethodDefinition CreateDataReceiveNew(IEnumerable<DataClassMeta> dataClassMetas, ILog log)
        {
            var dataReceiveNewMethod = CreateCommonDataReceiveMethod("DataReceiveNew", log);

            var processor = dataReceiveNewMethod.Body.GetILProcessor();

            // load dataTypeId
            processor.Emit(OpCodes.Ldarg_2);

            var (labels, defaultLabel) = CreateEnumAndReturnLabels(dataClassMetas, processor, OpCodes.Ldarg_3, OpCodes.Ret);

            processor.Emit(OpCodes.Ret);

            var index = 0;

            foreach (var dataTypeInfo in dataClassMetas)
            {
                processor.Append(labels[index]);

                // EntityId
                processor.Emit(OpCodes.Ldarg_1);
                // reader
                processor.Emit(OpCodes.Ldarg_0);


                var specializedDataStreamReaderCreateAndReadReference = SpecializeMethod(genericDataStreamReaderCreateAndReadReference, dataTypeInfo.dataStructTypeReference);
                processor.Emit(OpCodes.Call, specializedDataStreamReaderCreateAndReadReference);


                var specializedDataReceiverCreateNewMethodReference = SpecializeMethod(dataReceiverCreateNewMethodReference, dataTypeInfo.dataStructTypeReference);
                processor.Emit(OpCodes.Callvirt, specializedDataReceiverCreateNewMethodReference);


                processor.Emit(OpCodes.Ret);
                index++;
            }

            processor.Append(defaultLabel);

            AddGeneratedMethod(dataReceiveNewMethod);

            generatedDataReceiverNewMethod = dataReceiveNewMethod;

            log.Info("setting {GeneratedDataReceiverNewMethod}", generatedDataReceiverNewMethod);

            return dataReceiveNewMethod;
        }

        public MethodDefinition CreateDataReceiveUpdate(IEnumerable<DataClassMeta> dataClassMetas, ILog log)
        {
            var dataReceiveNewMethod = CreateCommonDataReceiveMethod("DataReceiveUpdate", log);

            var processor = dataReceiveNewMethod.Body.GetILProcessor();

            foreach (var dataTypeInfo in dataClassMetas)
            {
                var specificLocal = new VariableDefinition(dataTypeInfo.dataStructTypeReference);
                processor.Body.Variables.Add(specificLocal);
            }

            // load dataTypeId
            processor.Emit(OpCodes.Ldarg_2);

            var (labels, defaultLabel) = CreateEnumAndReturnLabels(dataClassMetas, processor, OpCodes.Ldarg_3, OpCodes.Ret);

            processor.Emit(OpCodes.Ret);

            var index = 0;

            foreach (var dataTypeInfo in dataClassMetas)
            {
                processor.Append(labels[index]); // LdArg_3 // IDataReceiver

                // To get an IDataReceiver for the IDataReceiver:Update call below
                // IDataReceiver
                //processor.Emit(OpCodes.Dup);

                // EntityId
                processor.Emit(OpCodes.Ldarg_1);

                var specializedDataReceiverGetMethodReference = SpecializeMethod(dataReceiverGrabOrCreateMethodReference, dataTypeInfo.dataStructTypeReference);
                processor.Emit(OpCodes.Callvirt, specializedDataReceiverGetMethodReference);

                // The stack now has the DataType struct on top of the stack

                // Save the Data type struct it in a local variable and take the address of that
                processor.Emit(OpCodes.Stloc_S, (byte)index);


                // IDataReceiver (prepare for callvirt below)
                processor.Emit(OpCodes.Ldarg_3);

                // reader
                processor.Emit(OpCodes.Ldarg_0);

                // take the address of the local variable and push on stack
                processor.Emit(OpCodes.Ldloca_S, (byte)index);

                var specializedDataStreamReaderReadMaskReference = SpecializeMethod(genericDataStreamReaderReadMaskMethodReference, dataTypeInfo.dataStructTypeReference);
                processor.Emit(OpCodes.Call, specializedDataStreamReaderReadMaskReference);

                // Stack has the previously pushed IDataReceiver and the mask

#if false
                  processor.Emit(OpCodes.Pop);
                  processor.Emit(OpCodes.Pop);
#else

                // EntityId
                processor.Emit(OpCodes.Ldarg_1);

                // take the address of the local variable and push on stack
                processor.Emit(OpCodes.Ldloc_S, (byte)index);

                var specializedDataReceiverUpdateMethodReference = SpecializeMethod(dataReceiverUpdateMethodReference, dataTypeInfo.dataStructTypeReference);
                processor.Emit(OpCodes.Callvirt, specializedDataReceiverUpdateMethodReference);
#endif
                processor.Emit(OpCodes.Ret);
                index++;
            }

            processor.Append(defaultLabel);

            AddGeneratedMethod(dataReceiveNewMethod);

            generatedDataReceiverUpdateMethod = dataReceiveNewMethod;

            return dataReceiveNewMethod;
        }

        (Instruction[], Instruction) CreateEnumAndReturnLabels(IEnumerable<DataClassMeta> dataClassMetas, ILProcessor processor, OpCode startLabelOpCode, OpCode startDefaultOpCode)
        {
            var labels = new Instruction[dataClassMetas.Count() + 1];
            var labelIndex = 0;

            var defaultLabel = processor.Create(OpCodes.Ret);
            labels[labelIndex++] = defaultLabel;

            foreach (var dataTypeInfo in dataClassMetas)
            {
                // Load `receiver`
                labels[labelIndex] = processor.Create(startLabelOpCode);
                labelIndex++;
            }

            processor.Emit(OpCodes.Switch, labels);

            return (labels.Skip(1).ToArray(), defaultLabel);
        }

        public MethodDefinition CreateDataReceiveDestroy(IEnumerable<DataClassMeta> dataClassMetas, ILog log)
        {
            var uint32Reference = moduleDefinition.ImportReference(typeof(uint));
            var voidReturnReference = moduleDefinition.ImportReference(typeof(void));
            var dataReceiveDestroyMethod = new MethodDefinition("DataReceiveDestroy",
                MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.HideBySig,
                voidReturnReference);

            var entityIdParameter = new ParameterDefinition("entityId", ParameterAttributes.None, uint32Reference);
            dataReceiveDestroyMethod.Parameters.Add(entityIdParameter);
            var dataTypeIdParameter = new ParameterDefinition("dataTypeId", ParameterAttributes.None, uint32Reference);
            dataReceiveDestroyMethod.Parameters.Add(dataTypeIdParameter);
            var dataReceiverInterfaceParameter = new ParameterDefinition("receiver", ParameterAttributes.None, dataReceiverInterfaceReference);
            dataReceiveDestroyMethod.Parameters.Add(dataReceiverInterfaceParameter);

            dataReceiveDestroyMethod.Body.InitLocals = false;

            var processor = dataReceiveDestroyMethod.Body.GetILProcessor();

            // load dataTypeId
            processor.Emit(OpCodes.Ldarg_1);

            var (labels, defaultLabel) = CreateEnumAndReturnLabels(dataClassMetas, processor, OpCodes.Ldarg_2, OpCodes.Ret);

            processor.Emit(OpCodes.Ret);

            var index = 0;

            foreach (var dataTypeInfo in dataClassMetas)
            {
                processor.Append(labels[index]); // LdArg_2 // IDataReceiver

                // EntityId
                processor.Emit(OpCodes.Ldarg_0);

                var specializedDataReceiverDestroyMethodReference = SpecializeMethod(dataReceiverDestroyMethodReference, dataTypeInfo.dataStructTypeReference);
                processor.Emit(OpCodes.Callvirt, specializedDataReceiverDestroyMethodReference);

                processor.Emit(OpCodes.Ret);
                index++;
            }

            processor.Append(defaultLabel);

            AddGeneratedMethod(dataReceiveDestroyMethod);

            generatedDataReceiverDestroyMethod = dataReceiveDestroyMethod;

            return dataReceiveDestroyMethod;
        }


        public class DataClassMeta
        {
            public readonly TypeReference dataStructTypeReference;
            public readonly TypeDefinition resolvedDataStructType;
            public readonly ByReferenceType resolvedDataStructTypeByReference;

            public readonly uint uniqueId;

            public MethodReference? diffMethodReference;
            public MethodReference? readFullMethodReference;
            public MethodReference? readMaskMethodReference;
            public MethodReference? writeFullMethodReference;
            public MethodReference? writeMaskMethodReference;

            public DataClassMeta( uint uniqueId, TypeReference typeReference, TypeDefinition resolvedDataStructType)
            {
                this.uniqueId = uniqueId;
                dataStructTypeReference = typeReference;

                this.resolvedDataStructType = resolvedDataStructType;
                resolvedDataStructTypeByReference = new(resolvedDataStructType);
            }
        }

        struct AnyStruct
        {

        }
    }
}