using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ProxyServer
{
    internal static class Program
    {
        private const byte ProtocolStringLength = 3;

        private static void OnData(IAsyncResult result)
        {
            var socket = (UdpClient) result.AsyncState;

            var clientEndPoint = new IPEndPoint(0, 0);

            byte[] data;
            try
            {
                data = socket.EndReceive(result, ref clientEndPoint);
            }
            catch
            {
                socket.BeginReceive(OnData, socket);

                return;
            }
            
            if (data != null && data.Length > ProtocolStringLength + 1)
            {
                var request = Encoding.ASCII.GetString(data);

                var args = request.Substring(ProtocolStringLength).Split(new[] { ":" }, StringSplitOptions.RemoveEmptyEntries);

                var protocol = request.Substring(0, ProtocolStringLength);

                if (args.Length == 2 && protocol == "tcp" || args.Length == 3 && protocol == "udp")
                {
                    if (IPAddress.TryParse(args[0], out var ip) && ushort.TryParse(args[1], out var port))
                    {
                        var proxyPort = protocol == "tcp" ? GetTcpProxyPort(new IPEndPoint(ip, port)) : GetUdpProxyPort(args[2], new IPEndPoint(ip, port));

                        var response = request + "/" + proxyPort;

                        var responseData = Encoding.ASCII.GetBytes(response);

                        socket.Send(responseData, responseData.Length, clientEndPoint);
                    }
                }
            }

            socket.BeginReceive(OnData, socket);
        }

        private static ushort GetTcpProxyPort(IPEndPoint endPoint)
        {
            lock (TcpPortMap)
            {
                if (TcpPortMap.TryGetValue(endPoint.ToString(), out var port)) return port;

                port = ++lastTcpPort;

                TcpPortMap.Add(endPoint.ToString(), port);

                File.AppendAllLines("tcp_port_map.txt", new[] { endPoint + "/" + port });

                var fileContent = $"server {{\n\nlisten {port};\ntcp_nodelay on;\nproxy_pass {endPoint};\n\n}}";

                File.WriteAllText(GetPath("tcp/" + port), fileContent);

                ReloadNginxConfig();

                return port;
            }
        }

        private static ushort GetUdpProxyPort(string id, IPEndPoint endPoint)
        {
            lock (UdpPortMap)
            {
                if (UdpPortMap.TryGetValue(endPoint + id, out var proxy))
                {
                    if (!proxy.Closed) return proxy.port;

                    Console.WriteLine(DateTime.Now + "Invalid request: Proxy already closed! Server: " + endPoint + " ID: " + id);

                    return InvalidUdpPort;
                }

                ushort port;
                if (FreeUdpPorts.Count > 0) port = FreeUdpPorts.Dequeue();
                else port = ++lastUdpPort;

                proxy = new UdpProxy(port, endPoint);

                UdpPortMap.Add(endPoint + id, proxy);

                Console.WriteLine(DateTime.Now + "Created proxy " + proxy.remoteEndPoint + "/" + proxy.port);

                return proxy.port;
            }
        }

        private static readonly Dictionary<string, ushort> TcpPortMap = new Dictionary<string, ushort>();

        private static readonly Dictionary<string, UdpProxy> UdpPortMap = new Dictionary<string, UdpProxy>();
        private static readonly Queue<ushort> FreeUdpPorts = new Queue<ushort>();

        private static bool nginxEnvironment;

        private static ushort lastTcpPort = 1000;
        private static ushort lastUdpPort = 5000;
        private const ushort InvalidUdpPort = 4999;

        private static string GetPath(string path)
        {
            if (nginxEnvironment) return "/etc/nginx/" + path;

            return path;
        }

        private static void ReloadNginxConfig()
        {
            if (!nginxEnvironment)
            {
                Console.WriteLine("Nginx not found!");

                return;
            }

            var info = new ProcessStartInfo
            {
                FileName = "sudo",
                Arguments = "nginx -s reload",
                UseShellExecute = false
            };

            using (var p = Process.Start(info))
            {
                p?.WaitForExit();
            }
        }

        private static void CheckUdpProxies()
        {
            while (true)
            {
                lock (UdpPortMap)
                {
                    StartCheck:

                    foreach (var entry in UdpPortMap)
                    {
                        if (entry.Value.Closed)
                        {
                            FreeUdpPorts.Enqueue(entry.Value.port);

                            entry.Value.Dispose();

                            UdpPortMap.Remove(entry.Key);

                            Console.WriteLine(DateTime.Now + " Deleted proxy " + entry.Value.remoteEndPoint + "/" + entry.Value.port);

                            goto StartCheck;
                        }
                    }
                }

                Thread.Sleep(10000);
            }
        }

        private static void Main()
        {
            nginxEnvironment = Directory.Exists("/etc/nginx/");

            ReloadNginxConfig();

            if (File.Exists("tcp_port_map.txt"))
            {
                var map = File.ReadAllLines("tcp_port_map.txt");

                var count = 0;
                foreach (var entry in map)
                {
                    if(string.IsNullOrEmpty(entry)) continue;

                    count++;

                    var args = entry.Split('/');

                    var port = ushort.Parse(args[1]);

                    if (port > lastTcpPort) lastTcpPort = port;

                    TcpPortMap.Add(args[0], port);
                }

                Console.WriteLine("Loaded TCP port map with " + count + " entries.");
            }

            new Thread(CheckUdpProxies)
            {
                IsBackground = true
            }.Start();

            const int listenPort = 20000;

            var socket = new UdpClient(listenPort);

            socket.BeginReceive(OnData, socket);

            Console.WriteLine("Address server started! Port: " + listenPort);

            while (true)
            {
                Console.WriteLine("Type 'q', 'quit' or 'exit' to stop.");

                var input = Console.ReadLine()?.ToLower();

                if(input == null) continue;

                if (input == "q" || input.Contains("quit") || input.Contains("exit")) return;
            }
        }
    }
}