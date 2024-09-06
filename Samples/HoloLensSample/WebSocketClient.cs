using System;
using System.Net.Sockets;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Timers;
using Microsoft.MixedReality.WebRTC;
using WebRTCtest;
using Windows.Storage;

/// <summary>
/// Initializes a new instance of the <see cref="WebSocketClient"/> class.
/// </summary>
public class WebSocketClient
{
    webrtcclient rtcClient;

    private ClientWebSocket _webSocket = new ClientWebSocket();
    private Uri _serverUri;
    private String ipAddress;

#pragma warning disable CS1591
    public event Action<string> OnMessage;

    public const String PEER_CONNECTED = "@@@";
    public const String PEER_DISCONNECTED = "###";
    public const String PEER_UNAVAILABLE = "***Peer unavailable";

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    public WebSocketClient(string serverUrl, webrtcclient _rtcClient)
    {
        _serverUri = new Uri(serverUrl);
        ipAddress = GetLocalIPAddress();
        rtcClient = _rtcClient;
        _ = WriteLogToFile("local ip: " + ipAddress);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            await _webSocket.ConnectAsync(_serverUri, CancellationToken.None);

            _ = WriteLogToFile("Connected to WebSocket server at " + _serverUri);
            string message = "WAK4k5SthTsWrAxp49U4yfybjpjZ7XRu" + "mn_fol";
            byte[] buffer = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
#pragma warning disable CS4014
            Task.Run(() => StartReceiving());
#pragma warning restore CS4014
        }

    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    public async Task<bool> SendMessageAsync(int type, string content, string clientKey)
    {
        try
        {
            var message = new
            {
                Id = clientKey,
                MessageType = type,
                Data = content,
                IceDataSeparator = "|"
            };

            string messageJson = JsonConvert.SerializeObject(message);
            byte[] buffer = Encoding.UTF8.GetBytes(messageJson);
            await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            _ = WriteLogToFile("[Sent message]: " + messageJson);
            return true;
        }
        catch (Exception ex)
        {
            _ = WriteLogToFile("Failed to send message: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    public async Task sendCandidateToSocket(IceCandidate iceCandidate)
    {
        String content = iceCandidate.Content
        + "|" + iceCandidate.SdpMlineIndex
        + "|" + iceCandidate.SdpMid;
        await SendMessageAsync(3, content, "mn_fol");
    }


    public async Task sendCandidateToSocket(string candidate, int sdpMlineindex, string sdpMid)
    {
        String content = candidate
        + "|" + sdpMlineindex
        + "|" + sdpMid;
        await SendMessageAsync(3, content, "mn_fol");
    }

    private async void StartReceiving()
    {
        var buffer = new byte[1024 * 4];
        try
        {
            while (_webSocket.State == WebSocketState.Open)
            {
                _ = WriteLogToFile("[Websocket] Attempting to receive...");
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _ = WriteLogToFile("[Websocket] received type: " + result.MessageType);
                _ = WriteLogToFile("[Websocket] received: " + message);
                HandleReceiveMessage(message);
                OnMessage?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            _ = WriteLogToFile("Error in receiving WebSocket messages: " + ex.Message);
        }
        _ = WriteLogToFile("[Websocket] Stopped Receive");
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    private void HandleReceiveMessage(string receivedMessage)
    {
        switch (receivedMessage)
        {
            case PEER_CONNECTED:
                _ = WriteLogToFile("Expert connected to signaling server");
                return;
            case PEER_DISCONNECTED:
                _ = WriteLogToFile("Expert disconnected to signaling server");
                return;
            case PEER_UNAVAILABLE:
                _ = WriteLogToFile("Expert unavailable to signaling server");
                break;
            default:
                JObject msgJson = JObject.Parse(receivedMessage);

                String senderId = "";
                senderId = msgJson["Id"]?.ToString() ?? "Unknown sender";
                int messageType = (int)msgJson["MessageType"];
                string messageData = msgJson["Data"].ToString();
                _ = WriteLogToFile("Received message Type: " + messageType);
                switch (messageType)
                {
                    case 1:
                        _ = WriteLogToFile($"handleOfferMessage: received offer from {senderId}, sending answer {receivedMessage}");
                        rtcClient.HandleOfferMessage(messageData);
                        break;
                    case 2:
                        _ = WriteLogToFile($"handleAnswerMessage: received answer from {senderId}: {receivedMessage}");
                        rtcClient.HandleAnswerMessage(messageData);
                        break;
                    case 3:
                        _ = WriteLogToFile($"handleIceCandidateMessage: receiving candidate from {senderId}: {receivedMessage}");
                        string[] parts = messageData.Split('|');
                        if (parts.Length >= 3)
                        {
                            string sdp = parts[0];
                            int sdpMLineIndex = int.Parse(parts[1]);
                            string sdpMid = parts[2];
                            rtcClient.HandleIceCandidateMessage(sdp, sdpMLineIndex, sdpMid);
                        }
                        break;
                    default:
                        //rtcClient.HandleDefaultMessage(receivedMessage);
                        break;
                }
                return;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    public static string GetLocalIPAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
            throw new Exception("No network adapters with an IPv4 address in the system!");
        }
        catch (Exception ex)
        {
            // Handle or log the exception as needed
            _ = WriteLogToFile("Unable to find local IP: " + ex.Message);
            return "";
        }
    }

    private static async Task WriteLogToFile(string logMessage)
    {
        StorageFolder localFolder = ApplicationData.Current.LocalFolder;
        StorageFile logFile = await localFolder.CreateFileAsync("applog.txt", CreationCollisionOption.OpenIfExists);
        await FileIO.AppendTextAsync(logFile, logMessage + "\n");
    }

}
