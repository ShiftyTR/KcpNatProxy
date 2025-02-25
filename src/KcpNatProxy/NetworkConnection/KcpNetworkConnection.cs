﻿using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.SocketTransport;
using KcpSharp;

namespace KcpNatProxy.NetworkConnection
{
    public sealed class KcpNetworkConnection : IKcpNetworkApplication, IKcpNetworkConnection, IKcpExceptionProducer<KcpNetworkConnection>, IAsyncDisposable, IDisposable
    {
        private readonly IKcpNetworkEndPointTransport _transport;
        private bool _ownsTransport;
        private KcpSocketNetworkApplicationRegistration _applicationRegistration;
        private readonly IKcpBufferPool _bufferPool;
        private int _mtu;

        private KcpExceptionProducerCore<KcpNetworkConnection> _exceptionProducer;
        private bool _transportClosed;
        private bool _disposed;
        private bool _resetReceived;

        private KcpNetworkConnectionState _state;
        private SpinLock _stateChangeLock;
        private KcpNetworkConnectionCallbackManagement _callbackManagement = new();

        private object _negotiationLock = new();
        private bool _negotiationPacketCachingDisabled;
        private KcpRentedBuffer _cachedNegotiationPacket;

        private KcpNetworkConnectionNegotiationOperation? _negotiationOperation;
        private KcpNetworkConnectionKeepAliveHandler? _keepAliveHandler;

        private uint _nextLocalSerial;
        private long _lastActiveTimeTick;
        private SpinLock _remoteStatisticsLock;
        private uint _nextRemoteSerial;
        private uint _packetsReceived;

        public const int PreBufferSize = 8;

        public KcpNetworkConnectionState State => _state;
        public int Mtu => _mtu;
        public int Mss => Mtu - PreBufferSize;
        public EndPoint? RemoteEndPoint => _transport.RemoteEndPoint;

        public KcpNetworkConnection(IKcpNetworkEndPointTransport transport, KcpNetworkConnectionOptions? options)
        {
            _transport = transport;
            _ownsTransport = false;
            _bufferPool = options?.BufferPool ?? DefaultBufferPool.Instance;
            _mtu = options?.Mtu ?? 1400;

            _transport.SetExceptionHandler((ex, _, state) =>
            {
                var thisObject = (KcpNetworkConnection?)state!;
                thisObject._exceptionProducer.RaiseException(thisObject, ex);
            }, this);
        }

        public KcpNetworkConnection(IKcpNetworkTransport transport, EndPoint remoteEndPoint, KcpNetworkConnectionOptions? options)
        {
            _transport = new KcpNetworkEndPointTransportAdapter(transport, remoteEndPoint);
            _ownsTransport = false;
            _bufferPool = options?.BufferPool ?? DefaultBufferPool.Instance;
            _mtu = options?.Mtu ?? 1400;

            _transport.SetExceptionHandler((ex, _, state) =>
            {
                var thisObject = (KcpNetworkConnection?)state!;
                thisObject._exceptionProducer.RaiseException(thisObject, ex);
            }, this);
        }

        internal KcpNetworkConnection(IKcpNetworkTransport transport, bool ownsTransport, EndPoint remoteEndPoint, KcpNetworkConnectionOptions? options)
        {
            _transport = new KcpNetworkEndPointTransportAdapter(transport, remoteEndPoint);
            _ownsTransport = ownsTransport;
            _bufferPool = options?.BufferPool ?? DefaultBufferPool.Instance;
            _mtu = options?.Mtu ?? 1400;

            if (ownsTransport)
            {
                _transport.SetExceptionHandler((ex, _, state) =>
                {
                    var thisObject = (KcpNetworkConnection?)state!;
                    thisObject._exceptionProducer.RaiseException(thisObject, ex);
                }, this);
            }
        }

        internal void SetApplicationRegistration(KcpSocketNetworkApplicationRegistration applicationRegistration)
        {
            _applicationRegistration = applicationRegistration;
        }

        public void SetExceptionHandler(Func<Exception, KcpNetworkConnection, object?, bool> handler, object? state)
            => _exceptionProducer.SetExceptionHandler(handler, state);

        public static async Task<KcpNetworkConnection> ConnectAsync(EndPoint remoteEndPoint, int sendQueueSize, KcpNetworkConnectionOptions? options = null, CancellationToken cancellationToken = default)
        {
            KcpSocketNetworkTransport? socketTransport = new KcpSocketNetworkTransport(options?.Mtu ?? 1400, options?.BufferPool);
            KcpNetworkConnection? networkConnection = null;
            try
            {
                await socketTransport.ConnectAsync(remoteEndPoint, cancellationToken).ConfigureAwait(false);
                remoteEndPoint = socketTransport.RemoteEndPoint ?? throw new InvalidOperationException();

                networkConnection = new KcpNetworkConnection(socketTransport, true, remoteEndPoint, options);

                socketTransport.RegisterFallback(networkConnection);

                socketTransport.Start(remoteEndPoint, sendQueueSize);

                socketTransport = null;
                return Interlocked.Exchange<KcpNetworkConnection?>(ref networkConnection, null);
            }
            finally
            {
#pragma warning disable CA1508 // Avoid dead conditional code
                networkConnection?.Dispose();
                socketTransport?.Dispose();
#pragma warning restore CA1508 // Avoid dead conditional code
            }
        }

        internal IKcpBufferPool GetAllocator() => _bufferPool;

        internal bool QueueRawPacket(ReadOnlyMemory<byte> packet)
        {
            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _transport.QueuePacket(bufferList);
        }

        internal void NotifyNegotiationResult(KcpNetworkConnectionNegotiationOperation operation, bool success, int? negotiatedMtu)
        {
            Interlocked.CompareExchange(ref _negotiationOperation, null, operation);
            lock (_negotiationLock)
            {
                _negotiationPacketCachingDisabled = true;
                if (_cachedNegotiationPacket.IsAllocated)
                {
                    _cachedNegotiationPacket.Dispose();
                    _cachedNegotiationPacket = default;
                }
            }
            if (_state != KcpNetworkConnectionState.Connecting)
            {
                return;
            }
            if (negotiatedMtu.HasValue)
            {
                _mtu = negotiatedMtu.GetValueOrDefault();
            }
            if (success)
            {
                Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
                ChangeStateTo(KcpNetworkConnectionState.Connected);
            }
            else
            {
                ChangeStateTo(KcpNetworkConnectionState.Failed);

                _applicationRegistration.Dispose();
                _applicationRegistration = default;
            }
        }


        public ValueTask<bool> NegotiateAsync(IKcpConnectionNegotiationContext negotiationContext, CancellationToken cancellationToken = default)
        {
            CheckAndChangeStateTo(KcpNetworkConnectionState.None, KcpNetworkConnectionState.Connecting);
            Debug.Assert(_negotiationOperation is null);
            _negotiationOperation = new KcpNetworkConnectionNegotiationOperation(this, negotiationContext);
            KcpRentedBuffer cachedPacket;
            lock (_negotiationLock)
            {
                _negotiationPacketCachingDisabled = true;
                cachedPacket = _cachedNegotiationPacket;
                _cachedNegotiationPacket = default;
            }
            return new ValueTask<bool>(_negotiationOperation.NegotiateAsync(cachedPacket, cancellationToken));
        }

        public void SkipNegotiation()
        {
            Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
            CheckAndChangeStateTo(KcpNetworkConnectionState.None, KcpNetworkConnectionState.Connecting);
            lock (_negotiationLock)
            {
                _negotiationPacketCachingDisabled = true;
                _cachedNegotiationPacket.Dispose();
                _cachedNegotiationPacket = default;
            }
        }

        public void SetupKeepAlive(TimeSpan interval, TimeSpan expireTimeout)
            => SetupKeepAliveCore(null, interval, expireTimeout);

        public void SetupKeepAlive(IKcpConnectionKeepAliveContext keepAliveContext, TimeSpan interval, TimeSpan expireTimeout)
            => SetupKeepAliveCore(keepAliveContext, interval, expireTimeout);

        private void SetupKeepAliveCore(IKcpConnectionKeepAliveContext? keepAliveContext, TimeSpan? interval, TimeSpan expireTimeout)
        {
            if (_state != KcpNetworkConnectionState.Connected)
            {
                ThrowInvalidOperationException();
            }
            if (_keepAliveHandler is not null)
            {
                ThrowInvalidOperationException();
            }
            _keepAliveHandler = new KcpNetworkConnectionKeepAliveHandler(this, keepAliveContext, interval, expireTimeout);
        }

        internal bool TrySetToDead(long threshold)
        {
            if (_state != KcpNetworkConnectionState.Connected)
            {
                return true;
            }

            if ((long)((ulong)threshold - (ulong)Interlocked.Read(ref _lastActiveTimeTick)) > 0)
            {
                ChangeStateTo(KcpNetworkConnectionState.Dead);

                _applicationRegistration.Dispose();
                _applicationRegistration = default;

                return true;
            }

            return false;
        }

        internal (uint nextRemoteSerial, uint packetsReceived) GatherPacketStatistics()
        {
            bool lockTaken = false;
            try
            {
                _remoteStatisticsLock.Enter(ref lockTaken);

                uint nextRemoteSerial = _nextRemoteSerial;
                uint packetsReceived = _packetsReceived;
                _packetsReceived = 0;

                return (nextRemoteSerial, packetsReceived);
            }
            finally
            {
                if (lockTaken)
                {
                    _remoteStatisticsLock.Exit();
                }
            }
        }

        public KcpNetworkConnectionCallbackRegistration Register(IKcpNetworkConnectionCallback callback)
        {
            if (_disposed)
            {
                ThrowObjectDisposedException();
            }
            return _callbackManagement.Register(callback);
        }

        ValueTask IKcpNetworkApplication.InputPacketAsync(ReadOnlyMemory<byte> packet, EndPoint remoteEndPoint, CancellationToken cancellationToken)
            => InputPacketAsync(packet, cancellationToken);

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> packetSpan = packet.Span;
            if (!packetSpan.IsEmpty && packetSpan[0] == 0xff)
            {
                OnResetReceived();
                return default;
            }

            if (packetSpan.Length < 4)
            {
                return default;
            }

            if (_disposed || _transportClosed)
            {
                return default;
            }

            bool? processResult = null;
            uint? remoteSerial = null;
            ReadOnlyMemory<byte> dataPayload = default;
            if (_state == KcpNetworkConnectionState.None)
            {
                // cache the initial packet for negotiation
                lock (_negotiationLock)
                {
                    if (_negotiationPacketCachingDisabled)
                    {
                        return default;
                    }
                    if (!_cachedNegotiationPacket.IsAllocated)
                    {
                        KcpRentedBuffer rentedBuffer = _bufferPool.Rent(new KcpBufferPoolRentOptions(packet.Length, false));
                        packetSpan.CopyTo(rentedBuffer.Span);
                        _cachedNegotiationPacket = rentedBuffer.Slice(0, packet.Length);
                    }
                }
            }
            else if (_state == KcpNetworkConnectionState.Connecting)
            {
                KcpNetworkConnectionNegotiationOperation? negotiationOperation = Volatile.Read(ref _negotiationOperation);
                if (packetSpan[0] == 1)
                {
                    processResult = negotiationOperation?.InputPacket(packetSpan);
                }
                else
                {
                    processResult = negotiationOperation?.NotifyRemoteProgressing();
                }
            }
            else if (_state == KcpNetworkConnectionState.Connected)
            {
                if (packetSpan[0] == 2)
                {
                    processResult = _keepAliveHandler?.ProcessKeepAlivePacket(packetSpan);
                }
                else if (packetSpan[0] == 3)
                {
                    // payload
                    if (TryParseDataPacketHeader(packetSpan, out ushort length, out uint serial))
                    {
                        dataPayload = packet.Slice(8, length - 4);
                        remoteSerial = serial;
                    }
                }
            }

            if (processResult.GetValueOrDefault())
            {
                Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
            }

            if (remoteSerial.HasValue)
            {
                bool lockTaken = false;
                try
                {
                    _remoteStatisticsLock.Enter(ref lockTaken);

                    if (remoteSerial.GetValueOrDefault() >= _nextRemoteSerial)
                    {
                        _nextRemoteSerial = remoteSerial.GetValueOrDefault() + 1;
                    }
                    _packetsReceived++;
                }
                finally
                {
                    if (lockTaken)
                    {
                        _remoteStatisticsLock.Exit();
                    }
                }
            }

            if (!dataPayload.IsEmpty)
            {
                return _callbackManagement.PacketReceivedAsync(dataPayload, cancellationToken);
            }

            return default;
        }

        public bool Send(KcpBufferList packet)
        {
            using KcpRentedBuffer packetHeader = _bufferPool.Rent(new KcpBufferPoolRentOptions(8, false));
            WriteDataPacketHeader(packetHeader.Span, packet.GetLength(), _nextLocalSerial++);
            return _transport.QueuePacket(packet.AddPreBuffer(packetHeader.Memory.Slice(0, 8)));
        }

        public bool Send(ReadOnlyMemory<byte> packet)
        {
            using var bufferList = KcpRentedBufferList.Allocate(packet);
            using KcpRentedBuffer packetHeader = _bufferPool.Rent(new KcpBufferPoolRentOptions(8, false));
            WriteDataPacketHeader(packetHeader.Span, packet.Length, _nextLocalSerial++);
            return _transport.QueuePacket(bufferList.AddPreBuffer(packetHeader.Memory.Slice(0, 8)));
        }

        public ValueTask SendAsync(KcpBufferList packet, CancellationToken cancellationToken)
        {
            using KcpRentedBuffer packetHeader = _bufferPool.Rent(new KcpBufferPoolRentOptions(8, false));
            WriteDataPacketHeader(packetHeader.Span, packet.GetLength(), _nextLocalSerial++);
            return _transport.QueueAndSendPacketAsync(packet.AddPreBuffer(packetHeader.Memory.Slice(0, 8)), cancellationToken);
        }

        public ValueTask SendPacketWithPreBufferAsync(Memory<byte> packet, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }
            if (packet.Length < PreBufferSize)
            {
                return ValueTask.FromException(new ArgumentException("Buffer must contain space for connection header.", nameof(packet)));
            }

            WriteDataPacketHeader(packet.Span, packet.Length - PreBufferSize, _nextLocalSerial++);
            return _transport.QueueAndSendPacketAsync(packet, cancellationToken);
        }

        private static void WriteDataPacketHeader(Span<byte> buffer, int payloadLength, uint serial)
        {
            if (buffer.Length < PreBufferSize)
            {
                Debug.Fail("Invalid buffer.");
                return;
            }
            buffer[0] = 3;
            buffer[1] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2), (ushort)(payloadLength + 4));
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4), serial);
        }

        private static bool TryParseDataPacketHeader(ReadOnlySpan<byte> packet, out ushort length, out uint serial)
        {
            if (packet.Length < 8 || packet[0] != 3 || packet[1] != 0)
            {
                length = 0;
                serial = 0;
                return false;
            }
            length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2));
            serial = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4));
            if ((packet.Length - 4) < length)
            {
                return false;
            }
            return true;
        }

        private void OnResetReceived()
        {
            _resetReceived = true;
            SetTransportClosed();
        }

        public void SetTransportClosed()
        {
            if (_transportClosed)
            {
                return;
            }
            _transportClosed = true;

            _applicationRegistration.Dispose();
            _applicationRegistration = default;
            ChangeStateTo(KcpNetworkConnectionState.Dead);

            lock (_negotiationLock)
            {
                _negotiationPacketCachingDisabled = true;
                if (_cachedNegotiationPacket.IsAllocated)
                {
                    _cachedNegotiationPacket.Dispose();
                    _cachedNegotiationPacket = default;
                }
            }

            if (_negotiationOperation is not null)
            {
                _negotiationOperation.NotifyDisposed();
                _negotiationOperation = null;
            }
            if (_keepAliveHandler is not null)
            {
                _keepAliveHandler.Dispose();
                _keepAliveHandler = null;
            }
        }

        public async ValueTask SetTransportClosedAsync()
        {
            if (_transportClosed)
            {
                return;
            }
            _transportClosed = true;

            if (!_resetReceived)
            {
                byte[] resetBytes = new byte[] { 0xff };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _transport.QueueAndSendPacketAsync(resetBytes, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                }
            }
            ChangeStateTo(KcpNetworkConnectionState.Dead);

            lock (_negotiationLock)
            {
                _negotiationPacketCachingDisabled = true;
                if (_cachedNegotiationPacket.IsAllocated)
                {
                    _cachedNegotiationPacket.Dispose();
                    _cachedNegotiationPacket = default;
                }
            }

            if (_negotiationOperation is not null)
            {
                _negotiationOperation.NotifyDisposed();
                _negotiationOperation = null;
            }
            if (_keepAliveHandler is not null)
            {
                _keepAliveHandler.Dispose();
                _keepAliveHandler = null;
            }
        }

        private void CheckAndChangeStateTo(KcpNetworkConnectionState expectedState, KcpNetworkConnectionState newState)
        {
            bool lockTaken = false;
            try
            {
                _stateChangeLock.Enter(ref lockTaken);

                if (_state != expectedState)
                {
                    ThrowInvalidOperationException();
                }
                _state = newState;
            }
            finally
            {
                if (lockTaken)
                {
                    _stateChangeLock.Exit();
                }

            }

            _callbackManagement.NotifyStateChanged(this);
        }

        private void ChangeStateTo(KcpNetworkConnectionState newState)
        {
            bool lockTaken = false;
            try
            {
                _stateChangeLock.Enter(ref lockTaken);

                if (_state == newState)
                {
                    return;
                }
                _state = newState;
            }
            finally
            {
                if (lockTaken)
                {
                    _stateChangeLock.Exit();
                }

            }

            _callbackManagement.NotifyStateChanged(this);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            SetTransportClosed();

            if (_ownsTransport)
            {
                _transport.Dispose();
                _ownsTransport = false;
            }
            _applicationRegistration.Dispose();
            _applicationRegistration = default;

            _exceptionProducer.Clear();
            _callbackManagement.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            await SetTransportClosedAsync().ConfigureAwait(false);

            if (_ownsTransport)
            {
                _transport.Dispose();
                _ownsTransport = false;
            }
            _applicationRegistration.Dispose();
            _applicationRegistration = default;

            _exceptionProducer.Clear();
            _callbackManagement.Clear();
        }

        [DoesNotReturn]
        private static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }

        [DoesNotReturn]
        private static void ThrowObjectDisposedException()
        {
            throw new ObjectDisposedException(nameof(KcpNetworkConnection));
        }


    }
}
