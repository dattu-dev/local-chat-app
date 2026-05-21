using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Linq;
using System.Threading.Channels;

class Program
{
    class ClientConnection
    {
        public TcpClient Client { get; }
        public Channel<byte[]> SendChannel { get; }

        public ClientConnection(TcpClient client)
        {
            Client = client;
            SendChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }
    }

    static List<ClientConnection> clients = new List<ClientConnection>();

    static async Task Main(string[] args)
    {
        // Lắng nghe trên tất cả network interfaces
        TcpListener server = new TcpListener(IPAddress.Any, 5000);
        server.Start();

        // Lấy IP thực của máy để hiển thị
        string hostName = Dns.GetHostName();
        string? ipAddress = GetLocalIpAddress();

        Console.WriteLine($"Chat server started on {ipAddress}:5000...");
        Console.WriteLine($"Hostname: {hostName}");
        Console.WriteLine("Waiting for connections...");

        while (true)
        {
            TcpClient client = await server.AcceptTcpClientAsync();
            var connection = new ClientConnection(client);
            
            lock (clients)
            {
                clients.Add(connection);
            }

            Console.WriteLine($"New client connected. Total clients: {clients.Count}");

            _ = HandleClient(connection);
            _ = StartWriterLoop(connection);
        }
    }

    static string? GetLocalIpAddress()
    {
        try
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65432);
                IPEndPoint? endPoint = socket.LocalEndPoint as IPEndPoint;
                return endPoint?.Address.ToString();
            }
        }
        catch
        {
            try
            {
                var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
                return hostEntry.AddressList
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?
                    .ToString();
            }
            catch
            {
                return "127.0.0.1";
            }
        }
    }

    static async Task HandleClient(ClientConnection connection)
    {
        TcpClient client = connection.Client;
        try
        {
            NetworkStream stream = client.GetStream();
            StreamReader reader = new StreamReader(stream);

            while (true)
            {
                string? message = await reader.ReadLineAsync();

                if (message == null)
                    break;

                if (message.Length > 200)
                {
                    Console.WriteLine(message.Substring(0, 100) + "... [Message too long, truncated]");
                }
                else
                {
                    Console.WriteLine(message);
                }

                await Broadcast(message, client);
            }
        }
        catch
        {
            // Clean handling of client disconnection
        }
        finally
        {
            Console.WriteLine("Client disconnected.");
            connection.SendChannel.Writer.Complete();
            lock (clients)
            {
                clients.Remove(connection);
            }
            client.Close();
        }
    }

    static async Task StartWriterLoop(ClientConnection connection)
    {
        try
        {
            NetworkStream stream = connection.Client.GetStream();
            var reader = connection.SendChannel.Reader;

            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out byte[]? buffer))
                {
                    if (buffer != null && connection.Client.Connected)
                    {
                        await stream.WriteAsync(buffer, 0, buffer.Length);
                    }
                }
                await stream.FlushAsync();
            }
        }
        catch
        {
            // Suppress writing errors on disconnected client
        }
        finally
        {
            lock (clients)
            {
                clients.Remove(connection);
            }
            try { connection.Client.Close(); } catch { }
        }
    }

    static async Task Broadcast(string message, TcpClient sender)
    {
        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message + "\n");

        ClientConnection[] activeClients;
        lock (clients)
        {
            activeClients = clients.ToArray();
        }

        foreach (var connection in activeClients)
        {
            try
            {
                // Optimization: skip echoing large [FILE_CHUNK] packets back to the sender
                if (message.StartsWith("[FILE_CHUNK] ") && connection.Client == sender)
                {
                    continue;
                }

                if (connection.Client.Connected)
                {
                    connection.SendChannel.Writer.TryWrite(buffer);
                }
            }
            catch
            {
                // Suppress queue errors
            }
        }
    }
}