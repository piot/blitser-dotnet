/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Piot.Blitser
{
    public static class DataDiff
    {
        public static uint Diff<T>(in T a, in T b) where T : struct
        {
            return DataDiffer<T>.diff!(a, b);
        }
    }
}