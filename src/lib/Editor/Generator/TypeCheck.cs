/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using Mono.CecilEx;
using System.Linq;

namespace Piot.Blitser.Generator
{
    public static class TypeCheck
    {
        public static bool IsBool(TypeReference fieldType)
        {
            return fieldType.IsPrimitive && fieldType.Name == nameof(Boolean);
        }

        public static bool IsAllowedBlittablePrimitive(TypeReference fieldType)
        {
            var name = fieldType.Name;
            var allowed = new[]
            {
                nameof(Boolean), nameof(Byte), nameof(SByte), nameof(UInt16), nameof(Int16), nameof(UInt32), nameof(Int32), nameof(UInt64), nameof(Int64)
            };

            return fieldType.IsPrimitive && allowed.Contains(name) || fieldType.Resolve().IsEnum;
        }        
    }
}