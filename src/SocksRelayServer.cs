﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SocksRelayServer.Dns;
using SocksRelayServer.Exception;
using SocksRelayServer.Relay;

namespace SocksRelayServer
{
    public class SocksRelayServer : ISocksRelayServer
    {
        private Socket _serverSocket;
        private Thread _acceptThread;
        private bool _serverStarted;

        public SocksRelayServer(IPEndPoint localEndPoint, IPEndPoint remoteProxyEndPoint)
        {
            if (Equals(localEndPoint, remoteProxyEndPoint))
            {
                throw new SocksRelayServerException("LocalEndPoint and RemoteEndPoint cannot be the same");
            }

            BufferSize = 8192;
            LocalEndPoint = localEndPoint;
            RemotEndPoint = remoteProxyEndPoint;
            SendTimeout = 0;
            ReceiveTimeout = 0;

            ResolveHostnamesRemotely = false;
            DnsResolver = new DefaultDnsResolver();
        }

        public event EventHandler<DnsEndPoint> OnLocalConnect;

        public event EventHandler<DnsEndPoint> OnRemoteConnect;
        
        public event EventHandler<System.Exception> OnError;

        public event EventHandler<string> OnLogMessage;

        public IDnsResolver DnsResolver { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public int BufferSize { get; set; }

        public bool ResolveHostnamesRemotely { get; set; }

        public IPEndPoint LocalEndPoint { get; }

        public IPEndPoint RemotEndPoint { get; }

        public int SendTimeout { get; set; }

        public int ReceiveTimeout { get; set; }

        public bool IsAcceptOnCurrentThread { get; set; }

        public void Start()
        {
            if (!ResolveHostnamesRemotely && DnsResolver == null)
            {
                throw new SocksRelayServerException("DnsResolver property cannot be null when using Local DNS resolution");
            }

            SetupServerSocket();

            _serverStarted = true;
            if (IsAcceptOnCurrentThread)
            {
                AcceptConnections();
            }
            else
            {
                _acceptThread = new Thread(AcceptConnections) { IsBackground = true };
                _acceptThread.Start();
            }
        }

        public void Stop()
        {
            _serverStarted = false;

            if (_serverSocket.Connected)
            {
                _serverSocket.Dispose();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static void SendSocks4Reply(Socket socket, byte statusCode, IReadOnlyList<byte> address, IReadOnlyList<byte> portNumber)
        {
            var response = new byte[]
            {
                0x00,
                statusCode,
                portNumber[0], portNumber[1],
                address[0], address[1], address[2], address[3],
            };

            socket.Send(response);
        }

        private static bool IsSocks4AProtocol(IReadOnlyList<byte> ip)
        {
            return ip[0] == 0 && ip[1] == 0 && ip[2] == 0 && ip[3] > 0;
        }

        private void SetupServerSocket()
        {
            // Create the socket, bind it, and start listening
            _serverSocket = new Socket(LocalEndPoint.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _serverSocket.Bind(LocalEndPoint);
            _serverSocket.Listen(1024);
        }

        private void AcceptConnections()
        {
            try
            {
                while (_serverStarted)
                {
                    // Accept a connection
                    var connection = new ConnectionInfo();

                    var socket = _serverSocket.Accept();
                    socket.ReceiveTimeout = ReceiveTimeout;
                    socket.SendTimeout = SendTimeout;

                    connection.LocalSocket = socket;
                    connection.RemoteSocket =
                        new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                        {
                            SendTimeout = SendTimeout,
                            ReceiveTimeout = ReceiveTimeout,
                        };

                    // Create the thread for the receive
                    var thread = new Thread(ProcessLocalConnection) { IsBackground = true };
                    thread.Start(connection);

                    var remoteIpEndPoint = (IPEndPoint)connection.LocalSocket.RemoteEndPoint;
                    OnLocalConnect?.Invoke(this,
                        new DnsEndPoint(remoteIpEndPoint.Address.ToString(), remoteIpEndPoint.Port,
                            remoteIpEndPoint.AddressFamily));
                }

                _serverSocket.Close();
            }
            catch (System.Exception ex)
            {
                // I get ObjectDisposedException from time to time
                OnError?.Invoke(this, ex);
                Stop();
                _serverSocket.Dispose();
            }
        }

        private async void ProcessLocalConnection(object state)
        {
            var connection = (ConnectionInfo)state;
            var buffer = new byte[BufferSize];

            try
            {
                var bytesRead = connection.LocalSocket.Receive(buffer);
                if (bytesRead < 1 || buffer[0] != Protocol.Socks4.Version)
                {
                    connection.Terminate();
                    return;
                }

                OnLogMessage?.Invoke(this, $"Got {bytesRead} bytes from ");
            }
            catch (SocketException ex)
            {
                OnLogMessage?.Invoke(this, $"Caught SocketException in ProcessLocalConnection with error code {ex.SocketErrorCode}");
            }

            try
            {
                switch (buffer[1])
                {
                    case Protocol.Socks4.CommandStreamConnection:
                    {
                        var portBuffer = new[] { buffer[2], buffer[3] };
                        var port = (ushort)(portBuffer[0] << 8 | portBuffer[1]);

                        var address = new[] { buffer[4], buffer[5], buffer[6], buffer[7] };
                        var destAddress = new IPAddress(address).ToString();
                        var destAddressFamily = AddressFamily.InterNetwork;

                        if (IsSocks4AProtocol(address))
                        {
                            var hostBuffer = new byte[256];
                            Buffer.BlockCopy(buffer, 9, hostBuffer, 0, 100);

                            // Resolve hostname, fallback to remote proxy dns resolution
                            var hostname = Encoding.ASCII.GetString(hostBuffer).TrimEnd((char)0);
                            if (!ResolveHostnamesRemotely)
                            {
                                var resolvedHostname = await DnsResolver.TryResolve(hostname);
                                if (resolvedHostname == null)
                                {
                                    OnLogMessage?.Invoke(this, $"DNS resolution failed for {hostname}");
                                    SendSocks4Reply(connection.LocalSocket, Protocol.Socks4.StatusRequestGranted, address, portBuffer);
                                    connection.Terminate();
                                    break;
                                }

                                destAddress = resolvedHostname.ToString();
                                destAddressFamily = resolvedHostname.AddressFamily;
                            }
                            else
                            {
                                destAddress = hostname;
                                destAddressFamily = AddressFamily.Unspecified;
                            }
                        }

                        connection.RemoteSocket = Socks5Client.Connect(
                            RemotEndPoint.Address.ToString(),
                            RemotEndPoint.Port,
                            destAddress,
                            port,
                            Username,
                            Password,
                            SendTimeout,
                            ReceiveTimeout);

                        OnRemoteConnect?.Invoke(this, new DnsEndPoint(destAddress, port, destAddressFamily));

                        if (connection.RemoteSocket.Connected)
                        {
                            OnLogMessage?.Invoke(this, "RelayBiDirectionally between server and client started");
                            SendSocks4Reply(connection.LocalSocket, Protocol.Socks4.StatusRequestGranted, address, portBuffer);
                            SocketRelay.RelayBiDirectionally(connection.RemoteSocket, connection.LocalSocket);
                        }
                        else
                        {
                            OnLogMessage?.Invoke(this, "RemoteSocket connection failed");
                            SendSocks4Reply(connection.LocalSocket, Protocol.Socks4.StatusRequestFailed, address, portBuffer);
                            connection.Terminate();
                        }

                        break;
                    }

                    case Protocol.Socks4.CommandBindingConnection:
                    {
                        var portBuffer = new[] { buffer[2], buffer[3] };
                        var address = new[] { buffer[4], buffer[5], buffer[6], buffer[7] };

                        // TCP/IP port binding not supported
                        SendSocks4Reply(connection.LocalSocket, Protocol.Socks4.StatusRequestFailed, address, portBuffer);
                        connection.Terminate();
                        break;
                    }

                    default:
                        OnLogMessage?.Invoke(this, "Unknown protocol on LocalSocket");
                        connection.Terminate();
                        break;
                }
            }
            catch (SocketException ex)
            {
                OnLogMessage?.Invoke(this, $"Caught SocketException in ProcessLocalConnection with error code {ex.SocketErrorCode.ToString()}");
            }
            catch (Socks5Exception ex)
            {
                var portBuffer = new[] { buffer[2], buffer[3] };
                var address = new[] { buffer[4], buffer[5], buffer[6], buffer[7] };

                SendSocks4Reply(connection.LocalSocket, Protocol.Socks4.StatusRequestFailed, address, portBuffer);
                connection.Terminate();

                OnLogMessage?.Invoke(this, $"Caught Socks5Exception in ProcessLocalConnection with message {ex.Message}");
            }
        }
    }
}
