//SkinnedMeshesValidator.cs
//
//Copyright (c) 2016 Tatsuro Matsubara
//Released under the MIT license
//http://opensource.org/licenses/mit-license.php
//

using UnityEngine;
using System;
using System.Collections.Generic;
using System.Text;

#if UNITY_EDITOR
using UnityEditor;
#endif

using DamienG.Security.Cryptography;
using System.Collections;

using Byn.Net;

[System.Serializable]
public class ValidatorPair : Serialize.KeyAndValue<string, Shader> {
	public ValidatorPair(string key, Shader value) : base(key, value) {
	}
}

[System.Serializable]
public class ValidatorTable : Serialize.TableBase<string, Shader, ValidatorPair> {
}

[RequireComponent(typeof(WebRTCManager))]
public class SkinnedMeshesValidator : MonoBehaviour {

	private WebRTCManager webRTCManager;

	int areaRange = 4;
	int packageSize = 128;

	bool isTimerReady = false;
	uint timeStamp = 0;

	List<SkinnedMeshRenderer> skinnedMeshRenderers = new List<SkinnedMeshRenderer>();

	byte[] configData;
	Dictionary<uint, byte[]> meshrenderersValidator = new Dictionary<uint, byte[]>();
	Dictionary<uint, byte[]> materialsValidator = new Dictionary<uint, byte[]>();
	Dictionary<uint, byte[]> texturesValidator = new Dictionary<uint, byte[]>();

	public List<KeyValuePair<Mesh, Material[]>> meshes = new List<KeyValuePair<Mesh, Material[]>>();
	public Dictionary<string, Material> materials = new Dictionary<string, Material>();
	public Dictionary<string, Texture> textures = new Dictionary<string, Texture>();

	public delegate void InitalCreationComplete();
	InitalCreationComplete initalCreationComplete;

	public delegate void RequestComplete(ref List<KeyValuePair<Mesh, Material[]>> meshes);
	RequestComplete requestComplete;
	public delegate void VerticesReceived(byte[] data);
	VerticesReceived verticesReceived;

	List<byte[]> requestQueue = new List<byte[]>();
	byte[] lastRequest = null;

	List<byte> tempBuffer = new List<byte>();

	bool isProcessingToAdd = false;

	public Shader defaultShader;

	public ValidatorTable shaders;

	public int GetAreaRange() {
		return areaRange;
	}

	public int GetPackageSize() {
		return packageSize;
	}

	void Reset() {
		timeStamp = 0;

		skinnedMeshRenderers.Clear();
		meshrenderersValidator.Clear();
		materialsValidator.Clear();
		texturesValidator.Clear();


		meshes.Clear();
		materials.Clear();
		textures.Clear();

		requestQueue.Clear();
		lastRequest = null;

		tempBuffer = new List<byte>();

		isProcessingToAdd = false;
	}

	/// <summary>
	/// Start as MeshSender support class
	/// </summary>
	public void StartAsSender(WebRTCManager manager, int range, int packSize) {
		webRTCManager = manager;
		areaRange = range;
		packageSize = packSize;
		webRTCManager.SetUserConnectedCallBack(UserConnected);
		webRTCManager.SetDataReceivedCallBack(DataReceivedAsSender);
		webRTCManager.SetServerClosedCallBack(ServerClosed);
		if(isTimerReady) {
			StartTimer();
		}
	}

	/// <summary>
	/// Start as MeshReceiver support class
	/// </summary>
	public void StartAsReceiver(WebRTCManager manager) {
		webRTCManager = manager;
		webRTCManager.SetDataReceivedCallBack(DataReceivedAsReceiver);
		webRTCManager.SetFirstResponceCallBack(FirstResponce);
	}

	public void ServerClosed() {
		Reset();
		Debug.Log("Validator: Disconnect from server...");
	}

	void StartTimer() {
		isTimerReady = true;
		StartCoroutine("DoTimer");
	}

	IEnumerator DoTimer() {
		while(isTimerReady) {
			yield return new WaitForSeconds(1);
			timeStamp += 1;
		}
	}

	/// <summary>
	/// Set SkinnedMeshRenderers
	/// </summary>
	/// <param name="renderers"></param>
	public void SetSkinnedMeshRenderers(List<SkinnedMeshRenderer> renderers, bool isReimportTexture) {
#if UNITY_EDITOR
		Reset();
		skinnedMeshRenderers = renderers;
		StartCoroutine(UpdateConfigs(renderers, isReimportTexture));
		//UpdateConfigs(renderers);
#endif
	}

	public void SetInitalCreationCompleteCallback(InitalCreationComplete callback) {
		initalCreationComplete = callback;
	}

	public void SetVerticesReceivedCallBack(VerticesReceived callback) {
		verticesReceived = callback;
	}

	public void SetRequestCompleteCallBack(RequestComplete callback) {
		requestComplete = callback;
	}

	/// <summary>
	/// Update / Create Validation Configs
	/// </summary>
	IEnumerator UpdateConfigs(List<SkinnedMeshRenderer> renderers, bool isReimportTexture) {
#if UNITY_EDITOR
		Dictionary<string, Material> materials = new Dictionary<string, Material>();
		Dictionary<string, Texture> textures = new Dictionary<string, Texture>();
		int meshCnt = 0;
		foreach(SkinnedMeshRenderer renderer in renderers) {
			if(renderer == null) {
				continue;
			}
			Mesh mesh = renderer.sharedMesh;
			if(mesh != null) {
				meshCnt++;
				CreateMeshInfo(renderer);
				yield return null;
			} else {
				continue;
			}

			int subMeshCnt = mesh.subMeshCount;
			for(int i = 0; i < subMeshCnt; i++) {
				Material mat = renderer.sharedMaterials[i];
				if(mat != null) {
					CreateMaterialInfo(mat);
					yield return null;
				} else {
					continue;
				}
				Material bufMat;
				// check existing material
				if(!materials.TryGetValue(mat.name, out bufMat)) {
					materials.Add(mat.name, mat);
					int propertyCount = ShaderUtil.GetPropertyCount(mat.shader);

					//Get Texture from Shader;
					for(int j = 0; j < propertyCount; j++) {
						string propName = ShaderUtil.GetPropertyName(mat.shader, j);
						//if(propName != "_MainTex") {
						//	continue;
						//}
						ShaderUtil.ShaderPropertyType type = ShaderUtil.GetPropertyType(mat.shader, j);
						if(type == ShaderUtil.ShaderPropertyType.TexEnv) {
							Texture tex = mat.GetTexture(propName);
							if(tex != null) {
								CreateTextureInfo(tex, isReimportTexture);
								yield return null;
							} else {
								continue;
							}
							Texture bufTexture;
							// check existing texture
							if(!textures.TryGetValue(tex.name, out bufTexture)) {
								textures.Add(tex.name, tex);
							}
						}
					}
				}
			}

		}
		int offset = 0;
		configData = new byte[
			10 +
			(sizeof(uint) * meshCnt) +
			(sizeof(uint) * materials.Count) +
			(sizeof(uint) * textures.Count)
		];

		configData[0] = 1; // COMM as config data sending
		offset += 1;

		Buffer.BlockCopy(BitConverter.GetBytes(timeStamp), 0, configData, offset, 4);
		offset += sizeof(uint);
		configData[offset    ] = (byte)areaRange;
		configData[offset + 1] = (byte)packageSize;
		configData[offset + 2] = (byte)textures.Count;
		configData[offset + 3] = (byte)materials.Count;
		configData[offset + 4] = (byte)meshCnt;
		offset += 5;

		uint[] meshKeys = new uint[meshrenderersValidator.Keys.Count];
		meshrenderersValidator.Keys.CopyTo(meshKeys, 0);
		uint[] matKeys = new uint[materialsValidator.Keys.Count];
		materialsValidator.Keys.CopyTo(matKeys, 0);
		uint[] texKeys = new uint[texturesValidator.Keys.Count];
		texturesValidator.Keys.CopyTo(texKeys, 0);

		List<uint> allKeys = new List<uint>();
		allKeys.AddRange(texKeys);
		allKeys.AddRange(matKeys);
		allKeys.AddRange(meshKeys);
		Buffer.BlockCopy(allKeys.ToArray(), 0, configData, offset, allKeys.Count * sizeof(uint));
		offset += allKeys.Count * sizeof(uint);
#endif
		yield return null;
		initalCreationComplete();
	}

	public static void SetTexturesWriteFlags(SkinnedMeshRenderer[] renderers, bool flag) {
#if UNITY_EDITOR
		foreach(SkinnedMeshRenderer renderer in renderers) {

			foreach(Material mat in renderer.sharedMaterials) {
				int propertyCount = ShaderUtil.GetPropertyCount(mat.shader);
				for(int i = 0; i < propertyCount; i++) {
					string propName = ShaderUtil.GetPropertyName(mat.shader, i);
					ShaderUtil.ShaderPropertyType type = ShaderUtil.GetPropertyType(mat.shader, i);
					if(type == ShaderUtil.ShaderPropertyType.TexEnv) {
						Texture texture = mat.GetTexture(propName);
						//Change texture readable flag from Texture Importer
						string pass = AssetDatabase.GetAssetPath(texture);
						TextureImporter ti = TextureImporter.GetAtPath(pass) as TextureImporter;
						ti.isReadable = flag;
						ti.textureFormat = TextureImporterFormat.RGBA32;
						AssetDatabase.ImportAsset(pass);
					}
				}
			}
		}
#endif
	}

#if UNITY_EDITOR
	uint CreateMeshInfo(SkinnedMeshRenderer renderer) {
		Mesh mesh = renderer.sharedMesh;
		byte[] meshInfoData = GetMeshInfoToByteArray(mesh, renderer.sharedMaterials);
		uint crcHash = BitConverter.ToUInt32(GetCRC32Hash(ref meshInfoData), 0);
		byte[] dummy;
		if(!meshrenderersValidator.TryGetValue(crcHash, out dummy)) {
			meshrenderersValidator.Add(crcHash, meshInfoData);
		} else {
			return 0;
		}
		return crcHash;
	}

	uint CreateMaterialInfo(Material mat) {
		byte[] materialInfoData = GetMaterialInfoToByteArray(mat);
		uint crcHash = BitConverter.ToUInt32(GetCRC32Hash(ref materialInfoData), 0);
		byte[] dummy;
		if(!materialsValidator.TryGetValue(crcHash, out dummy)) {
			materialsValidator.Add(crcHash, materialInfoData);
		} else {
			return 0;
		}
		return crcHash;
	}

	uint CreateTextureInfo(Texture tex, bool isReimportTexture) {
		byte[] textureInfoData = GetTextureToPNGByteArray(tex, isReimportTexture);
		uint crcHash = BitConverter.ToUInt32(GetCRC32Hash(ref textureInfoData), 0);
		byte[] dummy;
		if(!texturesValidator.TryGetValue(crcHash, out dummy)) {
			texturesValidator.Add(crcHash, textureInfoData);
		} else {
			return 0;
		}
		return crcHash;
	}
#endif

	public void UserConnected(ConnectionId id) {
		webRTCManager.SendData(configData, id);
	}

	public void FirstResponce(byte[] data) {
		byte comm = data[0];
		if(comm == 0x01) {
			Reset();

			int offset = 1;
			uint stamp = 0;
			stamp += data[offset];
			stamp += (uint)data[offset + 1] << 8;
			stamp += (uint)data[offset + 2] << 16;
			stamp += (uint)data[offset + 3] << 24;
			offset += 4;
			areaRange = data[offset];
			packageSize = data[offset + 1];
			int texCnt = data[offset + 2];
			int matCnt = data[offset + 3];
			int meshCnt = data[offset + 4];
			offset += 5;

			List<uint> hashes = new List<uint>();
			for(int ofs = offset; ofs < data.Length; ofs += 4) {
				uint hash = 0;
				hash += data[ofs];
				hash += (uint)data[ofs + 1] << 8;
				hash += (uint)data[ofs + 2] << 16;
				hash += (uint)data[ofs + 3] << 24;
				hashes.Add(hash);
			}

			int cnt = 0;
			for(int i = 0; i < texCnt; i++) {
				byte[] request = new byte[10];
				request[0] = 0x14;
				uint hash = hashes[cnt];
				request[5] = (byte)(hash & 0xFF);
				request[6] = (byte)((hash >> 8) & 0xFF);
				request[7] = (byte)((hash >> 16) & 0xFF);
				request[8] = (byte)((hash >> 24) & 0xFF);
				request[9] = (byte)i;
				requestQueue.Add(request);
				cnt++;
			}
			for(int i = 0; i < matCnt; i++) {
				byte[] request = new byte[10];
				request[0] = 0x13;
				uint hash = hashes[cnt];
				request[5] = (byte)(hash & 0xFF);
				request[6] = (byte)((hash >> 8) & 0xFF);
				request[7] = (byte)((hash >> 16) & 0xFF);
				request[8] = (byte)((hash >> 24) & 0xFF);
				request[9] = (byte)i;
				requestQueue.Add(request);
				cnt++;
			}
			for(int i = 0; i < meshCnt; i++) {
				byte[] request = new byte[10];
				request[0] = 0x12;
				uint hash = hashes[cnt];
				request[5] = (byte)(hash & 0xFF);
				request[6] = (byte)((hash >> 8) & 0xFF);
				request[7] = (byte)((hash >> 16) & 0xFF);
				request[8] = (byte)((hash >> 24) & 0xFF);
				request[9] = (byte)i;
				requestQueue.Add(request);
				cnt++;
			}
		}
		configData = data;
		StartCoroutine("DequeueRequests", requestQueue.ToArray());
	}

	public void DataReceivedAsSender(byte[] data, ConnectionId id) {
		byte comm = data[0];
		int offset = 1;
		//uint stamp = BitConverter.ToUInt32(data, offset);
		offset += 4;
		uint hash = BitConverter.ToUInt32(data, offset);
		offset += 4;
		byte objId = data[offset];

		byte[] header = new byte[5];
		byte[] metaData = null;
		bool valid = false;
		switch(comm) {
			case 0x11: {
					webRTCManager.SendData(configData, id);
				}
				return;
			case 0x12: {
					if(meshrenderersValidator.TryGetValue(hash, out metaData)) {
						header[0] = 0x02;
						valid = true;
					} else {
						header[0] = 0x22;
						header[2] = 0x10;
						valid = false;
					}
				}
				break;
			case 0x13: {
					if(materialsValidator.TryGetValue(hash, out metaData)) {
						header[0] = 0x03;
						valid = true;
					} else {
						header[0] = 0x23;
						header[2] = 0x10;
						valid = false;
					}
				}
				break;
			case 0x14: {
					if(texturesValidator.TryGetValue(hash, out metaData)) {
						header[0] = 0x04;
						valid = true;
					} else {
						header[0] = 0x24;
						header[2] = 0x10;
						valid = false;
					}
				}
				break;
		}
		header[1] = objId;
		if(!valid) {
			webRTCManager.SendData(header, id, true);
		}
		
		int packSize = 48 * 1024;
		int packCnt = metaData.Length / packSize;

		if(metaData.Length % packSize > 0) {
			packCnt += 1;
		}
		header[2] = (byte)packCnt;
		//Debug.Log("packCnt: " + packCnt);
		if(valid) {
			for(int p = 0; p < packCnt; p++) {
				header[3] = (byte)(p + 1);
				int remain = metaData.Length - p * packSize;
				int copySize = remain > packSize ? packSize : remain;

				byte[] packedData = new byte[copySize + 5];
				Buffer.BlockCopy(header, 0, packedData, 0, 5);
				Buffer.BlockCopy(metaData, p * packSize, packedData, 5, copySize);
				webRTCManager.SendData(packedData, id, true);
			}
		}

	}

	public void DataReceivedAsReceiver(byte[] data, ConnectionId id) {
		byte comm = data[0];
		if(comm == 0x02 || comm == 0x03 || comm == 0x04) {
            //int objId = data[1];
			int packCnt = data[2];
			int current = data[3];
			byte[] buf = new byte[data.Length - 5];
			Buffer.BlockCopy(data, 5, buf, 0, buf.Length);
			tempBuffer.AddRange(buf);
			if(current == packCnt) {
				if(comm == 0x02) {
					GetMeshFromByteArray(tempBuffer.ToArray());
				}
				if(comm == 0x03) {
					GetMaterialFromByteArray(tempBuffer.ToArray());
				}
				if(comm == 0x04) {
					GetTextureFromByteArray(tempBuffer.ToArray());
				}
			}
		}

		if(comm == 0x0F || comm == 0x0E) {
			if(verticesReceived != null) {
				verticesReceived(data);
			}
		}

		
		if(comm == 0x12 || comm == 0x13 || comm == 0x14) {
			DataReceivedAsSender(data, id);
		}

		if(comm == 0x22 || comm == 0x23 || comm == 0x24) {
			//byte[] header = new byte[5];
			//header[0] = 0x11;
			//webRTCManager.RequestData(header, true);
			if(lastRequest != null) {
				webRTCManager.RequestData(lastRequest, true);
			} else {
				Debug.LogError("Some data occurs an unexpected error.");
			}
		}
	}

	IEnumerator DequeueRequests(byte[][] requests) {
		for(int i = 0; i < requests.Length; i++) {
			yield return null;
			byte[] request = requests[i];
			lastRequest = request;
			while(!webRTCManager.RequestDataFromRandom(request)) {
				yield return new WaitForSeconds(2.0f);
			}
			isProcessingToAdd = true;
			while(true) {
				if(!isProcessingToAdd) {
					int comm = request[0];
					uint hash = request[5];
					hash += (uint)request[6] << 8;
					hash += (uint)request[7] << 16;
					hash += (uint)request[8] << 24;
					switch(comm) {
						case 0x12:
							if(!meshrenderersValidator.ContainsKey(hash)) {
								meshrenderersValidator.Add(hash, tempBuffer.ToArray());
							}
							break;
						case 0x13:
							if(!materialsValidator.ContainsKey(hash)) {
								materialsValidator.Add(hash, tempBuffer.ToArray());
							}
							break;
						case 0x14:
							if(!texturesValidator.ContainsKey(hash)) {
								texturesValidator.Add(hash, tempBuffer.ToArray());
							}
							break;
					}
					tempBuffer.Clear();
					lastRequest = null;
                    break;
				}
				yield return null;
			}
		}
		if(requestComplete != null) {
#if UNITY_WEBGL
			byte compress = 0x00;
#else
			byte compress = 0x01;
#endif
			byte[] data = { 0x30, compress, 0x00, 0x00, 0x00 };
			webRTCManager.RequestData(data, true);

			requestComplete(ref meshes);
		}
	}

	byte[] GetCRC32Hash(ref byte[] data) {
		Crc32 crc = new Crc32();
		if(data != null) {
			return crc.ComputeHash(data);
		}

		return null;
	}

	byte[] GetMeshInfoToByteArray(Mesh mesh, Material[] mats) {
		if(mesh == null) {
			return null;
		}

		int meshSize = 0;
		ushort vertsCnt = (ushort)mesh.vertexCount;
		ushort subMeshCnt = (ushort)mesh.subMeshCount;
		ushort[] indicesCnts = new ushort[subMeshCnt];

		meshSize = (sizeof(ushort) * 6) + (subMeshCnt * sizeof(ushort));

		//convert indices to short arrays
		ushort[][] indicesArray = new ushort[subMeshCnt][];
		for(int i = 0; i < subMeshCnt; i++) {
			int[] indices = mesh.GetIndices(i);
			indicesCnts[i] = (ushort)indices.Length;
			indicesArray[i] = new ushort[indices.Length];
            for(int j = 0; j < indices.Length; j++) {
				indicesArray[i][j] = (ushort)indices[j];
			}
			meshSize += indices.Length * sizeof(ushort);
		}

		//convert uvs to float single array
		ushort[] singleUVCount = new ushort[4];
		float[][] singleUV = new float[4][];

		if(mesh.uv != null) {
			singleUV[0] = new float[mesh.uv.Length * 2];
			singleUVCount[0] = (ushort)singleUV[0].Length;
			for(int i = 0; i < mesh.uv.Length; i++) {
				singleUV[0][i * 2] = mesh.uv[i].x;
				singleUV[0][i * 2 + 1] = mesh.uv[i].y;
			}
			meshSize += singleUV[0].Length * 2 * sizeof(float);
		}
		if(mesh.uv2 != null) {
			singleUV[1] = new float[mesh.uv2.Length * 2];
			singleUVCount[1] = (ushort)singleUV[1].Length;
			for(int i = 0; i < mesh.uv2.Length; i++) {
				singleUV[1][i * 2] = mesh.uv2[i].x;
				singleUV[1][i * 2 + 1] = mesh.uv2[i].y;
			}
			meshSize += singleUV[1].Length * 2 * sizeof(float);
		}
		if(mesh.uv3 != null) {
			singleUV[2] = new float[mesh.uv3.Length * 2];
			singleUVCount[2] = (ushort)singleUV[2].Length;
			for(int i = 0; i < mesh.uv3.Length; i++) {
				singleUV[2][i * 2] = mesh.uv3[i].x;
				singleUV[2][i * 2 + 1] = mesh.uv3[i].y;
			}
			meshSize += singleUV[2].Length * 2 * sizeof(float);
		}
		if(mesh.uv4 != null) {
			singleUV[3] = new float[mesh.uv4.Length * 2];
			singleUVCount[3] = (ushort)singleUV[3].Length;
			for(int i = 0; i < mesh.uv4.Length; i++) {
				singleUV[3][i * 2] = mesh.uv4[i].x;
				singleUV[3][i * 2 + 1] = mesh.uv4[i].y;
			}
			meshSize += singleUV[3].Length * 2 * sizeof(float);
		}

		byte[][] matNameBytes = new byte[4][];
		for(int i = 0; i < subMeshCnt; i++) {
			string name = mats[i].name;
			byte[] nameBuf = Encoding.Unicode.GetBytes(name);
			byte[] buf = new byte[64 * sizeof(char)];
			Buffer.BlockCopy(nameBuf, 0, buf, 0, nameBuf.Length);
			matNameBytes[i] = buf;
			meshSize += 64 * sizeof(char);
        }

		byte[] data = new byte[meshSize + 5];
		int offset = 0;
		data[0] = 

		data[offset ] = (byte)( vertsCnt & 0xFF);
		data[offset + 1] = (byte)((vertsCnt >> 8) & 0xFF);
		data[offset + 2] = (byte)( subMeshCnt & 0xFF);
		data[offset + 3] = (byte)((subMeshCnt >> 8) & 0xFF);
		offset += sizeof(ushort) * 2;

		for(int i = 0; i < 4; i++) {
			data[i * 2 + offset]	 = (byte)( singleUVCount[i] & 0xFF);
			data[i * 2 + offset + 1] = (byte)((singleUVCount[i] >> 8) & 0xFF);
        }
		offset += sizeof(ushort) * 4;

		for(int i = 0; i < subMeshCnt; i++) {
			data[i * 2 + offset] = (byte)(indicesCnts[i] & 0xFF);
			data[i * 2 + offset + 1] = (byte)((indicesCnts[i] >> 8) & 0xFF);
		}
		offset += sizeof(ushort) * subMeshCnt;

		for(int i = 0; i < subMeshCnt; i++) {
			ushort[] indices = indicesArray[i];
			for(int j = 0; j < indices.Length; j++) {
				data[j * 2 + offset] = (byte)(indices[j] & 0xFF);
				data[j * 2 + offset + 1] = (byte)((indices[j] >> 8) & 0xFF);
			}
			offset += sizeof(ushort) * indices.Length;
		}

		for(int i = 0; i < 4; i++) {
			if(singleUVCount[i] != 0) {
				float[] suvs = singleUV[i];
				for(int j = 0; j < suvs.Length; j++) {
					byte[] cuv = BitConverter.GetBytes(suvs[j]);
					data[j * 4 + offset    ] = cuv[0];
					data[j * 4 + offset + 1] = cuv[1];
					data[j * 4 + offset + 2] = cuv[2];
					data[j * 4 + offset + 3] = cuv[3];
				}
				offset += sizeof(float) * suvs.Length;
			}
		}

		for(int i = 0; i < subMeshCnt; i++) {
			Buffer.BlockCopy(matNameBytes[i], 0, data, offset, 64 * sizeof(char));
			offset += 64 * sizeof(char);
		}

		return data;
	}

	void GetMeshFromByteArray(byte[] data) {
		Mesh mesh = new Mesh();

		int vertsCnt = 0;
		int offset = 0;
		vertsCnt += data[0];
		vertsCnt += data[1] << 8;
		int subMeshCnt = 0;
		subMeshCnt += data[2];
		subMeshCnt += data[3] << 8;
		offset += 4;

		Vector3[] verts = new Vector3[vertsCnt];
		mesh.SetVertices(new List<Vector3>(verts));
		mesh.subMeshCount = subMeshCnt;

		int[] singleUVCount = new int[4];
		for(int i = 0; i < 4; i++) {
			singleUVCount[i] += data[i * 2 + offset];
			singleUVCount[i] += data[i * 2 + offset + 1] << 8;
		}
		offset += sizeof(ushort) * 4;

		int[] indicesCnts = new int[subMeshCnt];
		for(int i = 0; i < subMeshCnt; i++) {
			indicesCnts[i] += data[i * 2 + offset];
			indicesCnts[i] += data[i * 2 + offset + 1] << 8;
		}
		offset += sizeof(ushort) * subMeshCnt;

		for(int i = 0; i < subMeshCnt; i++) {
			int indicesCnt = indicesCnts[i];
            int[] indicesArray = new int[indicesCnt];
			for(int j = 0; j < indicesCnt; j++) {
				indicesArray[j] += data[j * 2 + offset];
				indicesArray[j] += data[j * 2 + offset + 1] << 8;
			}
			mesh.SetIndices(indicesArray, MeshTopology.Triangles, i);
			offset += sizeof(ushort) * indicesCnt;
		}

		float[][] singleUV = new float[4][];

		for(int i = 0; i < 4; i++) {
			if(singleUVCount[i] != 0) {
				singleUV[i] = new float[singleUVCount[i]];
				for(int j = 0; j < singleUV[i].Length; j++) {
					byte[] cuv = new byte[4];
					cuv[0] = data[j * 4 + offset];
					cuv[1] = data[j * 4 + offset + 1];
					cuv[2] = data[j * 4 + offset + 2];
					cuv[3] = data[j * 4 + offset + 3];
					singleUV[i][j] = BitConverter.ToSingle(cuv, 0);
				}
				offset += 4 * singleUV[i].Length;
			}
		}
		for(int i = 0; i < 4; i++) {
			if(singleUVCount[i] != 0) {
				Vector2[] uvs = new Vector2[singleUVCount[i] / 2];
				for(int j = 0; j < singleUV[i].Length; j += 2) {
					Vector2 uv = new Vector2();
					uv.x = singleUV[i][j];
					uv.y = singleUV[i][j + 1];
					uvs[j / 2] = uv;
				}

				mesh.SetUVs(i, new List<Vector2>(uvs));
			}
		}

		List<Material> mats = new List<Material>();
		for(int i = 0; i < subMeshCnt; i++) {
			byte[] nameBuf = new byte[64 * sizeof(char)];
			Buffer.BlockCopy(data, offset, nameBuf, 0, 64 * sizeof(char));
			string matName = Encoding.Unicode.GetString(nameBuf);
			Material mat;
			if(materials.TryGetValue(matName, out mat)) {
				mats.Add(mat);
			}
			offset += 64 * sizeof(char);
		}

		meshes.Add(new KeyValuePair<Mesh, Material[]>(mesh, mats.ToArray()));
		//Debug.Log("mesh added: " + mesh.name);
		isProcessingToAdd = false;
		return;
	}
#if UNITY_EDITOR
	byte[] GetMaterialInfoToByteArray(Material material) {
		Shader shader = material.shader;
		if(shader == null) {
			return null;
		}

		Dictionary<string, byte> properties = new Dictionary<string, byte>();
		List<byte> data = new List<byte>();
		int materialSize = 0;

		byte[] materialName = new byte[64 * sizeof(char)];
		byte[] nameBuf = Encoding.Unicode.GetBytes(material.name);
		Buffer.BlockCopy(nameBuf, 0, materialName, 0, nameBuf.Length);
		data.AddRange(materialName);
		materialSize += 64 * sizeof(char);

		//get properties and values
		byte propCnt = (byte)ShaderUtil.GetPropertyCount(shader);
		data.Add(propCnt);
		materialSize += sizeof(byte);

		//byte[][] propertiesName = new byte[propCnt][];
		for(int i = 0; i < propCnt; i++) {
			string propName = ShaderUtil.GetPropertyName(shader, i);
			ShaderUtil.ShaderPropertyType type = ShaderUtil.GetPropertyType(shader, i);

			byte[] buf = null;
			switch(type) {
				case ShaderUtil.ShaderPropertyType.Color:
					{
						Color col = material.GetColor(propName);
						float[] colBuf = new float[4];
						colBuf[0] = col.r;
						colBuf[1] = col.g;
						colBuf[2] = col.b;
						colBuf[3] = col.a;
						byte[] byteBuf = new byte[sizeof(float) * 4];
						Buffer.BlockCopy(colBuf, 0, byteBuf, 0, byteBuf.Length);
						buf = byteBuf;
					}
					break;
				case ShaderUtil.ShaderPropertyType.Vector:
					{
						Vector4 vec = material.GetVector(propName);
						float[] vecBuf = new float[4];
						vecBuf[0] = vec.x;
						vecBuf[1] = vec.y;
						vecBuf[2] = vec.z;
						vecBuf[3] = vec.w;
						byte[] byteBuf = new byte[sizeof(float) * 4];
						Buffer.BlockCopy(vecBuf, 0, byteBuf, 0, byteBuf.Length);
						buf = byteBuf;
					}
					break;
				case ShaderUtil.ShaderPropertyType.Float:
					{
						buf = BitConverter.GetBytes(material.GetFloat(propName));
					}
					break;
				case ShaderUtil.ShaderPropertyType.Range:
					{
						buf = BitConverter.GetBytes(material.GetFloat(propName));
					}
					break;
				case ShaderUtil.ShaderPropertyType.TexEnv:
					{
						Texture tex = material.GetTexture(propName);
						if(tex == null)
							break;
						byte[] name = Encoding.Unicode.GetBytes(tex.name);
						byte[] byteBuf = new byte[64 * sizeof(char)];
						Buffer.BlockCopy(name, 0, byteBuf, 0, name.Length);
						buf = byteBuf;
                    }
					break;
			}
			if(buf != null) {
				data.Add((byte)type);
				materialSize += 1;
				byte[] propNameBuf = Encoding.Unicode.GetBytes(propName);
				byte[] propertiesNameBuf = new byte[64 * sizeof(char)];
                Buffer.BlockCopy(propNameBuf, 0, propertiesNameBuf, 0, propNameBuf.Length);
				data.AddRange(propertiesNameBuf);
				materialSize += 64 * sizeof(char);
                data.AddRange(buf);
				materialSize += buf.Length;
				properties.Add(propName, (byte)type);
			}
		}

		return data.ToArray();
	}
#endif
	void GetMaterialFromByteArray(byte[] data) {
		int offset = 0;

		//Get material name
		byte[] nameBuf = new byte[64 * sizeof(char)];
		Buffer.BlockCopy(data, offset, nameBuf, 0, nameBuf.Length);
		string name = Encoding.Unicode.GetString(nameBuf, 0, nameBuf.Length);

		Material mat = null;
		Shader refShader = null;
		bool result = shaders.GetTable().TryGetValue(name.TrimEnd('\0'), out refShader);
        if(result) {
			if(refShader != null) {
				mat = new Material(refShader);
			} else {
				mat = new Material(defaultShader);
			}
        } else {
			mat = new Material(defaultShader);
		}
		mat.name = name;

		offset += 64 * sizeof(char);

		//Get property count
		int propCnt = data[offset];
		offset += 1;

		for(int i = 0; i < propCnt; i++) {
			//Get property type
			int type = data[offset];
			offset += 1;

			//Copy strings as property name
			byte[] propNameBuf = new byte[64 * sizeof(char)];
			Buffer.BlockCopy(data, offset, propNameBuf, 0, propNameBuf.Length);
			string propName = Encoding.Unicode.GetString(propNameBuf).Trim();
			offset += 64 * sizeof(char);

			switch(type) {
				case 0://ShaderUtil.ShaderPropertyType.Color:
					{
						float[] value = new float[4 * sizeof(float)];
						Buffer.BlockCopy(data, offset, value, 0, 4 * sizeof(float));
						offset += 4 * sizeof(float);

						Color col = new Color(value[0], value[1], value[2], value[3]);
						mat.SetColor(propName, col);
					}
					break;
				case 1://ShaderUtil.ShaderPropertyType.Vector:
					{
						float[] value = new float[4 * sizeof(float)];
						Buffer.BlockCopy(data, offset, value, 0, 4 * sizeof(float));
						offset += 4 * sizeof(float);

						Vector4 vec = new Vector4(value[0], value[1], value[2], value[3]);
						mat.SetVector(propName, vec);
					}
					break;
				case 2://ShaderUtil.ShaderPropertyType.Float:
					{
						byte[] valueBuf = new byte[4];
						Buffer.BlockCopy(data, offset, valueBuf, 0, sizeof(float));
						offset += sizeof(float);

						float value = BitConverter.ToSingle(valueBuf, 0);
						mat.SetFloat(propName, value);
					}
					break;
				case 3://ShaderUtil.ShaderPropertyType.Range:
					{
						byte[] valueBuf = new byte[4];
						Buffer.BlockCopy(data, offset, valueBuf, 0, sizeof(float));
						offset += sizeof(float);

						float value = BitConverter.ToSingle(valueBuf, 0);
						mat.SetFloat(propName, value);
					}
					break;
				case 4://ShaderUtil.ShaderPropertyType.TexEnv:
					{
						//Get using texture
						byte[] texNameBuf = new byte[64 * sizeof(char)];
						Buffer.BlockCopy(data, offset, texNameBuf, 0, texNameBuf.Length);
						string texName = Encoding.Unicode.GetString(texNameBuf);
						offset += 64 * sizeof(char);

						Texture tex;
						//Debug.Log("Find: " + texName);
						if(textures.TryGetValue(texName, out tex)) {
							mat.SetTexture(propName, tex);
							//Debug.Log("Found");
                        }
					}
					break;
			}
		}
		if(!materials.ContainsKey(name)) {
			materials.Add(name, mat);
		}
		//Debug.Log("material added: " + name);
		isProcessingToAdd = false;
		return;
	}
#if UNITY_EDITOR
	byte[] GetTextureToPNGByteArray(Texture texture, bool isReimportTexture) {
		if(texture == null) {
			return null;
		}

		TextureImporterFormat oldImporterFormat = new TextureImporterFormat();
		bool oldReadable = false;
        if(isReimportTexture) {
			//Change texture readable flag from Texture Importer
			string pass = AssetDatabase.GetAssetPath(texture);
			TextureImporter ti = TextureImporter.GetAtPath(pass) as TextureImporter;
			oldReadable = ti.isReadable;
			oldImporterFormat = ti.textureFormat;
			ti.isReadable = true;
			ti.textureFormat = TextureImporterFormat.RGBA32;
			AssetDatabase.ImportAsset(pass);
		}

		//Convert the texture to raw PNG data
		Texture2D tex = texture as Texture2D;
		byte[] buf = tex.EncodeToPNG();

		//Prepend texture name to buffer
		int offset = 0;
		byte[] data = new byte[buf.Length + 64 * sizeof(char)];
		byte[] nameBuf = Encoding.Unicode.GetBytes(texture.name);
		Buffer.BlockCopy(nameBuf, 0, data, offset, nameBuf.Length);
		offset += 64 * sizeof(char);

		Buffer.BlockCopy(buf, 0, data, offset, buf.Length);
		offset += buf.Length;

		if(isReimportTexture) {
			//Revert texture readable flag
			string pass = AssetDatabase.GetAssetPath(texture);
			TextureImporter ti = TextureImporter.GetAtPath(pass) as TextureImporter;
			ti.isReadable = oldReadable;
			ti.textureFormat = oldImporterFormat;
			AssetDatabase.ImportAsset(pass);
		}

		return data;
	}
#endif
	void GetTextureFromByteArray(byte[] data) {
		int offset = 0;
		
		//Get texture name;
		byte[] nameBuf = new byte[64 * sizeof(char)];
		Buffer.BlockCopy(data, 0, nameBuf, 0, nameBuf.Length);
		string name = Encoding.Unicode.GetString(nameBuf);
		offset += 64 * sizeof(char);

		//Get raw PNG data
        byte[] buf = new byte[data.Length - nameBuf.Length];
		Buffer.BlockCopy(data, offset, buf, 0, buf.Length);

		//Load texture form Texture2D.LoadImage()
		Texture2D tex = new Texture2D(2, 2);
		tex.LoadImage(buf, true);
		if(tex != null) {
			if(!textures.ContainsKey(name)) {
				textures.Add(name, tex as Texture);
			}
		}
		isProcessingToAdd = false;
	}
}