/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using Piot.Flood;

// ReSharper disable UnassignedField.Global

namespace Piot.Blitser
{
    public static class DataWriter<T> where T : unmanaged
    {
        public static Action<IBitWriter, T>? write;
        public static Action<IBitWriter, T, uint>? writeMask;
    }
}