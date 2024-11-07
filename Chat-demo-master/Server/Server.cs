﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Server
{
    public partial class Server : Form
    {
        private static ConcurrentBag<ClientInfo> clients = new ConcurrentBag<ClientInfo>();
        private TcpListener server;
        private byte[] message;

        public Server()
        {
            InitializeComponent();
        }
        //Lấy ip address từ wifi
        private void ShowWifiIPAddress()
        {
            StringBuilder wifiIP = new StringBuilder();

            foreach (NetworkInterface netInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Kiểm tra nếu giao diện là Wi-Fi và đang kết nối
                if (netInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                    netInterface.OperationalStatus == OperationalStatus.Up)
                {
                    foreach (UnicastIPAddressInformation ip in netInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork) // IPv4
                        {
                            wifiIP.AppendLine(ip.Address.ToString());
                        }
                    }
                }
            }

            if (txt_IPAddress.InvokeRequired)
            {
                txt_IPAddress.Invoke(new Action(() =>
                    txt_IPAddress.Text = wifiIP.ToString()));
            }
            else
            {
                txt_IPAddress.Text = wifiIP.ToString();
            }
        }
        // Khởi động server (không đồng bộ)
        public async Task Start_Server()
        {
            int port = 9999;

            server = new TcpListener(IPAddress.Any, port);
            server.Start();
            ShowWifiIPAddress();
            showStatus("Server đã khởi động...");

            txt_Message.Enabled = true;
            btn_Send.Enabled = true;
            btn_StopServer.Enabled = true;
            btn_StartServer.Enabled = false;

            while (true)
            {
                TcpClient client = await server.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
        }

        // Xử lý từng client (không đồng bộ)
        private async Task HandleClientAsync(TcpClient client)
        {
            string username = null;
            try
            {
                using (var networkStream = client.GetStream())
                using (var sslStream = new SslStream(networkStream, false))
                {
                    // Đường dẫn và pass của file chứng chỉ
                    string certPath = "server.pfx";
                    string certPass = "abc@123";
                    var serverCertificate = new X509Certificate(certPath, certPass);
                    await sslStream.AuthenticateAsServerAsync(serverCertificate, false, System.Security.Authentication.SslProtocols.Tls12, true);

                    // Nhận tên người dùng từ client
                    byte[] buffer = new byte[1024];
                    int byteCount = await sslStream.ReadAsync(buffer, 0, buffer.Length);
                    username = Encoding.UTF8.GetString(buffer, 0, byteCount).Trim();

                    // Thêm client vào danh sách
                    var clientInfo = new ClientInfo { TcpClient = client, SslStream = sslStream, Username = username };
                    clients.Add(clientInfo);
                    ShowUser("User name : " + username);
                    showStatus(username + " đã kết nối!");

                    // Gửi danh sách người dùng cho tất cả các client
                    await SendClientListToAllAsync();

                    // Xử lý tin nhắn từ client
                    await HandleMessagesAsync(clientInfo);
                }
            }
            catch (Exception ex)
            {
                showStatus("Error: " + ex.Message);
            }
            finally
            {
                if (username != null)
                {
                    // Loại bỏ client ra khỏi danh sách
                    clients = new ConcurrentBag<ClientInfo>(clients.Where(c => c.Username != username));
                    RemoveUser("User name : " + username);
                    await SendClientListToAllAsync();
                    showStatus(username + " đã ngắt kết nối.");
                }

                client.Close();
            }
        }

        // Xử lý tin nhắn từ client
        private async Task HandleMessagesAsync(ClientInfo clientInfo)
        {
            try
            {
                var sslStream = clientInfo.SslStream;
                message = new byte[1024];

                while (true)
                {
                    int byteRead = await sslStream.ReadAsync(message, 0, message.Length);
                    if (byteRead == 0) break;

                    string receivedMessage = Encoding.UTF8.GetString(message, 0, byteRead);
                    ShowMessage(clientInfo.Username + ": " + receivedMessage);

                    await BroadcastAsync(clientInfo.Username + ": " + receivedMessage, clientInfo.TcpClient);
                }
            }
            catch (Exception ex)
            {
                showStatus("Error: " + ex.Message);
            }
        }

        // Gửi tin nhắn đến tất cả các client ngoại trừ client gửi
        private async Task BroadcastAsync(string message, TcpClient excludeClient)
        {
            byte[] dataByte = Encoding.UTF8.GetBytes(message);

            var tasks = clients
                .Where(c => c.TcpClient != excludeClient)
                .Select(async clientInfo =>
                {
                    try
                    {
                        await clientInfo.SslStream.WriteAsync(dataByte, 0, dataByte.Length);
                    }
                    catch (Exception ex)
                    {
                        showStatus("Client Disconnected: " + ex.Message);
                        clientInfo.TcpClient.Close();
                        clients = new ConcurrentBag<ClientInfo>(clients.Where(c => c != clientInfo));
                    }
                });

            await Task.WhenAll(tasks);
        }

        // Gửi danh sách người dùng cho tất cả các client
        private async Task SendClientListToAllAsync()
        {
            var clientListMessage = "/ClientList " + string.Join(", ", clients.Select(c => c.Username));
            byte[] dataByte = Encoding.UTF8.GetBytes(clientListMessage);

            var tasks = clients.Select(async clientInfo =>
            {
                try
                {
                    await clientInfo.SslStream.WriteAsync(dataByte, 0, dataByte.Length);
                }
                catch (Exception ex)
                {
                    showStatus("Client Disconnected: " + ex.Message);
                    clientInfo.TcpClient.Close();
                    clients = new ConcurrentBag<ClientInfo>(clients.Where(c => c != clientInfo));
                }
            });

            await Task.WhenAll(tasks);
        }

        // Dừng server
        private void Stop_Server()
        {
            if (server != null)
            {
                foreach (var clientInfo in clients)
                {
                    clientInfo.SslStream.Close();
                    clientInfo.TcpClient.Close();
                }

                server.Stop();
                clients = new ConcurrentBag<ClientInfo>();
                showStatus("Server đã ngắt kết nối.");

                txt_Message.Enabled = false;
                btn_Send.Enabled = false;
                btn_StopServer.Enabled = false;
                btn_StartServer.Enabled = true;
            }
        }




    }
}
