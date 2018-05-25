using System;
using System.Net;
using System.Net.Sockets;

namespace ProxyServer
{
    internal class UdpProxy : IDisposable
    {
        internal readonly ushort port;

        internal readonly IPEndPoint remoteEndPoint;

        private readonly UdpClient remoteUdp;

        private readonly UdpClient localUdp;

        private IPEndPoint clientEndPoint;

        internal bool Closed => closed || Environment.TickCount - lastActiveTime > 180 * 1000;

        private bool closed;

        private int lastActiveTime;

        internal UdpProxy(ushort listenPort, IPEndPoint remote)
        {
            port = listenPort;

            remoteEndPoint = remote;

            lastActiveTime = Environment.TickCount;

            localUdp = new UdpClient(listenPort);

            localUdp.BeginReceive(OnInternalData, null);

            remoteUdp = new UdpClient(remote.Address.ToString(), remote.Port);

            remoteUdp.BeginReceive(OnGameSparksData, null);
        }

        public void Dispose()
        {
            try
            {
                localUdp.Close();
            }
            catch
            {
                //Ignore all
            }

            try
            {
                remoteUdp.Close();
            }
            catch
            {
                //Ignore all
            }

            closed = true;
        }

        private void OnInternalData(IAsyncResult result)
        {
            try
            {
                var message = localUdp.EndReceive(result, ref clientEndPoint);

                remoteUdp.Send(message, message.Length);

                localUdp.BeginReceive(OnInternalData, null);

                lastActiveTime = Environment.TickCount;
            }
            catch
            {
                closed = true;
            }
        }

        private void OnGameSparksData(IAsyncResult result)
        {
            try
            {
                var source = new IPEndPoint(0, 0);
                var message = remoteUdp.EndReceive(result, ref source);

                localUdp.Send(message, message.Length, clientEndPoint);

                remoteUdp.BeginReceive(OnGameSparksData, null);
            }
            catch
            {
                closed = true;
            }
        }
    }
}