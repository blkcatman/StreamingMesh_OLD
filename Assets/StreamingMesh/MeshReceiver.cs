//MeshReceiver.cs
//
//Copyright (c) 2016 Tatsuro Matsubara
//Released under the MIT license
//http://opensource.org/licenses/mit-license.php
//

using UnityEngine;
using System;
using System.Collections.Generic;

[RequireComponent(typeof(WebRTCManager), typeof(SkinnedMeshesValidator))]
public class MeshReceiver : MonoBehaviour {

	private WebRTCManager webRtcManager;
	private SkinnedMeshesValidator validator;

	//temporary buffers
	List<int[]> indicesBuf = new List<int[]>();
	Vector3[][] vertsBuf;
	Vector3[][] vertsBuf_old;
	Vector3 position;
	Vector3 position_old;
	List<int> linedIndices = new List<int>();

	//gameobjects and meshes
	List<GameObject> meshObjects = new List<GameObject>();
	GameObject localRoot = null;
	List<Mesh> meshBuf = new List<Mesh>();

	int areaRange = 4;
	int packageSize = 128;

	//bool isConnected = false;
	bool isRequestComplete = false;

	//uint currentTimeStamp = 0;

	float currentTime = 0.0f;
	float timeWeight = 0.0f;

	float lagTime = 0.0f;
	byte lastDataType = 0x0F;
	bool getLag = false;

	//global params
	public float timeSpan = 0.01f;
	public bool interpolateVertices = true;
	public bool autoReconnect = true;

	bool _interpolate = true;

	void Reset() {
		if(localRoot != null) {
			DestroyImmediate(localRoot);
		}
		
		foreach(GameObject obj in meshObjects) {
			DestroyImmediate(obj);
		}
		meshBuf.Clear();
		indicesBuf.Clear();
		vertsBuf = null;
		vertsBuf_old = null;
		linedIndices.Clear();

		//isConnected = false;
		isRequestComplete = false;
	}

	void Awake() {
		validator = gameObject.GetComponent<SkinnedMeshesValidator>();
		webRtcManager = gameObject.GetComponent<WebRTCManager>();
	}

	void Start () {
		_interpolate = interpolateVertices;

		Reset();
		validator.StartAsReceiver(webRtcManager);
		validator.SetRequestCompleteCallBack(RequestComplete);
		validator.SetVerticesReceivedCallBack(VerticesReceived);
		webRtcManager.SetServerClosedCallBack(ServerClosed);
		Invoke("ConnectToChannel", 5.0f);
	}

	public void ServerClosed() {
		Debug.Log("Receiver: Disconnect from server...");
		Reset();
		if(autoReconnect) {
			Invoke("ConnectToChannel", 4.0f + UnityEngine.Random.value * 2.0f);
		}
	}

	public void ConnectToChannel() {
		webRtcManager.autoReconnect = autoReconnect;
		webRtcManager.ConnectToChannel();
	}

	public void RequestComplete(ref List<KeyValuePair<Mesh, Material[]>> meshes) {
		vertsBuf = new Vector3[meshes.Count][];
		vertsBuf_old = new Vector3[meshes.Count][];

		areaRange = validator.GetAreaRange();
		packageSize = validator.GetPackageSize();

		localRoot = new GameObject("ReceivedGameObject");
		localRoot.transform.SetParent(transform, false);

		for(int i = 0; i < meshes.Count; i++) {
			KeyValuePair<Mesh, Material[]> pair = meshes[i];
            GameObject obj = new GameObject("Mesh"+ i);
			obj.transform.SetParent(localRoot.transform, false);
			MeshFilter filter = obj.AddComponent<MeshFilter>();
			MeshRenderer renderer = obj.AddComponent<MeshRenderer>();

			Mesh mesh = pair.Key;
			filter.mesh = mesh;
			renderer.materials = pair.Value;
			vertsBuf[i] = new Vector3[mesh.vertexCount];
			vertsBuf_old[i] = new Vector3[mesh.vertexCount];

			meshBuf.Add(mesh);
			meshObjects.Add(obj);
		}
		
		//Debug.Log("Receiver: RequestComplete!");

		isRequestComplete = true;
	}

	public void VerticesReceived(byte[] data)
	{
		if(isRequestComplete) {
			byte[] res = { 0x31, 0x00, 0x00, 0x00, 0x00 };
			webRtcManager.RequestData(res, false);

			lastDataType = data[0];

			if(_interpolate) {
				for(int i = 0; i < vertsBuf.Length; i++) {
					vertsBuf[i].CopyTo(vertsBuf_old[i], 0);
				}
				position_old = position;
			}

			//Debug.Log("Received bytes: " + data.Length);

			//byte[] stampBuf = { data[1], data[2], data[3], data[4] };
			//uint stamp = BitConverter.ToUInt32(stampBuf, 0);

			int packages = data[7] * 65536 + data[6] * 256 + data[5];
			bool isCompressed = data[8] == 0x01 ? true : false;

			byte[][] byteVec = new byte[3][];
			for(int i = 0; i < 3; i++) {
				byteVec[i] = new byte[4];
				for(int j = 0; j < 4; j++) {
					byteVec[i][j] = data[i * 4 + j + 9];
				}
			}
			position = new Vector3(
				BitConverter.ToSingle(byteVec[0], 0),
				BitConverter.ToSingle(byteVec[1], 0),
				BitConverter.ToSingle(byteVec[2], 0)
			);

			int offset = 21;

			byte[] buf = null;
#if UNITY_WEBGL || BROTLI_NO_COMPRESS
			buf = data;
#else
			if(isCompressed) {
				int bufSize = data.Length - 21;
				byte[] rawbuf = new byte[bufSize];
				Buffer.BlockCopy(data, 21, rawbuf, 0, bufSize);
				if(!brotli.decompressBuffer(rawbuf, ref buf)) {
					Debug.Log("decompress failed!");
					return;
				}
				offset = 0;
			} else {
				buf = data;
			}
#endif
			
			if(data[0] == 0x0F) {
				linedIndices.Clear();

				for(int i = 0; i < packages; i++) {
					VertexPack vp = new VertexPack();
					vp.tx = buf[offset];
					vp.ty = buf[offset + 1];
					vp.tz = buf[offset + 2];
					vp.poly1 = buf[offset + 3];
					vp.poly2 = buf[offset + 4];
					vp.poly3 = buf[offset + 5];

					offset += 6;

					int hk = packageSize / 2;
					int qk = hk / areaRange;

					int vertCount = vp.poly3 * 65536 + vp.poly2 * 256 + vp.poly1;
					for(int j = 0; j < vertCount; j++) {
						ByteCoord v = new ByteCoord();
						v.p1 = buf[offset + j * 5];
						v.p2 = buf[offset + j * 5 + 1];
						v.p3 = buf[offset + j * 5 + 2];
						int compress = 0;
						compress += buf[offset + j * 5 + 3];
						compress += (ushort)(buf[offset + j * 5 + 4] << 8);

						v.x = (byte)(compress & 0x1F);
						v.y = (byte)((compress >> 5) & 0x1F);
						v.z = (byte)((compress >> 10) & 0x1F);

						float x = ((int)vp.tx - hk) / (float)qk;
						float y = ((int)vp.ty - hk) / (float)qk;
						float z = ((int)vp.tz - hk) / (float)qk;
						x += (float)v.x / (32 * (float)qk);
						y += (float)v.y / (32 * (float)qk);
						z += (float)v.z / (32 * (float)qk);

						int vertIdx = v.p2 * 256 + v.p1;
						int meshIdx = v.p3;

						Vector3 vert = new Vector3(x, y, z);
						vertsBuf[meshIdx][vertIdx] = vert;
						linedIndices.Add(meshIdx * 0x10000 + vertIdx);
					}
					offset += (vertCount * 5);
				}
				if(getLag && _interpolate) {
					for(int i = 0; i < vertsBuf.Length; i++) {
						vertsBuf[i].CopyTo(vertsBuf_old[i], 0);
					}
					position_old = position;
				}
				getLag = false;
				_interpolate = interpolateVertices;
			} else if(data[0] == 0x0E && !getLag) {
				for(int i = 0; i < linedIndices.Count; i++) {
					int meshIdx = (linedIndices[i] >> 16) & 0xFF;
					int vertIdx = linedIndices[i] & 0xFFFF;
					int ix = buf[i * 3 + offset];
					int iy = buf[i * 3 + offset + 1];
					int iz = buf[i * 3 + offset + 2];
					float dx = ((float)ix - 128f) / 128f;
					float dy = ((float)iy - 128f) / 128f;
					float dz = ((float)iz - 128f) / 128f;
					float x = Mathf.Sign(dx) * Mathf.Pow(Mathf.Abs(dx), 2f);
					float y = Mathf.Sign(dy) * Mathf.Pow(Mathf.Abs(dy), 2f);
					float z = Mathf.Sign(dz) * Mathf.Pow(Mathf.Abs(dz), 2f);

					Vector3 vec = vertsBuf[meshIdx][vertIdx];
					vec = vec + new Vector3(x, y, z);
					vertsBuf[meshIdx][vertIdx] = vec;
				}
			}
			//currentTimeStamp = stamp;
		}
		if(!_interpolate) {
			if(lagTime >= 0.1f && lastDataType == 0x0E) {
				getLag = true;
				UpdateVerts();
				return;
			}
			lagTime = 0.0f;
			UpdateVerts();
		} else {
			timeWeight = 0.0f;
		}
	}

	// Update is called once per frame
	void Update() {
		if(_interpolate && isRequestComplete) {
			currentTime += Time.deltaTime;
			if(currentTime > timeSpan) {
				currentTime -= timeSpan;
				if(lagTime >= 0.1f && lastDataType == 0x0E) {
					_interpolate = false;
					getLag = true;
					UpdateVerts();
					return;
				}
				lagTime = 0.0f;
				UpdateVertsInterpolate();
			}
		}
		lagTime += Time.deltaTime;
	}

	void UpdateVerts() {
		for(int i = 0; i < meshBuf.Count; i++) {
			meshBuf[i].SetVertices(new List<Vector3>(vertsBuf[i]));
			meshBuf[i].RecalculateNormals();
			meshBuf[i].RecalculateBounds();
			localRoot.transform.localPosition = position;
		}
	}

	void UpdateVertsInterpolate() {
		if(timeWeight < 1.0f) {
			for(int i = 0; i < vertsBuf.Length; i++) {
				Vector3[] tempBuf = new Vector3[vertsBuf[i].Length];
				Vector3 tempPos = new Vector3();
				for(int j = 0; j < vertsBuf[i].Length; j++) {
					Vector3 old = vertsBuf_old[i][j];
					Vector3 dst = vertsBuf[i][j];
					tempBuf[j] = old * (1.0f - timeWeight) + dst * timeWeight;
					tempPos = position_old * (1.0f - timeWeight) + position * timeWeight;
				}
				meshBuf[i].SetVertices(new List<Vector3>(tempBuf));
				meshBuf[i].RecalculateNormals();
				meshBuf[i].RecalculateBounds();
				localRoot.transform.localPosition = tempPos;
			}
		}
		timeWeight += 0.1f;
	}

}
