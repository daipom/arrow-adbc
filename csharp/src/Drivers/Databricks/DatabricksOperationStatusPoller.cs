/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow.Adbc.Drivers.Databricks.CloudFetch;
using Apache.Hive.Service.Rpc.Thrift;

namespace Apache.Arrow.Adbc.Drivers.Databricks
{
    /// <summary>
    /// Service that periodically polls the operation status of a Databricks warehouse query to keep it alive.
    /// This is used to maintain the command results and session when reading results takes a long time.
    /// </summary>
    internal class DatabricksOperationStatusPoller : IDisposable
    {
        private readonly IHiveServer2Statement _statement;
        private readonly int _heartbeatIntervalSeconds;
        // internal cancellation token source - won't affect the external token
        private CancellationTokenSource? _internalCts;
        private Task? _operationStatusPollingTask;

        public DatabricksOperationStatusPoller(IHiveServer2Statement statement, int heartbeatIntervalSeconds = DatabricksConstants.DefaultOperationStatusPollingIntervalSeconds)
        {
            _statement = statement ?? throw new ArgumentNullException(nameof(statement));
            _heartbeatIntervalSeconds = heartbeatIntervalSeconds;
        }

        public bool IsStarted => _operationStatusPollingTask != null;

        /// <summary>
        /// Starts the operation status poller. Continues polling every minute until the operation is canceled/errored
        /// the token is canceled, or the operation status poller is disposed.
        /// </summary>
        /// <param name="externalToken">The external cancellation token.</param>
        public void Start(CancellationToken externalToken = default)
        {
            if (IsStarted)
            {
                throw new InvalidOperationException("Operation status poller already started");
            }
            _internalCts = new CancellationTokenSource();
            // create a linked token to the external token so that the external token can cancel the operation status polling task if needed
            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_internalCts.Token, externalToken).Token;
            _operationStatusPollingTask = Task.Run(() => PollOperationStatus(linkedToken));
        }

        private async Task PollOperationStatus(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var operationHandle = _statement.OperationHandle;
                    if (operationHandle == null) break;

                    var request = new TGetOperationStatusReq(operationHandle);
                    var response = await _statement.Client.GetOperationStatus(request, cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSeconds), cancellationToken);

                    // end the heartbeat if the command has terminated
                    if (response.OperationState == TOperationState.CANCELED_STATE ||
                        response.OperationState == TOperationState.ERROR_STATE)
                    {
                        break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // ignore
            }
        }

        public void Stop()
        {
            _internalCts?.Cancel();
        }

        public void Dispose()
        {
            if (_internalCts != null)
            {
                _internalCts.Cancel();
                _operationStatusPollingTask?.Wait();
                _internalCts.Dispose();
            }
        }
    }
}
