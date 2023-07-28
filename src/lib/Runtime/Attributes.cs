/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;

namespace Piot.Blitser
{
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class ReplicateComponentAttribute : Attribute
    {
        public bool generate { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BitSerializerAttribute : Attribute
    {
        public bool generate { get; set; }
    }
    
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ReplicateAttribute : Attribute
    {
        public bool generate { get; set; }
    }
}