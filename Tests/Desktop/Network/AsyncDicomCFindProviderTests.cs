// Copyright (c) 2012-2021 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Dicom.Helpers;
using Dicom.Log;
using Xunit;
using Xunit.Abstractions;

namespace Dicom.Network
{
    public class AsyncDicomCFindProviderTests
    {
        private readonly XUnitDicomLogger _logger;

        public AsyncDicomCFindProviderTests(ITestOutputHelper testOutputHelper)
        {
            _logger = new XUnitDicomLogger(testOutputHelper)
                .IncludeTimestamps()
                .IncludeThreadId()
                .WithMinimumLevel(LogLevel.Debug);
        }

        [Fact]
        public async Task OnCFindRequestAsync_ImmediateSuccess_ShouldRespond()
        {
            var port = Ports.GetNext();

            using (DicomServer.Create<ImmediateSuccessAsyncDicomCFindProvider>(port, logger: _logger.IncludePrefix("DicomServer")))
            {
                var client = new Client.DicomClient("127.0.0.1", port, false, "SCU", "ANY-SCP")
                {
                    Logger = _logger.IncludePrefix(typeof(DicomClient).Name)
                };

                DicomCFindResponse response = null;
                DicomRequest.OnTimeoutEventArgs timeout = null;
                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study)
                {
                    OnResponseReceived = (req, res) => response = res,
                    OnTimeout = (sender, args) => timeout = args
                };

                await client.AddRequestAsync(request).ConfigureAwait(false);
                await client.SendAsync().ConfigureAwait(false);

                Assert.NotNull(response);
                Assert.Equal(DicomStatus.Success, response.Status);
                Assert.Null(timeout);
            }
        }

        [Fact]
        public async Task OnCFindRequestAsync_Pending_ShouldRespond()
        {
            var port = Ports.GetNext();

            using (DicomServer.Create<PendingAsyncDicomCFindProvider>(port, logger: _logger.IncludePrefix("DicomServer")))
            {
                var client = new Client.DicomClient("127.0.0.1", port, false, "SCU", "ANY-SCP")
                {
                    Logger = _logger.IncludePrefix(typeof(DicomClient).Name)
                };

                var responses = new ConcurrentQueue<DicomCFindResponse>();
                DicomRequest.OnTimeoutEventArgs timeout = null;
                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study)
                {
                    OnResponseReceived = (req, res) => responses.Enqueue(res),
                    OnTimeout = (sender, args) => timeout = args
                };

                await client.AddRequestAsync(request).ConfigureAwait(false);
                await client.SendAsync().ConfigureAwait(false);

                Assert.Collection(
                    responses,
                    response1 => Assert.Equal(DicomStatus.Pending, response1.Status),
                    response2 => Assert.Equal(DicomStatus.Pending, response2.Status),
                    response3 => Assert.Equal(DicomStatus.Success, response3.Status)
                );
                Assert.Null(timeout);
            }
        }
    }

    #region helper classes

    public class ImmediateSuccessAsyncDicomCFindProvider : DicomService, IDicomServiceProvider, IAsyncDicomCFindProvider
    {
        public ImmediateSuccessAsyncDicomCFindProvider(INetworkStream stream, Encoding fallbackEncoding, Logger log)
            : base(stream, fallbackEncoding, log)
        {
        }

        /// <inheritdoc />
        public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            foreach (var pc in association.PresentationContexts)
            {
                pc.SetResult(DicomPresentationContextResult.Accept);
            }

            await SendAssociationAcceptAsync(association);
        }

        /// <inheritdoc />
        public async Task OnReceiveAssociationReleaseRequestAsync()
        {
            await SendAssociationReleaseResponseAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
        }

        /// <inheritdoc />
        public void OnConnectionClosed(Exception exception)
        {
        }

        public async Task<IEnumerable<Task<DicomCFindResponse>>> OnCFindRequestAsync(DicomCFindRequest request)
        {
            return InnerOnCFindRequestAsync();

            IEnumerable<Task<DicomCFindResponse>> InnerOnCFindRequestAsync()
            {
                yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Success));
            }
        }
    }

    public class PendingAsyncDicomCFindProvider : DicomService, IDicomServiceProvider, IAsyncDicomCFindProvider
    {
        public PendingAsyncDicomCFindProvider(INetworkStream stream, Encoding fallbackEncoding, Logger log)
            : base(stream, fallbackEncoding, log)
        {
        }

        /// <inheritdoc />
        public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            foreach (var pc in association.PresentationContexts)
            {
                pc.SetResult(DicomPresentationContextResult.Accept);
            }

            await SendAssociationAcceptAsync(association);
        }

        /// <inheritdoc />
        public async Task OnReceiveAssociationReleaseRequestAsync()
        {
            await SendAssociationReleaseResponseAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
        }

        /// <inheritdoc />
        public void OnConnectionClosed(Exception exception)
        {
        }

        public async Task<IEnumerable<Task<DicomCFindResponse>>> OnCFindRequestAsync(DicomCFindRequest request)
        {
            return InnerOnCFindRequestAsync();

            IEnumerable<Task<DicomCFindResponse>> InnerOnCFindRequestAsync()
            {
                yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Pending));
                yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Pending));
                yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Success));
            }
        }
    }


    #endregion
}
