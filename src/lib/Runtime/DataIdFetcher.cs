/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Piot.Blitser
{
    public static class DataIdFetcher
    {
        public static ushort Id<T>() where T : unmanaged
        {
            return DataIdLookup<T>.value;
        }
    }
}