//WebRTCManager.cs
//
//Copyright (c) 2016 Tatsuro Matsubara
//Released under the MIT license
//http://opensource.org/licenses/mit-license.php
//

using UnityEngine;

using System;
using System.Collections;
using System.Collections.Generic;

using Byn.Net;
using System.Linq;

public struct ConnectionStatus {
	public bool isReadyForStream;
	public bool isCompressStream;
	public bool isTemporalStop;
	public int sendQueue;
};

public class WebRTCManager : MonoBehaviour {

	public string signalingUrl = "wss://webrtc.blkcatman.net:12777";
	public string stunServer = "stun://stun.l.google.com:19302";
	public string channel = "";

	public delegate void Connected();
	Connected connected;
	public delegate void UserConnected(ConnectionId id);
	UserConnected userConnected;
	public delegate void FirstResponce(byte[] data);
	FirstResponce firstResponce;
	public delegate void DataReceived(byte[] data, ConnectionId id);
	DataReceived dataReceived;
	public delegate void ServerClosed();
	ServerClosed serverClosed;

	private IBasicNetwork mNetwork = null;
	private bool mIsServer = false;
	private List<KeyValuePair<ConnectionId, ConnectionStatus>> mConnections = new List<KeyValuePair<ConnectionId, ConnectionStatus>>();

	private ConnectionId serverID = ConnectionId.INVALID;

	public bool autoReconnect = false;

	private void Start()
	{
		if(channel.Length == 0) {
			System.Random random = new System.Random();
			const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
			channel = "channel_" + new string(Enumerable.Repeat(chars, 8)
			  .Select(s => s[random.Next(s.Length)]).ToArray());
		}
		/*
		WebRtcNetworkFactory factory = WebRtcNetworkFactory.Instance;
		if (factory != null) {
			//Debug.Log("WebRtcNetworkFactory created");
		}*/
	}
	
	private void Setup()
	{
		mNetwork = WebRtcNetworkFactory.Instance.CreateDefault(signalingUrl, new string[] { stunServer });
		if (mNetwork != null) {
			//Debug.Log("WebRTCNetwork created");
		} else {
			Debug.Log("Failed to access WebRTC");
		}
	}

	private void Cleanup()
	{
		if(mNetwork != null) {
			mNetwork.Dispose();
			mNetwork = null;
		}
	}

	private void Reset()
	{
		mIsServer = false;
		mConnections = new List<KeyValuePair<ConnectionId, ConnectionStatus>>();
		Cleanup();
	}

	private void OnDestroy()
	{
		if (mNetwork != null)
		{
			Cleanup();
		}
	}

	private void FixedUpdate()
	{
		HandleNetwork();
    }

	void HandleNetwork()
	{
		if(mNetwork != null) {
			mNetwork.Update();
			NetworkEvent evt;
			while(mNetwork != null && mNetwork.Dequeue(out evt)) {
				//Debug.Log(evt);
				switch(evt.Type) {
					case NetEventType.ServerInitialized:
						{
							//server initialized message received
							mIsServer = true;
							if(connected != null) {
								connected();
							}
						}
						break;

					case NetEventType.ServerInitFailed:
						{
							//user tried to start the server but it failed
							mIsServer = false;
							Debug.Log("Server start failed: " + channel);
							Reset();
							Invoke("CreateChannel", 5.0f);
						}
						break;

					case NetEventType.ServerClosed:
						{
							mIsServer = false;
							Debug.Log("Server closed. No incoming connections possible until restart.");
							Reset();
						}
						break;

					case NetEventType.NewConnection:
						{
							ConnectionStatus status;
							status.isReadyForStream = false;
							status.isCompressStream = false;
							status.isTemporalStop = false;
							status.sendQueue = 0;
							mConnections.Add(
								new KeyValuePair<ConnectionId, ConnectionStatus>(evt.ConnectionId, status)
							);
							if(mIsServer) {
								if(userConnected != null) {
									userConnected(evt.ConnectionId);
								}
							}
						}
						break;

					case NetEventType.ConnectionFailed:
						{
							//Outgoing connection failed. Inform the user.
							Debug.Log("Connection failed: " + channel);
							Reset();
							Invoke("ConnectToChannel", 5.0f);
						}
						break;

					case NetEventType.Disconnected:
						{
							foreach(KeyValuePair<ConnectionId, ConnectionStatus> p in mConnections) {
								if(evt.ConnectionId.Equals(p.Key)) {
									mConnections.Remove(p);
									break;
								}
							}

							//A connection was disconnected
							Debug.Log("Local Connection ID " + evt.ConnectionId + " disconnected");
							if(mIsServer == false) {
								//Reset();
							} else {
								//other users left? inform them 
								if(mConnections.Count > 0) {
									//SendString(userLeftMsg);
								}
							}
						}
						break;
					case NetEventType.ReliableMessageReceived:
					case NetEventType.UnreliableMessageReceived:
						{
							HandleIncommingData(ref evt);
						}
						break;
				}
			}

			//finish this update by flushing the messages out if the network wasn't destroyed during update
			if(mNetwork != null)
				mNetwork.Flush();
		}
	}

	public bool isServer {
		get { return mIsServer; }
	}

	public int connections {
		get { return mConnections.Count; }
	}

	public void ConnectToChannel() {
		Setup();
		if (mNetwork != null) {
			//ConnectionId connect = mNetwork.Connect(channel);
			mNetwork.Connect(channel);
			Debug.Log("ConnectToServer: " + channel);
		}
	}

	public void CreateChannel() {
		Setup();
		if (mNetwork != null) {
			mNetwork.StartServer(channel);
			Debug.Log("StartServer: " + channel);
		}
	}

	public void CloseServer() {
		if (mIsServer) {
			foreach(KeyValuePair<ConnectionId, ConnectionStatus> p in mConnections) {
				ConnectionId id = p.Key;
				byte[] closeData = { 0xFF, 0x00 };
				mNetwork.SendData(id, closeData, 0, closeData.Length, true);
			}

			mNetwork.StopServer();
			Reset();
        }
	}

	public void SetConnectedCallBack(Connected callback) {
		connected += callback;
	}

	public void SetUserConnectedCallBack(UserConnected callback) {
		userConnected += callback;
	}

	public void SetFirstResponceCallBack(FirstResponce callback) {
		firstResponce += callback;
	}

	public void SetDataReceivedCallBack(DataReceived callback) {
		dataReceived += callback;
	}

	public void SetServerClosedCallBack(ServerClosed callback) {
		serverClosed += callback;
	}

	private void HandleIncommingData(ref NetworkEvent evt)
	{
		MessageDataBuffer buffer = (MessageDataBuffer)evt.MessageData;

        //Disconnection request from MeshSender
		if(buffer.Buffer[0] == 0xFF && !mIsServer) {
			if (serverClosed != null) serverClosed();
            buffer.Dispose();
			Reset();
            return;
		}

		//preparing is complete as break signal
		if(buffer.Buffer[0] == 0x30 && mIsServer) {
			for(int i = 0; i < mConnections.Count; i++) {
				if(evt.ConnectionId.Equals(mConnections[i].Key)) {
					ConnectionStatus status = new ConnectionStatus();
					status.isReadyForStream = true;
					if(buffer.Buffer[1] == 0x01) {
						status.isCompressStream = true;
					} else {
						status.isCompressStream = false;
					}
					mConnections[i] =
						new KeyValuePair<ConnectionId, ConnectionStatus>(mConnections[i].Key, status);
					break;
				}
			}
			return;
		}

		//stream is complete
		if(buffer.Buffer[0] == 0x31 && mIsServer) {
			for(int i = 0; i < mConnections.Count; i++) {
				if(evt.ConnectionId.Equals(mConnections[i].Key)) {
					ConnectionStatus status = mConnections[i].Value;
					status.sendQueue -= 1;
					if(status.isTemporalStop && status.sendQueue < 1) {
						status.isTemporalStop = false;
						status.sendQueue = 0;
						//Debug.Log("WebRTC: restart stream queuing in " + mConnections[i].Key.ToString());
					}
					mConnections[i] =
						new KeyValuePair<ConnectionId, ConnectionStatus>(mConnections[i].Key, status);
					break;
				}
			}
			return;
		}

		byte[] dataBuffer = new byte[buffer.ContentLength];
		Buffer.BlockCopy(buffer.Buffer, 0, dataBuffer, 0, buffer.ContentLength);

		if(buffer.Buffer[0] == 0x01 && !mIsServer) {
			serverID = evt.ConnectionId;
			if(firstResponce != null) {
				firstResponce(dataBuffer);
				buffer.Dispose();
				return;
			}
		}

		if(dataReceived != null) {
			dataReceived(dataBuffer, evt.ConnectionId);
		}

		//return the buffer so the network can reuse it
		buffer.Dispose();
	}

	public bool SendVertexStream(byte[] rawData, byte[] compressedData, Vector3 position, int packages, bool isIframe, uint stamp, bool reliable = true)
	{
		if (mNetwork == null || mConnections.Count == 0)
		{
			//Debug.Log("No connection. Can't send message.");
			return false;
		}
		else
		{
			for(int i = 0; i < mConnections.Count; i++) {
				KeyValuePair<ConnectionId, ConnectionStatus> p = mConnections[i];
				ConnectionId id = p.Key;
				ConnectionStatus status = p.Value;
				if(status.isReadyForStream && !status.isTemporalStop) {
					byte[] sendData = null;
                    if(status.isCompressStream && compressedData != null) {
						sendData = new byte[compressedData.Length + 21];
						Buffer.BlockCopy(compressedData, 0, sendData, 21, compressedData.Length);
						sendData[8] = 0x01;
					} else {
						sendData = new byte[rawData.Length + 21];
						Buffer.BlockCopy(rawData, 0, sendData, 21, rawData.Length);
						sendData[8] = 0x00;
					}

					if(isIframe) {
						sendData[0] = 0xF;
						byte[] stampBuf = BitConverter.GetBytes(stamp);
						sendData[1] = stampBuf[0];
                        sendData[2] = stampBuf[1];
						sendData[3] = stampBuf[2];
						sendData[4] = stampBuf[3];

						sendData[5] = (byte)((packages & 0xFF));
						sendData[6] = (byte)((packages & 0xFF00) >> 8);
						sendData[7] = (byte)((packages & 0xFF0000) >> 16);
						//sendData[8] ;
					} else {
						sendData[0] = 0xE;
					}
					byte[][] vec = new byte[3][];
                    vec[0] = BitConverter.GetBytes(position.x);
					vec[1] = BitConverter.GetBytes(position.y);
					vec[2] = BitConverter.GetBytes(position.z);
					for(int j = 0; j < 3; j++) {
						for(int k = 0; k < 4; k++) {
							sendData[j * 4 + k + 9] = vec[j][k];
						}
					}

					mNetwork.SendData(id, sendData, 0, sendData.Length, reliable);
					status.sendQueue += 1;
					if(status.sendQueue > 10) {
						status.isTemporalStop = true;
						//Debug.Log("WebRTC: stop stream queuing in " + id.ToString());
					}

					mConnections[i] = new KeyValuePair<ConnectionId, ConnectionStatus>(id, status);
                }
			}
			return true;
		}
	}

	public bool SendData(byte[] msgData, ConnectionId id, bool reliable = true) {
		if(mNetwork == null || mConnections.Count == 0) {
			//Debug.Log("No connection. Can't send message.");
			return false;
		} else {
				mNetwork.SendData(id, msgData, 0, msgData.Length, reliable);
			return true;
		}
	}

	public bool RequestData(byte[] msgData, bool reliable = true) {
		if(mNetwork == null || mConnections.Count == 0 || !serverID.IsValid()) {
			//Debug.Log("No connection. Can't send message.");
			return false;
		} else {
			mNetwork.SendData(serverID, msgData, 0, msgData.Length, reliable);
			return true;
		}
	}

	public bool RequestDataFromRandom(byte[] msgData, bool reliable = true) {
		if(mNetwork == null || mConnections.Count == 0 || !serverID.IsValid()) {
			//Debug.Log("No connection. Can't send message.");
			return false;
		} else {
			System.Random rnd = new System.Random();
			int randomID = rnd.Next(0, mConnections.Count - 1);
			ConnectionId conID = mConnections[randomID].Key;
			mNetwork.SendData(conID, msgData, 0, msgData.Length, reliable);
			return true;
		}
	}


}
