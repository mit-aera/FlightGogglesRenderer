using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Disables baked reflection probes for Linux (not well supported)

public class linuxObjectDisabler : MonoBehaviour
{
    
    // Start is called before the first frame update
    void Start()
    {
        // Check runtime OS
        if (Application.platform == RuntimePlatform.LinuxPlayer){
            Debug.Log("Disabling baked reflection probes in linux.");
            // Disable this object.
            this.gameObject.SetActive(false);
        }
    }

}
