using Microsoft.MixedReality.WebRTC;
using Microsoft.Psi.Common;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Windows.Devices.Sms;
using Windows.Storage;
using Windows.Storage.Streams;
using static Emgu.CV.ML.LogisticRegression;

namespace WebRTCtest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
    /// </summary>
    public class webrtcclient
    {

#pragma warning disable CS1591, CS0414
        PeerConnection peerConnection;
        static WebSocketClient webSocket;
        static IceConnectionState currentState;
        string localdescription;

        private DataChannel videoChannel;
        private DataChannel forceChannel;
        private DataChannel depthChannel;

        private Boolean GatherComplete = false;
        private int restartCnt = 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
        /// </summary>
        public webrtcclient()
        {
            webSocket = new WebSocketClient("wss://mirthus.herokuapp.com", this);
            StartRTC();

            //Task.Run(async () => await StartFrceTest());
        }

        private async void StartRTC()
        {
            await webSocket.ConnectAsync();

            var iceServers = new List<IceServer>();

            iceServers.Add(new IceServer
            {
                Urls = new List<string> { "stun:stun.relay.metered.ca:80" }
            });

            iceServers.Add(new IceServer
            {
                Urls = new List<string> {
                "turn:standard.relay.metered.ca:80",
                "turn:standard.relay.metered.ca:80?transport=tcp",
                "turn:standard.relay.metered.ca:443",
                "turn:standard.relay.metered.ca:443?transport=tcp"
            },
                TurnUserName = "6120053268bd1226cca26cc3",
                TurnPassword = "iSyXLtZG8rwh0osi"
            });

            var config = new PeerConnectionConfiguration
            {
                IceServers = iceServers,
                IceTransportType = IceTransportType.All,
                BundlePolicy = BundlePolicy.MaxCompat,
                SdpSemantic = SdpSemantic.UnifiedPlan
            };

            peerConnection = new PeerConnection();

            try
            {
                await peerConnection.InitializeAsync(config);
                Console.WriteLine("Peer connection initialized");

                peerConnection.IceGatheringStateChanged += OnIceGatheringStateChanged;
                peerConnection.IceStateChanged += OnIceStateChanged;
                peerConnection.IceCandidateReadytoSend += OnIceCandidateReadyToSend;
                peerConnection.LocalSdpReadytoSend += OnLocalSdpReadyToSend;
                peerConnection.Connected += OnConnected;
                peerConnection.RenegotiationNeeded += OnRenegotiationNeeded;

                await InitializeDataChannel();
                Console.WriteLine("Gather completed");
                StartOfferLoop();
                // bool res = peerConnection.CreateOffer();
                //  Console.WriteLine("Peer connection offer created: " + res);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to initialize peer connection: " + ex.Message);
            }

        }

        public void StartOfferLoop()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (peerConnection != null && currentState != IceConnectionState.Connected && currentState != IceConnectionState.Completed)
                        {
                            Console.WriteLine("Attempting to create an offer...");
                            bool offerResult = peerConnection.CreateOffer();
                            Console.WriteLine($"Offer creation result: {offerResult}");
                        }
                        else
                        {
                            Console.WriteLine("Peer connection is either null or already connected.");
                        }
                        await Task.Delay(5000);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception in StartOfferLoop: {ex.Message}");
                        await Task.Delay(5000);
                    }
                }
            });
        }

        private void OnRenegotiationNeeded()
        {
            Console.WriteLine("OnRenegotiationNeeded.");
            bool res = peerConnection.CreateOffer();
        }


        private void OnIceGatheringStateChanged(IceGatheringState newState)
        {
            if (newState == IceGatheringState.Complete)
            {
                Console.WriteLine($"IceGatheringState {newState}");
                GatherComplete = true;
            }
        }

        private async void OnIceCandidateReadyToSend(IceCandidate candidate)
        {
            Console.WriteLine($"IceCandidate ready to send: {candidate.Content} | {candidate.SdpMid} | {candidate.SdpMlineIndex}");

            if (candidate.Content.Contains("typ relay"))
            {
                Console.WriteLine("Using TURN server for this candidate.");
            }
            else
            {
                Console.WriteLine("Direct or STUN candidate.");
            }
            // peerConnection.AddIceCandidate(candidate);

            if (currentState == IceConnectionState.Connected)
            {
                await webSocket.sendCandidateToSocket(candidate);
            }
            else
                await webSocket.sendCandidateToSocket(candidate);
        }



        private async void OnLocalSdpReadyToSend(SdpMessage message)
        {
            //while (!GatherComplete)
            // {
            //     await Task.Delay(1000);
            // }
            localdescription = message.Content;
            if (peerConnection == null || !peerConnection.Initialized)
            {
                _ = WriteLogToFile("PeerConnection is not initialized yet.");
                return;
            }

            Console.WriteLine($"Local SDP ready to send: Type {message.Type} SDP: {message.Content}");


            try
            {
                peerConnection.CreateAnswer();
                await webSocket.SendMessageAsync((int)message.Type, message.Content, "mn_fol");
            }
            catch (Exception ex)
            {
                _ = WriteLogToFile("Failed to set local description or send message: " + ex.Message);
            }
        }

        public void HandleRestart()
        {
            restartCnt++;
            if (restartCnt == 3)
            {
                restartCnt = 0;
                //peerConnection.CreateOffer();
            }
            //peerConnection.CreateOffer();
        }

        private async void OnIceCandidateReadyToSend(string candidate, int sdpMlineindex, string sdpMid)
        {
            //peerConnection.AddIceCandidate(sdpMid, sdpMlineindex, sdpMid);
            Console.WriteLine($"IceCandidate ready to send: {candidate}");
            if (candidate.Contains("typ relay"))
            {
                Console.WriteLine("Using TURN server for this candidate.");
            }
            else
            {
                Console.WriteLine("Direct or STUN candidate.");
            }

            await webSocket.sendCandidateToSocket(candidate, sdpMlineindex, sdpMid);
        }

        private async void OnLocalSdpReadyToSend(string type, string sdp)
        {
            localdescription = sdp;
            if (peerConnection == null || !peerConnection.Initialized)
            {
                Console.WriteLine("PeerConnection is not initialized yet.");
                return;
            }

            Console.WriteLine($"Local SDP ready to send: Type {type} SDP: {sdp}");
            try
            {
                Console.WriteLine("sending offer to peer");
                await webSocket.SendMessageAsync(1, sdp, "mn_fol");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to set local description or send message: " + ex.Message);
            }
        }

        private void OnIceStateChanged(IceConnectionState newState)
        {
            Console.WriteLine($"IceState Changed: {newState}");
            currentState = newState;
        }

        private async Task InitializeDataChannel()
        {

            videoChannel = await peerConnection.AddDataChannelAsync("vido", true, true);
            videoChannel.MessageReceived += OnMessageReceived;
            videoChannel.StateChanged += () => Console.WriteLine($"VidoChannel State Changed: {videoChannel.State}");

            forceChannel = await peerConnection.AddDataChannelAsync($"frce", true, true);
            forceChannel.MessageReceived += OnMessageReceived;
            forceChannel.StateChanged += () => Console.WriteLine($"FrceChannel State Changed: {forceChannel.State}");

            depthChannel = await peerConnection.AddDataChannelAsync($"dpth", true, true);
            depthChannel.MessageReceived += OnMessageReceived;
            depthChannel.StateChanged += () => Console.WriteLine($"dpthChannel State Changed: {depthChannel.State}");
        }

        private static void OnConnected()
        {
            Console.WriteLine("Peer connection successfully established!");
        }


        private async void onIceGatheringChange()
        {

            Console.WriteLine("ICE Gathering State Changed: Complete");

            await webSocket.SendMessageAsync(3, localdescription, "mn_fol");
        }

        private void OnMessageReceived(byte[] message)
        {
            string messageText = Encoding.UTF8.GetString(message);
            Console.WriteLine($"Received message on channel: {messageText}");
        }

        private async Task StartFrceTest()
        {
            try
            {
                while (peerConnection == null || !peerConnection.Initialized)
                {
                    await Task.Delay(500);
                    Console.WriteLine("Waiting for PeerConnection to initialize...");
                }

                while (true)
                {
                    if (currentState != IceConnectionState.Completed && currentState != IceConnectionState.Connected)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    if (forceChannel != null && forceChannel.State == DataChannel.ChannelState.Open)
                    {
                        string message = "frcetest";
                        byte[] buffer = Encoding.UTF8.GetBytes(message);

                        string message2 = "dpthtest";
                        byte[] buffer2 = Encoding.UTF8.GetBytes(message2);
                        try
                        {

                            forceChannel.SendMessage(buffer);
                            depthChannel.SendMessage(buffer2);
                            Console.WriteLine("Frce data sent");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error sending message: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("DataChannel is not ready or open.");
                    }
                    await Task.Delay(1000); // Adjust timing as needed
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred in StartFrceTest: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketClient"/> class.
        /// </summary>
        public async void HandleOfferMessage(string sdpContent)
        {
            localdescription = sdpContent;
            Console.WriteLine("HandleOfferMessage ");
            // Set the received SDP as the remote description
            var offer = new SdpMessage { Type = SdpMessageType.Offer, Content = sdpContent };
            await peerConnection.SetRemoteDescriptionAsync(offer);
            bool res = peerConnection.CreateAnswer();
            // Create and send an answer to the received offer
            //SdpMessage sdpmessage = new SdpMessage();
            //sdpmessage.Content = sdpContent;
            //await peerConnection.SetRemoteDescriptionAsync(sdpmessage);


            Console.WriteLine("create answer result: " + res);
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

            if (peerConnection == null || !peerConnection.Initialized || localdescription == null)
            {
                Console.WriteLine("PeerConnection is not initialized.");
                return;
            }

            if (!sdpContent.Contains("candidate"))
            {
                return;
            }

            try
            {
                string modifiedsdpContent = sdpContent.Replace("\r\n", "\n");
                Console.WriteLine("======================");
                Console.WriteLine(modifiedsdpContent);
                SdpMessage answer = new SdpMessage { Type = SdpMessageType.Answer, Content = sdpContent };
                await peerConnection.SetRemoteDescriptionAsync(answer);
                //Console.WriteLine("create answer result: " + res);
                Console.WriteLine("PeerConnection successfully SetRemoteDescription");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set remote description: {ex.Message}");
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

        public void SendDepth(byte[] depthstream)
        {
            if (depthChannel.State == DataChannel.ChannelState.Open)
                depthChannel.SendMessage(depthstream);
        }

        public void SendVido(byte[] vidostream)
        {
            if (videoChannel.State == DataChannel.ChannelState.Open) 
            {
                SliceData(vidostream);
            }
        }

        public void SliceData(byte[] videobyte)
        {
            byte[] metadata_bytes = new byte[16];
            int numChunk = 10;

            int totalLength = videobyte.Length;
            int chunkSize = totalLength / numChunk;
            int startIndex = 0;

            System.Random rnd = new System.Random();
            ushort label = 1001;
            ushort dataID = (ushort)rnd.Next(ushort.MinValue, ushort.MaxValue + 1);

            for (int i = 0; i < numChunk; i++)
            {
                int endIndex = startIndex + chunkSize;
                if (i == numChunk - 1)
                {
                    endIndex = totalLength;
                }

                int chunkLength = endIndex - startIndex;
                byte[] chunk = new byte[chunkLength];

                // Copy bytes from the original array to the chunk
                for (int j = 0; j < chunkLength; j++)
                {
                    chunk[j] = videobyte[startIndex + j];
                }
  
                AssembleSendPacket(metadata_bytes, chunk, label, dataID, startIndex, totalLength);

                startIndex = endIndex;
            }
        }

        private void AssembleSendPacket(byte[] metadata_bytes, byte[] chunk, ushort label, ushort dataID, int startIndex, int totalLength)
        {
            metadata_bytes[0] = (byte)(label & 0xFF);
            metadata_bytes[1] = (byte)(label >> 8);

            metadata_bytes[2] = (byte)(dataID & 0xFF);
            metadata_bytes[3] = (byte)(dataID >> 8);
            byte[] newOffsetBytes = BitConverter.GetBytes(startIndex);
            byte[] newDatalengthBytes = BitConverter.GetBytes(totalLength);


            // Overwrite datalength value with converted frame datalength
            for (int i = 0; i < newDatalengthBytes.Length; i++)
            {
                metadata_bytes[4 + i] = newDatalengthBytes[i];
            }

            // Overwrite offset value to the startIndex of current chunk
            for (int i = 0; i < newOffsetBytes.Length; i++)
            {
                metadata_bytes[8 + i] = newOffsetBytes[i];
            }


            byte[] senddata = new byte[metadata_bytes.Length + chunk.Length];
            System.Buffer.BlockCopy(metadata_bytes, 0, senddata, 0, metadata_bytes.Length);

            System.Buffer.BlockCopy(chunk, 0, senddata, metadata_bytes.Length, chunk.Length);

            videoChannel.SendMessage(senddata);
        }
    }
}
