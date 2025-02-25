﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.NetworkConnection
{
    internal sealed class KcpNetworkConnectionCallbackManagement
    {
        private CallbackRegistrationNode? _callback;
        private CallbackRegistrationNode? _lastCallback;

        public KcpNetworkConnectionCallbackRegistration Register(IKcpNetworkConnectionCallback callback)
        {
            if (callback is null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var node = new CallbackRegistrationNodeOfInterface(this, callback);
            AddCallback(node);
            return new KcpNetworkConnectionCallbackRegistration(node);
        }

        public void Clear()
        {
            lock (this)
            {
                _callback = null;
                _lastCallback = null;
            }
        }

        private void AddCallback(CallbackRegistrationNode node)
        {
            lock (this)
            {
                if (_callback is null)
                {
                    Debug.Assert(_lastCallback is null);
                    _callback = node;
                    _lastCallback = node;
                }
                else
                {
                    Debug.Assert(_lastCallback is not null);
                    _lastCallback.NextNode = node;
                    _lastCallback = node;
                }
            }
        }

        private void NotifyCallbackReleased(CallbackRegistrationNode node)
        {
            lock (this)
            {
                CallbackRegistrationNode? previous = null;
                CallbackRegistrationNode? current = _callback;
                while (current is not null)
                {
                    if (ReferenceEquals(current, node))
                    {
                        if (previous is null)
                        {
                            Debug.Assert(ReferenceEquals(_callback, node));
                            _callback = current.NextNode;
                        }
                        else
                        {
                            previous.NextNode = current.NextNode;
                        }

                        if (ReferenceEquals(_lastCallback, node))
                        {
                            _lastCallback = previous;
                        }

                        break;
                    }

                    previous = current;
                    current = current.NextNode;
                }
            }
        }

        public async ValueTask PacketReceivedAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            CallbackRegistrationNode? callback = _callback;
            while (callback is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await callback.PacketReceivedAsync(packet, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // TODO how to handle exceptions?
                }
                callback = callback.NextNode;
            }
        }

        public void NotifyStateChanged(KcpNetworkConnection connection)
        {
            CallbackRegistrationNode? callback = _callback;
            while (callback is not null)
            {
                try
                {
                    callback.NotifyStateChanged(connection);
                }
                catch
                {
                    // TODO how to handle exceptions?
                }
                callback = callback.NextNode;
            }

        }

        abstract class CallbackRegistrationNode : IDisposable
        {
            private KcpNetworkConnectionCallbackManagement? _management;

            public CallbackRegistrationNode(KcpNetworkConnectionCallbackManagement management)
            {
                _management = management;
            }

            public CallbackRegistrationNode? NextNode { get; set; }

            public abstract ValueTask PacketReceivedAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
            public abstract void NotifyStateChanged(KcpNetworkConnection connection);

            public virtual void Dispose()
            {
                KcpNetworkConnectionCallbackManagement? management = Interlocked.Exchange(ref _management, null);
                if (management is not null)
                {
                    management.NotifyCallbackReleased(this);
                }
            }
        }

        sealed class CallbackRegistrationNodeOfInterface : CallbackRegistrationNode
        {
            private bool _disposed;
            private IKcpNetworkConnectionCallback? _callback;

            public CallbackRegistrationNodeOfInterface(KcpNetworkConnectionCallbackManagement management, IKcpNetworkConnectionCallback callback) : base(management)
            {
                _callback = callback;
            }

            public override ValueTask PacketReceivedAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
            {
                IKcpNetworkConnectionCallback? callback = Volatile.Read(ref _callback);
                if (callback is not null)
                {
                    return callback.PacketReceivedAsync(packet, cancellationToken);
                }
                return default;
            }

            public override void NotifyStateChanged(KcpNetworkConnection connection)
            {
                IKcpNetworkConnectionCallback? callback = Volatile.Read(ref _callback);
                // T state = _state;
                if (callback is not null && !Volatile.Read(ref _disposed))
                {
                    callback.NotifyStateChanged(connection);
                }
            }

            public override void Dispose()
            {
                Volatile.Write(ref _disposed, true);
                base.Dispose();
                Volatile.Write(ref _callback, null);
                // _state = default!;
            }
        }

    }
}
