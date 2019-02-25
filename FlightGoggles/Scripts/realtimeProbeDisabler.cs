using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Disables realtime reflection probes if not supported.

public class realtimeProbeDisabler : MonoBehaviour
{
    
    // Start is called before the first frame update
    void Start()
    {
        // Check if realtime reflection probes are disabled.
        if (!QualitySettings.realtimeReflectionProbes){
            // Disable this realtime probe.
            this.gameObject.enabled = false;
        }
    }

}
