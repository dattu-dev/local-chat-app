using System;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        private TcpClient? client;
        private StreamReader? reader;
        private StreamWriter? writer;
        private string username = "";

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            username = UsernameTextBox.Text.Trim();
            string serverIp = ServerIpTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(username))
            {
                MessageBox.Show("Please enter username.");
                return;
            }

            if (string.IsNullOrWhiteSpace(serverIp))
            {
                MessageBox.Show("Please enter server IP address.");
                return;
            }

            try
            {
                client = new TcpClient();

                // Set timeout để tránh hang vô hạn
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(serverIp, 5000, cts.Token);
                }

                NetworkStream stream = client.GetStream();

                // Set read/write timeout
                stream.ReadTimeout = 30000;  // 30 seconds
                stream.WriteTimeout = 30000; // 30 seconds

                reader = new StreamReader(stream);
                writer = new StreamWriter(stream)
                {
                    AutoFlush = true
                };

                StatusTextBlock.Text = "Online";
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;

                ChatListBox.Items.Add($"Connected to server: {serverIp}:5000");
                ChatListBox.Items.Add($"Welcome, {username}!");

                _ = ReceiveMessages();
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Connection timeout! Server not responding. Check:\n" +
                    "1. Server IP address\n" +
                    "2. Server is running\n" +
                    "3. Firewall allows port 5000");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cannot connect to server:\n" + ex.Message);
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessage();
        }

        private async Task SendMessage()
        {
            if (writer == null || client == null || !client.Connected)
            {
                MessageBox.Show("You must connect first.");
                return;
            }

            string message = MessageTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                string time = DateTime.Now.ToString("HH:mm");
                string fullMessage = $"[{time}] {username}: {message}";

                await writer.WriteLineAsync(fullMessage);
                await writer.FlushAsync();

                MessageTextBox.Clear();
                MessageTextBox.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error sending message: " + ex.Message);
            }
        }

        private async Task ReceiveMessages()
        {
            try
            {
                while (true)
                {
                    string? message = await reader!.ReadLineAsync();

                    if (message == null)
                    {
                        break;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        ChatListBox.Items.Add(message);
                        ChatListBox.ScrollIntoView(message);
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusTextBlock.Text = "Offline";
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;

                    ChatListBox.Items.Add("Disconnected from server: " + ex.Message);
                });
            }
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                MessageTextBox.Text += button.Content.ToString();
                MessageTextBox.Focus();
                MessageTextBox.CaretIndex = MessageTextBox.Text.Length;
            }
        }

        private async void MessageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await SendMessage();
            }
        }
    }
}