using System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Linq;

class Program
{
    static List<TcpClient> clients = new List<TcpClient>();

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
            clients.Add(client);

            Console.WriteLine($"New client connected. Total clients: {clients.Count}");

            _ = HandleClient(client);
        }
    }

    static string? GetLocalIpAddress()
    {
        try
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65432);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
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

    static async Task HandleClient(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            StreamReader reader = new StreamReader(stream);

            while (true)
            {
                string? message = await reader.ReadLineAsync();

                if (message == null)
                    break;

                Console.WriteLine(message);
                await Broadcast(message, client);
            }
        }
        catch
        {
            Console.WriteLine("Client disconnected.");
        }
        finally
        {
            clients.Remove(client);
            client.Close();
        }
    }

    static async Task Broadcast(string message, TcpClient sender)
    {
        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message + "\n");

        foreach (TcpClient client in clients.ToArray())
        {
            try
            {
                if (client.Connected)
                {
                    NetworkStream stream = client.GetStream();
                    await stream.WriteAsync(buffer, 0, buffer.Length);
                    await stream.FlushAsync();
                }
            }
            catch
            {
                clients.Remove(client);
                try { client.Close(); } catch { }
            }
        }
    }
}