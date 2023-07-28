/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
// ReSharper disable UnassignedField.Global

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace Piot.Blitser
{
    public static class DataInfo
    {
        public static uint[]? inputComponentTypeIds;
        public static uint[]? logicComponentTypeIds;
        public static uint[]? ghostComponentTypeIds;
    }

    public enum DataType
    {
        Logic,
        Ghost,
        Input
    }

    public struct MetaInfo
    {
        public DataType dataType;
        public Type type;

        public MetaInfo(DataType dataType, Type type)
        {
            this.type = type;
            this.dataType = dataType;
        }

        public override string ToString()
        {
            return $"[Meta {type.FullName} ({dataType})]";
        }
    }

    public static class DataTypeInfo<T> where T : unmanaged
    {
        public static DataType dataType;
    }

    public static class DataMetaInfo
    {
        public static MetaInfo[] infos = Array.Empty<MetaInfo>();

        public static MetaInfo? GetMeta(uint componentTypeId)
        {
            if (componentTypeId >= infos.Length)
            {
                return null;
            }

            return infos[componentTypeId];
        }
    }
}