//MeshSender.cs
//
//Copyright (c) 2016 Tatsuro Matsubara
//Released under the MIT license
//http://opensource.org/licenses/mit-license.php
//

using UnityEngine;
using System.Collections.Generic;
using System.Runtime.InteropServices;

[RequireComponent(typeof(WebRTCManager), typeof(SkinnedMeshesValidator))]
public class MeshSender : MonoBehaviour {

	struct TiledVertex {
		public int tileID;
		public int polyIndex;
		public int x;
		public int y;
		public int z;
	}

	struct FragmentVertex {
		public int x;
		public int y;
		public int z;
	}

	static int[] alignedVerts = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 };
	static string[] kernelNames = {
									"cs_main8", "cs_main8",
									"cs_main32", "cs_main32",
									"cs_main128", "cs_main128",
									"cs_main512", "cs_main512",
									"cs_main512", "cs_main512",
									"cs_main512", "cs_main512",
									"cs_main512"
								};
	static int[] dispatches = { 2, 4, 2, 4, 2, 4, 2, 4, 8, 16, 32, 64, 128 };

	public GameObject targetGameObject;

	private WebRTCManager webRtcManager;
	private SkinnedMeshesValidator validator;

	private ComputeShader tiling;
	private ComputeShader diff;

	public int areaRange = 4;
	public int packageSize = 128;
	public float frameInterval = 0.1f;
	public int subframesPerKeyframe = 4;
	public bool autoReconnect = true;

	public bool reimportTexturesInGame = true;

	SkinnedMeshRenderer[] renderers;
	Vector3[][] oldVertsBuf;
	float[][] oldMatrix;
	List<int> linedIndices;

	float currentTime;
	int frameCnt = 0;

	bool isConnected = false;

	uint timeStamp = 0;

	// Use this for initialization
	void Awake() {
		validator = gameObject.GetComponent<SkinnedMeshesValidator>();
		webRtcManager = gameObject.GetComponent<WebRTCManager>();
		tiling = Resources.Load("TilingShader") as ComputeShader;
		diff = Resources.Load("DiffShader") as ComputeShader;
	}

	void Start() {
		if(packageSize > 255) {
			packageSize = 255;
		}
		if(targetGameObject != null) {
			if(validator != null && webRtcManager != null) {
				InitializeSender();
			}
			return;
		}
	}

	void OnValidate() {
		if(packageSize > 255) {
			packageSize = 255;
		}
	}

	void InitializeSender() {
		if(targetGameObject != null) {
			List<SkinnedMeshRenderer> smrs = new List<SkinnedMeshRenderer>();
			SkinnedMeshRenderer[] psmrs = targetGameObject.GetComponents<SkinnedMeshRenderer>();
			SkinnedMeshRenderer[] csmrs = targetGameObject.GetComponentsInChildren<SkinnedMeshRenderer>();
			if(psmrs.Length != 0) {
				smrs.AddRange(psmrs);
			}
			if(csmrs.Length != 0) {
				smrs.AddRange(csmrs);
			}
			/*
			List<MeshFilter> smfs = new List<MeshFilter>();
			MeshFilter[] pmfs = targetGameObject.GetComponents<MeshFilter>();
			MeshFilter[] cmfs = targetGameObject.GetComponentsInChildren<MeshFilter>();
			if(pmfs.Length != 0) {
				smfs.AddRange(pmfs);
			}
			if(cmfs.Length != 0) {
				smfs.AddRange(cmfs);
			}
			List<MeshRenderer> msrs = new List<MeshRenderer>();
			MeshRenderer[] pmrs = targetGameObject.GetComponents<MeshRenderer>();
			MeshRenderer[] cmrs = targetGameObject.GetComponentsInChildren<MeshRenderer>();
			if(pmrs.Length != 0) {
				msrs.AddRange(pmrs);
			}
			if(cmrs.Length != 0) {
				msrs.AddRange(cmrs);
			}
			SkinnedMeshRenderer[] mrTosmr = new SkinnedMeshRenderer[smfs.Count];
			for(int i = 0; i < mrTosmr.Length; i++) {
				mrTosmr[i] = new SkinnedMeshRenderer();
				mrTosmr[i].sharedMesh = smfs[i].sharedMesh;
				mrTosmr[i].materials = msrs[i].sharedMaterials;
            }
			smrs.AddRange(mrTosmr);
			*/
			renderers = smrs.ToArray();

			oldVertsBuf = new Vector3[renderers.Length][];
			oldMatrix = new float[renderers.Length][];
			linedIndices = new List<int>();

			validator.StartAsSender(webRtcManager, areaRange, packageSize);
			validator.SetInitalCreationCompleteCallback(InitalCreationComplete);
			webRtcManager.SetConnectedCallBack(Connected);
			validator.SetSkinnedMeshRenderers(smrs, reimportTexturesInGame);
		}
	}

	void OnApplicationQuit() {
		webRtcManager.CloseServer();
	}

	void Connected() {
		isConnected = true;
		Debug.Log("MeshSender: Server is open");
	}

	void InitalCreationComplete() {
		webRtcManager.autoReconnect = autoReconnect;
		webRtcManager.CreateChannel();
	}

	public static void SetTexturesWriteFlags(GameObject obj, bool flag) {
		if(obj != null) {
			List<SkinnedMeshRenderer> smrs = new List<SkinnedMeshRenderer>();
			SkinnedMeshRenderer[] psmrs = obj.GetComponents<SkinnedMeshRenderer>();
			SkinnedMeshRenderer[] csmrs = obj.GetComponentsInChildren<SkinnedMeshRenderer>();
			if(psmrs.Length != 0) {
				smrs.AddRange(psmrs);
			}
			if(csmrs.Length != 0) {
				smrs.AddRange(csmrs);
			}
			SkinnedMeshesValidator.SetTexturesWriteFlags(smrs.ToArray(), flag);
		}
	}

	void LateUpdate() {
		if (!isConnected || webRtcManager.connections == 0) return;

		currentTime += Time.deltaTime;
		if(currentTime > frameInterval) {
			currentTime -= frameInterval;
		} else {
			return;
		}

		bool isIframe = frameCnt == 0 ? true : false;
		if(isIframe) {
			tiling.SetInt("packSize", packageSize);
			tiling.SetInt("range", areaRange);
		}

		if (renderers != null) {
			Dictionary<int, TilePacker> dicPacks = new Dictionary<int, TilePacker>();
			List<FragmentVertex[]> frag = new List<FragmentVertex[]>();

			for (int i = 0; i < renderers.Length; i++) {
                SkinnedMeshRenderer smr = renderers[i];
                if(smr != null) {
					//convert to tiled vertices
                    Mesh tempMesh = new Mesh();
                    smr.BakeMesh(tempMesh);

					//calculate shader threads and dipatch size;
					int alignedNum = 16;
					string kernelName = "cs_main8";
                    int verts = tempMesh.vertices.Length;
					int dispatch = 2;
                    for(int k = 0; k < alignedVerts.Length; k++) {
						int aligned = alignedVerts[k];
						if (verts - aligned <= 0) {
							alignedNum = aligned;
							kernelName = kernelNames[k];
							dispatch = dispatches[k];
							break;
						}
					}

                    Vector3[] src = new Vector3[alignedNum];
					tempMesh.vertices.CopyTo(src, 0);
					DestroyImmediate(tempMesh);

					Quaternion quat = smr.transform.rotation;
					Vector3 pos = smr.transform.position - targetGameObject.transform.position;
					Vector3 scale = new Vector3(1, 1, 1);

					Matrix4x4 wrd = Matrix4x4.TRS(pos, quat, scale);

					float[] wrdMatrix = {
						wrd.m00, wrd.m01, wrd.m02, wrd.m03,
						wrd.m10, wrd.m11, wrd.m12, wrd.m13,
						wrd.m20, wrd.m21, wrd.m22, wrd.m23,
						wrd.m30, wrd.m31, wrd.m32, wrd.m33
					};

					if(isIframe) {
						tiling.SetFloats("wrdMatrix", wrdMatrix);
						tiling.SetInt("modelGroup", i);

						ComputeBuffer srcBuf = new ComputeBuffer(
							alignedNum, Marshal.SizeOf(typeof(Vector3)));
						ComputeBuffer destBuf = new ComputeBuffer(
							alignedNum, Marshal.SizeOf(typeof(TiledVertex)));
						srcBuf.SetData(src);

						int kernelNum = tiling.FindKernel(kernelName);
						tiling.SetBuffer(kernelNum, "srcBuf", srcBuf);
						tiling.SetBuffer(kernelNum, "destBuf", destBuf);
						tiling.Dispatch(kernelNum, dispatch, 1, 1);

						TiledVertex[] data = new TiledVertex[src.Length];
						destBuf.GetData(data);

						srcBuf.Release();
						destBuf.Release();

						for(int j = 0; j < verts; j++) {
							TiledVertex vert = data[j];
							int tx = (vert.tileID & 0xFF);
							int ty = (vert.tileID & 0xFF00) >> 8;
							int tz = (vert.tileID & 0xFF0000) >> 16;
							int tileID = tx + ty * packageSize + tz * (packageSize * packageSize);
							if(tx == 255 && ty == 255 && tz == 255) {
								continue;
							}

							TilePacker tile;
							if(!dicPacks.TryGetValue(tileID, out tile)) {
								tile = new TilePacker(tx, ty, tz);
								dicPacks.Add(tileID, tile);
							}

							ByteCoord coord = new ByteCoord();
							coord.p1 = (byte)((vert.polyIndex & 0xFF));
							coord.p2 = (byte)((vert.polyIndex & 0xFF00) >> 8);
							coord.p3 = (byte)((vert.polyIndex & 0xFF0000) >> 16);
							coord.x = (byte)vert.x;
							coord.y = (byte)vert.y;
							coord.z = (byte)vert.z;

							dicPacks[tileID].AddVertex(coord);
						}
					} else {
						diff.SetFloats("wrdMatrix", wrdMatrix);
						diff.SetFloats("oldMatrix", oldMatrix[i]);

						ComputeBuffer srcBuf = new ComputeBuffer(
							alignedNum, Marshal.SizeOf(typeof(Vector3)));
						ComputeBuffer oldBuf = new ComputeBuffer(
							alignedNum, Marshal.SizeOf(typeof(Vector3)));
						ComputeBuffer destBuf = new ComputeBuffer(
							alignedNum, Marshal.SizeOf(typeof(FragmentVertex)));
						srcBuf.SetData(src);
						oldBuf.SetData(oldVertsBuf[i]);

						int kernelNum = diff.FindKernel(kernelName);
						diff.SetBuffer(kernelNum, "srcBuf", srcBuf);
						diff.SetBuffer(kernelNum, "oldBuf", oldBuf);
						diff.SetBuffer(kernelNum, "destBuf", destBuf);
						diff.Dispatch(kernelNum, dispatch, 1, 1);

						FragmentVertex[] data = new FragmentVertex[src.Length];
						destBuf.GetData(data);

						srcBuf.Release();
						oldBuf.Release();
						destBuf.Release();

						frag.Add(data);
					}
					oldVertsBuf[i] = src;
					oldMatrix[i] = wrdMatrix;
				}
			}

			int byteCnt = 0;
			int packages = 0;
			List<byte> lPacks = new List<byte>();

			if(isIframe) {
				linedIndices.Clear();
				foreach(KeyValuePair<int, TilePacker> p in dicPacks) {
					packages++;
					TilePacker pack = p.Value;
					lPacks.AddRange(pack.PackToByteArray(packageSize));
					linedIndices.AddRange(pack.getIndices());
				}
			} else {
				for(int i = 0; i < linedIndices.Count; i++) {
					int meshIndex = (linedIndices[i] >> 16) & 0xFF;
					int vertIndex = linedIndices[i] & 0xFFFF;
					FragmentVertex f = frag[meshIndex][vertIndex];
					byte[] val = new byte[3];
					val[0] = (byte)(f.x);
					val[1] = (byte)(f.y);
					val[2] = (byte)(f.z);
					lPacks.AddRange(val);
				}
			}
			byteCnt += lPacks.Count;

			byte[] rawBuf = lPacks.ToArray();
			byte[] compressedBuf = null;
			int[] prog = new int[1];
#if UNITY_WEBGL || BROTLI_NO_COMPRESS
#else
			if(!brotli.compressBuffer(lPacks.ToArray(), ref compressedBuf, prog, quality:8)) {
				Debug.Log("compress failed!");
				return;
            }
#endif
			webRtcManager.SendVertexStream(rawBuf, compressedBuf, 
				targetGameObject.transform.position, packages, isIframe, timeStamp, true);
			
			timeStamp++;
		}
		frameCnt = (frameCnt + 1) % (subframesPerKeyframe + 1);
	}
}
