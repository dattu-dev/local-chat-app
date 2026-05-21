using System;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using System.Collections.ObjectModel;

namespace ChatClient
{
    public class StringToImageSourceConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
                    bmp.EndInit();
                    return bmp;
                }
                catch { }
            }
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class PendingAttachment
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName => System.IO.Path.GetFileName(FilePath);
        public bool IsImage { get; set; }
        public string DisplayIcon => IsImage ? FilePath : null;
    }

    public class ChatMessageItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _text = string.Empty;
        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(nameof(Text)); OnPropertyChanged(nameof(HasText)); }
        }
        public bool HasText => !string.IsNullOrEmpty(Text);

        private string _imageSource = null;
        public string ImageSource
        {
            get => _imageSource;
            set { _imageSource = value; OnPropertyChanged(nameof(ImageSource)); OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(HasFile)); }
        }
        public bool HasImage => !string.IsNullOrEmpty(ImageSource) && !IsTransferring;

        private string _filePath = string.Empty;
        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(nameof(FilePath)); }
        }

        private string _fileName = string.Empty;
        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(HasFile)); }
        }
        public bool HasFile => !string.IsNullOrEmpty(FileName) && !IsTransferring && string.IsNullOrEmpty(ImageSource);

        public HorizontalAlignment Alignment { get; set; }
        public Brush BackgroundColor { get; set; } = Brushes.Transparent;
        public Brush TextColor { get; set; } = Brushes.Black;

        private bool _isTransferring;
        public bool IsTransferring
        {
            get => _isTransferring;
            set { _isTransferring = value; OnPropertyChanged(nameof(IsTransferring)); OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(HasFile)); }
        }

        private double _progressPercentage;
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set { _progressPercentage = value; OnPropertyChanged(nameof(ProgressPercentage)); }
        }

        private string _progressText = string.Empty;
        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(nameof(ProgressText)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class MainWindow : Window
    {
        private TcpClient? client;
        private StreamReader? reader;
        private StreamWriter? writer;
        private string username = "";
        
        private SemaphoreSlim sendSemaphore = new SemaphoreSlim(1, 1);
        private ObservableCollection<PendingAttachment> pendingAttachments = new ObservableCollection<PendingAttachment>();
        private Dictionary<string, FileStream> activeDownloads = new Dictionary<string, FileStream>();
        private Dictionary<string, ChatMessageItem> activeProgressItems = new Dictionary<string, ChatMessageItem>();
        private Dictionary<string, long> activeTotalBytes = new Dictionary<string, long>();
        private Dictionary<string, long> activeBytesReceived = new Dictionary<string, long>();
        private Dictionary<string, double> activeLastUiPercentage = new Dictionary<string, double>();

        public MainWindow()
        {
            InitializeComponent();
            PendingAttachmentsControl.ItemsSource = pendingAttachments;
            pendingAttachments.CollectionChanged += PendingAttachments_CollectionChanged;
        }

        private void PendingAttachments_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            PendingAttachmentsControl.Visibility = pendingAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddSystemMessage(string text)
        {
            var item = new ChatMessageItem
            {
                Text = text,
                Alignment = HorizontalAlignment.Center,
                BackgroundColor = Brushes.Transparent,
                TextColor = Brushes.Gray
            };
            ChatListBox.Items.Add(item);
            ChatListBox.ScrollIntoView(item);
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
                
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                UsernameTextBox.IsEnabled = false;
                ServerIpTextBox.IsEnabled = false;

                AddSystemMessage($"Connected to server: {serverIp}:5000");

                // Broadcast join message
                await writer.WriteLineAsync($"[SYSTEM]{username} joined the chat.");
                await writer.FlushAsync();

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

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (writer != null && client != null && client.Connected)
            {
                try
                {
                    await writer.WriteLineAsync($"[SYSTEM]{username} left the chat.");
                    await writer.FlushAsync();
                }
                catch { }
            }
            
            Disconnect();
        }

        private void Disconnect()
        {
            try
            {
                if (client != null)
                {
                    client.Close();
                    client = null;
                }
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = "Offline";
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                UsernameTextBox.IsEnabled = true;
                ServerIpTextBox.IsEnabled = true;
                AddSystemMessage("Disconnected.");
            });
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
            var imagesToSend = pendingAttachments.ToList();

            if (string.IsNullOrWhiteSpace(message) && imagesToSend.Count == 0)
                return;

            MessageTextBox.Text = "";
            pendingAttachments.Clear();
            MessageTextBox.Focus();
            string time = DateTime.Now.ToString("HH:mm");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (imagesToSend.Count == 0)
                    {
                        // Text only
                        string fullMessage = $"[{time}] {username}: {message}";
                        await sendSemaphore.WaitAsync();
                        try
                        {
                            await writer.WriteLineAsync(fullMessage);
                            await writer.FlushAsync();
                        }
                        finally
                        {
                            sendSemaphore.Release();
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            string fullMessage = $"[{time}] {username}: {message}";
                            await sendSemaphore.WaitAsync();
                            try
                            {
                                await writer.WriteLineAsync(fullMessage);
                                await writer.FlushAsync();
                            }
                            finally
                            {
                                sendSemaphore.Release();
                            }
                        }

                        var fileTasks = new System.Collections.Generic.List<Task>();
                        for (int i = 0; i < imagesToSend.Count; i++)
                        {
                            var attachment = imagesToSend[i];
                            int currentIndex = i; // capture loop variable
                            
                            fileTasks.Add(Task.Run(async () =>
                            {
                                string fileId = Guid.NewGuid().ToString();
                                long fileSize = new FileInfo(attachment.FilePath).Length;
                                int chunkSize = 512 * 1024; // 512 KB
                                int totalChunks = (int)Math.Ceiling((double)fileSize / chunkSize);

                                string fullMsgText = $"[{time}] {username}: ";

                                // Create and add UI progress item locally BEFORE sending FILE_START
                                Dispatcher.Invoke(() =>
                                {
                                    var progressItem = new ChatMessageItem
                                    {
                                        Text = fullMsgText,
                                        FileName = attachment.FileName,
                                        IsTransferring = true,
                                        ProgressPercentage = 0,
                                        ProgressText = $"0.00 / {(fileSize / 1048576.0):F2} MB",
                                        Alignment = HorizontalAlignment.Right,
                                        BackgroundColor = (SolidColorBrush)(new BrushConverter().ConvertFrom("#DCF8C6")!),
                                        TextColor = Brushes.Black,
                                        FilePath = attachment.FilePath
                                    };
                                    activeProgressItems[fileId] = progressItem;
                                    activeTotalBytes[fileId] = fileSize;
                                    activeBytesReceived[fileId] = 0;
                                    activeLastUiPercentage[fileId] = 0;

                                    ChatListBox.Items.Add(progressItem);
                                    ChatListBox.ScrollIntoView(progressItem);
                                });

                                // Notify start
                                await sendSemaphore.WaitAsync();
                                try
                                {
                                    await writer.WriteLineAsync($"[FILE_START] {fileId}|{attachment.FileName}|{totalChunks}|{fileSize}|{username}|{fullMsgText}");
                                    await writer.FlushAsync();
                                }
                                finally
                                {
                                    sendSemaphore.Release();
                                }

                                // Send chunks sequentially
                                using (var fs = new FileStream(attachment.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                {
                                    for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                                    {
                                        byte[] chunk = new byte[chunkSize];
                                        int bytesRead;

                                        fs.Seek(chunkIndex * (long)chunkSize, SeekOrigin.Begin);
                                        bytesRead = await fs.ReadAsync(chunk, 0, chunkSize);

                                        if (bytesRead < chunkSize)
                                            Array.Resize(ref chunk, bytesRead);

                                        string base64Data = Convert.ToBase64String(chunk);
                                        string chunkMsg = $"[FILE_CHUNK] {fileId}|{chunkIndex}|{base64Data}";

                                        await sendSemaphore.WaitAsync();
                                        try
                                        {
                                            await writer.WriteLineAsync(chunkMsg);
                                            await writer.FlushAsync();
                                        }
                                        finally
                                        {
                                            sendSemaphore.Release();
                                        }

                                        // Local progress update for the sender (since chunks are not echoed back)
                                        int sentBytes = bytesRead;
                                        Dispatcher.InvokeAsync(() =>
                                        {
                                            if (activeProgressItems.TryGetValue(fileId, out var item))
                                            {
                                                long received;
                                                lock (activeBytesReceived)
                                                {
                                                    activeBytesReceived[fileId] += sentBytes;
                                                    received = activeBytesReceived[fileId];
                                                }
                                                long total = activeTotalBytes[fileId];

                                                double newPercentage = (double)received / total * 100.0;
                                                bool shouldUpdate = false;
                                                lock (activeLastUiPercentage)
                                                {
                                                    if (activeLastUiPercentage.TryGetValue(fileId, out double lastPct))
                                                    {
                                                        if (newPercentage - lastPct >= 1.0 || newPercentage >= 100.0)
                                                        {
                                                            activeLastUiPercentage[fileId] = newPercentage;
                                                            shouldUpdate = true;
                                                        }
                                                    }
                                                }

                                                if (shouldUpdate)
                                                {
                                                    item.ProgressPercentage = newPercentage;
                                                    item.ProgressText = $"{(received / 1048576.0):F2} / {(total / 1048576.0):F2} MB";
                                                }
                                            }
                                        });
                                    }
                                }

                                // Notify end
                                await sendSemaphore.WaitAsync();
                                try
                                {
                                    await writer.WriteLineAsync($"[FILE_END] {fileId}|{attachment.IsImage}|{fullMsgText}");
                                    await writer.FlushAsync();
                                }
                                finally
                                {
                                    sendSemaphore.Release();
                                }
                            }));
                        }
                        await Task.WhenAll(fileTasks);
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MessageBox.Show("Error sending message: " + ex.Message));
                }
            });
        }

        private async Task ReceiveMessages()
        {
            try
            {
                while (true)
                {
                    if (reader == null) break;
                    string? message = await reader.ReadLineAsync();

                    if (message == null)
                    {
                        break;
                    }

                    if (message.StartsWith("[SYSTEM]"))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AddSystemMessage(message.Substring(8));
                        });
                        continue;
                    }

                    if (message.StartsWith("[FILE_START] "))
                    {
                        string[] parts = message.Substring(13).Split('|', 6);
                        string fileId = parts[0];
                        string fileName = parts[1];
                        int totalChunks = int.Parse(parts[2]);
                        long fileSize = parts.Length > 3 ? long.Parse(parts[3]) : totalChunks * 512L * 1024L;
                        string senderName = parts.Length > 4 ? parts[4] : "";
                        string fullMsgText = parts.Length > 5 ? parts[5] : "";

                        bool isMyMessage = (senderName == username);

                        // If this is my own message, I already created the progress item locally.
                        // I do NOT need to create it again or setup download FileStream.
                        if (isMyMessage)
                        {
                            continue;
                        }

                        string displayTxt = "";
                        if (!string.IsNullOrEmpty(fullMsgText))
                        {
                            int firstSpace = fullMsgText.IndexOf(' ');
                            if (firstSpace != -1)
                            {
                                int colonIndex = fullMsgText.IndexOf(':', firstSpace);
                                if (colonIndex != -1)
                                {
                                    string userText = fullMsgText.Substring(colonIndex + 1).Trim();
                                    displayTxt = string.IsNullOrEmpty(userText) ? "" : fullMsgText;
                                }
                            }
                        }

                        string tempFile = Path.Combine(Path.GetTempPath(), fileId + "_" + fileName);
                        var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        activeDownloads[fileId] = fs;

                        Dispatcher.Invoke(() =>
                        {
                            var progressItem = new ChatMessageItem
                            {
                                Text = displayTxt,
                                FileName = fileName,
                                IsTransferring = true,
                                ProgressPercentage = 0,
                                ProgressText = $"0.00 / {(fileSize / 1048576.0):F2} MB",
                                Alignment = HorizontalAlignment.Left,
                                BackgroundColor = Brushes.White,
                                TextColor = Brushes.Black
                            };
                            activeProgressItems[fileId] = progressItem;
                            activeTotalBytes[fileId] = fileSize;
                            activeBytesReceived[fileId] = 0;
                            activeLastUiPercentage[fileId] = 0;
                            
                            ChatListBox.Items.Add(progressItem);
                            ChatListBox.ScrollIntoView(progressItem);
                        });
                        continue;
                    }

                    if (message.StartsWith("[FILE_CHUNK] "))
                    {
                        string[] parts = message.Substring(13).Split('|');
                        string fileId = parts[0];
                        int chunkIndex = int.Parse(parts[1]);
                        string base64 = parts[2];

                        try
                        {
                            if (activeDownloads.TryGetValue(fileId, out var fs))
                            {
                                byte[] bytes = Convert.FromBase64String(base64);
                                lock (fs)
                                {
                                    fs.Seek(chunkIndex * 512L * 1024L, SeekOrigin.Begin);
                                    fs.Write(bytes, 0, bytes.Length);
                                }

                                long received;
                                lock (activeBytesReceived)
                                {
                                    activeBytesReceived[fileId] += bytes.Length;
                                    received = activeBytesReceived[fileId];
                                }
                                long total = activeTotalBytes[fileId];

                                double newPercentage = (double)received / total * 100.0;
                                bool shouldUpdate = false;
                                lock (activeLastUiPercentage)
                                {
                                    if (activeLastUiPercentage.TryGetValue(fileId, out double lastPct))
                                    {
                                        if (newPercentage - lastPct >= 1.0 || newPercentage >= 100.0)
                                        {
                                            activeLastUiPercentage[fileId] = newPercentage;
                                            shouldUpdate = true;
                                        }
                                    }
                                }

                                if (shouldUpdate)
                                {
                                    Dispatcher.InvokeAsync(() =>
                                    {
                                        if (activeProgressItems.TryGetValue(fileId, out var item))
                                        {
                                            item.ProgressPercentage = newPercentage;
                                            item.ProgressText = $"{(received / 1048576.0):F2} / {(total / 1048576.0):F2} MB";
                                        }
                                    });
                                }
                            }
                        }
                        catch { }
                        continue;
                    }

                    if (message.StartsWith("[FILE_END] "))
                    {
                        string[] parts = message.Substring(11).Split('|', 3);
                        string fileId = parts[0];
                        bool isImage = bool.Parse(parts[1]);
                        string msgText = parts.Length > 2 ? parts[2] : "";

                        int firstSpace = msgText.IndexOf(' ');
                        int colonIndex = msgText.IndexOf(':', firstSpace);
                        string senderName = msgText.Substring(firstSpace + 1, colonIndex - firstSpace - 1);
                        bool isMyMessage = (senderName == username);
                        string userText = msgText.Substring(colonIndex + 1).Trim();
                        string displayTxt = string.IsNullOrEmpty(userText) ? "" : msgText;

                        // Case 1: Active download FileStream exists (receiver side)
                        if (activeDownloads.TryGetValue(fileId, out var fs))
                        {
                            string filePath = fs.Name;
                            string fileName = Path.GetFileName(filePath).Substring(fileId.Length + 1); // Remove GUID_
                            fs.Close();
                            activeDownloads.Remove(fileId);

                            Dispatcher.Invoke(() =>
                            {
                                if (activeProgressItems.TryGetValue(fileId, out var item))
                                {
                                    item.IsTransferring = false;
                                    item.Text = displayTxt;
                                    item.FilePath = filePath;
                                    item.ImageSource = isImage ? filePath : string.Empty;
                                    item.FileName = isImage ? string.Empty : fileName;
                                    
                                    activeProgressItems.Remove(fileId);
                                    activeTotalBytes.Remove(fileId);
                                    activeBytesReceived.Remove(fileId);
                                    activeLastUiPercentage.Remove(fileId);
                                }
                                else
                                {
                                    var newItem = new ChatMessageItem
                                    {
                                        Text = displayTxt,
                                        ImageSource = isImage ? filePath : string.Empty,
                                        FileName = isImage ? string.Empty : fileName,
                                        FilePath = filePath,
                                        Alignment = HorizontalAlignment.Left,
                                        BackgroundColor = Brushes.White,
                                        TextColor = Brushes.Black
                                    };
                                    ChatListBox.Items.Add(newItem);
                                    ChatListBox.ScrollIntoView(newItem);
                                }
                            });
                        }
                        // Case 2: No active download, but I am the sender, so I have the progress item in activeProgressItems
                        else if (isMyMessage)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (activeProgressItems.TryGetValue(fileId, out var item))
                                {
                                    item.IsTransferring = false;
                                    item.Text = displayTxt;
                                    
                                    // Set image source or file properties using local path
                                    item.ImageSource = isImage ? item.FilePath : string.Empty;
                                    item.FileName = isImage ? string.Empty : Path.GetFileName(item.FilePath);

                                    activeProgressItems.Remove(fileId);
                                    activeTotalBytes.Remove(fileId);
                                    activeBytesReceived.Remove(fileId);
                                    activeLastUiPercentage.Remove(fileId);
                                }
                            });
                        }
                        continue;
                    }

                    bool isImageMessage = message.StartsWith("[IMAGE] ");
                    bool isFileMessage = message.StartsWith("[FILE] ");

                    Action processMessageAction = () =>
                    {
                        try
                        {
                            string textContent = message;
                            string displayImage = string.Empty;
                            string fileName = string.Empty;
                            string filePath = string.Empty;
                            bool isMyMessage = false;

                            if (isImageMessage || isFileMessage)
                            {
                                int prefixLen = isImageMessage ? 8 : 7;
                                message = message.Substring(prefixLen);
                                textContent = message;
                            }

                            int firstSpace = message.IndexOf(' ');
                            if (firstSpace != -1)
                            {
                                int colonIndex = message.IndexOf(':', firstSpace);
                                if (colonIndex != -1)
                                {
                                    string senderName = message.Substring(firstSpace + 1, colonIndex - firstSpace - 1);
                                    if (senderName == username)
                                    {
                                        isMyMessage = true;
                                    }

                                    if (isImageMessage)
                                    {
                                        int imgStartIndex = textContent.IndexOf('|');
                                        if (imgStartIndex != -1)
                                        {
                                            string msgText = textContent.Substring(0, imgStartIndex);
                                            string base64Image = textContent.Substring(imgStartIndex + 1);

                                            byte[] imageBytes = Convert.FromBase64String(base64Image);
                                            string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".jpg");
                                            File.WriteAllBytes(tempFile, imageBytes);
                                            displayImage = tempFile;

                                            string userText = msgText.Substring(colonIndex + 1).Trim();
                                            textContent = string.IsNullOrEmpty(userText) ? "" : msgText;
                                        }
                                    }
                                    else if (isFileMessage)
                                    {
                                        int firstPipe = textContent.IndexOf('|');
                                        if (firstPipe != -1)
                                        {
                                            int secondPipe = textContent.IndexOf('|', firstPipe + 1);
                                            if (secondPipe != -1)
                                            {
                                                string msgText = textContent.Substring(0, firstPipe);
                                                fileName = textContent.Substring(firstPipe + 1, secondPipe - firstPipe - 1);
                                                string base64File = textContent.Substring(secondPipe + 1);

                                                byte[] fileBytes = Convert.FromBase64String(base64File);
                                                string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + "_" + fileName);
                                                File.WriteAllBytes(tempFile, fileBytes);
                                                filePath = tempFile;

                                                string userText = msgText.Substring(colonIndex + 1).Trim();
                                                textContent = string.IsNullOrEmpty(userText) ? "" : msgText;
                                            }
                                        }
                                    }
                                }
                            }

                            Dispatcher.Invoke(() =>
                            {
                                var item = new ChatMessageItem
                                {
                                    Text = textContent,
                                    ImageSource = displayImage,
                                    FileName = fileName,
                                    FilePath = filePath,
                                    Alignment = isMyMessage ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                                    BackgroundColor = isMyMessage ? (SolidColorBrush)(new BrushConverter().ConvertFrom("#DCF8C6")!) : Brushes.White,
                                    TextColor = Brushes.Black
                                };

                                ChatListBox.Items.Add(item);
                                ChatListBox.ScrollIntoView(item);
                            });
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => AddSystemMessage("Error parsing message: " + ex.Message));
                        }
                    };

                    if (isImageMessage || isFileMessage)
                    {
                        // Decode big files in background
                        _ = Task.Run(processMessageAction);
                    }
                    else
                    {
                        processMessageAction();
                    }
                }
            }
            catch (Exception ex)
            {
                if (client != null && client.Connected)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AddSystemMessage("Connection error: " + ex.Message);
                        Disconnect();
                    });
                }
            }
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag != null)
            {
                MessageTextBox.Text += button.Tag.ToString();
                MessageTextBox.Focus();
                MessageTextBox.CaretPosition = MessageTextBox.Document.ContentEnd;
            }
        }

        private async void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    // Nếu giữ Shift + nhấn Enter: Cho phép xuống dòng bình thường
                    return; 
                }
                else
                {
                    // Nếu chỉ nhấn Enter: Chặn ngay việc tạo dòng mới và Gửi tin nhắn
                    e.Handled = true; 
                    await SendMessage();
                }
            }
        }


        private void ImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (writer == null || client == null || !client.Connected)
            {
                MessageBox.Show("You must connect first.");
                return;
            }

            var openFileDialog = new OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.gif;*.bmp)|*.jpg;*.jpeg;*.png;*.gif;*.bmp|All Files (*.*)|*.*",
                Title = "Select Image(s) to Send",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                // Push reading files out of the click event loop to avoid UI freezing
                Task.Run(() =>
                {
                    try
                    {
                        foreach (string file in openFileDialog.FileNames)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                pendingAttachments.Add(new PendingAttachment
                                {
                                    FilePath = file,
                                    IsImage = true
                                });
                            });
                        }
                        Dispatcher.Invoke(() => MessageTextBox.Focus());
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => MessageBox.Show("Error loading image(s): " + ex.Message));
                    }
                });
            }
        }

        private void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (writer == null || client == null || !client.Connected)
            {
                MessageBox.Show("You must connect first.");
                return;
            }

            var openFileDialog = new OpenFileDialog
            {
                Filter = "All Files (*.*)|*.*",
                Title = "Select File(s) to Send",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                Task.Run(() =>
                {
                    try
                    {
                        foreach (string file in openFileDialog.FileNames)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                pendingAttachments.Add(new PendingAttachment
                                {
                                    FilePath = file,
                                    IsImage = false
                                });
                            });
                        }
                        Dispatcher.Invoke(() => MessageTextBox.Focus());
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => MessageBox.Show("Error loading file(s): " + ex.Message));
                    }
                });
            }
        }

        private void FolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (writer == null || client == null || !client.Connected)
            {
                MessageBox.Show("You must connect first.");
                return;
            }

            var folderDialog = new OpenFolderDialog
            {
                Title = "Select Folder to Send"
            };

            if (folderDialog.ShowDialog() == true)
            {
                Task.Run(() =>
                {
                    try
                    {
                        var files = Directory.GetFiles(folderDialog.FolderName, "*", SearchOption.AllDirectories);

                        foreach (string file in files)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                pendingAttachments.Add(new PendingAttachment
                                {
                                    FilePath = file,
                                    IsImage = false
                                });
                            });
                        }
                        Dispatcher.Invoke(() => MessageTextBox.Focus());
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => MessageBox.Show("Error loading folder: " + ex.Message));
                    }
                });
            }
        }

        private void RemovePendingAttachment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PendingAttachment attachment)
            {
                pendingAttachments.Remove(attachment);
            }
        }

        private void FileDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string fileTempPath && File.Exists(fileTempPath))
            {
                var saveDialog = new SaveFileDialog
                {
                    FileName = Path.GetFileName(fileTempPath).Substring(37), // remove GUID prefix
                    Title = "Save File"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    try
                    {
                        File.Copy(fileTempPath, saveDialog.FileName, true);
                        MessageBox.Show("File downloaded successfully.");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to save file: " + ex.Message);
                    }
                }
            }
        }

        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Image image && image.Source != null)
            {
                FullscreenImage.Source = image.Source;
                ImageOverlay.Visibility = Visibility.Visible;
            }
        }

        private void ImageOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ImageOverlay.Visibility = Visibility.Collapsed;
        }

        private void CloseOverlay_Click(object sender, RoutedEventArgs e)
        {
            ImageOverlay.Visibility = Visibility.Collapsed;
        }
    }
}