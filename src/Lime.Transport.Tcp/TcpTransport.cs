﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lime.Protocol;
using Lime.Protocol.Network;
using Lime.Protocol.Security;
using Lime.Protocol.Serialization;
using Lime.Protocol.Serialization.Newtonsoft;
using System.Buffers;
using System.Security.Cryptography;

namespace Lime.Transport.Tcp
{
    /// <summary>
    /// Provides the messaging protocol transport for TCP connections.
    /// </summary>
    public class TcpTransport : TransportBase, ITransport, IAuthenticatableTransport, IDisposable
    {
        public const int DEFAULT_BUFFER_SIZE = 8192;
        public const int DEFAULT_MAX_BUFFER_SIZE = DEFAULT_BUFFER_SIZE * 1024;

        public static readonly string UriSchemeNetTcp = "net.tcp";
        public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(30);

        private readonly SemaphoreSlim _optionsSemaphore;
        private readonly ITcpClient _tcpClient;
        private readonly IEnvelopeSerializer _envelopeSerializer;
        private readonly ITraceWriter _traceWriter;
        private readonly RemoteCertificateValidationCallback _serverCertificateValidationCallback;
        private readonly RemoteCertificateValidationCallback _clientCertificateValidationCallback;
        private readonly X509Certificate2 _serverCertificate;
        private readonly X509Certificate2 _clientCertificate;
        private readonly JsonBuffer _jsonBuffer;
        private readonly int _maxSenderBufferSize;
        private readonly ArrayPool<byte> _arrayPool;

        private Stream _stream;
        private string _hostName;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpTransport"/> class.
        /// </summary>
        /// <param name="clientCertificate"></param>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <param name="maxBufferSize">Max size of the buffer for increasing.</param>
        /// <param name="traceWriter">The trace writer.</param>
        /// <param name="serverCertificateValidationCallback">A callback to validate the server certificate in the TLS authentication process.</param>
        public TcpTransport(
            X509Certificate2 clientCertificate = null,
            int bufferSize = DEFAULT_BUFFER_SIZE,
            int maxBufferSize = DEFAULT_MAX_BUFFER_SIZE,
            ITraceWriter traceWriter = null,
            RemoteCertificateValidationCallback serverCertificateValidationCallback = null)
            : this(new EnvelopeSerializer(new DocumentTypeResolver()), clientCertificate, bufferSize, maxBufferSize, traceWriter, serverCertificateValidationCallback)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpTransport" /> class.
        /// </summary>
        /// <param name="envelopeSerializer">The envelope serializer.</param>
        /// <param name="clientCertificate">The client certificate.</param>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <param name="maxBufferSize">Max size of the buffer for increasing.</param>
        /// <param name="traceWriter">The trace writer.</param>
        /// <param name="serverCertificateValidationCallback">A callback to validate the server certificate in the TLS authentication process.</param>
        public TcpTransport(
            IEnvelopeSerializer envelopeSerializer,
            X509Certificate2 clientCertificate = null,
            int bufferSize = DEFAULT_BUFFER_SIZE,
            int maxBufferSize = DEFAULT_MAX_BUFFER_SIZE,
            ITraceWriter traceWriter = null,
            RemoteCertificateValidationCallback serverCertificateValidationCallback = null)
            : this(new TcpClientAdapter(new TcpClient()), envelopeSerializer, null, clientCertificate, null, bufferSize, maxBufferSize, null, traceWriter, serverCertificateValidationCallback, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpTransport"/> class.
        /// </summary>
        /// <param name="tcpClient">The TCP client.</param>
        /// <param name="envelopeSerializer">The envelope serializer.</param>
        /// <param name="hostName">Name of the host.</param>
        /// <param name="clientCertificate">The client certificate.</param>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <param name="maxBufferSize">Max size of the buffer for increasing.</param>
        /// <param name="traceWriter">The trace writer.</param>
        /// <param name="serverCertificateValidationCallback">A callback to validate the server certificate in the TLS authentication process.</param>
        public TcpTransport(
            ITcpClient tcpClient,
            IEnvelopeSerializer envelopeSerializer,
            string hostName,
            X509Certificate2 clientCertificate = null,
            int bufferSize = DEFAULT_BUFFER_SIZE,
            int maxBufferSize = DEFAULT_MAX_BUFFER_SIZE,
            ITraceWriter traceWriter = null,
            RemoteCertificateValidationCallback serverCertificateValidationCallback = null)
            : this(tcpClient, envelopeSerializer, null, clientCertificate, hostName, bufferSize, maxBufferSize, null, traceWriter, serverCertificateValidationCallback, null)

        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpTransport"/> class.
        /// This constructor is used by the <see cref="TcpTransportListener"/> class.
        /// </summary>
        /// <param name="tcpClient">The TCP client.</param>
        /// <param name="envelopeSerializer">The envelope serializer.</param>
        /// <param name="serverCertificate">The server certificate.</param>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <param name="maxBufferSize">Max size of the buffer for increasing.</param>
        /// <param name="arrayPool">The array pool instance, to allow reusing the created buffers.</param>
        /// <param name="traceWriter">The trace writer.</param>
        /// <param name="clientCertificateValidationCallback">A callback to validate the client certificate in the TLS authentication process.</param>
        /// <param name="ignoreDeserializationErrors">if set to <c>true</c> the deserialization on received envelopes will be ignored; otherwise, any deserialization error will be throw to the caller.</param>
        internal TcpTransport(
            ITcpClient tcpClient,
            IEnvelopeSerializer envelopeSerializer,
            X509Certificate2 serverCertificate,
            int bufferSize = DEFAULT_BUFFER_SIZE,
            int maxBufferSize = DEFAULT_MAX_BUFFER_SIZE,
            ArrayPool<byte> arrayPool = null,
            ITraceWriter traceWriter = null,
            RemoteCertificateValidationCallback clientCertificateValidationCallback = null)
            : this(tcpClient, envelopeSerializer, serverCertificate, null, null, bufferSize, maxBufferSize, arrayPool, traceWriter, null, clientCertificateValidationCallback)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpTransport"/> class.
        /// </summary>
        /// <param name="tcpClient">The TCP client.</param>
        /// <param name="envelopeSerializer">The envelope serializer.</param>
        /// <param name="serverCertificate">The server certificate.</param>
        /// <param name="clientCertificate">The client certificate.</param>
        /// <param name="hostName">Name of the host.</param>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <param name="maxBufferSize">Max size of the buffer.</param>
        /// <param name="arrayPool">The array pool instance, to allow reusing the created buffers.</param>
        /// <param name="traceWriter">The trace writer.</param>
        /// <param name="serverCertificateValidationCallback">The server certificate validation callback.</param>
        /// <param name="clientCertificateValidationCallback">The client certificate validation callback.</param>
        /// <exception cref="System.ArgumentNullException">
        /// tcpClient
        /// or
        /// envelopeSerializer
        /// </exception>
        private TcpTransport(
            ITcpClient tcpClient,
            IEnvelopeSerializer envelopeSerializer,
            X509Certificate2 serverCertificate,
            X509Certificate2 clientCertificate,
            string hostName,
            int bufferSize,
            int maxBufferSize,
            ArrayPool<byte> arrayPool,
            ITraceWriter traceWriter,
            RemoteCertificateValidationCallback serverCertificateValidationCallback,
            RemoteCertificateValidationCallback clientCertificateValidationCallback)
        {
            _tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
            _envelopeSerializer = envelopeSerializer ?? throw new ArgumentNullException(nameof(envelopeSerializer));
            _serverCertificate = serverCertificate;
            _clientCertificate = clientCertificate;
            _hostName = hostName;
            _arrayPool = arrayPool ?? ArrayPool<byte>.Shared;
            _traceWriter = traceWriter;
            _serverCertificateValidationCallback = serverCertificateValidationCallback ?? ValidateServerCertificate;
            _clientCertificateValidationCallback = clientCertificateValidationCallback ?? ValidateClientCertificate;
            _maxSenderBufferSize = maxBufferSize == 0 ? bufferSize : maxBufferSize;

            _jsonBuffer = new JsonBuffer(bufferSize, maxBufferSize, _arrayPool);
            _optionsSemaphore = new SemaphoreSlim(1);
        }

        /// <summary>
        /// Sends an envelope to the connected node.
        /// </summary>
        /// <param name="envelope">Envelope to be transported</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (_stream == null) throw new InvalidOperationException("The stream was not initialized. Call OpenAsync first.");
            if (!_stream.CanWrite) throw new InvalidOperationException("Invalid stream state");

            var serializedEnvelope = _envelopeSerializer.Serialize(envelope);
            int envelopeByteCount = Encoding.UTF8.GetByteCount(serializedEnvelope);

            if (envelopeByteCount > _maxSenderBufferSize)
            {
                // Prevent sending an envelope that could potentially make the receiver side drop the connection,
                // since most probably it's _maxBufferSize has the same value used by the sender side
                await TraceAsync($"EnvelopeTooLarge (size of {envelopeByteCount}): {serializedEnvelope}", DataOperation.Error).ConfigureAwait(false);
                throw new EnvelopeTooLargeException(
                    $"Envelope NOT sent: {envelope.GetType().Name} with id {envelope.Id} and size {envelopeByteCount} will probably exceed the maximum size supported by a receiver (the local value is {_maxSenderBufferSize})",
                    envelope);
            }

            await TraceAsync(serializedEnvelope, DataOperation.Send).ConfigureAwait(false);

            var buffer = _arrayPool.Rent(envelopeByteCount);
            var length = Encoding.UTF8.GetBytes(serializedEnvelope, 0, serializedEnvelope.Length, buffer, 0);
            
            try
            {
                await _stream.WriteAsync(buffer, 0, length, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await CloseWithTimeoutAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                _arrayPool.Return(buffer);
            }
        }

        /// <summary>
        /// Reads one envelope from the stream.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task<Envelope> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("The stream was not initialized. Call OpenAsync first.");
            if (!_stream.CanRead) throw new InvalidOperationException("Invalid stream state");
            
            try
            {
                Envelope envelope = null;

                while (envelope == null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    envelope = await GetEnvelopeFromBufferAsync().ConfigureAwait(false);

                    if (envelope == null && CanRead)
                    {
                        // The NetworkStream ReadAsync method doesn't supports cancellation
                        // http://stackoverflow.com/questions/21468137/async-network-operations-never-finish
                        // http://referencesource.microsoft.com/#mscorlib/system/io/stream.cs,421
                        
                        // This is no longer true for .NET Core 3.0
                        // https://github.com/dotnet/corefx/pull/36516
                        // The problem persists only with ConnectAsync operations:
                        // https://github.com/dotnet/corefx/issues/37118
                        var read = await _stream
                            .ReadAsync(
                                _jsonBuffer.Buffer,
                                _jsonBuffer.BufferCurPos,
                                _jsonBuffer.Buffer.Length - _jsonBuffer.BufferCurPos,
                                cancellationToken)
                            .ConfigureAwait(false);

                        // https://msdn.microsoft.com/en-us/library/hh193669(v=vs.110).aspx
                        if (read == 0)
                        {
                            await CloseWithTimeoutAsync().ConfigureAwait(false);
                            break;
                        }

                        _jsonBuffer.BufferCurPos += read;

                        if (_jsonBuffer.BufferCurPos >= _jsonBuffer.Buffer.Length)
                        {
                            _jsonBuffer.IncreaseBuffer();
                        }
                    }
                }

                return envelope;
            }
            catch (BufferOverflowException)
            {
                await TraceAsync($"{nameof(RemoteEndPoint)}: {RemoteEndPoint} - BufferOverflow: {Encoding.UTF8.GetString(_jsonBuffer.Buffer)}", DataOperation.Error).ConfigureAwait(false);
                await CloseWithTimeoutAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Enumerates the supported encryption options for the transport.
        /// </summary>
        /// <returns></returns>
        public override SessionEncryption[] GetSupportedEncryption()
        {
            // Server or client mode
            if (_serverCertificate != null ||
                string.IsNullOrWhiteSpace(_hostName))
            {
                return new[]
                {
                    SessionEncryption.None,
                    SessionEncryption.TLS
                };
            }
            return new[]
            {
                SessionEncryption.None
            };
        }

        /// <summary>
        /// Defines the encryption mode for the transport.
        /// </summary>
        /// <param name="encryption"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="System.NotSupportedException"></exception>        
        public override async Task SetEncryptionAsync(SessionEncryption encryption, CancellationToken cancellationToken)
        {
            await _optionsSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                switch (encryption)
                {
                    case SessionEncryption.None:
                        _stream = _tcpClient.GetStream();
                        break;
                    case SessionEncryption.TLS:
                        SslStream sslStream;
                        
                        if (_serverCertificate != null)
                        {
                            if (_stream == null) throw new InvalidOperationException("The stream was not initialized. Call OpenAsync first.");

                            // Server
                            sslStream = new SslStream(
                                _stream,
                                false,
                                _clientCertificateValidationCallback,
                                null,
                                EncryptionPolicy.RequireEncryption);

                            await sslStream
                                .AuthenticateAsServerAsync(
                                    _serverCertificate,
                                    true,
                                    SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
                                    false)
                                .WithCancellation(cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            // Client
                            if (string.IsNullOrWhiteSpace(_hostName))
                            {
                                throw new InvalidOperationException("The hostname is mandatory for TLS client encryption support");
                            }

                            sslStream = new SslStream(
                                _stream,
                                false,
                                 _serverCertificateValidationCallback);

                            X509CertificateCollection clientCertificates = null;

                            if (_clientCertificate != null)
                            {
                                clientCertificates = new X509CertificateCollection(new X509Certificate[] { _clientCertificate });
                            }

                            await sslStream
                                .AuthenticateAsClientAsync(
                                    _hostName,
                                    clientCertificates,
                                    SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
                                    false)
                                .WithCancellation(cancellationToken)
                                .ConfigureAwait(false);
                        }
                        _stream = sslStream;
                        break;

                    default:
                        throw new NotSupportedException();
                }

                Encryption = encryption;
            }
            finally
            {
                _optionsSemaphore.Release();
            }
        }

        /// <summary>
        /// Indicates if the transport is connected.
        /// </summary>
        public override bool IsConnected => _tcpClient.Connected;

        /// <summary>
        /// Gets the local endpoint address.
        /// </summary>
        public override string LocalEndPoint => _tcpClient.Client?.LocalEndPoint.ToString();

        /// <summary>
        /// Gets the remote endpoint address.
        /// </summary>
        public override string RemoteEndPoint => _tcpClient.Client?.RemoteEndPoint.ToString();

        /// <summary>
        /// Gets specific transport metadata information.
        /// </summary>
        public override IReadOnlyDictionary<string, object> Options
        {
            get
            {
                if (_tcpClient.Client == null) return null;
                return new Dictionary<string, object>()
                {
                    {nameof(Socket.AddressFamily), _tcpClient.Client.AddressFamily},
                    {nameof(Socket.Blocking), _tcpClient.Client.Blocking},
                    {nameof(Socket.DontFragment), _tcpClient.Client.DontFragment},
                    {nameof(Socket.ExclusiveAddressUse), _tcpClient.Client.ExclusiveAddressUse},
                    {$"{nameof(Socket.LingerState)}.{nameof(LingerOption.Enabled)}", _tcpClient.Client.LingerState?.Enabled},
                    {$"{nameof(Socket.LingerState)}.{nameof(LingerOption.LingerTime)}", _tcpClient.Client.LingerState?.LingerTime},
                    {nameof(Socket.NoDelay), _tcpClient.Client.NoDelay},
                    {nameof(Socket.ProtocolType), _tcpClient.Client.ProtocolType},
                    {nameof(Socket.ReceiveBufferSize), _tcpClient.Client.ReceiveBufferSize},
                    {nameof(Socket.ReceiveTimeout), _tcpClient.Client.ReceiveTimeout},
                    {nameof(Socket.SendBufferSize), _tcpClient.Client.SendBufferSize},
                    {nameof(Socket.SendTimeout), _tcpClient.Client.SendTimeout},
                    {nameof(Socket.SocketType), _tcpClient.Client.SocketType},
                    {nameof(Socket.Ttl), _tcpClient.Client.Ttl}
                };
            }
        }

        /// <summary>
        /// Sets a transport option value.
        /// </summary>
        /// <param name="name">Name of the option.</param>
        /// <param name="value">The value.</param>
        /// <returns></returns>
        public override Task SetOptionAsync(string name, object value)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (_tcpClient.Client == null) throw new InvalidOperationException("The client state is invalid");

            var names = name.Split('.');

            object obj;

            if (names.Length == 1)
            {
                obj = _tcpClient.Client;
            }
            else if (names.Equals(nameof(Socket.LingerState)) &&
                     names.Length == 2)
            {
                obj = _tcpClient.Client.LingerState;
                name = names[1];
            }
            else
            {
                throw new ArgumentException("Invalid option name", nameof(name));
            }

            var propertyInfo = obj.GetType().GetProperty(name);
            propertyInfo.SetValue(obj, value);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Authenticate the identity in the transport layer.
        /// </summary>
        /// <param name="identity">The identity to be authenticated</param>
        /// <returns>
        /// Indicates if the identity is authenticated
        /// </returns>
        public Task<DomainRole> AuthenticateAsync(Identity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            
            if (!(_stream is SslStream sslStream) || 
                !sslStream.IsAuthenticated || 
                sslStream.RemoteCertificate == null)
            {
                return Task.FromResult(DomainRole.Unknown);
            }

            var certificate = sslStream.RemoteCertificate as X509Certificate2 ?? new X509Certificate2(sslStream.RemoteCertificate);
            return Task.FromResult(certificate.GetDomainRole(identity));
        }

        /// <summary>
        /// Opens the transport connection with the specified Uri and begins to read from the stream.
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        protected override async Task PerformOpenAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_tcpClient.Connected)
            {                
                if (uri == null) throw new ArgumentNullException(nameof(uri), "The uri is mandatory for a not connected TCP client");
                if (uri.Scheme != UriSchemeNetTcp)
                {
                    throw new ArgumentException($"Invalid URI scheme. Expected is '{UriSchemeNetTcp}'.", nameof(uri));
                }

                if (string.IsNullOrWhiteSpace(_hostName))
                {
                    _hostName = uri.Host;
                }

                await _tcpClient.ConnectAsync(uri.Host, uri.Port).ConfigureAwait(false);
            }

            _stream = _tcpClient.GetStream();
        }

        /// <summary>
        /// Closes the transport.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        protected override Task PerformCloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream?.Close();
            _tcpClient.Close();
            return Task.FromResult<object>(null);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _optionsSemaphore.Dispose();
                    _stream?.Dispose();
                    _jsonBuffer.Dispose();
                }

                _disposed = true;
            }
        }

        private async Task<Envelope> GetEnvelopeFromBufferAsync()
        {
            Envelope envelope = null;

            if (_jsonBuffer.TryExtractJsonFromBuffer(out var jsonBytes))
            {
                var envelopeJson = Encoding.UTF8.GetString(jsonBytes);

                await TraceAsync(envelopeJson, DataOperation.Receive).ConfigureAwait(false);
                envelope = _envelopeSerializer.Deserialize(envelopeJson);
            }
            
            return envelope;
        }

        private bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        private bool ValidateClientCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // TODO: Check key usage
            // The client certificate can be null 
            // but if present, must be valid
            if (certificate == null)
            {
                return true;
            }
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        private bool CanRead => _stream != null && _stream.CanRead && _tcpClient.Connected;

        private Task CloseWithTimeoutAsync()
        {
            using (var cts = new CancellationTokenSource(CloseTimeout))
            {
                return CloseAsync(cts.Token);
            }
        }

        private Task TraceAsync(string data, DataOperation operation)
        {
            if (_traceWriter == null || !_traceWriter.IsEnabled) return Task.CompletedTask;
            return _traceWriter.TraceAsync(data, operation);
        }
    }
}