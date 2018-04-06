﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lime.Protocol.Network;
using Lime.Protocol.Serialization;
using Lime.Protocol.Serialization.Newtonsoft;
using Lime.Protocol.Server;
using Lime.Protocol.UnitTests;
using Lime.Transport.Tcp;
using Lime.Transport.WebSocket;
using Newtonsoft.Json;
using NUnit.Framework;
using Shouldly;

namespace Lime.Protocol.LoadTests.WebSocket
{

    public class WebSocketTransportBinaryTests : IDisposable
    {
        private Uri _uri;
        private CancellationToken _cancellationToken;
        private IEnvelopeSerializer _envelopeSerializer;
        private ITransportListener _transportListener;
        private ITransport _clientTransport;
        private ITransport _serverTransport;

        public WebSocketTransportBinaryTests()
        {
            var trace = new CustomTraceWriter();
            _uri = new Uri("ws://localhost:8081");
            _cancellationToken = TimeSpan.FromSeconds(30).ToCancellationToken();
            _envelopeSerializer = new FakeEnvelopeSerializer(10);
            _transportListener = new WebSocketTransportListener(_uri, null, _envelopeSerializer, trace, webSocketMessageType: System.Net.WebSockets.WebSocketMessageType.Binary);
            _transportListener.StartAsync(_cancellationToken).Wait();

            var serverTcpTransportTask = _transportListener.AcceptTransportAsync(_cancellationToken);

            _clientTransport = new ClientWebSocketTransport(_envelopeSerializer, trace, 16384, System.Net.WebSockets.WebSocketMessageType.Binary);
            _clientTransport.OpenAsync(_uri, _cancellationToken).Wait();

            _serverTransport = (WebSocketTransport)serverTcpTransportTask.Result;
            _serverTransport.OpenAsync(_uri, _cancellationToken).Wait();
        }

        public void Dispose()
        {
            _clientTransport.CloseAsync(CancellationToken.None);
            _serverTransport.CloseAsync(CancellationToken.None);
            _transportListener.StopAsync(_cancellationToken).Wait();
        }

        [Test]
        public async Task Send100000EnvelopesAsync()
        {
            // Arrange
            var count = 100000;
            var envelopes = Enumerable
                .Range(0, count)
                .Select(i => Dummy.CreateMessage(Dummy.CreateTextContent()));

            var receivedEnvelopes = Enumerable
                .Range(0, count)
                .Select(i => _serverTransport.ReceiveAsync(_cancellationToken))
                .ToArray();

            // Act
            var sw = Stopwatch.StartNew();
            foreach (var envelope in envelopes)
            {
                await _clientTransport.SendAsync(envelope, _cancellationToken);
            }

            await Task.WhenAll(receivedEnvelopes);
            sw.Stop();

            // Assert
            sw.ElapsedMilliseconds.ShouldBeLessThan(count * 2);
        }

        [Test]
        public async Task Send10000EnvelopesAsync()
        {
            // Arrange
            var count = 10000;
            var envelopes = Enumerable
                .Range(0, count)
                .Select(i => Dummy.CreateMessage(Dummy.CreateTextContent()));

            var receivedEnvelopes = Enumerable
                .Range(0, count)
                .Select(i => _serverTransport.ReceiveAsync(_cancellationToken))
                .ToArray();

            // Act
            var sw = Stopwatch.StartNew();
            foreach (var envelope in envelopes)
            {
                await _clientTransport.SendAsync(envelope, _cancellationToken);
            }

            await Task.WhenAll(receivedEnvelopes);
            sw.Stop();

            // Assert
            sw.ElapsedMilliseconds.ShouldBeLessThan(count * 2);
        }

        [Test]
        public async Task Send500EnvelopesAsync()
        {
            // Arrange
            var count = 500;
            var envelopes = Enumerable
                .Range(0, count)
                .Select(i => Dummy.CreateMessage(Dummy.CreateTextContent()));

            // Act
            var sw = Stopwatch.StartNew();
            foreach (var envelope in envelopes)
            {
                await _clientTransport.SendAsync(envelope, _cancellationToken);
            }

            var receivedEnvelopes = Enumerable
                .Range(0, count)
                .Select(i => _serverTransport.ReceiveAsync(_cancellationToken))
                .ToArray();

            await Task.WhenAll(receivedEnvelopes);
            sw.Stop();

            // Assert
            sw.ElapsedMilliseconds.ShouldBeLessThan(count * 2);
        }

        [Test]
        public async Task Send200EnvelopesAsync()
        {
            // Arrange
            var count = 200;
            var envelopes = Enumerable
                .Range(0, count)
                .Select(i => Dummy.CreateMessage(Dummy.CreateTextContent()));

            // Act
            var sw = Stopwatch.StartNew();
            foreach (var envelope in envelopes)
            {
                await _clientTransport.SendAsync(envelope, _cancellationToken);
            }

            var receivedEnvelopes = Enumerable
                .Range(0, count)
                .Select(i => _serverTransport.ReceiveAsync(_cancellationToken))
                .ToArray();

            await Task.WhenAll(receivedEnvelopes);
            sw.Stop();

            // Assert
            sw.ElapsedMilliseconds.ShouldBeLessThan(count * 2);
        }

        [Test]
        public async Task SendHugeEnvelope()
        {
            // Act
            var sw = Stopwatch.StartNew();

            var path = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory);
            var content = File.ReadAllLines(Path.Combine(path, "huge-json.txt"));
            var envelope = JsonConvert.DeserializeObject<Command>(string.Join("", content));

            await _clientTransport.SendAsync(envelope, _cancellationToken);

            var receivedEnvelopes = Enumerable
                .Range(0, 1)
                .Select(i => _serverTransport.ReceiveAsync(_cancellationToken))
                .ToArray();

            await Task.WhenAll(receivedEnvelopes);
            sw.Stop();

            // Assert
            sw.ElapsedMilliseconds.ShouldBeLessThan(100);
        }
    }
}
