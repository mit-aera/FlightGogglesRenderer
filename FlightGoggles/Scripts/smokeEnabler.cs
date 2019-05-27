using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Disables baked reflection probes for Linux (not well supported)

public class smokeEnabler : MonoBehaviour
{
    int framesSinceAwake = 0;
    const int framesToWait = 10;
    bool isEmitting = false;

    // Start is called before the first frame update
    void Update()
    {
        if (!isEmitting && framesSinceAwake > framesToWait)
        {
            this.gameObject.GetComponent<TrailRenderer>().emitting = true;
            isEmitting = true;
        }
        framesSinceAwake++;
    }

}
