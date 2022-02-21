// Copyright (c) 2012-2021 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dicom.Helpers;
using Dicom.Log;
using Dicom.Network.Client.EventArguments;
using Xunit;
using Xunit.Abstractions;

namespace Dicom.Network.Client
{
    [Collection("Network"), Trait("Category", "Network")]
    public class DicomClientTimeoutTest
    {
        #region Fields

        private readonly XUnitDicomLogger _logger;

        #endregion

        #region Constructors

        public DicomClientTimeoutTest(ITestOutputHelper testOutputHelper)
        {
            _logger = new XUnitDicomLogger(testOutputHelper)
                .IncludeTimestamps()
                .IncludeThreadId()
                .WithMinimumLevel(LogLevel.Debug);
        }

        #endregion

        #region Unit tests

        private IDicomServer CreateServer<T>(int port) where T : DicomService, IDicomServiceProvider
        {
            var server = DicomServer.Create<T>(port);
            server.Logger = _logger.IncludePrefix(typeof(T).Name).WithMinimumLevel(LogLevel.Debug);
            return server;
        }

        private DicomClient CreateClient(int port)
        {
            var client = new DicomClient("127.0.0.1", port, false, "SCU", "ANY-SCP")
            {
                Logger = _logger.IncludePrefix(typeof(DicomClient).Name).WithMinimumLevel(LogLevel.Debug),
                Options = new DicomServiceOptions()
            };
            return client;
        }

        [Fact]
        public async Task SendingFindRequestToServerThatNeverRespondsShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<NeverRespondingDicomServer>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Patient)
                {
                    Dataset = new DicomDataset
                    {
                        {DicomTag.PatientID, "PAT123"}
                    },
                    OnResponseReceived = (req, res) => throw new Exception("Did not expect a response"),
                };

                DicomRequest.OnTimeoutEventArgs eventArgsFromRequestTimeout = null;
                request.OnTimeout += (sender, args) => eventArgsFromRequestTimeout = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(eventArgsFromRequestTimeout);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.Options.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingMoveRequestToServerThatNeverRespondsShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<NeverRespondingDicomServer>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCMoveRequest("another-AE", "study123")
                {
                    OnResponseReceived = (req, res) => throw new Exception("Did not expect a response")
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.Options.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingFindRequestToServerThatSendsPendingResponsesWithinTimeoutShouldNotTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<FastPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(2);

                DicomCFindResponse lastResponse = null;
                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Patient)
                {
                    Dataset = new DicomDataset
                    {
                        {DicomTag.PatientID, "PAT123"}
                    },
                    OnResponseReceived = (req, res) => { lastResponse = res; }
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(lastResponse);
                Assert.Equal(lastResponse.Status, DicomStatus.Success);
                Assert.Null(onTimeoutEventArgs);
            }
        }

        [Fact]
        public async Task SendingMoveRequestToServerThatSendsPendingResponsesWithinTimeoutShouldNotTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<FastPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(2);

                DicomCMoveResponse lastResponse = null;
                var request = new DicomCMoveRequest("another-AE", "study123")
                {
                    OnResponseReceived = (req, res) => { lastResponse = res; }
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(lastResponse);
                Assert.Equal(lastResponse.Status, DicomStatus.Success);
                Assert.Null(onTimeoutEventArgs);
            }
        }

        [Fact]
        public async Task SendingFindRequestToServerThatSendsPendingResponsesTooSlowlyShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<SlowPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Patient)
                {
                    Dataset = new DicomDataset
                    {
                        {DicomTag.PatientID, "PAT123"}
                    }
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.Options.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingMoveRequestToServerThatSendsPendingResponsesTooSlowlyShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<SlowPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCMoveRequest("another-AE", "study123");

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.Options.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingLargeFileUsingVeryShortResponseTimeoutShouldSucceed()
        {
            var port = Ports.GetNext();
            using (CreateServer<InMemoryDicomCStoreProvider>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(2);
                client.Options.MaxPDULength = 16 * 1024; // 16 KB

                TimeSpan streamWriteTimeout = TimeSpan.FromMilliseconds(10);

                client.NetworkManager = new ConfigurableNetworkManager(() => Thread.Sleep(streamWriteTimeout), () => Task.FromResult(false));

                DicomResponse response = null;

                // Size = 5 192 KB, one PDU = 16 KB, so this will result in 325 PDUs
                // If stream timeout = 50ms, then total time to send will be 3s 250ms
                var request = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response = res,
                };
                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);

                Assert.NotNull(response);

                Assert.Equal(DicomStatus.Success, response.Status);
            }
        }

        [Fact]
        public async Task SendingLargeFileUsingVeryShortResponseTimeoutAndSendingTakesTooLongShouldFail()
        {
            var port = Ports.GetNext();
            using (CreateServer<InMemoryDicomCStoreProvider>(port))
            {
                var client = CreateClient(port);

                client.Options.RequestTimeout = TimeSpan.FromSeconds(1);
                client.Options.MaxPDULength = 16 * 1024;

                TimeSpan streamWriteTimeout = TimeSpan.FromMilliseconds(1500);

                client.NetworkManager = new ConfigurableNetworkManager(() => Thread.Sleep(streamWriteTimeout), () => Task.FromResult(false));

                DicomResponse response = null;
                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;

                // Size = 5 192 KB, one PDU = 16 KB, so this will result in 325 PDUs
                // If stream timeout = 1500ms, then total time to send will be 325 * 1500 = 487.5 seconds
                var request = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response = res,
                    OnTimeout = (sender, args) => onTimeoutEventArgs = args
                };

                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;
                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Same(winner, sendTask);
                Assert.Null(response);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.Options.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Theory]
        [InlineData( /* number of reqs: */ 6, /* max requests per assoc: */ 1, /* async ops invoked: */ 1)]
        [InlineData( /* number of reqs: */ 6, /* max requests per assoc: */ 6, /* async ops invoked: */ 1)]
        [InlineData( /* number of reqs: */ 6, /* max requests per assoc: */ 2, /* async ops invoked: */ 1)]
        [InlineData( /* number of reqs: */ 6, /* max requests per assoc: */ 1, /* async ops invoked: */ 2)]
        [InlineData( /* number of reqs: */ 6, /* max requests per assoc: */ 6, /* async ops invoked: */ 6)]
        public async Task ShouldSendAllRequestsEvenThoughTheyAllTimeOut(int numberOfRequests, int maximumRequestsPerAssociation, int asyncOpsInvoked)
        {
            var options = new
            {
                Requests = numberOfRequests,
                TimeBetweenRequests = TimeSpan.FromMilliseconds(100),
                MaxRequestsPerAssoc = maximumRequestsPerAssociation,
            };

            var port = Ports.GetNext();
            using (CreateServer<NeverRespondingDicomServer>(port))
            {
                var client = CreateClient(port);
                client.NegotiateAsyncOps(asyncOpsInvoked);

                // Ensure the client is quite impatient
                client.Options.RequestTimeout = TimeSpan.FromMilliseconds(200);

                var testLogger = _logger.IncludePrefix("Test");
                testLogger.Info($"Beginning {options.Requests} parallel requests with {options.MaxRequestsPerAssoc} requests / association");

                var requests = new List<DicomRequest>();
                for (var i = 1; i <= options.Requests; i++)
                {
                    var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);

                    requests.Add(request);
                    await client.AddRequestAsync(request).ConfigureAwait(false);

                    if (i < options.Requests)
                    {
                        testLogger.Info($"Waiting {options.TimeBetweenRequests.TotalMilliseconds}ms between requests");
                        await Task.Delay(options.TimeBetweenRequests);
                        testLogger.Info($"Waited {options.TimeBetweenRequests.TotalMilliseconds}ms, moving on to next request");
                    }
                }

                var timedOutRequests = new List<DicomRequest>();
                client.RequestTimedOut += (sender, args) => { timedOutRequests.Add(args.Request); };

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromMinutes(1), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                if (winner != sendTask)
                    throw new Exception("DicomClient.SendAsync timed out");

                Assert.Equal(requests.OrderBy(m => m.MessageID), timedOutRequests.OrderBy(m => m.MessageID));
            }
        }

        [Fact]
        public async Task SendAsync_WithSocketException_ShouldNotLoopInfinitely()
        {
            var port = Ports.GetNext();
            var logger = _logger.IncludePrefix("UnitTest");

            IDicomServer server = null;
            DicomCStoreResponse response1 = null, response2 = null, response3 = null;
            DicomRequest.OnTimeoutEventArgs timeout1 = null, timeout2 = null, timeout3 = null;
            using (CreateServer<InMemoryDicomCStoreProvider>(port))
            {
                var client = CreateClient(port);

                var request1HasArrived = false;
                client.NetworkManager = new ConfigurableNetworkManager(() =>
                {
                    if (request1HasArrived)
                    {
                        throw new IOException("Request 1 has arrived, we can no longer write to this stream!",
                            new SocketException());
                    }
                }, () => Task.FromResult(false));

                // Ensure requests are handled sequentially
                client.NegotiateAsyncOps(1, 1);

                // Size = 5 192 KB, one PDU = 16 KB, so this will result in 325 PDUs
                // If stream timeout = 1500ms, then total time to send will be 325 * 1500 = 487.5 seconds
                var request1 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) =>
                    {
                        request1HasArrived = true;
                        response1 = res;
                    },
                    OnTimeout = (sender, args) => timeout1 = args
                };
                var request2 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response2 = res,
                    OnTimeout = (sender, args) => timeout2 = args
                };
                var request3 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response3 = res,
                    OnTimeout = (sender, args) => timeout3 = args
                };

                await client.AddRequestsAsync(request1, request2, request3).ConfigureAwait(false);

                using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
                {
                    Exception exception = null;
                    try
                    {
                        await client.SendAsync(cancellation.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }

                    Assert.NotNull(exception);

                    Assert.False(cancellation.IsCancellationRequested);
                }
            }

            Assert.NotNull(response1);
            Assert.Null(response2);
            Assert.Null(response3);
            Assert.Null(timeout1);
            Assert.Null(timeout2);
            Assert.Null(timeout3);
        }

        [Fact]
        public async Task SendAsync_WithGenericStreamException_ShouldNotLoopInfinitely()
        {
            var port = Ports.GetNext();
            var logger = _logger.IncludePrefix("UnitTest");

            DicomCStoreResponse response1 = null, response2 = null, response3 = null;
            DicomRequest.OnTimeoutEventArgs timeout1 = null, timeout2 = null, timeout3 = null;
            using (CreateServer<InMemoryDicomCStoreProvider>(port))
            {
                var client = CreateClient(port);
                

                var request1HasArrived = false;
                client.NetworkManager = new ConfigurableNetworkManager(() =>
                {
                    if (request1HasArrived)
                    {
                        throw new Exception("Request 1 has arrived, we can no longer write to this stream!");
                    }
                }, () => Task.FromResult(false));

                // Ensure requests are handled sequentially
                client.NegotiateAsyncOps(1, 1);

                // Size = 5 192 KB, one PDU = 16 KB, so this will result in 325 PDUs
                // If stream timeout = 1500ms, then total time to send will be 325 * 1500 = 487.5 seconds
                var request1 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) =>
                    {
                        request1HasArrived = true;
                        response1 = res;
                    },
                    OnTimeout = (sender, args) => timeout1 = args
                };
                var request2 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response2 = res,
                    OnTimeout = (sender, args) => timeout2 = args
                };
                var request3 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response3 = res,
                    OnTimeout = (sender, args) => timeout3 = args
                };

                await client.AddRequestsAsync(request1, request2, request3).ConfigureAwait(false);

                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    Exception exception = null;
                    try
                    {
                        await client.SendAsync(cancellation.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }

                    Assert.NotNull(exception);
                    Assert.False(cancellation.IsCancellationRequested, "The DicomClient had to be cancelled, this indicates it was stuck in an infinite loop");
                }
            }

            Assert.NotNull(response1);
            Assert.Null(response2);
            Assert.Null(response3);
            Assert.Null(timeout1);
            Assert.Null(timeout2);
            Assert.Null(timeout3);
        }

        [Fact]
        public async Task SendAsync_ServerCrash_ShouldNotLoopInfinitely()
        {
            var port = Ports.GetNext();
            var logger = _logger.IncludePrefix("UnitTest");

            DicomCStoreResponse response1 = null, response2 = null, response3 = null;
            DicomRequest.OnTimeoutEventArgs timeout1 = null, timeout2 = null, timeout3 = null;

            using (var server = CreateServer<InMemoryDicomCStoreProvider>(port))
            {
                var client = CreateClient(port);
                //client.Options.LogDataPDUs = true;
                //client.Options.RequestTimeout = TimeSpan.FromSeconds(8);
                

                var response1Received = false;
                var thrown = false;
                var stillWritingAfterThrow = false;
                Func<Task> onRead = async () =>
                {
                    if (response1Received && !thrown)
                    {
                        thrown = true;
                        throw new IOException("Fail", new SocketException(10060));
                    }

                    if (thrown)
                    {
                        throw new IOException("Fail", new SocketException(10054));
                    }
                };
                Action onStreamWrite = () =>
                {
                    if (thrown)
                    {
                        stillWritingAfterThrow = true;
                        throw new IOException("Fail", new SocketException(10054));
                    }
                };
                client.NetworkManager = new ConfigurableNetworkManager(onStreamWrite, onRead);

                //Bug only occurs when asyncOps > 1
                client.NegotiateAsyncOps(0, 0);

                // Size = 5 192 KB, one PDU = 16 KB, so this will result in 325 PDUs
                // If stream timeout = 1500ms, then total time to send will be 325 * 1500 = 487.5 seconds
                var request1 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) =>
                    {
                        response1Received = true;
                        response1 = res;
                    },
                    OnTimeout = (sender, args) =>
                    {
                        timeout1 = args;
                    }
                };
                var request2 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response2 = res,
                    OnTimeout = (sender, args) =>
                    {
                        timeout2 = args;
                    }
                };
                var request3 = new DicomCStoreRequest(@"./Test Data/10200904.dcm")
                {
                    OnResponseReceived = (req, res) => response3 = res,
                    OnTimeout = (sender, args) => timeout3 = args
                };

                await client.AddRequestsAsync(request1, request2, request3).ConfigureAwait(false);

                //var sendTask = client.SendAsync();
                //var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                //var sendTimeout = Task.Delay(TimeSpan.FromSeconds(15), sendTimeoutCancellationTokenSource.Token);

                //var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                //Assert.Equal(sendTask, winner);

                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    Exception exception = null;
                    Task winner = null;
                    Task sendTask = null;
                    try
                    {
                        sendTask = client.SendAsync(cancellation.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation);
                        var sendTimeout = Task.Delay(TimeSpan.FromMinutes(1));
                        winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);
                        if (winner == sendTask)
                            await sendTask;

                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }

                    Assert.NotNull(winner);
                    Assert.NotNull(sendTask);
                    Assert.True(winner == sendTask, "send Task was not canceled from the Cancellation Token");
                    Assert.NotNull(exception);
                    Assert.False(cancellation.IsCancellationRequested, "The DicomClient had to be cancelled, this indicates it was stuck in an infinite loop");
                }
            }
        }

        #endregion

        #region Support classes

        private class ConfigurableNetworkManager : DesktopNetworkManager
        {
            private readonly Action _onStreamWrite;
            private readonly Func<Task> _onStreamReadAsync;

            public ConfigurableNetworkManager(Action onStreamWrite, Func<Task> onStreamReadAsync)
            {
                _onStreamWrite = onStreamWrite ?? throw new ArgumentNullException(nameof(onStreamWrite));
                _onStreamReadAsync = onStreamReadAsync;
            }

            protected internal override INetworkStream CreateNetworkStreamImpl(string host, int port, bool useTls, bool noDelay, bool ignoreSslPolicyErrors,
                int millisecondsTimeout)
            {
                return new ConfigurableDesktopNetworkStreamDecorator(
                    _onStreamWrite,
                    _onStreamReadAsync,
                    new DesktopNetworkStream(host, port, useTls, noDelay, ignoreSslPolicyErrors, millisecondsTimeout)
                );
            }
        }

        private class ConfigurableDesktopNetworkStreamDecorator : INetworkStream
        {
            private readonly Action _onStreamWrite;
            private readonly Func<Task> _onStreamReadAsync;
            private readonly DesktopNetworkStream _desktopNetworkStream;

            public ConfigurableDesktopNetworkStreamDecorator(Action onStreamWrite, Func<Task> onStreamReadAsync,
                DesktopNetworkStream desktopNetworkStream)
            {
                _onStreamWrite = onStreamWrite ?? throw new ArgumentNullException(nameof(onStreamWrite));
                _onStreamReadAsync = onStreamReadAsync;
                _desktopNetworkStream = desktopNetworkStream;
            }

            public void Dispose()
            {
                _desktopNetworkStream.Dispose();
            }

            public string RemoteHost => _desktopNetworkStream.RemoteHost;

            public string LocalHost => _desktopNetworkStream.LocalHost;

            public int RemotePort => _desktopNetworkStream.RemotePort;

            public int LocalPort => _desktopNetworkStream.LocalPort;

            public Stream AsStream()
            {
                return new ConfigurableStreamDecorator(_onStreamWrite, _onStreamReadAsync, (NetworkStream) _desktopNetworkStream.AsStream());
            }
        }

        private class ConfigurableStreamDecorator : Stream
        {
            private readonly Action _onStreamWrite;
            private readonly Func<Task> _onStreamReadAsync;
            private readonly NetworkStream _inner;

            public ConfigurableStreamDecorator(Action onStreamWrite, Func<Task> onStreamReadAsync, NetworkStream inner)
            {
                _onStreamWrite = onStreamWrite ?? throw new ArgumentNullException(nameof(onStreamWrite));
                _onStreamReadAsync = onStreamReadAsync;
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _inner.ReadTimeout = 10;
            }

            public override void Flush() => _inner.Flush();

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

            public override void SetLength(long value) => _inner.SetLength(value);

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override void Write(byte[] buffer, int offset, int count)
            {
                _onStreamWrite();

                _inner.Write(buffer, offset, count);
            }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => _inner.CanSeek;

            public override bool CanWrite => _inner.CanWrite;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                return _inner.CopyToAsync(destination, bufferSize, cancellationToken);
            }

            public override void Close()
            {
                _inner.Close();
            }

            protected override void Dispose(bool disposing)
            {
                _inner.Dispose();
                base.Dispose(disposing);
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _inner.FlushAsync(cancellationToken);
            }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                return _inner.BeginRead(buffer, offset, count, callback, state);
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                return _inner.EndRead(asyncResult);
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await _onStreamReadAsync();
                return await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                return _inner.BeginWrite(buffer, offset, count, callback, state);
            }

            public override void EndWrite(IAsyncResult asyncResult)
            {
                _inner.EndWrite(asyncResult);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                _onStreamWrite();

                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override int ReadByte()
            {
                return _inner.ReadByte();
            }

            public override void WriteByte(byte value)
            {
                _inner.WriteByte(value);
            }

            public override bool CanTimeout => _inner.CanTimeout;
            public override int ReadTimeout => _inner.ReadTimeout;

            public override int WriteTimeout
            {
                get => _inner.WriteTimeout;
                set => _inner.WriteTimeout = value;
            }

            public override object InitializeLifetimeService()
            {
                return _inner.InitializeLifetimeService();
            }

            public override ObjRef CreateObjRef(Type requestedType)
            {
                return _inner.CreateObjRef(requestedType);
            }

            public override string ToString()
            {
                return _inner.ToString();
            }

            public override bool Equals(object obj)
            {
                return _inner.Equals(obj);
            }

            public override int GetHashCode()
            {
                return _inner.GetHashCode();
            }
        }

        private class InMemoryDicomCStoreProvider : DicomService, IDicomServiceProvider, IDicomCStoreProvider
        {
            public InMemoryDicomCStoreProvider(INetworkStream stream, Encoding fallbackEncoding, Logger log) : base(stream, fallbackEncoding, log) { }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

            public void OnConnectionClosed(Exception exception) { }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public DicomCStoreResponse OnCStoreRequest(DicomCStoreRequest request)
            {
                return new DicomCStoreResponse(request, DicomStatus.Success);
            }

            public void OnCStoreRequestException(string tempFileName, Exception e) { }
        }

        private class NeverRespondingDicomServer : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCMoveProvider
        {
            private readonly ConcurrentBag<DicomRequest> _requests;

            public IEnumerable<DicomRequest> Requests => _requests;

            public NeverRespondingDicomServer(INetworkStream stream, Encoding fallbackEncoding, Logger log) : base(stream, fallbackEncoding, log)
            {
                _requests = new ConcurrentBag<DicomRequest>();
            }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

            public void OnConnectionClosed(Exception exception) { }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public IEnumerable<DicomCFindResponse> OnCFindRequest(DicomCFindRequest request)
            {
                _requests.Add(request);
                yield break;
            }

            public IEnumerable<DicomCMoveResponse> OnCMoveRequest(DicomCMoveRequest request)
            {
                _requests.Add(request);
                yield break;
            }
        }

        private class FastPendingResponsesDicomServer : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCMoveProvider
        {
            public FastPendingResponsesDicomServer(INetworkStream stream, Encoding fallbackEncoding, Logger log) : base(stream, fallbackEncoding, log) { }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

            public void OnConnectionClosed(Exception exception) { }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public IEnumerable<DicomCFindResponse> OnCFindRequest(DicomCFindRequest request)
            {
                Thread.Sleep(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                Thread.Sleep(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                Thread.Sleep(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                Thread.Sleep(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Success);
            }

            public IEnumerable<DicomCMoveResponse> OnCMoveRequest(DicomCMoveRequest request)
            {
                Thread.Sleep(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                Thread.Sleep(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                Thread.Sleep(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                Thread.Sleep(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Success);
            }
        }

        private class SlowPendingResponsesDicomServer : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCMoveProvider
        {
            public SlowPendingResponsesDicomServer(INetworkStream stream, Encoding fallbackEncoding, Logger log) : base(stream, fallbackEncoding, log) { }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

            public void OnConnectionClosed(Exception exception) { }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public IEnumerable<DicomCFindResponse> OnCFindRequest(DicomCFindRequest request)
            {
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                Thread.Sleep(3000);
                yield return new DicomCFindResponse(request, DicomStatus.Success);
            }

            public IEnumerable<DicomCMoveResponse> OnCMoveRequest(DicomCMoveRequest request)
            {
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                Thread.Sleep(3000);
                yield return new DicomCMoveResponse(request, DicomStatus.Success);
            }
        }

        private class FailFastDicomServer : DicomService, IDicomServiceProvider, IDicomCStoreProvider
        {
            public FailFastDicomServer(INetworkStream stream, Encoding fallbackEncoding, Logger log) : base(stream, fallbackEncoding, log)
            {
            }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
                
            }

            public void OnConnectionClosed(Exception exception)
            {
                
            }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public DicomCStoreResponse OnCStoreRequest(DicomCStoreRequest request)
            {
                return new DicomCStoreResponse(request, DicomStatus.Success);
            }

            public void OnCStoreRequestException(string tempFileName, Exception e)
            {
                
            }
        }
        #endregion
    }
}
