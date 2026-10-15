using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TextBillboard : MonoBehaviour
{
	Transform cam;
	
	void Start()
	{
		if (Mirror.NetworkServer.active && !Mirror.NetworkClient.active || Mirror.DfuDedicatedServerOptions.Requested)
		{
			enabled = false;
			return;
		}
		if (Camera.main != null)
			cam = Camera.main.transform;
	}
	
	void Update() {
        if (cam == null)
        {
            if (Camera.main == null) return;
            cam = Camera.main.transform;
        }
        transform.rotation = cam.rotation;
    }
}
