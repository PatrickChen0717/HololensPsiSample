using Microsoft.MixedReality.WebRTC;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WebRTCtest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    public class webrtcclient
    {
        PeerConnection peerConnection;
        static WebSocketClient webSocket;
        static IceConnectionState currentState;
        string localdescription;

        private DataChannel videoChannel;
        private DataChannel forceChannel;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
        /// </summary>
        public webrtcclient()
        {
            webSocket = new WebSocketClient("wss://mirthus.herokuapp.com", this);
            StartRTC();

            Task.Run(async () => await StartFrceTest());
        }

        private async void StartRTC()
        {
            await webSocket.ConnectAsync();


            //PeerConnection peerConnection;
            
            //Thread.Sleep(2000);

            var iceServers = new List<IceServer>();
            iceServers.Add(new IceServer
            {
                Urls = new List<string> { "stun:stun.relay.metered.ca:80" }
            });

            // Add TURN servers
            iceServers.Add(new IceServer
            {
                Urls = new List<string> { "turn:standard.relay.metered.ca:80" },
                TurnUserName = "6120053268bd1226cca26cc3",
                TurnPassword = "iSyXLtZG8rwh0osi"
            });

            iceServers.Add(new IceServer
            {
                Urls = new List<string> { "turn:standard.relay.metered.ca:80?transport=tcp" },
                TurnUserName = "6120053268bd1226cca26cc3",
                TurnPassword = "iSyXLtZG8rwh0osi"
            });

            iceServers.Add(new IceServer
            {
                Urls = new List<string> { "turn:standard.relay.metered.ca:443" },
                TurnUserName = "6120053268bd1226cca26cc3",
                TurnPassword = "iSyXLtZG8rwh0osi"
            });

            iceServers.Add(new IceServer
            {
                Urls = new List<string> { "turn:standard.relay.metered.ca:443?transport=tcp" },
                TurnUserName = "6120053268bd1226cca26cc3",
                TurnPassword = "iSyXLtZG8rwh0osi"
            });

            var config = new PeerConnectionConfiguration
            {
                IceServers = iceServers
            };

            _ = WriteLogToFile("1");
            //Thread.Sleep(2000);
            peerConnection = new PeerConnection();
            _ = WriteLogToFile("1");
            //Thread.Sleep(2000);

            try
            {
                await peerConnection.InitializeAsync(config);
                Console.WriteLine("Peer connection initialized");

                peerConnection.IceStateChanged += OnIceStateChanged;
                peerConnection.IceCandidateReadytoSend += OnIceCandidateReadyToSend;
                peerConnection.IceGatheringStateChanged += onIceGatheringChange;
                peerConnection.LocalSdpReadytoSend += OnLocalSdpReadyToSend;
                peerConnection.Connected += OnConnected;



                await InitializeDataChannel();
                bool res = peerConnection.CreateOffer();
                _ = WriteLogToFile("Peer connection offer created: " + res);
            }
            catch (Exception ex)
            {
                _ = WriteLogToFile("Failed to initialize peer connection: " + ex.Message);
            }

        }

        private static void OnIceStateChanged(IceConnectionState newState)
        {
            _ = WriteLogToFile($"IceState Changed: {newState}");
            currentState = newState;
        }

        private async Task InitializeDataChannel()
        {

            videoChannel = await peerConnection.AddDataChannelAsync("vido", true, true);
            videoChannel.MessageReceived += OnMessageReceived;
            videoChannel.StateChanged += () => _ = WriteLogToFile($"VidoChannel State Changed: {videoChannel.State}");

            forceChannel = await peerConnection.AddDataChannelAsync($"frce", true, true);
            forceChannel.MessageReceived += OnMessageReceived;
            forceChannel.StateChanged += () => _ = WriteLogToFile($"FrceChannel State Changed: {forceChannel.State}");
        }

        private static void OnConnected()
        {
            _ = WriteLogToFile("Peer connection successfully established!");
        }

        private async static void OnIceCandidateReadyToSend(IceCandidate candidate)
        {
            _ = WriteLogToFile($"IceCandidate ready to send: {candidate.Content}");

            await webSocket.sendCandidateToSocket(candidate);
        }

        private async void OnLocalSdpReadyToSend(SdpMessage message)
        {
            localdescription = message.Content;
            if (peerConnection == null || !peerConnection.Initialized)
            {
                _ = WriteLogToFile("PeerConnection is not initialized yet.");
                return;
            }

            _ = WriteLogToFile($"Local SDP ready to send: Type {message.Type} SDP: {message.Content}");
            try
            {

                await webSocket.SendMessageAsync((int)message.Type, message.Content, "mn_fol");
            }
            catch (Exception ex)
            {
                _ = WriteLogToFile("Failed to set local description or send message: " + ex.Message);
            }
        }

        private async void onIceGatheringChange(IceGatheringState iceGatheringState)
        {
            _ = WriteLogToFile($"ICE Gathering State Changed: {iceGatheringState}");

            if (iceGatheringState == IceGatheringState.Complete)
                await webSocket.SendMessageAsync(3, localdescription, "mn_fol");
        }

        private void OnMessageReceived(byte[] message)
        {
            string messageText = Encoding.UTF8.GetString(message);
            _ = WriteLogToFile($"Received message on channel: {messageText}");
        }

        private async Task StartFrceTest()
        {
            try
            {
                while (peerConnection == null || !peerConnection.Initialized)
                {
                    await Task.Delay(500);
                    _ = WriteLogToFile("Waiting for PeerConnection to initialize...");
                }

                while (true)
                {
                    if(currentState != IceConnectionState.Completed && currentState != IceConnectionState.Connected)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    if (forceChannel != null && forceChannel.State == DataChannel.ChannelState.Open)
                    {
                        string message = "frcetest";
                        byte[] buffer = Encoding.UTF8.GetBytes(message);
                        try
                        {

                            forceChannel.SendMessage(buffer);
                            _ = WriteLogToFile("Frce data sent");
                        }
                        catch (Exception ex)
                        {
                            _ = WriteLogToFile($"Error sending message: {ex.Message}");
                        }
                    }
                    else
                    {
                        _ = WriteLogToFile("DataChannel is not ready or open.");
                    }
                    await Task.Delay(1000); // Adjust timing as needed
                }
            }
            catch (Exception ex)
            {
                _ = WriteLogToFile($"An error occurred in StartFrceTest: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
        /// </summary>
        public async void HandleOfferMessage(string sdpContent)
        {
            localdescription = sdpContent;
            _ = WriteLogToFile("HandleOfferMessage ");
            // Set the received SDP as the remote description
            var offer = new SdpMessage { Type = SdpMessageType.Offer, Content = sdpContent };
            await peerConnection.SetRemoteDescriptionAsync(offer);
            bool res = peerConnection.CreateAnswer();
            // Create and send an answer to the received offer
            //SdpMessage sdpmessage = new SdpMessage();
            //sdpmessage.Content = sdpContent;
            //await peerConnection.SetRemoteDescriptionAsync(sdpmessage);


            _ = WriteLogToFile("create answer result: " + res);
            // Signal the SDP answer back to the remote peer via your signaling channel
            //SendSdpToSocket(answer.Content, SdpMessageType.Answer);
            //await webSocket.SendMessageAsync((int)SdpMessageType.Answer, sdpContent, "mn_fol");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
        /// </summary>
        public async void HandleAnswerMessage(string sdpContent)
        {
            localdescription = sdpContent;
            /*
            if(sdpanswer == sdpContent)
            {
                return;
            }
            sdpanswer = sdpContent;*/

            if (peerConnection == null || !peerConnection.Initialized)
            {
                _ = WriteLogToFile("PeerConnection is not initialized.");
                return;
            }

            try
            {
                var answer = new SdpMessage { Type = SdpMessageType.Answer, Content = sdpContent };
                await peerConnection.SetRemoteDescriptionAsync(answer);
                //Console.WriteLine("create answer result: " + res);
                _ = WriteLogToFile("PeerConnection successfully SetRemoteDescription");
            }
            catch (Exception ex)
            {
                _ = WriteLogToFile($"Failed to set remote description: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
        /// </summary>
        public void HandleIceCandidateMessage(string sdp, int sdpMLineIndex, string sdpMid)
        {
            var candidate = new IceCandidate
            {
                SdpMid = sdpMid,
                SdpMlineIndex = sdpMLineIndex,
                Content = sdp
            };

            peerConnection.AddIceCandidate(candidate);
        }

        private static async Task WriteLogToFile(string logMessage)
        {
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile logFile = await localFolder.CreateFileAsync("applog.txt", CreationCollisionOption.OpenIfExists);
            await FileIO.AppendTextAsync(logFile, logMessage + "\n");
        }
    }
}
