﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Ninja.WebSockets;
using UnityEngine;

namespace Mirror.Transport.Websocket
{
    public class Server 
    {
        public event Action<int> Connected;
        public event Action<int, byte[]> ReceivedData;
        public event Action<int> Disconnected;
        public event Action<int, Exception> ReceivedError;

        private const int MaxMessageSize = 256 * 1024;

        // listener
        TcpListener listener;
        private readonly IWebSocketServerFactory webSocketServerFactory = new WebSocketServerFactory();

        CancellationTokenSource cancellation;

        // clients with <connectionId, TcpClient>
        Dictionary<int, WebSocket> clients = new Dictionary<int, WebSocket>();

        public bool NoDelay = true;

        // connectionId counter
        // (right now we only use it from one listener thread, but we might have
        //  multiple threads later in case of WebSockets etc.)
        // -> static so that another server instance doesn't start at 0 again.
        static int counter = 0;

        // public next id function in case someone needs to reserve an id
        // (e.g. if hostMode should always have 0 connection and external
        //  connections should start at 1, etc.)
        public static int NextConnectionId()
        {
            int id = Interlocked.Increment(ref counter);

            // it's very unlikely that we reach the uint limit of 2 billion.
            // even with 1 new connection per second, this would take 68 years.
            // -> but if it happens, then we should throw an exception because
            //    the caller probably should stop accepting clients.
            // -> it's hardly worth using 'bool Next(out id)' for that case
            //    because it's just so unlikely.
            if (id == int.MaxValue)
            {
                throw new Exception("connection id limit reached: " + id);
            }

            return id;
        }

        // check if the server is running
        public bool Active
        {
            get { return listener != null; }
        }

        public WebSocket GetClient(int connectionId)
        {
            // paul:  null is evil,  throw exception if not found
            return clients[connectionId];
        }

        public async void Listen(int port)
        {
            try
            {
                cancellation = new CancellationTokenSource();

                listener = TcpListener.Create(port);
                listener.Server.NoDelay = this.NoDelay;
                listener.Start();
                Debug.Log($"Websocket server started listening on port {port}");
                while (true)
                {
                    TcpClient tcpClient = await listener.AcceptTcpClientAsync();
                    ProcessTcpClient(tcpClient, cancellation.Token);
                }
            }
            catch (ObjectDisposedException)
            {
                // do nothing. This will be thrown if the Listener has been stopped
            }
            catch (Exception ex)
            {
                ReceivedError?.Invoke(0, ex);
            }
        }

        private async void ProcessTcpClient(TcpClient tcpClient, CancellationToken token)
        {

            try
            {
                // this worker thread stays alive until either of the following happens:
                // Client sends a close conection request OR
                // An unhandled exception is thrown OR
                // The server is disposed

                // get a secure or insecure stream
                Stream stream = tcpClient.GetStream();
                WebSocketHttpContext context = await webSocketServerFactory.ReadHttpHeaderFromStreamAsync(stream, token);
                if (context.IsWebSocketRequest)
                {
                    var options = new WebSocketServerOptions() { KeepAliveInterval = TimeSpan.FromSeconds(30), SubProtocol = "binary" };

                    WebSocket webSocket = await webSocketServerFactory.AcceptWebSocketAsync(context, options);

                    await ReceiveLoopAsync(webSocket, token);
                }
                else
                {
                    Debug.Log("Http header contains no web socket upgrade request. Ignoring");
                }

            }
            catch (ObjectDisposedException)
            {
                // do nothing. This will be thrown if the Listener has been stopped
            }
            catch (Exception ex)
            {
                ReceivedError?.Invoke(0, ex);
            }
            finally
            {
                try
                {
                    tcpClient.Client.Close();
                    tcpClient.Close();
                }
                catch (Exception ex)
                {
                    ReceivedError?.Invoke(0, ex);
                }
            }
        }

        private async Task ReceiveLoopAsync(WebSocket webSocket, CancellationToken token)
        {
            int connectionId = NextConnectionId();
            clients.Add(connectionId, webSocket);

            byte[] buffer = new byte[MaxMessageSize];

            try
            {
                // someone connected,  raise event
                Connected?.Invoke(connectionId);


                while (true)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.Log($"Client initiated close. Status: {result.CloseStatus} Description: {result.CloseStatusDescription}");
                        break;
                    }

                    byte[] data = await ReadFrames(connectionId, result, webSocket, buffer, token);

                    if (data == null)
                        break;
                    // we received some data,  raise event
                    ReceivedData?.Invoke(connectionId, data);
                }

            }
            catch (Exception exception)
            {
                ReceivedError?.Invoke(connectionId, exception);
            }
            finally
            {
                clients.Remove(connectionId);
                Disconnected?.Invoke(connectionId);
            }
        }

        // a message might come splitted in multiple frames
        // collect all frames 
        private async Task<byte[]> ReadFrames(int connectionId, WebSocketReceiveResult result, WebSocket webSocket, byte[] buffer, CancellationToken token)
        {
            int count = result.Count;

            while (!result.EndOfMessage)
            {
                if (count >= MaxMessageSize)
                {
                    string closeMessage = string.Format("Maximum message size: {0} bytes.", MaxMessageSize);
                    await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, closeMessage, CancellationToken.None);
                    ReceivedError?.Invoke(connectionId, new WebSocketException(WebSocketError.HeaderError));
                    return null;
                }

                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, count, MaxMessageSize - count), CancellationToken.None);
                count += result.Count;

            }
            return new ArraySegment<byte>(buffer, 0, count).ToArray();
        }

        public void Stop()
        {
            // only if started
            if (!Active) return;

            Debug.Log("Server: stopping...");
            cancellation.Cancel();

            // stop listening to connections so that no one can connect while we
            // close the client connections
            listener.Stop();

            // clear clients list
            clients.Clear();
            listener = null;
        }

        // send message to client using socket connection or throws exception
        public async void Send(int connectionId, byte[] data)
        {
            // find the connection
            WebSocket client;
            if (clients.TryGetValue(connectionId, out client))
            {
                try
                {
                    await client.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, cancellation.Token);
                }
                catch (ObjectDisposedException) {
                    // connection has been closed,  swallow exception
                    Disconnect(connectionId);
                }
                catch (Exception exception)
                {
                    if (clients.ContainsKey(connectionId))
                    {
                        // paul:  If someone unplugs their internet
                        // we can potentially get hundreds of errors here all at once
                        // because all the WriteAsync wake up at once and throw exceptions

                        // by hiding inside this if, I ensure that we only report the first error
                        // all other errors are swallowed.  
                        // this prevents a log storm that freezes the server for several seconds
                        ReceivedError?.Invoke(connectionId, exception);
                    }

                    Disconnect(connectionId);
                }
            }
            else
            {
                ReceivedError?.Invoke(connectionId, new SocketException((int)SocketError.NotConnected));
            }
        }

        // get connection info in case it's needed (IP etc.)
        // (we should never pass the TcpClient to the outside)
        public bool GetConnectionInfo(int connectionId, out string address)
        {
            // find the connection
            WebSocket client;
            if (clients.TryGetValue(connectionId, out client))
            {
                address = "";
                return true;
            }
            address = null;
            return false;
        }

        // disconnect (kick) a client
        public bool Disconnect(int connectionId)
        {
            // find the connection
            WebSocket client;
            if (clients.TryGetValue(connectionId, out client))
            {
                clients.Remove(connectionId);
                // just close it. client thread will take care of the rest.
                client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                Debug.Log("Server.Disconnect connectionId:" + connectionId);
                return true;
            }
            return false;
        }
    }
}