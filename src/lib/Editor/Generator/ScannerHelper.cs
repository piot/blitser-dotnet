/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using Mono.CecilEx;

namespace Piot.Blitser.Generator
{
    public static class ScannerHelper
    {
        public static bool HasAttribute<T>(TypeDefinition t) where T : Attribute
        {
            foreach (var custom in t.CustomAttributes)
            {
                if (custom.AttributeType.FullName == typeof(T).FullName)
                {
                    return true;
                }
            }

            return false;

        }

        public static bool HasAttribute(TypeDefinition t, string name)
        {
            foreach (var custom in t.CustomAttributes)
            {
                if (custom.AttributeType.Name == name)
                {
                    return true;
                }
            }

            return false;
        }
        
        public static bool FieldHasAttribute(FieldDefinition field, string name)
        {
            foreach (var custom in field.CustomAttributes)
            {
                if (custom.AttributeType.Name == name)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsStruct(TypeDefinition t)
        {
            return !t.IsAbstract && t.IsValueType;
        }
        

    }
}