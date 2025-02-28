﻿using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Utility;

namespace Robust.Shared.Serialization.Manager.Definition
{
    public partial class DataDefinition
    {
        private PopulateDelegateSignature EmitPopulateDelegate(IDependencyCollection collection)
        {
            var isServer = collection.Resolve<INetManager>().IsServer;

            object PopulateDelegate(
                object target,
                MappingDataNode mappingDataNode,
                ISerializationManager serializationManager,
                ISerializationContext? serializationContext,
                bool skipHook,
                object?[] defaultValues)
            {
                for (var i = 0; i < BaseFieldDefinitions.Length; i++)
                {
                    var fieldDefinition = BaseFieldDefinitions[i];

                    if (fieldDefinition.Attribute.ServerOnly && !isServer)
                    {
                        continue;
                    }

                    var mapped = mappingDataNode.Has(fieldDefinition.Attribute.Tag);

                    if (!mapped)
                    {
                        if (fieldDefinition.Attribute.Required)
                            throw new InvalidOperationException($"Required field {fieldDefinition.Attribute.Tag} of type {target.GetType()} wasn't mapped.");
                        continue;
                    }

                    var type = fieldDefinition.FieldType;
                    var node = mappingDataNode.Get(fieldDefinition.Attribute.Tag);
                    object? result;
                    if (fieldDefinition.Attribute.CustomTypeSerializer != null && node switch
                        {
                            ValueDataNode => FieldInterfaceInfos[i].Reader.Value,
                            SequenceDataNode => FieldInterfaceInfos[i].Reader.Sequence,
                            MappingDataNode => FieldInterfaceInfos[i].Reader.Mapping,
                            _ => throw new InvalidOperationException()
                        })
                    {
                        result = serializationManager.ReadWithTypeSerializer(type,
                            fieldDefinition.Attribute.CustomTypeSerializer, node, serializationContext, skipHook);
                    }
                    else
                    {
                        result = serializationManager.Read(type, node, serializationContext, skipHook);
                    }

                    var defValue = defaultValues[i];

                    if (Equals(result, defValue))
                    {
                        continue;
                    }

                    FieldAssigners[i](ref target, result);
                }

                return target;
            }

            return PopulateDelegate;
        }

        private SerializeDelegateSignature EmitSerializeDelegate(IDependencyCollection collection)
        {
            MappingDataNode SerializeDelegate(
                object obj,
                ISerializationManager manager,
                ISerializationContext? context,
                bool alwaysWrite,
                object?[] defaultValues)
            {
                var mapping = new MappingDataNode();

                for (var i = BaseFieldDefinitions.Length - 1; i >= 0; i--)
                {
                    var fieldDefinition = BaseFieldDefinitions[i];

                    if (fieldDefinition.Attribute.ReadOnly)
                    {
                        continue;
                    }

                    if (fieldDefinition.Attribute.ServerOnly &&
                        !collection.Resolve<INetManager>().IsServer)
                    {
                        continue;
                    }

                    var value = FieldAccessors[i](ref obj);

                    if (value == null)
                    {
                        continue;
                    }

                    if (!fieldDefinition.Attribute.Required &&
                        !alwaysWrite &&
                        Equals(value, defaultValues[i]))
                    {
                        continue;
                    }

                    var type = fieldDefinition.FieldType;

                    DataNode node;
                    if (fieldDefinition.Attribute.CustomTypeSerializer != null && FieldInterfaceInfos[i].Writer)
                    {
                        node = manager.WriteWithTypeSerializer(type, fieldDefinition.Attribute.CustomTypeSerializer,
                            value, alwaysWrite, context);
                    }
                    else
                    {
                        node = manager.WriteValue(type, value, alwaysWrite, context);
                    }

                    mapping[fieldDefinition.Attribute.Tag] = node;
                }

                return mapping;
            }

            return SerializeDelegate;
        }

        // TODO Serialization: add skipHook
        private CopyDelegateSignature EmitCopyDelegate()
        {
            object CopyDelegate(
                object source,
                object target,
                ISerializationManager manager,
                ISerializationContext? context)
            {
                for (var i = 0; i < BaseFieldDefinitions.Length; i++)
                {
                    var field = BaseFieldDefinitions[i];
                    var accessor = FieldAccessors[i];
                    var sourceValue = accessor(ref source);
                    var targetValue = accessor(ref target);

                    object? copy;
                    if (sourceValue != null &&
                        targetValue != null &&
                        TypeHelpers.SelectCommonType(sourceValue.GetType(), targetValue.GetType()) == null)
                    {
                        copy = manager.Copy(sourceValue, context);
                    }
                    else
                    {
                        if (field.Attribute.CustomTypeSerializer != null && FieldInterfaceInfos[i].Copier)
                        {
                            copy = manager.CopyWithTypeSerializer(field.Attribute.CustomTypeSerializer, sourceValue,
                                targetValue,
                                context);
                        }
                        else
                        {
                            copy = targetValue;
                            manager.Copy(sourceValue, ref copy, context);
                        }
                    }

                    FieldAssigners[i](ref target, copy);
                }

                return target;
            }

            return CopyDelegate;
        }

        private void EmitSetField(RobustILGenerator rGenerator, AbstractFieldInfo info)
        {
            switch (info)
            {
                case SpecificFieldInfo field:
                    rGenerator.Emit(OpCodes.Stfld, field.FieldInfo);
                    break;
                case SpecificPropertyInfo property:
                    var setter = property.PropertyInfo.GetSetMethod(true) ?? throw new NullReferenceException();

                    var opCode = info.DeclaringType?.IsValueType ?? false
                        ? OpCodes.Call
                        : OpCodes.Callvirt;

                    rGenerator.Emit(opCode, setter);
                    break;
            }
        }

        private AccessField<object, object?> EmitFieldAccessor(FieldDefinition fieldDefinition)
        {
            var method = new DynamicMethod(
                "AccessField",
                typeof(object),
                new[] {typeof(object).MakeByRefType()},
                true);

            method.DefineParameter(1, ParameterAttributes.Out, "target");

            var generator = method.GetRobustGen();

            if (Type.IsValueType)
            {
                generator.DeclareLocal(Type);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldind_Ref);
                generator.Emit(OpCodes.Unbox_Any, Type);
                generator.Emit(OpCodes.Stloc_0);
                generator.Emit(OpCodes.Ldloca_S, 0);
            }
            else
            {
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldind_Ref);
                generator.Emit(OpCodes.Castclass, Type);
            }

            switch (fieldDefinition.BackingField)
            {
                case SpecificFieldInfo field:
                    generator.Emit(OpCodes.Ldfld, field.FieldInfo);
                    break;
                case SpecificPropertyInfo property:
                    var getter = property.PropertyInfo.GetGetMethod(true) ?? throw new NullReferenceException();
                    var opCode = Type.IsValueType ? OpCodes.Call : OpCodes.Callvirt;
                    generator.Emit(opCode, getter);
                    break;
            }

            var returnType = fieldDefinition.BackingField.FieldType;
            if (returnType.IsValueType)
            {
                generator.Emit(OpCodes.Box, returnType);
            }

            generator.Emit(OpCodes.Ret);

            return method.CreateDelegate<AccessField<object, object?>>();
        }

        private AssignField<object, object?> EmitFieldAssigner(FieldDefinition fieldDefinition)
        {
            var method = new DynamicMethod(
                "AssignField",
                typeof(void),
                new[] {typeof(object).MakeByRefType(), typeof(object)},
                true);

            method.DefineParameter(1, ParameterAttributes.Out, "target");
            method.DefineParameter(2, ParameterAttributes.None, "value");

            var generator = method.GetRobustGen();

            if (Type.IsValueType)
            {
                generator.DeclareLocal(Type);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldind_Ref);
                generator.Emit(OpCodes.Unbox_Any, Type);
                generator.Emit(OpCodes.Stloc_0);
                generator.Emit(OpCodes.Ldloca, 0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Unbox_Any, fieldDefinition.FieldType);

                EmitSetField(generator, fieldDefinition.BackingField);

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Box, Type);
                generator.Emit(OpCodes.Stind_Ref);

                generator.Emit(OpCodes.Ret);
            }
            else
            {
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldind_Ref);
                generator.Emit(OpCodes.Castclass, Type);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Unbox_Any, fieldDefinition.FieldType);

                EmitSetField(generator, fieldDefinition.BackingField);

                generator.Emit(OpCodes.Ret);
            }

            return method.CreateDelegate<AssignField<object, object?>>();
        }
    }
}
