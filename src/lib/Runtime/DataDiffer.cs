/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// ReSharper disable UnassignedField.Global
namespace Piot.Blitser
{
    public static class DataDiffer<T> where T : unmanaged
    {
        public delegate uint DiffDelegate(in T a, in T b);
        public static DiffDelegate? diff;
    }
}