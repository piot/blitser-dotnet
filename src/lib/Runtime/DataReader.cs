/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using Piot.Flood;

// ReSharper disable UnassignedField.Global

namespace Piot.Blitser
{
    public static class DataReader<T> where T : unmanaged
    {
        public delegate uint ReadMaskDelegate(IBitReader reader, ref T data);
        public static Func<IBitReader, T>? read;
        public static ReadMaskDelegate? readMask;
    }
}