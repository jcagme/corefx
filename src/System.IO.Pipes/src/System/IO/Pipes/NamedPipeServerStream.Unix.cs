// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Pipes
{
    /// <summary>
    /// Named pipe server
    /// </summary>
    public sealed partial class NamedPipeServerStream : PipeStream
    {
        private Socket _socket;
        private string _path;
        private PipeDirection _direction;
        private PipeOptions _options;
        private int _inBufferSize;
        private int _outBufferSize;
        private HandleInheritability _inheritability;

        [SecurityCritical]
        private void Create(string pipeName, PipeDirection direction, int maxNumberOfServerInstances,
                PipeTransmissionMode transmissionMode, PipeOptions options, int inBufferSize, int outBufferSize,
                HandleInheritability inheritability)
        {
            Debug.Assert(pipeName != null && pipeName.Length != 0, "fullPipeName is null or empty");
            Debug.Assert(direction >= PipeDirection.In && direction <= PipeDirection.InOut, "invalid pipe direction");
            Debug.Assert(inBufferSize >= 0, "inBufferSize is negative");
            Debug.Assert(outBufferSize >= 0, "outBufferSize is negative");
            Debug.Assert((maxNumberOfServerInstances >= 1) || (maxNumberOfServerInstances == MaxAllowedServerInstances), "maxNumberOfServerInstances is invalid");
            Debug.Assert(transmissionMode >= PipeTransmissionMode.Byte && transmissionMode <= PipeTransmissionMode.Message, "transmissionMode is out of range");

            if (transmissionMode == PipeTransmissionMode.Message)
            {
                throw new PlatformNotSupportedException(SR.PlatformNotSupported_MessageTransmissionMode);
            }

            // NOTE: We don't have a good way to enforce maxNumberOfServerInstances, and don't currently try.
            // It's a Windows-specific concept.

            _path = GetPipePath(".", pipeName);
            _direction = direction;
            _options = options;
            _inBufferSize = inBufferSize;
            _outBufferSize = outBufferSize;
            _inheritability = inheritability;

            // Binding to an existing path fails, so we need to remove anything left over at this location.
            // There's of course a race condition here, where it could be recreated by someone else between this
            // deletion and the bind below, in which case we'll simply let the bind fail and throw.
            Interop.Sys.Unlink(_path); // ignore any failures

            // Start listening for connections on the path.  They'll only be accepted in WaitForConnection{Async}.
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(_path));
                socket.Listen(int.MaxValue);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            _socket = socket;
        }

        public void WaitForConnection()
        {
            CheckConnectOperationsServer();
            if (State == PipeState.Connected)
            {
                throw new InvalidOperationException(SR.InvalidOperation_PipeAlreadyConnected);
            }

            HandleAcceptedSocket(_socket.Accept());
        }

        public Task WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            CheckConnectOperationsServer();
            if (State == PipeState.Connected)
            {
                throw new InvalidOperationException(SR.InvalidOperation_PipeAlreadyConnected);
            }

            return cancellationToken.IsCancellationRequested ?
                Task.FromCanceled(cancellationToken) :
                WaitForConnectionAsyncCore();

            async Task WaitForConnectionAsyncCore() =>
               HandleAcceptedSocket(await _socket.AcceptAsync().ConfigureAwait(false));
        }

        private void HandleAcceptedSocket(Socket acceptedSocket)
        {
            var serverHandle = new SafePipeHandle(acceptedSocket);
            try
            {
                ConfigureSocket(acceptedSocket, serverHandle, _direction, _inBufferSize, _outBufferSize, _inheritability);
            }
            catch
            {
                serverHandle.Dispose();
                acceptedSocket.Dispose();
                throw;
            }

            InitializeHandle(serverHandle, isExposed: false, isAsync: (_options & PipeOptions.Asynchronous) != 0);
            State = PipeState.Connected;
        }

        internal override void DisposeCore(bool disposing)
        {
            Interop.Sys.Unlink(_path); // ignore any failures
            if (disposing)
            {
                _socket?.Dispose();
            }
        }

        [SecurityCritical]
        public void Disconnect()
        {
            CheckDisconnectOperations();
            State = PipeState.Disconnected;
            InternalHandle.Dispose();
            InitializeHandle(null, false, false);
        }

        // Gets the username of the connected client.  Not that we will not have access to the client's 
        // username until it has written at least once to the pipe (and has set its impersonationLevel 
        // argument appropriately). 
        [SecurityCritical]
        public string GetImpersonationUserName()
        {
            CheckWriteOperations();

            SafeHandle handle = InternalHandle?.NamedPipeSocketHandle;
            if (handle == null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_PipeHandleNotSet);
            }

            string name = Interop.Sys.GetPeerUserName(handle);
            if (name != null)
            {
                return name;
            }

            throw CreateExceptionForLastError();
        }

        public override int InBufferSize
        {
            get
            {
                CheckPipePropertyOperations();
                if (!CanRead) throw new NotSupportedException(SR.NotSupported_UnreadableStream);
                return InternalHandle?.NamedPipeSocket?.ReceiveBufferSize ?? _inBufferSize;
            }
        }

        public override int OutBufferSize
        {
            get
            {
                CheckPipePropertyOperations();
                if (!CanWrite) throw new NotSupportedException(SR.NotSupported_UnwritableStream);
                return InternalHandle?.NamedPipeSocket?.SendBufferSize ?? _outBufferSize;
            }
        }

        // -----------------------------
        // ---- PAL layer ends here ----
        // -----------------------------

        // This method calls a delegate while impersonating the client.
        public void RunAsClient(PipeStreamImpersonationWorker impersonationWorker)
        {
            CheckWriteOperations();
            SafeHandle handle = InternalHandle?.NamedPipeSocketHandle;
            if (handle == null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_PipeHandleNotSet);
            }
            // Get the current effective ID to fallback to after the impersonationWorker is run
            uint currentEUID = Interop.Sys.GetEUid();

            // Get the userid of the client process at the end of the pipe
            uint peerID;
            if (Interop.Sys.GetPeerID(handle, out peerID) == -1)
            {
                throw CreateExceptionForLastError();
            }

            // set the effective userid of the current (server) process to the clientid
            if (Interop.Sys.SetEUid(peerID) == -1)
            {
                throw CreateExceptionForLastError();
            }

            try
            {
                impersonationWorker();
            }
            finally
            {
                // set the userid of the current (server) process back to its original value
                Interop.Sys.SetEUid(currentEUID);
            }
        }

        private Exception CreateExceptionForLastError()
        {
            Interop.ErrorInfo error = Interop.Sys.GetLastErrorInfo();
            return error.Error == Interop.Error.ENOTSUP ?
                new PlatformNotSupportedException(SR.Format(SR.PlatformNotSupported_OperatingSystemError, nameof(Interop.Error.ENOTSUP))) :
                Interop.GetExceptionForIoErrno(error, _path);
        }
    }
}
