/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Runtime.InteropServices;

// ReSharper disable UnassignedField.Global

namespace Piot.Blitser
{
    public static class DataCopy
    {
        public static int ToBytes<T>(Span<byte> target, ref T data) where T : unmanaged
        {
            MemoryMarshal.Write(target, ref data);
            return Marshal.SizeOf(typeof(T));
        }
    }
}