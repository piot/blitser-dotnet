/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Peter Bjorklund. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Piot.Blitser
{
    public interface IDataReceiver
    {
        public void ReceiveNew<T>(uint entityId, T data) where T : struct;
        public void Update<T>(uint mask, uint entityId, T data) where T : struct;
        public T GrabOrCreate<T>(uint entityId) where T : struct;
        public void DestroyComponent<T>(uint entityId) where T : struct;
    }
}