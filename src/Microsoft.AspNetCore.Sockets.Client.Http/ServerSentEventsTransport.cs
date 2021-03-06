// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Sockets.Client.Http;
using Microsoft.AspNetCore.Sockets.Client.Internal;
using Microsoft.AspNetCore.Sockets.Internal.Formatters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.AspNetCore.Sockets.Client
{
    public class ServerSentEventsTransport : ITransport
    {
        private readonly HttpClient _httpClient;
        private readonly HttpOptions _httpOptions;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _transportCts = new CancellationTokenSource();
        private readonly ServerSentEventsMessageParser _parser = new ServerSentEventsMessageParser();

        private IDuplexPipe _application;

        public Task Running { get; private set; } = Task.CompletedTask;

        public TransferMode? Mode { get; private set; }

        public ServerSentEventsTransport(HttpClient httpClient)
            : this(httpClient, null, null)
        { }

        public ServerSentEventsTransport(HttpClient httpClient, HttpOptions httpOptions, ILoggerFactory loggerFactory)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(_httpClient));
            }

            _httpClient = httpClient;
            _httpOptions = httpOptions;
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ServerSentEventsTransport>();
        }

        public Task StartAsync(Uri url, IDuplexPipe application, TransferMode requestedTransferMode, IConnection connection)
        {
            if (requestedTransferMode != TransferMode.Binary && requestedTransferMode != TransferMode.Text)
            {
                throw new ArgumentException("Invalid transfer mode.", nameof(requestedTransferMode));
            }

            _application = application;
            Mode = TransferMode.Text; // Server Sent Events is a text only transport

            _logger.StartTransport(Mode.Value);

            var sendTask = SendUtils.SendMessages(url, _application, _httpClient, _httpOptions, _transportCts, _logger);
            var receiveTask = OpenConnection(_application, url, _transportCts.Token);

            Running = Task.WhenAll(sendTask, receiveTask).ContinueWith(t =>
            {
                _logger.TransportStopped(t.Exception?.InnerException);
                _application.Output.Complete(t.Exception?.InnerException);
                _application.Input.Complete();

                return t;
            }).Unwrap();

            return Task.CompletedTask;
        }

        private async Task OpenConnection(IDuplexPipe application, Uri url, CancellationToken cancellationToken)
        {
            _logger.StartReceive();

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SendUtils.PrepareHttpRequest(request, _httpOptions);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var stream = await response.Content.ReadAsStreamAsync();
            var pipelineReader = StreamPipeConnection.CreateReader(PipeOptions.Default, stream);

            var readCancellationRegistration = cancellationToken.Register(
                reader => ((PipeReader)reader).CancelPendingRead(), pipelineReader);
            try
            {
                while (true)
                {
                    var result = await pipelineReader.ReadAsync();
                    var input = result.Buffer;
                    if (result.IsCancelled || (input.IsEmpty && result.IsCompleted))
                    {
                        _logger.EventStreamEnded();
                        break;
                    }

                    var consumed = input.Start;
                    var examined = input.End;

                    try
                    {
                        var parseResult = _parser.ParseMessage(input, out consumed, out examined, out var buffer);

                        switch (parseResult)
                        {
                            case ServerSentEventsMessageParser.ParseResult.Completed:
                                await _application.Output.WriteAsync(buffer);
                                _parser.Reset();
                                break;
                            case ServerSentEventsMessageParser.ParseResult.Incomplete:
                                if (result.IsCompleted)
                                {
                                    throw new FormatException("Incomplete message.");
                                }
                                break;
                        }
                    }
                    finally
                    {
                        pipelineReader.AdvanceTo(consumed, examined);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.ReceiveCanceled();
            }
            finally
            {
                readCancellationRegistration.Dispose();
                _transportCts.Cancel();
                try
                {
                    stream.Dispose();
                }
                // workaround issue with a null-ref in 2.0
                catch { }
                _logger.ReceiveStopped();
            }
        }

        public async Task StopAsync()
        {
            _logger.TransportStopping();
            _transportCts.Cancel();

            try
            {
                await Running;
            }
            catch
            {
                // exceptions have been handled in the Running task continuation by closing the channel with the exception
            }
        }
    }
}
