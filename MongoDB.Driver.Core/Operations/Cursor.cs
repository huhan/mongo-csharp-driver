﻿/* Copyright 2010-2013 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Core.Protocol;
using MongoDB.Driver.Core.Sessions;
using MongoDB.Driver.Core.Support;

namespace MongoDB.Driver.Core.Operations
{
    /// <summary>
    /// Represents a cursor.
    /// </summary>
    /// <typeparam name="TDocument">The type of the document.</typeparam>
    public sealed class Cursor<TDocument> : ICursor<TDocument>
    {
        // private fields
        private readonly CancellationToken _cancellationToken;
        private readonly IServerChannelProvider _channelProvider;
        private readonly CollectionNamespace _collection;
        private readonly int _numberToReturn;
        private readonly Func<ICursorStatistics, bool> _prefetchFunc;
        private readonly BsonBinaryReaderSettings _readerSettings;
        private readonly IBsonSerializer _serializer;
        private readonly IBsonSerializationOptions _serializationOptions;
        private readonly TimeSpan _timeout;
        private long _cursorId;
        private bool _disposed;
        private List<TDocument> _currentBatch;
        private long _currentBatchNumber; // what batch number we are currently on
        private int _currentBatchIndex; // the current index into _currentBatch
        private long _currentIndex; // the total number of documents current iterated
        private ManualResetEventSlim _prefetchWait;
        private volatile List<TDocument> _nextBatch;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="Cursor{TDocument}" /> class.
        /// </summary>
        /// <param name="channelProvider">The channel provider.</param>
        /// <param name="cursorId">The cursor id.</param>
        /// <param name="collection">The collection.</param>
        /// <param name="numberToReturn">Size of the batch.</param>
        /// <param name="firstBatch">The first batch.</param>
        /// <param name="prefetchFunc">The prefetch func.</param>
        /// <param name="serializer">The serializer.</param>
        /// <param name="serializationOptions">The serialization options.</param>
        /// <param name="timeout">The timeout.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="readerSettings">The reader settings.</param>
        public Cursor(IServerChannelProvider channelProvider, long cursorId, CollectionNamespace collection, int numberToReturn, IEnumerable<TDocument> firstBatch, Func<ICursorStatistics, bool> prefetchFunc, IBsonSerializer serializer, IBsonSerializationOptions serializationOptions, TimeSpan timeout, CancellationToken cancellationToken, BsonBinaryReaderSettings readerSettings)
        {
            Ensure.IsNotNull("channelProvider", channelProvider);
            Ensure.IsNotNull("collection", collection);
            Ensure.IsNotNull("firstBatch", firstBatch);
            Ensure.IsNotNull("serializer", serializer);
            Ensure.IsNotNull("readerSettings", readerSettings);

            _channelProvider = channelProvider;
            _cursorId = cursorId;
            _collection = collection;
            _numberToReturn = numberToReturn;
            _currentBatch = firstBatch.ToList();
            _prefetchFunc = prefetchFunc;
            _serializer = serializer;
            _serializationOptions = serializationOptions;
            _timeout = timeout;
            _cancellationToken = cancellationToken;
            _readerSettings = readerSettings;
            _currentBatchIndex = -1;
            _prefetchWait = new ManualResetEventSlim(true);
        }

        // public properties
        /// <summary>
        /// Gets the collection.
        /// </summary>
        public CollectionNamespace Collection
        {
            get { return _collection; }
        }

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        /// <returns>The element in the collection at the current position of the enumerator.</returns>
        public TDocument Current
        {
            get
            {
                ThrowIfDisposed();
                if (_currentBatchIndex == -1)
                {
                    throw new InvalidOperationException("There is no current item.  Ensure a call to MoveNext() returns successfully.");
                }

                return _currentBatch[_currentBatchIndex];
            }
        }

        /// <summary>
        /// Gets the current batch that is being enumerated.
        /// </summary>
        public long CurrentBatch
        {
            get { return _currentBatchNumber; }
        }

        /// <summary>
        /// Gets the number of documents in the current batch.
        /// </summary>
        public long CurrentBatchCount
        {
            get { return _currentBatch.Count; }
        }

        /// <summary>
        /// Gets the index of the current document in the current batch.
        /// </summary>
        /// <exception cref="System.NotImplementedException"></exception>
        public long CurrentBatchIndex
        {
            get { return _currentBatchIndex; }
        }

        /// <summary>
        /// Gets the index of the current document.
        /// </summary>
        /// <exception cref="System.NotImplementedException"></exception>
        public long CurrentIndex
        {
            get { return _currentIndex; }
        }

        /// <summary>
        /// Gets the type of the document.
        /// </summary>
        /// <value>
        /// The type of the document.
        /// </value>
        public Type DocumentType
        {
            get { return typeof(TDocument); }
        }

        // implicit properties
        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        /// <returns>The element in the collection at the current position of the enumerator.</returns>
        object System.Collections.IEnumerator.Current
        {
            get { return Current; }
        }

        // public methods
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    KillCursorIfNecessary();
                }
                finally
                {
                    _prefetchWait.Dispose();
                    _channelProvider.Dispose();
                    _disposed = true;
                }
            }
        }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns>
        /// true if the enumerator was successfully advanced to the next element; false if the enumerator has passed the end of the collection.
        /// </returns>
        public bool MoveNext()
        {
            ThrowIfDisposed();
            _currentBatchIndex++;
            _currentIndex++;
            if (_currentBatchIndex < _currentBatch.Count)
            {
                if (_nextBatch == null && _prefetchFunc != null && _prefetchFunc(this))
                {
                    _prefetchWait.Reset();
                    ThreadPool.QueueUserWorkItem(_ => GetNextBatch());
                }

                return true;
            }

            while (_cursorId != 0)
            {
                _prefetchWait.Wait();
                if (_nextBatch == null)
                {
                    GetNextBatch();
                }

                if (_nextBatch != null)
                {
                    _currentBatch = _nextBatch;
                    _currentBatchIndex = 0;
                    _currentBatchNumber++;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sets the enumerator to its initial position, which is before the first element in the collection.
        /// </summary>
        /// <exception cref="System.NotSupportedException">MongoDB Cursors cannot be reset.  Reissue the query to retrieve another cursor.</exception>
        public void Reset()
        {
            throw new NotSupportedException("MongoDB Cursors cannot be reset.  Reissue the query to retrieve another cursor.");
        }

        // private methods
        private void GetNextBatch()
        {
            _cancellationToken.ThrowIfCancellationRequested();

            var protocol = new GetMoreProtocol<TDocument>(
                collection: _collection,
                cursorId: _cursorId,
                numberToReturn: _numberToReturn,
                readerSettings: _readerSettings,
                serializer: _serializer,
                serializationOptions: _serializationOptions);

            using (var channel = _channelProvider.GetChannel(_timeout, _cancellationToken))
            {
                var result = protocol.Execute(channel);
                _cursorId = result.CursorId;
                var docs = result.Documents.ToList();
                if (docs.Count > 0)
                {
                    _nextBatch = docs;
                }
            }
        }

        private void KillCursorIfNecessary()
        {
            if (_cursorId != 0)
            {
                var protocol = new KillCursorsProtocol(new[] { _cursorId });

                // Intentionally ignoring cancellation tokens and timeouts. 
                using (var channel = _channelProvider.GetChannel(Timeout.InfiniteTimeSpan, CancellationToken.None))
                {
                    protocol.Execute(channel);
                }
                _cursorId = 0;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}