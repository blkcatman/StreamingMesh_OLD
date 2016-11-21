//MeshSenderEditor.cs
//
//Copyright (c) 2016 Tatsuro Matsubara
//Released under the MIT license
//http://opensource.org/licenses/mit-license.php
//

using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[CustomEditor(typeof(MeshSender))]
public class MeshSenderEditor : Editor {
	public override void OnInspectorGUI() {
		DrawDefaultInspector();
		serializedObject.Update();
		DrawProperties();
		serializedObject.ApplyModifiedProperties();
	}

	void DrawProperties() {
		MeshSender obj = target as MeshSender;

		if (!obj.reimportTexturesInGame) {
			if(GUILayout.Button("Enable Textures Writable Flag")) {
				MeshSender.SetTexturesWriteFlags(obj.targetGameObject, true);
			}
			if(GUILayout.Button("Disable Textures Writable Flag")) {
				MeshSender.SetTexturesWriteFlags(obj.targetGameObject, false);
			}
		}
	}
}
